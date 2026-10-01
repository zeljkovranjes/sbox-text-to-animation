using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace TextToAnimation.Editor.Inference.Onnx;

/// <summary>
/// The performance-critical kernels of the managed runtime: a packed GEMM with an AVX2/FMA 6x16 register
/// micro-kernel (portable <see cref="Vector{T}"/> fallback), fused scaled-dot-product attention, RMSNorm,
/// and copy-based transpose/slice. All are multithreaded across cores.
/// </summary>
public static unsafe class FastKernels
{
	const int MR = 6;
	const int NR = 16;

	/// <summary>B[K,N] packed into column panels of 16: panel p holds rows k=0..K-1 of columns 16p..16p+15 (zero padded).</summary>
	public sealed class PackedMatrix
	{
		public int K, N, Panels;
		public float[] Data;
	}

	public static PackedMatrix Pack( float[] b, int K, int N )
	{
		var panels = (N + NR - 1) / NR;
		var data = new float[panels * K * NR];
		Parallel.For( 0, panels, p =>
		{
			var baseOut = p * K * NR;
			var col0 = p * NR;
			var width = Math.Min( NR, N - col0 );
			for ( var k = 0; k < K; k++ )
			{
				var row = k * N + col0;
				var o = baseOut + k * NR;
				for ( var c = 0; c < width; c++ ) data[o + c] = b[row + c];
			}
		} );
		return new PackedMatrix { K = K, N = N, Panels = panels, Data = data };
	}

	/// <summary>C[M,N] = A[M,K] · B (packed), optionally + bias[N].</summary>
	public static void Gemm( float[] a, int aOffset, int M, PackedMatrix b, float[] c, int cOffset, ExecContext ctx )
	{
		var K = b.K; var N = b.N;
		const int chunkRows = 48; // 8 micro-row blocks: A chunk stays in L2 while panels stream through
		var chunks = (M + chunkRows - 1) / chunkRows;
		var useFma = Fma.IsSupported && Avx.IsSupported;
		void Chunk( int ci )
		{
			var r0 = ci * chunkRows;
			var r1 = Math.Min( M, r0 + chunkRows );
			fixed ( float* pa = a, pb = b.Data, pc = c )
			{
				for ( var p = 0; p < b.Panels; p++ )
				{
					var panel = pb + (long)p * K * NR;
					var col0 = p * NR;
					var width = Math.Min( NR, N - col0 );
					for ( var r = r0; r < r1; r += MR )
					{
						var rows = Math.Min( MR, r1 - r );
						var arow = pa + aOffset + (long)r * K;
						var crow = pc + cOffset + (long)r * N + col0;
						if ( useFma ) Micro6x16( arow, K, panel, crow, N, rows, width );
						else MicroPortable( arow, K, panel, crow, N, rows, width );
					}
				}
			}
		}
		if ( (long)M * N * K > 1 << 18 && chunks > 1 )
			Parallel.For( 0, chunks, ctx.Parallel, Chunk );
		else
			for ( var ci = 0; ci < chunks; ci++ ) Chunk( ci );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization )]
	static void Micro6x16( float* a, int K, float* panel, float* c, int ldc, int rows, int width )
	{
		Vector256<float> c00 = default, c01 = default, c10 = default, c11 = default, c20 = default, c21 = default,
			c30 = default, c31 = default, c40 = default, c41 = default, c50 = default, c51 = default;
		var a0 = a; var a1 = a + K; var a2 = a + 2 * K; var a3 = a + 3 * K; var a4 = a + 4 * K; var a5 = a + 5 * K;
		if ( rows == MR )
		{
			for ( var k = 0; k < K; k++ )
			{
				var b0 = Avx.LoadVector256( panel + k * NR );
				var b1 = Avx.LoadVector256( panel + k * NR + 8 );
				var v = Vector256.Create( a0[k] ); c00 = Fma.MultiplyAdd( v, b0, c00 ); c01 = Fma.MultiplyAdd( v, b1, c01 );
				v = Vector256.Create( a1[k] ); c10 = Fma.MultiplyAdd( v, b0, c10 ); c11 = Fma.MultiplyAdd( v, b1, c11 );
				v = Vector256.Create( a2[k] ); c20 = Fma.MultiplyAdd( v, b0, c20 ); c21 = Fma.MultiplyAdd( v, b1, c21 );
				v = Vector256.Create( a3[k] ); c30 = Fma.MultiplyAdd( v, b0, c30 ); c31 = Fma.MultiplyAdd( v, b1, c31 );
				v = Vector256.Create( a4[k] ); c40 = Fma.MultiplyAdd( v, b0, c40 ); c41 = Fma.MultiplyAdd( v, b1, c41 );
				v = Vector256.Create( a5[k] ); c50 = Fma.MultiplyAdd( v, b0, c50 ); c51 = Fma.MultiplyAdd( v, b1, c51 );
			}
		}
		else
		{
			// partial row block: rows beyond the matrix read row 0 (results discarded)
			if ( rows < 2 ) a1 = a0; if ( rows < 3 ) a2 = a0; if ( rows < 4 ) a3 = a0; if ( rows < 5 ) a4 = a0; if ( rows < 6 ) a5 = a0;
			for ( var k = 0; k < K; k++ )
			{
				var b0 = Avx.LoadVector256( panel + k * NR );
				var b1 = Avx.LoadVector256( panel + k * NR + 8 );
				var v = Vector256.Create( a0[k] ); c00 = Fma.MultiplyAdd( v, b0, c00 ); c01 = Fma.MultiplyAdd( v, b1, c01 );
				v = Vector256.Create( a1[k] ); c10 = Fma.MultiplyAdd( v, b0, c10 ); c11 = Fma.MultiplyAdd( v, b1, c11 );
				v = Vector256.Create( a2[k] ); c20 = Fma.MultiplyAdd( v, b0, c20 ); c21 = Fma.MultiplyAdd( v, b1, c21 );
				v = Vector256.Create( a3[k] ); c30 = Fma.MultiplyAdd( v, b0, c30 ); c31 = Fma.MultiplyAdd( v, b1, c31 );
				v = Vector256.Create( a4[k] ); c40 = Fma.MultiplyAdd( v, b0, c40 ); c41 = Fma.MultiplyAdd( v, b1, c41 );
				v = Vector256.Create( a5[k] ); c50 = Fma.MultiplyAdd( v, b0, c50 ); c51 = Fma.MultiplyAdd( v, b1, c51 );
			}
		}
		Store( c, width, c00, c01 );
		if ( rows > 1 ) Store( c + ldc, width, c10, c11 );
		if ( rows > 2 ) Store( c + 2 * ldc, width, c20, c21 );
		if ( rows > 3 ) Store( c + 3 * ldc, width, c30, c31 );
		if ( rows > 4 ) Store( c + 4 * ldc, width, c40, c41 );
		if ( rows > 5 ) Store( c + 5 * ldc, width, c50, c51 );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	static void Store( float* c, int width, Vector256<float> lo, Vector256<float> hi )
	{
		if ( width == NR ) { Avx.Store( c, lo ); Avx.Store( c + 8, hi ); return; }
		var tmp = stackalloc float[NR];
		Avx.Store( tmp, lo ); Avx.Store( tmp + 8, hi );
		for ( var i = 0; i < width; i++ ) c[i] = tmp[i];
	}

	static void MicroPortable( float* a, int K, float* panel, float* c, int ldc, int rows, int width )
	{
		var acc = stackalloc float[MR * NR];
		for ( var i = 0; i < MR * NR; i++ ) acc[i] = 0;
		for ( var k = 0; k < K; k++ )
		{
			var b = panel + k * NR;
			for ( var r = 0; r < rows; r++ )
			{
				var s = a[r * K + k];
				var row = acc + r * NR;
				for ( var j = 0; j < NR; j++ ) row[j] += s * b[j];
			}
		}
		for ( var r = 0; r < rows; r++ )
			for ( var j = 0; j < width; j++ ) c[r * ldc + j] = acc[r * NR + j];
	}

	// ------------------------------------------------------------------ attention

	/// <summary>
	/// softmax(Q Kᵀ · scale + mask) V for Q (N,H,Sq,D), K/V (N,H,Sk,D); mask broadcastable to (N,H,Sq,Sk)
	/// given as (…,Sq,Sk) with leading dims of size 1 or full.
	/// </summary>
	public static Tensor Attention( Tensor q, Tensor k, Tensor v, Tensor mask, float scale, ExecContext ctx )
	{
		int N = q.Shape[0], Hq = q.Shape[1], Sq = q.Shape[2], Dh = q.Shape[3];
		int Hk = k.Shape[1], Sk = k.Shape[2], Dv = v.Shape[3];
		var groups = Hq / Hk;
		var output = new float[N * Hq * Sq * Dv];
		var qf = q.F; var kf = k.F; var vf = v.F;
		// mask strides over (n, h) with broadcasting
		float[] mf = mask?.AsFloats();
		int maskN = 0, maskH = 0;
		if ( mask is not null )
		{
			var ms = mask.Shape;
			var rank = ms.Length;
			var hDim = rank >= 3 ? ms[rank - 3] : 1;
			var nDim = rank >= 4 ? ms[rank - 4] : 1;
			maskH = hDim == 1 ? 0 : Sq * Sk;
			maskN = nDim == 1 ? 0 : hDim * Sq * Sk;
		}
		Parallel.For( 0, N * Hq, ctx.Parallel, nh =>
		{
			var n = nh / Hq; var h = nh % Hq; var hk = h / groups;
			var qBase = (n * Hq + h) * Sq * Dh;
			var kBase = (n * Hk + hk) * Sk * Dh;
			var vBase = (n * Hk + hk) * Sk * Dv;
			var oBase = (n * Hq + h) * Sq * Dv;
			var mBase = n * maskN + h * maskH;
			var scores = new float[Sk];
			for ( var i = 0; i < Sq; i++ )
			{
				var qs = qf.AsSpan( qBase + i * Dh, Dh );
				var max = float.NegativeInfinity;
				for ( var j = 0; j < Sk; j++ )
				{
					var s = Dot( qs, kf.AsSpan( kBase + j * Dh, Dh ) ) * scale;
					if ( mf is not null ) s += mf[mBase + i * Sk + j];
					scores[j] = s;
					if ( s > max ) max = s;
				}
				var sum = 0f;
				for ( var j = 0; j < Sk; j++ ) { var e = float.IsNegativeInfinity( scores[j] ) ? 0f : MathF.Exp( scores[j] - max ); scores[j] = e; sum += e; }
				var inv = sum > 0 ? 1f / sum : 0f;
				var o = output.AsSpan( oBase + i * Dv, Dv );
				var ov = MemoryMarshal.Cast<float, Vector<float>>( o );
				var tail = ov.Length * Vector<float>.Count;
				for ( var j = 0; j < Sk; j++ )
				{
					var p = scores[j] * inv;
					if ( p == 0f ) continue;
					var vs = vf.AsSpan( vBase + j * Dv, Dv );
					var vv = MemoryMarshal.Cast<float, Vector<float>>( vs );
					var pv = new Vector<float>( p );
					for ( var d = 0; d < ov.Length; d++ ) ov[d] += vv[d] * pv;
					for ( var d = tail; d < Dv; d++ ) o[d] += p * vs[d];
				}
			}
		} );
		return Tensor.Float( new[] { N, Hq, Sq, Dv }, output );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static float Dot( ReadOnlySpan<float> a, ReadOnlySpan<float> b )
	{
		var va = MemoryMarshal.Cast<float, Vector<float>>( a );
		var vb = MemoryMarshal.Cast<float, Vector<float>>( b );
		var acc = Vector<float>.Zero;
		for ( var i = 0; i < va.Length; i++ ) acc += va[i] * vb[i];
		var sum = Vector.Dot( acc, Vector<float>.One );
		for ( var i = va.Length * Vector<float>.Count; i < a.Length; i++ ) sum += a[i] * b[i];
		return sum;
	}

	// ------------------------------------------------------------------ normalisation

	public static Tensor RmsNorm( Tensor x, Tensor scale, int axis, float eps, ExecContext ctx )
	{
		if ( axis < 0 ) axis += x.Rank;
		var norm = 1;
		for ( var d = axis; d < x.Rank; d++ ) norm *= x.Shape[d];
		var rows = x.Length / Math.Max( 1, norm );
		var src = x.F; var r = new float[x.Length]; var g = scale.F;
		void Row( int row )
		{
			var s = src.AsSpan( row * norm, norm );
			var ss = Dot( s, s );
			var inv = 1f / MathF.Sqrt( ss / norm + eps );
			var o = r.AsSpan( row * norm, norm );
			if ( g.Length == norm )
			{
				var vs = MemoryMarshal.Cast<float, Vector<float>>( s );
				var vg = MemoryMarshal.Cast<float, Vector<float>>( g.AsSpan( 0, norm ) );
				var vo = MemoryMarshal.Cast<float, Vector<float>>( o );
				var vi = new Vector<float>( inv );
				for ( var i = 0; i < vo.Length; i++ ) vo[i] = vs[i] * vi * vg[i];
				for ( var i = vo.Length * Vector<float>.Count; i < norm; i++ ) o[i] = s[i] * inv * g[i];
			}
			else for ( var i = 0; i < norm; i++ ) o[i] = s[i] * inv * g[g.Length == 1 ? 0 : i % g.Length];
		}
		if ( rows > 256 ) Parallel.For( 0, rows, ctx.Parallel, Row ); else for ( var i = 0; i < rows; i++ ) Row( i );
		return Tensor.Float( x.Shape, r );
	}

	// ------------------------------------------------------------------ data movement

	/// <summary>Transpose that copies contiguous runs when the last axis stays last.</summary>
	public static Tensor TransposeRuns( Tensor t, int[] perm )
	{
		var rank = t.Rank;
		var outShape = perm.Select( i => t.Shape[i] ).ToArray();
		var inStrides = Tensor.Strides( t.Shape );
		var run = t.Shape[rank - 1];
		var outer = t.Length / Math.Max( 1, run );
		var src = perm.Take( rank - 1 ).Select( i => inStrides[i] ).ToArray();
		var dims = outShape.Take( rank - 1 ).ToArray();
		float[] f = t.IsFloat ? new float[t.Length] : null;
		long[] l = t.IsInt ? new long[t.Length] : null;
		var offsets = new int[outer];
		{
			var idx = new int[rank - 1];
			var off = 0;
			for ( var o = 0; o < outer; o++ )
			{
				offsets[o] = off;
				for ( var d = rank - 2; d >= 0; d-- )
				{
					idx[d]++; off += src[d];
					if ( idx[d] < dims[d] ) break;
					off -= src[d] * dims[d]; idx[d] = 0;
				}
			}
		}
		if ( f is not null )
		{
			if ( (long)t.Length > 1 << 16 )
				Parallel.For( 0, outer, o => Array.Copy( t.F, offsets[o], f, o * run, run ) );
			else for ( var o = 0; o < outer; o++ ) Array.Copy( t.F, offsets[o], f, o * run, run );
			return Tensor.Float( outShape, f );
		}
		for ( var o = 0; o < outer; o++ ) Array.Copy( t.L, offsets[o], l, o * run, run );
		return Tensor.Int64( outShape, l );
	}

	/// <summary>Slice [start,end) on one axis (step 1) by copying contiguous runs.</summary>
	public static Tensor SliceAxis( Tensor t, int axis, int start, int end )
	{
		var shape = (int[])t.Shape.Clone();
		var len = Math.Max( 0, end - start );
		shape[axis] = len;
		var inner = 1;
		for ( var d = axis + 1; d < t.Rank; d++ ) inner *= t.Shape[d];
		var outer = 1;
		for ( var d = 0; d < axis; d++ ) outer *= t.Shape[d];
		var dim = t.Shape[axis];
		var run = len * inner;
		if ( t.IsFloat )
		{
			var r = new float[outer * run];
			for ( var o = 0; o < outer; o++ ) Array.Copy( t.F, (o * dim + start) * inner, r, o * run, run );
			return Tensor.Float( shape, r );
		}
		if ( t.IsInt )
		{
			var r = new long[outer * run];
			for ( var o = 0; o < outer; o++ ) Array.Copy( t.L, (o * dim + start) * inner, r, o * run, run );
			return Tensor.Int64( shape, r );
		}
		var b = new bool[outer * run];
		for ( var o = 0; o < outer; o++ ) Array.Copy( t.B, (o * dim + start) * inner, b, o * run, run );
		return Tensor.Bool( shape, b );
	}

	// ------------------------------------------------------------------ broadcasting binary ops

	public enum Op { Add, Sub, Mul, Div }

	/// <summary>
	/// Elementwise op with broadcasting for float tensors: the output is walked in contiguous inner blocks in
	/// which each operand is either dense or a single repeated value, so the inner loop is vectorised.
	/// Returns null when the pattern isn't supported (the caller falls back to the generic kernel).
	/// </summary>
	public static Tensor Binary( Tensor a, Tensor b, Op op, ExecContext ctx )
	{
		var shape = OnnxOps.BroadcastShape( a.Shape, b.Shape );
		var rank = shape.Length;
		var sa = Strides( a.Shape, shape ); var sb = Strides( b.Shape, shape );
		// inner block: the trailing dims over which each operand is consistently dense (contiguous) or a
		// single broadcast value; mode per operand: 0 unknown, 1 dense, 2 broadcast
		var inner = 1; var split = rank;
		int ma = 0, mb = 0;
		for ( var d = rank - 1; d >= 0; d-- )
		{
			if ( shape[d] == 1 ) { split = d; continue; }
			int Mode( int stride, int mode ) => mode switch
			{
				0 => stride == inner ? 1 : stride == 0 ? 2 : -1,
				1 => stride == inner ? 1 : -1,
				_ => stride == 0 ? 2 : -1,
			};
			var na = Mode( sa[d], ma ); var nb = Mode( sb[d], mb );
			if ( na < 0 || nb < 0 ) break;
			ma = na; mb = nb;
			inner *= shape[d];
			split = d;
		}
		if ( inner < 8 ) return null;
		var adFlag = ma != 2; var bdFlag = mb != 2;
		var outer = Tensor.SizeOf( shape ) / inner;
		var result = new float[outer * inner];
		var af = a.F; var bf = b.F;
		var outerDims = shape.Take( split ).ToArray();
		var oa = new int[outer]; var ob = new int[outer];
		{
			var idx = new int[split];
			int ia = 0, ib = 0;
			for ( var o = 0; o < outer; o++ )
			{
				oa[o] = ia; ob[o] = ib;
				for ( var d = split - 1; d >= 0; d-- )
				{
					idx[d]++; ia += sa[d]; ib += sb[d];
					if ( idx[d] < outerDims[d] ) break;
					ia -= sa[d] * outerDims[d]; ib -= sb[d] * outerDims[d]; idx[d] = 0;
				}
			}
		}
		void Block( int o )
		{
			var r = result.AsSpan( o * inner, inner );
			var vr = MemoryMarshal.Cast<float, Vector<float>>( r );
			var w = Vector<float>.Count;
			if ( adFlag && bdFlag )
			{
				var va = MemoryMarshal.Cast<float, Vector<float>>( af.AsSpan( oa[o], inner ) );
				var vb = MemoryMarshal.Cast<float, Vector<float>>( bf.AsSpan( ob[o], inner ) );
				for ( var i = 0; i < vr.Length; i++ ) vr[i] = Apply( va[i], vb[i], op );
				for ( var i = vr.Length * w; i < inner; i++ ) r[i] = Apply( af[oa[o] + i], bf[ob[o] + i], op );
			}
			else if ( adFlag )
			{
				var s = bf[ob[o]]; var vs = new Vector<float>( s );
				var va = MemoryMarshal.Cast<float, Vector<float>>( af.AsSpan( oa[o], inner ) );
				for ( var i = 0; i < vr.Length; i++ ) vr[i] = Apply( va[i], vs, op );
				for ( var i = vr.Length * w; i < inner; i++ ) r[i] = Apply( af[oa[o] + i], s, op );
			}
			else if ( bdFlag )
			{
				var s = af[oa[o]]; var vs = new Vector<float>( s );
				var vb = MemoryMarshal.Cast<float, Vector<float>>( bf.AsSpan( ob[o], inner ) );
				for ( var i = 0; i < vr.Length; i++ ) vr[i] = Apply( vs, vb[i], op );
				for ( var i = vr.Length * w; i < inner; i++ ) r[i] = Apply( s, bf[ob[o] + i], op );
			}
			else
			{
				var v = Apply( af[oa[o]], bf[ob[o]], op );
				r.Fill( v );
			}
		}
		if ( (long)outer * inner > 1 << 15 ) Parallel.For( 0, outer, ctx.Parallel, Block );
		else for ( var o = 0; o < outer; o++ ) Block( o );
		return Tensor.Float( shape, result );
	}

	static int[] Strides( int[] shape, int[] outShape )
	{
		var s = new int[outShape.Length];
		var strides = Tensor.Strides( shape );
		var offset = outShape.Length - shape.Length;
		for ( var i = 0; i < shape.Length; i++ ) s[offset + i] = shape[i] == 1 ? 0 : strides[i];
		return s;
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	static Vector<float> Apply( Vector<float> a, Vector<float> b, Op op ) => op switch
	{
		Op.Add => a + b,
		Op.Sub => a - b,
		Op.Mul => a * b,
		_ => a / b,
	};

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	static float Apply( float a, float b, Op op ) => op switch
	{
		Op.Add => a + b,
		Op.Sub => a - b,
		Op.Mul => a * b,
		_ => a / b,
	};

	/// <summary>Vectorised logistic sigmoid.</summary>
	public static Tensor Sigmoid( Tensor x, ExecContext ctx )
	{
		var src = x.F; var r = new float[src.Length];
		void Chunk( int c )
		{
			var end = Math.Min( src.Length, (c + 1) * 16384 );
			for ( var i = c * 16384; i < end; i++ ) r[i] = 1f / (1f + MathF.Exp( -src[i] ));
		}
		var chunks = (src.Length + 16383) / 16384;
		if ( chunks > 1 ) Parallel.For( 0, chunks, ctx.Parallel, Chunk ); else Chunk( 0 );
		return Tensor.Float( x.Shape, r );
	}
}
