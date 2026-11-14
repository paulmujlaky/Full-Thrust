using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using FullThrust.Assets;
using FullThrust.Core.Ecs;
using FullThrust.Core.Rendering;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering;

// Draws every entity with a ModelInstance using the glTF metallic-roughness materials.
public sealed class MeshPass : IRenderPass, IDepthCaster {

    private readonly DeviceEx _device;

    private readonly World _world;
    private readonly FloatingOrigin _origin;
    private readonly ModelLibrary _models;
    private readonly Camera _camera;

    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _singleSided;
    private readonly VkPipeline _doubleSided;

    private readonly VkPipeline _depthSingleSided;
    private readonly VkPipeline _depthDoubleSided;
    private readonly VkPipeline _shadow;
    private readonly VkPipeline _cutoutDepth;
    private readonly VkPipeline _cutoutShadow;
    private readonly VkPipeline _lightShadow;
    private readonly VkPipeline _cutoutLightShadow;

    // Entity placements of this frame and the last, each in its own frame's render-local space.
    private Dictionary<Entity, float4x4> _previous = new(512);
    private Dictionary<Entity, float4x4> _current = new(512);

    public unsafe MeshPass(DeviceEx device, VkRenderPass renderPass, VkRenderPass depthPass, VkRenderPass shadowPass, VkDescriptorSetLayout frameLayout, ShaderLibrary shaders, World world, FloatingOrigin origin, ModelLibrary models, Camera camera) {

        _device = device;
        _world = world;
        _origin = origin;
        _models = models;
        _camera = camera;

        _layout = device.CreatePipelineLayout([frameLayout, models.MaterialLayout], [

            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Offset = ByteSize.Zero, Size = ByteSize.Of<MeshPush>() },

        ], null);

        var desc = new PipelineDesc {

            RenderPass = renderPass,
            Layout = _layout,
            VertexShader = "mesh.vert",
            FragmentShader = "mesh.frag",
            VertexInput = VertexInput.For<MeshVertex>(),
            Cull = VkCullModeFlags.BackBit,
            Depth = DepthMode.TestWrite,

        };

        _singleSided = Pipelines.Create(device, shaders, desc);

        desc.Cull = VkCullModeFlags.None;

        _doubleSided = Pipelines.Create(device, shaders, desc);

        desc.RenderPass = depthPass;
        desc.FragmentShader = "velocity.frag";
        desc.Cull = VkCullModeFlags.BackBit;

        _depthSingleSided = Pipelines.Create(device, shaders, desc);

        desc.Cull = VkCullModeFlags.None;

        _depthDoubleSided = Pipelines.Create(device, shaders, desc);

        desc.FragmentShader = "mesh_cutout.frag";
        _cutoutDepth = Pipelines.Create(device, shaders, desc);

        desc.RenderPass = shadowPass;
        desc.DepthOnly = true;
        desc.VertexShader = "mesh_shadow.vert";
        desc.ShadowCaster = true;

        _cutoutShadow = Pipelines.Create(device, shaders, desc);
        desc.ClipNear = true;
        _cutoutLightShadow = Pipelines.Create(device, shaders, desc);
        desc.FragmentShader = null;
        _lightShadow = Pipelines.Create(device, shaders, desc);
        desc.ClipNear = false;
        _shadow = Pipelines.Create(device, shaders, desc);

    }

    public string Name => "Meshes";

    public RenderStage Stage => RenderStage.Opaque;

    public bool Enabled { get; set; } = true;

    public bool CastsDepth => Enabled;

    public int DrawCount { get; private set; }

    public void Record(in FrameContext frame) {

        var cmd = frame.CommandBuffer;

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, _singleSided);
        cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, _layout, 0, frame.FrameSet, []);

        _models.ResetStats();

        DrawAll(cmd, _singleSided, _doubleSided, default, Frustum.FromMatrix(frame.View * frame.Projection), false, -1);

        DrawCount = _models.DrawCalls;

        // The prepass recorded where everything is this frame; next frame measures motion against it.
        (_previous, _current) = (_current, _previous);
        _current.Clear();

    }

    public void RecordDepth(in DepthContext context) {

        if (context.View == DepthView.Horizon) {

            return;

        }

        var cmd = context.CommandBuffer;

        var (single, both, cutout) = context.View switch {

            DepthView.Shadow => (_shadow, _shadow, _cutoutShadow),
            DepthView.Light => (_lightShadow, _lightShadow, _cutoutLightShadow),
            _ => (_depthSingleSided, _depthDoubleSided, _cutoutDepth),

        };

        cmd.BindPipeline(VkPipelineBindPoint.Graphics, single);
        cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, _layout, 0, context.FrameSet, []);

        DrawAll(cmd, single, both, cutout, context.Frustum, true, context.Cascade);

    }

    private void DrawAll(CommandBuffer cmd, VkPipeline single, VkPipeline both, VkPipeline cutout, in Frustum frustum, bool depthOnly, int cascade) {

        foreach (var row in _world.Query<Transform, ModelInstance>()) {

            ref readonly var transform = ref row.C1;
            var range = row.C2.DetailRange;
            if (range > 0 && (transform.Position - _camera.Position).LengthSquared() > range * range) {

                continue;

            }

            var rotation = float4x4.CreateFromQuaternion(SpaceMath.ToFloat(transform.Rotation));
            var entity = rotation * float4x4.CreateTranslation(_origin.ToLocal(transform.Position));

            if (!_previous.TryGetValue(row.Entity, out var before)) {

                before = entity;

            }

            if (depthOnly && cascade < 0) {

                _current[row.Entity] = entity;

            }

            _models.Draw(cmd, _layout, single, both, cutout, row.C2.Model, entity, before, frustum, depthOnly, cascade);

            if (row.C2.Shroud is { } shroud) {

                _models.Draw(cmd, _layout, single, both, cutout, shroud, entity, before, frustum, depthOnly, cascade);

            }

        }

    }

    public void Dispose() {

        _device.DestroyPipeline(_singleSided, null);
        _device.DestroyPipeline(_doubleSided, null);
        _device.DestroyPipeline(_depthSingleSided, null);
        _device.DestroyPipeline(_depthDoubleSided, null);
        _device.DestroyPipeline(_shadow, null);
        _device.DestroyPipeline(_cutoutDepth, null);
        _device.DestroyPipeline(_cutoutShadow, null);
        _device.DestroyPipeline(_lightShadow, null);
        _device.DestroyPipeline(_cutoutLightShadow, null);
        _device.DestroyPipelineLayout(_layout, null);

    }

}
