#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Core.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>One limb: a chain of bones hanging off the central body.</summary>
public sealed class RigLimb
{
    public required BoneSide Side { get; init; }
    public required LimbKind Kind { get; set; }
    /// <summary>The central bone the limb hangs from.</summary>
    public required int Attach { get; init; }
    /// <summary>Main chain from the limb root outwards; ends at the fan bone (hand, foot) when the limb has digits.</summary>
    public required List<int> Chain { get; init; }
    /// <summary>Bones below the chain's end (fingers, toes) and side branches.</summary>
    public required List<int> Digits { get; init; }
    public required bool Grounded { get; init; }
    /// <summary>Sum of the chain's bone lengths.</summary>
    public required float Length { get; init; }
    /// <summary>The mirrored limb on the other side, or null.</summary>
    public RigLimb? Partner { get; set; }
}
