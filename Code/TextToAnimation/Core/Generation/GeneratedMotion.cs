#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

/// <summary>One generated take, on the workspace skeleton.</summary>
public sealed class GeneratedMotion
{
    public required List<XForm[]> Frames { get; init; }
    public required float Fps { get; init; }
    public int Seed { get; init; }
    public List<string> Notes { get; init; } = new();
}
