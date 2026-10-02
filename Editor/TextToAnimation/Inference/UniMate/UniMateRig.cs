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

	/// <summary>
	/// The largest skeleton in the checkpoint's training data (max_joints in the released config). Only a padding
	/// size upstream: the network has no per-joint weights (spectral RoPE, shared output heads), so bigger rigs run
	/// whole rather than losing bones.
	/// </summary>
	public const int TrainedMaxJoints = 71;

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

	/// <summary>
	/// The statistics a rig is normalised with. Upstream normalises every skeleton with the statistics of the dataset
	/// it came from - people (Mixamo), animals and creatures (Truebones), other objects (Objaverse) - and the
	/// network's output is only right in that space (a person under the Objaverse statistics never touches the
	/// ground). A new rig gets the dataset its body resembles.
	/// </summary>
	/// <summary>Words upstream's name rule gives procedural helper bones (twist / helper / IK targets / cloth).</summary>
	static readonly HashSet<string> HelperWords = new( StringComparer.Ordinal ) { "Twist", "Helper", "Ikrule", "Target", "Clothing", "IK" };

	/// <summary>The joints of UniMate's people (Mixamo, clean names; "Spine" three times).</summary>
	static readonly HashSet<string> MixamoBody = new( StringComparer.Ordinal )
	{
		"Hips", "Spine", "Neck", "Head", "Left Shoulder", "Right Shoulder", "Left Upper Arm", "Right Upper Arm", "Left Forearm", "Right Forearm",
		"Left Hand", "Right Hand", "Left Thigh", "Right Thigh", "Left Shin", "Right Shin", "Left Foot", "Right Foot", "Left Toe", "Right Toe",
	};
	/// <summary>Joints a person must have to be shown to UniMate as a Mixamo body.</summary>
	static readonly HashSet<string> MixamoCore = new( StringComparer.Ordinal )
	{
		"Hips", "Spine", "Head", "Left Upper Arm", "Right Upper Arm", "Left Forearm", "Right Forearm", "Left Hand", "Right Hand",
		"Left Thigh", "Right Thigh", "Left Shin", "Right Shin", "Left Foot", "Right Foot",
	};

	/// <summary>Mixamo's word for a joint where people rigs differ ("Left Ankle" is Mixamo's "Left Foot").</summary>
	static string PeopleName( string clean ) => clean.Replace( "Ankle", "Foot" );

	/// <summary>The Mixamo body's joint name for a humanoid role (fingers and twists have none: they follow).</summary>
	static string MixamoNameOf( Mapping.BoneRole role ) => role switch
	{
		Mapping.BoneRole.Hips => "Hips",
		Mapping.BoneRole.Spine0 or Mapping.BoneRole.Spine1 or Mapping.BoneRole.Spine2 or Mapping.BoneRole.Spine3 or Mapping.BoneRole.Spine4 => "Spine",
		Mapping.BoneRole.Neck => "Neck",
		Mapping.BoneRole.Head => "Head",
		Mapping.BoneRole.ClavicleL => "Left Shoulder", Mapping.BoneRole.ClavicleR => "Right Shoulder",
		Mapping.BoneRole.UpperArmL => "Left Upper Arm", Mapping.BoneRole.UpperArmR => "Right Upper Arm",
		Mapping.BoneRole.LowerArmL => "Left Forearm", Mapping.BoneRole.LowerArmR => "Right Forearm",
		Mapping.BoneRole.HandL => "Left Hand", Mapping.BoneRole.HandR => "Right Hand",
		Mapping.BoneRole.UpperLegL => "Left Thigh", Mapping.BoneRole.UpperLegR => "Right Thigh",
		Mapping.BoneRole.LowerLegL => "Left Shin", Mapping.BoneRole.LowerLegR => "Right Shin",
		Mapping.BoneRole.FootL => "Left Foot", Mapping.BoneRole.FootR => "Right Foot",
		Mapping.BoneRole.ToeL => "Left Toe", Mapping.BoneRole.ToeR => "Right Toe",
		_ => "",
	};

	/// <summary>Words upstream's name rule gives finger bones.</summary>
	static readonly HashSet<string> FingerWords = new( StringComparer.Ordinal ) { "Finger", "Thumb" };

	static readonly string[] CreatureWords = { "thigh", "shin", "calf", "leg", "foot", "toe", "paw", "hoof", "wing", "tail" };

	public static RigFamily DetectFamily( MotionRig rig )
	{
		var a = rig.Analysis;
		if ( a.IsHumanoid ) return RigFamily.Humanoid;
		if ( a.Limbs.Any( l => l.Kind is LimbKind.Leg or LimbKind.FrontLeg or LimbKind.Wing )
			|| a.Tails.Any( t => t.Count > 0 && a.Part[t[0]] == RigPart.Tail )
			|| a.Facing == RigFacing.BodyAxis )
			return RigFamily.Animal;
		// a rest pose the shape analysis can't read (no mirror plane: a lopsided bind pose) - fall back on upstream's own
		// joint names: legs, wings or a tail make a creature
		if ( a.Limbs.Count == 0 )
		{
			var objectType = UniMateSkin.ObjectTypeOf( rig.Skeleton );
			var words = Enumerable.Range( 0, rig.Skeleton.Count ).Select( b => UniMateNames.Clean( rig.Skeleton[b].Name, objectType ).ToLowerInvariant() );
			if ( words.Any( w => CreatureWords.Any( w.Contains ) ) ) return RigFamily.Animal;
		}
		return RigFamily.Object;
	}

	/// <summary>
	/// Prepares a rig like upstream prepares its training rigs: pruning helpers by skin weight, clean names and
	/// facing joints by upstream's rules, canonical T-pose and topology (see <see cref="UniMatePrep"/>).
	/// </summary>
	/// <param name="alignVocabulary">
	/// Rewrite joint names upstream's rule leaves outside UniMate's training vocabulary into it (and pick the facing
	/// pair on the aligned names) - see <see cref="UniMateVocabulary"/>. Off reproduces upstream exactly (tests).
	/// </param>
	/// <param name="skipHelpers">
	/// Leave procedural helper bones out of what UniMate animates (they keep following their limbs): bones the
	/// model's constraints drive, and bones upstream's name rule calls twist / helper / IK / clothing bones (and
	/// fingers) under the people statistics, whose Mixamo body has none; for animals and objects only such names
	/// outside UniMate's training vocabulary. On the s&amp;box human they left the shins kicking up (lowest shin angle
	/// 3 deg below horizontal against UniMate's 36). Off reproduces upstream's preparation exactly (tests).
	/// </param>
	/// <param name="peopleBody">
	/// Under the people statistics, show UniMate a person as the Mixamo bodies it learned people from: exactly their
	/// joints (hips, three spine joints, neck, head, shoulders, arms, hands, legs, feet, toes - Mixamo's names, so an
	/// "ankle" is a "foot"), arms in a T-pose. Everything else follows the animated bones as the engine poses it; the
	/// T-pose only changes the arm bones' rest rotations UniMate is given (every bone keeps its offsets, so the
	/// motion lands exactly on the real bones; the model is untouched). Measured on the s&amp;box human over three
	/// seeds against UniMate's own Mixamo body: walking arms 64 deg below horizontal (UniMate 65, before 71), punch
	/// arm extension 0.82 (UniMate 0.74, before 0.96), hands 0.66 of height (0.70, before 0.62), hip drift 0.20
	/// (0.14, before 0.27). A rig without those joints keeps the rule above. Off reproduces upstream (tests).
	/// </param>
	public static UniMateRig Build( MotionRig rig, RigFamily family = RigFamily.Auto, bool alignVocabulary = true, bool skipHelpers = true, bool peopleBody = true )
	{
		var s = rig.Skeleton;
		var weights = UniMateSkin.WeightsOf( s );
		var objectType = UniMateSkin.ObjectTypeOf( s );
		var driven = UniMateSkin.DrivenOf( s );
		// a person rig of a known convention (humanoid-retargeter's profiles: Mixamo, UE mannequin, Rigify, DAZ, VRM,
		// Biped, SMPL, ...): every body bone's role, so its Mixamo joint name comes from what it is, not how it's spelled
		// Only for a rig that is a person by its shape: animals are rigged with person conventions too (Truebones' dog,
		// bear, ... use 3ds Max Biped names), so a profile names a person's bones but never decides that it is one
		if ( family == RigFamily.Auto ) family = DetectFamily( rig );
		var profile = peopleBody && family == RigFamily.Humanoid ? Mapping.ProfileDetector.Detect( s ) : null;
		var roleOf = profile?.Result.RoleToBone.ToDictionary( kv => kv.Value, kv => kv.Key ) ?? new Dictionary<int, Mapping.BoneRole>();
		string PeopleNameOf( int b ) => profile is not null
			? (roleOf.TryGetValue( b, out var role ) ? MixamoNameOf( role ) : "")
			: PeopleName( UniMateVocabulary.Align( UniMateNames.Clean( s[b].Name, objectType ) ) );
		var mixamoBody = peopleBody && family == RigFamily.Humanoid
			&& MixamoCore.IsSubsetOf( Enumerable.Range( 0, s.Count ).Where( b => !driven.Contains( s[b].Name ) ).Select( PeopleNameOf ) );
		var posed = mixamoBody ? ArmsInTPose( rig ) : null;
		IReadOnlyList<XForm> rest = posed ?? s.RestWorld;
		// under the people statistics (Mixamo: 22 joints, no twists, no fingers) helper and finger chains are foreign;
		// Truebones and Objaverse did train on twist, IK-chain and finger joints, so for animals and objects only
		// helper names outside the training vocabulary are left out
		var people = family == RigFamily.Humanoid;
		bool Helper( int b )
		{
			if ( mixamoBody ) return driven.Contains( s[b].Name ) || !MixamoBody.Contains( PeopleNameOf( b ) );
			if ( !skipHelpers ) return false;
			if ( driven.Contains( s[b].Name ) ) return true;
			var clean = UniMateNames.Clean( s[b].Name, objectType );
			var words = clean.Split( ' ' );
			if ( people ) return words.Any( HelperWords.Contains ) || words.Any( FingerWords.Contains );
			return words.Any( HelperWords.Contains ) && !UniMateVocabulary.Names.Contains( clean );
		}
		(double Max, double Sum) W( int b ) => Helper( b ) ? (0, 0)
			: weights is null ? (1, 1) : weights.TryGetValue( s[b].Name, out var w ) ? w : (0, 0);
		var useSkin = weights is not null || Enumerable.Range( 0, s.Count ).Any( Helper );
		var prep = UniMatePrep.Prepare( new UniMatePrep.Input
		{
			ObjectType = objectType,
			Names = Enumerable.Range( 0, s.Count ).Select( b => s[b].Name ).ToList(),
			Parents = Enumerable.Range( 0, s.Count ).Select( b => s[b].ParentIndex ).ToList(),
			RestWorldPos = Enumerable.Range( 0, s.Count ).Select( b => rest[b].Pos ).ToList(),
			SkinMax = !useSkin ? null : Enumerable.Range( 0, s.Count ).Select( b => W( b ).Max ).ToList(),
			SkinSum = !useSkin ? null : Enumerable.Range( 0, s.Count ).Select( b => W( b ).Sum ).ToList(),
		} );
		if ( prep.Kept.Length < MinJoints )
			throw new InvalidOperationException( $"Only {prep.Kept.Length} bones of this skeleton deform the mesh; UniMate was trained on skeletons with at least {MinJoints}." );
		UniMateSkeleton skeleton;
		try
		{
			var names = prep.CleanNames;
			int faceRight = prep.FaceRight, faceLeft = prep.FaceLeft;
			var bodyAxis = prep.BodyAxis;
			if ( alignVocabulary && !mixamoBody )
			{
				// UniMate's second naming stage (its language model's corrections)
				var where = new Dictionary<int, UniMateNaming.Place>();
				foreach ( var limb in rig.Analysis.Limbs )
					for ( var k = 0; k < limb.Chain.Count; k++ )
						where[limb.Chain[k]] = new UniMateNaming.Place( $"{limb.Kind}/{limb.Side}/{limb.Chain[0]}", k, limb.Chain.Count, false );
				where[rig.Analysis.BodyRoot] = new UniMateNaming.Place( null, 0, 1, true );
				var corrected = UniMateNaming.Correct( prep.RawNames, names, prep.Parents,
					prep.Kept.Select( b => where.TryGetValue( b, out var w ) ? w : (UniMateNaming.Place?)null ).ToList(), objectType );
				// a raw name UniMate's own data labelled: exactly that label
				for ( var i = 0; i < corrected.Length; i++ )
					if ( UniMateNameTable.Lookup( prep.RawNames[i] ) is { } known ) corrected[i] = known;
				if ( !corrected.SequenceEqual( names ) )
				{
					names = corrected;
					(faceRight, faceLeft, bodyAxis, _) = UniMateNames.ResolveFaceJoints( corrected, prep.RawNames );
				}
				// one of UniMate's own rigs (the same joints): the facing UniMate gave it
				if ( UniMateNameTable.RigFacing( prep.RawNames ) is { } rigFace )
					(faceRight, faceLeft, bodyAxis) = rigFace;
			}
			if ( alignVocabulary )
			{
				var aligned = mixamoBody ? prep.Kept.Select( PeopleNameOf ).ToArray() : names.Select( UniMateVocabulary.Align ).ToArray();
				if ( !aligned.SequenceEqual( names ) )
				{
					names = aligned;
					(faceRight, faceLeft, bodyAxis, _) = UniMateNames.ResolveFaceJoints( aligned, prep.RawNames );
				}
			}
			// a facing pair sitting on one point (or one above the other) defines no direction: the rule stage's pair,
			// else none, instead of a skeleton that can't be put in UniMate's frame
			bool Degenerate( int r, int l ) => r >= 0 && l >= 0 && !bodyAxis && Vector3.Cross( rest[prep.Kept[r]].Pos - rest[prep.Kept[l]].Pos, rig.Up ).LengthSquared() < 1e-10f;
			if ( Degenerate( faceRight, faceLeft ) )
			{
				(faceRight, faceLeft, bodyAxis) = (prep.FaceRight, prep.FaceLeft, prep.BodyAxis);
				if ( Degenerate( faceRight, faceLeft ) ) (faceRight, faceLeft, bodyAxis) = (-1, -1, false);
			}
			skeleton = UniMateSkeleton.Build( names, prep.Parents, prep.Kept.Select( b => rest[b].Pos ).ToList(),
				prep.Kept.Select( b => rest[b].Rot ).ToList(), faceRight, faceLeft, null,
				UniMateSkeleton.EngineCanonicalBasis, bodyAxis: bodyAxis );
		}
		catch ( ArgumentException e ) { throw new InvalidOperationException( e.Message, e ); }
		var bone = skeleton.SourceIndex.Select( i => prep.Kept[i] ).ToArray();
		return new UniMateRig( rig, skeleton, bone, new Vector3[bone.Length], family ) { Prep = prep, AsMixamoBody = mixamoBody };
	}

	/// <summary>UniMate joints with no child joint (their own rotation is not part of the motion).</summary>
	bool[] EndJoint => _endJoint ??= Enumerable.Range( 0, Count ).Select( j => !Skeleton.Parents.Contains( j ) ).ToArray();
	bool[] _endJoint;

	/// <summary>True when UniMate was shown this person as a Mixamo body (see Build's peopleBody).</summary>
	public bool AsMixamoBody { get; private init; }

	/// <summary>Arms further than this from level and straight count as not in a T-pose.</summary>
	const float TPoseToleranceDeg = 20f;

	/// <summary>
	/// The rest pose with each arm (upper arm, forearm, hand; a clavicle stays) turned level and straight out to its
	/// side, as Mixamo characters stand, or null when the arms already are. World transforms.
	/// </summary>
	static XForm[] ArmsInTPose( MotionRig rig )
	{
		var s = rig.Skeleton;
		var arms = rig.Analysis.Limbs.Where( l => l.Kind == LimbKind.Arm && l.Chain.Count >= 3 ).ToList();
		if ( arms.Count == 0 ) return null;
		var world = s.RestWorld.ToArray();
		var children = new List<int>[s.Count];
		for ( var b = 0; b < s.Count; b++ ) children[b] = new List<int>();
		for ( var b = 0; b < s.Count; b++ ) if ( s[b].ParentIndex >= 0 ) children[s[b].ParentIndex].Add( b );
		var lateralAxis = Vector3.Normalize( Vector3.Cross( rig.Up, rig.Forward ) );
		void Turn( int b, Vector3 tip, Vector3 target )
		{
			var dir = tip - world[b].Pos;
			if ( dir.LengthSquared() < 1e-8f ) return;
			var delta = MathQ.FromTo( Vector3.Normalize( dir ), target );
			var pivot = world[b].Pos;
			var stack = new Stack<int>(); stack.Push( b );
			while ( stack.Count > 0 )
			{
				var d = stack.Pop();
				world[d] = new XForm( pivot + Vector3.Transform( world[d].Pos - pivot, delta ), Quaternion.Normalize( delta * world[d].Rot ) );
				foreach ( var c in children[d] ) stack.Push( c );
			}
		}
		Vector3 TipOf( int b, int next ) => next >= 0 ? world[next].Pos
			: children[b].Count > 0 ? children[b].Aggregate( Vector3.Zero, ( a, c ) => a + world[c].Pos ) / children[b].Count : world[b].Pos;
		var changed = false;
		foreach ( var arm in arms )
		{
			var chain = arm.Chain;
			var start = chain.Count >= 4 ? 1 : 0;
			var outward = Vector3.Dot( world[chain[start]].Pos - world[arm.Attach].Pos, lateralAxis ) >= 0 ? lateralAxis : -lateralAxis;
			for ( var i = start; i < chain.Count; i++ )
			{
				var b = chain[i];
				var tip = TipOf( b, i + 1 < chain.Count ? chain[i + 1] : -1 );
				var dir = tip - world[b].Pos;
				if ( dir.LengthSquared() < 1e-8f ) continue;
				if ( MathQ.AngleBetween( Vector3.Normalize( dir ), outward ) * 180f / MathF.PI <= TPoseToleranceDeg ) continue;
				Turn( b, tip, outward );
				changed = true;
			}
		}
		return changed ? world : null;
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
		// skinned secondary roots grafted onto the body (UniMatePrep): they follow the bone they hang off, at their
		// rest offset from it, so they come after it; everything else in index order (parents first)
		var grafts = Prep?.Grafts ?? new Dictionary<int, int>();
		int TopOf( int b ) { while ( s[b].ParentIndex >= 0 ) b = s[b].ParentIndex; return b; }
		var order = Enumerable.Range( 0, s.Count ).OrderBy( b => grafts.ContainsKey( TopOf( b ) ) ? 1 : 0 ).ThenBy( b => b ).ToArray();
		for ( var t = 0; t < T; t++ )
		{
			Array.Clear( desiredWorld );
			// an end joint (hand, head, toe) has no rotation of its own in UniMate's motion - nothing below it to aim - so
			// it keeps the rig's own relation to its parent (upstream leaves end joints at rest too); the rest UniMate
			// saw may differ from the rig's (arms shown in a T-pose), which would turn the wrists
			for ( var j = 0; j < Count; j++ ) if ( Bone[j] >= 0 && !EndJoint[j] ) desiredWorld[Bone[j]] = motion.WorldRot[t, j];
			var src = baseFrames is { Count: > 0 } ? baseFrames[Math.Min( t, baseFrames.Count - 1 )] : null;
			var frame = new XForm[s.Count];
			var carry = Quaternion.Identity;
			if ( ancestors.Count > 0 && desiredWorld[rootBone] is { } rootRot )
			{
				MathQ.SwingTwist( Quaternion.Normalize( rootRot * Quaternion.Conjugate( restWorld[rootBone].Rot ) ), up, out _, out carry );
			}
			foreach ( var b in order )
			{
				var local = src?[b] ?? s[b].RestLocal;
				var parent = s[b].ParentIndex;
				var parentWorld = parent < 0 ? XForm.Identity : world[parent];
				if ( parent < 0 && grafts.TryGetValue( b, out var host ) )
				{
					var follow = XForm.Compose( world[host], XForm.ToLocal( restWorld[host], restWorld[b] ) );
					local = desiredWorld[b] is { } gr ? new XForm( follow.Pos, gr ) : follow;
				}
				else if ( ancestors.Contains( b ) && parent < 0 )
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
