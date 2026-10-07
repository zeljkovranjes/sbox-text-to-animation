#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

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

    /// <summary>
    /// Clean the generated motion (smooth jitter, lock planted feet). Off = the model's raw output. Pinned frames
    /// and locked bones are restored afterwards either way.
    /// </summary>
    public bool CleanUp { get; init; } = true;

    /// <summary>Sampling steps (quality vs speed). 0 = model default.</summary>
    public int Steps { get; init; }

    /// <summary>Which kind of motion data the rig resembles (Auto = decided from the skeleton).</summary>
    public RigFamily RigFamily { get; init; } = RigFamily.Auto;
}
