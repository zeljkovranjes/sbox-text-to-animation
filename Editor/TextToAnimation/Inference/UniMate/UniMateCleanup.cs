using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace TextToAnimation.Editor.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// UniMate's own motion clean-up and prompt joins, ported 1:1 from its Blender add-on (UniMate-B3D, MIT:
/// backend/collision.py, backend/ground.py, backend/timeline.py finish_motion): mesh-capsule self-collision
/// projection, ground contact with planted-sole limb IK, foot tilt limits, knee-side preservation, and inertial joins
/// where prompts meet. Motion is in the add-on's form: per frame and joint a world position and a world rotation
/// relative to the rest pose (rest = identity), joints parent-before-child.
/// Adapter (engine rigs are not Blender armatures): a bone's "tail" direction is toward its children (the parent's
/// direction for a leaf), labels are UniMate's for the joints it animates and the add-on's semantic_name otherwise,
/// joints may have several roots, and a joint's offset from its parent is the frame's own (a moving root under a
/// static parent); the ground is the rest-pose sole plane (the add-on's default without a ground mesh).
/// </summary>
public static class UniMateCleanup
{
	// ------------------------------------------------------------------ rig

	public sealed record Capsule( int Joint, Vector3 A, Vector3 B, float Radius );

	public sealed class FootProfile
	{
		public int Joint, Parent, Upper;
		public float LegLength, StanceTilt, SwingTilt;
	}

	/// <summary>The add-on's exported skeleton, for an engine skeleton.</summary>
	public sealed class Skeleton
	{
		public int[] Parents;
		public Vector3[] Heads;
		public string[] Labels;
		public string[] Names;
		/// <summary>The rest matrix's Y (toward the tail) and X columns.</summary>
		public Vector3[] RestY, RestX;
		public List<Capsule> Capsules = new();
		public List<FootProfile> Profiles = new();
		/// <summary>The joint whose position is "the root" (the add-on's joint 0): the motion root.</summary>
		public int Root;
		/// <summary>The engine bone of each joint (when built from an engine rig).</summary>
		public int[] Engine;
		/// <summary>
		/// Adapter: capsule pairs that already overlap in the rest pose are skin that meets by design (a hand's
		/// neighbouring fingers), not collisions - the add-on would push such fingers apart on every frame. Off in the
		/// parity tests, which compare against the add-on as it is.
		/// </summary>
		public bool IgnoreRestOverlaps;
		/// <summary>Adapter (the editor's): UniMate's paws glide while down - a stance is held where the foot is on average,
		/// a leg with a thigh of its own is solved with it, and a paw no stance holds stands at each touch-down (PlanGait).</summary>
		public bool GroundedStances;
		/// <summary>Adapter: each foot's lowest rest-pose mesh point (its own and its children's), where it meets the ground.</summary>
		public Dictionary<int, Vector3> Soles;
		public int Count => Parents.Length;
	}

	/// <summary>The add-on's semantic_name (motion.py).</summary>
	public static string SemanticName( string name )
	{
		name = (name ?? "").Split( ':' ).Last();
		name = Regex.Replace( name, @"^(DEF|ORG|MCH)[-_]", "", RegexOptions.IgnoreCase );
		name = Regex.Replace( name, @"([a-z])([A-Z])", "$1 $2" );
		name = Regex.Replace( name, @"[._-]L$", " left", RegexOptions.IgnoreCase );
		name = Regex.Replace( name, @"[._-]R$", " right", RegexOptions.IgnoreCase );
		return Regex.Replace( name, @"[_.-]+", " " ).Trim().ToLowerInvariant();
	}

	/// <summary>
	/// The skeleton of an engine rig: rest heads, tail directions, labels; capsules fitted to <paramref name="points"/>
	/// (mesh vertices in the rig's rest space, per bone, rig.py mesh_capsules) and foot profiles (foot_profiles).
	/// </summary>
	public static Skeleton Build( IReadOnlyList<int> parents, IReadOnlyList<Vector3> heads, IReadOnlyList<string> names,
		IReadOnlyList<string> labels, IReadOnlyDictionary<int, IReadOnlyList<Vector3>> points, int root, IReadOnlyList<Quaternion> restRot = null,
		IEnumerable<int> feet = null )
	{
		var extraFeet = feet?.ToHashSet() ?? new HashSet<int>();
		var n = parents.Count;
		var s = new Skeleton
		{
			Parents = parents.ToArray(), Heads = heads.ToArray(), Names = names.ToArray(), Labels = labels.ToArray(), Root = root,
			RestY = new Vector3[n], RestX = new Vector3[n],
		};
		var children = Enumerable.Range( 0, n ).ToLookup( j => s.Parents[j] );
		for ( var j = 0; j < n; j++ )
		{
			var kids = children[j].Where( c => (s.Heads[c] - s.Heads[j]).Length() > 1e-6f ).ToList();
			Vector3 dir;
			if ( kids.Count > 0 ) dir = kids.Aggregate( Vector3.Zero, ( a, c ) => a + s.Heads[c] ) / kids.Count - s.Heads[j];
			// an end bone (a hand, a paw, a twist bone beside its limb's head) points along its parent's axis as carried
			// by its own rest orientation, the way an imported leaf bone is laid out
			else if ( s.Parents[j] >= 0 && restRot is not null ) dir = Mul( restRot[j] * T( restRot[s.Parents[j]] ), s.RestY[s.Parents[j]] );
			else if ( s.Parents[j] >= 0 ) dir = s.Heads[j] - s.Heads[s.Parents[j]];
			else dir = Vector3.UnitY;
			if ( dir.Length() < 1e-9f ) dir = Vector3.UnitY;
			s.RestY[j] = Vector3.Normalize( dir );
			var x = Vector3.Cross( s.RestY[j], Vector3.UnitZ );
			s.RestX[j] = x.Length() > 1e-6f ? Vector3.Normalize( x ) : Vector3.UnitX;
		}
		if ( points is not null )
			foreach ( var (j, values) in points.OrderBy( kv => kv.Key ) )
			{
				if ( values.Count < 4 ) continue;
				var head = s.Heads[j];
				var direction = s.RestY[j];
				var axial = values.Select( v => Vector3.Dot( v - head, direction ) ).ToArray();
				var radial = values.Select( ( v, i ) => (v - head - axial[i] * direction).Length() ).ToArray();
				var radius = Quantile( radial, .98f );
				if ( radius < 1e-6f ) continue;
				float low = axial.Min() + radius, high = axial.Max() - radius;
				if ( low > high ) low = high = (axial.Min() + axial.Max()) * .5f;
				s.Capsules.Add( new Capsule( j, head + direction * low, head + direction * high, radius ) );
			}
		// the lowest rest point of each bone's mesh with its children's (a paw and its toes)
		if ( points is not null )
		{
			s.Soles = new Dictionary<int, Vector3>();
			for ( var j = 0; j < n; j++ )
			{
				var own = new List<Vector3>();
				for ( var k = 0; k < n; k++ )
				{
					var a = k; while ( a >= 0 && a != j ) a = s.Parents[a];
					if ( a == j && points.TryGetValue( k, out var pts ) ) own.AddRange( pts );
				}
				if ( own.Count > 0 ) s.Soles[j] = own.OrderBy( v => v.Z ).First();
			}
		}
		// foot_profiles: terminal support bones by their label
		for ( var j = 0; j < n; j++ )
		{
			if ( s.Names[j] is null || s.Parents[j] < 0 ) continue;
			var words = SemanticName( s.Labels[j] ).Split( ' ' );
			// adapter: every leg the rig analysis finds stands on its end bone (a quadruped's front paws are labelled
			// "Hand", as in UniMate's own data, and the add-on would leave them sliding)
			if ( !words.Contains( "foot" ) && !words.Contains( "paw" ) && !extraFeet.Contains( j ) ) continue;
			var parent = s.Parents[j];
			var upper = s.Parents[parent];
			if ( upper < 0 ) continue;
			var rest = s.RestY[j];
			var pitch = MathF.Atan2( rest.Z, MathF.Sqrt( rest.X * rest.X + rest.Y * rest.Y ) ) * 180f / MathF.PI;
			var stance = Math.Clamp( MathF.Abs( pitch ) + 15, 20, 45 );
			s.Profiles.Add( new FootProfile
			{
				Joint = j, Parent = parent, Upper = upper, StanceTilt = stance, SwingTilt = MathF.Max( stance + 15, 45 ),
				LegLength = (s.Heads[parent] - s.Heads[upper]).Length() + (s.Heads[j] - s.Heads[parent]).Length(),
			} );
		}
		// one foot per leg: a profiled bone with another profiled bone below it (a hock above its paw) is the leg, not
		// the foot - both standing would pull one chain two ways
		if ( extraFeet.Count > 0 || s.Soles is not null )
		{
			bool Below( int j, int ancestor ) { for ( var a = s.Parents[j]; a >= 0; a = s.Parents[a] ) if ( a == ancestor ) return true; return false; }
			s.Profiles = s.Profiles.Where( p => !s.Profiles.Any( q => q != p && Below( q.Joint, p.Joint ) ) ).ToList();
		}
		return s;
	}

	/// <summary>numpy's default (linear) quantile.</summary>
	static float Quantile( float[] values, float q )
	{
		var sorted = values.OrderBy( v => v ).ToArray();
		var pos = q * (sorted.Length - 1);
		var lo = (int)MathF.Floor( pos );
		var hi = Math.Min( lo + 1, sorted.Length - 1 );
		return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
	}

	// ------------------------------------------------------------------ rotations (matrices as quaternions: A@B = A*B)

	static Quaternion FromRotvec( Vector3 v )
	{
		var angle = v.Length();
		return angle < 1e-12f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle( v / angle, angle );
	}

	static Vector3 ToRotvec( Quaternion q )
	{
		q = Quaternion.Normalize( q );
		if ( q.W < 0 ) q = Quaternion.Negate( q );
		var xyz = new Vector3( q.X, q.Y, q.Z );
		var s = xyz.Length();
		if ( s < 1e-12f ) return xyz * 2f;
		return xyz / s * (2f * MathF.Atan2( s, q.W ));
	}

	static float Magnitude( Quaternion q ) => ToRotvec( q ).Length();
	static Quaternion T( Quaternion q ) => Quaternion.Conjugate( q );
	static Vector3 Mul( Quaternion q, Vector3 v ) => Vector3.Transform( v, q );
	static Vector3 Unit( Vector3 v ) => v / MathF.Max( v.Length(), 1e-12f );

	// ------------------------------------------------------------------ motion

	/// <summary>A clip in the add-on's form, with each joint's offset from its parent (rest-relative, per frame).</summary>
	public sealed class Motion
	{
		public Vector3[][] Pos;
		public Quaternion[][] Rot;
		/// <summary>Per frame, joint: parent's rest-relative offset (heads[j]-heads[p] for a joint at rest).</summary>
		public Vector3[][] Offset;
		public int Frames => Pos.Length;
	}

	public static Quaternion[][] ToLocal( Quaternion[][] rot, int[] parents )
	{
		var local = new Quaternion[rot.Length][];
		for ( var t = 0; t < rot.Length; t++ )
		{
			local[t] = new Quaternion[parents.Length];
			for ( var j = 0; j < parents.Length; j++ )
				local[t][j] = parents[j] < 0 ? rot[t][j] : T( rot[t][parents[j]] ) * rot[t][j];
		}
		return local;
	}

	/// <summary>forward_kinematics for one frame: roots keep their own positions (<paramref name="roots"/>).</summary>
	static (Vector3[] Pos, Quaternion[] Rot) Fk( Vector3[] roots, Quaternion[] local, Vector3[] offset, int[] parents )
	{
		var n = parents.Length;
		var pos = new Vector3[n];
		var rot = new Quaternion[n];
		for ( var j = 0; j < n; j++ )
		{
			var p = parents[j];
			// rotations kept exact: the add-on never re-orthonormalises its matrices, and on a deep chain (a hand's
			// fingers) the drift doubles with every conjugated update until the pose blows up
			if ( p < 0 ) { pos[j] = roots[j]; rot[j] = Quaternion.Normalize( local[j] ); continue; }
			rot[j] = Quaternion.Normalize( rot[p] * local[j] );
			pos[j] = pos[p] + Mul( rot[p], offset[j] );
		}
		return (pos, rot);
	}

	static Vector3[] RootsOf( Vector3[] pos, int[] parents )
	{
		var r = new Vector3[parents.Length];
		for ( var j = 0; j < parents.Length; j++ ) if ( parents[j] < 0 ) r[j] = pos[j];
		return r;
	}

	// ------------------------------------------------------------------ prompt joins (timeline.py finish_motion)

	static float Ease( float u ) => u * u * u * (10 + u * (-15 + 6 * u));

	/// <summary>
	/// The inertial correction at each prompt boundary (<paramref name="cursors"/>: first frame of each later clip):
	/// the step from the previous frame's local pose decays over <paramref name="transitionFrames"/>, and the root
	/// carries its previous velocity while the error fades (finish_motion without pose references).
	/// </summary>
	public static void Joins( Motion m, Skeleton s, IEnumerable<int> cursors, int transitionFrames = 12 )
	{
		var local = ToLocal( m.Rot, s.Parents );
		var roots = Enumerable.Range( 0, m.Frames ).Select( t => RootsOf( m.Pos[t], s.Parents ) ).ToArray();
		var bounds = cursors.Where( c => c > 0 && c < m.Frames ).Distinct().OrderBy( c => c ).ToList();
		for ( var b = 0; b < bounds.Count; b++ )
		{
			var cursor = bounds[b];
			var clipEnd = b + 1 < bounds.Count ? bounds[b + 1] - 1 : m.Frames - 1;
			var end = Math.Min( cursor + transitionFrames, clipEnd );
			if ( end <= cursor ) continue;
			var n = end - cursor;
			var rotError = new Vector3[s.Count];
			for ( var j = 0; j < s.Count; j++ ) rotError[j] = ToRotvec( local[cursor - 1][j] * T( local[cursor][j] ) );
			for ( var j = 0; j < s.Count; j++ )
			{
				if ( s.Parents[j] >= 0 ) continue;
				var rootError = roots[cursor - 1][j] - roots[cursor][j];
				var rootVelocity = cursor > 1 ? roots[cursor - 1][j] - roots[cursor - 2][j] : Vector3.Zero;
				for ( var t = 0; t < n; t++ )
				{
					var weight = 1 - Ease( (float)t / n );
					roots[cursor + t][j] += rootError * weight + rootVelocity * (t + 1) * weight;
				}
			}
			for ( var t = 0; t < n; t++ )
			{
				var weight = 1 - Ease( (float)t / n );
				for ( var j = 0; j < s.Count; j++ ) local[cursor + t][j] = FromRotvec( rotError[j] * weight ) * local[cursor + t][j];
			}
		}
		for ( var t = 0; t < m.Frames; t++ ) (m.Pos[t], m.Rot[t]) = Fk( roots[t], local[t], m.Offset[t], s.Parents );
	}

	// ------------------------------------------------------------------ self-collision (collision.py)

	/// <summary>Exact closest points for two segments (collision.py closest, for one pair).</summary>
	static (Vector3 P, Vector3 Q) Closest( Vector3 a, Vector3 b, Vector3 c, Vector3 d )
	{
		Vector3 u = b - a, v = d - c, w = a - c;
		float uu = Vector3.Dot( u, u ), vv = Vector3.Dot( v, v ), uv = Vector3.Dot( u, v ), uw = Vector3.Dot( u, w ), vw = Vector3.Dot( v, w );
		var denom = uu * vv - uv * uv;
		var ss = (uv * vw - vv * uw) / MathF.Max( denom, 1e-20f );
		var tt = (uu * vw - uv * uw) / MathF.Max( denom, 1e-20f );
		var valid = denom > 1e-15f && ss >= 0 && ss <= 1 && tt >= 0 && tt <= 1;
		var sa = new[] { 0f, 1f, Math.Clamp( -uw / MathF.Max( uu, 1e-20f ), 0, 1 ), Math.Clamp( (uv - uw) / MathF.Max( uu, 1e-20f ), 0, 1 ), Math.Clamp( ss, 0, 1 ) };
		var tb = new[] { Math.Clamp( vw / MathF.Max( vv, 1e-20f ), 0, 1 ), Math.Clamp( (vw + uv) / MathF.Max( vv, 1e-20f ), 0, 1 ), 0f, 1f, Math.Clamp( tt, 0, 1 ) };
		var best = -1; var bestDist = float.PositiveInfinity;
		for ( var k = 0; k < 5; k++ )
		{
			if ( k == 4 && !valid ) continue;
			var dist = (a + sa[k] * u - (c + tb[k] * v)).Length();
			if ( dist < bestDist ) { bestDist = dist; best = k; }
		}
		if ( best < 0 ) best = 0; // numpy's argmin over no finite candidate
		return (a + sa[best] * u, c + tb[best] * v);
	}

	sealed class CapsuleRig
	{
		public readonly int[] Joints;
		public readonly Vector3[] A, B;
		public readonly float[] Radii;
		public readonly (int I, int J)[] Pairs;
		public readonly (int[] First, int[] Second)[] Chains;
		public readonly float[] Clearance, Mobility;
		public readonly float Tolerance;

		public CapsuleRig( Skeleton s )
		{
			var caps = s.Capsules;
			Joints = caps.Select( c => c.Joint ).ToArray();
			A = caps.Select( c => c.A - s.Heads[c.Joint] ).ToArray();
			B = caps.Select( c => c.B - s.Heads[c.Joint] ).ToArray();
			Radii = caps.Select( c => c.Radius ).ToArray();
			var ancestors = new List<int>[s.Count];
			for ( var j = 0; j < s.Count; j++ )
			{
				var chain = new List<int>();
				for ( var k = j; k >= 0; k = s.Parents[k] ) chain.Add( k );
				ancestors[j] = chain;
			}
			var pairs = new List<(int, int)>();
			var chains = new List<(int[], int[])>();
			for ( var i = 0; i < caps.Count; i++ )
				for ( var j = i + 1; j < caps.Count; j++ )
				{
					var ac = ancestors[Joints[i]]; var bc = ancestors[Joints[j]];
					var lca = ac.FirstOrDefault( k => bc.Contains( k ), -1 );
					if ( lca < 0 ) continue; // separate roots never share skin
					// Neighbouring tissue belongs to a continuous skin surface.
					if ( ac.IndexOf( lca ) + bc.IndexOf( lca ) <= 2 ) continue;
					if ( s.IgnoreRestOverlaps )
					{
						var (p, q) = Closest( A[i] + s.Heads[Joints[i]], B[i] + s.Heads[Joints[i]], A[j] + s.Heads[Joints[j]], B[j] + s.Heads[Joints[j]] );
						if ( (p - q).Length() < Radii[i] + Radii[j] ) continue;
					}
					pairs.Add( (i, j) );
					chains.Add( (ac.Take( Math.Min( ac.IndexOf( lca ), 3 ) ).ToArray(), bc.Take( Math.Min( bc.IndexOf( lca ), 3 ) ).ToArray()) );
				}
			Pairs = pairs.ToArray();
			Chains = chains.ToArray();
			Clearance = Pairs.Select( p => Radii[p.I] + Radii[p.J] ).ToArray();
			var volume = new float[s.Count];
			for ( var c = 0; c < caps.Count; c++ )
			{
				var mass = Radii[c] * Radii[c] * ((B[c] - A[c]).Length() + 4 * Radii[c] / 3);
				foreach ( var parent in ancestors[Joints[c]] ) volume[parent] += mass;
			}
			var positive = volume.Where( v => v > 0 ).OrderBy( v => v ).ToArray();
			var scale = positive.Length > 0 ? Median( positive ) : 1f;
			Mobility = volume.Select( v => Math.Clamp( scale / MathF.Max( v, scale * .1f ), .03f, 3f ) ).ToArray();
			for ( var j = 0; j < s.Count; j++ ) if ( s.Parents[j] < 0 ) Mobility[j] = 0; // the add-on's joint 0
			var lo = new Vector3( float.MaxValue ); var hi = new Vector3( float.MinValue );
			foreach ( var h in s.Heads ) { lo = Vector3.Min( lo, h ); hi = Vector3.Max( hi, h ); }
			Tolerance = (hi - lo).Length() * .0005f;
		}

		public (Vector3[] P, Vector3[] Q, Vector3[] Delta, float[] Distance) Contacts( Vector3[] pos, Quaternion[] rot )
		{
			var n = Pairs.Length;
			var p = new Vector3[n]; var q = new Vector3[n]; var delta = new Vector3[n]; var distance = new float[n];
			for ( var k = 0; k < n; k++ )
			{
				var (i, j) = Pairs[k];
				var ai = pos[Joints[i]] + Mul( rot[Joints[i]], A[i] ); var bi = pos[Joints[i]] + Mul( rot[Joints[i]], B[i] );
				var aj = pos[Joints[j]] + Mul( rot[Joints[j]], A[j] ); var bj = pos[Joints[j]] + Mul( rot[Joints[j]], B[j] );
				(p[k], q[k]) = Closest( ai, bi, aj, bj );
				delta[k] = p[k] - q[k];
				distance[k] = delta[k].Length();
			}
			return (p, q, delta, distance);
		}

		public float Penetration( Vector3[] pos, Quaternion[] rot )
		{
			if ( Pairs.Length == 0 ) return 0;
			var d = Contacts( pos, rot ).Distance;
			var worst = 0f;
			for ( var k = 0; k < d.Length; k++ ) worst = MathF.Max( worst, Clearance[k] - d[k] );
			return worst;
		}
	}

	static float Median( float[] sorted ) => sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * .5f;

	/// <summary>collision.py solve: articulated projection out of capsule overlaps, coherent across frames.</summary>
	public static string Collisions( Motion m, Skeleton s, int iterations = 32 )
	{
		if ( s.Capsules.Count == 0 ) return "no collision shapes";
		var rig = new CapsuleRig( s );
		if ( rig.Pairs.Length == 0 ) return "no capsule pairs";
		var local = ToLocal( m.Rot, s.Parents );
		var priorNormal = new Vector3[rig.Pairs.Length];
		var correction = Enumerable.Repeat( Quaternion.Identity, s.Count ).ToArray();
		float before = 0, after = 0;
		for ( var t = 0; t < m.Frames; t++ )
		{
			before = MathF.Max( before, rig.Penetration( m.Pos[t], m.Rot[t] ) );
			var poseLocal = new Quaternion[s.Count];
			for ( var j = 0; j < s.Count; j++ ) poseLocal[j] = FromRotvec( ToRotvec( correction[j] ) * .96f ) * local[t][j];
			var roots = RootsOf( m.Pos[t], s.Parents );
			var (pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
			for ( var iteration = 0; iteration < iterations; iteration++ )
			{
				var (pa, pb, delta, dist) = rig.Contacts( pos, rot );
				var normal = new Vector3[rig.Pairs.Length];
				var penetration = new float[rig.Pairs.Length];
				var active = new List<int>();
				for ( var k = 0; k < rig.Pairs.Length; k++ )
				{
					normal[k] = dist[k] < 1e-8f ? Vector3.UnitZ : delta[k] / MathF.Max( dist[k], 1e-9f );
					var old = priorNormal[k].Length() > .5f;
					var near = dist[k] < rig.Clearance[k] * 1.35f;
					// Retain which side the limb approached from until it separates.
					if ( old && near && Vector3.Dot( normal[k], priorNormal[k] ) < .5f ) normal[k] = priorNormal[k];
					penetration[k] = rig.Clearance[k] - Vector3.Dot( delta[k], normal[k] );
					if ( penetration[k] > rig.Tolerance ) active.Add( k );
				}
				if ( active.Count == 0 ) break;
				var updates = new Vector3[s.Count];
				foreach ( var pair in active )
				{
					var jacobians = new List<(int Joint, Vector3 Jac)>();
					foreach ( var (chain, point, sign) in new[] { (rig.Chains[pair].First, pa[pair], 1f), (rig.Chains[pair].Second, pb[pair], -1f) } )
						foreach ( var joint in chain )
							jacobians.Add( (joint, sign * Vector3.Cross( point - pos[joint], normal[pair] )) );
					var denom = jacobians.Sum( x => rig.Mobility[x.Joint] * Vector3.Dot( x.Jac, x.Jac ) );
					if ( denom < 1e-10f ) continue;
					foreach ( var (joint, jac) in jacobians ) updates[joint] += jac * rig.Mobility[joint] * penetration[pair] / denom * .65f;
				}
				var cap = 4f * MathF.PI / 180f;
				for ( var j = 0; j < s.Count; j++ )
				{
					var length = updates[j].Length();
					updates[j] *= MathF.Min( 1f, cap / MathF.Max( length, 1e-12f ) );
				}
				for ( var j = 0; j < s.Count; j++ )
				{
					var parent = s.Parents[j];
					if ( parent < 0 ) continue;
					var worldChange = FromRotvec( updates[j] );
					poseLocal[j] = Quaternion.Normalize( T( rot[parent] ) * worldChange * rot[parent] * poseLocal[j] );
				}
				(pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
			}
			{
				var (_, _, delta, dist) = rig.Contacts( pos, rot );
				for ( var k = 0; k < rig.Pairs.Length; k++ )
				{
					var near = dist[k] < rig.Clearance[k] * 1.15f;
					var current = delta[k] / MathF.Max( dist[k], 1e-9f );
					var good = near && Vector3.Dot( current, priorNormal[k] ) > .5f;
					if ( good ) priorNormal[k] = current;
					else if ( near && priorNormal[k].Length() < .5f ) priorNormal[k] = current;
					if ( !near ) priorNormal[k] = Vector3.Zero;
				}
			}
			for ( var j = 0; j < s.Count; j++ ) correction[j] = poseLocal[j] * T( local[t][j] );
			m.Pos[t] = pos; m.Rot[t] = rot;
			after = MathF.Max( after, rig.Penetration( pos, rot ) );
		}
		return $"collisions: {rig.Pairs.Length} pairs, overlap {before:0.###} -> {after:0.###}";
	}

	// ------------------------------------------------------------------ ground contact (ground.py)

	sealed class Foot
	{
		public FootProfile Profile; public Vector3 Offset; public float Radius;
		/// <summary>GroundedStances: the thigh above a four-bone leg (a fox's hind leg: thigh, shin, hock, paw), turned with
		/// the add-on's three joints - its "upper" is the knee there - or -1.</summary>
		public int Hip = -1;
		/// <summary>The leg's length from its top joint to the end bone's head.</summary>
		public float Chain;
		public int Top => Hip >= 0 ? Hip : Profile.Upper;
		public IEnumerable<int> Joints => Hip >= 0 ? new[] { Profile.Joint, Profile.Parent, Profile.Upper, Hip } : new[] { Profile.Joint, Profile.Parent, Profile.Upper };
	}

	static Vector3 Sole( Vector3 pos, Quaternion rot, Foot foot, Vector3 normal ) => pos + Mul( rot, foot.Offset ) - normal * foot.Radius;

	static float[] UniformNearest( float[] x, int size )
	{
		var r = size / 2; var n = x.Length; var y = new float[n];
		for ( var i = 0; i < n; i++ ) { float sum = 0; for ( var k = -r; k <= r; k++ ) sum += x[Math.Clamp( i + k, 0, n - 1 )]; y[i] = sum / size; }
		return y;
	}

	static float[] MaximumNearest( float[] x, int size )
	{
		var r = size / 2; var n = x.Length; var y = new float[n];
		for ( var i = 0; i < n; i++ ) { var m = float.MinValue; for ( var k = -r; k <= r; k++ ) m = MathF.Max( m, x[Math.Clamp( i + k, 0, n - 1 )] ); y[i] = m; }
		return y;
	}

	static float[] GaussianNearest( float[] x, float sigma )
	{
		var r = (int)(4f * sigma + .5f); var n = x.Length;
		var w = Enumerable.Range( -r, 2 * r + 1 ).Select( k => MathF.Exp( -.5f * k * k / (sigma * sigma) ) ).ToArray();
		var total = w.Sum();
		var y = new float[n];
		for ( var i = 0; i < n; i++ ) { float sum = 0; for ( var k = -r; k <= r; k++ ) sum += w[k + r] * x[Math.Clamp( i + k, 0, n - 1 )]; y[i] = sum / total; }
		return y;
	}

	static List<(int Start, int End)> Runs( bool[] mask, int minLength = 4 )
	{
		var result = new List<(int, int)>();
		int? start = null;
		for ( var i = 0; i <= mask.Length; i++ )
		{
			var value = i < mask.Length && mask[i];
			if ( value && start is null ) start = i;
			else if ( !value && start is { } s0 ) { if ( i - s0 >= minLength ) result.Add( (s0, i) ); start = null; }
		}
		return result;
	}

	static float SoftTilt( float angle, float limit )
	{
		var shoulder = .6f * limit;
		var extra = MathF.Max( MathF.Abs( angle ) - shoulder, 0 );
		return MathF.Sign( angle ) * (MathF.Min( MathF.Abs( angle ), shoulder ) + (limit - shoulder) * MathF.Tanh( extra / MathF.Max( limit - shoulder, 1e-8f ) ));
	}

	/// <summary>ground.py plant: lift intrusions, find stances, limit foot tilt, hold planted soles, keep knee sides.</summary>
	public static string Plant( Motion m, Skeleton s, Motion reference = null )
	{
		var caps = s.Capsules.ToDictionary( c => c.Joint );
		// adapter: with the mesh at hand a foot touches the ground at its own lowest point (a fat foot capsule's bottom
		// lies well below the real sole, so the add-on's ground sat under the floor and most stances were out of reach)
		var feet = s.Profiles.Where( p => caps.ContainsKey( p.Joint ) || s.Soles?.ContainsKey( p.Joint ) == true ).Select( p =>
			s.Soles is not null && s.Soles.TryGetValue( p.Joint, out var sole )
				? new Foot { Profile = p, Offset = sole - s.Heads[p.Joint], Radius = 0 }
				: new Foot { Profile = p, Offset = caps[p.Joint].B - s.Heads[p.Joint], Radius = caps[p.Joint].Radius } ).ToList();
		if ( feet.Count == 0 ) return "no foot or paw bones to plant";
		foreach ( var foot in feet )
		{
			foot.Chain = foot.Profile.LegLength;
			if ( !s.GroundedStances ) continue;
			// a joint above the leg that is this leg's alone (not the pelvis or spine every leg hangs from)
			var up = s.Parents[foot.Profile.Upper];
			bool Above( int j, int ancestor ) { for ( var a = s.Parents[j]; a >= 0; a = s.Parents[a] ) if ( a == ancestor ) return true; return false; }
			if ( up < 0 || s.Parents[up] < 0 || feet.Any( o => o != foot && Above( o.Profile.Joint, up ) ) ) continue;
			foot.Hip = up;
			foot.Chain += (s.Heads[foot.Profile.Upper] - s.Heads[up]).Length();
		}
		var normal = Vector3.UnitZ;
		var height = feet.Min( f => f.Radius == 0 ? s.Heads[f.Profile.Joint].Z + f.Offset.Z
			: MathF.Min( caps[f.Profile.Joint].A.Z, caps[f.Profile.Joint].B.Z ) - caps[f.Profile.Joint].Radius );
		int n = m.Frames;

		float[] HeightsOf( Motion x, int f ) => Enumerable.Range( 0, n ).Select( t => Vector3.Dot( Sole( x.Pos[t][feet[f].Profile.Joint], x.Rot[t][feet[f].Profile.Joint], feet[f], normal ), normal ) - height ).ToArray();
		float[] Heights( int f ) => HeightsOf( m, f );
		// adapter: stances are found, and anchored, on the motion as the model made it when the port has smoothed it
		// (smoothing smears a planted foot into a glide)
		var found = reference ?? m;

		// Lift deep penetrations gradually so the IK can preserve the supplied root path.
		var intrusion = new float[n];
		for ( var f = 0; f < feet.Count; f++ ) { var h = Heights( f ); for ( var t = 0; t < n; t++ ) intrusion[t] = MathF.Max( intrusion[t], MathF.Max( -h[t], 0 ) ); }
		var envelope = GaussianNearest( MaximumNearest( intrusion, 7 ), 2 );
		var lift = new float[n];
		for ( var t = 0; t < n; t++ )
		{
			lift[t] = MathF.Max( intrusion[t], envelope[t] );
			for ( var j = 0; j < s.Count; j++ ) m.Pos[t][j] += lift[t] * normal;
		}

		// stance_windows
		var windows = new List<List<(int Start, int End)>>();
		var lifts = new float[feet.Count][];
		var rootSpeed = Enumerable.Range( 0, n ).Select( t => t == 0 ? 0f : new Vector3( m.Pos[t][0].X - m.Pos[t - 1][0].X, m.Pos[t][0].Y - m.Pos[t - 1][0].Y, 0 ).Length() ).ToArray();
		if ( n > 1 ) rootSpeed[0] = rootSpeed[1];
		var bodySpeed = UniformNearest( rootSpeed, 5 );
		for ( var f = 0; f < feet.Count; f++ )
		{
			var leg = feet[f].Profile.LegLength;
			var sole = Enumerable.Range( 0, n ).Select( t => Sole( found.Pos[t][feet[f].Profile.Joint], found.Rot[t][feet[f].Profile.Joint], feet[f], normal ) ).ToArray();
			var h = HeightsOf( found, f );
			var rawVelocity = Enumerable.Range( 0, n ).Select( t => t == 0 ? 0f : (sole[t] - sole[t - 1]).Length() ).ToArray();
			var velocity = UniformNearest( rawVelocity, 3 );
			var smooth = UniformNearest( h, 3 );
			var vertical = Enumerable.Range( 0, n ).Select( t => t == 0 ? 0f : MathF.Abs( h[t] - h[t - 1] ) ).ToArray();
			var mask = Enumerable.Range( 0, n ).Select( t => smooth[t] < MathF.Max( .025f, .16f * leg ) && vertical[t] < .08f * leg && velocity[t] < .08f * leg && rawVelocity[t] < .10f * leg ).ToArray();
			windows.Add( Runs( mask ) );
			lifts[f] = UniformNearest( Heights( f ), 3 );
		}

		StabilizeFeet( m, s, feet, normal, windows );
		var source = m.Pos.Select( p => p.ToArray() ).ToArray();
		OutOfReach = 0;
		var active = SolveContacts( m, s, feet, normal, height, windows, found, lifts, bodySpeed );
		var stanceFrames = windows.Sum( w => w.Sum( x => x.End - x.Start ) );
		var bends = PreserveBend( m, s, feet, source );
		return $"ground: {feet.Count} feet, {windows.Sum( w => w.Count )} stances ({stanceFrames} foot-frames, {OutOfReach} out of reach), {active} planted frames";
	}

	static void StabilizeFeet( Motion m, Skeleton s, List<Foot> feet, Vector3 normal, List<List<(int Start, int End)>> windows )
	{
		var n = m.Frames;
		for ( var f = 0; f < feet.Count; f++ )
		{
			var local = ToLocal( m.Rot, s.Parents );
			var foot = feet[f];
			var j = foot.Profile.Joint;
			var parent = s.Parents[j];
			var rest = s.RestY[j];
			var fallback = rest - normal * Vector3.Dot( rest, normal );
			var right = s.RestX[j];
			var signValue = MathF.Sign( Vector3.Dot( Vector3.Cross( normal, right ), fallback ) );
			var weight = new float[n];
			foreach ( var (start, end) in windows[f] )
				for ( var t = start; t < end; t++ ) weight[t] = MathF.Min( 1f, MathF.Min( (t - start + 1) / 4f, (end - t) / 4f ) );
			var stance = foot.Profile.StanceTilt * MathF.PI / 180f;
			var swing = foot.Profile.SwingTilt * MathF.PI / 180f;
			var desired = new Quaternion[n];
			for ( var t = 0; t < n; t++ )
			{
				var direction = Mul( m.Rot[t][j], rest );
				var dot = Vector3.Dot( direction, normal );
				var horizontal = direction - dot * normal;
				var length = horizontal.Length();
				var heading = horizontal / MathF.Max( length, 1e-12f );
				var side = Mul( m.Rot[t][j], right );
				var alternative = Vector3.Cross( normal, side );
				alternative /= MathF.Max( alternative.Length(), 1e-12f );
				alternative *= signValue != 0 ? signValue : 1f;
				// Toe heading is ambiguous when the foot points almost vertically.
				var w = Math.Clamp( (length - .15f) / .25f, 0, 1 );
				heading = heading * w + alternative * (1 - w);
				heading /= MathF.Max( heading.Length(), 1e-12f );
				if ( length < 1e-7f ) heading = Unit( fallback );
				var angle = MathF.Atan2( dot, length );
				var limit = swing + (stance - swing) * weight[t];
				var desiredAngle = SoftTilt( angle, limit );
				var target = heading * MathF.Cos( desiredAngle ) + normal * MathF.Sin( desiredAngle );
				var axis = Vector3.Cross( direction, target );
				var sine = axis.Length();
				var cosine = Math.Clamp( Vector3.Dot( direction, target ), -1f, 1f );
				axis *= MathF.Atan2( sine, cosine ) / MathF.Max( sine, 1e-12f );
				desired[t] = FromRotvec( axis ) * m.Rot[t][j];
			}
			// Avoid yaw flips when a near-vertical toe changes its horizontal sign.
			for ( var t = 1; t < n; t++ )
			{
				var raw = Magnitude( T( m.Rot[t - 1][j] ) * m.Rot[t][j] );
				var cap = MathF.Max( 8f * MathF.PI / 180f, raw + 5f * MathF.PI / 180f );
				var step = ToRotvec( T( desired[t - 1] ) * desired[t] );
				var stepAngle = step.Length();
				if ( stepAngle > cap ) desired[t] = desired[t - 1] * FromRotvec( step * cap / stepAngle );
			}
			for ( var t = 0; t < n; t++ )
			{
				local[t][j] = T( m.Rot[t][parent] ) * desired[t];
				(m.Pos[t], m.Rot[t]) = Fk( RootsOf( m.Pos[t], s.Parents ), local[t], m.Offset[t], s.Parents );
			}
		}
	}

	/// <summary>
	/// The port's gait planner (GroundedStances): UniMate's paws glide - a fox's never stop while its body travels, its
	/// legs barely sweeping. At each touch-down of such a paw (the lowest point of its path) it eases off its own path
	/// onto the spot where the model puts it down, stands there while its leg reaches, and eases back onto its path for
	/// the swing: the model's own footfalls and cadence, with a stance in each stride and the speed continuous throughout.
	/// The body is lowered a little where the legs are too straight to stand at all. Per foot and frame: the target, or
	/// none (the add-on's stance windows then apply).
	/// </summary>
	static (Vector3?[] Target, float[] Weight)[] PlanGait( Vector3[][] sole, Vector3[][] made, Vector3[][] hip, float[][] lift, bool[][] held, List<Foot> feet, Vector3 normal, float height, float[] bodySpeed, out float[] drop )
	{
		const int Blend = 3, Stand = 3; // at most: frames easing on and off, frames standing either side of the touch-down
		var n = sole[0].Length;
		Vector3 Flat( Vector3 v ) => v - normal * Vector3.Dot( v, normal );
		Vector3 Grounded( Vector3 p ) { var z = Vector3.Dot( p, normal ); return z < height ? p + normal * (height - z) : p; }
		float Reach( int f ) => .97f * (feet[f].Chain + feet[f].Offset.Length());
		var downs = new List<(int Foot, int Time, Vector3 Spot)>();
		for ( var f = 0; f < feet.Count; f++ )
		{
			var leg = feet[f].Profile.LegLength;
			var h = lift[f];
			// touch-downs: the lowest point of the path within a third of a second either way, in its lower part
			var floor = Enumerable.Range( 0, n ).Select( t => Enumerable.Range( Math.Max( 0, t - 15 ), Math.Min( n, t + 16 ) - Math.Max( 0, t - 15 ) ).Min( u => h[u] ) ).ToArray();
			var times = Enumerable.Range( 0, n ).Where( t => h[t] <= floor[t] + .15f * leg
				&& Enumerable.Range( Math.Max( 0, t - 5 ), Math.Min( n, t + 6 ) - Math.Max( 0, t - 5 ) ).All( u => h[u] > h[t] || (h[u] == h[t] && u >= t) ) ).ToList();
			// (as the model made it: smoothing smears a standing foot into a glide)
			float Speed( int t ) => t == 0 ? Flat( made[f][1] - made[f][0] ).Length() : Flat( made[f][t] - made[f][t - 1] ).Length();
			// a paw that glides: it never stands (its three slowest frames over the stride around) while the body travels
			foreach ( var t0 in times )
			{
				if ( held[f][t0] ) continue; // the add-on's stance holds it
				int a = Math.Max( 0, t0 - 10 ), b = Math.Min( n - 1, t0 + 10 );
				if ( Enumerable.Range( a, b - a + 1 ).Average( t => bodySpeed[t] ) <= .02f * leg ) continue;
				if ( Enumerable.Range( a, b - a + 1 ).Select( t => Speed( t ) / MathF.Max( bodySpeed[t], 1e-6f ) ).OrderBy( x => x ).Take( 3 ).Average() <= .3f ) continue;
				var z = Vector3.Dot( sole[f][t0], normal );
				downs.Add( (f, t0, sole[f][t0] + normal * (MathF.Max( z, height ) - z)) );
			}
		}
		// legs too straight to stand: the body lowered (by a quarter of the shortest leg at most) as far as the spots need
		var minLeg = feet.Min( f => f.Profile.LegLength );
		var need = new float[n];
		foreach ( var (f, t0, spot) in downs )
			for ( var t = Math.Max( 0, t0 - Stand - Blend ); t <= Math.Min( n - 1, t0 + Stand + Blend ); t++ )
			{
				var across = Flat( spot - hip[f][t] ).Length();
				var up = Vector3.Dot( hip[f][t] - spot, normal );
				var reach = Reach( f ) * .98f;
				var d = across < reach ? up - MathF.Sqrt( reach * reach - across * across ) : up;
				need[t] = MathF.Max( need[t], Math.Clamp( d, 0, minLeg / 4f ) );
			}
		var lowered = GaussianNearest( MaximumNearest( need, 9 ), 3 );
		drop = lowered;
		var plans = new (Vector3?[] Target, float[] Weight)[feet.Count];
		for ( var f = 0; f < feet.Count; f++ )
		{
			plans[f] = (new Vector3?[n], new float[n]);
			// a lowered body: every paw kept where it was (on the ground at least), or held by its stance
			for ( var t = 0; t < n; t++ )
				if ( lowered[t] > 1e-4f * minLeg && !held[f][t] ) { plans[f].Target[t] = Grounded( sole[f][t] ); plans[f].Weight[t] = 1f; }
		}
		var stood = new List<(int Foot, int Start, int End)>();
		foreach ( var (f, t0, spot) in downs )
		{
			bool Reaches( int t ) => t < 0 || t >= n || (spot - (hip[f][t] - normal * lowered[t])).Length() <= Reach( f );
			// standing as long as the leg reaches the spot, easing on and off while it does (a galloping paw stands for a
			// tenth of a second)
			var fit = new[] { (3, 3), (2, 3), (1, 3), (2, 2), (1, 2), (0, 2) }.Select( x => ((int Stand, int Blend)?)x )
				.FirstOrDefault( x => Enumerable.Range( t0 - x.Value.Stand - x.Value.Blend, 2 * (x.Value.Stand + x.Value.Blend) + 1 ).All( Reaches ) );
			if ( fit is not { } window ) continue;
			stood.Add( (f, t0 - window.Stand - window.Blend, t0 + window.Stand + window.Blend) );
			var (target, weight) = plans[f];
			for ( var t = Math.Max( 0, t0 - window.Stand - window.Blend ); t <= Math.Min( n - 1, t0 + window.Stand + window.Blend ); t++ )
			{
				var off = Math.Abs( t - t0 ) - window.Stand;
				var on = off <= 0 ? 1f : Ease( 1f - off / (float)window.Blend );
				var own = target[t] ?? Grounded( sole[f][t] );
				// descending onto the spot and rising off it: on the ground only while it stands
				var air = .1f * feet[f].Profile.LegLength * (1f - on);
				var blended = Vector3.Lerp( own, spot, on );
				var under = height + air - Vector3.Dot( blended, normal );
				target[t] = under > 0 ? blended + normal * under : blended;
				weight[t] = 1f;
			}
		}
		// between one stance and the next the paw is in the air - it touches down where it stands, not along the way
		foreach ( var group in stood.GroupBy( x => x.Foot ) )
		{
			var f = group.Key;
			var clearance = .1f * feet[f].Profile.LegLength;
			var (target, weight) = plans[f];
			var list = group.OrderBy( x => x.Start ).ToList();
			for ( var i = 0; i + 1 < list.Count; i++ )
			{
				int a = list[i].End, b = list[i + 1].Start;
				if ( b - a < 2 ) continue;
				for ( var t = a + 1; t < b; t++ )
				{
					// lifted off within a tenth of a second of either stance
					var arc = Ease( MathF.Min( 1f, (t - a) / 3f ) ) * Ease( MathF.Min( 1f, (b - t) / 3f ) );
					var own = target[t] ?? Grounded( sole[f][t] );
					var clear = height + clearance * arc - Vector3.Dot( own, normal );
					target[t] = clear > 0 ? own + normal * clear : own;
					weight[t] = 1f;
				}
			}
		}
		// the planned paths low-passed a little (a frame's sigma), so a paw lands and lifts off without a jolt
		const float Sigma = 1.5f;
		for ( var f = 0; f < feet.Count; f++ )
		{
			var (target, _) = plans[f];
			foreach ( var (a, b) in Runs( target.Select( x => x.HasValue ).ToArray() ) )
			{
				var xs = GaussianNearest( Enumerable.Range( a, b - a ).Select( t => target[t].Value.X ).ToArray(), Sigma );
				var ys = GaussianNearest( Enumerable.Range( a, b - a ).Select( t => target[t].Value.Y ).ToArray(), Sigma );
				var zs = GaussianNearest( Enumerable.Range( a, b - a ).Select( t => target[t].Value.Z ).ToArray(), Sigma );
				for ( var t = a; t < b; t++ ) target[t] = new Vector3( xs[t - a], ys[t - a], zs[t - a] );
			}
		}
		return plans;
	}

	/// <summary>Stance frames whose anchor was out of the leg's reach in the last clean-up (diagnostics).</summary>
	[ThreadStatic] public static int OutOfReach;

	static int SolveContacts( Motion m, Skeleton s, List<Foot> feet, Vector3 normal, float height, List<List<(int Start, int End)>> windows, Motion found,
		float[][] lifts, float[] bodySpeed )
	{
		var n = m.Frames;
		// anchors_for: the sole where each stance starts, on the ground
		var anchors = new Dictionary<(int Foot, int Start, int End), Vector3>();
		for ( var f = 0; f < feet.Count; f++ )
			foreach ( var (start, end) in windows[f] )
			{
				// the add-on anchors where the stance starts; a gliding foot (GroundedStances) is anchored where it is on
				// average, so the leg is turned by half as much at either end
				var b = Sole( found.Pos[start][feet[f].Profile.Joint], found.Rot[start][feet[f].Profile.Joint], feet[f], normal );
				if ( s.GroundedStances )
				{
					b = Vector3.Zero;
					for ( var t = start; t < end; t++ ) b += Sole( found.Pos[t][feet[f].Profile.Joint], found.Rot[t][feet[f].Profile.Joint], feet[f], normal );
					b /= end - start;
				}
				anchors[(f, start, end)] = b + normal * (height - Vector3.Dot( b, normal ));
			}
		(Vector3?[] Target, float[] Weight)[] plan = null;
		if ( s.GroundedStances )
		{
			var soles = feet.Select( foot => Enumerable.Range( 0, n ).Select( t => Sole( m.Pos[t][foot.Profile.Joint], m.Rot[t][foot.Profile.Joint], foot, normal ) ).ToArray() ).ToArray();
			var hips = feet.Select( foot => Enumerable.Range( 0, n ).Select( t => m.Pos[t][foot.Top] ).ToArray() ).ToArray();
			var made = feet.Select( foot => Enumerable.Range( 0, n ).Select( t => Sole( found.Pos[t][foot.Profile.Joint], found.Rot[t][foot.Profile.Joint], foot, normal ) ).ToArray() ).ToArray();
			// the add-on's stances whose anchor the leg reaches throughout
			var held = feet.Select( _ => new bool[n] ).ToArray();
			for ( var f = 0; f < feet.Count; f++ )
				foreach ( var (start, end) in windows[f] )
				{
					var anchor = anchors[(f, start, end)];
					if ( Enumerable.Range( start, end - start ).All( t => (anchor - m.Pos[t][feet[f].Profile.Upper]).Length() <= feet[f].Profile.LegLength + feet[f].Offset.Length() * 1.1f ) )
						for ( var t = start; t < end; t++ ) held[f][t] = true;
				}
			plan = PlanGait( soles, made, hips, lifts, held, feet, normal, height, bodySpeed, out var drop );
			for ( var t = 0; t < n; t++ )
				for ( var j = 0; j < s.Count; j++ ) m.Pos[t][j] -= normal * drop[t];
		}
		var original = m.Rot.Select( r => r.ToArray() ).ToArray();
		var local = ToLocal( m.Rot, s.Parents );
		var startPoints = m.Pos.Select( p => p.ToArray() ).ToArray();
		var outP = new Vector3[n][]; var outR = new Quaternion[n][];
		var previousCorrection = Enumerable.Repeat( Quaternion.Identity, s.Count ).ToArray();
		var active = 0;
		for ( var t = 0; t < n; t++ )
		{
			var constraints = new List<(int Foot, Vector3 Anchor, float Weight)>();
			var planned = new bool[feet.Count];
			for ( var f = 0; f < feet.Count && plan is not null; f++ )
			{
				if ( plan[f].Target[t] is not { } goal || plan[f].Weight[t] <= 0 ) continue;
				planned[f] = true;
				var leg = feet[f].Profile.LegLength;
				var hip = startPoints[t][feet[f].Top];
				// out of the leg's reach: as near as it reaches (the paw gives way smoothly rather than snapping free)
				var limit = feet[f].Chain + feet[f].Offset.Length() * 1.1f;
				if ( (goal - hip).Length() > limit ) { OutOfReach++; goal = hip + (goal - hip) * (limit / (goal - hip).Length()); }
				if ( t > 0 )
				{
					var previous = Sole( outP[t - 1][feet[f].Profile.Joint], outR[t - 1][feet[f].Profile.Joint], feet[f], normal );
					var approach = goal - previous;
					var length = approach.Length();
					var maxMove = .6f * leg; // a planned paw moves faster than the add-on's settling of a plant
					if ( length > maxMove ) goal = previous + approach * (maxMove / length);
				}
				constraints.Add( (f, goal, plan[f].Weight[t]) );
			}
			for ( var f = 0; f < feet.Count; f++ )
				foreach ( var (start, end) in windows[f] )
				{
					if ( planned[f] ) break;
					if ( !(start <= t && t < end) ) continue;
					var leg = feet[f].Profile.LegLength;
					var anchor = anchors[(f, start, end)];
					var hip = startPoints[t][feet[f].Profile.Upper];
					if ( (anchor - hip).Length() > leg + feet[f].Offset.Length() * 1.1f ) { OutOfReach++; continue; }
					var weight = MathF.Min( 1f, MathF.Min( (t - start + 1) / 4f, (end - t) / 4f ) );
					if ( t > 0 )
					{
						var previous = Sole( outP[t - 1][feet[f].Profile.Joint], outR[t - 1][feet[f].Profile.Joint], feet[f], normal );
						var approach = anchor - previous;
						var length = approach.Length();
						var maxMove = .045f * leg;
						if ( length > maxMove ) anchor = previous + approach * (maxMove / length);
					}
					constraints.Add( (f, anchor, weight) );
					break;
				}
			if ( constraints.Count > 0 ) active++;
			var jointWeights = Enumerable.Repeat( .88f, s.Count ).ToArray();
			foreach ( var (f, _, weight) in constraints )
				foreach ( var joint in feet[f].Joints )
					jointWeights[joint] = MathF.Max( jointWeights[joint], weight );
			var poseLocal = new Quaternion[s.Count];
			for ( var j = 0; j < s.Count; j++ ) poseLocal[j] = FromRotvec( ToRotvec( previousCorrection[j] ) * jointWeights[j] ) * local[t][j];
			var roots = RootsOf( m.Pos[t], s.Parents );
			var (pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
			// Gradually level a planted sole before solving its ground position (a flat ground: its normal is up).
			foreach ( var (f, _, weight) in constraints )
			{
				var j = feet[f].Profile.Joint;
				var oldUp = Mul( rot[j], normal );
				var axis = Vector3.Cross( oldUp, normal );
				var sine = axis.Length();
				var turn = MathF.Atan2( sine, Math.Clamp( Vector3.Dot( oldUp, normal ), -1f, 1f ) );
				if ( sine > 1e-7f )
				{
					var delta = FromRotvec( axis / sine * turn * weight );
					var parent = s.Parents[j];
					poseLocal[j] = Quaternion.Normalize( T( rot[parent] ) * delta * rot[parent] * poseLocal[j] );
					(pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
				}
			}
			for ( var iteration = 0; iteration < (constraints.Count > 0 ? 20 : 0); iteration++ )
			{
				var joints = constraints.SelectMany( c => feet[c.Foot].Joints ).Distinct().OrderBy( j => j ).ToList();
				var columns = joints.Select( ( j, k ) => (j, k) ).ToDictionary( x => x.j, x => 3 * x.k );
				var rows = 3 * constraints.Count; var cols = 3 * joints.Count + 3;
				var matrix = new double[rows, cols];
				var error = new double[rows];
				var legMin = constraints.Min( c => feet[c.Foot].Profile.LegLength );
				for ( var c = 0; c < constraints.Count; c++ )
				{
					var (f, target, weight) = constraints[c];
					var point = Sole( pos[feet[f].Profile.Joint], rot[feet[f].Profile.Joint], feet[f], normal );
					var e = (target - point) * weight;
					error[c * 3] = e.X; error[c * 3 + 1] = e.Y; error[c * 3 + 2] = e.Z;
					foreach ( var (joint, gain) in feet[f].Joints.Zip( new[] { .25f, .7f, 1f, 1f } ) )
					{
						// -skew(r) * gain: the change of the point for a rotation omega is omega x r = -skew(r) omega
						var r = point - pos[joint];
						var col = columns[joint];
						double[,] skew = { { 0, -r.Z, r.Y }, { r.Z, 0, -r.X }, { -r.Y, r.X, 0 } };
						for ( var a = 0; a < 3; a++ ) for ( var b = 0; b < 3; b++ ) matrix[c * 3 + a, col + b] = -skew[a, b] * gain;
					}
				}
				if ( error.Max( Math.Abs ) < .001 * legMin ) break;
				var damping = Math.Pow( .025 * legMin, 2 );
				var step = DampedStep( matrix, error, damping );
				var footJoints = constraints.Select( c => feet[c.Foot].Profile.Joint ).ToHashSet();
				var parentJoints = constraints.Select( c => feet[c.Foot].Profile.Parent ).ToHashSet();
				foreach ( var joint in joints )
				{
					var col = columns[joint];
					var omega = new Vector3( (float)step[col], (float)step[col + 1], (float)step[col + 2] );
					// The Jacobian columns were scaled to favor proximal joints.
					if ( footJoints.Contains( joint ) ) omega *= .25f;
					else if ( parentJoints.Contains( joint ) ) omega *= .7f;
					var norm = omega.Length();
					var max = 6f * MathF.PI / 180f;
					if ( norm > max ) omega *= max / norm;
					var parent = s.Parents[joint];
					var delta = FromRotvec( omega );
					poseLocal[joint] = Quaternion.Normalize( parent < 0 ? delta * poseLocal[joint] : T( rot[parent] ) * delta * rot[parent] * poseLocal[joint] );
				}
				// (the root columns are zero in the add-on, so its root never moves here)
				(pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
			}
			if ( t > 0 )
			{
				var projected = new Quaternion[s.Count];
				var affected = new bool[s.Count];
				for ( var joint = 0; joint < s.Count; joint++ )
				{
					var parent = s.Parents[joint];
					var originalStep = Magnitude( T( original[t - 1][joint] ) * original[t][joint] );
					var previousAngle = ToRotvec( previousCorrection[joint] ).Length();
					var correctionAngle = ToRotvec( poseLocal[joint] * T( local[t][joint] ) ).Length();
					affected[joint] = correctionAngle > 1e-5f || previousAngle > 1e-5f || (parent >= 0 && affected[parent]);
					var candidate = parent >= 0 ? projected[parent] * poseLocal[joint] : poseLocal[joint];
					if ( affected[joint] )
					{
						var change = ToRotvec( T( outR[t - 1][joint] ) * candidate );
						var angle = change.Length();
						var cap = MathF.Min( 45f * MathF.PI / 180f, MathF.Max( 5f * MathF.PI / 180f, originalStep + 3f * MathF.PI / 180f ) );
						if ( angle > cap )
						{
							candidate = outR[t - 1][joint] * FromRotvec( change * cap / angle );
							poseLocal[joint] = parent >= 0 ? T( projected[parent] ) * candidate : candidate;
						}
					}
					projected[joint] = candidate;
				}
				(pos, rot) = Fk( roots, poseLocal, m.Offset[t], s.Parents );
			}
			outP[t] = pos; outR[t] = rot;
			for ( var j = 0; j < s.Count; j++ ) previousCorrection[j] = poseLocal[j] * T( local[t][j] );
		}
		for ( var t = 0; t < n; t++ ) { m.Pos[t] = outP[t]; m.Rot[t] = outR[t]; }
		return active;
	}

	/// <summary>step = M^T (M M^T + damping I)^-1 e.</summary>
	static double[] DampedStep( double[,] m, double[] e, double damping )
	{
		int rows = m.GetLength( 0 ), cols = m.GetLength( 1 );
		var a = new double[rows, rows + 1];
		for ( var i = 0; i < rows; i++ )
		{
			for ( var j = 0; j < rows; j++ )
			{
				double sum = 0;
				for ( var k = 0; k < cols; k++ ) sum += m[i, k] * m[j, k];
				a[i, j] = sum + (i == j ? damping : 0);
			}
			a[i, rows] = e[i];
		}
		for ( var c = 0; c < rows; c++ )
		{
			var pivot = c;
			for ( var r = c + 1; r < rows; r++ ) if ( Math.Abs( a[r, c] ) > Math.Abs( a[pivot, c] ) ) pivot = r;
			for ( var k = 0; k <= rows; k++ ) (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
			for ( var r = 0; r < rows; r++ )
			{
				if ( r == c || a[c, c] == 0 ) continue;
				var f = a[r, c] / a[c, c];
				for ( var k = c; k <= rows; k++ ) a[r, k] -= f * a[c, k];
			}
		}
		var y = new double[rows];
		for ( var i = 0; i < rows; i++ ) y[i] = a[i, i] == 0 ? 0 : a[i, rows] / a[i, i];
		var step = new double[cols];
		for ( var k = 0; k < cols; k++ ) { double sum = 0; for ( var i = 0; i < rows; i++ ) sum += m[i, k] * y[i]; step[k] = sum; }
		return step;
	}

	/// <summary>preserve_bend: keep a two-bone knee on the side the generated pose chose.</summary>
	static int PreserveBend( Motion m, Skeleton s, List<Foot> feet, Vector3[][] source )
	{
		static Quaternion Align( Vector3 start, Vector3 target )
		{
			var a = Unit( start ); var b = Unit( target );
			var axis = Vector3.Cross( a, b );
			var sine = axis.Length();
			var cosine = Math.Clamp( Vector3.Dot( a, b ), -1f, 1f );
			if ( sine < 1e-9f )
			{
				if ( cosine > 0 ) return Quaternion.Identity;
				var abs = Vector3.Abs( a );
				var helper = abs.X <= abs.Y && abs.X <= abs.Z ? Vector3.UnitX : abs.Y <= abs.Z ? Vector3.UnitY : Vector3.UnitZ;
				axis = Unit( Vector3.Cross( a, helper ) );
			}
			else axis /= sine;
			return FromRotvec( axis * MathF.Atan2( sine, cosine ) );
		}
		var local = ToLocal( m.Rot, s.Parents );
		var total = 0;
		foreach ( var foot in feet )
		{
			int upper = foot.Profile.Upper, lower = foot.Profile.Parent, ankle = foot.Profile.Joint;
			var lengthA = (s.Heads[lower] - s.Heads[upper]).Length();
			var lengthB = (s.Heads[ankle] - s.Heads[lower]).Length();
			var leg = lengthA + lengthB;
			for ( var t = 0; t < m.Frames; t++ )
			{
				Vector3 h = m.Pos[t][upper], k = m.Pos[t][lower], a = m.Pos[t][ankle];
				var direction = a - h;
				var reach = direction.Length();
				if ( !(1e-7f < reach && reach < leg - 1e-7f) ) continue;
				direction /= reach;
				Vector3 sh = source[t][upper], sk = source[t][lower], sa = source[t][ankle];
				var sourceAxis = Unit( sa - sh );
				var sourcePole = sk - sh - sourceAxis * Vector3.Dot( sk - sh, sourceAxis );
				if ( sourcePole.Length() < .002f * leg ) continue;
				var pole = sourcePole - direction * Vector3.Dot( sourcePole, direction );
				if ( pole.Length() < 1e-7f ) continue;
				pole = Unit( pole );
				var along = (lengthA * lengthA - lengthB * lengthB + reach * reach) / (2 * reach);
				var radius = MathF.Sqrt( MathF.Max( lengthA * lengthA - along * along, 0 ) );
				if ( radius < 1e-7f ) continue;
				var current = k - h - direction * Vector3.Dot( k - h, direction );
				if ( Vector3.Dot( current, pole ) >= .7f * radius ) continue;
				var targetK = h + direction * along + pole * radius;
				var upperNew = Align( k - h, targetK - h ) * m.Rot[t][upper];
				var lowerNew = Align( a - k, a - targetK ) * m.Rot[t][lower];
				var upperParent = s.Parents[upper];
				local[t][upper] = upperParent >= 0 ? T( m.Rot[t][upperParent] ) * upperNew : upperNew;
				local[t][lower] = T( upperNew ) * lowerNew;
				local[t][ankle] = T( lowerNew ) * m.Rot[t][ankle];
				(m.Pos[t], m.Rot[t]) = Fk( RootsOf( m.Pos[t], s.Parents ), local[t], m.Offset[t], s.Parents );
				total++;
			}
		}
		return total;
	}

	/// <summary>
	/// The add-on's skeleton for an engine rig, as its exporter makes one (rig.py export_skeleton): the bones that deform
	/// the mesh and their ancestors, one tree - a rig's IK helpers and attachments are left to their own motion. Skinned
	/// roots UniMate's preparation grafted onto the body hang off it here too. Labels: UniMate's on the joints it
	/// animates (what the add-on reads as a bone's unimate_label), the add-on's semantic_name otherwise.
	/// </summary>
	public static Skeleton ForRig( TextToAnimation.Animation.MotionRig rig, UniMateRig uni )
	{
		var sk = rig.Skeleton;
		var n = sk.Count;
		var names = sk.Bones.Select( b => b.Name ).ToArray();
		var labels = names.Select( SemanticName ).ToArray();
		if ( uni is not null )
			for ( var j = 0; j < uni.Count; j++ )
				if ( uni.Bone[j] >= 0 ) labels[uni.Bone[j]] = uni.Skeleton.CleanNames[j];
		var weights = UniMateSkin.WeightsOf( sk );
		var points = UniMateSkin.PointsOf( sk );
		bool Deforms( int j )
		{
			if ( weights is null && points is null ) return true;
			if ( points?.ContainsKey( j ) == true ) return true;
			return weights is not null && (weights.TryGetValue( names[j], out var w ) || weights.TryGetValue( Formats.Fbx.FbxClipExport.EngineName( names[j] ), out w )) && w.Max >= UniMatePrep.SkinEps;
		}
		var grafts = uni?.Prep?.Grafts ?? new Dictionary<int, int>();
		int ParentOf( int j ) => grafts.TryGetValue( j, out var host ) ? host : sk[j].ParentIndex;
		int RootOf( int j ) { while ( ParentOf( j ) >= 0 ) j = ParentOf( j ); return j; }
		var selected = new HashSet<int>();
		for ( var j = 0; j < n; j++ )
			if ( Deforms( j ) ) for ( var k = j; k >= 0 && selected.Add( k ); k = ParentOf( k ) ) { }
		if ( selected.Count == 0 ) selected.UnionWith( Enumerable.Range( 0, n ) );
		// one tree (the add-on refuses several roots): the one UniMate's root is in
		var mainRoot = RootOf( uni?.RootBone ?? rig.RootIndex );
		if ( !selected.Contains( mainRoot ) ) mainRoot = selected.GroupBy( RootOf ).OrderByDescending( g => g.Count() ).First().Key;
		selected.RemoveWhere( j => RootOf( j ) != mainRoot );
		// parent before child (a grafted root may come before its host in the engine's order)
		var children = selected.Where( j => j != mainRoot ).ToLookup( ParentOf );
		var order = new List<int>();
		void Visit( int j ) { order.Add( j ); foreach ( var c in children[j].OrderBy( c => c ) ) Visit( c ); }
		Visit( mainRoot );
		var index = order.Select( ( e, k ) => (e, k) ).ToDictionary( x => x.e, x => x.k );
		var s = Build( order.Select( e => ParentOf( e ) is var p && p >= 0 ? index[p] : -1 ).ToArray(), order.Select( e => (Vector3)sk.RestWorld[e].Pos ).ToArray(),
			order.Select( e => names[e] ).ToArray(), order.Select( e => labels[e] ).ToArray(),
			points?.Where( kv => index.ContainsKey( kv.Key ) ).ToDictionary( kv => index[kv.Key], kv => kv.Value ), 0,
			order.Select( e => (Quaternion)sk.RestWorld[e].Rot ).ToArray(),
			rig.Feet.Select( f => f.Ankle ).Where( index.ContainsKey ).Select( b => index[b] ) );
		s.Engine = order.ToArray();
		s.IgnoreRestOverlaps = true;
		s.GroundedStances = true;
		return s;
	}

	/// <summary>The joints of <paramref name="s"/> (a subset of the engine's bones) out of an engine-wide motion, offsets from their own parents.</summary>
	public static Motion Subset( Motion full, Skeleton s )
	{
		var m = new Motion { Pos = new Vector3[full.Frames][], Rot = new Quaternion[full.Frames][], Offset = new Vector3[full.Frames][] };
		for ( var t = 0; t < full.Frames; t++ )
		{
			m.Pos[t] = s.Engine.Select( e => full.Pos[t][e] ).ToArray();
			m.Rot[t] = s.Engine.Select( e => full.Rot[t][e] ).ToArray();
			m.Offset[t] = new Vector3[s.Count];
			for ( var j = 0; j < s.Count; j++ )
				if ( s.Parents[j] >= 0 ) m.Offset[t][j] = Mul( T( m.Rot[t][s.Parents[j]] ), m.Pos[t][j] - m.Pos[t][s.Parents[j]] );
		}
		return m;
	}

	/// <summary>Engine frames (bone locals) in the add-on's form.</summary>
	public static Motion FromFrames( IReadOnlyList<TextToAnimation.Maths.XForm[]> frames, TextToAnimation.Rig.Skeleton sk )
	{
		var n = sk.Count;
		var rest = sk.RestWorld;
		var m = new Motion { Pos = new Vector3[frames.Count][], Rot = new Quaternion[frames.Count][], Offset = new Vector3[frames.Count][] };
		var world = new TextToAnimation.Maths.XForm[n];
		for ( var t = 0; t < frames.Count; t++ )
		{
			TextToAnimation.Processing.FkUtil.ToWorld( frames[t], sk, world );
			m.Pos[t] = new Vector3[n]; m.Rot[t] = new Quaternion[n]; m.Offset[t] = new Vector3[n];
			for ( var j = 0; j < n; j++ )
			{
				m.Pos[t][j] = world[j].Pos;
				m.Rot[t][j] = Quaternion.Normalize( world[j].Rot * T( rest[j].Rot ) );
			}
			for ( var j = 0; j < n; j++ )
			{
				var p = sk[j].ParentIndex;
				if ( p >= 0 ) m.Offset[t][j] = Mul( T( m.Rot[t][p] ), m.Pos[t][j] - m.Pos[t][p] );
			}
		}
		return m;
	}

	/// <summary>Back to engine frames: rotations from the motion, roots' positions from it, other local positions kept.</summary>
	public static void ToFrames( Motion m, List<TextToAnimation.Maths.XForm[]> frames, TextToAnimation.Rig.Skeleton sk, IReadOnlySet<int> changed = null )
	{
		var rest = sk.RestWorld;
		for ( var t = 0; t < frames.Count; t++ )
		{
			var frame = new TextToAnimation.Maths.XForm[sk.Count];
			for ( var j = 0; j < sk.Count; j++ )
			{
				if ( changed is not null && !changed.Contains( j ) ) { frame[j] = frames[t][j]; continue; }
				var p = sk[j].ParentIndex;
				var world = Quaternion.Normalize( m.Rot[t][j] * rest[j].Rot );
				if ( p < 0 ) { frame[j] = new TextToAnimation.Maths.XForm( m.Pos[t][j], world ); continue; }
				var parentWorld = Quaternion.Normalize( m.Rot[t][p] * rest[p].Rot );
				frame[j] = new TextToAnimation.Maths.XForm( frames[t][j].Pos, Quaternion.Normalize( T( parentWorld ) * world ) );
			}
			frames[t] = frame;
		}
	}

	/// <summary>
	/// UniMate's clean-up on generated engine frames, in place: inertial joins at <paramref name="joins"/> (frames where
	/// a later prompt or chained window begins), then collisions, ground contact, collisions. Returns what it did.
	/// </summary>
	public static string Apply( List<TextToAnimation.Maths.XForm[]> frames, TextToAnimation.Animation.MotionRig rig, UniMateRig uni,
		IReadOnlyList<int> joins, int transitionFrames = 12 )
	{
		if ( frames.Count < 2 ) return "";
		var s = ForRig( rig, uni );
		var full = FromFrames( frames, rig.Skeleton );
		var m = Subset( full, s );
		string Invalid( string stage ) => Finite( m ) ? null : $"UniMate clean-up skipped: invalid numbers after {stage}.";
		if ( Invalid( "reading the clip" ) is { } bad ) return bad;
		if ( joins is { Count: > 0 } ) Joins( m, s, joins, transitionFrames );
		if ( Invalid( "the prompt joins" ) is { } badJoins ) return badJoins;
		// the port's addition: the model's frame-to-frame jitter low-passed before the clean-up corrects contacts
		var made = Copy( m );
		SmoothMotion( m, s, MotionSigma );
		var overlapBefore = Overlap( m, s );
		Collisions( m, s );
		if ( Invalid( "the collisions" ) is { } badFirst ) return badFirst;
		var ground = Plant( m, s, made );
		if ( Invalid( "the ground contact" ) is { } badGround ) return badGround;
		Collisions( m, s );
		if ( Invalid( "the second collision pass" ) is { } badSecond ) return badSecond;
		var overlapAfter = Overlap( m, s );
		for ( var t = 0; t < full.Frames; t++ )
			for ( var j = 0; j < s.Count; j++ ) { full.Pos[t][s.Engine[j]] = m.Pos[t][j]; full.Rot[t][s.Engine[j]] = m.Rot[t][j]; }
		ToFrames( full, frames, rig.Skeleton, s.Engine.ToHashSet() );
		var parts = new List<string>();
		if ( s.Capsules.Count == 0 ) parts.Add( "no body shapes (the model's FBX wasn't found), so no collisions or ground contact" );
		else
		{
			if ( overlapBefore > 0 ) parts.Add( $"body overlaps {(overlapBefore - overlapAfter) / MathF.Max( overlapBefore, 1e-6f ) * 100:0}% resolved" );
			parts.Add( ground );
		}
		return "UniMate clean-up: " + string.Join( "; ", parts ) + ".";
	}

	// ------------------------------------------------------------------ smoothing (the port's addition: the add-on has none)

	static float[] GaussianWeights( float sigma, out int radius )
	{
		radius = Math.Max( 1, (int)MathF.Ceiling( 3f * sigma ) );
		var r = radius;
		var w = Enumerable.Range( -r, 2 * r + 1 ).Select( k => MathF.Exp( -.5f * k * k / (sigma * sigma) ) ).ToArray();
		var total = w.Sum();
		return w.Select( x => x / total ).ToArray();
	}

	/// <summary>A Gaussian average of rotations (one hemisphere, normalised): small-angle exact enough for frame neighbours.</summary>
	static Quaternion Average( IReadOnlyList<Quaternion> q, float[] w )
	{
		var reference = q[q.Count / 2];
		var sum = System.Numerics.Vector4.Zero;
		for ( var k = 0; k < q.Count; k++ )
		{
			var v = new System.Numerics.Vector4( q[k].X, q[k].Y, q[k].Z, q[k].W );
			if ( Quaternion.Dot( q[k], reference ) < 0 ) v = -v;
			sum += v * w[k];
		}
		return Quaternion.Normalize( new Quaternion( sum.X, sum.Y, sum.Z, sum.W ) );
	}

	/// <summary>
	/// Low-pass of the motion itself: each joint's local rotation and each root's position averaged over neighbouring
	/// frames (Gaussian, edges held). Removes the model's frame-to-frame jitter before the clean-up corrects contacts.
	/// </summary>
	public static void SmoothMotion( Motion m, Skeleton s, float sigma )
	{
		if ( sigma <= 0 || m.Frames < 3 ) return;
		var w = GaussianWeights( sigma, out var r );
		var local = ToLocal( m.Rot, s.Parents );
		var n = m.Frames;
		var outLocal = new Quaternion[n][];
		var roots = new Vector3[n][];
		for ( var t = 0; t < n; t++ )
		{
			outLocal[t] = new Quaternion[s.Count];
			roots[t] = new Vector3[s.Count];
			for ( var j = 0; j < s.Count; j++ )
			{
				var window = Enumerable.Range( -r, 2 * r + 1 ).Select( k => local[Math.Clamp( t + k, 0, n - 1 )][j] ).ToList();
				outLocal[t][j] = Average( window, w );
				if ( s.Parents[j] < 0 )
				{
					var p = Vector3.Zero;
					for ( var k = -r; k <= r; k++ ) p += m.Pos[Math.Clamp( t + k, 0, n - 1 )][j] * w[k + r];
					roots[t][j] = p;
				}
			}
		}
		for ( var t = 0; t < n; t++ ) (m.Pos[t], m.Rot[t]) = Fk( roots[t], outLocal[t], m.Offset[t], s.Parents );
	}

	/// <summary>
	/// Low-pass of what a clean-up changed: per joint the local correction (after * before^-1) and per root the
	/// position correction, averaged over neighbouring frames and applied back to <paramref name="before"/>. A frame-
	/// by-frame solver's corrections twitch; their average keeps what they fix (limbs apart, feet down) without the twitch.
	/// </summary>
	public static void SmoothCorrections( Motion before, Motion after, Skeleton s, float sigma )
	{
		if ( sigma <= 0 || after.Frames < 3 ) return;
		var w = GaussianWeights( sigma, out var r );
		var lb = ToLocal( before.Rot, s.Parents );
		var la = ToLocal( after.Rot, s.Parents );
		var n = after.Frames;
		var correction = new Vector3[n][];
		var shift = new Vector3[n][];
		for ( var t = 0; t < n; t++ )
		{
			correction[t] = new Vector3[s.Count];
			shift[t] = new Vector3[s.Count];
			for ( var j = 0; j < s.Count; j++ )
			{
				correction[t][j] = ToRotvec( la[t][j] * T( lb[t][j] ) );
				if ( s.Parents[j] < 0 ) shift[t][j] = after.Pos[t][j] - before.Pos[t][j];
			}
		}
		for ( var t = 0; t < n; t++ )
		{
			var local = new Quaternion[s.Count];
			var roots = new Vector3[s.Count];
			for ( var j = 0; j < s.Count; j++ )
			{
				Vector3 c = Vector3.Zero, d = Vector3.Zero;
				for ( var k = -r; k <= r; k++ )
				{
					var f = Math.Clamp( t + k, 0, n - 1 );
					c += correction[f][j] * w[k + r];
					d += shift[f][j] * w[k + r];
				}
				local[j] = Quaternion.Normalize( FromRotvec( c ) * lb[t][j] );
				if ( s.Parents[j] < 0 ) roots[j] = before.Pos[t][j] + d;
			}
			(after.Pos[t], after.Rot[t]) = Fk( roots, local, before.Offset[t], s.Parents );
		}
	}

	/// <summary>
	/// The editor's clean-up: the model's jitter low-passed (<paramref name="motionSigma"/> frames), UniMate's own
	/// clean-up, then its frame-by-frame corrections low-passed (<paramref name="correctionSigma"/> frames).
	/// </summary>
	public static string Smoothed( Motion m, Skeleton s, float motionSigma = MotionSigma, float correctionSigma = CorrectionSigma )
	{
		var made = Copy( m );
		SmoothMotion( m, s, motionSigma );
		var before = Copy( m );
		var report = Clean( m, s, motionSigma > 0 ? made : null );
		SmoothCorrections( before, m, s, correctionSigma );
		return report;
	}

	/// <summary>Measured (SmoothnessTests): smoothing the motion halves jerk and keeps every fix; smoothing the corrections brings overlaps back.</summary>
	public const float MotionSigma = 2.5f, CorrectionSigma = 0f;

	public static Motion Copy( Motion m ) => new()
	{
		Pos = m.Pos.Select( f => f.ToArray() ).ToArray(), Rot = m.Rot.Select( f => f.ToArray() ).ToArray(), Offset = m.Offset.Select( f => f.ToArray() ).ToArray(),
	};

	/// <summary>The deepest capsule overlap over the clip (0 without shapes).</summary>
	public static float Overlap( Motion m, Skeleton s )
	{
		if ( s.Capsules.Count == 0 ) return 0;
		var rig = new CapsuleRig( s );
		var worst = 0f;
		for ( var t = 0; t < m.Frames; t++ ) worst = MathF.Max( worst, rig.Penetration( m.Pos[t], m.Rot[t] ) );
		return worst;
	}

	static bool Finite( Motion m )
	{
		foreach ( var f in m.Pos ) foreach ( var p in f ) if ( !float.IsFinite( p.X + p.Y + p.Z ) ) return false;
		foreach ( var f in m.Rot ) foreach ( var q in f ) if ( !float.IsFinite( q.X + q.Y + q.Z + q.W ) ) return false;
		return true;
	}

	/// <summary>collision.py cleanup: collisions, ground contact, collisions again.</summary>
	public static string Clean( Motion m, Skeleton s, Motion reference = null )
	{
		var first = Collisions( m, s );
		var ground = Plant( m, s, reference );
		var second = Collisions( m, s );
		return $"{first}; {ground}; {second}";
	}
}
