#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

/// <summary>What a generator supports, for enabling UI.</summary>
public sealed class GeneratorCapabilities
{
    public IReadOnlyCollection<GenerationMode> Modes { get; init; } = Array.Empty<GenerationMode>();
    public float NativeFps { get; init; } = 30f;
    public float MaxSegmentSeconds { get; init; } = 10f;
    public float DefaultSeconds { get; init; } = 4f;
    public float DefaultGuidance { get; init; } = 2.5f;
}
