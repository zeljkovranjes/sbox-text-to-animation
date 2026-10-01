using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Mapping;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// The bridge between a workspace skeleton (engine space) and UniMate: which bones the model animates
/// (the mapped humanoid body, plus virtual tip joints so leaf bones such as the head, hands and toes get a
/// rotation), their training-vocabulary names, and conversion of poses both ways.
/// </summary>
public sealed class UniMateRig
{
	public MotionRig Motion { get; }
	public UniMateSkeleton Skeleton { get; }
	/// <summary>For each UniMate joint (BFS order): the workspace bone, or -1 for a virtual tip.</summary>
	public int[] Bone { get; }
	/// <summary>For each UniMate joint: tip offset from its parent joint in the parent's rest frame (tips only).</summary>
	readonly Vector3[] _tipLocal;

	public int Count => Skeleton.Count;

	static readonly (BoneRole Role, string Name)[] Roles =
	{
		(BoneRole.Hips, "Hips"), (BoneRole.Spine0, "Spine"), (BoneRole.Spine1, "Spine"), (BoneRole.Spine2, "Spine"),
		(BoneRole.Spine3, "Spine"), (BoneRole.Spine4, "Spine"), (BoneRole.Neck, "Neck"), (BoneRole.Head, "Head"),
		(BoneRole.ClavicleL, "Left Shoulder"), (BoneRole.UpperArmL, "Left Upper Arm"), (BoneRole.LowerArmL, "Left Forearm"), (BoneRole.HandL, "Left Hand"),
		(BoneRole.ClavicleR, "Right Shoulder"), (BoneRole.UpperArmR, "Right Upper Arm"), (BoneRole.LowerArmR, "Right Forearm"), (BoneRole.HandR, "Right Hand"),
		(BoneRole.UpperLegL, "Left Thigh"), (BoneRole.LowerLegL, "Left Shin"), (BoneRole.FootL, "Left Foot"), (BoneRole.ToeL, "Left Toe"),
		(BoneRole.UpperLegR, "Right Thigh"), (BoneRole.LowerLegR, "Right Shin"), (BoneRole.FootR, "Right Foot"), (BoneRole.ToeR, "Right Toe"),
	};

	UniMateRig( MotionRig motion, UniMateSkeleton skeleton, int[] bone, Vector3[] tipLocal )
	{
		Motion = motion;
		Skeleton = skeleton;
		Bone = bone;
		_tipLocal = tipLocal;
	}

	/// <summary>Problems that prevent UniMate from animating this rig (empty = fine).</summary>
	public static List<string> Validate( MotionRig rig )
	{
		var problems = new List<string>();
		if ( !rig.IsHumanoid ) problems.Add( "UniMate needs a humanoid skeleton (hips, spine, legs, arms and head)." );
		return problems;
	}

	public static UniMateRig Build( MotionRig rig )
	{
		var problems = Validate( rig );
		if ( problems.Count > 0 ) throw new InvalidOperationException( string.Join( " ", problems ) );
		var s = rig.Skeleton;
		var rest = s.RestWorld;

		// mapped body bones
		var bones = new List<int>();
		var names = new List<string>();
		foreach ( var (role, name) in Roles )
		{
			if ( rig.Bone( role ) is not int b || bones.Contains( b ) ) continue;
			bones.Add( b ); names.Add( name );
		}
		// parents: nearest selected ancestor
		var parents = bones.Select( b =>
		{
			for ( var p = s[b].ParentIndex; p >= 0; p = s[p].ParentIndex )
			{
				var i = bones.IndexOf( p );
				if ( i >= 0 ) return i;
			}
			return -1;
		} ).ToList();
		var hips = bones.IndexOf( rig.HipsIndex );
		for ( var i = 0; i < parents.Count; i++ ) if ( parents[i] < 0 && i != hips ) parents[i] = hips; // stray roots hang off the hips
		parents[hips] = -1;

		var pos = bones.Select( b => rest[b].Pos ).ToList();
		var rot = bones.Select( b => rest[b].Rot ).ToList();
		var boneOf = new List<int>( bones );
		var tipLocal = new List<Vector3>( bones.Select( _ => Vector3.Zero ) );

		// virtual tips at the leaf bones' ends
		void Tip( BoneRole leaf, BoneRole? towards, string name, float lengthScale )
		{
			if ( rig.Bone( leaf ) is not int b ) return;
			var j = bones.IndexOf( b );
			if ( j < 0 ) return;
			var p = rest[b].Pos;
			Vector3 end;
			var child = FarthestDescendant( rig, b );
			if ( child is int c && (rest[c].Pos - p).Length() > rig.Cm( 3f ) ) end = rest[c].Pos;
			else
			{
				var parent = s[b].ParentIndex;
				var dir = parent >= 0 ? rest[b].Pos - rest[parent].Pos : rig.Up;
				if ( dir.LengthSquared() < 1e-8f ) dir = rig.Up;
				end = p + Vector3.Normalize( dir ) * MathF.Max( dir.Length() * lengthScale, rig.Cm( 6f ) );
			}
			pos.Add( end ); rot.Add( rest[b].Rot ); names.Add( name ); parents.Add( j ); boneOf.Add( -1 );
			tipLocal.Add( Vector3.Transform( end - p, Quaternion.Conjugate( rest[b].Rot ) ) );
		}
		Tip( BoneRole.Head, null, "Head End", 0.9f );
		Tip( BoneRole.HandL, null, "Left Hand End", 0.6f );
		Tip( BoneRole.HandR, null, "Right Hand End", 0.6f );
		var leftToe = rig.Bone( BoneRole.ToeL ) is not null;
		Tip( leftToe ? BoneRole.ToeL : BoneRole.FootL, null, leftToe ? "Left Toe End" : "Left Toe", 0.6f );
		var rightToe = rig.Bone( BoneRole.ToeR ) is not null;
		Tip( rightToe ? BoneRole.ToeR : BoneRole.FootR, null, rightToe ? "Right Toe End" : "Right Toe", 0.6f );

		var rh = bones.IndexOf( rig.Bone( BoneRole.UpperLegR ) ?? -1 );
		var lh = bones.IndexOf( rig.Bone( BoneRole.UpperLegL ) ?? -1 );
		var skeleton = UniMateSkeleton.Build( names, parents, pos, rot, rh, lh, rig.Forward, UniMateSkeleton.EngineUpBasis );
		var bfsBone = skeleton.SourceIndex.Select( i => boneOf[i] ).ToArray();
		var bfsTip = skeleton.SourceIndex.Select( i => tipLocal[i] ).ToArray();
		return new UniMateRig( rig, skeleton, bfsBone, bfsTip );
	}

	static int? FarthestDescendant( MotionRig rig, int bone )
	{
		int? best = null; var bestDist = 0f;
		foreach ( var d in rig.Descendants( bone ) )
		{
			if ( d == bone || !rig.IsMotionBone( d ) ) continue;
			var dist = (rig.Skeleton.RestWorld[d].Pos - rig.Skeleton.RestWorld[bone].Pos).Length();
			if ( dist > bestDist ) { bestDist = dist; best = d; }
		}
		return best;
	}

	/// <summary>World transforms of the UniMate joints for every frame (BFS order), from workspace frames.</summary>
	public (Vector3[,] Pos, Quaternion[,] Rot) JointWorld( IReadOnlyList<XForm[]> frames )
	{
		var n = frames.Count; var J = Count;
		var pos = new Vector3[n, J]; var rot = new Quaternion[n, J];
		var world = new XForm[Motion.Skeleton.Count];
		for ( var f = 0; f < n; f++ )
		{
			FkUtil.ToWorld( frames[f], Motion.Skeleton, world );
			for ( var j = 0; j < J; j++ )
			{
				if ( Bone[j] >= 0 ) { pos[f, j] = world[Bone[j]].Pos; rot[f, j] = world[Bone[j]].Rot; }
				else
				{
					var parentBone = Bone[Skeleton.Parents[j]];
					var pw = world[parentBone];
					pos[f, j] = pw.Pos + Vector3.Transform( _tipLocal[j], pw.Rot );
					rot[f, j] = pw.Rot;
				}
			}
		}
		return (pos, rot);
	}

	/// <summary>
	/// Writes a source-space motion (UniMate joints, BFS order) into workspace frames: animated bones get their
	/// world rotation, the hips their world position; other bones keep the locals of <paramref name="baseFrames"/>
	/// (or the rest pose).
	/// </summary>
	public List<XForm[]> ToFrames( SourceMotion motion, IReadOnlyList<XForm[]> baseFrames = null )
	{
		var s = Motion.Skeleton;
		var T = motion.RootPos.Length;
		var desiredWorld = new Quaternion?[s.Count];
		var hipsJoint = Array.IndexOf( Bone, Motion.HipsIndex );
		var result = new List<XForm[]>( T );
		var world = new XForm[s.Count];
		for ( var t = 0; t < T; t++ )
		{
			Array.Clear( desiredWorld );
			for ( var j = 0; j < Count; j++ ) if ( Bone[j] >= 0 ) desiredWorld[Bone[j]] = motion.WorldRot[t, j];
			var src = baseFrames is { Count: > 0 } ? baseFrames[Math.Min( t, baseFrames.Count - 1 )] : null;
			var frame = new XForm[s.Count];
			for ( var b = 0; b < s.Count; b++ )
			{
				var local = src?[b] ?? s[b].RestLocal;
				var parent = s[b].ParentIndex;
				var parentWorld = parent < 0 ? XForm.Identity : world[parent];
				if ( desiredWorld[b] is { } g )
				{
					var pos = local.Pos;
					if ( b == Motion.HipsIndex )
					{
						var wantPos = motion.RootPos[t];
						pos = parent < 0 ? wantPos : Vector3.Transform( wantPos - parentWorld.Pos, Quaternion.Conjugate( parentWorld.Rot ) );
					}
					var rotLocal = parent < 0 ? g : Quaternion.Normalize( Quaternion.Conjugate( parentWorld.Rot ) * g );
					local = new XForm( pos, rotLocal );
				}
				frame[b] = local;
				world[b] = parent < 0 ? local : XForm.Compose( parentWorld, local );
			}
			result.Add( frame );
		}
		return result;
	}

	/// <summary>UniMate joints (BFS indices) that carry the motion of the given workspace bones (for "keep joints").</summary>
	public HashSet<int> JointsForBones( IEnumerable<int> bones )
	{
		var set = new HashSet<int>();
		var boneSet = bones.ToHashSet();
		for ( var j = 0; j < Count; j++ )
		{
			// slot j stores its PARENT's rotation and its own position: keeping bone b's motion needs the slots
			// of b's children (rotation) and b's own slot (position)
			var b = Bone[j];
			if ( b >= 0 && boneSet.Contains( b ) ) set.Add( j );
			var p = Skeleton.Parents[j];
			if ( p >= 0 && Bone[p] >= 0 && boneSet.Contains( Bone[p] ) ) set.Add( j );
		}
		// the root slot carries the trajectory: keep it when the hips are kept
		if ( boneSet.Contains( Motion.HipsIndex ) ) set.Add( 0 );
		return set;
	}
}
