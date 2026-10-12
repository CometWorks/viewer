#nullable enable
using System;
using System.Collections.Generic;

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
    public double[]? Position { get; set; }
    public double[]? Rotation { get; set; }
    public double[]? Size { get; set; }
    public double[]? Velocity { get; set; }
    public double[]? AngularVelocity { get; set; }
    public double[]? Gravity { get; set; }
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
    public List<string> Acknowledge { get; set; } = new List<string>();
}

public sealed class ReplayPage
{
    public List<ReplayChunk> Chunks { get; set; } = new List<ReplayChunk>();
    public long LostChunks { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ReplayLayoutPage
{
    public const int MaxBlocksPerGrid = 50000;
    public string[] Pages { get; set; } = Array.Empty<string>();
}
