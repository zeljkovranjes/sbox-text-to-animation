using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>A decoded motion in UniMate's canonical space.</summary>
public sealed class UniMateMotion
{
	/// <summary>(T,J) T-pose-relative local rotations (BVH order; joint 0 = root world rotation).</summary>
	public Quaternion[,] Local;
	/// <summary>(T) root position in canonical space.</summary>
	public Vector3[] Root;
	public int Frames => Root.Length;
}
