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

/// <summary>Options for one clip being saved.</summary>
public sealed class ClipSaveRequest
{
    public required AnimClip Clip { get; init; }
    /// <summary>Final frames (keys applied).</summary>
    public required List<XForm[]> Frames { get; init; }
    /// <summary>Sequence to write. Must be new unless <see cref="ReplaceExisting"/> is set.</summary>
    public required string SequenceName { get; init; }
    /// <summary>Overwrite the AnimFile of the same name in place, keeping its other settings.</summary>
    public bool ReplaceExisting { get; init; }
}
