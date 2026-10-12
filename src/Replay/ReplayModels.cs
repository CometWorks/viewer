#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace CometWorks.EntityViewer.Replay;

// Shared source: the companion targets netstandard2.0; Quasar targets net10.0.
public sealed class ReplayRecord
{
    public long At { get; set; }
    public long Tick { get; set; }
    public double Elapsed { get; set; }
    public string Kind { get; set; } = "sample";
    public string Entity { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "grid";
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
    [DefaultValue("")]
    public string Character { get; set; } = "";
    [DefaultValue("")]
    public string Weapon { get; set; } = "";
    [DefaultValue("")]
    public string Related { get; set; } = "";
    // Optional compact character state: aim, primary, secondary, crouch, sprint, use, dead.
    public int? Actions { get; set; }
    public int? Movement { get; set; } // MyCharacterMovementEnum, sampled and transition-only
    public double[]? View { get; set; }
    public int[]? Cell { get; set; }
    public double[]? Position { get; set; }
    public double[]? Rotation { get; set; }
    public double[]? Size { get; set; }
    public double[]? Velocity { get; set; }
    public double[]? AngularVelocity { get; set; }
    public double[]? Gravity { get; set; }
    public double[]? Sun { get; set; }
    public double SunIntensity { get; set; }
    [DefaultValue(1d)]
    public double ModelScale { get; set; } = 1;
    [DefaultValue("")]
    public string TerrainId { get; set; } = "";
    public ReplayVoxelPage? Terrain { get; set; }
    public string Model { get; set; } = "";
    public string Mod { get; set; } = "";
    public double[]? Color { get; set; }
    public double Value { get; set; }
    public int Count { get; set; }
    public bool High { get; set; }
    public double[]? Origin { get; set; }
    public bool GeometryReady { get; set; }
    public double GridSize { get; set; }
    public string LayoutId { get; set; } = "";
    public string[]? Layout { get; set; }
    // Capture-only messages consumed by the writer, never stored as timeline records.
    public ReplayBlock? Block { get; set; }
}

public sealed class ReplayBlock
{
    public int[] Cell { get; set; } = Array.Empty<int>();
    public int[] Min { get; set; } = Array.Empty<int>();
    public int[] Max { get; set; } = Array.Empty<int>();
    public string Definition { get; set; } = "";
    public string Model { get; set; } = "";
    public string Mod { get; set; } = "";
    public string Topology { get; set; } = "";
    public string PartsId { get; set; } = "";
    public byte[]? Patterns { get; set; }
    public ReplayModelParts? Parts { get; set; } // capture only; materialized by writer
    public ReplayBlock StorageCopy() => (ReplayBlock)MemberwiseClone();
    public double[] Translation { get; set; } = Array.Empty<double>();
    public double[] Rotation { get; set; } = Array.Empty<double>();
    public double[] Color { get; set; } = Array.Empty<double>();
    public double Integrity { get; set; }
    public double Build { get; set; }
}

public sealed class ReplayBatch
{
    public int Version { get; set; } = 1;
    public string Session { get; set; } = "";
    public string World { get; set; } = "";
    public Dictionary<string, ReplayBlock[]> Geometry { get; set; } = new Dictionary<string, ReplayBlock[]>();
    public Dictionary<string, ReplayModelParts> ModelParts { get; set; } = new Dictionary<string, ReplayModelParts>();
    public Dictionary<string, ReplayVoxelPage> Voxels { get; set; } = new Dictionary<string, ReplayVoxelPage>();
    public List<ReplayRecord> Records { get; set; } = new List<ReplayRecord>();
}

public sealed class ReplayChunk
{
    public string Id { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public Dictionary<string, byte[]> Geometry { get; set; } = new Dictionary<string, byte[]>();
}

public sealed class ReplayReadRequest
{
    public bool Record { get; set; } // Explicit opt-in; renewed only by the enabled collector.
    public List<string> Acknowledge { get; set; } = new List<string>();
}

public sealed class ReplayRecordingSettings
{
    public bool Enabled { get; set; }
}

public sealed class ReplayPage
{
    public bool Draining { get; set; }
    public List<ReplayChunk> Chunks { get; set; } = new List<ReplayChunk>();
    public long LostChunks { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ReplayLayoutPage
{
    public const int MaxBlocksPerGrid = 50000;
    public string[] Pages { get; set; } = Array.Empty<string>();
}

// Immutable, content-addressed observations. Stored once in the existing 24-hour blob archive.
public sealed class ReplayVoxelPage
{
    public string Kind { get; set; } = "voxel";
    public string Body { get; set; } = "";
    public double[] Origin { get; set; } = Array.Empty<double>();
    public double[] Rotation { get; set; } = Array.Empty<double>();
    public int Lod { get; set; }
    public int[] Min { get; set; } = Array.Empty<int>();
    public int[] Size { get; set; } = Array.Empty<int>();
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public byte[] Materials { get; set; } = Array.Empty<byte>();
    public ReplayVoxelMaterial[] Palette { get; set; } = Array.Empty<ReplayVoxelMaterial>();
}
public class ReplayVoxelMaterial
{
    public int Index { get; set; }
    public string SubtypeId { get; set; } = "";
    public string MaterialTypeName { get; set; } = "";
    public string Mod { get; set; } = "";
    public string ColorMetalXZnY { get; set; } = "";
    public string ColorMetalY { get; set; } = "";
    public string NormalGlossXZnY { get; set; } = "";
    public string NormalGlossY { get; set; } = "";
    public string ExtXZnY { get; set; } = "";
    public string ExtY { get; set; } = "";
    public float TilingScale { get; set; }
}

public sealed class ReplayModelParts
{
    public string Kind { get; set; } = "models";
    public ReplayModelPart[] Parts { get; set; } = Array.Empty<ReplayModelPart>();
}
public sealed class ReplayModelPart
{
    public string Model { get; set; } = "";
    public double[] Matrix { get; set; } = Array.Empty<double>();
}
