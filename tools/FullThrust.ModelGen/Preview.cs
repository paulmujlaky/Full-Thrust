using Brutal.Numerics;
using FullThrust.Assets;

namespace FullThrust.ModelGen;

// Flat-shaded orthographic snapshots of a model from the side and from below, for checking imports without the game.
public static class Preview {

    private const int Height = 640;

    public static void Render(string path, string output, float4x4 rotation) {

        var model = GltfModelLoader.Load(path);
        var triangles = new List<(float3 A, float3 B, float3 C, float3 Color)>();

        foreach (var instance in model.Instances) {

            var mesh = model.Meshes[instance.Mesh];
            var transform = instance.Transform * rotation;

            for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

                var primitive = model.Primitives[p];
                var material = model.Materials[primitive.Material];

                for (var t = primitive.FirstIndex; t + 2 < primitive.FirstIndex + primitive.IndexCount; t += 3) {

                    var a = model.Vertices[model.Indices[t]];
                    var b = model.Vertices[model.Indices[t + 1]];
                    var c = model.Vertices[model.Indices[t + 2]];
                    var color = new float3(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z);

                    if (material.BaseColorTexture >= 0) {

                        color *= Sample(model.Textures[material.BaseColorTexture], (a.Uv + b.Uv + c.Uv) / 3);

                    }

                    triangles.Add((float3.Transform(a.Position, transform), float3.Transform(b.Position, transform), float3.Transform(c.Position, transform), color));

                }

            }

        }

        var min = new float3(float.MaxValue);
        var max = new float3(float.MinValue);

        foreach (var (a, b, c, _) in triangles) {

            min = float3.Min(min, float3.Min(a, float3.Min(b, c)));
            max = float3.Max(max, float3.Max(a, float3.Max(b, c)));

        }

        var size = max - min;
        var scale = (Height - 20) / MathF.Max(size.Y, MathF.Max(size.X, size.Z));
        var panel = (int)(MathF.Max(size.X, size.Z) * scale) + 20;
        var width = panel * 3;
        var pixels = new byte[width * Height * 4];
        var depth = new float[width * Height];

        Array.Fill(depth, float.MaxValue);

        for (var i = 0; i < pixels.Length; i += 4) {

            pixels[i] = 40;
            pixels[i + 1] = 44;
            pixels[i + 2] = 52;
            pixels[i + 3] = 255;

        }

        // Front (looking down -Z), side (looking down +X) and from below (looking up +Y).
        for (var view = 0; view < 3; view++) {

            foreach (var (a, b, c, color) in triangles) {

                Raster(Project(a, view), Project(b, view), Project(c, view), Shade(a, b, c, color, view), pixels, depth, width, panel * view);

            }

        }

        File.WriteAllBytes(output, PngEncoder.Encode(pixels, width, Height));

        float3 Project(float3 p, int view) {

            var q = p - min;

            return view switch {

                0 => new float3(10 + q.X * scale, Height - 10 - q.Y * scale, max.Z - p.Z),
                1 => new float3(10 + q.Z * scale, Height - 10 - q.Y * scale, p.X - min.X),
                _ => new float3(10 + q.X * scale, 10 + q.Z * scale, q.Y),

            };

        }

    }

    private static float3 Shade(float3 a, float3 b, float3 c, float3 color, int view) {

        var normal = float3.NormalizeOrZero(float3.Cross(b - a, c - a));
        var eye = view switch {

            0 => new float3(0, 0, 1),
            1 => new float3(-1, 0, 0),
            _ => new float3(0, -1, 0),

        };

        var light = float3.Normalize(eye + new float3(0.4f, 0.6f, 0.3f));

        return color * (0.25f + 0.75f * MathF.Abs(float3.Dot(normal, light)));

    }

    private static float3 Sample(TextureData texture, float2 uv) {

        var x = Math.Clamp((int)((uv.X - MathF.Floor(uv.X)) * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)((uv.Y - MathF.Floor(uv.Y)) * texture.Height), 0, texture.Height - 1);
        var i = (y * texture.Width + x) * 4;

        return new float3(texture.Pixels[i], texture.Pixels[i + 1], texture.Pixels[i + 2]) / 255;

    }

    private static void Raster(float3 a, float3 b, float3 c, float3 color, byte[] pixels, float[] depth, int width, int offset) {

        var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var x1 = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
        var y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        if (MathF.Abs(area) < 1e-6f) {

            return;

        }

        for (var y = y0; y <= y1; y++) {

            for (var x = x0; x <= x1; x++) {

                var px = x + 0.5f;
                var py = y + 0.5f;
                var w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                var w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                var w2 = 1 - w0 - w1;

                if (w0 < 0 || w1 < 0 || w2 < 0) {

                    continue;

                }

                var screen = x + offset;

                if (screen >= width) {

                    continue;

                }

                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var index = y * width + screen;

                if (z >= depth[index]) {

                    continue;

                }

                depth[index] = z;
                pixels[index * 4] = (byte)Math.Clamp(color.X * 255, 0, 255);
                pixels[index * 4 + 1] = (byte)Math.Clamp(color.Y * 255, 0, 255);
                pixels[index * 4 + 2] = (byte)Math.Clamp(color.Z * 255, 0, 255);

            }

        }

    }

}
