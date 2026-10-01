using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.Editor.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>
/// A skeleton prepared for UniMate: joints in UniMate's BFS order with parents, the canonical T-pose
/// (Y-up, facing +Z, tree diameter 2), the transform back to source space, cleaned joint names and the
/// topology tensors the denoiser conditions on. Built from rest world positions/rotations in source
/// (engine) coordinates. Port of unimate_ref.canonicalize_rest / build_cond and topology_utils.
/// </summary>
public sealed class UniMateSkeleton
{
	public int Count { get; private init; }
	/// <summary>For each joint (BFS order): index into the caller's joint list.</summary>
	public int[] SourceIndex { get; private init; }
	public int[] Parents { get; private init; }
	public string[] CleanNames { get; private init; }
	/// <summary>Rest world rotations in source coordinates (BFS order).</summary>
	public Quaternion[] RestWorldRot { get; private init; }
	public Vector3[] RestWorldPos { get; private init; }

	/// <summary>Canonical T-pose joint positions (J,3).</summary>
	public Vector3[] TPose { get; private init; }
	public Vector3[] Offsets { get; private init; }
	/// <summary>canonical = Scale * (M * p_src - Origin).</summary>
	public M3 M { get; private init; }
	public Quaternion MQuat { get; private init; }
	public Vector3 Origin { get; private init; }
	public float Scale { get; private init; }
	/// <summary>Face joints (BFS indices): the right/left pair, or head/tail when <see cref="BodyAxis"/>; -1 = no facing (identity).</summary>
	public int RightHip { get; private init; } = -1;
	public int LeftHip { get; private init; } = -1;
	/// <summary>The face joints are the head and tail ends of a body without left/right (a snake): forward is head - tail.</summary>
	public bool BodyAxis { get; private init; }

	/// <summary>UniMate's facing from the face joints at one frame (canonical Y-up positions): rotation taking forward to +Z.</summary>
	public static Quaternion FacingFrom( Vector3 right, Vector3 left, bool bodyAxis )
	{
		var across = right - left;
		across /= MathF.Max( across.Length(), 1e-8f );
		var fwd = Vector3.Cross( Vector3.UnitY, across );
		fwd /= MathF.Max( fwd.Length(), 1e-8f );
		if ( bodyAxis ) fwd = Vector3.Transform( fwd, BodyAxisCorrection );
		return UniMateMath.Between( fwd, Vector3.UnitZ );
	}

	/// <summary>Upstream's -90° rotation about Y that turns a body-axis "across" into the forward direction.</summary>
	static readonly Quaternion BodyAxisCorrection = Quaternion.CreateFromAxisAngle( Vector3.UnitY, -MathF.PI / 2f );

	public long[,] Relations { get; private init; }
	public long[,] GraphDist { get; private init; }
	public long[] Depths { get; private init; }
	/// <summary>Laplacian eigenvector features (J,8).</summary>
	public float[,] Spectral { get; private set; }

	/// <summary>s&amp;box engine space (Z-up, X forward, Y left) to UniMate's Y-up frame: (x,y,z) -> (x, z, -y).</summary>
	public static readonly M3 EngineUpBasis = M3.FromRows( 1, 0, 0, 0, 0, 1, 0, -1, 0 );

	/// <summary>
	/// s&amp;box engine space to UniMate's canonical frame as upstream's Blender pipeline would see the same model:
	/// (x,y,z) -> (y, z, x). Engine forward (+X) becomes +Z, the canonical forward, as a Blender-authored rig's -Y
	/// does after upstream's Z-up -> Y-up step; it only matters when a rig has no facing joints (identity facing).
	/// </summary>
	public static readonly M3 EngineCanonicalBasis = M3.FromRows( 0, 1, 0, 0, 0, 1, 1, 0, 0 );

	/// <summary>Blender (Z-up) to Y-up: upstream's apply_zup_to_yup, a -90 degree rotation about X.</summary>
	public static readonly M3 BlenderUpBasis = M3.FromRows( 1, 0, 0, 0, 0, 1, 0, -1, 0 );

	/// <summary>
	/// Builds the skeleton. <paramref name="parents"/> index into the same arrays (-1 for the single root).
	/// <paramref name="rightHip"/>/<paramref name="leftHip"/> (caller indices) define the facing; when absent,
	/// <paramref name="forward"/> (source space) is used.
	/// </summary>
	public static UniMateSkeleton Build( IReadOnlyList<string> cleanNames, IReadOnlyList<int> parents,
		IReadOnlyList<Vector3> restWorldPos, IReadOnlyList<Quaternion> restWorldRot,
		int rightHip, int leftHip, Vector3? forward, M3 upBasis, float targetDiameter = 2f, bool bodyAxis = false )
	{
		var n = parents.Count;
		if ( n < 2 ) throw new ArgumentException( "UniMate needs at least 2 joints." ); // upstream builds any tree; the 5-joint training minimum is checked before generating
		if ( parents.Count( p => p < 0 ) != 1 ) throw new ArgumentException( "The UniMate skeleton must have exactly one root." );

		var (order, newParents) = BfsOrder( parents, restWorldPos );
		var oldToNew = new int[n];
		for ( var i = 0; i < n; i++ ) oldToNew[order[i]] = i;
		var pos = order.Select( o => restWorldPos[o] ).ToArray();
		var rot = order.Select( o => Quaternion.Normalize( restWorldRot[o] ) ).ToArray();
		var names = order.Select( o => cleanNames[o] ).ToArray();
		var rh = rightHip >= 0 ? oldToNew[rightHip] : -1;
		var lh = leftHip >= 0 ? oldToNew[leftHip] : -1;

		// facing in the up-aligned frame
		var p = pos.Select( v => upBasis * v ).ToArray();
		Vector3 fwd;
		if ( rh >= 0 && lh >= 0 && bodyAxis )
		{
			var across = Vector3.Normalize( p[rh] - p[lh] );
			fwd = Vector3.Transform( Vector3.Cross( Vector3.UnitY, across ), BodyAxisCorrection );
		}
		else if ( rh >= 0 && lh >= 0 )
		{
			var across = Vector3.Normalize( p[rh] - p[lh] );
			fwd = Vector3.Cross( Vector3.UnitY, across );
		}
		else if ( forward is { } f )
		{
			fwd = upBasis * f;
			fwd.Y = 0;
		}
		else
		{
			// upstream with no face joints: identity facing (the up-aligned rest is used as it is)
			fwd = Vector3.UnitZ;
		}
		fwd = Vector3.Normalize( fwd );
		var qf = UniMateMath.Between( fwd, Vector3.UnitZ );
		var m = M3.FromQuaternion( qf ) * upBasis;
		p = pos.Select( v => m * v ).ToArray();
		var diameter = TreeDiameter( newParents, p );
		// upstream process_tpose refuses such a skeleton (DegenerateSkeletonError) rather than divide by ~0
		if ( !float.IsFinite( diameter ) || diameter <= 1e-8f )
			throw new ArgumentException( $"This skeleton has no measurable size (leaf-to-leaf diameter {diameter:0.###e+0}; zero-length bones?), so UniMate can't scale it." );
		var scale = targetDiameter / diameter;
		var origin = new Vector3( p[0].X, p.Min( v => v.Y ), p[0].Z );
		var tpos = p.Select( v => (v - origin) * scale ).ToArray();
		var offsets = tpos.ToArray();
		for ( var j = 1; j < n; j++ ) offsets[j] = tpos[j] - tpos[newParents[j]];

		var (rel, dist) = RelationsAndDistances( newParents, 5 );
		var skel = new UniMateSkeleton
		{
			Count = n,
			SourceIndex = order,
			Parents = newParents,
			CleanNames = names,
			RestWorldRot = rot,
			RestWorldPos = pos,
			TPose = tpos,
			Offsets = offsets,
			M = m,
			MQuat = m.ToQuaternion(),
			Origin = origin,
			Scale = scale,
			RightHip = rh,
			LeftHip = lh,
			BodyAxis = bodyAxis && rh >= 0 && lh >= 0,
			Relations = rel,
			GraphDist = dist,
			Depths = JointDepths( newParents ),
		};
		skel.Spectral = LaplacianEigenvectors( newParents, 8 );
		return skel;
	}

	/// <summary>Overrides the spectral features (tests use the fixture's values; symmetric skeletons have
	/// degenerate eigenspaces where any basis is valid).</summary>
	public void SetSpectral( float[,] spectral ) => Spectral = spectral;

	/// <summary>BFS from the root, children sorted by (-subtree size, bone length), stable.</summary>
	public static (int[] Order, int[] Parents) BfsOrder( IReadOnlyList<int> parents, IReadOnlyList<Vector3> restWorld )
	{
		var n = parents.Count;
		var bone = new double[n];
		var children = Enumerable.Range( 0, n ).Select( _ => new List<int>() ).ToArray();
		var root = -1;
		for ( var j = 0; j < n; j++ )
		{
			var p = parents[j];
			if ( p < 0 ) root = j;
			else
			{
				children[p].Add( j );
				// double precision, like upstream's float64 offsets
				double dx = (double)restWorld[j].X - restWorld[p].X, dy = (double)restWorld[j].Y - restWorld[p].Y, dz = (double)restWorld[j].Z - restWorld[p].Z;
				bone[j] = Math.Sqrt( dx * dx + dy * dy + dz * dz );
			}
		}
		var topo = new List<int>();
		var q = new Queue<int>();
		q.Enqueue( root );
		while ( q.Count > 0 ) { var u = q.Dequeue(); topo.Add( u ); foreach ( var c in children[u] ) q.Enqueue( c ); }
		var size = Enumerable.Repeat( 1, n ).ToArray();
		for ( var i = topo.Count - 1; i >= 0; i-- ) foreach ( var c in children[topo[i]] ) size[topo[i]] += size[c];
		for ( var i = 0; i < n; i++ )
		{
			// bone lengths equal up to rounding (mirrored limbs) are one tie, kept in file order: siblings are grouped
			// into clusters of lengths 1e-6 (relative) apart, then ordered by (-subtree size, cluster, file order).
			// Upstream compares the raw floats, so its order there is decided by rounding noise.
			var byLen = children[i].OrderBy( c => bone[c] ).ToList();
			var cluster = new Dictionary<int, double>();
			for ( var k = 0; k < byLen.Count; k++ )
				cluster[byLen[k]] = k > 0 && bone[byLen[k]] - bone[byLen[k - 1]] <= 1e-6 * bone[byLen[k]] ? cluster[byLen[k - 1]] : bone[byLen[k]];
			children[i] = children[i].Select( ( c, k ) => (c, k) ).OrderBy( t => -size[t.c] ).ThenBy( t => cluster[t.c] ).ThenBy( t => t.k ).Select( t => t.c ).ToList();
		}
		var order = new List<int>();
		q.Enqueue( root );
		while ( q.Count > 0 ) { var u = q.Dequeue(); order.Add( u ); foreach ( var c in children[u] ) q.Enqueue( c ); }
		if ( order.Count != n ) throw new ArgumentException( "The UniMate skeleton isn't a single connected tree." );
		var oldToNew = new int[n];
		for ( var i = 0; i < n; i++ ) oldToNew[order[i]] = i;
		var newParents = order.Select( o => parents[o] < 0 ? -1 : oldToNew[parents[o]] ).ToArray();
		return (order.ToArray(), newParents);
	}

	/// <summary>Bone-length weighted tree diameter (two farthest-node searches from joint 0).</summary>
	public static float TreeDiameter( IReadOnlyList<int> parents, IReadOnlyList<Vector3> pos )
	{
		var n = parents.Count;
		var adj = Enumerable.Range( 0, n ).Select( _ => new List<(int, double)>() ).ToArray();
		for ( var j = 0; j < n; j++ )
		{
			var p = parents[j];
			if ( p < 0 ) continue;
			var l = (double)(pos[j] - pos[p]).Length();
			adj[p].Add( (j, l) ); adj[j].Add( (p, l) );
		}
		(int Node, double Dist) Far( int s )
		{
			var d = new Dictionary<int, double> { [s] = 0 };
			var queue = new List<int> { s };
			var best = s; var bd = 0.0;
			for ( var i = 0; i < queue.Count; i++ )
			{
				var u = queue[i];
				foreach ( var (v, w) in adj[u] )
				{
					if ( d.ContainsKey( v ) ) continue;
					d[v] = d[u] + w; queue.Add( v );
					if ( d[v] > bd ) { bd = d[v]; best = v; }
				}
			}
			return (best, bd);
		}
		var (u0, _) = Far( 0 );
		return (float)Far( u0 ).Dist;
	}

	public static long[] JointDepths( IReadOnlyList<int> parents )
	{
		var n = parents.Count;
		var depth = Enumerable.Repeat( -1L, n ).ToArray();
		for ( var j = 0; j < n; j++ ) if ( parents[j] < 0 || parents[j] == j ) depth[j] = 0;
		var changed = true;
		while ( changed )
		{
			changed = false;
			for ( var j = 0; j < n; j++ )
			{
				if ( depth[j] >= 0 ) continue;
				var p = parents[j];
				if ( p >= 0 && p < n && depth[p] >= 0 ) { depth[j] = depth[p] + 1; changed = true; }
			}
		}
		return depth;
	}

	/// <summary>0 self, 1 parent, 2 child, 3 sibling, 4 none, 5 end effector (diagonal); BFS hop distance clamped.</summary>
	public static (long[,] Rel, long[,] Dist) RelationsAndDistances( IReadOnlyList<int> parents, int maxPath )
	{
		var n = parents.Count;
		var rel = new long[n, n];
		var childCount = new int[n];
		for ( var j = 0; j < n; j++ ) if ( parents[j] >= 0 ) childCount[parents[j]]++;
		for ( var i = 0; i < n; i++ )
		{
			var pi = parents[i];
			for ( var j = 0; j < n; j++ )
			{
				var pj = parents[j];
				rel[i, j] = i == j ? 0 : pj == i ? 2 : (j == pi && pi != -1) ? 1 : (pi != -1 && pj == pi) ? 3 : 4;
			}
			if ( childCount[i] == 0 ) rel[i, i] = 5;
		}
		var adj = Enumerable.Range( 0, n ).Select( _ => new List<int>() ).ToArray();
		for ( var j = 0; j < n; j++ ) if ( parents[j] >= 0 ) { adj[j].Add( parents[j] ); adj[parents[j]].Add( j ); }
		var dist = new long[n, n];
		for ( var s = 0; s < n; s++ )
		{
			var d = Enumerable.Repeat( int.MaxValue, n ).ToArray();
			d[s] = 0;
			var q = new Queue<int>();
			q.Enqueue( s );
			while ( q.Count > 0 )
			{
				var u = q.Dequeue();
				if ( d[u] >= maxPath ) continue;
				foreach ( var v in adj[u] )
					if ( d[v] > d[u] + 1 ) { d[v] = d[u] + 1; q.Enqueue( v ); }
			}
			for ( var j = 0; j < n; j++ ) dist[s, j] = Math.Min( d[j], maxPath );
		}
		return (rel, dist);
	}

	/// <summary>
	/// Smallest non-trivial eigenvectors of the symmetric normalised Laplacian I - D^-1/2 A D^-1/2 (Jacobi
	/// eigensolver, ascending), each column L2-normalised, zero padded to <paramref name="maxFreqs"/>.
	/// </summary>
	public static float[,] LaplacianEigenvectors( IReadOnlyList<int> parents, int maxFreqs )
	{
		var n = parents.Count;
		var k = Math.Min( n - 1, maxFreqs );
		var a = new double[n, n];
		var degree = new double[n];
		for ( var j = 0; j < n; j++ )
		{
			var p = parents[j];
			if ( p < 0 || p == j ) continue;
			a[j, p] = a[p, j] = 1; degree[j]++; degree[p]++;
		}
		var l = new double[n, n];
		for ( var i = 0; i < n; i++ )
			for ( var j = 0; j < n; j++ )
			{
				var norm = degree[i] > 0 && degree[j] > 0 ? a[i, j] / Math.Sqrt( degree[i] * degree[j] ) : 0;
				l[i, j] = (i == j ? (degree[i] > 0 ? 1 : 0) : 0) - norm;
			}
		var (values, vectors) = Jacobi( l );
		var idx = Enumerable.Range( 0, n ).OrderBy( i => values[i] ).ToArray();
		var result = new float[n, maxFreqs];
		for ( var c = 0; c < k; c++ )
		{
			var col = idx[c + 1];
			double norm = 0;
			for ( var r = 0; r < n; r++ ) norm += vectors[r, col] * vectors[r, col];
			norm = Math.Sqrt( norm );
			for ( var r = 0; r < n; r++ ) result[r, c] = (float)(norm > 1e-12 ? vectors[r, col] / norm : vectors[r, col]);
		}
		return result;
	}

	/// <summary>Cyclic Jacobi eigen-decomposition of a symmetric matrix (vectors in columns).</summary>
	static (double[] Values, double[,] Vectors) Jacobi( double[,] input )
	{
		var n = input.GetLength( 0 );
		var a = (double[,])input.Clone();
		var v = new double[n, n];
		for ( var i = 0; i < n; i++ ) v[i, i] = 1;
		for ( var sweep = 0; sweep < 100; sweep++ )
		{
			double off = 0;
			for ( var p = 0; p < n; p++ ) for ( var q = p + 1; q < n; q++ ) off += a[p, q] * a[p, q];
			if ( off < 1e-22 ) break;
			for ( var p = 0; p < n; p++ )
				for ( var q = p + 1; q < n; q++ )
				{
					if ( Math.Abs( a[p, q] ) < 1e-300 ) continue;
					var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
					var t = Math.Sign( theta ) / (Math.Abs( theta ) + Math.Sqrt( theta * theta + 1 ));
					if ( theta == 0 ) t = 1;
					var c = 1 / Math.Sqrt( t * t + 1 ); var s = t * c;
					for ( var k = 0; k < n; k++ )
					{
						var akp = a[k, p]; var akq = a[k, q];
						a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
					}
					for ( var k = 0; k < n; k++ )
					{
						var apk = a[p, k]; var aqk = a[q, k];
						a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
					}
					for ( var k = 0; k < n; k++ )
					{
						var vkp = v[k, p]; var vkq = v[k, q];
						v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
					}
				}
		}
		var values = new double[n];
		for ( var i = 0; i < n; i++ ) values[i] = a[i, i];
		return (values, v);
	}
}
