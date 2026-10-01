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
/// training-vocabulary names, the virtual tip joints that give leaf bones a rotation, and conversion of poses
/// both ways. Joints are chosen from the skeleton's shape (<see cref="RigAnalysis"/>), so any armature works
/// whatever its bones are called: humans, birds, dinosaurs, quadrupeds, snakes.
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

	/// <summary>UniMate supports at most this many joints (virtual tips included).</summary>
	public const int MaxJoints = 70;

	/// <summary>Which normalisation statistics (training data family) the rig uses.</summary>
	public RigFamily Family { get; }

	/// <summary>The workspace bone carrying the root trajectory (UniMate joint 0, the hips).</summary>
	public int RootBone => Bone[0];

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
	/// Chooses the joints UniMate animates, from the rig's shape (bone names don't matter): for a humanoid the
	/// core body (hips, spine, neck, head, arms to the hands, legs to the toes) like UniMate's Mixamo training
	/// rigs; for any other armature every body bone (helpers, IK targets and twist bones left out), trimmed to
	/// <see cref="MaxJoints"/>. Leaf bones get a virtual tip joint so they have a rotation.
	/// </summary>
	public static UniMateRig Build( MotionRig rig, RigFamily family = RigFamily.Auto )
	{
		var a = rig.Analysis;
		if ( family == RigFamily.Auto ) family = DetectFamily( rig );
		if ( !a.InBody[a.BodyRoot] ) throw new InvalidOperationException( "UniMate couldn't find the body of this skeleton (it has no connected bones)." );
		return a.IsHumanoid ? BuildHumanoid( rig, family ) : BuildGeneric( rig, family );
	}

	static UniMateRig BuildHumanoid( MotionRig rig, RigFamily family )
	{
		var a = rig.Analysis;
		var s = rig.Skeleton;
		var rest = s.RestWorld;
		var bones = new List<int> { a.BodyRoot };
		foreach ( var b in a.SpineChain )
		{
			bones.Add( b );
			if ( b == a.Head ) break;
		}
		var arms = a.Limbs.Where( l => l.Kind == LimbKind.Arm ).OrderBy( l => l.Side == BoneSide.Left ? 0 : 1 ).ToList();
		var legs = a.Limbs.Where( l => l.Kind == LimbKind.Leg ).OrderBy( l => l.Side == BoneSide.Left ? 0 : 1 ).ToList();
		foreach ( var limb in arms.Concat( legs ) ) bones.AddRange( limb.Chain );
		var j = new JointList( rig, bones );

		j.AddTip( a.Head, "Head End", 0.9f );
		foreach ( var arm in arms ) j.AddTip( arm.Chain[^1], a.Label[arm.Chain[^1]] + " End", 0.6f );
		foreach ( var leg in legs )
		{
			var side = leg.Side == BoneSide.Left ? "Left" : "Right";
			j.AddTip( leg.Chain[^1], leg.Chain.Count >= 4 ? side + " Toe End" : side + " Toe", 0.6f );
		}
		var rh = bones.IndexOf( legs.First( l => l.Side == BoneSide.Right ).Chain[0] );
		var lh = bones.IndexOf( legs.First( l => l.Side == BoneSide.Left ).Chain[0] );
		var skeleton = UniMateSkeleton.Build( j.Names, j.Parents, j.Pos, j.Rot, rh, lh, rig.Forward, UniMateSkeleton.EngineUpBasis );
		return Finish( rig, skeleton, j, family );
	}

	static UniMateRig BuildGeneric( MotionRig rig, RigFamily family )
	{
		var a = rig.Analysis;
		var s = rig.Skeleton;
		var rest = s.RestWorld;
		var selected = new HashSet<int>( a.BodySubtree( a.BodyRoot ) );
		var keep = new HashSet<int> { a.BodyRoot };
		if ( a.Head >= 0 ) keep.Add( a.Head );
		if ( a.FacingRight >= 0 ) { keep.Add( a.FacingRight ); keep.Add( a.FacingLeft ); }
		foreach ( var limb in a.Limbs ) keep.Add( limb.Chain[0] );

		int Count() => selected.Count + selected.Count( b => !Below( s, selected, b ).Any() );
		// over budget: drop fingers and toes, then head details, then thin long single chains (tails, necks)
		if ( Count() > MaxJoints ) selected.RemoveWhere( b => a.Part[b] == RigPart.Digit && !keep.Contains( b ) );
		if ( Count() > MaxJoints ) selected.RemoveWhere( b => a.Part[b] == RigPart.HeadPart && !keep.Contains( b ) );
		while ( Count() > MaxJoints )
		{
			var candidate = selected
				.Where( b => !keep.Contains( b ) && Below( s, selected, b ).Count() == 1 )
				.Where( b => s[b].ParentIndex >= 0 && selected.Contains( s[b].ParentIndex ) && Below( s, selected, s[b].ParentIndex ).Count() == 1 )
				.OrderBy( b => (rest[b].Pos - rest[s[b].ParentIndex].Pos).Length() ).ThenBy( b => b )
				.FirstOrDefault( -1 );
			if ( candidate < 0 ) break;
			selected.Remove( candidate );
		}
		var total = Count();
		if ( total > MaxJoints )
			throw new InvalidOperationException( $"This skeleton has too many bones for UniMate ({total} joints after simplifying, at most {MaxJoints})." );

		// breadth-first from the hips, children in skeleton order
		var bones = new List<int>();
		var queue = new Queue<int>();
		queue.Enqueue( a.BodyRoot );
		while ( queue.Count > 0 )
		{
			var b = queue.Dequeue();
			bones.Add( b );
			foreach ( var c in Below( s, selected, b ) ) queue.Enqueue( c );
		}
		if ( bones.Count < 3 ) throw new InvalidOperationException( $"UniMate needs a skeleton with at least 3 connected bones; this one has {bones.Count}." );
		var j = new JointList( rig, bones );
		foreach ( var b in bones )
		{
			if ( Below( s, selected, b ).Any() ) continue;
			var label = a.Label[b];
			j.AddTip( b, label.EndsWith( " End", StringComparison.Ordinal ) ? label : label + " End", b == a.Head ? 0.9f : 0.6f );
		}

		var rh = a.FacingRight >= 0 ? bones.IndexOf( a.FacingRight ) : -1;
		var lh = a.FacingLeft >= 0 ? bones.IndexOf( a.FacingLeft ) : -1;
		if ( rh < 0 || lh < 0 ) rh = lh = -1;
		var skeleton = UniMateSkeleton.Build( j.Names, j.Parents, j.Pos, j.Rot, rh, lh, rig.Forward, UniMateSkeleton.EngineUpBasis,
			bodyAxis: a.Facing == RigFacing.BodyAxis && rh >= 0 );
		return Finish( rig, skeleton, j, family );
	}

	static UniMateRig Finish( MotionRig rig, UniMateSkeleton skeleton, JointList j, RigFamily family )
	{
		var bfsBone = skeleton.SourceIndex.Select( i => j.BoneOf[i] ).ToArray();
		var bfsTip = skeleton.SourceIndex.Select( i => j.TipLocal[i] ).ToArray();
		return new UniMateRig( rig, skeleton, bfsBone, bfsTip, family );
	}

	/// <summary>The nearest selected descendants of <paramref name="bone"/> (skipping unselected bones in between).</summary>
	static IEnumerable<int> Below( Skeleton s, HashSet<int> selected, int bone )
	{
		for ( var i = 0; i < s.Count; i++ )
		{
			if ( i == bone || !selected.Contains( i ) ) continue;
			var p = s[i].ParentIndex;
			while ( p >= 0 && !selected.Contains( p ) ) p = s[p].ParentIndex;
			if ( p == bone ) yield return i;
		}
	}

	/// <summary>The joints being assembled: bones first (parents = nearest listed ancestor), then virtual tips.</summary>
	sealed class JointList
	{
		readonly MotionRig _rig;
		readonly List<int> _bones;
		public readonly List<string> Names;
		public readonly List<int> Parents;
		public readonly List<Vector3> Pos;
		public readonly List<Quaternion> Rot;
		public readonly List<int> BoneOf;
		public readonly List<Vector3> TipLocal;

		public JointList( MotionRig rig, List<int> bones )
		{
			_rig = rig;
			_bones = bones;
			var s = rig.Skeleton;
			var rest = s.RestWorld;
			Names = bones.Select( b => rig.Analysis.Label[b] ).ToList();
			Parents = bones.Select( b =>
			{
				for ( var p = s[b].ParentIndex; p >= 0; p = s[p].ParentIndex )
				{
					var i = bones.IndexOf( p );
					if ( i >= 0 ) return i;
				}
				return -1;
			} ).ToList();
			for ( var i = 1; i < Parents.Count; i++ ) if ( Parents[i] < 0 ) Parents[i] = 0; // stray roots hang off the hips
			Parents[0] = -1;
			Pos = bones.Select( b => rest[b].Pos ).ToList();
			Rot = bones.Select( b => rest[b].Rot ).ToList();
			BoneOf = new List<int>( bones );
			TipLocal = new List<Vector3>( bones.Select( _ => Vector3.Zero ) );
		}

		/// <summary>A virtual tip joint at the end of leaf bone <paramref name="b"/>: its farthest body descendant, or the bone extended.</summary>
		public void AddTip( int b, string name, float lengthScale )
		{
			var j = _bones.IndexOf( b );
			if ( j < 0 ) return;
			var s = _rig.Skeleton;
			var rest = s.RestWorld;
			var p = rest[b].Pos;
			Vector3 end;
			var child = _rig.Analysis.FarthestBodyDescendant( b );
			if ( child >= 0 && (rest[child].Pos - p).Length() > _rig.Cm( 3f ) ) end = rest[child].Pos;
			else
			{
				var parent = s[b].ParentIndex;
				var dir = parent >= 0 ? rest[b].Pos - rest[parent].Pos : _rig.Up;
				if ( dir.LengthSquared() < 1e-8f ) dir = _rig.Up;
				end = p + Vector3.Normalize( dir ) * MathF.Max( dir.Length() * lengthScale, _rig.Cm( 6f ) );
			}
			Pos.Add( end ); Rot.Add( rest[b].Rot ); Names.Add( name ); Parents.Add( j ); BoneOf.Add( -1 );
			TipLocal.Add( Vector3.Transform( end - p, Quaternion.Conjugate( rest[b].Rot ) ) );
		}
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
