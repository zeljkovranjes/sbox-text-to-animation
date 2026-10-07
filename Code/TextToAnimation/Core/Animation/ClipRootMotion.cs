#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>How the saved sequence treats horizontal root travel.</summary>
public enum ClipRootMotion { Keep, Extract, InPlace }
