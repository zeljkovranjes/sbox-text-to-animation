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

		// the BFS order may only differ among ties: siblings with equal subtree size and bone lengths equal up to
		// rounding (relative, or absolute against the rig size for near-zero bones). Compared per parent - a swapped
		// pair shifts the global positions of everything below it, which is not a further difference.
		var upParents = bfs.GetProperty( "parents" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
		for ( var u = 0; u < J; u++ ) Assert.Equal( upParents[u] < 0 ? -1 : cOf[upParents[u]], sk.Parents[cOf[u]] );
		var extent = prep.Kept.Select( b => input.RestWorldPos[b] ).Aggregate( 0f, ( m, p ) => MathF.Max( m, (p - input.RestWorldPos[prep.Kept[0]]).Length() ) );
		float Len( int a ) => (input.RestWorldPos[prep.Kept[a]] - input.RestWorldPos[prep.Kept[prep.Parents[a]]]).Length();
		var reordered = 0;
		for ( var c = 0; c < J; c++ )
		{
			var mine = Enumerable.Range( 0, J ).Where( k => sk.Parents[k] == c ).Select( k => sk.SourceIndex[k] ).ToArray();
			var theirs = Enumerable.Range( 0, J ).Where( u => upParents[u] >= 0 && cOf[upParents[u]] == c ).Select( u => upOrder[u] ).ToArray();
			for ( var k = 0; k < mine.Length; k++ )
			{
				if ( mine[k] == theirs[k] ) continue;
				reordered++;
				float la = Len( mine[k] ), lb = Len( theirs[k] );
				Assert.True( MathF.Abs( la - lb ) <= 1e-5f * MathF.Max( la, lb ) + 1e-6f * extent,
					$"BFS order differs on non-tied siblings {prep.RawNames[mine[k]]} / {prep.RawNames[theirs[k]]} ({la} vs {lb})" );
			}
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

	/// <summary>Rigs with sampling fixtures (T2A_RIGS=a,b restricts them, for iterating on one).</summary>
	public static IEnumerable<object[]> SampledRigs() => Directory.GetFiles( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep" ), "sample_*.npz" )
		.Select( f => Path.GetFileNameWithoutExtension( f )[7..] )
		.Where( r => Environment.GetEnvironmentVariable( "T2A_RIGS" ) is not { Length: > 0 } only || only.Split( ',' ).Contains( r ) )
		.Select( r => new object[] { r } );

	// ------------------------------------------------------------------------------------------------------------
	// Stage by stage, from the model's bones as the engine would give them, against upstream's own code
	// ------------------------------------------------------------------------------------------------------------

	/// <summary>Blender (Z up, -Y forward) to engine (Z up, +X forward): (x,y,z) -> (-y, x, z), a +90 degree turn about Z.</summary>
	static readonly System.Numerics.Quaternion BlenderToEngine = System.Numerics.Quaternion.CreateFromAxisAngle( System.Numerics.Vector3.UnitZ, MathF.PI / 2 );

	/// <summary>The rig as the engine would give it (bones, parents, rest local transforms) with its FBX skin weights attached.</summary>
	static (TextToAnimation.Animation.MotionRig Rig, Dictionary<string, int> Index) EngineRig( string rig )
	{
		var j = JsonDocument.Parse( File.ReadAllText( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"prep_{rig}.json" ) ) ).RootElement;
		var raw = j.GetProperty( "raw" ).EnumerateArray().ToList();
		var world = raw.Select( b =>
		{
			var p = b.GetProperty( "pos" ).EnumerateArray().Select( x => x.GetSingle() ).ToArray();
			var q = b.GetProperty( "rot" ).EnumerateArray().Select( x => x.GetSingle() ).ToArray();
			return new TextToAnimation.Maths.XForm( System.Numerics.Vector3.Transform( new System.Numerics.Vector3( p[0], p[1], p[2] ), BlenderToEngine ),
				System.Numerics.Quaternion.Normalize( BlenderToEngine * new System.Numerics.Quaternion( q[1], q[2], q[3], q[0] ) ) );
		} ).ToArray();
		var defs = raw.Select( ( b, i ) =>
		{
			var parent = b.GetProperty( "parent" ).GetInt32();
			var local = parent < 0 ? world[i] : TextToAnimation.Maths.XForm.ToLocal( world[parent], world[i] );
			return new TextToAnimation.Rig.BoneDefinition( b.GetProperty( "name" ).GetString(), parent < 0 ? null : raw[parent].GetProperty( "name" ).GetString(), local );
		} ).ToList();
		var skeleton = TextToAnimation.Rig.Skeleton.Create( defs );
		var weights = raw.ToDictionary( b => b.GetProperty( "name" ).GetString(), b => (b.GetProperty( "skin_max" ).GetDouble(), b.GetProperty( "weight" ).GetDouble()) );
		UniMateSkin.Attach( skeleton, weights, j.GetProperty( "rig" ).GetString() );
		var index = raw.Select( ( b, i ) => (b.GetProperty( "name" ).GetString(), i) ).ToDictionary( t => t.Item1, t => t.i );
		return (TextToAnimation.Animation.MotionRig.Create( skeleton ), index);
	}

	/// <summary>
	/// The production chain from the engine skeleton: UniMateRig.Build (preparation, generic statistics family),
	/// the conditioning tensors, sampling with upstream's noise, decoding and FK - each compared with upstream.
	/// </summary>
	sealed class Chain
	{
		public UniMateRig Uni; public int[] COf; public UniMateCoreTests.Npz Z; public float[] X; public UniMateStats Stats;
		public PreparedSkeleton Prepared;
	}

	Chain RunChain( string rig, bool sample = true )
	{
		var (motionRig, index) = EngineRig( rig );
		var uni = UniMateRig.Build( motionRig );
		var z = new UniMateCoreTests.Npz( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"sample_{rig}.npz" ) );
		var srcRaw = z["src_bone"].Values.Select( v => (int)v ).ToArray();
		var J = uni.Count;
		Assert.Equal( srcRaw.Length, J );
		// upstream joint u <-> C# joint c, by bone identity (the order may differ on exact ties)
		var rawOfSkeleton = new int[motionRig.Skeleton.Count];
		for ( var b = 0; b < motionRig.Skeleton.Count; b++ ) rawOfSkeleton[b] = index[motionRig.Skeleton[b].Name];
		var cOf = srcRaw.Select( r => Array.FindIndex( uni.Bone, b => rawOfSkeleton[b] == r ) ).ToArray();
		Assert.DoesNotContain( -1, cOf );
		Assert.Equal( TextToAnimation.Generation.RigFamily.Object, uni.Family );
		var spec = z["spectral"].Values; var sp = new float[J, 8];
		for ( var u = 0; u < J; u++ ) for ( var c = 0; c < 8; c++ ) sp[cOf[u], c] = spec[u * 8 + c];
		uni.Skeleton.SetSpectral( sp ); // the eigenbasis inside repeated eigenvalues is LAPACK's choice: use upstream's
		var model = UniMateSamplerTests.Model();
		var stats = UniMateStats.For( uni.Family );
		var prepared = model.Prepare( uni.Skeleton, stats, default );
		const int T = UniMateModel.Frames;
		var noiseUp = z["noise"].Values; var noise = new float[J * 12 * T];
		for ( var u = 0; u < J; u++ ) Array.Copy( noiseUp, u * 12 * T, noise, cOf[u] * 12 * T, 12 * T );
		var x = !sample ? null : model.Sample( prepared, z["caption_emb"].Values, noise, new SampleSettings { Steps = 8, Guidance = 3f }, null, default );
		return new Chain { Uni = uni, COf = cOf, Z = z, X = x, Stats = stats, Prepared = prepared };
	}

	/// <summary>Every conditioning tensor the network gets, against upstream's mixture_batch_collate output.</summary>
	[Theory]
	[MemberData( nameof( SampledRigs ) )]
	public void ConditioningMatchesUpstream( string rig )
	{
		if ( !UniMateSamplerTests.Available ) return;
		var ch = RunChain( rig, sample: false );
		using var z = ch.Z;
		var J = ch.Uni.Count; var cOf = ch.COf; var t = UniMateSamplerTests.Model().ConditioningInputs( ch.Uni.Skeleton, ch.Stats, default );
		float Rows( string mine, string up, int width )
		{
			var a = t[mine].F; var b = z[up].Values; var worst = 0f;
			for ( var u = 0; u < J; u++ ) for ( var k = 0; k < width; k++ ) worst = MathF.Max( worst, MathF.Abs( a[cOf[u] * width + k] - b[u * width + k] ) );
			return worst;
		}
		var tposErr = Rows( "tpos", "cond_tpos", 12 );
		var parentErr = Rows( "tpos_parent", "cond_tpos_parents", 12 );
		var nameErr = Rows( "name_emb", "cond_name_emb", T5TextEncoder.Width );
		var rel = t["joint_rel"].L; var dist = t["graph_dist"].L; var depth = t["depth"].L;
		var relUp = z["cond_relations"].Values; var distUp = z["cond_graph_dist"].Values; var depthUp = z["cond_depths"].Values;
		int badRel = 0, badDist = 0, badDepth = 0;
		for ( var u = 0; u < J; u++ )
		{
			if ( (long)depthUp[u] != depth[cOf[u]] ) badDepth++;
			for ( var v = 0; v < J; v++ )
			{
				if ( (long)relUp[u * J + v] != rel[cOf[u] * J + cOf[v]] ) badRel++;
				if ( (long)distUp[u * J + v] != dist[cOf[u] * J + cOf[v]] ) badDist++;
			}
		}
		_out.WriteLine( $"{rig}: integer tensors: {badRel} relations, {badDist} graph distances, {badDepth} depths differ" );
		Assert.True( badRel == 0 && badDist == 0 && badDepth == 0, "integer conditioning differs" );
		_out.WriteLine( $"{rig}: {J} joints; T-pose features max diff {tposErr:0.000000}; parent features {parentErr:0.000000}; joint-name embeddings {nameErr:0.000000}; relations, graph distances, depths exact" );
		Assert.True( tposErr < 1e-4f && parentErr < 1e-4f, $"T-pose conditioning differs by {tposErr} / {parentErr}" );
		Assert.True( nameErr < 2e-3f, $"joint-name embeddings differ by {nameErr}" );
	}

	/// <summary>Decoding + FK of upstream's own features in C#, against upstream's recover_unimate_joint_pos_from_rot.</summary>
	[Theory]
	[MemberData( nameof( SampledRigs ) )]
	public void DecodingAndFkMatchUpstream( string rig )
	{
		var (motionRig, index) = EngineRig( rig );
		var uni = UniMateRig.Build( motionRig );
		using var z = new UniMateCoreTests.Npz( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"sample_{rig}.npz" ) );
		var srcRaw = z["src_bone"].Values.Select( v => (int)v ).ToArray();
		var J = uni.Count;
		var rawOfSkeleton = Enumerable.Range( 0, motionRig.Skeleton.Count ).Select( b => index[motionRig.Skeleton[b].Name] ).ToArray();
		var cOf = srcRaw.Select( r => Array.FindIndex( uni.Bone, b => rawOfSkeleton[b] == r ) ).ToArray();
		// upstream's features, in upstream's joint order, decoded with upstream's skeleton (parents/offsets in its order)
		var f = z["features"]; int T = f.Shape[0];
		var feat = new float[T, J, 12];
		for ( var t = 0; t < T; t++ ) for ( var u = 0; u < J; u++ ) for ( var c = 0; c < 12; c++ ) feat[t, u, c] = f.Values[(t * J + u) * 12 + c];
		var upParents = new int[J];
		for ( var u = 0; u < J; u++ ) { var pc = uni.Skeleton.Parents[cOf[u]]; upParents[u] = pc < 0 ? -1 : Array.IndexOf( cOf, pc ); }
		var offsets = new System.Numerics.Vector3[J];
		for ( var u = 0; u < J; u++ ) offsets[u] = uni.Skeleton.Offsets[cOf[u]];
		var motion = UniMateFeatures.Decode( feat, upParents );
		var (_, pos) = UniMateFeatures.Fk( motion, offsets, upParents );
		var up = z["fk_pos"].Values;
		var worst = 0f; float first = 0f, rootErr = 0f, relErr = 0f;
		for ( var t = 0; t < T; t++ ) for ( var u = 0; u < J; u++ )
		{
			var e = (pos[t, u] - new System.Numerics.Vector3( up[(t * J + u) * 3], up[(t * J + u) * 3 + 1], up[(t * J + u) * 3 + 2] ));
			worst = MathF.Max( worst, e.Length() );
			if ( t == 0 ) first = MathF.Max( first, e.Length() );
			if ( u == 0 ) rootErr = MathF.Max( rootErr, e.Length() );
			// relative to the root: the pose without the integrated trajectory
			var er = e - (pos[t, 0] - new System.Numerics.Vector3( up[t * J * 3], up[t * J * 3 + 1], up[t * J * 3 + 2] ));
			relErr = MathF.Max( relErr, er.Length() );
		}
		// the C# canonical T-pose (built from the engine-space skeleton) against upstream's, joint by joint
		var prepJ = JsonDocument.Parse( File.ReadAllText( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"prep_{rig}.json" ) ) ).RootElement;
		var prepOrder = prepJ.GetProperty( "bfs" ).GetProperty( "order" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
		var prepNames = prepJ.GetProperty( "pruned" ).GetProperty( "names" ).EnumerateArray().Select( e => e.GetString() ).ToArray();
		var prepTpos = prepJ.GetProperty( "bfs" ).GetProperty( "tpos" ).EnumerateArray().Select( r => r.EnumerateArray().Select( x => x.GetSingle() ).ToArray() ).ToArray();
		var tposErr = 0f;
		for ( var k = 0; k < prepOrder.Length; k++ )
		{
			var c = Array.FindIndex( uni.Bone, b => motionRig.Skeleton[b].Name == prepNames[prepOrder[k]] );
			tposErr = MathF.Max( tposErr, (uni.Skeleton.TPose[c] - new System.Numerics.Vector3( prepTpos[k][0], prepTpos[k][1], prepTpos[k][2] )).Length() );
		}
		_out.WriteLine( $"{rig}: engine-space T-pose vs upstream {tposErr:0.0000000}" );
		var bvhUp = z["bvh_local_q"].Values; var decodeDeg = 0f;
		for ( var t = 0; t < T; t++ ) for ( var u = 0; u < J; u++ )
		{
			var i = (t * J + u) * 4;
			var q = new System.Numerics.Quaternion( bvhUp[i + 1], bvhUp[i + 2], bvhUp[i + 3], bvhUp[i] );
			decodeDeg = MathF.Max( decodeDeg, (float)AngleDeg( motion.Local[t, u], q ) );
		}
		_out.WriteLine( $"{rig}: FK joint positions max diff {worst:0.000000} (frame 0 {first:0.000000}; root path {rootErr:0.000000}; pose relative to root {relErr:0.000000}) - canonical units, body diameter 2; decoded local rotations max diff {decodeDeg:0.0000} deg" );
		Assert.True( worst < 1e-4f, $"FK positions differ by {worst}" );
	}

	/// <summary>
	/// The whole production chain onto the original skeleton, against upstream's reconstruction on the real model
	/// (Blender: sync_armature_bones merge + compute_bone_keyframes): the world transform of every bone UniMate
	/// drives, every frame.
	/// </summary>
	[Theory]
	[MemberData( nameof( SampledRigs ) )]
	public void ReconstructionOnTheOriginalRigMatchesUpstream( string rig )
	{
		if ( !UniMateSamplerTests.Available ) return;
		var ch = RunChain( rig );
		using var z = ch.Z;
		using var recon = new UniMateCoreTests.Npz( Path.Combine( AppContext.BaseDirectory, "fixtures", "upstream_prep", $"recon_{rig}.npz" ) );
		var J = ch.Uni.Count; var cOf = ch.COf;
		const int T = UniMateModel.Frames;
		var xErr = 0f; var xUp = z["x_final"].Values;
		for ( var u = 0; u < J; u++ ) for ( var k = 0; k < 12 * T; k++ ) xErr = MathF.Max( xErr, MathF.Abs( ch.X[cOf[u] * 12 * T + k] - xUp[u * 12 * T + k] ) );

		var motion = UniMateFeatures.Decode( UniMateFeatures.FromModel( ch.X, J, T, ch.Stats ), ch.Uni.Skeleton.Parents );
		var frames = ch.Uni.ToFrames( UniMateFeatures.ToSource( motion, ch.Uni.Skeleton ) );
		var skeleton = ch.Uni.Motion.Skeleton;
		var world = new TextToAnimation.Maths.XForm[skeleton.Count];
		var toBlender = System.Numerics.Quaternion.Conjugate( BlenderToEngine );
		var wp = recon["world_pos"].Values; var wr = recon["world_rot"].Values;
		var size = 0f;
		for ( var u = 0; u < J; u++ ) size = MathF.Max( size, new System.Numerics.Vector3( wp[u * 3], wp[u * 3 + 1], wp[u * 3 + 2] ).Length() );

		// upstream decodes a parent's rotation from its last child: where tied siblings came out in another order,
		// that parent (and what it carries) may legitimately differ - reported separately
		var lastDiffers = new bool[J];
		for ( var u = 0; u < J; u++ )
		{
			var kidsUp = Enumerable.Range( 0, J ).Where( v => ch.Uni.Skeleton.Parents[cOf[v]] == cOf[u] ).OrderBy( v => v ).ToList();
			var kidsC = Enumerable.Range( 0, J ).Where( c => ch.Uni.Skeleton.Parents[c] == cOf[u] ).ToList();
			if ( kidsUp.Count > 0 && cOf[kidsUp[^1]] != kidsC[^1] ) lastDiffers[u] = true;
		}
		var affected = new bool[J];
		for ( var u = 0; u < J; u++ )
			for ( var c = cOf[u]; c >= 0; c = ch.Uni.Skeleton.Parents[c] )
				if ( lastDiffers[Array.IndexOf( cOf, c )] ) { affected[u] = true; break; }

		float posErr = 0f, rotErr = 0f, tieErr = 0f; string worstRot = "", worstPos = "";
		for ( var t = 0; t < T - 1; t++ ) // upstream's features have T frames; the last has no velocity term
		{
			TextToAnimation.Processing.FkUtil.ToWorld( frames[t], skeleton, world );
			for ( var u = 0; u < J; u++ )
			{
				var b = ch.Uni.Bone[cOf[u]];
				var i = t * J + u;
				var pos = System.Numerics.Vector3.Transform( world[b].Pos, toBlender );
				var rot = toBlender * world[b].Rot;
				var upPos = new System.Numerics.Vector3( wp[i * 3], wp[i * 3 + 1], wp[i * 3 + 2] );
				var upRot = new System.Numerics.Quaternion( wr[i * 4 + 1], wr[i * 4 + 2], wr[i * 4 + 3], wr[i * 4] );
				var deg = 2f * MathF.Acos( MathF.Min( 1f, MathF.Abs( System.Numerics.Quaternion.Dot( System.Numerics.Quaternion.Normalize( rot ), System.Numerics.Quaternion.Normalize( upRot ) ) ) ) ) * 180f / MathF.PI;
				if ( affected[u] ) tieErr = MathF.Max( tieErr, deg );
				else
				{
					if ( deg > rotErr ) { rotErr = deg; worstRot = $"{skeleton[b].Name}@{t}"; }
					var d = (pos - upPos).Length();
					if ( d > posErr ) { posErr = d; worstPos = $"{skeleton[b].Name}@{t} C# {pos} up {upPos}"; }
				}
			}
		}
		_out.WriteLine( $"{rig}: {J} joints; network output {xErr:0.00000}; bone world rotations {rotErr:0.000} deg [{worstRot}]; bone world positions {posErr:0.00000} [{worstPos}] (rig size {size:0.00})" +
			(affected.Any( a => a ) ? $"; {affected.Count( a => a )} bones under tie-ordered children {tieErr:0.00} deg" : "") );
		Assert.True( xErr < 0.02f, $"network output differs by {xErr}" );
		Assert.True( rotErr < 0.25f, $"bone rotations differ by {rotErr} degrees" );
		Assert.True( posErr < 1e-3f * MathF.Max( size, 1f ), $"bone positions differ by {posErr}" );
		Assert.True( tieErr < 3f, $"bones under tie-ordered children differ by {tieErr} degrees" );
	}

	/// <summary>The angle between two rotations in degrees, precise near zero (atan2 of the relative rotation's parts, in double).</summary>
	static double AngleDeg( System.Numerics.Quaternion a, System.Numerics.Quaternion b )
	{
		double aw = a.W, ax = a.X, ay = a.Y, az = a.Z, bw = b.W, bx = b.X, by = b.Y, bz = b.Z;
		var na = Math.Sqrt( aw * aw + ax * ax + ay * ay + az * az ); var nb = Math.Sqrt( bw * bw + bx * bx + by * by + bz * bz );
		aw /= na; ax /= na; ay /= na; az /= na; bw /= nb; bx /= nb; by /= nb; bz /= nb;
		// conj(a) * b
		var w = aw * bw + ax * bx + ay * by + az * bz;
		var x = aw * bx - ax * bw - ay * bz + az * by;
		var y = aw * by + ax * bz - ay * bw - az * bx;
		var z = aw * bz - ax * by + ay * bx - az * bw;
		return 2 * Math.Atan2( Math.Sqrt( x * x + y * y + z * z ), Math.Abs( w ) ) * 180 / Math.PI;
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
