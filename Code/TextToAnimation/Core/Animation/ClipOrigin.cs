#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>Where a clip in a workspace came from.</summary>
public enum ClipOrigin { Generated, Imported, ImportedFile, Duplicated, Empty }
