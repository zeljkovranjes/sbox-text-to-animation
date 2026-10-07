#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Numerics;
using TextToAnimation.Core.Maths;
using SkeletonModel = TextToAnimation.Core.Rig.Skeleton;

namespace TextToAnimation.Core.Processing;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/TextToAnimation/Assembly.cs)

/// <summary>How root motion should be handled in the output clip.</summary>
public enum RootMotionMode
{
    /// <summary>Leave hips/root channels exactly as solved.</summary>
    Off,

    /// <summary>
    /// Move the ground-projected, smoothed hips trajectory onto the dedicated root bone;
    /// hips keep height and full rotation locally (Unity/UE convention).
    /// </summary>
    Extract,

    /// <summary>Remove horizontal hips travel entirely (in-place clip); root stays put.</summary>
    InPlace,
}
