using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.Editor.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>
/// Port of upstream UniMate's skeleton preparation for an arbitrary rig (<c>data_process/utils/blender_export.py</c>:
/// <c>prune_secondary_roots</c> and <c>prune_skeleton_shared</c>; then the rule annotation in
/// <see cref="UniMateNames"/>). A rig being animated has no clips yet, so the pruning runs exactly as upstream's
/// does with the rest pose as the only clip: an unskinned leaf is removed, an unskinned single-child root is
/// collapsed and an unskinned pass-through bone is merged into its child, until nothing changes; skinned bones are
/// never removed. Positions of the kept joints are unchanged by any of this.
/// </summary>
public static class UniMatePrep
{
	/// <summary>Upstream's _SKINNING_WEIGHT_EPS.</summary>
	public const double SkinEps = 1e-5;

	/// <summary>Upstream's prune_skeleton_shared min_joints.</summary>
	public const int MinJoints = 4;

	public sealed class Input
	{
		/// <summary>The object type name (upstream passes it to the name cleaner; it only matters for a few rigs).</summary>
		public string ObjectType { get; init; } = "";
		public IReadOnlyList<string> Names { get; init; }
		public IReadOnlyList<int> Parents { get; init; }
		public IReadOnlyList<Vector3> RestWorldPos { get; init; }
		/// <summary>Per bone: its largest single vertex weight; null when unknown (every bone then counts as skinned).</summary>
		public IReadOnlyList<double> SkinMax { get; init; }
		/// <summary>Per bone: the sum of its vertex weights (upstream picks the main root by it); null when unknown.</summary>
		public IReadOnlyList<double> SkinSum { get; init; }
	}

	public sealed class Result
	{
		/// <summary>The bones kept (indices into the input), in the input's order (upstream's export order).</summary>
		public int[] Kept { get; init; }
		/// <summary>Parent of each kept bone, as an index into <see cref="Kept"/> (-1 for the root).</summary>
		public int[] Parents { get; init; }
		public string[] RawNames { get; init; }
		public string[] CleanNames { get; init; }
		/// <summary>Facing joints (indices into <see cref="Kept"/>), -1 when none: right/left, or head/tail for a body axis.</summary>
		public int FaceRight { get; init; } = -1;
		public int FaceLeft { get; init; } = -1;
		public bool BodyAxis { get; init; }
		public string FaceSource { get; init; } = "empty";
	}

	/// <summary>Upstream's preparation of <paramref name="input"/>.</summary>
	public static Result Prepare( Input input )
	{
		var n = input.Names.Count;
		bool Skinned( int b ) => input.SkinMax is null || input.SkinMax[b] >= SkinEps;

		// the working skeleton: input indices in order, with parents as input indices
		var nodes = Enumerable.Range( 0, n ).ToList();
		var parent = input.Parents.ToArray();

		// ---- prune_secondary_roots: keep the root whose subtree carries the most skin weight (first on ties)
		// (without skin weights: the root with the most bones under it)
		var roots = nodes.Where( b => parent[b] < 0 ).ToList();
		if ( roots.Count > 1 )
		{
			double SubtreeWeight( int r ) => nodes.Where( b => RootOf( parent, b ) == r ).Sum( b => input.SkinSum is null ? 1.0 : input.SkinSum[b] );
			var best = roots[0]; var bestW = SubtreeWeight( best );
			foreach ( var r in roots.Skip( 1 ) ) { var w = SubtreeWeight( r ); if ( w > bestW ) { best = r; bestW = w; } }
			nodes = nodes.Where( b => RootOf( parent, b ) == best ).ToList();
		}

		List<int> Children( int b ) => nodes.Where( c => parent[c] == b ).ToList();
		int Root() => nodes.First( b => parent[b] < 0 || !nodes.Contains( parent[b] ) );

		// ---- prune_skeleton_shared(min_joints=4) with the rest pose as the only clip
		while ( nodes.Count > MinJoints )
		{
			var progress = 0;

			// pass 1: remove prunable leaves, all at once, until none (or fewer than min_joints would remain)
			while ( true )
			{
				var candidates = nodes.Where( b => Children( b ).Count == 0 && !Skinned( b ) ).ToList();
				if ( candidates.Count == 0 ) break;
				if ( nodes.Count - candidates.Count < MinJoints ) break;
				nodes = nodes.Except( candidates ).ToList();
				progress += candidates.Count;
			}

			// pass 2: collapse an unskinned root with a single child into that child
			while ( true )
			{
				var root = Root();
				var kids = Children( root );
				if ( kids.Count != 1 || Skinned( root ) ) break;
				nodes.Remove( root );
				parent[kids[0]] = -1;
				progress++;
			}

			// pass 3: merge prunable pass-through bones into their child, deepest of the first candidate's chain first
			while ( true )
			{
				var root = Root();
				var candidates = nodes.Where( b => b != root && Children( b ).Count == 1 && !Skinned( b ) ).ToList();
				if ( candidates.Count == 0 ) break;
				var set = candidates.ToHashSet();
				var j = candidates[0];
				while ( set.Contains( Children( j )[0] ) ) j = Children( j )[0];
				var child = Children( j )[0];
				parent[child] = parent[j];
				nodes.Remove( j );
				progress++;
			}

			if ( progress == 0 ) break;
		}

		var index = new Dictionary<int, int>();
		for ( var i = 0; i < nodes.Count; i++ ) index[nodes[i]] = i;
		var parents = nodes.Select( b => parent[b] >= 0 && index.TryGetValue( parent[b], out var p ) ? p : -1 ).ToArray();
		var raw = nodes.Select( b => input.Names[b] ).ToArray();
		var clean = raw.Select( r => UniMateNames.Clean( r, input.ObjectType ) ).ToArray();
		var (fr, fl, bodyAxis, source) = UniMateNames.ResolveFaceJoints( clean, raw );
		return new Result
		{
			Kept = nodes.ToArray(), Parents = parents, RawNames = raw, CleanNames = clean,
			FaceRight = fr, FaceLeft = fl, BodyAxis = bodyAxis, FaceSource = source,
		};
	}

	static int RootOf( int[] parent, int b )
	{
		while ( parent[b] >= 0 ) b = parent[b];
		return b;
	}
}
