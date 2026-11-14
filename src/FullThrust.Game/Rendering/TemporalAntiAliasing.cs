using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace FullThrust.Game.Rendering;

// Accumulates jittered frames along each pixel's motion, so sub-pixel geometry and highlights settle instead of shimmering.
public sealed class TemporalAntiAliasing : IRenderPass, IResizeListener, IProjectionJitter {

    private const int GroupSize = 8;

    // Halton(2, 3) repeats after this many frames, each landing in a different part of the pixel.
    private const int JitterPhases = 16;

    private readonly DeviceEx _device;
    private readonly HdrTarget _target;

    private readonly VkSampler _pointSampler;
    private readonly VkSampler _linearSampler;

    private readonly VkDescriptorSetLayout _resolveLayout;
    private readonly VkDescriptorSetLayout _copyLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _resolveSet;
    private readonly VkDescriptorSet _copySet;

    private readonly ComputeKernel _resolve;
    private readonly ComputeKernel _copy;

    private readonly RenderTexture _resolved;
    private readonly RenderTexture _history;

    private bool _historyValid;

    public TemporalAntiAliasing(DeviceEx device, ShaderLibrary shaders, HdrTarget target, FrameResources frames) {

        _device = device;
        _target = target;

        _pointSampler = CreateSampler(device, VkFilter.Nearest);
        _linearSampler = CreateSampler(device, VkFilter.Linear);

        _resolveLayout = Descriptors.CreateLayout(device, [

            Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(3, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(4, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),

        ]);

        _copyLayout = Descriptors.CreateLayout(device, [

            Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(1, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),

        ]);

        _pool = Descriptors.CreatePool(device, 2, [

            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 5 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 },

        ]);

        _resolveSet = Descriptors.Allocate(device, _pool, _resolveLayout);
        _copySet = Descriptors.Allocate(device, _pool, _copyLayout);

        _resolve = new ComputeKernel(device, shaders, "taa.comp", frames.SetLayout, _resolveLayout, ByteSize.Of<float4>());
        _copy = new ComputeKernel(device, shaders, "copy.comp", _copyLayout, ByteSize.Zero);

        _resolved = new RenderTexture(device, "TaaResolved", HdrTarget.ColorFormat, target.Extent.Width, target.Extent.Height);
        _history = new RenderTexture(device, "TaaHistory", HdrTarget.ColorFormat, target.Extent.Width, target.Extent.Height);

        WriteDescriptors();

    }

    public string Name => "Temporal anti-aliasing";

    public RenderStage Stage => RenderStage.PostProcess;

    // Later passes read this pass's output, so it always runs; Active turns the accumulation itself off.
    public bool Enabled { get; set; } = true;

    public bool Active { get; set; } = true;

    // Share of each frame in the result; lower is steadier, higher follows change sooner.
    public float NewFrameWeight { get; set; } = 0.08f;

    // Standard deviations of its neighbourhood that history may differ by before it is pulled back.
    public float ClipSpread { get; set; } = 1.25f;

    public float2 Offset(long frame) {

        if (!Active) {

            return default;

        }

        var index = (int)(frame % JitterPhases) + 1;

        return new float2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f);

    }

    private static float Halton(int index, int radix) {

        var result = 0.0f;
        var fraction = 1.0f / radix;

        while (index > 0) {

            result += index % radix * fraction;
            index /= radix;
            fraction /= radix;

        }

        return result;

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

    public void OnResize(VkExtent2D extent) {

        _resolved.Resize(extent.Width, extent.Height);
        _history.Resize(extent.Width, extent.Height);
        _historyValid = false;

        WriteDescriptors();

    }

    private void WriteDescriptors() {

        Descriptors.WriteImage(_device, _resolveSet, 0, _target.PostView, _pointSampler, _target.PostLayout);
        Descriptors.WriteImage(_device, _resolveSet, 1, _target.VelocityView, _pointSampler);
        Descriptors.WriteImage(_device, _resolveSet, 2, _target.DepthView, _pointSampler, VkImageLayout.DepthStencilReadOnlyOptimal);
        Descriptors.WriteImage(_device, _resolveSet, 3, _history.View, _linearSampler, VkImageLayout.General);
        Descriptors.WriteStorageImage(_device, _resolveSet, 4, _resolved.View);

        Descriptors.WriteImage(_device, _copySet, 0, _resolved.View, _pointSampler, VkImageLayout.General);
        Descriptors.WriteStorageImage(_device, _copySet, 1, _history.View);

        _target.SetPost(_resolved.View, VkImageLayout.General);

    }

    public void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;
        var x = ComputeKernel.Groups(_resolved.Width, GroupSize);
        var y = ComputeKernel.Groups(_resolved.Height, GroupSize);

        _resolved.Prepare(cmd);
        _history.Prepare(cmd);

        var weight = Active && _historyValid ? NewFrameWeight : 1.0f;

        Barriers.ShadersToCompute(cmd);
        _resolve.Dispatch(cmd, frame.FrameSet, _resolveSet, new float4(weight, ClipSpread, 0, 0), x, y, 1);
        Barriers.ComputeToShaders(cmd);

        if (Active) {

            _copy.Dispatch(cmd, default, _copySet, x, y, 1);
            Barriers.ComputeToShaders(cmd);

        }

        _historyValid = Active;

    }

    public void Dispose() {

        _resolve.Dispose();
        _copy.Dispose();
        _resolved.Dispose();
        _history.Dispose();

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_resolveLayout, null);
        _device.DestroyDescriptorSetLayout(_copyLayout, null);
        _device.DestroySampler(_pointSampler, null);
        _device.DestroySampler(_linearSampler, null);

    }

}
