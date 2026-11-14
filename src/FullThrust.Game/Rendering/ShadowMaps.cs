using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace FullThrust.Game.Rendering;

// Sun cascades bounded by spheres and texel-snapped, so edges hold still as the camera turns and moves.
public sealed class ShadowMaps : IRenderPass, IFrameUniformsWriter {

    public const int MaxCascades = 4;

    // The horizon map follows the cascades in the shadow matrices.
    public const int HorizonIndex = MaxCascades;

    public const int HorizonResolution = 4096;

    public const VkFormat Format = VkFormat.D32SFloat;

    private readonly DeviceEx _device;

    private readonly ImageEx _horizonImage;
    private readonly ImageViewEx _horizonView;
    private readonly VkFramebuffer _horizonFramebuffer;

    private float4x4 _horizonViewProjection;
    private float _horizonTexel;
    private bool _horizonValid;

    private readonly ImageEx _image;
    private readonly ImageViewEx _arrayView;
    private readonly ImageViewEx[] _layerViews = new ImageViewEx[MaxCascades];
    private readonly VkFramebuffer[] _framebuffers = new VkFramebuffer[MaxCascades];

    private readonly List<IDepthCaster> _casters = [];

    private readonly float4x4[] _viewProjection = new float4x4[MaxCascades];
    private readonly float[] _splits = new float[MaxCascades];
    private readonly float[] _texel = new float[MaxCascades];

    private float3 _anchor;

    public unsafe ShadowMaps(DeviceEx device, int resolution) {

        _device = device;

        Resolution = resolution;

        _image = device.CreateImage(new ImageEx.CreateInfo {

            Name = "ShadowMaps",
            ImageType = VkImageType._2D,
            ImageFormat = Format,
            ImageExtent = new VkExtent3D { Width = resolution, Height = resolution, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = MaxCascades,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,

        });

        _arrayView = _image.CreateImageView(VkImageViewType._2DArray, _image.GetFullRange(VkImageAspectFlags.DepthBit), null);

        RenderPass = RenderPasses.Create(device, null, new AttachmentDesc {

            Format = Format,
            Load = VkAttachmentLoadOp.Clear,
            Store = VkAttachmentStoreOp.Store,
            Initial = VkImageLayout.Undefined,
            Subpass = VkImageLayout.DepthStencilAttachmentOptimal,
            Final = VkImageLayout.ShaderReadOnlyOptimal,

        });

        for (var i = 0; i < MaxCascades; i++) {

            _layerViews[i] = _image.CreateImageView(VkImageViewType._2D, new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, BaseArrayLayer = i, LayerCount = 1 }, null);

            var view = _layerViews[i].VkImageView;

            _framebuffers[i] = device.CreateFramebuffer(new VkFramebufferCreateInfo {

                RenderPass = RenderPass,
                AttachmentCount = 1,
                Attachments = &view,
                Width = resolution,
                Height = resolution,
                Layers = 1,

            }, null);

        }

        _horizonImage = device.CreateImage(new ImageEx.CreateInfo {

            Name = "HorizonShadowMap",
            ImageType = VkImageType._2D,
            ImageFormat = Format,
            ImageExtent = new VkExtent3D { Width = HorizonResolution, Height = HorizonResolution, Depth = 1 },
            ImageMipLevels = 1,
            ImageArrayLayers = 1,
            ImageSamples = VkSampleCountFlags._1Bit,
            ImageTiling = VkImageTiling.Optimal,
            ImageUsage = VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit,
            AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,

        });

        _horizonView = _horizonImage.CreateImageView(VkImageViewType._2D, _horizonImage.GetFullRange(VkImageAspectFlags.DepthBit), null);

        var horizonView = _horizonView.VkImageView;

        _horizonFramebuffer = device.CreateFramebuffer(new VkFramebufferCreateInfo {

            RenderPass = RenderPass,
            AttachmentCount = 1,
            Attachments = &horizonView,
            Width = HorizonResolution,
            Height = HorizonResolution,
            Layers = 1,

        }, null);

        // Reverse-Z: a receiver is lit when it is at least as near the sun as the stored caster. The black border reads as lit.
        Sampler = device.CreateSampler(new VkSamplerCreateInfo {

            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            MipmapMode = VkSamplerMipmapMode.Nearest,
            AddressModeU = VkSamplerAddressMode.ClampToBorder,
            AddressModeV = VkSamplerAddressMode.ClampToBorder,
            AddressModeW = VkSamplerAddressMode.ClampToBorder,
            BorderColor = VkBorderColor.FloatTransparentBlack,
            CompareEnable = true,
            CompareOp = VkCompareOp.GreaterOrEqual,

        }, null);

    }

    public string Name => "Shadow maps";

    public RenderStage Stage => RenderStage.Prepare;

    public bool Enabled { get; set; } = true;

    // Off still clears the maps each frame, so their layout stays valid for the shaders that sample them.
    public bool CastShadows { get; set; } = true;

    public int Resolution { get; }

    // Depth only; compatible with HdrTarget.DepthPass.
    public VkRenderPass RenderPass { get; }

    public VkImageView View => _arrayView.VkImageView;

    public VkSampler Sampler { get; }

    public VkImageView HorizonView => _horizonView.VkImageView;

    // Terrain shadows from the last cascade out to the horizon, and across the whole lit face from orbit.
    public bool HorizonShadows { get; set; } = true;

    // Smallest half-width the horizon map covers, which sets its finest texel.
    public float HorizonMinimumRadius { get; set; } = 24000.0f;

    public int Cascades { get; set; } = MaxCascades;

    public float MaxDistance { get; set; } = 4000.0f;

    // Blend of logarithmic (1) and uniform (0) split spacing.
    public float SplitLambda { get; set; } = 0.92f;

    public float NormalOffset { get; set; } = 1.5f;

    public float FilterRadius { get; set; } = 1.0f;

    public float ConstantBias { get; set; } = 1.0f;

    public float SlopeBias { get; set; } = 1.5f;

    public bool ShowCascades { get; set; }

    public int DrawCount { get; private set; }

    public void AddCaster(IDepthCaster caster) => _casters.Add(caster);

    public float Split(int cascade) => _splits[cascade];

    // Fits one map to the part of the nearest body the camera can see: its whole face from afar, the ground out to the horizon when low.
    private void UpdateHorizon(float3 eye, float4 body, float3 lightX, float3 lightY, float3 lightZ) {

        _horizonValid = HorizonShadows && CastShadows && body.W > 0;

        if (!_horizonValid) {

            return;

        }

        var toCamera = -new float3(body.X, body.Y, body.Z);
        var distance = MathF.Max(toCamera.Length(), body.W + 1);
        var up = toCamera / toCamera.Length();

        // The visible cap ends where sight lines graze the sphere; its rim circle bounds it.
        var cosine = body.W / distance;
        var sine = MathF.Sqrt(MathF.Max(1 - cosine * cosine, 0));
        var radius = MathF.Max(body.W * sine * 1.05f, HorizonMinimumRadius);

        // Whole steps of radius keep the texel size, and so the shadow edges, still while the camera climbs.
        radius = MathF.Pow(2, MathF.Ceiling(MathF.Log2(radius) * 8) / 8);

        var texel = 2 * radius / HorizonResolution;
        var center = Snap(eye + new float3(body.X, body.Y, body.Z) + up * (body.W * cosine), _anchor, texel, lightX, lightY, lightZ);

        var lightView = float4x4.CreateLookTo(center + lightZ * radius, -lightZ, lightY);

        _horizonViewProjection = lightView * OrthographicReverseZ(radius, 2 * radius);
        _horizonTexel = texel;

    }

    // Snaps a map centre to whole texels counted from the ground anchor, so the grid moves with the ground rather than sliding across it.
    private static float3 Snap(float3 center, float3 anchor, float texel, float3 lightX, float3 lightY, float3 lightZ) {

        var ax = float3.Dot(anchor, lightX);
        var ay = float3.Dot(anchor, lightY);
        var x = MathF.Floor((float3.Dot(center, lightX) - ax) / texel) * texel + ax;
        var y = MathF.Floor((float3.Dot(center, lightY) - ay) / texel) * texel + ay;

        return lightX * x + lightY * y + lightZ * float3.Dot(center, lightZ);

    }

    // The anchor is relative to the camera.
    public void Update(in float4x4 view, in float4x4 projection, float3 sunDirection, float4 nearestBody, float3 anchor) {

        float4x4.Invert(view, out var inverseView);

        var eye = new float3(inverseView.W.X, inverseView.W.Y, inverseView.W.Z);

        _anchor = eye + anchor;
        var back = new float3(inverseView.Z.X, inverseView.Z.Y, inverseView.Z.Z);

        var tanX = 1.0f / projection.X.X;
        var tanY = 1.0f / MathF.Abs(projection.Y.Y);
        var near = Math.Max(projection.W.Z, 0.05f);

        var k2 = 1.0f + tanX * tanX + tanY * tanY;

        var lightZ = float3.Normalize(sunDirection);
        var up = MathF.Abs(lightZ.Y) < 0.99f ? float3.UnitY : float3.UnitX;
        var lightX = float3.Normalize(float3.Cross(up, lightZ));
        var lightY = float3.Cross(lightZ, lightX);

        UpdateHorizon(eye, nearestBody, lightX, lightY, lightZ);

        var count = Math.Clamp(Cascades, 1, MaxCascades);
        var start = near;

        for (var i = 0; i < count; i++) {

            var t = (i + 1) / (float)count;
            var logarithmic = near * MathF.Pow(MaxDistance / near, t);
            var uniform = near + (MaxDistance - near) * t;
            var end = SplitLambda * logarithmic + (1 - SplitLambda) * uniform;

            // Smallest sphere around the frustum slice; it depends only on the split distances and field of view.
            var centerDistance = k2 * (start + end) * 0.5f;
            float radius;

            if (centerDistance >= end) {

                centerDistance = end;
                radius = end * MathF.Sqrt(k2 - 1);

            } else {

                radius = MathF.Sqrt((end - centerDistance) * (end - centerDistance) + end * end * (k2 - 1));

            }

            var texel = 2 * radius / Resolution;
            var center = Snap(eye - back * centerDistance, _anchor, texel, lightX, lightY, lightZ);

            var lightView = float4x4.CreateLookTo(center + lightZ * radius, -lightZ, lightY);

            _viewProjection[i] = lightView * OrthographicReverseZ(radius, 2 * radius);
            _splits[i] = end;
            _texel[i] = texel;

            start = end;

        }

        for (var i = count; i < MaxCascades; i++) {

            _viewProjection[i] = _viewProjection[count - 1];
            _splits[i] = _splits[count - 1];
            _texel[i] = _texel[count - 1];

        }

    }

    public void Fill(ref FrameUniforms uniforms, bool enabled) {

        for (var i = 0; i < MaxCascades; i++) {

            uniforms.ShadowViewProjection[i] = _viewProjection[i];

        }

        uniforms.ShadowSplits = new float4(_splits[0], _splits[1], _splits[2], _splits[3]);
        uniforms.ShadowTexel = new float4(_texel[0], _texel[1], _texel[2], _texel[3]);
        uniforms.ShadowParams = new float4(CastShadows ? Math.Clamp(Cascades, 1, MaxCascades) : 0, NormalOffset, FilterRadius, ShowCascades ? 1 : 0);

        uniforms.ShadowViewProjection[HorizonIndex] = _horizonViewProjection;
        uniforms.HorizonShadow = new float4(_horizonTexel, _horizonValid ? 1 : 0, 1.0f / HorizonResolution, 0);

    }

    // Depth 1 at the sun-facing side of the box, 0 at the far side.
    private static float4x4 OrthographicReverseZ(float halfSize, float depth) {

        float4x4 result;

        result.X = new float4(1 / halfSize, 0, 0, 0);
        result.Y = new float4(0, 1 / halfSize, 0, 0);
        result.Z = new float4(0, 0, 1 / depth, 0);
        result.W = new float4(0, 0, 1, 1);

        return result;

    }

    public unsafe void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;
        var count = Math.Clamp(Cascades, 1, MaxCascades);
        var clear = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0.0f } };

        DrawCount = 0;

        for (var i = 0; i < count; i++) {

            cmd.BeginRenderPass(new VkRenderPassBeginInfo {

                RenderPass = RenderPass,
                Framebuffer = _framebuffers[i],
                RenderArea = new VkRect2D(new VkExtent2D { Width = Resolution, Height = Resolution }),
                ClearValueCount = 1,
                ClearValues = &clear,

            }, VkSubpassContents.Inline);

            cmd.SetViewport(0, [new VkViewport { X = 0, Y = 0, Width = Resolution, Height = Resolution, MinDepth = 0, MaxDepth = 1 }]);
            cmd.SetScissor(0, [new VkRect2D(new VkExtent2D { Width = Resolution, Height = Resolution })]);

            // Reverse-Z: pushing casters away from the sun lowers their depth.
            cmd.SetDepthBias(-ConstantBias, 0.0f, -SlopeBias);

            var context = new DepthContext(cmd, frame.FrameSet, DepthView.Shadow, i, Frustum.FromMatrix(_viewProjection[i], includeNear: false));

            foreach (var caster in _casters) {

                if (CastShadows && caster.CastsDepth) {

                    caster.RecordDepth(context);
                    DrawCount++;

                }

            }

            cmd.EndRenderPass();

        }

        // Always cleared, so its layout stays valid for the shaders that sample it.
        cmd.BeginRenderPass(new VkRenderPassBeginInfo {

            RenderPass = RenderPass,
            Framebuffer = _horizonFramebuffer,
            RenderArea = new VkRect2D(new VkExtent2D { Width = HorizonResolution, Height = HorizonResolution }),
            ClearValueCount = 1,
            ClearValues = &clear,

        }, VkSubpassContents.Inline);

        if (_horizonValid) {

            cmd.SetViewport(0, [new VkViewport { X = 0, Y = 0, Width = HorizonResolution, Height = HorizonResolution, MinDepth = 0, MaxDepth = 1 }]);
            cmd.SetScissor(0, [new VkRect2D(new VkExtent2D { Width = HorizonResolution, Height = HorizonResolution })]);
            cmd.SetDepthBias(-ConstantBias, 0.0f, -SlopeBias);

            var context = new DepthContext(cmd, frame.FrameSet, DepthView.Horizon, HorizonIndex, Frustum.FromMatrix(_horizonViewProjection, includeNear: false));

            foreach (var caster in _casters) {

                if (caster.CastsDepth) {

                    caster.RecordDepth(context);
                    DrawCount++;

                }

            }

        }

        cmd.EndRenderPass();

    }

    public void Dispose() {

        _device.DestroySampler(Sampler, null);

        for (var i = 0; i < MaxCascades; i++) {

            _device.DestroyFramebuffer(_framebuffers[i], null);
            _layerViews[i].Dispose();

        }

        _device.DestroyFramebuffer(_horizonFramebuffer, null);
        _horizonView.Dispose();
        _horizonImage.Dispose();

        _device.DestroyRenderPass(RenderPass, null);
        _arrayView.Dispose();
        _image.Dispose();

    }

}
