#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Formats;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

public sealed record ClipIssue(IssueSeverity Severity, string Code, string Message, int? Frame = null, string? Bone = null, IssueFix Fix = IssueFix.None);
