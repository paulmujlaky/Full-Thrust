using Brutal.Numerics;
using System.Text.Json;
using FullThrust.Assets;
using FullThrust.Core.Parts;
using FullThrust.ModelGen;

// dotnet run --project tools/FullThrust.ModelGen -- [build [recipes.json]]  | profile <model.glb> [rx ry rz [upx upy upz]] | axis <model.glb> [mesh] | preview <model.glb> <out.png> [rx ry rz [upx upy upz]]| inspect <model.glb>
var repository = FindRepository();
var modelsRoot = Path.Combine(repository, "data", "models");

if (args.Length >= 2 && args[0] == "inspect") {

    Inspect(args[1]);

    return;

}

if (args.Length >= 3 && args[0] == "preview") {

    var numbers = args.Skip(3).Select(double.Parse).ToArray();

    Preview.Render(args[1], args[2], SourceModel.Rotation(numbers.Length >= 6 ? numbers[3..6] : null, numbers.Length >= 3 ? numbers[..3] : [0, 0, 0]));

    return;

}

if (args.Length >= 2 && args[0] == "axis") {

    Axis(args[1], args.Length >= 3 ? args[2] : "");

    return;

}

if (args.Length >= 2 && args[0] == "profile") {

    var numbers = args.Skip(2).Select(double.Parse).ToArray();

    Profile(args[1], numbers.Length >= 3 ? numbers[..3] : [0, 0, 0], numbers.Length >= 6 ? numbers[3..6] : null);

    return;

}

var recipesPath = args.Length >= 2 && args[0] == "build" ? args[1] : Path.Combine(modelsRoot, "recipes.json");

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
var recipes = JsonSerializer.Deserialize<RecipeFile>(File.ReadAllText(recipesPath), options) ?? throw new InvalidDataException("Recipe file is empty.");
var parts = PartCatalogLoader.LoadFile(Path.Combine(repository, "data", "parts", "parts.json"));

foreach (var recipe in recipes.Models) {

    if (recipe.Part is { } id) {

        var part = parts[id];
        recipe.Length = recipe.Length > 0 ? recipe.Length : part.Length;
        recipe.Diameter = recipe.Diameter > 0 ? recipe.Diameter : part.Radius * 2;

        if (recipe.EngineDiameter <= 0 && part.Module<EngineDefinition>()?.Cluster is { } cluster) {

            recipe.EngineDiameter = cluster.Diameter;
            recipe.EngineLength = cluster.Length;
            recipe.RingRadius = cluster.RingRadius;
            recipe.RingEngines = cluster.RingCount;
            recipe.CenterEngine = cluster.Center;

        }

    }

    var model = Generators.Build(recipe, modelsRoot);
    var output = Path.Combine(modelsRoot, recipe.Output);

    GltfModelWriter.WriteGlb(model, output, "FullThrust.ModelGen");

    Console.WriteLine($"{recipe.Output}: {model.TriangleCount} triangles, {model.Textures.Length} textures, {new FileInfo(output).Length / 1024} KiB, bounds {model.BoundsMin} .. {model.BoundsMax}");

}

static void Inspect(string path) {

    var model = GltfModelLoader.Load(path);

    Console.WriteLine($"{model.Name}: {model.TriangleCount} triangles, bounds {model.BoundsMin} .. {model.BoundsMax}");

    for (var i = 0; i < model.Instances.Length; i++) {

        var instance = model.Instances[i];
        var mesh = model.Meshes[instance.Mesh];
        var single = new ModelData { Vertices = model.Vertices, Indices = model.Indices, Primitives = model.Primitives, Meshes = model.Meshes, Instances = [instance] };

        single.ComputeBounds();

        var materials = string.Join(",", Enumerable.Range(mesh.FirstPrimitive, mesh.PrimitiveCount).Select(p => model.Materials[model.Primitives[p].Material].Name));

        var triangles = Enumerable.Range(mesh.FirstPrimitive, mesh.PrimitiveCount).Sum(p => model.Primitives[p].IndexCount / 3);
        var centroid = float3.Zero;
        var area = 0f;

        for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

            var primitive = model.Primitives[p];

            for (var t = primitive.FirstIndex; t + 2 < primitive.FirstIndex + primitive.IndexCount; t += 3) {

                var a = float3.Transform(model.Vertices[model.Indices[t]].Position, instance.Transform);
                var b = float3.Transform(model.Vertices[model.Indices[t + 1]].Position, instance.Transform);
                var c = float3.Transform(model.Vertices[model.Indices[t + 2]].Position, instance.Transform);
                var weight = float3.Cross(b - a, c - a).Length();

                centroid += (a + b + c) / 3 * weight;
                area += weight;

            }

        }

        centroid /= MathF.Max(area, 1e-12f);

        Console.WriteLine($"  [{i}] {mesh.Name} {triangles} tris centroid {centroid} {single.BoundsMin} .. {single.BoundsMax} {materials}");

    }

}

// Silhouette by height: how far the surface reaches from the vertical axis and which material dominates, to choose crop heights.
static void Profile(string path, double[] degrees, double[]? up) {

    var model = GltfModelLoader.Load(path);
    var rotation = SourceModel.Rotation(up, degrees);
    var points = new List<(float3 Position, int Material)>();

    foreach (var instance in model.Instances) {

        var mesh = model.Meshes[instance.Mesh];

        for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

            var primitive = model.Primitives[p];

            for (var i = primitive.FirstIndex; i < primitive.FirstIndex + primitive.IndexCount; i++) {

                points.Add((float3.Transform(model.Vertices[model.Indices[i]].Position, instance.Transform * rotation), primitive.Material));

            }

        }

    }

    var min = points.Aggregate(new float3(float.MaxValue), (m, p) => float3.Min(m, p.Position));
    var max = points.Aggregate(new float3(float.MinValue), (m, p) => float3.Max(m, p.Position));
    var center = (min + max) * 0.5f;
    const int Slices = 60;

    Console.WriteLine($"bounds {min} .. {max}, axis at x {center.X} z {center.Z}");

    for (var s = Slices - 1; s >= 0; s--) {

        var y0 = min.Y + (max.Y - min.Y) * s / Slices;
        var y1 = min.Y + (max.Y - min.Y) * (s + 1) / Slices;
        var slice = points.Where(p => p.Position.Y >= y0 && p.Position.Y < y1).ToList();

        if (slice.Count == 0) {

            Console.WriteLine($"{y0,10:F3}  -");

            continue;

        }

        var radii = slice.Select(p => MathF.Sqrt((p.Position.X - center.X) * (p.Position.X - center.X) + (p.Position.Z - center.Z) * (p.Position.Z - center.Z))).Order().ToArray();
        var reach = radii[^1];
        var median = radii[radii.Length / 2];
        var material = slice.GroupBy(p => p.Material).OrderByDescending(g => g.Count()).First().Key;

        Console.WriteLine($"{y0,10:F3}  r {reach,8:F3}  median {median,8:F3}  {new string('#', (int)(40 * reach / MathF.Max(max.X - center.X, max.Z - center.Z)))}  {model.Materials[material].Name}");

    }

}

// Principal axes of the surface; a body of revolution has its symmetry axis as the eigenvector with the odd eigenvalue out.
static void Axis(string path, string filter) {

    var model = GltfModelLoader.Load(path);
    var sum = 0.0;
    var mean = new double[3];
    var triangles = new List<(double[] Center, double Weight)>();

    foreach (var instance in model.Instances) {

        var mesh = model.Meshes[instance.Mesh];

        if (!mesh.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) {

            continue;

        }

        for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

            var primitive = model.Primitives[p];

            for (var t = primitive.FirstIndex; t + 2 < primitive.FirstIndex + primitive.IndexCount; t += 3) {

                var a = float3.Transform(model.Vertices[model.Indices[t]].Position, instance.Transform);
                var b = float3.Transform(model.Vertices[model.Indices[t + 1]].Position, instance.Transform);
                var c = float3.Transform(model.Vertices[model.Indices[t + 2]].Position, instance.Transform);
                var center = (a + b + c) / 3;
                var weight = float3.Cross(b - a, c - a).Length();

                triangles.Add(([center.X, center.Y, center.Z], weight));
                sum += weight;

                for (var k = 0; k < 3; k++) {

                    mean[k] += center[k] * weight;

                }

            }

        }

    }

    var covariance = new double[3, 3];

    for (var k = 0; k < 3; k++) {

        mean[k] /= sum;

    }

    foreach (var (center, weight) in triangles) {

        for (var i = 0; i < 3; i++) {

            for (var j = 0; j < 3; j++) {

                covariance[i, j] += (center[i] - mean[i]) * (center[j] - mean[j]) * weight / sum;

            }

        }

    }

    var vectors = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    for (var sweep = 0; sweep < 50; sweep++) {

        for (var p = 0; p < 2; p++) {

            for (var q = p + 1; q < 3; q++) {

                if (Math.Abs(covariance[p, q]) < 1e-15) {

                    continue;

                }

                var theta = 0.5 * Math.Atan2(2 * covariance[p, q], covariance[q, q] - covariance[p, p]);
                var (s, c) = Math.SinCos(theta);

                for (var k = 0; k < 3; k++) {

                    var kp = covariance[k, p];
                    var kq = covariance[k, q];

                    covariance[k, p] = c * kp - s * kq;
                    covariance[k, q] = s * kp + c * kq;

                }

                for (var k = 0; k < 3; k++) {

                    var pk = covariance[p, k];
                    var qk = covariance[q, k];

                    covariance[p, k] = c * pk - s * qk;
                    covariance[q, k] = s * pk + c * qk;

                }

                for (var k = 0; k < 3; k++) {

                    var kp = vectors[k, p];
                    var kq = vectors[k, q];

                    vectors[k, p] = c * kp - s * kq;
                    vectors[k, q] = s * kp + c * kq;

                }

            }

        }

    }

    Console.WriteLine($"centroid {mean[0]:F4} {mean[1]:F4} {mean[2]:F4}");

    for (var k = 0; k < 3; k++) {

        Console.WriteLine($"eigenvalue {covariance[k, k]:F4}  axis {vectors[0, k]:F5} {vectors[1, k]:F5} {vectors[2, k]:F5}");

    }

}

static string FindRepository() {

    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent) {

        if (File.Exists(Path.Combine(directory.FullName, "FullThrust.slnx"))) {

            return directory.FullName;

        }

    }

    throw new InvalidOperationException("Run ModelGen from inside the Full-Thrust repository.");

}
