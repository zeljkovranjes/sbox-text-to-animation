#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>
/// One animation clip in a workspace: dense parent-local poses (one <see cref="XForm"/> per skeleton bone
/// per frame, skeleton units) plus everything the editor layers on top - events, pinned frames used as
/// in-between constraints, bone locks used by text-guided editing, bone keyframes, and export options.
/// </summary>
public sealed class AnimClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Animation";
    public float Fps { get; set; } = 30f;
    public bool Looping { get; set; }
    public List<XForm[]> Frames { get; set; } = new();
    public List<ClipEvent> Events { get; set; } = new();

    /// <summary>Frames whose full pose is held fixed when regenerating (motion in-betweening constraints).</summary>
    public SortedSet<int> PinnedFrames { get; set; } = new();

    /// <summary>Bones (by name) preserved when regenerating with a new prompt (text-guided editing).</summary>
    public HashSet<string> LockedBones { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Pose edits authored on top of <see cref="Frames"/>, per bone name.</summary>
    public KeyLayer Keys { get; set; } = new();

    public ClipOrigin Origin { get; set; } = ClipOrigin.Empty;

    /// <summary>For imported clips: the sequence name in the vmdl it came from (the one "Replace" overwrites).</summary>
    public string? SourceSequence { get; set; }

    public GenerationRecord? Generation { get; set; }
    public ClipExportSettings Export { get; set; } = new();

    /// <summary>When the clip was last written into the vmdl (null = never saved).</summary>
    public DateTime? SavedUtc { get; set; }

    /// <summary>Incremented on every change; the workspace compares it against the saved revision.</summary>
    public int Revision { get; set; }

    public int FrameCount => Frames.Count;
    public float Duration => FrameCount <= 1 ? 0f : (FrameCount - 1) / Fps;

    /// <summary>Sequence name used inside the vmdl.</summary>
    public string EffectiveSequenceName => string.IsNullOrWhiteSpace(Export.SequenceName)
        ? SanitizeSequenceName(Name)
        : SanitizeSequenceName(Export.SequenceName);

    /// <summary>Final poses: <see cref="Frames"/> with the key layer applied.</summary>
    public List<XForm[]> EvaluateFrames(Rig.Skeleton skeleton) => Keys.IsEmpty ? Frames : Keys.Apply(Frames, skeleton);

    /// <summary>Deep copy (frames, events, pins, locks, keys and settings) with a new id.</summary>
    public AnimClip Duplicate(string name)
    {
        var copy = CloneDeep();
        copy.Id = Guid.NewGuid();
        copy.Name = name;
        copy.Origin = ClipOrigin.Duplicated;
        copy.SavedUtc = null;
        copy.Export.SequenceName = "";
        copy.Revision = 0;
        return copy;
    }

    /// <summary>Deep copy that keeps the id (used for undo snapshots).</summary>
    public AnimClip CloneDeep() => new()
    {
        Id = Id,
        Name = Name,
        Fps = Fps,
        Looping = Looping,
        Frames = CopyFrames(Frames),
        Events = Events.Select(e => e.Clone()).ToList(),
        PinnedFrames = new SortedSet<int>(PinnedFrames),
        LockedBones = new HashSet<string>(LockedBones, StringComparer.Ordinal),
        Keys = Keys.Clone(),
        Origin = Origin,
        SourceSequence = SourceSequence,
        Generation = Generation?.Clone(),
        Export = Export.Clone(),
        SavedUtc = SavedUtc,
        Revision = Revision,
    };

    public static List<XForm[]> CopyFrames(IReadOnlyList<XForm[]> frames)
    {
        var copy = new List<XForm[]>(frames.Count);
        foreach (var f in frames)
        {
            var c = new XForm[f.Length];
            Array.Copy(f, c, f.Length);
            copy.Add(c);
        }
        return copy;
    }

    /// <summary>
    /// Makes a valid vmdl sequence name: letters, digits and underscores, not starting with a digit.
    /// </summary>
    public static string SanitizeSequenceName(string name)
    {
        var chars = (name ?? "").Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length == 0) s = "animation";
        if (char.IsDigit(s[0])) s = "a_" + s;
        return s;
    }
}
