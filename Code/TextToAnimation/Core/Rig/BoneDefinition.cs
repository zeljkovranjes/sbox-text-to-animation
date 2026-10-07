#nullable enable annotations

using System;
using System.Collections.Generic;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Rig;

/// <summary>
/// Input definition for a single bone, used to build a <see cref="Skeleton"/>.
/// Order does not matter; construction topologically sorts parents before children.
/// </summary>
/// <param name="Name">Unique bone name.</param>
/// <param name="ParentName">Parent bone name, or null for a root bone (multiple roots allowed).</param>
/// <param name="RestLocal">Rest (bind) transform relative to the parent bone, centimeters.</param>
public readonly record struct BoneDefinition(string Name, string? ParentName, XForm RestLocal);
