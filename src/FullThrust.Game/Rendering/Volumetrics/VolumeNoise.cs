using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace FullThrust.Game.Rendering.Volumetrics;

// Tileable 3D noise every volume samples: large Perlin-Worley shapes and fine Worley detail, generated once on the GPU.
public sealed class VolumeNoise : IDisposable {

    private const int ShapeSize = 128;
    private const int DetailSize = 32;
    private const int GroupSize = 4;

    // Share of the coverage map's width cross-faded to close its dateline seam.
    private const int WrapBandFraction = 32;

    private readonly DeviceEx _device;

    private readonly RenderTexture _shape;
    private readonly RenderTexture _detail;
    private readonly GpuTexture _weather;

    private readonly ComputeKernel _shapeKernel;
    private readonly ComputeKernel _detailKernel;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet _shapeSet;
    private readonly VkDescriptorSet _detailSet;

    private bool _generated;

    public VolumeNoise(DeviceEx device, ShaderLibrary shaders, VkDescriptorSetLayout frameLayout, GpuUploader uploader) {

        _device = device;

        _shape = new RenderTexture(device, "VolumeShapeNoise", VkFormat.R8G8B8A8UNorm, ShapeSize, ShapeSize, ShapeSize);
        _detail = new RenderTexture(device, "VolumeDetailNoise", VkFormat.R8G8B8A8UNorm, DetailSize, DetailSize, DetailSize);
        using var pixels = Stb.Load(Path.Combine(AppContext.BaseDirectory, "data", "textures", "weather", "blue-marble-clouds.png"), 4);
        var band = Math.Max(1, pixels.Width / WrapBandFraction);
        var wrapped = WrapLongitude(pixels.AsSpan(), pixels.Width, pixels.Height, band);

        _weather = new GpuTexture(uploader, "SatelliteCoverage", wrapped, pixels.Width - band, pixels.Height, srgb: false);
        uploader.Flush();

        _setLayout = Descriptors.CreateLayout(device, [Descriptors.Binding(0, VkDescriptorType.StorageImage, VkShaderStageFlags.ComputeBit)]);
        _pool = Descriptors.CreatePool(device, 2, [

            new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2 },

        ]);

        _shapeSet = Descriptors.Allocate(device, _pool, _setLayout);
        _detailSet = Descriptors.Allocate(device, _pool, _setLayout);

        Descriptors.WriteStorageImage(device, _shapeSet, 0, _shape.View);
        Descriptors.WriteStorageImage(device, _detailSet, 0, _detail.View);

        _shapeKernel = new ComputeKernel(device, shaders, "volume_shape_noise.comp", frameLayout, _setLayout, ByteSize.Zero);
        _detailKernel = new ComputeKernel(device, shaders, "volume_detail_noise.comp", frameLayout, _setLayout, ByteSize.Zero);

        Sampler = device.CreateSampler(new VkSamplerCreateInfo {

            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.Repeat,
            AddressModeV = VkSamplerAddressMode.Repeat,
            AddressModeW = VkSamplerAddressMode.Repeat,

        }, null);

    }

    public VkImageView Shape => _shape.View;

    public VkImageView Detail => _detail.View;
    public VkImageView Weather => _weather.View;

    public VkSampler Sampler { get; }

    // Records the one-off generation the first time it is called.
    public void Prepare(CommandBuffer cmd, VkDescriptorSet frameSet) {

        _shape.Prepare(cmd);
        _detail.Prepare(cmd);

        if (_generated) {

            return;

        }

        var shapeGroups = ComputeKernel.Groups(ShapeSize, GroupSize);
        var detailGroups = ComputeKernel.Groups(DetailSize, GroupSize);

        _shapeKernel.Dispatch(cmd, frameSet, _shapeSet, shapeGroups, shapeGroups, shapeGroups);
        _detailKernel.Dispatch(cmd, frameSet, _detailSet, detailGroups, detailGroups, detailGroups);
        Barriers.ComputeToShaders(cmd);

        _generated = true;

    }

    // The source's east and west edges do not meet, so its first columns are dropped and cross-faded into its last ones instead.
    public static byte[] WrapLongitude(ReadOnlySpan<byte> rgba, int width, int height, int band) {

        var outWidth = width - band;
        var result = new byte[outWidth * height * 4];

        for (var y = 0; y < height; y++) {

            var row = rgba.Slice(y * width * 4, width * 4);
            var target = result.AsSpan(y * outWidth * 4, outWidth * 4);

            row[(band * 4)..].CopyTo(target);

            for (var i = 0; i < band; i++) {

                var t = (i + 1) / (float)(band + 1);
                var s = t * t * (3 - 2 * t);
                var x = outWidth - band + i;

                for (var c = 0; c < 4; c++) {

                    target[x * 4 + c] = (byte)MathF.Round(row[(width - band + i) * 4 + c] * (1 - s) + row[i * 4 + c] * s);

                }

            }

        }

        return result;

    }

    public void Dispose() {

        _shapeKernel.Dispose();
        _detailKernel.Dispose();
        _shape.Dispose();
        _detail.Dispose();
        _weather.Dispose();

        _device.DestroySampler(Sampler, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);

    }

}
