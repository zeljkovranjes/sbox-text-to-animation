using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>Result of mapping a canonical motion to the source rig (BFS joint order).</summary>
public sealed class SourceMotion
{
	public Quaternion[,] WorldRot;   // (T,J)
	public Quaternion[,] LocalRot;   // (T,J), relative to the UniMate parent joint
	public Vector3[] RootPos;        // (T) root joint world position, source space
}
