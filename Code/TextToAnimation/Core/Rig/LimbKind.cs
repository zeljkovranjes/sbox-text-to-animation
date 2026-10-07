#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Core.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>A limb kind, found from where a mirrored chain attaches and whether it reaches the ground.</summary>
public enum LimbKind { Leg, FrontLeg, Arm, Wing, HeadPart, Fin, Other }
