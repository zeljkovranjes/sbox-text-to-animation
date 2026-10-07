#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Mapping;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using TextToAnimation.Core.Rig;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>Coarse body region of a bone, used by the lock tools ("lock arms") and the generator masks.</summary>
public enum BodyRegion { Root, Spine, Head, ArmL, ArmR, HandL, HandR, LegL, LegR, Other, Tail }
