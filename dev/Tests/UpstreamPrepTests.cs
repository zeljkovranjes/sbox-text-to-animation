using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The C# port of upstream UniMate's skeleton preprocessing against upstream's own code, on real skeletons.
/// Fixtures come from dev/tools/upstream_prep (upstream Python run on UniML3D's published joint names and on
/// real FBX/glTF rigs); nothing here is hand-written expectation.
/// </summary>
public class UpstreamPrepTests
{
	readonly ITestOutputHelper _out;
	public UpstreamPrepTests( ITestOutputHelper output ) => _out = output;

	static JsonDocument LoadGz( string name )
	{
		using var gz = new GZipStream( File.OpenRead( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", name ) ), CompressionMode.Decompress );
		return JsonDocument.Parse( gz );
	}

	/// <summary>names_clean_rule + face_select_rule on all 7,355 Objaverse skeletons UniMate published.</summary>
	[Fact]
	public void NameAndFacingRulesMatchUpstreamOnEveryPublishedSkeleton()
	{
		using var doc = LoadGz( "rules_objaverse.json.gz" );
		int names = 0, nameMismatch = 0, objects = 0, faceMismatch = 0;
		var examples = new List<string>();
		foreach ( var obj in doc.RootElement.EnumerateObject() )
		{
			objects++;
			var raw = obj.Value.GetProperty( "raw" ).EnumerateArray().Select( e => e.GetString() ).ToList();
			var expected = obj.Value.GetProperty( "clean" ).EnumerateArray().Select( e => e.GetString() ).ToList();
			var clean = raw.Select( r => UniMateNames.Clean( r, obj.Name ) ).ToList();
			for ( var i = 0; i < raw.Count; i++ )
			{
				names++;
				if ( clean[i] == expected[i] ) continue;
				nameMismatch++;
				if ( examples.Count < 25 ) examples.Add( $"name '{raw[i]}' ({obj.Name}): upstream '{expected[i]}', C# '{clean[i]}'" );
			}
			// the facing rule on upstream's clean names, so a name difference can't hide or fake a facing difference
			var (r, l, bodyAxis, _) = UniMateNames.ResolveFaceJoints( expected, raw );
			var er = obj.Value.GetProperty( "r" ).GetInt32(); var el = obj.Value.GetProperty( "l" ).GetInt32();
			var eb = obj.Value.GetProperty( "body_axis" ).GetBoolean();
			// upstream stores the pair by raw name: compare the names (duplicate raw names resolve to the first)
			string N( int i ) => i < 0 ? "" : raw[i];
			if ( N( r ) != N( er ) || N( l ) != N( el ) || bodyAxis != eb )
			{
				faceMismatch++;
				if ( examples.Count < 25 ) examples.Add( $"face {obj.Name}: upstream ({N( er )}, {N( el )}, {eb}), C# ({N( r )}, {N( l )}, {bodyAxis})" );
			}
		}
		_out.WriteLine( $"{objects} skeletons, {names} joint names: {nameMismatch} name mismatches, {faceMismatch} facing mismatches" );
		foreach ( var e in examples ) _out.WriteLine( e );
		Assert.True( nameMismatch == 0 && faceMismatch == 0, string.Join( "\n", examples ) );
	}

	public static IEnumerable<object[]> PrepRigs() => Directory.GetFiles( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep" ), "prep_*.json" )
		.Select( f => new object[] { Path.GetFileNameWithoutExtension( f )[5..] } );

	/// <summary>
	/// Real rigs (humans, quadrupeds, a crocodile, an octopus, a robot, a snake, a spider, a bird, broken files)
	/// through upstream's own pipeline in Blender vs the C# port from the same raw bones and skin weights:
	/// pruning, clean names, facing, BFS order, canonical T-pose and every topology tensor.
	/// </summary>
	[Theory]
	[MemberData( nameof( PrepRigs ) )]
	public void PreparationMatchesUpstreamOnRealRigs( string rig )
	{
		var j = JsonDocument.Parse( File.ReadAllText( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"prep_{rig}.json" ) ) ).RootElement;
		var raw = j.GetProperty( "raw" ).EnumerateArray().ToList();
		static System.Numerics.Vector3 V( JsonElement e ) { var a = e.EnumerateArray().Select( x => x.GetSingle() ).ToArray(); return new( a[0], a[1], a[2] ); }
		static System.Numerics.Quaternion Q( JsonElement e ) { var a = e.EnumerateArray().Select( x => x.GetSingle() ).ToArray(); return new( a[1], a[2], a[3], a[0] ); }
		var input = new UniMatePrep.Input
		{
			ObjectType = j.GetProperty( "rig" ).GetString(),
			Names = raw.Select( b => b.GetProperty( "name" ).GetString() ).ToList(),
			Parents = raw.Select( b => b.GetProperty( "parent" ).GetInt32() ).ToList(),
			RestWorldPos = raw.Select( b => V( b.GetProperty( "pos" ) ) ).ToList(),
			SkinMax = raw.Select( b => b.GetProperty( "skin_max" ).GetDouble() ).ToList(),
			SkinSum = raw.Select( b => b.GetProperty( "weight" ).GetDouble() ).ToList(),
		};
		var rot = raw.Select( b => Q( b.GetProperty( "rot" ) ) ).ToList();
		var prep = UniMatePrep.Prepare( input );

		var pruned = j.GetProperty( "pruned" );
		Assert.Equal( pruned.GetProperty( "names" ).EnumerateArray().Select( e => e.GetString() ), prep.RawNames );
		Assert.Equal( pruned.GetProperty( "parents" ).EnumerateArray().Select( e => e.GetInt32() ), prep.Parents );
		if ( j.TryGetProperty( "degenerate", out _ ) )
		{
			// upstream refuses it (DegenerateSkeletonError); so must the port
			Assert.ThrowsAny<ArgumentException>( () => UniMateSkeleton.Build( prep.CleanNames, prep.Parents, prep.Kept.Select( b => input.RestWorldPos[b] ).ToList(),
				prep.Kept.Select( b => rot[b] ).ToList(), prep.FaceRight, prep.FaceLeft, null, UniMateSkeleton.BlenderUpBasis, bodyAxis: prep.BodyAxis ) );
			_out.WriteLine( $"{rig}: degenerate, refused like upstream" );
			return;
		}
		Assert.Equal( j.GetProperty( "clean_names" ).EnumerateArray().Select( e => e.GetString() ), prep.CleanNames );
		var face = j.GetProperty( "face" );
		string FaceRaw( string k ) => face.GetProperty( k ).GetProperty( "raw" ).GetString();
		Assert.Equal( FaceRaw( "r_hip" ), prep.FaceRight < 0 ? "" : prep.RawNames[prep.FaceRight] );
		Assert.Equal( FaceRaw( "l_hip" ), prep.FaceLeft < 0 ? "" : prep.RawNames[prep.FaceLeft] );

		var sk = UniMateSkeleton.Build( prep.CleanNames, prep.Parents, prep.Kept.Select( b => input.RestWorldPos[b] ).ToList(),
			prep.Kept.Select( b => rot[b] ).ToList(), prep.FaceRight, prep.FaceLeft, null, UniMateSkeleton.BlenderUpBasis, bodyAxis: prep.BodyAxis );
		var bfs = j.GetProperty( "bfs" );
		Assert.Equal( bfs.GetProperty( "body_axis" ).GetBoolean(), sk.BodyAxis );

		// joints matched by identity (the kept bone each one is): upstream position u <-> C# position c
		var upOrder = bfs.GetProperty( "order" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
		var J = sk.Count;
		Assert.Equal( upOrder.Length, J );
		var cOf = new int[J];
		for ( var u = 0; u < J; u++ ) cOf[u] = Array.IndexOf( sk.SourceIndex, upOrder[u] );
		Assert.DoesNotContain( -1, cOf );
		Assert.Equal( 0, cOf[0] ); // same root

		// the BFS order may only differ among exact ties: siblings with equal subtree size and equal bone length
		var upParents = bfs.GetProperty( "parents" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
		var reordered = 0;
		for ( var u = 0; u < J; u++ )
		{
			Assert.Equal( upParents[u] < 0 ? -1 : cOf[upParents[u]], sk.Parents[cOf[u]] );
			if ( cOf[u] == u ) continue;
			reordered++;
			var twin = sk.SourceIndex[u]; // the bone C# put where upstream put this one
			var a = sk.SourceIndex[cOf[u]];
			var la = (input.RestWorldPos[prep.Kept[a]] - input.RestWorldPos[prep.Kept[prep.Parents[a]]]).Length();
			var lb = (input.RestWorldPos[prep.Kept[twin]] - input.RestWorldPos[prep.Kept[prep.Parents[twin]]]).Length();
			Assert.True( MathF.Abs( la - lb ) <= 1e-5f * MathF.Max( la, lb ), $"BFS order differs on non-tied siblings {prep.RawNames[a]} / {prep.RawNames[twin]} ({la} vs {lb})" );
		}

		var upClean = bfs.GetProperty( "clean_names" ).EnumerateArray().Select( e => e.GetString() ).ToArray();
		var faceIdx = bfs.GetProperty( "face_idxs" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
		Assert.Equal( faceIdx[0] < 0 ? -1 : cOf[faceIdx[0]], sk.RightHip );
		Assert.Equal( faceIdx[1] < 0 ? -1 : cOf[faceIdx[1]], sk.LeftHip );
		var tpos = bfs.GetProperty( "tpos" ).EnumerateArray().Select( V ).ToArray();
		var depths = bfs.GetProperty( "depths" ).EnumerateArray().Select( e => e.GetInt64() ).ToArray();
		long[][] Rows( string k ) => bfs.GetProperty( k ).EnumerateArray().Select( r => r.EnumerateArray().Select( x => x.GetInt64() ).ToArray() ).ToArray();
		var rel = Rows( "relations" ); var dist = Rows( "graph_dists" );
		var worst = 0f;
		for ( var u = 0; u < J; u++ )
		{
			var c = cOf[u];
			Assert.Equal( upClean[u], sk.CleanNames[c] );
			Assert.Equal( depths[u], sk.Depths[c] );
			worst = MathF.Max( worst, (tpos[u] - sk.TPose[c]).Length() );
			for ( var v = 0; v < J; v++ )
			{
				Assert.Equal( rel[u][v], sk.Relations[c, cOf[v]] );
				Assert.Equal( dist[u][v], sk.GraphDist[c, cOf[v]] );
			}
		}

		// spectral features: each column must be a unit eigenvector of the skeleton's normalised Laplacian with the
		// eigenvalue of upstream's column. The checkpoint's SignNet is sign-invariant, and inside a repeated
		// eigenvalue (any left/right-symmetric body) numpy's own basis is whatever LAPACK returns, so that is the
		// strongest well-defined comparison.
		var spec = bfs.GetProperty( "spectral" ).EnumerateArray().Select( r => r.EnumerateArray().Select( x => x.GetDouble() ).ToArray() ).ToArray();
		var L = NormalisedLaplacian( sk.Parents );
		var specWorst = 0.0;
		for ( var col = 0; col < spec[0].Length; col++ )
		{
			var up = new double[J]; var mine = new double[J];
			for ( var u = 0; u < J; u++ ) { up[cOf[u]] = spec[u][col]; mine[cOf[u]] = sk.Spectral[cOf[u], col]; }
			if ( up.All( x => x == 0 ) ) { Assert.True( mine.All( x => x == 0 ) ); continue; } // padding (fewer joints than frequencies)
			var lambda = Dot( up, MatVec( L, up ) );
			var lv = MatVec( L, mine );
			var residual = Math.Sqrt( Enumerable.Range( 0, J ).Sum( i => Math.Pow( lv[i] - lambda * mine[i], 2 ) ) );
			specWorst = Math.Max( specWorst, Math.Max( residual, Math.Abs( Dot( mine, mine ) - 1 ) ) );
		}
		_out.WriteLine( $"{rig}: {raw.Count} bones -> {J} joints; face {prep.FaceSource}; {reordered} tie-reordered; T-pose max diff {worst:0.000000}; spectral eigen residual {specWorst:0.0000000}" );
		Assert.True( worst < 1e-4f, $"T-pose differs from upstream by {worst}" );
		Assert.True( specWorst < 1e-4, $"spectral features aren't upstream's eigenvectors (residual {specWorst})" );
	}

	public static IEnumerable<object[]> SampledRigs() => Directory.GetFiles( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep" ), "sample_*.npz" )
		.Select( f => new object[] { Path.GetFileNameWithoutExtension( f )[7..] } );

	/// <summary>
	/// The whole pipeline on real rigs against upstream: preparation, conditioning, the released checkpoint with
	/// the same noise (Euler, CFG 3), decoding, and the motion mapped back onto the rig's own bones. Compares the
	/// network output, the decoded motion and the final world rotation of every bone and the root trajectory.
	/// </summary>
	[Theory]
	[MemberData( nameof( SampledRigs ) )]
	public void GeneratedMotionMatchesUpstreamOnRealRigs( string rig )
	{
		if ( !UniMateSamplerTests.Available ) return;
		var j = JsonDocument.Parse( File.ReadAllText( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"prep_{rig}.json" ) ) ).RootElement;
		using var z = new UniMateCoreTests.Npz( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"sample_{rig}.npz" ) );
		var raw = j.GetProperty( "raw" ).EnumerateArray().ToList();
		static System.Numerics.Vector3 V( JsonElement e ) { var a = e.EnumerateArray().Select( x => x.GetSingle() ).ToArray(); return new( a[0], a[1], a[2] ); }
		static System.Numerics.Quaternion Q( JsonElement e ) { var a = e.EnumerateArray().Select( x => x.GetSingle() ).ToArray(); return new( a[1], a[2], a[3], a[0] ); }
		var input = new UniMatePrep.Input
		{
			ObjectType = j.GetProperty( "rig" ).GetString(),
			Names = raw.Select( b => b.GetProperty( "name" ).GetString() ).ToList(),
			Parents = raw.Select( b => b.GetProperty( "parent" ).GetInt32() ).ToList(),
			RestWorldPos = raw.Select( b => V( b.GetProperty( "pos" ) ) ).ToList(),
			SkinMax = raw.Select( b => b.GetProperty( "skin_max" ).GetDouble() ).ToList(),
			SkinSum = raw.Select( b => b.GetProperty( "weight" ).GetDouble() ).ToList(),
		};
		var rot = raw.Select( b => Q( b.GetProperty( "rot" ) ) ).ToList();
		var prep = UniMatePrep.Prepare( input, UniMateRig.MaxJoints ); // production limit (trims rigs over it)
		var sk = UniMateSkeleton.Build( prep.CleanNames, prep.Parents, prep.Kept.Select( b => input.RestWorldPos[b] ).ToList(),
			prep.Kept.Select( b => rot[b] ).ToList(), prep.FaceRight, prep.FaceLeft, null, UniMateSkeleton.BlenderUpBasis, bodyAxis: prep.BodyAxis );
		var J = sk.Count;

		// upstream joint u <-> C# joint c (they differ only on exact ties); upstream's spectral basis for exactness
		var srcBone = z["src_bone"].Values.Select( v => (int)v ).ToArray();
		var cOf = new int[J];
		for ( var u = 0; u < J; u++ ) cOf[u] = Array.FindIndex( sk.SourceIndex, i => prep.Kept[i] == srcBone[u] );
		Assert.DoesNotContain( -1, cOf );
		var spec = z["spectral"].Values; var sp = new float[J, 8];
		for ( var u = 0; u < J; u++ ) for ( var c = 0; c < 8; c++ ) sp[cOf[u], c] = spec[u * 8 + c];
		sk.SetSpectral( sp );

		var stats = rig.StartsWith( "citizen", StringComparison.Ordinal ) ? UniMateStats.Mixamo : UniMateStats.Truebones; // the same choice as make_sample_fixtures.py
		var model = UniMateSamplerTests.Model();
		var prepared = model.Prepare( sk, stats, default );
		const int T = UniMateModel.Frames;
		var noiseUp = z["noise"].Values;
		var noise = new float[J * 12 * T];
		for ( var u = 0; u < J; u++ ) Array.Copy( noiseUp, u * 12 * T, noise, cOf[u] * 12 * T, 12 * T );
		var x = model.Sample( prepared, z["caption_emb"].Values, noise, new SampleSettings { Steps = 8, Guidance = 3f }, null, default );

		var xUp = z["x_final"].Values;
		var xErr = 0f;
		for ( var u = 0; u < J; u++ ) for ( var k = 0; k < 12 * T; k++ ) xErr = MathF.Max( xErr, MathF.Abs( x[cOf[u] * 12 * T + k] - xUp[u * 12 * T + k] ) );

		var motion = UniMateFeatures.Decode( UniMateFeatures.FromModel( x, J, T, stats ), sk.Parents );
		var src = UniMateFeatures.ToSource( motion, sk );
		var gUp = z["engine_world_q"].Values; var rootUp = z["engine_root_pos"].Values;
		var frames = z["engine_root_pos"].Shape[0];

		// upstream decodes a parent's rotation from its LAST child (hml_rotations_to_bvh_quaternions); where tied
		// siblings came out in another order, a different child is last, and the parent (and the bones it carries)
		// legitimately differ by about the children's disagreement - checked separately
		var upParents = new int[J];
		for ( var u = 0; u < J; u++ ) upParents[u] = sk.Parents[cOf[u]] < 0 ? -1 : Array.IndexOf( cOf, sk.Parents[cOf[u]] );
		var lastChildDiffers = new bool[J];
		for ( var p = 0; p < J; p++ )
		{
			var kidsUp = Enumerable.Range( 0, J ).Where( u => upParents[u] == p ).ToList();
			var kidsC = Enumerable.Range( 0, J ).Where( c => sk.Parents[c] == cOf[p] ).ToList();
			if ( kidsUp.Count > 0 && cOf[kidsUp[^1]] != kidsC[^1] ) lastChildDiffers[p] = true;
		}
		var affected = new bool[J];
		for ( var u = 0; u < J; u++ ) for ( var a = u; a >= 0; a = upParents[a] ) if ( lastChildDiffers[a] ) { affected[u] = true; break; }

		float rotErr = 0f, tieErr = 0f, rootErr = 0f;
		for ( var t = 0; t < frames; t++ )
		{
			rootErr = MathF.Max( rootErr, (src.RootPos[t] - new System.Numerics.Vector3( rootUp[t * 3], rootUp[t * 3 + 1], rootUp[t * 3 + 2] )).Length() );
			for ( var u = 0; u < J; u++ )
			{
				var i = (t * J + u) * 4;
				var up = new System.Numerics.Quaternion( gUp[i + 1], gUp[i + 2], gUp[i + 3], gUp[i] );
				var deg = 2f * MathF.Acos( MathF.Min( 1f, MathF.Abs( System.Numerics.Quaternion.Dot( src.WorldRot[t, cOf[u]], up ) ) ) ) * 180f / MathF.PI;
				if ( affected[u] ) tieErr = MathF.Max( tieErr, deg ); else rotErr = MathF.Max( rotErr, deg );
			}
		}
		var size = input.RestWorldPos.Max( p => p.Length() );
		_out.WriteLine( $"{rig}: {J} joints; network output max diff {xErr:0.00000}; bone rotations max diff {rotErr:0.000} deg" +
			$"{(affected.Any( a => a ) ? $" ({affected.Count( a => a )} bones under tie-ordered children: {tieErr:0.000} deg)" : "")}; root path max diff {rootErr:0.0000} (rig size {size:0.00})" );
		Assert.True( xErr < 0.02f, $"network output differs by {xErr}" );
		Assert.True( rotErr < 0.25f, $"bone rotations differ by {rotErr} degrees" );
		Assert.True( tieErr < 3f, $"bones under tie-ordered children differ by {tieErr} degrees" );
		Assert.True( rootErr < 1e-2f * size, $"root path differs by {rootErr}" );
	}

	static double[,] NormalisedLaplacian( IReadOnlyList<int> parents )
	{
		var n = parents.Count; var a = new double[n, n]; var d = new double[n];
		for ( var j = 0; j < n; j++ ) { var p = parents[j]; if ( p < 0 ) continue; a[j, p] = a[p, j] = 1; d[j]++; d[p]++; }
		var l = new double[n, n];
		for ( var i = 0; i < n; i++ ) for ( var k = 0; k < n; k++ ) l[i, k] = (i == k && d[i] > 0 ? 1 : 0) - (d[i] > 0 && d[k] > 0 ? a[i, k] / Math.Sqrt( d[i] * d[k] ) : 0);
		return l;
	}

	static double[] MatVec( double[,] m, double[] v ) { var n = v.Length; var r = new double[n]; for ( var i = 0; i < n; i++ ) for ( var k = 0; k < n; k++ ) r[i] += m[i, k] * v[k]; return r; }

	static double Dot( double[] a, double[] b ) => a.Zip( b, ( x, y ) => x * y ).Sum();
}
