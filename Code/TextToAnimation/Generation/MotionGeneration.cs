#nullable enable annotations

using TextToAnimation.Animation;
using TextToAnimation.Maths;

namespace TextToAnimation.Generation;

/// <summary>What the user asks a motion model to do.</summary>
public enum GenerationMode
{
    /// <summary>Create motion from a text prompt.</summary>
    TextToMotion,
    /// <summary>Fill the frames between pinned key poses of an existing clip.</summary>
    InBetween,
    /// <summary>Keep the locked bones of an existing clip and regenerate the rest from a new prompt.</summary>
    TextEdit,
    /// <summary>Chain several prompts into one long motion ("walk, look behind, then run").</summary>
    Expansion,
    /// <summary>New takes of an existing motion.</summary>
    Variation,
}

/// <summary>
/// What kind of motion data a rig resembles, which picks the model's normalisation statistics. Auto
/// decides from the skeleton's shape (two legs, two arms and a head: humanoid; legs, wings or a tail: animal).
/// </summary>
public enum RigFamily
{
    Auto,
    /// <summary>People and humanoid characters (UniMate's Mixamo statistics).</summary>
    Humanoid,
    /// <summary>Animals and creatures: quadrupeds, birds, dinosaurs, reptiles, snakes (Truebones statistics).</summary>
    Animal,
    /// <summary>Anything else: robots, props, unusual rigs (Objaverse statistics).</summary>
    Object,
}

/// <summary>A generation job. Frame data always uses the workspace skeleton (the generator converts internally).</summary>
public sealed class GenerationRequest
{
    public GenerationMode Mode { get; init; } = GenerationMode.TextToMotion;

    /// <summary>One prompt, or one per segment for <see cref="GenerationMode.Expansion"/>.</summary>
    public IReadOnlyList<string> Prompts { get; init; } = Array.Empty<string>();

    /// <summary>Requested length in seconds (per segment for expansion). 0 = model default.</summary>
    public float DurationSeconds { get; init; }

    /// <summary>Frame rate of the returned motion (the workspace's frame rate).</summary>
    public float OutputFps { get; init; } = 30f;

    public int Seed { get; init; }

    /// <summary>How many takes to produce (variations / batch generate).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Prompt adherence (classifier-free guidance). 0 = model default.</summary>
    public float Guidance { get; init; }

    /// <summary>The existing motion for in-betweening, editing and variations (final evaluated frames).</summary>
    public IReadOnlyList<XForm[]>? SourceFrames { get; init; }
    public float SourceFps { get; init; } = 30f;

    /// <summary>Source frames whose full pose must be kept (in-betweening).</summary>
    public IReadOnlyList<int> KeepFrames { get; init; } = Array.Empty<int>();

    /// <summary>Skeleton bone indices whose motion must be kept on every frame (text-guided editing).</summary>
    public IReadOnlyCollection<int> KeepBones { get; init; } = Array.Empty<int>();

    /// <summary>For variations: 0 = almost the same motion, 1 = only loosely related.</summary>
    public float VariationStrength { get; init; } = 0.5f;

    /// <summary>Frames shared between consecutive expansion segments.</summary>
    public int ExpansionOverlapFrames { get; init; } = 10;

    /// <summary>Sampling steps (quality vs speed). 0 = model default.</summary>
    public int Steps { get; init; }

    /// <summary>Which kind of motion data the rig resembles (Auto = decided from the skeleton).</summary>
    public RigFamily RigFamily { get; init; } = RigFamily.Auto;
}

/// <summary>Coarse progress for the UI.</summary>
public readonly record struct GenerationProgress(string Stage, float Fraction, string Detail = "");

/// <summary>One generated take, on the workspace skeleton.</summary>
public sealed class GeneratedMotion
{
    public required List<XForm[]> Frames { get; init; }
    public required float Fps { get; init; }
    public int Seed { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>What a generator supports, for enabling UI.</summary>
public sealed class GeneratorCapabilities
{
    public IReadOnlyCollection<GenerationMode> Modes { get; init; } = Array.Empty<GenerationMode>();
    public float NativeFps { get; init; } = 30f;
    public float MaxSegmentSeconds { get; init; } = 10f;
    public float DefaultSeconds { get; init; } = 4f;
    public float DefaultGuidance { get; init; } = 2.5f;
}

/// <summary>
/// A text-to-motion model. The editor talks only to this interface, so other models can be added
/// later without touching the UI or the workspace code.
/// </summary>
public interface IMotionGenerator
{
    /// <summary>Display name ("UniMate").</summary>
    string Name { get; }

    GeneratorCapabilities Capabilities { get; }

    /// <summary>Checks a rig can be animated and returns problems (empty = fine).</summary>
    IReadOnlyList<string> Validate(MotionRig rig);

    /// <summary>The family <see cref="RigFamily.Auto"/> resolves to for this rig (shown in the UI as the default).</summary>
    RigFamily DetectFamily(MotionRig rig);

    /// <summary>Runs on a worker thread; must honour cancellation and never touch engine objects.</summary>
    Task<IReadOnlyList<GeneratedMotion>> GenerateAsync(MotionRig rig, GenerationRequest request,
        Action<GenerationProgress>? progress, CancellationToken token);
}
