using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>
/// The (T,J,12) motion features: encoding user motion, decoding model output, FK and the mapping between
/// canonical space and the source rig. Port of unimate_ref (validated against the upstream implementation).
/// Feature tensors use layout [t, j, c].
/// </summary>
public static class UniMateFeatures
{
	// ------------------------------------------------------------------ decode

	/// <summary>Denormalised features (T,J,12) -> local rotations and root trajectory.</summary>
	public static UniMateMotion Decode( float[,,] feat, IReadOnlyList<int> parents )
	{
		var T = feat.GetLength( 0 );
		var J = feat.GetLength( 1 );
		var local = new Quaternion[T, J];
		var root = new Vector3[T];
		var facing = new Quaternion[T];
		Span<float> six = stackalloc float[6];
		for ( var t = 0; t < T; t++ )
		{
			for ( var j = 0; j < J; j++ ) local[t, j] = Quaternion.Identity;
			for ( var j = 0; j < J; j++ )
			{
				for ( var c = 0; c < 6; c++ ) six[c] = feat[t, j, 3 + c];
				var q = M3.From6D( six ).ToQuaternion();
				if ( j == 0 ) facing[t] = q;
				else local[t, parents[j]] = q; // last child wins (upstream)
			}
		}
		// root: integrate facing-frame velocities
		var r = new Vector3[T];
		for ( var t = 1; t < T; t++ )
			r[t] = UniMateMath.Rotate( UniMateMath.Inverse( facing[t] ), new Vector3( feat[t - 1, 0, 9], 0, feat[t - 1, 0, 11] ) );
		var acc = Vector3.Zero;
		for ( var t = 0; t < T; t++ )
		{
			acc += r[t];
			root[t] = new Vector3( acc.X, feat[t, 0, 1], acc.Z );
		}
		return new UniMateMotion { Local = local, Root = root };
	}

	/// <summary>Forward kinematics in canonical space: world rotations and positions (T,J).</summary>
	public static (Quaternion[,] Rot, Vector3[,] Pos) Fk( UniMateMotion m, IReadOnlyList<Vector3> offsets, IReadOnlyList<int> parents )
	{
		var T = m.Frames; var J = parents.Count;
		var gq = new Quaternion[T, J]; var gp = new Vector3[T, J];
		for ( var t = 0; t < T; t++ )
		{
			gq[t, 0] = m.Local[t, 0]; gp[t, 0] = m.Root[t];
			for ( var j = 1; j < J; j++ )
			{
				var p = parents[j];
				gq[t, j] = gq[t, p] * m.Local[t, j];
				gp[t, j] = gp[t, p] + UniMateMath.Rotate( gq[t, p], offsets[j] );
			}
		}
		return (gq, gp);
	}

	/// <summary>Canonical motion -> source-rig world/local rotations and root positions (BFS order).</summary>
	public static SourceMotion ToSource( UniMateMotion m, UniMateSkeleton s )
	{
		var T = m.Frames; var J = s.Count;
		var mq = s.MQuat;
		var mqInv = UniMateMath.Inverse( mq );
		var world = new Quaternion[T, J];
		var local = new Quaternion[T, J];
		var root = new Vector3[T];
		var w = new Quaternion[J];
		var mt = s.M.Transposed;
		for ( var t = 0; t < T; t++ )
		{
			for ( var j = 0; j < J; j++ )
				w[j] = j == 0 ? m.Local[t, 0] : w[s.Parents[j]] * m.Local[t, j];
			for ( var j = 0; j < J; j++ )
				world[t, j] = Quaternion.Normalize( mqInv * w[j] * mq * s.RestWorldRot[j] );
			for ( var j = 0; j < J; j++ )
				local[t, j] = j == 0 ? world[t, 0] : Quaternion.Normalize( UniMateMath.Inverse( world[t, s.Parents[j]] ) * world[t, j] );
			root[t] = mt * (m.Root[t] / s.Scale + s.Origin);
		}
		return new SourceMotion { WorldRot = world, LocalRot = local, RootPos = root };
	}

	// ------------------------------------------------------------------ encode

	/// <summary>Alignment applied when encoding a user clip (frame-0 facing to +Z, root XZ to 0, ground).</summary>
	public sealed class Alignment
	{
		public Quaternion Q;
		public Vector3 Xz;
		public float Ground;
	}

	/// <summary>
	/// User motion (T+1 frames of joint world positions/rotations in source space, BFS order) to
	/// denormalised features (T,J,12) in a window whose first frame faces +Z.
	/// </summary>
	public static (float[,,] Features, Alignment Align) Encode( Vector3[,] worldPos, Quaternion[,] worldRot, UniMateSkeleton s )
	{
		var T1 = worldPos.GetLength( 0 ); var J = s.Count;
		var pos = new Vector3[T1, J];
		var W = new Quaternion[T1, J];
		var mq = s.MQuat; var mqInv = UniMateMath.Inverse( mq );
		for ( var t = 0; t < T1; t++ )
			for ( var j = 0; j < J; j++ )
			{
				pos[t, j] = (s.M * worldPos[t, j] - s.Origin) * s.Scale;
				W[t, j] = Quaternion.Normalize( mq * (worldRot[t, j] * UniMateMath.Inverse( s.RestWorldRot[j] )) * mqInv );
			}
		var f0 = FacingQuats( pos, s, 0, 1 )[0];
		var q = f0;
		for ( var t = 0; t < T1; t++ )
			for ( var j = 0; j < J; j++ )
			{
				pos[t, j] = UniMateMath.Rotate( q, pos[t, j] );
				W[t, j] = q * W[t, j];
			}
		var xz = new Vector3( pos[0, 0].X, 0, pos[0, 0].Z );
		var ground = float.MaxValue;
		for ( var t = 0; t < T1; t++ )
			for ( var j = 0; j < J; j++ )
			{
				pos[t, j] -= xz;
				ground = MathF.Min( ground, pos[t, j].Y );
			}
		for ( var t = 0; t < T1; t++ ) for ( var j = 0; j < J; j++ ) pos[t, j].Y -= ground;
		var local = new Quaternion[T1, J];
		for ( var t = 0; t < T1; t++ )
			for ( var j = 0; j < J; j++ )
				local[t, j] = j == 0 ? W[t, 0] : Quaternion.Normalize( UniMateMath.Inverse( W[t, s.Parents[j]] ) * W[t, j] );
		var facing = FacingQuats( pos, s, 0, T1 );
		return (Features( pos, local, s.Parents, facing ), new Alignment { Q = q, Xz = xz, Ground = ground });
	}

	/// <summary>Undoes <see cref="Encode"/>'s alignment on a decoded motion (root position and root rotation).</summary>
	public static void Unalign( UniMateMotion m, Alignment a )
	{
		var inv = UniMateMath.Inverse( a.Q );
		for ( var t = 0; t < m.Frames; t++ )
		{
			m.Root[t] = UniMateMath.Rotate( inv, m.Root[t] + a.Xz + new Vector3( 0, a.Ground, 0 ) );
			m.Local[t, 0] = Quaternion.Normalize( inv * m.Local[t, 0] );
		}
	}

	/// <summary>
	/// Per-frame facing (get_root_facing_quat): from the right/left face joints, from head/tail for body-axis rigs,
	/// or the identity for rigs without face joints (upstream's [-1, -1] sentinel).
	/// </summary>
	static Quaternion[] FacingQuats( Vector3[,] pos, UniMateSkeleton s, int start, int count )
	{
		var result = new Quaternion[count];
		for ( var i = 0; i < count; i++ )
			result[i] = s.RightHip < 0 || s.LeftHip < 0
				? Quaternion.Identity
				: UniMateSkeleton.FacingFrom( pos[start + i, s.RightHip], pos[start + i, s.LeftHip], s.BodyAxis );
		return result;
	}

	/// <summary>compute_unimate_motion_feats: (T+1) positions/local rotations -> (T,J,12).</summary>
	public static float[,,] Features( Vector3[,] pos, Quaternion[,] local, IReadOnlyList<int> parents, Quaternion[] facing )
	{
		var T1 = pos.GetLength( 0 ); var J = parents.Count; var T = T1 - 1;
		var feat = new float[T, J, 12];
		Span<float> six = stackalloc float[6];
		for ( var t = 0; t < T; t++ )
		{
			var rootXz = new Vector3( pos[t, 0].X, 0, pos[t, 0].Z );
			for ( var j = 0; j < J; j++ )
			{
				var ric = UniMateMath.Rotate( facing[t], pos[t, j] - rootXz );
				feat[t, j, 0] = ric.X; feat[t, j, 1] = ric.Y; feat[t, j, 2] = ric.Z;
				var rot = j == 0 ? facing[t] : local[t, parents[j]];
				M3.FromQuaternion( rot ).To6D( six );
				for ( var c = 0; c < 6; c++ ) feat[t, j, 3 + c] = six[c];
				var vel = UniMateMath.Rotate( facing[t + 1], pos[t + 1, j] - pos[t, j] );
				feat[t, j, 9] = vel.X; feat[t, j, 10] = vel.Y; feat[t, j, 11] = vel.Z;
			}
			// channels with a degenerate std must be exact (they are multiplied by 1e8 when normalised)
			feat[t, 0, 0] = 0; feat[t, 0, 2] = 0;
		}
		return feat;
	}

	// ------------------------------------------------------------------ normalisation

	/// <summary>Denormalised (T,J,12) -> normalised model layout (J,12,T).</summary>
	public static float[] ToModel( float[,,] feat, UniMateStats stats, int frames )
	{
		var T = feat.GetLength( 0 ); var J = feat.GetLength( 1 );
		var x = new float[J * 12 * frames];
		for ( var j = 0; j < J; j++ )
			for ( var c = 0; c < 12; c++ )
				for ( var t = 0; t < frames; t++ )
				{
					var src = Math.Min( t, T - 1 ); // pad by repeating the last real frame
					var value = feat[src, j, c];
					// The root channels whose std is floored at 1e-8 (root x/z, facing 6D off-axis terms) are
					// constant by construction and equal to the mean; writing it exactly avoids 1e8-scaled noise.
					if ( j == 0 && stats.StdRoot[c] <= 1e-7 ) value = (float)stats.MeanRoot[c];
					x[(j * 12 + c) * frames + t] = stats.Normalize( j, c, value );
				}
		return x;
	}

	/// <summary>Normalised model layout (J,12,T) -> denormalised (T,J,12).</summary>
	public static float[,,] FromModel( float[] x, int joints, int frames, UniMateStats stats )
	{
		var feat = new float[frames, joints, 12];
		for ( var j = 0; j < joints; j++ )
			for ( var c = 0; c < 12; c++ )
				for ( var t = 0; t < frames; t++ )
					feat[t, j, c] = (float)stats.Denormalize( j, c, x[(j * 12 + c) * frames + t] );
		return feat;
	}
}
