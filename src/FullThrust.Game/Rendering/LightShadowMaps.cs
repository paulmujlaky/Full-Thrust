using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering;

// A cube of depth maps around each point light, so plumes and floodlights are blocked by the hull, the tower and the pad.
public sealed class LightShadowMaps : IRenderPass {

    public const int Faces = 6;

    public const int MapCount = LightVectors.MaxPointLights * Faces;

    // The light matrices follow the sun's cascades and the horizon map.
    public const int FirstMatrix = ShadowMaps.MaxCascades + 1;

    public const int Resolution = 512;

    // Mirrors the face tables in common.glsl.
    private static readonly float3[] Forward = [float3.UnitX, -float3.UnitX, float3.UnitY, -float3.UnitY, float3.UnitZ, -float3.UnitZ];
    private static readonly float3[] Up = [float3.UnitY, float3.UnitY, float3.UnitZ, float3.UnitZ, float3.UnitY, float3.UnitY];

    private readonly DeviceEx _device;
    private readonly VkRenderPass _renderPass;

    private readonly ImageEx _image;
    private readonly ImageViewEx _arrayView;
    private readonly ImageViewEx[] _layerViews = new ImageViewEx[MapCount];
    private readonly VkFramebuffer[] _framebuffers = new VkFramebuffer[MapCount];

    private readonly List<IDepthCaster> _casters = [];

    private readonly float4x4[] _viewProjection = new float4x4[MapCount];
    private readonly bool[] _faceUsed = new bool[MapCount];

    private int _lightCount;

    public unsafe LightShadowMaps(DeviceEx device, VkRenderPass depthOnlyPass) {

        _device = device;
        _renderPass = depthOnlyPass;

        _image = device.CreateImage(new ImageEx.CreateInfo {

            Name = "LightShadowMaps",
            ImageType = VkImageType._2D,
            ImageFormat = ShadowMaps.Format,
            ImageExtent = new VkExtent3D { Width = Resolution, Height = Resolution, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = MapCount,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,

        });

        _arrayView = _image.CreateImageView(VkImageViewType._2DArray, _image.GetFullRange(VkImageAspectFlags.DepthBit), null);

        for (var i = 0; i < MapCount; i++) {

            _layerViews[i] = _image.CreateImageView(VkImageViewType._2D, new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, BaseArrayLayer = i, LayerCount = 1 }, null);

            var view = _layerViews[i].VkImageView;

            _framebuffers[i] = device.CreateFramebuffer(new VkFramebufferCreateInfo {

                RenderPass = _renderPass,
                AttachmentCount = 1,
                Attachments = &view,
                Width = Resolution,
                Height = Resolution,
                Layers = 1,

            }, null);

        }

    }

    public string Name => "Light shadows";

    public RenderStage Stage => RenderStage.Prepare;

    // Off still clears the maps each frame, so their layout stays valid for the shaders that sample them.
    public bool Enabled { get; set; } = true;

    public bool CastShadows { get; set; } = true;

    // Geometry nearer a light than this is its own fixture or nozzle, and does not shade it.
    public float NearPlane { get; set; } = 0.35f;

    public float SlopeBias { get; set; } = 2.0f;

    public VkImageView View => _arrayView.VkImageView;

    public int DrawCount { get; private set; }

    public void AddCaster(IDepthCaster caster) => _casters.Add(caster);

    // Reads the lights already written into the frame uniforms and adds their shadow matrices.
    public void Fill(ref FrameUniforms uniforms) {

        _lightCount = CastShadows ? Math.Min((int)uniforms.LightCount.X, LightVectors.MaxPointLights) : 0;

        var projection = SpaceMath.PerspectiveReverseZ(MathF.PI / 2, 1, NearPlane);

        for (var light = 0; light < _lightCount; light++) {

            var position = uniforms.LightPositions[light];
            var beam = uniforms.LightDirections[light];
            var origin = new float3(position.X, position.Y, position.Z);
            var axis = new float3(beam.X, beam.Y, beam.Z);

            for (var face = 0; face < Faces; face++) {

                var map = light * Faces + face;

                // A spotlight's beam never reaches the faces behind it; a face spans up to 55 degrees off its axis.
                _faceUsed[map] = beam.W <= 0 || float3.Dot(axis, Forward[face]) > MathF.Cos(MathF.Acos(Math.Clamp(beam.W - 0.15f, -1, 1)) + 0.96f);
                _viewProjection[map] = float4x4.CreateLookTo(origin, Forward[face], Up[face]) * projection;

                uniforms.ShadowViewProjection[FirstMatrix + map] = _viewProjection[map];

            }

        }

        uniforms.LightShadow = new float4(_lightCount > 0 ? 1 : 0, NearPlane, 1.0f / Resolution, 0);

    }

    public unsafe void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;
        var clear = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0.0f } };
        var area = new VkRect2D(new VkExtent2D { Width = Resolution, Height = Resolution });

        DrawCount = 0;

        for (var map = 0; map < MapCount; map++) {

            cmd.BeginRenderPass(new VkRenderPassBeginInfo {

                RenderPass = _renderPass,
                Framebuffer = _framebuffers[map],
                RenderArea = area,
                ClearValueCount = 1,
                ClearValues = &clear,

            }, VkSubpassContents.Inline);

            if (map < _lightCount * Faces && _faceUsed[map]) {

                cmd.SetViewport(0, [new VkViewport { X = 0, Y = 0, Width = Resolution, Height = Resolution, MinDepth = 0, MaxDepth = 1 }]);
                cmd.SetScissor(0, [area]);

                // Depth here is near over distance, so a constant bias means nothing; the receiver adds its own.
                cmd.SetDepthBias(0.0f, 0.0f, -SlopeBias);

                var context = new DepthContext(cmd, frame.FrameSet, DepthView.Light, FirstMatrix + map, Frustum.FromMatrix(_viewProjection[map]));

                foreach (var caster in _casters) {

                    if (caster.CastsDepth) {

                        caster.RecordDepth(context);
                        DrawCount++;

                    }

                }

            }

            cmd.EndRenderPass();

        }

    }

    public void Dispose() {

        for (var i = 0; i < MapCount; i++) {

            _device.DestroyFramebuffer(_framebuffers[i], null);
            _layerViews[i].Dispose();

        }

        _arrayView.Dispose();
        _image.Dispose();

    }

}
