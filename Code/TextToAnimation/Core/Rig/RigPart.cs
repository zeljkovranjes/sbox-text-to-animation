#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Core.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>What a bone is part of, found from the skeleton's shape.</summary>
public enum RigPart { Excluded, Root, Hips, Spine, Neck, Head, HeadPart, Tail, Arm, Leg, FrontLeg, Wing, Fin, Digit, Other }
