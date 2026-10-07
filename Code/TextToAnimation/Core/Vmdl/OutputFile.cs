#nullable enable annotations

using System.Globalization;
using System.Numerics;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Formats;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using RigClip = TextToAnimation.Core.Rig.Clip;

namespace TextToAnimation.Core.Vmdl;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>A file the save writes next to the model.</summary>
public sealed record OutputFile(string AssetPath, string Content);
