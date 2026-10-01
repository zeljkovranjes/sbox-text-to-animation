using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TextToAnimation.Editor.Inference.Onnx;
using TextToAnimation.Editor.Inference.Runtime;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>Architecture constants of the UniMate graph_adaln denoiser.</summary>
public sealed record UniMateArch( int Layers, int Width = 512, int Heads = 8, int FfHidden = 1365, int MaxDepth = 19, int Features = 12, int TextWidth = 768 )
{
	public int HeadDim => Width / Heads;
	public static UniMateArch V2 { get; } = new( 10 );
	public static UniMateArch Mixamo { get; } = new( 6, MaxDepth: 7 );
}

/// <summary>
/// Writes the UniMate denoiser as two ONNX graphs (opset 23) from its PyTorch weights, entirely in C#:
/// <list type="bullet">
/// <item><b>prepare</b> (once per skeleton): tpos tokens, token embeddings, the tpos pool vector, spectral
/// RoPE tables and every block's graph attention bias;</item>
/// <item><b>step</b> (every sampling step): velocity v = f(x, t, caption, prepared tensors).</item>
/// </list>
/// Shapes are static (batch, frames and joints are fixed per graph). The math follows
/// unimate/models/denoiser exactly; splitting it in two only hoists inputs that don't depend on x or t.
/// </summary>
public sealed class UniMateGraphBuilder
{
	/// <summary>Bumped whenever the generated graphs or weight layout change (old weight blobs are rebuilt).</summary>
	public const int FormatVersion = 2;

	readonly IReadOnlyDictionary<string, WeightTensor> _w;
	readonly UniMateArch _arch;
	readonly WeightBlob _blob;
	OnnxGraphBuilder _g;
	readonly Dictionary<string, string> _weightNames = new( StringComparer.Ordinal );

	/// <param name="weights">Raw checkpoint weights; may be null when every weight is already in <paramref name="blob"/>.</param>
	/// <param name="blob">Shared external data file. Null keeps weights in each graph's own data stream.</param>
	public UniMateGraphBuilder( IReadOnlyDictionary<string, WeightTensor> weights, UniMateArch arch, WeightBlob blob = null )
	{
		_w = weights ?? new Dictionary<string, WeightTensor>();
		_arch = arch;
		_blob = blob;
	}

	const int F32 = OnnxGraphBuilder.Float;
	const int I64 = OnnxGraphBuilder.Int64;
	int D => _arch.Width;
	int H => _arch.Heads;
	int Hd => _arch.HeadDim;

	// ---------------------------------------------------------------- helpers

	WeightTensor W( string name ) => _w.TryGetValue( name, out var t ) ? t : throw new KeyNotFoundException( $"UniMate weight '{name}' is missing." );

	string Raw( string name, int[] shape, float[] data )
	{
		if ( _weightNames.TryGetValue( name, out var existing ) ) return existing;
		if ( _blob is not null && (_blob.Writable || _blob.TryGet( name, out _ )) )
		{
			var e = _blob.GetOrAdd( name, shape, data );
			var r = _g.ExternalReference( name, F32, shape.Select( s => (long)s ).ToArray(), _blob.FileName, e.Offset, e.Length );
			_weightNames[name] = r;
			return r;
		}
		var bytes = MemoryMarshal.AsBytes( data.AsSpan() );
		var n = _g.Weight( name, F32, shape.Select( s => (long)s ).ToArray(), bytes );
		_weightNames[name] = n;
		return n;
	}

	/// <summary>A reference to a weight already in the shared blob (no raw weights needed), or null.</summary>
	string FromBlob( string key )
	{
		if ( _weightNames.TryGetValue( key, out var existing ) ) return existing;
		if ( _blob is null || !_blob.TryGet( key, out var e ) ) return null;
		var r = _g.ExternalReference( key, F32, e.Shape.Select( s => (long)s ).ToArray(), _blob.FileName, e.Offset, e.Length );
		_weightNames[key] = r;
		return r;
	}

	/// <summary>A weight as stored ([out, in] for Linear weights).</summary>
	string Param( string name ) { if ( FromBlob( name ) is { } r ) return r; var t = W( name ); return Raw( name, t.Shape, t.Data ); }

	/// <summary>A Linear weight transposed to [in, out] for MatMul.</summary>
	string LinearWeight( string name )
	{
		var key = name + ".T";
		if ( FromBlob( key ) is { } cached ) return cached;
		var t = W( name );
		int o = t.Shape[0], i = t.Shape[1];
		var data = new float[o * i];
		for ( var r = 0; r < o; r++ ) for ( var c = 0; c < i; c++ ) data[c * o + r] = t.Data[r * i + c];
		return Raw( key, new[] { i, o }, data );
	}

	string Floats( string name, int[] shape, float[] data ) => Raw( name, shape, data );
	string Ints( params long[] v ) => _g.Constant( v );
	string Op( string op, params string[] inputs ) => _g.Node( op, inputs );
	string Op( string op, string[] inputs, Action<OnnxGraphBuilder.Attributes> a ) => _g.Node( op, inputs, a );

	string Linear( string x, string prefix, bool bias = true )
	{
		var y = Op( "MatMul", x, LinearWeight( prefix + ".weight" ) );
		return bias ? Op( "Add", y, Param( prefix + ".bias" ) ) : y;
	}

	/// <summary>Linear from a slice of rows of a packed weight (e.g. MultiheadAttention in_proj).</summary>
	string LinearRows( string x, string weightName, string biasName, int rowStart, int rows, string key )
	{
		if ( FromBlob( key + ".w" ) is { } bw && FromBlob( key + ".b" ) is { } bb )
			return Op( "Add", Op( "MatMul", x, bw ), bb );
		var w = W( weightName ); var b = W( biasName );
		var inDim = w.Shape[1];
		var data = new float[rows * inDim];
		for ( var r = 0; r < rows; r++ ) for ( var c = 0; c < inDim; c++ ) data[c * rows + r] = w.Data[(rowStart + r) * inDim + c];
		var y = Op( "MatMul", x, Raw( key + ".w", new[] { inDim, rows }, data ) );
		return Op( "Add", y, Raw( key + ".b", new[] { rows }, b.Data.Skip( rowStart ).Take( rows ).ToArray() ) );
	}

	string SiLU( string x ) => Op( "Mul", x, Op( "Sigmoid", x ) );
	string Gelu( string x ) => Op( "Gelu", new[] { x }, a => a.String( "approximate", "none" ) );

	string Rms( string x, string weight ) => Op( "RMSNormalization", new[] { x, Param( weight ) }, a => a.Int( "axis", -1 ).Float( "epsilon", 1e-6f ) );

	string Mlp2( string x, string prefix ) => Linear( SiLU( Linear( x, prefix + ".0" ) ), prefix + ".2" );

	string Reshape( string x, params long[] shape ) => Op( "Reshape", x, Ints( shape ) );
	string Transpose( string x, params long[] perm ) => Op( "Transpose", new[] { x }, a => a.Ints( "perm", perm ) );
	string[] Split( string x, int axis, params long[] sizes ) => _g.Node( "Split", new[] { x, Ints( sizes ) }, sizes.Length, a => a.Int( "axis", axis ) );
	string Slice( string x, long axis, long start, long end ) => Op( "Slice", x, Ints( start ), Ints( end ), Ints( axis ) );
	string Attention( string q, string k, string v, string mask, float scale )
		=> mask is null
			? Op( "Attention", new[] { q, k, v }, a => a.Float( "scale", scale ) )
			: Op( "Attention", new[] { q, k, v, mask }, a => a.Float( "scale", scale ) );

	/// <summary>x*cos + rotate_half(x)*sin, rotate_half via a constant 64x64 matrix.</summary>
	string Rope( string x, string cos, string sin )
	{
		var half = Hd / 2;
		var r = new float[Hd * Hd];
		// rotate_half([a|b]) = [-b|a]: out[i] = -x[i+half] (i<half), out[i] = x[i-half] (i>=half); as x @ R
		for ( var i = 0; i < half; i++ ) { r[(i + half) * Hd + i] = -1f; r[i * Hd + (i + half)] = 1f; }
		var rot = Op( "MatMul", x, Floats( "rope.rotate_half", new[] { Hd, Hd }, r ) );
		return Op( "Add", Op( "Mul", x, cos ), Op( "Mul", rot, sin ) );
	}

	/// <summary>torch.nn.MultiheadAttention with separate q / kv inputs (in_proj split into rows).</summary>
	string Mha( string q, string kv, string values, string prefix, int heads, long batch, long qLen, long kLen )
	{
		var dh = D / heads;
		var Q = LinearRows( q, prefix + ".in_proj_weight", prefix + ".in_proj_bias", 0, D, prefix + ".q" );
		var K = LinearRows( kv, prefix + ".in_proj_weight", prefix + ".in_proj_bias", D, D, prefix + ".k" );
		var V = LinearRows( values, prefix + ".in_proj_weight", prefix + ".in_proj_bias", 2 * D, D, prefix + ".v" );
		var q4 = Transpose( Reshape( Q, batch, qLen, heads, dh ), 0, 2, 1, 3 );
		var k4 = Transpose( Reshape( K, batch, kLen, heads, dh ), 0, 2, 1, 3 );
		var v4 = Transpose( Reshape( V, batch, kLen, heads, dh ), 0, 2, 1, 3 );
		var o = Attention( q4, k4, v4, null, 1f / MathF.Sqrt( dh ) );
		var merged = Reshape( Transpose( o, 0, 2, 1, 3 ), batch, qLen, D );
		return Linear( merged, prefix + ".out_proj" );
	}

	// ---------------------------------------------------------------- prepare graph

	/// <summary>
	/// Inputs: tpos (J,12), tpos_parent (J,12), joint_rel (J,J), graph_dist (J,J), depth (J), spectral (J,8),
	/// name_emb (J,768). Outputs: frame0 (1,1,J,D), token_bias (1,1,J,D), tpos_pool (1,D), rope_cos/rope_sin
	/// (J,64), bias_i (H,J,J) per block.
	/// </summary>
	public byte[] BuildPrepare( int joints, Stream weightData, string weightFile )
	{
		_g = new OnnxGraphBuilder( weightData, weightFile );
		_weightNames.Clear();
		long J = joints;
		_g.Input( "tpos", F32, J, 12 );
		_g.Input( "tpos_parent", F32, J, 12 );
		_g.Input( "joint_rel", I64, J, J );
		_g.Input( "graph_dist", I64, J, J );
		_g.Input( "depth", I64, J );
		_g.Input( "spectral", F32, J, 8 );
		_g.Input( "name_emb", F32, J, 768 );

		// tpos tokens: root embedder for joint 0, fused joint+parent embedders for the rest
		var tposRoot = Mlp2( Slice( "tpos", 0, 0, 1 ), "input_layer.root_tpos_embedder" );
		var jointProj = Mlp2( Slice( "tpos", 0, 1, J ), "input_layer.joint_tpos_embedder" );
		var parentProj = Mlp2( Slice( "tpos_parent", 0, 1, J ), "input_layer.parent_tpos_embedder" );
		var fused = Mlp2( Op( "Concat", new[] { jointProj, parentProj }, a => a.Int( "axis", -1 ) ), "input_layer.tpos_fuse" );
		var tposEmb = Op( "Concat", new[] { tposRoot, fused }, a => a.Int( "axis", 0 ) ); // (J,D)

		// tpos pool (TposCrossAttentionPool, 4 queries, 4 heads)
		var queries = Reshape( Param( "tpos_pool.pool.queries" ), 4, D );
		var kv = Rms( tposEmb, "tpos_pool.pool.norm_kv.weight" );
		var qn = Rms( queries, "tpos_pool.pool.norm_q.weight" );
		var attn = Reshape( Mha( Reshape( qn, 1, 4, D ), Reshape( kv, 1, J, D ), Reshape( kv, 1, J, D ), "tpos_pool.pool.cross_attn", 4, 1, 4, J ), 4, D );
		var q = Op( "Add", queries, attn );
		var ff = Rms( q, "tpos_pool.pool.norm_ff.weight" );
		var x12 = Linear( ff, "tpos_pool.pool.ffn.w12" );
		var poolHidden = 2 * D; // TposCrossAttentionPool: SwiGLU hidden = latent * mlp_ratio(2.0)
		var halves = Split( x12, -1, poolHidden, poolHidden );
		var hidden = Op( "Mul", SiLU( halves[0] ), halves[1] );
		q = Op( "Add", q, Linear( hidden, "tpos_pool.pool.ffn.w3" ) );
		var pooled = Op( "ReduceMean", new[] { q, Ints( 0 ) }, a => a.Int( "keepdims", 1 ) ); // (1,D)
		var tposPool = Linear( Rms( pooled, "tpos_pool.pool.norm_out.weight" ), "tpos_pool.pool.out_proj" );

		// token embeddings: depth + joint name, added to every frame (including the tpos frame)
		var depth = Op( "Clip", new[] { "depth", Ints( 0 ), Ints( _arch.MaxDepth ) } );
		var depthEmb = Op( "Gather", new[] { Param( "depth_embedding.weight" ), depth }, a => a.Int( "axis", 0 ) );
		var nameEmb = Linear( "name_emb", "joint_name_embedder" );
		var tokenBias = Op( "Add", depthEmb, nameEmb );
		var frame0 = Op( "Add", tposEmb, tokenBias );

		// spectral RoPE angles (SignNet): rho([phi(v_k)+phi(-v_k)]_k)
		var v = Reshape( "spectral", J, 8, 1 );
		string Phi( string input ) => Linear( Gelu( Linear( input, "rope_j.spectral_encoder.phi.0" ) ), "rope_j.spectral_encoder.phi.2" );
		var phiSum = Op( "Add", Phi( v ), Phi( Op( "Neg", v ) ) ); // (J,8,64)
		var flat = Reshape( phiSum, J, 8 * 64 );
		var angles = Linear( Gelu( Linear( flat, "rope_j.spectral_encoder.rho.0" ) ), "rope_j.spectral_encoder.rho.2" ); // (J,32)
		var angles2 = Op( "Concat", new[] { angles, angles }, a => a.Int( "axis", -1 ) );
		var cos = Op( "Cos", angles2 ); var sin = Op( "Sin", angles2 );

		_g.Identity( Reshape( frame0, 1, 1, J, D ), "frame0" );
		_g.Identity( Reshape( tokenBias, 1, 1, J, D ), "token_bias" );
		_g.Identity( tposPool, "tpos_pool" );
		_g.Identity( cos, "rope_cos" );
		_g.Identity( sin, "rope_sin" );
		_g.Output( "frame0", F32, 1, 1, J, D );
		_g.Output( "token_bias", F32, 1, 1, J, D );
		_g.Output( "tpos_pool", F32, 1, D );
		_g.Output( "rope_cos", F32, J, Hd );
		_g.Output( "rope_sin", F32, J, Hd );

		// graph attention bias per block: (dist_proj(E_dist[dist]) * s_d + rel_proj(E_rel[rel]) * s_r) -> (H,J,J)
		for ( var i = 0; i < _arch.Layers; i++ )
		{
			var p = $"transformer_blocks.{i}.s_attn";
			var dist = Linear( Op( "Gather", new[] { Param( p + ".graph_dist_embedding.weight" ), "graph_dist" }, a => a.Int( "axis", 0 ) ), p + ".graph_dist_proj" );
			var rel = Linear( Op( "Gather", new[] { Param( p + ".graph_rel_embedding.weight" ), "joint_rel" }, a => a.Int( "axis", 0 ) ), p + ".graph_rel_proj" );
			var bias = Op( "Add", Op( "Mul", dist, Param( p + ".graph_dist_scale" ) ), Op( "Mul", rel, Param( p + ".graph_rel_scale" ) ) ); // (J,J,H)
			var name = $"bias_{i}";
			_g.Identity( Transpose( bias, 2, 0, 1 ), name );
			_g.Output( name, F32, H, J, J );
		}
		return _g.Build( "unimate_prepare", 23 );
	}

	// ---------------------------------------------------------------- step graph

	/// <summary>
	/// Inputs: x (B,J,12,T), t (B), caption_emb (B,768) plus the prepare outputs.
	/// Output: v (B,J,12,T).
	/// </summary>
	public byte[] BuildStep( int batch, int joints, int frames, Stream weightData, string weightFile )
	{
		_g = new OnnxGraphBuilder( weightData, weightFile );
		_weightNames.Clear();
		long B = batch, J = joints, T = frames, Fp = frames + 1;
		_g.Input( "x", F32, B, J, 12, T );
		_g.Input( "t", F32, B );
		_g.Input( "caption_emb", F32, B, 768 );
		_g.Input( "frame0", F32, 1, 1, J, D );
		_g.Input( "token_bias", F32, 1, 1, J, D );
		_g.Input( "tpos_pool", F32, 1, D );
		_g.Input( "rope_cos", F32, J, Hd );
		_g.Input( "rope_sin", F32, J, Hd );
		for ( var i = 0; i < _arch.Layers; i++ ) _g.Input( $"bias_{i}", F32, H, J, J );

		// timestep embedding: [cos(t f), sin(t f)], f_i = exp(-ln(10000) i/128)
		var freqs = Enumerable.Range( 0, 128 ).Select( i => MathF.Exp( -MathF.Log( 10000f ) * i / 128f ) ).ToArray();
		var args = Op( "Mul", Reshape( "t", B, 1 ), Floats( "time.freqs", new[] { 1, 128 }, freqs ) );
		var tFreq = Op( "Concat", new[] { Op( "Cos", args ), Op( "Sin", args ) }, a => a.Int( "axis", -1 ) );
		var temb = Linear( SiLU( Linear( tFreq, "time_embedder.mlp.0" ) ), "time_embedder.mlp.2" );
		var y = Op( "Add", Op( "Add", temb, Linear( "caption_emb", "cond_embedder" ) ), "tpos_pool" ); // (B,D)
		var sy = SiLU( y );

		// input tokens: (B,J,12,T) -> (B,T,J,12) -> root / joint embedders
		var xt = Transpose( "x", 0, 3, 1, 2 );
		var root = Mlp2( Slice( xt, 2, 0, 1 ), "input_layer.root_x_embedder" );
		var rest = Mlp2( Slice( xt, 2, 1, J ), "input_layer.joint_x_embedder" );
		var motion = Op( "Add", Op( "Concat", new[] { root, rest }, a => a.Int( "axis", 2 ) ), "token_bias" ); // (B,T,J,D)
		var frame0 = Op( "Expand", "frame0", Ints( B, 1, J, D ) );
		var h = Op( "Concat", new[] { frame0, motion }, a => a.Int( "axis", 1 ) ); // (B,F',J,D)

		// temporal RoPE table (positions 0..F'-1, base 200)
		var tBase = (MathF.Floor( (int)(8 * Fp / MathF.PI) / 100f ) + 1) * 100f;
		var tcos = new float[Fp * Hd]; var tsin = new float[Fp * Hd];
		for ( var p = 0; p < Fp; p++ )
			for ( var i = 0; i < Hd / 2; i++ )
			{
				var inv = 1.0 / Math.Pow( tBase, 2.0 * i / Hd );
				var a = p * (float)inv;
				tcos[p * Hd + i] = tcos[p * Hd + i + Hd / 2] = MathF.Cos( a );
				tsin[p * Hd + i] = tsin[p * Hd + i + Hd / 2] = MathF.Sin( a );
			}
		var tCos = Floats( "rope_t.cos", new[] { (int)Fp, Hd }, tcos );
		var tSin = Floats( "rope_t.sin", new[] { (int)Fp, Hd }, tsin );
		var sCos = Reshape( "rope_cos", 1, 1, J, Hd );
		var sSin = Reshape( "rope_sin", 1, 1, J, Hd );

		for ( var i = 0; i < _arch.Layers; i++ )
		{
			var p = $"transformer_blocks.{i}";
			var c = Linear( sy, p + ".adaLN_modulation.1" ); // (B, 9D)
			var chunks = Split( c, -1, D, D, D, D, D, D, D, D, D ).Select( z => Reshape( z, B, 1, 1, D ) ).ToArray();
			string Modulate( string x, int shift, int scale ) => Op( "Add", Op( "Mul", x, Op( "Add", chunks[scale], Floats( "one", new[] { 1 }, new[] { 1f } ) ) ), chunks[shift] );

			// spatial: per frame over joints, graph biased
			var us = Modulate( Rms( h, p + ".norm_s.weight" ), 0, 1 );
			// q, k, v as three matmuls (rows of the packed qkv weight): no split copies
			string SpatialHeads( string lin ) => Reshape( Transpose( Reshape( lin, B, Fp, J, H, Hd ), 0, 1, 3, 2, 4 ), B * Fp, H, J, Hd );
			var sq = Rope( Rms( SpatialHeads( LinearRows( us, p + ".s_attn.qkv.weight", p + ".s_attn.qkv.bias", 0, D, p + ".s_attn.q" ) ), p + ".s_attn.q_norm.weight" ), sCos, sSin );
			var sk = Rope( Rms( SpatialHeads( LinearRows( us, p + ".s_attn.qkv.weight", p + ".s_attn.qkv.bias", D, D, p + ".s_attn.k" ) ), p + ".s_attn.k_norm.weight" ), sCos, sSin );
			var sv = SpatialHeads( LinearRows( us, p + ".s_attn.qkv.weight", p + ".s_attn.qkv.bias", 2 * D, D, p + ".s_attn.v" ) );
			var so = Attention( sq, sk, sv, $"bias_{i}", 1f / MathF.Sqrt( Hd ) ); // (B*F',H,J,hd)
			var sOut = Linear( Reshape( Transpose( Reshape( so, B, Fp, H, J, Hd ), 0, 1, 3, 2, 4 ), B, Fp, J, D ), p + ".s_attn.proj" );
			h = Op( "Add", h, Op( "Mul", chunks[2], sOut ) );

			// temporal: per joint over frames
			var ut = Transpose( Modulate( Rms( h, p + ".norm_t.weight" ), 3, 4 ), 0, 2, 1, 3 ); // (B,J,F',D)
			string TemporalHeads( string lin ) => Reshape( Transpose( Reshape( lin, B, J, Fp, H, Hd ), 0, 1, 3, 2, 4 ), B * J, H, Fp, Hd );
			var tq = Rope( Rms( TemporalHeads( LinearRows( ut, p + ".t_attn.qkv.weight", p + ".t_attn.qkv.bias", 0, D, p + ".t_attn.q" ) ), p + ".t_attn.q_norm.weight" ), tCos, tSin );
			var tk = Rope( Rms( TemporalHeads( LinearRows( ut, p + ".t_attn.qkv.weight", p + ".t_attn.qkv.bias", D, D, p + ".t_attn.k" ) ), p + ".t_attn.k_norm.weight" ), tCos, tSin );
			var tv = TemporalHeads( LinearRows( ut, p + ".t_attn.qkv.weight", p + ".t_attn.qkv.bias", 2 * D, D, p + ".t_attn.v" ) );
			var to = Attention( tq, tk, tv, null, 1f / MathF.Sqrt( Hd ) ); // (B*J,H,F',hd)
			var tOut = Linear( Reshape( Transpose( Reshape( to, B, J, H, Fp, Hd ), 0, 3, 1, 2, 4 ), B, Fp, J, D ), p + ".t_attn.proj" );
			h = Op( "Add", h, Op( "Mul", chunks[5], tOut ) );

			// SwiGLU MLP
			var um = Modulate( Rms( h, p + ".norm_mlp.weight" ), 6, 7 );
			var gateIn = LinearRows( um, p + ".mlp.w12.weight", p + ".mlp.w12.bias", 0, _arch.FfHidden, p + ".mlp.w1" );
			var valueIn = LinearRows( um, p + ".mlp.w12.weight", p + ".mlp.w12.bias", _arch.FfHidden, _arch.FfHidden, p + ".mlp.w2" );
			var mOut = Linear( Op( "Mul", SiLU( gateIn ), valueIn ), p + ".mlp.w3" );
			h = Op( "Add", h, Op( "Mul", chunks[8], mOut ) );
		}

		// final layer: adaLN, root aggregates the joints per frame (4-head cross attention), output MLPs
		var fc = Split( Linear( sy, "final_layer.adaLN_modulation.1" ), -1, D, D ).Select( z => Reshape( z, B, 1, 1, D ) ).ToArray();
		var u = Op( "Add", Op( "Mul", Rms( h, "final_layer.norm_final.weight" ), Op( "Add", fc[1], Floats( "one", new[] { 1 }, new[] { 1f } ) ) ), fc[0] );
		var uRoot = Reshape( Slice( u, 2, 0, 1 ), B * Fp, 1, D );
		var uJoints = Reshape( Slice( u, 2, 1, J ), B * Fp, J - 1, D );
		var agg = Mha( Rms( uRoot, "final_layer.root_cross_norm_q.weight" ), Rms( uJoints, "final_layer.root_cross_norm_kv.weight" ), uJoints,
			"final_layer.root_cross_attn", 4, B * Fp, 1, J - 1 );
		var rootTok = Op( "Add", uRoot, agg );
		var rootOut = Reshape( Mlp2( rootTok, "final_layer.root_out" ), B, Fp, 1, _arch.Features );
		var jointOut = Mlp2( Reshape( uJoints, B, Fp, J - 1, D ), "final_layer.joint_out" );
		var outAll = Op( "Concat", new[] { rootOut, jointOut }, a => a.Int( "axis", 2 ) ); // (B,F',J,12)
		var v = Transpose( Slice( outAll, 1, 1, Fp ), 0, 2, 3, 1 ); // (B,J,12,T)
		_g.Identity( v, "v" );
		_g.Output( "v", F32, B, J, 12, T );
		return _g.Build( "unimate_step", 23 );
	}
}
