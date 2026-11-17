using Brutal.Numerics;
using FullThrust.Assets;

namespace FullThrust.ModelGen;

// Garland-Heckbert quadric simplification by half-edge collapse; collapsing onto an existing vertex keeps its UVs valid.
public static class Decimator {

    private sealed class Quadric {

        public double A2, AB, AC, AD, B2, BC, BD, C2, CD, D2;

        public void Add(double a, double b, double c, double d, double weight) {

            A2 += a * a * weight;
            AB += a * b * weight;
            AC += a * c * weight;
            AD += a * d * weight;
            B2 += b * b * weight;
            BC += b * c * weight;
            BD += b * d * weight;
            C2 += c * c * weight;
            CD += c * d * weight;
            D2 += d * d * weight;

        }

        public void Add(Quadric q) {

            A2 += q.A2;
            AB += q.AB;
            AC += q.AC;
            AD += q.AD;
            B2 += q.B2;
            BC += q.BC;
            BD += q.BD;
            C2 += q.C2;
            CD += q.CD;
            D2 += q.D2;

        }

        public double Error(float3 p) {

            double x = p.X;
            double y = p.Y;
            double z = p.Z;

            return A2 * x * x + 2 * AB * x * y + 2 * AC * x * z + 2 * AD * x + B2 * y * y + 2 * BC * y * z + 2 * BD * y + C2 * z * z + 2 * CD * z + D2;

        }

    }

    // Reduces every mesh in proportion until the whole model holds about target triangles.
    public static void Reduce(ModelData model, int target) {

        var ratio = Math.Clamp((double)target / Math.Max(model.TriangleCount, 1), 0.01, 1);
        var indices = new List<uint>();
        var primitives = new List<PrimitiveData>();
        var meshes = new List<MeshRange>();

        foreach (var mesh in model.Meshes) {

            var triangles = new List<(uint A, uint B, uint C, int Primitive)>();

            for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

                var primitive = model.Primitives[p];

                for (var i = primitive.FirstIndex; i + 2 < primitive.FirstIndex + primitive.IndexCount; i += 3) {

                    triangles.Add((model.Indices[i], model.Indices[i + 1], model.Indices[i + 2], p));

                }

            }

            var kept = Collapse(model.Vertices, triangles, (int)(triangles.Count * ratio));
            var first = primitives.Count;

            for (var p = mesh.FirstPrimitive; p < mesh.FirstPrimitive + mesh.PrimitiveCount; p++) {

                var start = indices.Count;

                foreach (var triangle in kept) {

                    if (triangle.Primitive == p) {

                        indices.Add(triangle.A);
                        indices.Add(triangle.B);
                        indices.Add(triangle.C);

                    }

                }

                if (indices.Count > start) {

                    primitives.Add(new PrimitiveData(start, indices.Count - start, model.Primitives[p].Material));

                }

            }

            meshes.Add(new MeshRange(first, primitives.Count - first, mesh.Name));

        }

        var used = new Dictionary<uint, uint>();
        var compact = new List<MeshVertex>();

        for (var i = 0; i < indices.Count; i++) {

            if (!used.TryGetValue(indices[i], out var mapped)) {

                mapped = (uint)compact.Count;
                used[indices[i]] = mapped;
                compact.Add(model.Vertices[indices[i]]);

            }

            indices[i] = mapped;

        }

        model.Vertices = compact.ToArray();
        model.Indices = indices.ToArray();
        model.Primitives = primitives.ToArray();
        model.Meshes = meshes.ToArray();

    }

    private static List<(uint A, uint B, uint C, int Primitive)> Collapse(MeshVertex[] vertices, List<(uint A, uint B, uint C, int Primitive)> triangles, int target) {

        var faces = triangles.Select(t => new[] { t.A, t.B, t.C }).ToArray();
        var alive = Enumerable.Repeat(true, faces.Length).ToArray();
        var around = new Dictionary<uint, List<int>>();
        var quadrics = new Dictionary<uint, Quadric>();
        var edges = new Dictionary<(uint, uint), int>();

        for (var f = 0; f < faces.Length; f++) {

            var face = faces[f];
            var a = vertices[face[0]].Position;
            var normal = float3.Cross(vertices[face[1]].Position - a, vertices[face[2]].Position - a);
            var area = normal.Length();
            var unit = area > 0 ? normal / area : float3.Zero;
            var d = -float3.Dot(unit, a);

            for (var k = 0; k < 3; k++) {

                var v = face[k];

                if (!around.TryGetValue(v, out var list)) {

                    around[v] = list = [];
                    quadrics[v] = new Quadric();

                }

                list.Add(f);
                quadrics[v].Add(unit.X, unit.Y, unit.Z, d, area);

                var key = Key(v, face[(k + 1) % 3]);

                edges[key] = edges.GetValueOrDefault(key) + 1;

            }

        }

        // Open edges are mesh borders and UV seams; moving their vertices would tear the surface.
        var locked = new HashSet<uint>();

        foreach (var (key, count) in edges) {

            if (count != 2) {

                locked.Add(key.Item1);
                locked.Add(key.Item2);

            }

        }

        var version = new Dictionary<uint, int>();
        var queue = new PriorityQueue<(uint From, uint To, int Version), double>();

        void Push(uint from) {

            if (locked.Contains(from)) {

                return;

            }

            var stamp = version.GetValueOrDefault(from);

            foreach (var f in around[from]) {

                if (!alive[f]) {

                    continue;

                }

                foreach (var to in faces[f]) {

                    if (to != from) {

                        queue.Enqueue((from, to, stamp), quadrics[from].Error(vertices[to].Position) + quadrics[to].Error(vertices[to].Position));

                    }

                }

            }

        }

        foreach (var v in around.Keys) {

            Push(v);

        }

        var remaining = faces.Length;

        while (remaining > target && queue.TryDequeue(out var edge, out _)) {

            var (from, to, stamp) = edge;

            if (version.GetValueOrDefault(from) != stamp || !Collapsible(from, to, faces, alive, around, vertices)) {

                continue;

            }

            foreach (var f in around[from]) {

                if (!alive[f]) {

                    continue;

                }

                var face = faces[f];

                if (Array.IndexOf(face, to) >= 0) {

                    alive[f] = false;
                    remaining--;

                    continue;

                }

                face[Array.IndexOf(face, from)] = to;
                around[to].Add(f);

            }

            around[from].Clear();
            quadrics[to].Add(quadrics[from]);
            version[to] = version.GetValueOrDefault(to) + 1;

            foreach (var f in around[to]) {

                if (!alive[f]) {

                    continue;

                }

                foreach (var v in faces[f]) {

                    version[v] = version.GetValueOrDefault(v) + 1;
                    Push(v);

                }

            }

        }

        var result = new List<(uint A, uint B, uint C, int Primitive)>(remaining);

        for (var f = 0; f < faces.Length; f++) {

            if (alive[f]) {

                result.Add((faces[f][0], faces[f][1], faces[f][2], triangles[f].Primitive));

            }

        }

        return result;

    }

    // Only edges whose ends share exactly two neighbours keep the surface manifold, and no surviving face may flip.
    private static bool Collapsible(uint from, uint to, uint[][] faces, bool[] alive, Dictionary<uint, List<int>> around, MeshVertex[] vertices) {

        var shared = 0;
        var linkFrom = new HashSet<uint>();

        foreach (var f in around[from]) {

            if (!alive[f]) {

                continue;

            }

            var face = faces[f];

            foreach (var v in face) {

                linkFrom.Add(v);

            }

            if (Array.IndexOf(face, to) >= 0) {

                shared++;

                continue;

            }

            var before = Normal(vertices[face[0]].Position, vertices[face[1]].Position, vertices[face[2]].Position);
            var after = Normal(Moved(face[0], from, to, vertices), Moved(face[1], from, to, vertices), Moved(face[2], from, to, vertices));

            if (float3.Dot(before, after) < 0.3f) {

                return false;

            }

        }

        if (shared != 2) {

            return false;

        }

        var common = new HashSet<uint>();

        foreach (var f in around[to]) {

            if (!alive[f]) {

                continue;

            }

            foreach (var v in faces[f]) {

                if (v != to && v != from && linkFrom.Contains(v)) {

                    common.Add(v);

                }

            }

        }

        return common.Count == 2;

    }

    private static float3 Moved(uint v, uint from, uint to, MeshVertex[] vertices) => vertices[v == from ? to : v].Position;

    private static float3 Normal(float3 a, float3 b, float3 c) => float3.NormalizeOrZero(float3.Cross(b - a, c - a));

    private static (uint, uint) Key(uint a, uint b) => a < b ? (a, b) : (b, a);

}
