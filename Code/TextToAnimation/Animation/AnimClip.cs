#nullable enable annotations

using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Animation;

/// <summary>Where a clip in a workspace came from.</summary>
public enum ClipOrigin { Generated, Imported, ImportedFile, Duplicated, Empty }

/// <summary>How the saved sequence treats horizontal root travel.</summary>
public enum ClipRootMotion { Keep, Extract, InPlace }

/// <summary>An animation event on the clip's frame grid (footsteps and user events).</summary>
public sealed class ClipEvent
{
    public string EventClass { get; set; } = "";
    public int Frame { get; set; }
    public string? Attachment { get; set; }
    public string? Foot { get; set; }
    public double? Volume { get; set; }
    /// <summary>True when the event was generated automatically (footsteps) and may be regenerated.</summary>
    public bool Automatic { get; set; }
    public ClipEvent Clone() => new()
    {
        EventClass = EventClass, Frame = Frame, Attachment = Attachment, Foot = Foot, Volume = Volume, Automatic = Automatic,
    };
}

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

/// <summary>The settings that produced a generated clip, kept so it can be regenerated or varied.</summary>
public sealed class GenerationRecord
{
    public string Mode { get; set; } = "";
    public List<string> Prompts { get; set; } = new();
    public int Seed { get; set; }
    public float DurationSeconds { get; set; }
    public float Guidance { get; set; }
    public string Generator { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public GenerationRecord Clone() => new()
    {
        Mode = Mode, Prompts = Prompts.ToList(), Seed = Seed, DurationSeconds = DurationSeconds,
        Guidance = Guidance, Generator = Generator, CreatedUtc = CreatedUtc,
    };
}

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
