using Brutal.Numerics;
using Brutal.StbApi;
using FullThrust.Assets;

namespace FullThrust.ModelGen;

// Third-party models brought into part space: node transforms baked, turned nose up, cropped, decimated and fitted to the part.
public static class SourceModel {

    public static ModelData Import(Recipe recipe, string modelsRoot) {

        var source = GltfModelLoader.Load(Path.Combine(modelsRoot, recipe.Source!));
        var rotation = Rotation(recipe.SourceUp, recipe.SourceRotationDeg);
        var low = Bound(recipe.CropMin, recipe.CropMinY, float.NegativeInfinity);
        var high = Bound(recipe.CropMax, recipe.CropMaxY, float.PositiveInfinity);

        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var primitives = new List<PrimitiveData>();
        var meshes = new List<MeshRange>();

        foreach (var instance in source.Instances) {

            var range = source.Meshes[instance.Mesh];

            var kept = recipe.KeepMeshes.Count == 0 || recipe.KeepMeshes.Exists(name => range.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (!kept || recipe.DropMeshes.Exists(name => range.Name.Contains(name, StringComparison.OrdinalIgnoreCase))) {

                continue;

            }

            var transform = instance.Transform * rotation;
            var remap = new Dictionary<uint, uint>();
            var first = primitives.Count;

            for (var p = range.FirstPrimitive; p < range.FirstPrimitive + range.PrimitiveCount; p++) {

                var primitive = source.Primitives[p];

                if (recipe.DropMaterials.Exists(name => source.Materials[primitive.Material].Name.Contains(name, StringComparison.OrdinalIgnoreCase))) {

                    continue;

                }

                var start = indices.Count;

                for (var i = primitive.FirstIndex; i + 2 < primitive.FirstIndex + primitive.IndexCount; i += 3) {

                    var centroid = float3.Zero;

                    for (var k = 0; k < 3; k++) {

                        centroid += float3.Transform(source.Vertices[source.Indices[i + k]].Position, transform) / 3;

                    }

                    if (centroid.X < low.X || centroid.Y < low.Y || centroid.Z < low.Z || centroid.X > high.X || centroid.Y > high.Y || centroid.Z > high.Z) {

                        continue;

                    }

                    for (var k = 0; k < 3; k++) {

                        var index = source.Indices[i + k];

                        if (!remap.TryGetValue(index, out var mapped)) {

                            mapped = (uint)vertices.Count;
                            remap[index] = mapped;
                            vertices.Add(Transform(source.Vertices[index], transform));

                        }

                        indices.Add(mapped);

                    }

                }

                if (indices.Count > start) {

                    primitives.Add(new PrimitiveData(start, indices.Count - start, primitive.Material));

                }

            }

            if (primitives.Count > first) {

                meshes.Add(new MeshRange(first, primitives.Count - first, range.Name));

            }

        }

        var model = new ModelData {

            Name = recipe.Output,
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            Primitives = primitives.ToArray(),
            Meshes = meshes.ToArray(),
            Instances = Enumerable.Range(0, meshes.Count).Select(m => new MeshInstance(m, float4x4.Identity)).ToArray(),
            Materials = source.Materials,
            Textures = Shrink(source.Textures, recipe.MaxTextureSize),

        };

        if (recipe.Decimate > 0 && model.TriangleCount > recipe.Decimate) {

            Decimator.Reduce(model, recipe.Decimate);

        }

        model.ComputeBounds();
        Fit(model, recipe);

        return model;

    }

    public static float4x4 Rotation(double[]? up, double[] degrees) {

        var upright = up is { Length: >= 3 } ? float4x4.CreateFromQuaternion(Generators.FromTo(float3.NormalizeOrZero(new float3((float)up[0], (float)up[1], (float)up[2])), float3.UnitY)) : float4x4.Identity;

        return upright * float4x4.CreateRotationX((float)(degrees[0] * Math.PI / 180)) * float4x4.CreateRotationY((float)(degrees[1] * Math.PI / 180)) * float4x4.CreateRotationZ((float)(degrees[2] * Math.PI / 180));

    }

    // Centres the model on its bounds and scales it to the recipe's diameter and length.
    private static void Fit(ModelData model, Recipe recipe) {

        var fit = recipe.Fit.ToLowerInvariant();

        if (fit == "none") {

            return;

        }

        var size = model.BoundsMax - model.BoundsMin;
        var center = (model.BoundsMin + model.BoundsMax) * 0.5f;

        if (recipe.SourceDiameter > 0) {

            size = new float3((float)recipe.SourceDiameter, size.Y, (float)recipe.SourceDiameter);
            center = recipe.SourceAxis is { Length: >= 2 } axis ? new float3((float)axis[0], center.Y, (float)axis[1]) : center;

        }
        var target = new float3((float)recipe.Diameter, (float)recipe.Length, (float)recipe.Diameter);
        var ratio = target / size;

        var scale = fit switch {

            "stretch" => ratio,
            "uniform" => new float3(MathF.Min(ratio.X, MathF.Min(ratio.Y, ratio.Z))),
            "length" => new float3(ratio.Y),
            _ => throw new InvalidDataException($"{recipe.Output}: unknown fit '{recipe.Fit}'."),

        };

        for (var i = 0; i < model.Vertices.Length; i++) {

            ref var vertex = ref model.Vertices[i];

            vertex.Position = (vertex.Position - center) * scale;
            vertex.Normal = float3.NormalizeOrZero(vertex.Normal / scale);

            var tangent = float3.NormalizeOrZero(new float3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z) * scale);

            vertex.Tangent = new float4(tangent, vertex.Tangent.W);

        }

        model.ComputeBounds();

    }

    private static TextureData[] Shrink(TextureData[] textures, int limit) {

        if (limit <= 0) {

            return textures;

        }

        return textures.Select(texture => {

            var jpeg = texture.Jpeg is not null;
            var shrunk = false;

            while (Math.Max(texture.Width, texture.Height) > limit && texture.Width > 1 && texture.Height > 1) {

                texture = Halve(texture);
                shrunk = true;

            }

            if (shrunk && jpeg) {

                texture.Jpeg = EncodeJpeg(texture);

            }

            return texture;

        }).ToArray();

    }

    private static byte[] EncodeJpeg(TextureData texture) {

        var path = Path.Combine(Path.GetTempPath(), $"modelgen-{Guid.NewGuid():N}.jpg");

        try {

            if (!Stb.WriteJpg(path, texture.Width, texture.Height, 4, texture.Pixels, 92)) {

                throw new IOException($"Could not encode {texture.Name} as JPEG.");

            }

            return File.ReadAllBytes(path);

        } finally {

            File.Delete(path);

        }

    }

    // Box filter in linear space for data maps; colour maps average in sRGB, which is close enough for a 2x step.
    private static TextureData Halve(TextureData texture) {

        var width = texture.Width / 2;
        var height = texture.Height / 2;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {

            for (var x = 0; x < width; x++) {

                for (var c = 0; c < 4; c++) {

                    var sum = 0;

                    for (var k = 0; k < 4; k++) {

                        sum += texture.Pixels[((y * 2 + k / 2) * texture.Width + x * 2 + k % 2) * 4 + c];

                    }

                    pixels[(y * width + x) * 4 + c] = (byte)((sum + 2) / 4);

                }

            }

        }

        return new TextureData { Name = texture.Name, Width = width, Height = height, Pixels = pixels, Srgb = texture.Srgb };

    }

    private static MeshVertex Transform(MeshVertex vertex, float4x4 transform) {

        var tangent = float3.TransformNormal(new float3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);

        vertex.Position = float3.Transform(vertex.Position, transform);
        vertex.Normal = float3.NormalizeOrZero(float3.TransformNormal(vertex.Normal, transform));
        vertex.Tangent = new float4(tangent.LengthSquared() > 0 ? float3.NormalizeOrZero(tangent) : float3.UnitX, vertex.Tangent.W);

        return vertex;

    }

    private static float3 Bound(double[]? box, double y, float fallback) {

        if (box is { Length: >= 3 }) {

            return new float3((float)box[0], (float)box[1], (float)box[2]);

        }

        return new float3(fallback, double.IsInfinity(y) ? fallback : (float)y, fallback);

    }

}
