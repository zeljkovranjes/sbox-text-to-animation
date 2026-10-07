#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Core.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>How the character's facing direction is measured (UniMate's face joints).</summary>
public enum RigFacing { None, Pair, BodyAxis }
