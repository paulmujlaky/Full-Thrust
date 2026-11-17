using Brutal.Numerics;

namespace FullThrust.ModelGen;

public sealed class RecipeFile {

    public List<Recipe> Models { get; set; } = [];

}

// One generated model. Unused fields are ignored by the recipe type.
public sealed class Recipe {

    // Relative to data/models.
    public string Output { get; set; } = "";

    // tank, pod, ring, rcs, cluster, crop, launchpad.
    public string Type { get; set; } = "";

    // Takes length and diameter from data/parts/parts.json.
    public string? Part { get; set; }

    public double Length { get; set; }

    public double Diameter { get; set; }

    public double TopDiameter { get; set; }

    public double[] Paint { get; set; } = [0.85, 0.85, 0.83];

    public double[] Accent { get; set; } = [0.05, 0.05, 0.05];

    public double PaintMetallic { get; set; }

    public double PaintRoughness { get; set; } = 0.45;

    // solid, roll, radiator, corrugated.
    public string Pattern { get; set; } = "solid";

    public List<Band> Bands { get; set; } = [];

    public bool Raceway { get; set; } = true;

    public int Windows { get; set; }

    public double HeatShieldDepth { get; set; } = 0.25;

    public double NoseHeight { get; set; }

    public bool DockingTunnel { get; set; }

    // crop and cluster: a source model relative to data/models.
    public string? Source { get; set; }

    public double[] SourceRotationDeg { get; set; } = [0, 0, 0];

    public double CropMinY { get; set; } = double.NegativeInfinity;

    public double CropMaxY { get; set; } = double.PositiveInfinity;

    public bool CloseHeatShield { get; set; }

    // crop: a box in the rotated source, as [x, y, z]; overrides cropMinY and cropMaxY.
    public double[]? CropMin { get; set; }

    public double[]? CropMax { get; set; }

    // crop: source meshes and materials to leave out, matched by name fragment.
    public List<string> DropMeshes { get; set; } = [];

    public List<string> DropMaterials { get; set; } = [];

    // crop: when set, only source meshes matching one of these name fragments are kept.
    public List<string> KeepMeshes { get; set; } = [];

    // crop: triangle budget for scanned sources; 0 keeps every triangle.
    public int Decimate { get; set; }

    // crop: the source direction turned to +Y before sourceRotationDeg, for scans whose axis is tilted.
    public double[]? SourceUp { get; set; }

    // crop: the body diameter in source units, so fins and legs past it do not shrink the fit; 0 uses the bounds.
    public double SourceDiameter { get; set; }

    // crop: where the body axis crosses the rotated source, as [x, z], used with sourceDiameter; omitted uses the bounds centre.
    public double[]? SourceAxis { get; set; }

    // crop: give untextured surfaces the generated tank skin, with paint, accent, pattern and bands as for a tank.
    public bool Repaint { get; set; }

    // crop: close open tube ends with bulkhead discs.
    public bool CapEnds { get; set; }

    // crop: repaint exactly these materials, matched by name fragment; empty repaints every untextured one.
    public List<string> RepaintMaterials { get; set; } = [];

    // crop: textures larger than this are halved until they fit; 0 keeps them.
    public int MaxTextureSize { get; set; }

    // crop: none, stretch, uniform or length, against the part's diameter and length.
    public string Fit { get; set; } = "none";

    public int RingEngines { get; set; }

    public bool CenterEngine { get; set; }

    public double RingRadius { get; set; }

    public double EngineDiameter { get; set; }

    public double EngineLength { get; set; }

    public bool VacuumExtension { get; set; }

    public int Seed { get; set; } = 1;

    public float3 PaintColor => Color(Paint);

    public float3 AccentColor => Color(Accent);

    private static float3 Color(double[] c) => c.Length >= 3 ? new float3((float)c[0], (float)c[1], (float)c[2]) : new float3(0.8f);

}

// A painted or bare-metal ring on a tank, in metres from the bottom.
public sealed class Band {

    public double From { get; set; }

    public double To { get; set; }

    // accent or metal.
    public string Material { get; set; } = "accent";

}
