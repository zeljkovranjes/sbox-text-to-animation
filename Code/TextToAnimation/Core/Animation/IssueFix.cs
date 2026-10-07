#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Formats;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>One-click fixes the quality panel can offer for an issue.</summary>
public enum IssueFix { None, FixRotations, CleanFootSliding, GroundFeet, MakeSeamlessLoop, RemoveRootDrift }
