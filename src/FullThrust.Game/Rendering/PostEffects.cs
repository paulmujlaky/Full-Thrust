using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace FullThrust.Game.Rendering;

// Camera effects on the resolved image: heat haze around flames, then motion blur along each pixel's velocity.
public sealed class PostEffects : IRenderPass, IResizeListener {

    private const int GroupSize = 8;

    private readonly DeviceEx _device;
    private readonly HdrTarget _target;

    private readonly VkSampler _linearSampler;
    private readonly VkSampler _pointSampler;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _set;

    private readonly ComputeKernel _kernel;
    private readonly RenderTexture _result;

    public PostEffects(DeviceEx device, ShaderLibrary shaders, HdrTarget target, FrameResources frames) {

        _device = device;
        _target = target;

        _linearSampler = CreateSampler(device, VkFilter.Linear);
        _pointSampler = CreateSampler(device, VkFilter.Nearest);

        _setLayout = Descriptors.CreateLayout(device, [

            Descriptors.Binding(0, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(1, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(2, VkDescriptorType.CombinedImageSampler, VkShaderStageFlags.ComputeBit),
            Descriptors.Binding(3, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit),

        ]);

        _pool = Descriptors.CreatePool(device, 1, [

            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 },
            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 1 },

        ]);

        _set = Descriptors.Allocate(device, _pool, _setLayout);
        _kernel = new ComputeKernel(device, shaders, "post_effects.comp", frames.SetLayout, _setLayout, ByteSize.Of<float4>());
        _result = new RenderTexture(device, "PostEffects", HdrTarget.ColorFormat, target.Extent.Width, target.Extent.Height);

        WriteDescriptors();

    }

    public string Name => "Post effects";

    public RenderStage Stage => RenderStage.PostProcess;

    // Later passes read this pass's output, so it always runs; zero strengths pass the image through.
    public bool Enabled { get; set; } = true;

    // Share of the frame time the shutter stays open; a film camera's half is 0.5, and 0 turns motion blur off.
    public float Shutter { get; set; } = 0.5f;

    // Time warp moves everything many frames' worth at once, which would smear the whole view.
    public bool SuspendBlur { get; set; }

    // Longest streak in pixels at 1080p; it scales with resolution.
    public float MaxBlur { get; set; } = 28.0f;

    // How far, in pixels at 1080p, hot exhaust bends what lies behind it; 0 turns heat haze off.
    public float HeatHaze { get; set; } = 3.5f;

    // How far the shimmer reaches around a flame, in radii of its light.
    public float HazeReach { get; set; } = 2.6f;

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

        _result.Resize(extent.Width, extent.Height);

        WriteDescriptors();

    }

    private void WriteDescriptors() {

        Descriptors.WriteImage(_device, _set, 0, _target.PostView, _linearSampler, _target.PostLayout);
        Descriptors.WriteImage(_device, _set, 1, _target.VelocityView, _pointSampler);
        Descriptors.WriteImage(_device, _set, 2, _target.DepthView, _pointSampler, VkImageLayout.DepthStencilReadOnlyOptimal);
        Descriptors.WriteStorageImage(_device, _set, 3, _result.View);

        _target.SetPost(_result.View, VkImageLayout.General);

    }

    public void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;
        var scale = _result.Height / 1080.0f;

        _result.Prepare(cmd);

        Barriers.ShadersToCompute(cmd);
        _kernel.Dispatch(cmd, frame.FrameSet, _set, new float4(SuspendBlur ? 0 : Shutter, MaxBlur * scale, HeatHaze * scale, HazeReach), ComputeKernel.Groups(_result.Width, GroupSize), ComputeKernel.Groups(_result.Height, GroupSize), 1);
        Barriers.ComputeToShaders(cmd);

    }

    public void Dispose() {

        _kernel.Dispose();
        _result.Dispose();

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _device.DestroySampler(_linearSampler, null);
        _device.DestroySampler(_pointSampler, null);

    }

}
