#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Core.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>Which side of the character's mirror plane a bone is on.</summary>
public enum BoneSide { Center, Left, Right }
