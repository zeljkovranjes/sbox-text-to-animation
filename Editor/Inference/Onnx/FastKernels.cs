using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>
/// The performance-critical kernels of the managed runtime: a packed GEMM with an AVX2/FMA 6x16 register
/// micro-kernel (portable <see cref="Vector{T}"/> fallback), fused scaled-dot-product attention, RMSNorm,
/// and copy-based transpose/slice. All are multithreaded across cores.
/// </summary>
public static class FastKernels
{
	/// <summary>AVX-512 (32 registers, 16 floats wide) allows a 12x32 register tile; otherwise AVX2 6x16.</summary>
	public static readonly bool UseAvx512 = Avx512F.IsSupported;
	static readonly int MR = UseAvx512 ? 12 : 6;
	static readonly int NR = UseAvx512 ? 32 : 16;

	/// <summary>B[K,N] packed into column panels of NR: panel p holds rows k=0..K-1 of columns NR*p.. (zero padded).</summary>
	public sealed class PackedMatrix
	{
		public int K, N, Panels, Nr;
		public float[] Data;
	}

	public static PackedMatrix Pack( float[] b, int K, int N )
	{
		var nr = NR;
		var panels = (N + nr - 1) / nr;
		var data = new float[panels * K * nr];
		Parallel.For( 0, panels, p =>
		{
			var baseOut = p * K * nr;
			var col0 = p * nr;
			var width = Math.Min( nr, N - col0 );
			for ( var k = 0; k < K; k++ )
			{
				var row = k * N + col0;
				var o = baseOut + k * nr;
				for ( var c = 0; c < width; c++ ) data[o + c] = b[row + c];
			}
		} );
		return new PackedMatrix { K = K, N = N, Panels = panels, Nr = nr, Data = data };
	}

	[ThreadStatic] static float[] _packedA, _acc, _biasPad, _scores, _kt;

	/// <summary>Micro-panels of A per parallel chunk (rows = MR x this); larger chunks stream B less often.</summary>
	public static int ChunkBlocks = 8;

	/// <summary>A per-thread scratch buffer of at least <paramref name="n"/> floats (contents undefined).</summary>
	static float[] Scratch( ref float[] slot, int n )
	{
		if ( slot is null || slot.Length < n ) slot = GC.AllocateUninitializedArray<float>( n );
		return slot;
	}

	/// <summary>C[M,N] = A[M,K] · B (packed). A rows are packed per chunk into [k][MR] micro-panels.</summary>
	public static unsafe void Gemm( float[] a, int aOffset, int M, PackedMatrix b, float[] c, int cOffset, ExecContext ctx, float[] bias = null )
	{
		var K = b.K; var N = b.N; var mr = MR; var nr = b.Nr;
		var chunkRows = mr * ChunkBlocks;
		var chunks = (M + chunkRows - 1) / chunkRows;
		var fma = Fma.IsSupported && Avx.IsSupported;
		void Chunk( int ci )
		{
			var r0 = ci * chunkRows;
			var r1 = Math.Min( M, r0 + chunkRows );
			var blocks = (r1 - r0 + mr - 1) / mr;
			var packedA = Scratch( ref _packedA, blocks * K * mr );
			for ( var blk = 0; blk < blocks; blk++ )
			{
				var rows = Math.Min( mr, r1 - r0 - blk * mr );
				var dst = blk * K * mr;
				for ( var r = 0; r < rows; r++ )
				{
					var src = aOffset + (r0 + blk * mr + r) * K;
					for ( var k = 0; k < K; k++ ) packedA[dst + k * mr + r] = a[src + k];
				}
			}
			var acc = Scratch( ref _acc, mr * nr );
			var biasPad = Scratch( ref _biasPad, nr );
			fixed ( float* pa = packedA, pb = b.Data, pc = c, pacc = acc, pbias = biasPad )
			{
				for ( var p = 0; p < b.Panels; p++ )
				{
					var col0 = p * nr;
					var width = Math.Min( nr, N - col0 );
					var bp = pb + (long)p * K * nr;
					for ( var blk = 0; blk < blocks; blk++ )
					{
						var rows = Math.Min( mr, r1 - r0 - blk * mr );
						var ap = pa + (long)blk * K * mr;
						var cp = pc + cOffset + (long)(r0 + blk * mr) * N + col0;
						if ( UseAvx512 ) Micro12x32( ap, bp, K, pacc );
						else if ( fma ) Micro6x16( ap, bp, K, pacc );
						else MicroPortable( ap, bp, K, pacc, mr, nr );
						if ( bias is not null )
						{
							for ( var j = 0; j < width; j++ ) pbias[j] = bias[col0 + j];
							for ( var r = 0; r < rows; r++ )
							{
								var arow = pacc + r * nr;
								for ( var j = 0; j < width; j++ ) arow[j] += pbias[j];
							}
						}
						for ( var r = 0; r < rows; r++ )
						{
							var crow = cp + (long)r * N;
							var arow = pacc + r * nr;
							if ( width == nr ) Buffer.MemoryCopy( arow, crow, nr * 4, nr * 4 );
							else for ( var j = 0; j < width; j++ ) crow[j] = arow[j];
						}
					}
				}
			}
		}
		if ( (long)M * N * K > 1 << 18 && chunks > 1 )
			Parallel.For( 0, chunks, ctx.Parallel, Chunk );
		else
			for ( var ci = 0; ci < chunks; ci++ ) Chunk( ci );
	}

	[MethodImpl( MethodImplOptions.AggressiveOptimization )]
	static unsafe void Micro12x32( float* a, float* b, int K, float* c )
	{
		Vector512<float> c0a = default, c0b = default, c1a = default, c1b = default, c2a = default, c2b = default,
			c3a = default, c3b = default, c4a = default, c4b = default, c5a = default, c5b = default,
			c6a = default, c6b = default, c7a = default, c7b = default, c8a = default, c8b = default,
			c9a = default, c9b = default, c10a = default, c10b = default, c11a = default, c11b = default;
		for ( var k = 0; k < K; k++ )
		{
			var b0 = Avx512F.LoadVector512( b );
			var b1 = Avx512F.LoadVector512( b + 16 );
			var v = Vector512.Create( a[0] ); c0a = Avx512F.FusedMultiplyAdd( v, b0, c0a ); c0b = Avx512F.FusedMultiplyAdd( v, b1, c0b );
			v = Vector512.Create( a[1] ); c1a = Avx512F.FusedMultiplyAdd( v, b0, c1a ); c1b = Avx512F.FusedMultiplyAdd( v, b1, c1b );
			v = Vector512.Create( a[2] ); c2a = Avx512F.FusedMultiplyAdd( v, b0, c2a ); c2b = Avx512F.FusedMultiplyAdd( v, b1, c2b );
			v = Vector512.Create( a[3] ); c3a = Avx512F.FusedMultiplyAdd( v, b0, c3a ); c3b = Avx512F.FusedMultiplyAdd( v, b1, c3b );
			v = Vector512.Create( a[4] ); c4a = Avx512F.FusedMultiplyAdd( v, b0, c4a ); c4b = Avx512F.FusedMultiplyAdd( v, b1, c4b );
			v = Vector512.Create( a[5] ); c5a = Avx512F.FusedMultiplyAdd( v, b0, c5a ); c5b = Avx512F.FusedMultiplyAdd( v, b1, c5b );
			v = Vector512.Create( a[6] ); c6a = Avx512F.FusedMultiplyAdd( v, b0, c6a ); c6b = Avx512F.FusedMultiplyAdd( v, b1, c6b );
			v = Vector512.Create( a[7] ); c7a = Avx512F.FusedMultiplyAdd( v, b0, c7a ); c7b = Avx512F.FusedMultiplyAdd( v, b1, c7b );
			v = Vector512.Create( a[8] ); c8a = Avx512F.FusedMultiplyAdd( v, b0, c8a ); c8b = Avx512F.FusedMultiplyAdd( v, b1, c8b );
			v = Vector512.Create( a[9] ); c9a = Avx512F.FusedMultiplyAdd( v, b0, c9a ); c9b = Avx512F.FusedMultiplyAdd( v, b1, c9b );
			v = Vector512.Create( a[10] ); c10a = Avx512F.FusedMultiplyAdd( v, b0, c10a ); c10b = Avx512F.FusedMultiplyAdd( v, b1, c10b );
			v = Vector512.Create( a[11] ); c11a = Avx512F.FusedMultiplyAdd( v, b0, c11a ); c11b = Avx512F.FusedMultiplyAdd( v, b1, c11b );
			a += 12; b += 32;
		}
		Avx512F.Store( c, c0a ); Avx512F.Store( c + 16, c0b );
		Avx512F.Store( c + 32, c1a ); Avx512F.Store( c + 48, c1b );
		Avx512F.Store( c + 64, c2a ); Avx512F.Store( c + 80, c2b );
		Avx512F.Store( c + 96, c3a ); Avx512F.Store( c + 112, c3b );
		Avx512F.Store( c + 128, c4a ); Avx512F.Store( c + 144, c4b );
		Avx512F.Store( c + 160, c5a ); Avx512F.Store( c + 176, c5b );
		Avx512F.Store( c + 192, c6a ); Avx512F.Store( c + 208, c6b );
		Avx512F.Store( c + 224, c7a ); Avx512F.Store( c + 240, c7b );
		Avx512F.Store( c + 256, c8a ); Avx512F.Store( c + 272, c8b );
		Avx512F.Store( c + 288, c9a ); Avx512F.Store( c + 304, c9b );
		Avx512F.Store( c + 320, c10a ); Avx512F.Store( c + 336, c10b );
		Avx512F.Store( c + 352, c11a ); Avx512F.Store( c + 368, c11b );
	}

	[MethodImpl( MethodImplOptions.AggressiveOptimization )]
	static unsafe void Micro6x16( float* a, float* b, int K, float* c )
	{
		Vector256<float> c00 = default, c01 = default, c10 = default, c11 = default, c20 = default, c21 = default,
			c30 = default, c31 = default, c40 = default, c41 = default, c50 = default, c51 = default;
		for ( var k = 0; k < K; k++ )
		{
			var b0 = Avx.LoadVector256( b );
			var b1 = Avx.LoadVector256( b + 8 );
			var v = Vector256.Create( a[0] ); c00 = Fma.MultiplyAdd( v, b0, c00 ); c01 = Fma.MultiplyAdd( v, b1, c01 );
			v = Vector256.Create( a[1] ); c10 = Fma.MultiplyAdd( v, b0, c10 ); c11 = Fma.MultiplyAdd( v, b1, c11 );
			v = Vector256.Create( a[2] ); c20 = Fma.MultiplyAdd( v, b0, c20 ); c21 = Fma.MultiplyAdd( v, b1, c21 );
			v = Vector256.Create( a[3] ); c30 = Fma.MultiplyAdd( v, b0, c30 ); c31 = Fma.MultiplyAdd( v, b1, c31 );
			v = Vector256.Create( a[4] ); c40 = Fma.MultiplyAdd( v, b0, c40 ); c41 = Fma.MultiplyAdd( v, b1, c41 );
			v = Vector256.Create( a[5] ); c50 = Fma.MultiplyAdd( v, b0, c50 ); c51 = Fma.MultiplyAdd( v, b1, c51 );
			a += 6; b += 16;
		}
		Avx.Store( c, c00 ); Avx.Store( c + 8, c01 );
		Avx.Store( c + 16, c10 ); Avx.Store( c + 24, c11 );
		Avx.Store( c + 32, c20 ); Avx.Store( c + 40, c21 );
		Avx.Store( c + 48, c30 ); Avx.Store( c + 56, c31 );
		Avx.Store( c + 64, c40 ); Avx.Store( c + 72, c41 );
		Avx.Store( c + 80, c50 ); Avx.Store( c + 88, c51 );
	}

	static unsafe void MicroPortable( float* a, float* b, int K, float* c, int mr, int nr )
	{
		for ( var i = 0; i < mr * nr; i++ ) c[i] = 0;
		for ( var k = 0; k < K; k++ )
		{
			var bk = b + k * nr;
			for ( var r = 0; r < mr; r++ )
			{
				var s = a[k * mr + r];
				var row = c + r * nr;
				for ( var j = 0; j < nr; j++ ) row[j] += s * bk[j];
			}
		}
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
		var output = ExecContext.AllocZeroed( N * Hq * Sq * Dv );
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
		if ( Fma.IsSupported && Avx.IsSupported )
		{
			Parallel.For( 0, N * Hq, ctx.Parallel, nh =>
			{
				var n = nh / Hq; var h = nh % Hq; var hk = h / groups;
				AttentionHead( qf, kf, vf, mf, output, (n * Hq + h) * Sq * Dh, (n * Hk + hk) * Sk * Dh, (n * Hk + hk) * Sk * Dv,
					(n * Hq + h) * Sq * Dv, n * maskN + h * maskH, Sq, Sk, Dh, Dv, scale );
			} );
			return Tensor.Float( new[] { N, Hq, Sq, Dv }, output );
		}
		Parallel.For( 0, N * Hq, ctx.Parallel, nh =>
		{
			var n = nh / Hq; var h = nh % Hq; var hk = h / groups;
			var qBase = (n * Hq + h) * Sq * Dh;
			var kBase = (n * Hk + hk) * Sk * Dh;
			var vBase = (n * Hk + hk) * Sk * Dv;
			var oBase = (n * Hq + h) * Sq * Dv;
			var mBase = n * maskN + h * maskH;
			var scores = Scratch( ref _scores, Sk );
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

	/// <summary>
	/// One head of attention, blocked like a GEMM: K is transposed so keys run along the vector lanes, four query
	/// rows share every K and V load, and the softmax exponentials are vectorised. Same arithmetic as the plain
	/// loop (dot, scale, mask, max-shifted softmax, weighted sum of V); only the summation order differs.
	/// </summary>
	static unsafe void AttentionHead( float[] qf, float[] kf, float[] vf, float[] mf, float[] output, int qBase, int kBase, int vBase,
		int oBase, int mBase, int Sq, int Sk, int Dh, int Dv, float scale )
	{
		var skPad = (Sk + 7) & ~7;
		var kt = Scratch( ref _kt, Dh * skPad );
		var sc = Scratch( ref _scores, 4 * skPad );
		fixed ( float* q = qf, k = kf, v = vf, o = output, ktp = kt, s = sc )
		{
			for ( var d = 0; d < Dh; d++ )
			{
				var row = ktp + d * skPad;
				for ( var j = 0; j < Sk; j++ ) row[j] = k[kBase + j * Dh + d];
				for ( var j = Sk; j < skPad; j++ ) row[j] = 0f;
			}
			var vscale = Vector256.Create( scale );
			var negInf = Vector256.Create( float.NegativeInfinity );
			for ( var i0 = 0; i0 < Sq; i0 += 4 )
			{
				var rows = Math.Min( 4, Sq - i0 );
				float* q0 = q + qBase + i0 * Dh;
				float* q1 = rows > 1 ? q0 + Dh : q0, q2 = rows > 2 ? q0 + 2 * Dh : q0, q3 = rows > 3 ? q0 + 3 * Dh : q0;
				// scores (rows x Sk): Q K^T * scale, eight keys per register, four rows per K load
				for ( var j = 0; j < skPad; j += 8 )
				{
					Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
					var kp = ktp + j;
					for ( var d = 0; d < Dh; d++, kp += skPad )
					{
						var kv = Avx.LoadVector256( kp );
						a0 = Fma.MultiplyAdd( Vector256.Create( q0[d] ), kv, a0 );
						a1 = Fma.MultiplyAdd( Vector256.Create( q1[d] ), kv, a1 );
						a2 = Fma.MultiplyAdd( Vector256.Create( q2[d] ), kv, a2 );
						a3 = Fma.MultiplyAdd( Vector256.Create( q3[d] ), kv, a3 );
					}
					Avx.Store( s + j, Avx.Multiply( a0, vscale ) );
					Avx.Store( s + skPad + j, Avx.Multiply( a1, vscale ) );
					Avx.Store( s + 2 * skPad + j, Avx.Multiply( a2, vscale ) );
					Avx.Store( s + 3 * skPad + j, Avx.Multiply( a3, vscale ) );
				}
				// softmax per row (mask added, padding keys excluded)
				for ( var r = 0; r < rows; r++ )
				{
					var sr = s + r * skPad;
					if ( mf is not null )
					{
						var mrow = mBase + (i0 + r) * Sk;
						for ( var j = 0; j < Sk; j++ ) sr[j] += mf[mrow + j];
					}
					for ( var j = Sk; j < skPad; j++ ) sr[j] = float.NegativeInfinity;
					var max = float.NegativeInfinity;
					for ( var j = 0; j < Sk; j++ ) if ( sr[j] > max ) max = sr[j];
					if ( float.IsNegativeInfinity( max ) )
					{
						for ( var j = 0; j < skPad; j++ ) sr[j] = 0f; // every key masked: the plain loop's output is zero too
						continue;
					}
					var vmax = Vector256.Create( max );
					var vsum = Vector256<float>.Zero;
					for ( var j = 0; j < skPad; j += 8 )
					{
						var x = Avx.LoadVector256( sr + j );
						var e = Vector256.Exp( Avx.Subtract( x, vmax ) );
						e = Avx.BlendVariable( e, Vector256<float>.Zero, Avx.CompareEqual( x, negInf ) );
						Avx.Store( sr + j, e );
						vsum = Avx.Add( vsum, e );
					}
					var sum = Vector256.Sum( vsum );
					var inv = Vector256.Create( sum > 0 ? 1f / sum : 0f );
					for ( var j = 0; j < skPad; j += 8 ) Avx.Store( sr + j, Avx.Multiply( Avx.LoadVector256( sr + j ), inv ) );
				}
				// output rows: P V, four rows per V load
				float* p0 = s, p1 = s + skPad, p2 = s + 2 * skPad, p3 = s + 3 * skPad;
				float* o0 = o + oBase + i0 * Dv;
				var dv8 = Dv & ~7;
				for ( var c = 0; c < dv8; c += 8 )
				{
					Vector256<float> a0 = default, a1 = default, a2 = default, a3 = default;
					var vp = v + vBase + c;
					for ( var j = 0; j < Sk; j++, vp += Dv )
					{
						var vv = Avx.LoadVector256( vp );
						a0 = Fma.MultiplyAdd( Vector256.Create( p0[j] ), vv, a0 );
						a1 = Fma.MultiplyAdd( Vector256.Create( p1[j] ), vv, a1 );
						a2 = Fma.MultiplyAdd( Vector256.Create( p2[j] ), vv, a2 );
						a3 = Fma.MultiplyAdd( Vector256.Create( p3[j] ), vv, a3 );
					}
					Avx.Store( o0 + c, a0 );
					if ( rows > 1 ) Avx.Store( o0 + Dv + c, a1 );
					if ( rows > 2 ) Avx.Store( o0 + 2 * Dv + c, a2 );
					if ( rows > 3 ) Avx.Store( o0 + 3 * Dv + c, a3 );
				}
				for ( var c = dv8; c < Dv; c++ )
					for ( var r = 0; r < rows; r++ )
					{
						var acc = 0f;
						for ( var j = 0; j < Sk; j++ ) acc += s[r * skPad + j] * v[vBase + j * Dv + c];
						o0[r * Dv + c] = acc;
					}
			}
		}
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
		var src = x.F; var r = ExecContext.Alloc( x.Length ); var g = scale.F;
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
		float[] f = t.IsFloat ? ExecContext.Alloc( t.Length ) : null;
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
			var r = ExecContext.Alloc( outer * run );
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
		var result = ExecContext.Alloc( outer * inner );
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

	static readonly ConditionalWeakTable<Tensor, RotationInfo> RotationMaps = new();

	/// <summary>Source index and sign per output column of a signed permutation matrix.</summary>
	public sealed record RotationInfo( int[] Src, float[] Sign );

	/// <summary>For a square matrix that is a signed permutation, (source index, sign) per output column; else null.</summary>
	public static RotationInfo RotationMap( Tensor r )
	{
		if ( r.Rank != 2 || r.Shape[0] != r.Shape[1] || !r.IsFloat || r.Shape[0] > 256 ) return null;
		if ( RotationMaps.TryGetValue( r, out var cached ) ) return cached;
		var n = r.Shape[0];
		var src = new int[n]; var sign = new float[n];
		for ( var col = 0; col < n; col++ )
		{
			var found = -1;
			for ( var row = 0; row < n; row++ )
			{
				var v = r.F[row * n + col];
				if ( v == 0 ) continue;
				if ( found >= 0 || MathF.Abs( MathF.Abs( v ) - 1 ) > 1e-6f ) return null;
				found = row; sign[col] = v;
			}
			if ( found < 0 ) return null;
			src[col] = found;
		}
		var info = new RotationInfo( src, sign );
		RotationMaps.AddOrUpdate( r, info );
		return info;
	}

	/// <summary>Fused rotary embedding: x*cos + (x·R)*sin with R a signed permutation; cos/sin hold one row
	/// per position of the second-to-last axis of x.</summary>
	public static Tensor Rope( Tensor x, Tensor cos, Tensor sin, Tensor r, ExecContext ctx )
	{
		var map = RotationMap( r ) ?? throw new InvalidOperationException( "Rope needs a signed permutation matrix." );
		var d = x.Shape[^1];
		var positions = x.Shape[^2];
		if ( cos.Length != positions * d || sin.Length != positions * d )
			throw new InvalidOperationException( "Rope cos/sin must have one row per position." );
		var rows = x.Length / d;
		var src = x.F; var cf = cos.F; var sf = sin.F;
		var output = ExecContext.Alloc( x.Length );
		var perm = map.Src; var sign = map.Sign;
		void Row( int row )
		{
			var o = row * d;
			var p = (row % positions) * d;
			for ( var i = 0; i < d; i++ ) output[o + i] = src[o + i] * cf[p + i] + sign[i] * src[o + perm[i]] * sf[p + i];
		}
		if ( rows > 2048 ) Parallel.For( 0, rows, ctx.Parallel, Row ); else for ( var i = 0; i < rows; i++ ) Row( i );
		return Tensor.Float( x.Shape, output );
	}

	/// <summary>a*b + c with broadcasting (inner blocks vectorised); null when the pattern isn't supported.</summary>
	public static Tensor MulAdd( Tensor a, Tensor b, Tensor c, ExecContext ctx )
	{
		if ( !a.IsFloat || !b.IsFloat || !c.IsFloat ) return null;
		var shape = OnnxOps.BroadcastShape( OnnxOps.BroadcastShape( a.Shape, b.Shape ), c.Shape );
		var rank = shape.Length;
		var s = new[] { Strides( a.Shape, shape ), Strides( b.Shape, shape ), Strides( c.Shape, shape ) };
		var inner = 1; var split = rank;
		var modes = new int[3];
		for ( var dim = rank - 1; dim >= 0; dim-- )
		{
			if ( shape[dim] == 1 ) { split = dim; continue; }
			var next = new int[3];
			var ok = true;
			for ( var k = 0; k < 3 && ok; k++ )
			{
				var st = s[k][dim];
				next[k] = modes[k] switch
				{
					0 => st == inner ? 1 : st == 0 ? 2 : -1,
					1 => st == inner ? 1 : -1,
					_ => st == 0 ? 2 : -1,
				};
				ok = next[k] > 0;
			}
			if ( !ok ) break;
			modes = next;
			inner *= shape[dim];
			split = dim;
		}
		if ( inner < 8 ) return null;
		var total = Tensor.SizeOf( shape );
		var outer = total / inner;
		var result = ExecContext.Alloc( total );
		var outerDims = shape.Take( split ).ToArray();
		var offs = new int[3][];
		for ( var k = 0; k < 3; k++ ) offs[k] = new int[outer];
		{
			var idx = new int[split];
			var cur = new int[3];
			for ( var o = 0; o < outer; o++ )
			{
				for ( var k = 0; k < 3; k++ ) offs[k][o] = cur[k];
				for ( var dim = split - 1; dim >= 0; dim-- )
				{
					idx[dim]++;
					for ( var k = 0; k < 3; k++ ) cur[k] += s[k][dim];
					if ( idx[dim] < outerDims[dim] ) break;
					for ( var k = 0; k < 3; k++ ) cur[k] -= s[k][dim] * outerDims[dim];
					idx[dim] = 0;
				}
			}
		}
		var af = a.F; var bf = b.F; var cf = c.F;
		var da = modes[0] != 2; var db = modes[1] != 2; var dc = modes[2] != 2;
		var w = Vector<float>.Count;
		void Block( int o )
		{
			var ra = offs[0][o]; var rb = offs[1][o]; var rc = offs[2][o];
			var ro = o * inner;
			var va = new Vector<float>( af[ra] ); var vb = new Vector<float>( bf[rb] ); var vc = new Vector<float>( cf[rc] );
			var i = 0;
			for ( ; i + w <= inner; i += w )
			{
				var x = da ? new Vector<float>( af, ra + i ) : va;
				var y = db ? new Vector<float>( bf, rb + i ) : vb;
				var z = dc ? new Vector<float>( cf, rc + i ) : vc;
				(x * y + z).CopyTo( result, ro + i );
			}
			for ( ; i < inner; i++ )
				result[ro + i] = af[da ? ra + i : ra] * bf[db ? rb + i : rb] + cf[dc ? rc + i : rc];
		}
		if ( (long)total > 1 << 15 ) Parallel.For( 0, outer, ctx.Parallel, Block ); else for ( var o = 0; o < outer; o++ ) Block( o );
		return Tensor.Float( shape, result );
	}

	/// <summary>x * sigmoid(x).</summary>
	public static unsafe Tensor SiLU( Tensor x, ExecContext ctx )
	{
		var src = x.F; var r = ExecContext.Alloc( src.Length );
		void Chunk( int c )
		{
			var end = Math.Min( src.Length, (c + 1) * 16384 );
			var i = c * 16384;
			if ( Vector256.IsHardwareAccelerated )
			{
				var one = Vector256.Create( 1f );
				fixed ( float* ps = src, pr = r )
					for ( ; i + 8 <= end; i += 8 )
					{
						var v = Vector256.Load( ps + i );
						Vector256.Store( v / (one + Vector256.Exp( -v )), pr + i );
					}
			}
			for ( ; i < end; i++ ) { var v = src[i]; r[i] = v / (1f + MathF.Exp( -v )); }
		}
		var chunks = (src.Length + 16383) / 16384;
		if ( chunks > 1 ) Parallel.For( 0, chunks, ctx.Parallel, Chunk ); else Chunk( 0 );
		return Tensor.Float( x.Shape, r );
	}

	/// <summary>Vectorised logistic sigmoid.</summary>
	public static Tensor Sigmoid( Tensor x, ExecContext ctx )
	{
		var src = x.F; var r = ExecContext.Alloc( src.Length );
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
