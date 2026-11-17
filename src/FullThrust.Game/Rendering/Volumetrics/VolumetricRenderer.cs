using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering.Volumetrics;

[StructLayout(LayoutKind.Sequential)]
public struct ResolvePush {

    public float4x4 PreviousViewProjection;

    // xyz: floating origin now minus then; w: weight of the new frame.
    public float4 OriginShift;

    // xyz: how far the craft moved in render-local space since last frame; w: terrain slot of the body the other volumes turn with, or -1.
    public float4 Motion;

    // Which pixel of each 2x2 block was marched: xy this frame, zw last frame.
    public float4 Offsets;

    // Inverse of the craft's rotation since last frame, for history that turns with it.
    public float4 CraftTurn;

}

// Ray-marches every volume source at half resolution after the opaque scene, accumulates it over frames, and Composite blends it in.
public sealed class VolumetricRenderer : IRenderPass, IResizeListener, IFrameUniformsWriter {

    public const int Capacity = 96;

    private const int GroupSize = 8;
    private const int HeaderBytes = 16;

    // Each frame keeps a tenth of the new march and nine tenths of the reprojected history.
    private const float NewFrameWeight = 0.1f;

    private const int HullHeaderBytes = 16;

    private static readonly int PrimitiveBytes = Marshal.SizeOf<VolumePrimitive>();

    private readonly DeviceEx _device;
    private readonly HdrTarget _target;
    private readonly FloatingOrigin _origin;

    private readonly VolumeNoise _noise;
    private readonly VolumeList _volumes = new(Capacity);
    private readonly List<IVolumeSource> _sources = [];

    private readonly BufferEx[] _buffers = new BufferEx[Renderer.FramesInFlight];
    private readonly MappedMemory[] _mapped = new MappedMemory[Renderer.FramesInFlight];
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly BufferEx[] _hullBuffers = new BufferEx[Renderer.FramesInFlight];
    private readonly MappedMemory[] _hullMapped = new MappedMemory[Renderer.FramesInFlight];

    private readonly VkSampler _pointSampler;
    private readonly VkSampler _linearSampler;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly ComputeKernel _march;

    private readonly VkDescriptorSetLayout _resolveLayout;
    private readonly VkDescriptorPool _resolvePool;
    private readonly VkDescriptorSet[] _resolveSets = new VkDescriptorSet[2];
    private readonly ComputeKernel _resolve;

    private readonly RenderTexture _color;
    private readonly RenderTexture _depth;
    private readonly RenderTexture _distance;
    private readonly RenderTexture[] _history = new RenderTexture[2];

    // History texture this frame resolved into, which the composite reads.
    private int _written;
    private bool _historyValid;
    private long _frameCount;
    private float4x4 _previousViewProjection;
    private double3 _previousOrigin;
    private float3 _previousCraftPosition;
    private floatQuat _previousCraftRotation = floatQuat.Identity;

    // Which pixel of each 2x2 block the last march sampled.
    private float2 _offset;

    public VolumetricRenderer(DeviceEx device, ShaderLibrary shaders, HdrTarget target, FrameResources frames, FloatingOrigin origin, GpuUploader uploader) {

        _device = device;
        _target = target;
        _origin = origin;

        _noise = new VolumeNoise(device, shaders, frames.SetLayout, uploader);

        _pointSampler = CreateSampler(device, VkFilter.Nearest);
        _linearSampler = CreateSampler(device, VkFilter.Linear);

        _setLayout = Descriptors.CreateLayout(device, [

            Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(3, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(4, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(5, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(6, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(8, VkDescriptorType.StorageBuffer, VkShaderStageFlags.ComputeBit),

        ]);

        _pool = Descriptors.CreatePool(device, Renderer.FramesInFlight, [

            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 * Renderer.FramesInFlight },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * Renderer.FramesInFlight },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 3 * Renderer.FramesInFlight },

        ]);

        var size = new ByteSize((nuint)(HeaderBytes + Capacity * PrimitiveBytes));

        for (var i = 0; i < Renderer.FramesInFlight; i++) {

            _buffers[i] = device.CreateBuffer(new BufferEx.CreateInfo {

                Name = "Volumes",
                BufferSize = size,
                BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,

            });

            _mapped[i] = _buffers[i].Map();
            _sets[i] = Descriptors.Allocate(device, _pool, _setLayout);

            Descriptors.WriteStorageBuffer(device, _sets[i], 3, _buffers[i].VkBuffer, size);

            _hullBuffers[i] = device.CreateBuffer(new BufferEx.CreateInfo {

                Name = "Hulls",
                BufferSize = HullBytes,
                BufferUsage = VkBufferUsageFlags.StorageBufferBit,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,

            });

            _hullMapped[i] = _hullBuffers[i].Map();

            Descriptors.WriteStorageBuffer(device, _sets[i], 8, _hullBuffers[i].VkBuffer, HullBytes);

        }

        _march = new ComputeKernel(device, shaders, "volumetrics.comp", frames.SetLayout, _setLayout, ByteSize.Of<float4>());

        _resolveLayout = Descriptors.CreateLayout(device, [

            Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(3, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),

        ]);

        _resolvePool = Descriptors.CreatePool(device, 2, [

            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 6 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 },

        ]);

        _resolveSets[0] = Descriptors.Allocate(device, _resolvePool, _resolveLayout);
        _resolveSets[1] = Descriptors.Allocate(device, _resolvePool, _resolveLayout);
        _resolve = new ComputeKernel(device, shaders, "volumetric_resolve.comp", frames.SetLayout, _resolveLayout, ByteSize.Of<ResolvePush>());

        var half = Half(target.Extent);

        _color = new RenderTexture(device, "VolumeColor", VkFormat.R16G16B16A16SFloat, half.Width, half.Height);
        _depth = new RenderTexture(device, "VolumeDepth", VkFormat.R32SFloat, half.Width, half.Height);
        _distance = new RenderTexture(device, "VolumeDistance", VkFormat.R32SFloat, half.Width, half.Height);
        _history[0] = new RenderTexture(device, "VolumeHistory0", VkFormat.R16G16B16A16SFloat, half.Width, half.Height);
        _history[1] = new RenderTexture(device, "VolumeHistory1", VkFormat.R16G16B16A16SFloat, half.Width, half.Height);

        WriteDescriptors();

        CompositePass = new Composite(this, shaders, frames.SetLayout);

        frames.WriteImage(GlobalBindings.CloudNoise, _noise.Shape, _noise.Sampler, VkImageLayout.General);
        frames.WriteImage(GlobalBindings.WeatherMap, _noise.Weather, _noise.Sampler);

    }

    public string Name => "Volumetrics";

    public RenderStage Stage => RenderStage.AfterOpaque;

    public bool Enabled { get; set; } = true;

    public int VolumeCount => _volumes.Count;

    public VolumeNoise Noise => _noise;

    // Emitters add lights while collecting; they reach shaders in the next frame's uniforms.
    public PointLights Lights { get; } = new();

    // Render-local position of the craft whose exhaust and plasma are in view; their history follows it.
    public float3 CraftPosition { get; set; }

    public floatQuat CraftRotation { get; set; } = floatQuat.Identity;

    // Refreshed before sources collect, so they can query it.
    public HullSet? Hulls { get; set; }

    private static ByteSize HullBytes => new((nuint)(HullHeaderBytes + HullSet.Capacity * Marshal.SizeOf<HullPrimitive>() + HullSet.IndexCapacity * sizeof(int)));

    // Terrain slot of the body whose ground and air carry the clouds and smoke, or -1 to treat them as fixed in space.
    public int GroundBody { get; set; } = -1;

    // Register with the renderer after this pass.
    public IRenderPass CompositePass { get; }

    public void AddSource(IVolumeSource source) => _sources.Add(source);

    public void Fill(ref FrameUniforms uniforms, bool enabled) {

        Lights.Fill(ref uniforms, enabled);

        foreach (var source in _sources) {

            if (source is IFrameUniformsWriter writer) {

                writer.Fill(ref uniforms, enabled);

            }

        }

    }

    private static VkSampler CreateSampler(DeviceEx device, VkFilter filter) {

        return device.CreateSampler(new VkSamplerCreateInfo {

            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,

        }, null);

    }

    private static VkExtent2D Half(VkExtent2D extent) => new() { Width = Math.Max(1, (extent.Width + 1) / 2), Height = Math.Max(1, (extent.Height + 1) / 2) };

    public void OnResize(VkExtent2D extent) {

        var half = Half(extent);

        _color.Resize(half.Width, half.Height);
        _depth.Resize(half.Width, half.Height);
        _distance.Resize(half.Width, half.Height);
        _history[0].Resize(half.Width, half.Height);
        _history[1].Resize(half.Width, half.Height);
        _historyValid = false;

        WriteDescriptors();
        ((Composite)CompositePass).WriteDescriptors();

    }

    private void WriteDescriptors() {

        foreach (var set in _sets) {

            Descriptors.WriteImage(_device, set, 0, _target.DepthView, _pointSampler, VkImageLayout.DepthStencilReadOnlyOptimal);
            Descriptors.WriteImage(_device, set, 1, _noise.Shape, _noise.Sampler, VkImageLayout.General);
            Descriptors.WriteImage(_device, set, 2, _noise.Detail, _noise.Sampler, VkImageLayout.General);
            Descriptors.WriteStorageImage(_device, set, 4, _color.View);
            Descriptors.WriteStorageImage(_device, set, 5, _depth.View);
            Descriptors.WriteStorageImage(_device, set, 6, _distance.View);

        }

        // Set k reads the other history and resolves into history k.
        for (var k = 0; k < 2; k++) {

            Descriptors.WriteImage(_device, _resolveSets[k], 0, _color.View, _pointSampler, VkImageLayout.General);
            Descriptors.WriteImage(_device, _resolveSets[k], 1, _distance.View, _pointSampler, VkImageLayout.General);
            Descriptors.WriteImage(_device, _resolveSets[k], 2, _history[1 - k].View, _linearSampler, VkImageLayout.General);
            Descriptors.WriteStorageImage(_device, _resolveSets[k], 3, _history[k].View);

        }

    }

    public void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;

        _noise.Prepare(cmd, frame.FrameSet);
        _color.Prepare(cmd);
        _depth.Prepare(cmd);
        _distance.Prepare(cmd);
        _history[0].Prepare(cmd);
        _history[1].Prepare(cmd);

        Hulls?.Update();
        _volumes.Clear();

        foreach (var source in _sources) {

            if (source.Visible) {

                source.Collect(_volumes);

            }

        }

        var bytes = _mapped[frame.FrameIndex].AsSpan<byte>();

        MemoryMarshal.Write(bytes, new int4(_volumes.Count, 0, 0, 0));
        _volumes.Items.CopyTo(MemoryMarshal.Cast<byte, VolumePrimitive>(bytes[HeaderBytes..]));

        WriteHulls(_hullMapped[frame.FrameIndex].AsSpan<byte>());

        var viewProjection = frame.View * frame.Projection;
        var origin = _origin.Origin;

        if (_volumes.Count == 0) {

            _historyValid = false;

            return;

        }

        // Golden-ratio steps of the march offset, so frames fill in between each other's samples.
        var jitter = (float)(_frameCount * 0.6180339887 % 1.0);

        // Each frame marches a different pixel of every 2x2 block, so over four frames every full-resolution pixel is seen.
        var phase = (int)(_frameCount++ % 4);
        var offset = new float2(phase is 1 or 2 ? 1 : 0, phase is 1 or 3 ? 1 : 0);
        var x = ComputeKernel.Groups(_color.Width, GroupSize);
        var y = ComputeKernel.Groups(_color.Height, GroupSize);

        Barriers.ShadersToCompute(cmd);
        _march.Dispatch(cmd, frame.FrameSet, _sets[frame.FrameIndex], new float4(jitter, offset.X, offset.Y, 0), x, y, 1);
        Barriers.ComputeToShaders(cmd);

        _written = 1 - _written;

        var turn = floatQuat.Conjugate(CraftRotation * floatQuat.Conjugate(_previousCraftRotation));

        var push = new ResolvePush {

            PreviousViewProjection = _previousViewProjection,
            OriginShift = new float4(SpaceMath.ToFloat(origin - _previousOrigin), _historyValid ? NewFrameWeight : 1.0f),
            Motion = new float4(_previousCraftPosition - Rotate(turn, CraftPosition), GroundBody),
            Offsets = new float4(offset.X, offset.Y, _offset.X, _offset.Y),
            CraftTurn = ToFloat4(turn),

        };

        _previousCraftPosition = CraftPosition;
        _previousCraftRotation = CraftRotation;
        _offset = offset;

        _resolve.Dispatch(cmd, frame.FrameSet, _resolveSets[_written], push, x, y, 1);
        Barriers.ComputeToShaders(cmd);

        _previousViewProjection = viewProjection;
        _previousOrigin = origin;
        _historyValid = true;

    }

    private void WriteHulls(Span<byte> bytes) {

        var hulls = Hulls;
        var count = hulls?.Count ?? 0;
        var indices = hulls?.IndexCount ?? 0;

        MemoryMarshal.Write(bytes, new int4(count, indices, 0, 0));

        if (hulls is null) {

            return;

        }

        var primitiveBytes = HullSet.Capacity * Marshal.SizeOf<HullPrimitive>();

        hulls.Primitives.CopyTo(MemoryMarshal.Cast<byte, HullPrimitive>(bytes.Slice(HullHeaderBytes, primitiveBytes)));
        hulls.Indices.CopyTo(MemoryMarshal.Cast<byte, int>(bytes[(HullHeaderBytes + primitiveBytes)..]));

    }

    private static float4 ToFloat4(floatQuat q) => new(q.X, q.Y, q.Z, q.W);

    private static float3 Rotate(floatQuat q, float3 v) {

        var axis = new float3(q.X, q.Y, q.Z);
        var t = 2 * float3.Cross(axis, v);

        return v + q.W * t + float3.Cross(axis, t);

    }

    // The renderer disposes CompositePass with its other passes.
    public void Dispose() {

        _march.Dispose();
        _resolve.Dispose();
        _noise.Dispose();
        _color.Dispose();
        _depth.Dispose();
        _distance.Dispose();
        _history[0].Dispose();
        _history[1].Dispose();

        for (var i = 0; i < _buffers.Length; i++) {

            _mapped[i].Dispose();
            _buffers[i].Dispose();
            _hullMapped[i].Dispose();
            _hullBuffers[i].Dispose();

        }

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _device.DestroyDescriptorPool(_resolvePool, null);
        _device.DestroyDescriptorSetLayout(_resolveLayout, null);
        _device.DestroySampler(_pointSampler, null);
        _device.DestroySampler(_linearSampler, null);

    }

    // Upsamples the half-resolution volumes with depth-aware weights and blends them over the scene.
    private sealed class Composite : IRenderPass {

        private readonly VolumetricRenderer _owner;

        private readonly VkDescriptorSetLayout _setLayout;
        private readonly VkDescriptorPool _pool;

        // One per history texture.
        private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[2];

        private readonly VkPipelineLayout _layout;
        private readonly VkPipeline _pipeline;

        public unsafe Composite(VolumetricRenderer owner, ShaderLibrary shaders, VkDescriptorSetLayout frameLayout) {

            _owner = owner;

            var device = owner._device;

            _setLayout = Descriptors.CreateLayout(device, [

                Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit),
                Descriptors.Binding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit),
                Descriptors.Binding(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.FragmentBit),

            ]);

            _pool = Descriptors.CreatePool(device, 2, [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 6 }]);
            _sets[0] = Descriptors.Allocate(device, _pool, _setLayout);
            _sets[1] = Descriptors.Allocate(device, _pool, _setLayout);
            _layout = device.CreatePipelineLayout([frameLayout, _setLayout], [

                new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Offset = ByteSize.Zero, Size = ByteSize.Of<float4>() },

            ], null);

            // Premultiplied: destination times the volume's transmittance, plus its light.
            var attachment = new VkPipelineColorBlendAttachmentState {

                BlendEnable = true,
                SrcColorBlendFactor = VkBlendFactor.One,
                DstColorBlendFactor = VkBlendFactor.SrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.Zero,
                DstAlphaBlendFactor = VkBlendFactor.One,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,

            };

            var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &attachment };

            _pipeline = Pipelines.Create(device, shaders, new PipelineDesc {

                RenderPass = owner._target.TransparentPass,
                Layout = _layout,
                VertexShader = "fullscreen.vert",
                FragmentShader = "volume_composite.frag",
                Cull = VkCullModeFlags.None,
                Depth = DepthMode.None,
                Blend = &blend,

            });

            WriteDescriptors();

        }

        public string Name => "Volumetric composite";

        public RenderStage Stage => RenderStage.Transparent;

        public bool Enabled { get; set; } = true;

        public void WriteDescriptors() {

            var device = _owner._device;

            for (var k = 0; k < 2; k++) {

                Descriptors.WriteImage(device, _sets[k], 0, _owner._history[k].View, _owner._pointSampler, VkImageLayout.General);
                Descriptors.WriteImage(device, _sets[k], 1, _owner._depth.View, _owner._pointSampler, VkImageLayout.General);
                Descriptors.WriteImage(device, _sets[k], 2, _owner._target.DepthView, _owner._pointSampler, VkImageLayout.DepthStencilReadOnlyOptimal);

            }

        }

        public void Record(in FrameContext frame) {

            if (!_owner.Enabled || _owner._volumes.Count == 0) {

                return;

            }

            var cmd = frame.CommandBuffer;

            cmd.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, _layout, 0, frame.FrameSet, []);
            cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, _layout, 1, _sets[_owner._written], []);
            cmd.PushConstants(_layout, VkShaderStageFlags.FragmentBit, ByteSize.Zero, new float4(_owner._offset, 0, 0));
            cmd.Draw(3, 1, 0, 0);

        }

        public void Dispose() {

            var device = _owner._device;

            device.DestroyPipeline(_pipeline, null);
            device.DestroyPipelineLayout(_layout, null);
            device.DestroyDescriptorPool(_pool, null);
            device.DestroyDescriptorSetLayout(_setLayout, null);

        }

    }

}
