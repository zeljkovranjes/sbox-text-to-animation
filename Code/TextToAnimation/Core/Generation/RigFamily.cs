#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

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
