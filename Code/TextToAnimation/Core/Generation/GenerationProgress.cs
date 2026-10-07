#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

/// <summary>Coarse progress for the UI.</summary>
public readonly record struct GenerationProgress(string Stage, float Fraction, string Detail = "");
