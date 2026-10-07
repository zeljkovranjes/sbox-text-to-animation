#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

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
