using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Generation;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using TextToAnimation.Rig;

namespace TextToAnimation.Editor.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>
/// The bridge between a workspace skeleton (engine space) and UniMate: which bones the model animates, their
/// clean names, the facing joints, and conversion of poses both ways. Every rig goes through upstream UniMate's
/// own skeleton preparation (<see cref="UniMatePrep"/>: skin-based pruning, rule-based names and facing, then the
/// canonical T-pose of <see cref="UniMateSkeleton"/>), exactly as upstream prepares the rigs it was trained on;
/// nothing depends on what kind of creature it is.
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

	/// <summary>The checkpoint's training limit (max_joints in the released uniml3d_f60_v2 config.json); bigger rigs are trimmed to it.</summary>
	public const int MaxJoints = 71;

	/// <summary>The checkpoint's training minimum (uniml3d config min_joints).</summary>
	public const int MinJoints = 5;

	/// <summary>Which normalisation statistics (training data family) the rig uses.</summary>
	public RigFamily Family { get; }

	/// <summary>The workspace bone carrying the root trajectory (UniMate joint 0, the hips).</summary>
	public int RootBone => Bone[0];

	/// <summary>The workspace bones UniMate animates (every model joint that is a real bone).</summary>
	public IReadOnlySet<int> AnimatedBones => _animated ??= Bone.Where( b => b >= 0 ).ToHashSet();
	HashSet<int> _animated;

	UniMateRig( MotionRig motion, UniMateSkeleton skeleton, int[] bone, Vector3[] tipLocal, RigFamily family )
	{
		Motion = motion;
		Skeleton = skeleton;
		Bone = bone;
		_tipLocal = tipLocal;
		Family = family;
	}

	/// <summary>Problems that prevent UniMate from animating this rig (empty = fine).</summary>
	public static List<string> Validate( MotionRig rig )
	{
		var problems = new List<string>();
		try { Build( rig ); }
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException ) { problems.Add( e.Message ); }
		return problems;
	}

	/// <summary>The training family a rig's motion resembles: humans (Mixamo), animals and creatures (Truebones), anything else (Objaverse).</summary>
	public static RigFamily DetectFamily( MotionRig rig )
	{
		var a = rig.Analysis;
		if ( a.IsHumanoid ) return RigFamily.Humanoid;
		if ( a.Limbs.Any( l => l.Kind is LimbKind.Leg or LimbKind.FrontLeg or LimbKind.Wing )
			|| a.Tails.Any( t => t.Count > 0 && a.Part[t[0]] == RigPart.Tail )
			|| a.Facing == RigFacing.BodyAxis )
			return RigFamily.Animal;
		return RigFamily.Object;
	}

	/// <summary>
	/// Prepares a rig like upstream prepares its training rigs: pruning helpers by skin weight, clean names and
	/// facing joints by upstream's rules, canonical T-pose and topology (see <see cref="UniMatePrep"/>).
	/// </summary>
	public static UniMateRig Build( MotionRig rig, RigFamily family = RigFamily.Auto )
	{
		var s = rig.Skeleton;
		var rest = s.RestWorld;
		var weights = UniMateSkin.WeightsOf( s );
		(double Max, double Sum) W( int b ) => weights is not null && weights.TryGetValue( s[b].Name, out var w ) ? w : (0, 0);
		var prep = UniMatePrep.Prepare( new UniMatePrep.Input
		{
			ObjectType = UniMateSkin.ObjectTypeOf( s ),
			Names = Enumerable.Range( 0, s.Count ).Select( b => s[b].Name ).ToList(),
			Parents = Enumerable.Range( 0, s.Count ).Select( b => s[b].ParentIndex ).ToList(),
			RestWorldPos = Enumerable.Range( 0, s.Count ).Select( b => rest[b].Pos ).ToList(),
			SkinMax = weights is null ? null : Enumerable.Range( 0, s.Count ).Select( b => W( b ).Max ).ToList(),
			SkinSum = weights is null ? null : Enumerable.Range( 0, s.Count ).Select( b => W( b ).Sum ).ToList(),
		}, MaxJoints );
		if ( prep.Kept.Length < MinJoints )
			throw new InvalidOperationException( $"Only {prep.Kept.Length} bones of this skeleton deform the mesh; UniMate was trained on skeletons with at least {MinJoints}." );
		UniMateSkeleton skeleton;
		try
		{
			skeleton = UniMateSkeleton.Build( prep.CleanNames, prep.Parents, prep.Kept.Select( b => rest[b].Pos ).ToList(),
				prep.Kept.Select( b => rest[b].Rot ).ToList(), prep.FaceRight, prep.FaceLeft, null,
				UniMateSkeleton.EngineCanonicalBasis, bodyAxis: prep.BodyAxis );
		}
		catch ( ArgumentException e ) { throw new InvalidOperationException( e.Message, e ); }
		if ( family == RigFamily.Auto ) family = DetectFamily( rig );
		var bone = skeleton.SourceIndex.Select( i => prep.Kept[i] ).ToArray();
		return new UniMateRig( rig, skeleton, bone, new Vector3[bone.Length], family ) { Prep = prep };
	}

	/// <summary>How upstream's preparation treated this rig (kept bones, names, facing, trimmed leaves).</summary>
	public UniMatePrep.Result Prep { get; private init; }

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
	/// Writes a source-space motion (UniMate joints, BFS order) into workspace frames: every engine bone gets a valid
	/// local transform. Animated bones get their world rotation, the root joint (hips) its world position; bones
	/// UniMate doesn't animate (fingers left out, helpers, IK targets, bones above the hips) keep the locals of
	/// <paramref name="baseFrames"/> (or the rest pose) under their new parents.
	/// </summary>
	public List<XForm[]> ToFrames( SourceMotion motion, IReadOnlyList<XForm[]> baseFrames = null )
	{
		var s = Motion.Skeleton;
		var T = motion.RootPos.Length;
		var desiredWorld = new Quaternion?[s.Count];
		var rootBone = RootBone;
		var result = new List<XForm[]>( T );
		var world = new XForm[s.Count];
		// the bones above the root joint (a rig's top "root" bone) travel with the body as one rigid block: the top
		// bone takes the root joint's translation and heading (keeping its rest tilt), the ones below it keep their
		// rest offsets - frozen at rest, anything hanging off them would be left behind
		var ancestors = new HashSet<int>();
		for ( var a = rootBone >= 0 ? s[rootBone].ParentIndex : -1; a >= 0; a = s[a].ParentIndex ) ancestors.Add( a );
		var restWorld = s.RestWorld;
		var up = Motion.Up;
		for ( var t = 0; t < T; t++ )
		{
			Array.Clear( desiredWorld );
			for ( var j = 0; j < Count; j++ ) if ( Bone[j] >= 0 ) desiredWorld[Bone[j]] = motion.WorldRot[t, j];
			var src = baseFrames is { Count: > 0 } ? baseFrames[Math.Min( t, baseFrames.Count - 1 )] : null;
			var frame = new XForm[s.Count];
			var carry = Quaternion.Identity;
			if ( ancestors.Count > 0 && desiredWorld[rootBone] is { } rootRot )
			{
				MathQ.SwingTwist( Quaternion.Normalize( rootRot * Quaternion.Conjugate( restWorld[rootBone].Rot ) ), up, out _, out carry );
			}
			for ( var b = 0; b < s.Count; b++ )
			{
				var local = src?[b] ?? s[b].RestLocal;
				var parent = s[b].ParentIndex;
				var parentWorld = parent < 0 ? XForm.Identity : world[parent];
				if ( ancestors.Contains( b ) && parent < 0 )
				{
					var wantRot = Quaternion.Normalize( carry * restWorld[b].Rot );
					var wantPos = motion.RootPos[t] + Vector3.Transform( restWorld[b].Pos - restWorld[rootBone].Pos, carry );
					var w = new XForm( wantPos, wantRot );
					local = parent < 0 ? w : XForm.ToLocal( parentWorld, w );
				}
				else if ( desiredWorld[b] is { } g )
				{
					var pos = local.Pos;
					if ( b == rootBone )
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
		if ( boneSet.Contains( RootBone ) ) set.Add( 0 );
		return set;
	}
}
