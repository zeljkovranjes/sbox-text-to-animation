#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Core.Maths;
using SkeletonModel = TextToAnimation.Core.Rig.Skeleton;

namespace TextToAnimation.Core.Processing;

using Vector3 = System.Numerics.Vector3; // s&box compat: shadow engine's global-namespace Vector3 (see Code/TextToAnimation/Assembly.cs)

/// <summary>Results of a <see cref="FootPlant.Apply"/> run.</summary>
public sealed class FootPlantReport
{
    /// <summary>Left-foot results.</summary>
    public required FootPlantFootReport Left { get; init; }

    /// <summary>Right-foot results.</summary>
    public required FootPlantFootReport Right { get; init; }

    /// <summary>Estimated ground level (height along the up axis), centimeters.</summary>
    public float GroundHeight { get; set; }
}
