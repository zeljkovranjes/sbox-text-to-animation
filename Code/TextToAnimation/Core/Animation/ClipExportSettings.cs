#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>Per-clip options used when the clip is saved into a vmdl or exported.</summary>
public sealed class ClipExportSettings
{
    /// <summary>Sequence name inside the vmdl. Empty = derived from the clip name.</summary>
    public string SequenceName { get; set; } = "";
    public ClipRootMotion RootMotion { get; set; } = ClipRootMotion.Keep;
    public bool Footsteps { get; set; } = true;
    public bool AdditiveVariant { get; set; }
    public int AdditiveReferenceFrame { get; set; }
    public bool MirroredVariant { get; set; }
    public ClipExportSettings Clone() => new()
    {
        SequenceName = SequenceName, RootMotion = RootMotion, Footsteps = Footsteps, AdditiveVariant = AdditiveVariant,
        AdditiveReferenceFrame = AdditiveReferenceFrame, MirroredVariant = MirroredVariant,
    };
}
