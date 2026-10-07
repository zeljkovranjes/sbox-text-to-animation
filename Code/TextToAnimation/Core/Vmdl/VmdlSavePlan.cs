#nullable enable annotations

using System.Globalization;
using System.Numerics;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Formats;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using RigClip = TextToAnimation.Core.Rig.Clip;

namespace TextToAnimation.Core.Vmdl;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>The planned result of saving clips into a vmdl: the new vmdl text and every DMX it references.</summary>
public sealed class VmdlSavePlan
{
    public required string VmdlText { get; init; }
    public required string OriginalVmdlText { get; init; }
    public List<OutputFile> Files { get; } = new();
    /// <summary>Sequence names this save writes (for compile verification).</summary>
    public List<string> Sequences { get; } = new();
    /// <summary>Expected frames per sequence (engine space, for round-trip verification).</summary>
    public Dictionary<string, List<XForm[]>> Expected { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = new();
}
