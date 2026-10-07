using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>
/// The operator kernels of the managed ONNX interpreter (opset 13-18 semantics, NumPy broadcasting).
/// Floating math is fp32; heavy kernels (MatMul/Gemm, softmax, normalization) use <see cref="Vector{T}"/>
/// and run across cores.
/// </summary>
public static class OnnxOps
{
	public delegate Tensor[] Kernel( OnnxNode node, Tensor[] inputs, ExecContext ctx );

	static readonly Dictionary<string, Kernel> Kernels = new( StringComparer.Ordinal )
	{
		["Identity"] = ( n, i, c ) => new[] { i[0] },
		["Dropout"] = ( n, i, c ) => new[] { i[0], Tensor.Bool( i[0].Shape ) },
		["Add"] = ( n, i, c ) => new[] { FastBinary( i[0], i[1], BinOp.Add, FastKernels.Op.Add, c ) },
		["Sub"] = ( n, i, c ) => new[] { FastBinary( i[0], i[1], BinOp.Sub, FastKernels.Op.Sub, c ) },
		["Mul"] = ( n, i, c ) => new[] { FastBinary( i[0], i[1], BinOp.Mul, FastKernels.Op.Mul, c ) },
		["Div"] = ( n, i, c ) => new[] { FastBinary( i[0], i[1], BinOp.Div, FastKernels.Op.Div, c ) },
		["RMSNormalization"] = ( n, i, c ) => new[] { FastKernels.RmsNorm( i[0], i[1], (int)n.GetInt( "axis", -1 ), n.GetFloat( "epsilon", 1e-5f ), c ) },
		["Attention"] = ( n, i, c ) => new[] { FastKernels.Attention( i[0], i[1], i[2], i.Length > 3 ? i[3] : null,
			n.Attributes.ContainsKey( "scale" ) ? n.GetFloat( "scale", 1f ) : 1f / MathF.Sqrt( i[0].Shape[^1] ), c ) },
		["Pow"] = ( n, i, c ) => new[] { Binary( i[0], i[1], BinOp.Pow ) },
		["Max"] = ( n, i, c ) => new[] { i.Skip( 1 ).Aggregate( i[0], ( a, b ) => Binary( a, b, BinOp.Max ) ) },
		["Min"] = ( n, i, c ) => new[] { i.Skip( 1 ).Aggregate( i[0], ( a, b ) => Binary( a, b, BinOp.Min ) ) },
		["Sum"] = ( n, i, c ) => new[] { i.Skip( 1 ).Aggregate( i[0], ( a, b ) => Binary( a, b, BinOp.Add ) ) },
		["Mod"] = ( n, i, c ) => new[] { Binary( i[0], i[1], n.GetInt( "fmod", 0 ) == 1 ? BinOp.FMod : BinOp.Mod ) },
		["Equal"] = ( n, i, c ) => new[] { Compare( i[0], i[1], CmpOp.Eq ) },
		["Less"] = ( n, i, c ) => new[] { Compare( i[0], i[1], CmpOp.Lt ) },
		["LessOrEqual"] = ( n, i, c ) => new[] { Compare( i[0], i[1], CmpOp.Le ) },
		["Greater"] = ( n, i, c ) => new[] { Compare( i[0], i[1], CmpOp.Gt ) },
		["GreaterOrEqual"] = ( n, i, c ) => new[] { Compare( i[0], i[1], CmpOp.Ge ) },
		["And"] = ( n, i, c ) => new[] { Logical( i[0], i[1], ( a, b ) => a && b ) },
		["Or"] = ( n, i, c ) => new[] { Logical( i[0], i[1], ( a, b ) => a || b ) },
		["Xor"] = ( n, i, c ) => new[] { Logical( i[0], i[1], ( a, b ) => a ^ b ) },
		["Not"] = ( n, i, c ) => new[] { Tensor.Bool( i[0].Shape, i[0].B.Select( v => !v ).ToArray() ) },
		["Where"] = ( n, i, c ) => new[] { Where( i[0], i[1], i[2] ) },
		["Sqrt"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Sqrt ) },
		["Exp"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Exp ) },
		["Log"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Log ) },
		["Tanh"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Tanh ) },
		["Sin"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Sin ) },
		["Cos"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Cos ) },
		["Floor"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Floor ) },
		["Ceil"] = ( n, i, c ) => new[] { Unary( i[0], MathF.Ceiling ) },
		["Round"] = ( n, i, c ) => new[] { Unary( i[0], v => MathF.Round( v, MidpointRounding.ToEven ) ) },
		["Reciprocal"] = ( n, i, c ) => new[] { Unary( i[0], v => 1f / v ) },
		["Sigmoid"] = ( n, i, c ) => new[] { FastKernels.Sigmoid( i[0], c ) },
		["Relu"] = ( n, i, c ) => new[] { Unary( i[0], v => v > 0 ? v : 0 ) },
		["Erf"] = ( n, i, c ) => new[] { Unary( i[0], Erf ) },
		["Gelu"] = ( n, i, c ) => new[] { Unary( i[0], n.GetString( "approximate", "none" ) == "tanh"
			? v => 0.5f * v * (1f + MathF.Tanh( 0.7978845608f * (v + 0.044715f * v * v * v) ))
			: v => 0.5f * v * (1f + Erf( v * 0.70710678f )) ) },
		["Neg"] = ( n, i, c ) => new[] { i[0].IsInt ? Tensor.Int64( i[0].Shape, i[0].L.Select( v => -v ).ToArray() ) : Unary( i[0], v => -v ) },
		["Abs"] = ( n, i, c ) => new[] { i[0].IsInt ? Tensor.Int64( i[0].Shape, i[0].L.Select( Math.Abs ).ToArray() ) : Unary( i[0], MathF.Abs ) },
		["Sign"] = ( n, i, c ) => new[] { Unary( i[0], v => MathF.Sign( v ) ) },
		["IsNaN"] = ( n, i, c ) => new[] { Tensor.Bool( i[0].Shape, i[0].F.Select( float.IsNaN ).ToArray() ) },
		["Clip"] = ( n, i, c ) => new[] { Clip( n, i ) },
		["Cast"] = ( n, i, c ) => new[] { Cast( i[0], (OnnxType)n.GetInt( "to", 1 ) ) },
		["CastLike"] = ( n, i, c ) => new[] { Cast( i[0], i[1].Type ) },
		["Shape"] = ( n, i, c ) => new[] { ShapeOf( n, i[0] ) },
		["Size"] = ( n, i, c ) => new[] { Tensor.ScalarInt( i[0].Length ) },
		["Reshape"] = ( n, i, c ) => new[] { Reshape( i[0], i[1].AsLongs(), n.GetInt( "allowzero", 0 ) == 1 ) },
		["Flatten"] = ( n, i, c ) => new[] { Flatten( i[0], (int)n.GetInt( "axis", 1 ) ) },
		["Unsqueeze"] = ( n, i, c ) => new[] { Unsqueeze( i[0], i.Length > 1 ? i[1].AsLongs() : n.GetInts( "axes" ) ) },
		["Squeeze"] = ( n, i, c ) => new[] { Squeeze( i[0], i.Length > 1 && i[1] is not null ? i[1].AsLongs() : n.GetInts( "axes" ) ) },
		["Transpose"] = ( n, i, c ) => new[] { FastTranspose( i[0], n.GetInts( "perm" ) ) },
		["Concat"] = ( n, i, c ) => new[] { Concat( i.Where( t => t is not null ).ToArray(), (int)n.GetInt( "axis", 0 ) ) },
		["Split"] = ( n, i, c ) => Split( n, i ),
		["Slice"] = ( n, i, c ) => new[] { FastSlice( i ) ?? Slice( i ) },
		["Gather"] = ( n, i, c ) => new[] { Gather( i[0], i[1], (int)n.GetInt( "axis", 0 ) ) },
		["GatherElements"] = ( n, i, c ) => new[] { GatherElements( i[0], i[1], (int)n.GetInt( "axis", 0 ) ) },
		["Expand"] = ( n, i, c ) => new[] { Expand( i[0], i[1].AsLongs() ) },
		["Tile"] = ( n, i, c ) => new[] { Tile( i[0], i[1].AsLongs() ) },
		["Range"] = ( n, i, c ) => new[] { Range( i[0], i[1], i[2] ) },
		["Constant"] = ( n, i, c ) => new[] { ConstantOf( n ) },
		["ConstantOfShape"] = ( n, i, c ) => new[] { ConstantOfShape( n, i[0].AsLongs() ) },
		["Trilu"] = ( n, i, c ) => new[] { Trilu( i[0], i.Length > 1 && i[1] is not null ? i[1].AsLongs()[0] : 0, n.GetInt( "upper", 1 ) == 1 ) },
		["CumSum"] = ( n, i, c ) => new[] { CumSum( i[0], (int)i[1].AsLongs()[0], n.GetInt( "exclusive", 0 ) == 1, n.GetInt( "reverse", 0 ) == 1 ) },
		["ReduceMean"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.Mean ) },
		["ReduceSum"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.Sum ) },
		["ReduceMax"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.Max ) },
		["ReduceMin"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.Min ) },
		["ReduceProd"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.Prod ) },
		["ReduceL2"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.L2 ) },
		["ReduceSumSquare"] = ( n, i, c ) => new[] { Reduce( n, i, ReduceOp.SumSquare ) },
		["ArgMax"] = ( n, i, c ) => new[] { ArgMax( i[0], (int)n.GetInt( "axis", 0 ), n.GetInt( "keepdims", 1 ) == 1 ) },
		["Softmax"] = ( n, i, c ) => new[] { Softmax( i[0], (int)n.GetInt( "axis", -1 ), c, log: false ) },
		["LogSoftmax"] = ( n, i, c ) => new[] { Softmax( i[0], (int)n.GetInt( "axis", -1 ), c, log: true ) },
		["LayerNormalization"] = ( n, i, c ) => new[] { LayerNorm( i[0], i[1], i.Length > 2 ? i[2] : null, (int)n.GetInt( "axis", -1 ), n.GetFloat( "epsilon", 1e-5f ), c ) },
		["SimplifiedLayerNormalization"] = ( n, i, c ) => new[] { RmsNorm( i[0], i[1], (int)n.GetInt( "axis", -1 ), n.GetFloat( "epsilon", 1e-6f ), c ) },
		["MatMul"] = ( n, i, c ) => new[] { MatMul( i[0], i[1], c ) },
		// runtime fusions (see OnnxSession.Fuse)
		["MatMulBias"] = ( n, i, c ) => new[] { MatMulBias( i[0], i[1], i[2], c ) },
		["SiLU"] = ( n, i, c ) => new[] { FastKernels.SiLU( i[0], c ) },
		["Rope"] = ( n, i, c ) => new[] { FastKernels.Rope( i[0], i[1], i[2], i[3], c ) },
		["MulAdd"] = ( n, i, c ) => new[] { FastKernels.MulAdd( i[0], i[1], i[2], c )
			?? FastBinary( FastBinary( i[0], i[1], BinOp.Mul, FastKernels.Op.Mul, c ), i[2], BinOp.Add, FastKernels.Op.Add, c ) },
		["Gemm"] = ( n, i, c ) => new[] { Gemm( n, i, c ) },
	};

	public static bool Supports( string op ) => Kernels.ContainsKey( op );
	public static IEnumerable<string> SupportedOps => Kernels.Keys;

	public static Tensor[] Run( OnnxNode node, Tensor[] inputs, ExecContext ctx )
	{
		if ( !Kernels.TryGetValue( node.OpType, out var kernel ) )
			throw new NotSupportedException( $"ONNX operator {node.OpType} is not supported by the managed runtime." );
		return kernel( node, inputs, ctx );
	}

	// ======================================================================== elementwise

	enum BinOp { Add, Sub, Mul, Div, Pow, Max, Min, Mod, FMod }

	static Tensor FastBinary( Tensor a, Tensor b, BinOp op, FastKernels.Op fast, ExecContext ctx )
	{
		if ( a.IsFloat && b.IsFloat && (a.Length > 64 || b.Length > 64) )
		{
			var r = FastKernels.Binary( a, b, fast, ctx );
			if ( r is not null ) return r;
		}
		return Binary( a, b, op );
	}

	static Tensor FastTranspose( Tensor t, long[] perm )
	{
		var p = perm is { Length: > 0 } ? perm.Select( x => (int)x ).ToArray() : Enumerable.Range( 0, t.Rank ).Reverse().ToArray();
		if ( t.Rank >= 2 && p[^1] == t.Rank - 1 && !t.IsBool && t.Shape[^1] >= 4 ) return FastKernels.TransposeRuns( t, p );
		return Transpose( t, perm );
	}

	static Tensor FastSlice( Tensor[] i )
	{
		var t = i[0];
		var starts = i[1].AsLongs(); var ends = i[2].AsLongs();
		if ( starts.Length != 1 ) return null;
		var axis = i.Length > 3 && i[3] is not null ? (int)i[3].AsLongs()[0] : 0;
		if ( axis < 0 ) axis += t.Rank;
		if ( i.Length > 4 && i[4] is not null && i[4].AsLongs()[0] != 1 ) return null;
		var dim = t.Shape[axis];
		long s = starts[0], e = ends[0];
		if ( s < 0 ) s += dim; if ( e < 0 ) e += dim;
		s = Math.Clamp( s, 0, dim ); e = Math.Clamp( e, 0, dim );
		return FastKernels.SliceAxis( t, axis, (int)s, (int)Math.Max( s, e ) );
	}
	enum CmpOp { Eq, Lt, Le, Gt, Ge }

	public static int[] BroadcastShape( int[] a, int[] b )
	{
		var rank = Math.Max( a.Length, b.Length );
		var r = new int[rank];
		for ( var i = 0; i < rank; i++ )
		{
			var da = i - (rank - a.Length) >= 0 ? a[i - (rank - a.Length)] : 1;
			var db = i - (rank - b.Length) >= 0 ? b[i - (rank - b.Length)] : 1;
			if ( da != db && da != 1 && db != 1 )
				throw new ArgumentException( $"Shapes [{string.Join( ",", a )}] and [{string.Join( ",", b )}] don't broadcast." );
			r[i] = da == 1 ? db : da;
		}
		return r;
	}

	/// <summary>Element strides of <paramref name="shape"/> laid over a broadcast output shape (0 for broadcast dims).</summary>
	static int[] BroadcastStrides( int[] shape, int[] outShape )
	{
		var s = new int[outShape.Length];
		var strides = Tensor.Strides( shape );
		var offset = outShape.Length - shape.Length;
		for ( var i = 0; i < shape.Length; i++ ) s[offset + i] = shape[i] == 1 ? 0 : strides[i];
		return s;
	}

	/// <summary>Calls <paramref name="body"/>(outIndex, aIndex, bIndex) for every output element.</summary>
	static void ForEachBroadcast( int[] outShape, int[] sa, int[] sb, Action<int, int, int> body )
	{
		var n = Tensor.SizeOf( outShape );
		var rank = outShape.Length;
		var idx = new int[rank];
		int ia = 0, ib = 0;
		for ( var o = 0; o < n; o++ )
		{
			body( o, ia, ib );
			for ( var d = rank - 1; d >= 0; d-- )
			{
				idx[d]++;
				ia += sa[d]; ib += sb[d];
				if ( idx[d] < outShape[d] ) break;
				ia -= sa[d] * outShape[d]; ib -= sb[d] * outShape[d];
				idx[d] = 0;
			}
		}
	}

	static Tensor Binary( Tensor a, Tensor b, BinOp op )
	{
		if ( a.IsInt && b.IsInt ) return BinaryInt( a, b, op );
		var fa = a.AsFloats(); var fb = b.AsFloats();
		var shape = BroadcastShape( a.Shape, b.Shape );
		var result = ExecContext.AllocZeroed( Tensor.SizeOf( shape ) );
		if ( a.Length == result.Length && b.Length == result.Length )
		{
			VecBinary( fa, 0, fb, 0, result, 0, result.Length, op, false );
		}
		else if ( a.Length == result.Length && IsSuffix( b.Shape, shape ) )
		{
			// b repeats every b.Length elements (bias-style broadcast)
			var inner = b.Length;
			for ( var o = 0; o < result.Length; o += inner ) VecBinary( fa, o, fb, 0, result, o, inner, op, false );
		}
		else if ( b.Length == 1 )
		{
			var s = fb[0];
			for ( var o = 0; o < result.Length; o++ ) result[o] = Apply( fa[a.Length == 1 ? 0 : o], s, op );
		}
		else if ( a.Length == 1 )
		{
			var s = fa[0];
			for ( var o = 0; o < result.Length; o++ ) result[o] = Apply( s, fb[o], op );
		}
		else
		{
			ForEachBroadcast( shape, BroadcastStrides( a.Shape, shape ), BroadcastStrides( b.Shape, shape ),
				( o, ia, ib ) => result[o] = Apply( fa[ia], fb[ib], op ) );
		}
		return Tensor.Float( shape, result );
	}

	static bool IsSuffix( int[] small, int[] shape )
	{
		// small (ignoring leading 1s) equals the trailing dims of shape
		var s = small.SkipWhile( d => d == 1 ).ToArray();
		if ( s.Length > shape.Length ) return false;
		for ( var i = 0; i < s.Length; i++ ) if ( s[^(i + 1)] != shape[^(i + 1)] ) return false;
		return s.Length > 0;
	}

	static void VecBinary( float[] a, int ao, float[] b, int bo, float[] r, int ro, int count, BinOp op, bool _ )
	{
		var i = 0;
		var w = Vector<float>.Count;
		if ( op is BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Div or BinOp.Max or BinOp.Min )
		{
			var va = MemoryMarshal.Cast<float, Vector<float>>( a.AsSpan( ao, count ) );
			var vb = MemoryMarshal.Cast<float, Vector<float>>( b.AsSpan( bo, count ) );
			var vr = MemoryMarshal.Cast<float, Vector<float>>( r.AsSpan( ro, count ) );
			for ( var k = 0; k < vr.Length; k++ )
			{
				vr[k] = op switch
				{
					BinOp.Add => va[k] + vb[k],
					BinOp.Sub => va[k] - vb[k],
					BinOp.Mul => va[k] * vb[k],
					BinOp.Div => va[k] / vb[k],
					BinOp.Max => Vector.Max( va[k], vb[k] ),
					_ => Vector.Min( va[k], vb[k] ),
				};
			}
			i = vr.Length * w;
		}
		for ( ; i < count; i++ ) r[ro + i] = Apply( a[ao + i], b[bo + i], op );
	}

	static float Apply( float a, float b, BinOp op ) => op switch
	{
		BinOp.Add => a + b,
		BinOp.Sub => a - b,
		BinOp.Mul => a * b,
		BinOp.Div => a / b,
		BinOp.Pow => b == 2f ? a * a : b == 0.5f ? MathF.Sqrt( a ) : MathF.Pow( a, b ),
		BinOp.Max => MathF.Max( a, b ),
		BinOp.Min => MathF.Min( a, b ),
		BinOp.FMod => a % b,
		_ => a - MathF.Floor( a / b ) * b,
	};

	static Tensor BinaryInt( Tensor a, Tensor b, BinOp op )
	{
		var shape = BroadcastShape( a.Shape, b.Shape );
		var r = new long[Tensor.SizeOf( shape )];
		ForEachBroadcast( shape, BroadcastStrides( a.Shape, shape ), BroadcastStrides( b.Shape, shape ), ( o, ia, ib ) =>
		{
			long x = a.L[ia], y = b.L[ib];
			r[o] = op switch
			{
				BinOp.Add => x + y,
				BinOp.Sub => x - y,
				BinOp.Mul => x * y,
				BinOp.Div => y == 0 ? 0 : x / y,
				BinOp.Pow => (long)Math.Pow( x, y ),
				BinOp.Max => Math.Max( x, y ),
				BinOp.Min => Math.Min( x, y ),
				BinOp.FMod => y == 0 ? 0 : x % y,
				_ => y == 0 ? 0 : ((x % y) + y) % y,
			};
		} );
		return Tensor.Int64( shape, r );
	}

	static Tensor Compare( Tensor a, Tensor b, CmpOp op )
	{
		var shape = BroadcastShape( a.Shape, b.Shape );
		var r = new bool[Tensor.SizeOf( shape )];
		var sa = BroadcastStrides( a.Shape, shape ); var sb = BroadcastStrides( b.Shape, shape );
		if ( a.IsInt && b.IsInt )
			ForEachBroadcast( shape, sa, sb, ( o, ia, ib ) => r[o] = Cmp( a.L[ia].CompareTo( b.L[ib] ), op ) );
		else if ( a.IsBool && b.IsBool )
			ForEachBroadcast( shape, sa, sb, ( o, ia, ib ) => r[o] = op == CmpOp.Eq && a.B[ia] == b.B[ib] );
		else
		{
			var fa = a.AsFloats(); var fb = b.AsFloats();
			ForEachBroadcast( shape, sa, sb, ( o, ia, ib ) => r[o] = Cmp( fa[ia].CompareTo( fb[ib] ), op ) && !(op == CmpOp.Eq && float.IsNaN( fa[ia] )) );
		}
		return Tensor.Bool( shape, r );
	}

	static bool Cmp( int c, CmpOp op ) => op switch
	{
		CmpOp.Eq => c == 0,
		CmpOp.Lt => c < 0,
		CmpOp.Le => c <= 0,
		CmpOp.Gt => c > 0,
		_ => c >= 0,
	};

	static Tensor Logical( Tensor a, Tensor b, Func<bool, bool, bool> f )
	{
		var shape = BroadcastShape( a.Shape, b.Shape );
		var r = new bool[Tensor.SizeOf( shape )];
		ForEachBroadcast( shape, BroadcastStrides( a.Shape, shape ), BroadcastStrides( b.Shape, shape ), ( o, ia, ib ) => r[o] = f( a.B[ia], b.B[ib] ) );
		return Tensor.Bool( shape, r );
	}

	static Tensor Where( Tensor cond, Tensor x, Tensor y )
	{
		var shape = BroadcastShape( BroadcastShape( cond.Shape, x.Shape ), y.Shape );
		var n = Tensor.SizeOf( shape );
		var sc = BroadcastStrides( cond.Shape, shape );
		var sx = BroadcastStrides( x.Shape, shape );
		var sy = BroadcastStrides( y.Shape, shape );
		var rank = shape.Length;
		var idx = new int[rank];
		int ic = 0, ix = 0, iy = 0;
		if ( x.IsInt && y.IsInt )
		{
			var r = new long[n];
			for ( var o = 0; o < n; o++ ) { r[o] = cond.B[ic] ? x.L[ix] : y.L[iy]; Step( ref ic, ref ix, ref iy ); }
			return Tensor.Int64( shape, r );
		}
		var fx = x.AsFloats(); var fy = y.AsFloats();
		var rf = new float[n];
		for ( var o = 0; o < n; o++ ) { rf[o] = cond.B[ic] ? fx[ix] : fy[iy]; Step( ref ic, ref ix, ref iy ); }
		return Tensor.Float( shape, rf );

		void Step( ref int a, ref int b, ref int c )
		{
			for ( var d = rank - 1; d >= 0; d-- )
			{
				idx[d]++; a += sc[d]; b += sx[d]; c += sy[d];
				if ( idx[d] < shape[d] ) return;
				a -= sc[d] * shape[d]; b -= sx[d] * shape[d]; c -= sy[d] * shape[d];
				idx[d] = 0;
			}
		}
	}

	static Tensor Unary( Tensor t, Func<float, float> f )
	{
		var src = t.AsFloats();
		var r = ExecContext.Alloc( src.Length );
		if ( r.Length > 65536 ) Parallel.For( 0, (r.Length + 8191) / 8192, chunk =>
		{
			var end = Math.Min( r.Length, (chunk + 1) * 8192 );
			for ( var i = chunk * 8192; i < end; i++ ) r[i] = f( src[i] );
		} );
		else for ( var i = 0; i < r.Length; i++ ) r[i] = f( src[i] );
		return Tensor.Float( t.Shape, r );
	}

	/// <summary>Error function, |error| &lt; 1.2e-7 (Numerical Recipes erfc).</summary>
	public static float Erf( float xf )
	{
		double x = xf;
		var z = Math.Abs( x );
		var t = 1.0 / (1.0 + 0.5 * z);
		var r = t * Math.Exp( -z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
			t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))) );
		return (float)(x >= 0 ? 1.0 - r : r - 1.0);
	}

	static Tensor Clip( OnnxNode n, Tensor[] i )
	{
		var lo = i.Length > 1 && i[1] is not null ? i[1].AsFloats()[0] : n.GetFloat( "min", float.NegativeInfinity );
		var hi = i.Length > 2 && i[2] is not null ? i[2].AsFloats()[0] : n.GetFloat( "max", float.PositiveInfinity );
		return Unary( i[0], v => Math.Clamp( v, lo, hi ) );
	}

	static Tensor Cast( Tensor t, OnnxType to )
	{
		switch ( to )
		{
			case OnnxType.Float: case OnnxType.Double: case OnnxType.Float16: case OnnxType.BFloat16:
				if ( to == OnnxType.Float16 && t.IsFloat ) return Tensor.Float( t.Shape, t.F.Select( v => (float)(Half)v ).ToArray() );
				return t.IsFloat ? t : Tensor.Float( t.Shape, t.AsFloats() );
			case OnnxType.Bool:
				return t.IsBool ? t : Tensor.Bool( t.Shape, t.IsInt ? t.L.Select( v => v != 0 ).ToArray() : t.F.Select( v => v != 0 ).ToArray() );
			default:
				return t.IsInt ? t : Tensor.Int64( t.Shape, t.IsFloat ? t.F.Select( v => (long)v ).ToArray() : t.B.Select( v => v ? 1L : 0L ).ToArray() );
		}
	}

	// ======================================================================== shape ops

	static Tensor ShapeOf( OnnxNode n, Tensor t )
	{
		var rank = t.Rank;
		var start = (int)n.GetInt( "start", 0 );
		var end = (int)n.GetInt( "end", rank );
		if ( start < 0 ) start += rank;
		if ( end < 0 ) end += rank;
		start = Math.Clamp( start, 0, rank ); end = Math.Clamp( end, 0, rank );
		return Tensor.Vector( t.Shape.Skip( start ).Take( Math.Max( 0, end - start ) ).Select( d => (long)d ).ToArray() );
	}

	public static Tensor Reshape( Tensor t, long[] target, bool allowZero )
	{
		var shape = new int[target.Length];
		var infer = -1;
		long known = 1;
		for ( var i = 0; i < target.Length; i++ )
		{
			var d = target[i];
			if ( d == 0 && !allowZero ) d = t.Shape[i];
			if ( d == -1 ) { infer = i; shape[i] = 1; continue; }
			shape[i] = (int)d;
			known *= d;
		}
		if ( infer >= 0 ) shape[infer] = known == 0 ? 0 : (int)(t.Length / known);
		return t.WithShape( shape );
	}

	static Tensor Flatten( Tensor t, int axis )
	{
		if ( axis < 0 ) axis += t.Rank;
		var a = t.Shape.Take( axis ).Aggregate( 1, ( x, y ) => x * y );
		return t.WithShape( new[] { a, t.Length / Math.Max( 1, a ) } );
	}

	static Tensor Unsqueeze( Tensor t, long[] axes )
	{
		var rank = t.Rank + axes.Length;
		var norm = axes.Select( a => (int)(a < 0 ? a + rank : a) ).OrderBy( a => a ).ToArray();
		var shape = new List<int>( t.Shape );
		foreach ( var a in norm ) shape.Insert( a, 1 );
		return t.WithShape( shape.ToArray() );
	}

	static Tensor Squeeze( Tensor t, long[] axes )
	{
		if ( axes is null || axes.Length == 0 ) return t.WithShape( t.Shape.Where( d => d != 1 ).ToArray() );
		var set = axes.Select( a => (int)(a < 0 ? a + t.Rank : a) ).ToHashSet();
		return t.WithShape( t.Shape.Where( ( d, i ) => !set.Contains( i ) ).ToArray() );
	}

	public static Tensor Transpose( Tensor t, long[] perm )
	{
		var rank = t.Rank;
		var p = perm is { Length: > 0 } ? perm.Select( x => (int)x ).ToArray() : Enumerable.Range( 0, rank ).Reverse().ToArray();
		var outShape = p.Select( i => t.Shape[i] ).ToArray();
		var inStrides = Tensor.Strides( t.Shape );
		var src = p.Select( i => inStrides[i] ).ToArray();
		var n = t.Length;
		int[] map = new int[n];
		var idx = new int[rank];
		var off = 0;
		for ( var o = 0; o < n; o++ )
		{
			map[o] = off;
			for ( var d = rank - 1; d >= 0; d-- )
			{
				idx[d]++; off += src[d];
				if ( idx[d] < outShape[d] ) break;
				off -= src[d] * outShape[d]; idx[d] = 0;
			}
		}
		return Gathered( t, outShape, map );
	}

	/// <summary>New tensor whose element o is the source element map[o].</summary>
	static Tensor Gathered( Tensor t, int[] shape, int[] map )
	{
		if ( t.IsFloat ) { var r = new float[map.Length]; for ( var i = 0; i < r.Length; i++ ) r[i] = t.F[map[i]]; return Tensor.Float( shape, r ); }
		if ( t.IsInt ) { var r = new long[map.Length]; for ( var i = 0; i < r.Length; i++ ) r[i] = t.L[map[i]]; return Tensor.Int64( shape, r ); }
		var b = new bool[map.Length]; for ( var i = 0; i < b.Length; i++ ) b[i] = t.B[map[i]]; return Tensor.Bool( shape, b );
	}

	public static Tensor Concat( Tensor[] parts, int axis )
	{
		var rank = parts[0].Rank;
		if ( axis < 0 ) axis += rank;
		var shape = (int[])parts[0].Shape.Clone();
		shape[axis] = parts.Sum( p => p.Shape[axis] );
		var outer = shape.Take( axis ).Aggregate( 1, ( a, b ) => a * b );
		var innerOut = Tensor.SizeOf( shape ) / Math.Max( 1, outer );
		var isFloat = parts.Any( p => p.IsFloat );
		var isInt = !isFloat && parts.Any( p => p.IsInt );
		var f = isFloat ? ExecContext.AllocZeroed( Tensor.SizeOf( shape ) ) : null;
		var l = isInt ? new long[Tensor.SizeOf( shape )] : null;
		var bo = !isFloat && !isInt ? new bool[Tensor.SizeOf( shape )] : null;
		var offset = 0;
		foreach ( var p in parts )
		{
			var inner = p.Length / Math.Max( 1, outer );
			for ( var o = 0; o < outer; o++ )
			{
				if ( f is not null ) Array.Copy( p.AsFloats(), o * inner, f, o * innerOut + offset, inner );
				else if ( l is not null ) Array.Copy( p.AsLongs(), o * inner, l, o * innerOut + offset, inner );
				else Array.Copy( p.B, o * inner, bo, o * innerOut + offset, inner );
			}
			offset += inner;
		}
		return f is not null ? Tensor.Float( shape, f ) : l is not null ? Tensor.Int64( shape, l ) : Tensor.Bool( shape, bo );
	}

	static Tensor[] Split( OnnxNode n, Tensor[] i )
	{
		var t = i[0];
		var axis = (int)n.GetInt( "axis", 0 );
		if ( axis < 0 ) axis += t.Rank;
		long[] sizes = i.Length > 1 && i[1] is not null ? i[1].AsLongs() : n.GetInts( "split" );
		var count = sizes?.Length ?? (int)n.GetInt( "num_outputs", n.Outputs.Length );
		if ( sizes is null || sizes.Length == 0 )
		{
			var dim = t.Shape[axis];
			var each = (dim + count - 1) / count;
			sizes = Enumerable.Range( 0, count ).Select( k => (long)Math.Min( each, dim - k * each ) ).ToArray();
		}
		var results = new Tensor[sizes.Length];
		long start = 0;
		for ( var k = 0; k < sizes.Length; k++ )
		{
			results[k] = FastKernels.SliceAxis( t, axis, (int)start, (int)(start + sizes[k]) );
			start += sizes[k];
		}
		return results;
	}

	static Tensor SliceAxis( Tensor t, int axis, int start, int end )
	{
		var starts = new long[t.Rank]; var ends = t.Shape.Select( d => (long)d ).ToArray();
		starts[axis] = start; ends[axis] = end;
		return SliceCore( t, starts, ends, Enumerable.Repeat( 1L, t.Rank ).ToArray() );
	}

	static Tensor Slice( Tensor[] i )
	{
		var t = i[0];
		var startsIn = i[1].AsLongs(); var endsIn = i[2].AsLongs();
		var axes = i.Length > 3 && i[3] is not null ? i[3].AsLongs() : Enumerable.Range( 0, startsIn.Length ).Select( x => (long)x ).ToArray();
		var stepsIn = i.Length > 4 && i[4] is not null ? i[4].AsLongs() : Enumerable.Repeat( 1L, startsIn.Length ).ToArray();
		var starts = new long[t.Rank]; var ends = t.Shape.Select( d => (long)d ).ToArray(); var steps = Enumerable.Repeat( 1L, t.Rank ).ToArray();
		for ( var k = 0; k < axes.Length; k++ )
		{
			var a = (int)(axes[k] < 0 ? axes[k] + t.Rank : axes[k]);
			var dim = t.Shape[a];
			var step = stepsIn[k];
			long s = startsIn[k], e = endsIn[k];
			if ( s < 0 ) s += dim;
			if ( e < 0 ) e += dim;
			if ( step > 0 ) { s = Math.Clamp( s, 0, dim ); e = Math.Clamp( e, 0, dim ); }
			else { s = Math.Clamp( s, 0, dim - 1 ); e = Math.Clamp( e, -1, dim - 1 ); }
			starts[a] = s; ends[a] = e; steps[a] = step;
		}
		return SliceCore( t, starts, ends, steps );
	}

	static Tensor SliceCore( Tensor t, long[] starts, long[] ends, long[] steps )
	{
		var rank = t.Rank;
		var shape = new int[rank];
		for ( var d = 0; d < rank; d++ )
		{
			var len = steps[d] > 0 ? (ends[d] - starts[d] + steps[d] - 1) / steps[d] : (starts[d] - ends[d] - steps[d] - 1) / -steps[d];
			shape[d] = (int)Math.Max( 0, len );
		}
		var n = Tensor.SizeOf( shape );
		var strides = Tensor.Strides( t.Shape );
		var map = new int[n];
		var idx = new int[rank];
		for ( var o = 0; o < n; o++ )
		{
			var off = 0;
			for ( var d = 0; d < rank; d++ ) off += (int)(starts[d] + idx[d] * steps[d]) * strides[d];
			map[o] = off;
			for ( var d = rank - 1; d >= 0; d-- ) { if ( ++idx[d] < shape[d] ) break; idx[d] = 0; }
		}
		return Gathered( t, shape, map );
	}

	static Tensor Gather( Tensor data, Tensor indices, int axis )
	{
		if ( axis < 0 ) axis += data.Rank;
		var idx = indices.AsLongs();
		var dim = data.Shape[axis];
		var outer = data.Shape.Take( axis ).Aggregate( 1, ( a, b ) => a * b );
		var inner = data.Shape.Skip( axis + 1 ).Aggregate( 1, ( a, b ) => a * b );
		var shape = data.Shape.Take( axis ).Concat( indices.Shape ).Concat( data.Shape.Skip( axis + 1 ) ).ToArray();
		var map = new int[Tensor.SizeOf( shape )];
		var o = 0;
		for ( var a = 0; a < outer; a++ )
			foreach ( var raw in idx )
			{
				var k = raw < 0 ? raw + dim : raw;
				if ( k < 0 || k >= dim ) throw new IndexOutOfRangeException( $"Gather index {raw} out of range {dim}." );
				var src = (a * dim + (int)k) * inner;
				for ( var i = 0; i < inner; i++ ) map[o++] = src + i;
			}
		return Gathered( data, shape, map );
	}

	static Tensor GatherElements( Tensor data, Tensor indices, int axis )
	{
		if ( axis < 0 ) axis += data.Rank;
		var idx = indices.AsLongs();
		var ds = Tensor.Strides( data.Shape );
		var map = new int[idx.Length];
		var pos = new int[indices.Rank];
		for ( var o = 0; o < idx.Length; o++ )
		{
			var off = 0;
			for ( var d = 0; d < indices.Rank; d++ )
			{
				var v = d == axis ? (int)(idx[o] < 0 ? idx[o] + data.Shape[d] : idx[o]) : pos[d];
				off += v * ds[d];
			}
			map[o] = off;
			for ( var d = indices.Rank - 1; d >= 0; d-- ) { if ( ++pos[d] < indices.Shape[d] ) break; pos[d] = 0; }
		}
		return Gathered( data, indices.Shape, map );
	}

	static Tensor Expand( Tensor t, long[] target )
	{
		var shape = BroadcastShape( t.Shape, target.Select( d => (int)d ).ToArray() );
		var strides = BroadcastStrides( t.Shape, shape );
		var map = new int[Tensor.SizeOf( shape )];
		ForEachBroadcast( shape, strides, strides, ( o, ia, _ ) => map[o] = ia );
		return Gathered( t, shape, map );
	}

	static Tensor Tile( Tensor t, long[] repeats )
	{
		var shape = t.Shape.Select( ( d, i ) => d * (int)repeats[i] ).ToArray();
		var strides = Tensor.Strides( t.Shape );
		var map = new int[Tensor.SizeOf( shape )];
		var idx = new int[shape.Length];
		for ( var o = 0; o < map.Length; o++ )
		{
			var off = 0;
			for ( var d = 0; d < shape.Length; d++ ) off += (idx[d] % t.Shape[d]) * strides[d];
			map[o] = off;
			for ( var d = shape.Length - 1; d >= 0; d-- ) { if ( ++idx[d] < shape[d] ) break; idx[d] = 0; }
		}
		return Gathered( t, shape, map );
	}

	static Tensor Range( Tensor start, Tensor limit, Tensor delta )
	{
		if ( start.IsInt && limit.IsInt && delta.IsInt )
		{
			long s = start.L[0], e = limit.L[0], d = delta.L[0];
			var n = (int)Math.Max( 0, (long)Math.Ceiling( (e - s) / (double)d ) );
			return Tensor.Int64( new[] { n }, Enumerable.Range( 0, n ).Select( i => s + i * d ).ToArray() );
		}
		float fs = start.AsFloats()[0], fe = limit.AsFloats()[0], fd = delta.AsFloats()[0];
		var count = (int)Math.Max( 0, Math.Ceiling( (fe - fs) / fd ) );
		return Tensor.Float( new[] { count }, Enumerable.Range( 0, count ).Select( i => fs + i * fd ).ToArray() );
	}

	static Tensor ConstantOf( OnnxNode n )
	{
		if ( n.Attributes.TryGetValue( "value", out var v ) && v.Tensor is { } init ) return Tensor.FromInitializer( init, "" );
		if ( n.Attributes.TryGetValue( "value_float", out var vf ) ) return Tensor.Scalar( vf.Float ?? 0 );
		if ( n.Attributes.TryGetValue( "value_int", out var vi ) ) return Tensor.ScalarInt( vi.Int ?? 0 );
		if ( n.Attributes.TryGetValue( "value_ints", out var vis ) ) return Tensor.Vector( vis.Ints );
		if ( n.Attributes.TryGetValue( "value_floats", out var vfs ) ) return Tensor.Float( new[] { vfs.Floats.Length }, vfs.Floats );
		throw new NotSupportedException( "Constant node without a supported value." );
	}

	static Tensor ConstantOfShape( OnnxNode n, long[] dims )
	{
		var shape = dims.Select( d => (int)d ).ToArray();
		if ( n.Attributes.TryGetValue( "value", out var v ) && v.Tensor is { } init )
		{
			var value = Tensor.FromInitializer( init, "" );
			if ( value.IsInt ) return Tensor.Int64( shape, Enumerable.Repeat( value.L[0], Tensor.SizeOf( shape ) ).ToArray() );
			if ( value.IsBool ) return Tensor.Bool( shape, Enumerable.Repeat( value.B[0], Tensor.SizeOf( shape ) ).ToArray() );
			return Tensor.Float( shape, Enumerable.Repeat( value.F[0], Tensor.SizeOf( shape ) ).ToArray() );
		}
		return Tensor.Float( shape );
	}

	static Tensor Trilu( Tensor t, long k, bool upper )
	{
		var rows = t.Shape[^2]; var cols = t.Shape[^1];
		var keep = new bool[t.Length];
		for ( var o = 0; o < t.Length; o++ )
		{
			var r = (o / cols) % rows; var c = o % cols;
			keep[o] = upper ? c - r >= k : c - r <= k;
		}
		if ( t.IsFloat ) return Tensor.Float( t.Shape, t.F.Select( ( v, o ) => keep[o] ? v : 0f ).ToArray() );
		if ( t.IsInt ) return Tensor.Int64( t.Shape, t.L.Select( ( v, o ) => keep[o] ? v : 0L ).ToArray() );
		return Tensor.Bool( t.Shape, t.B.Select( ( v, o ) => keep[o] && v ).ToArray() );
	}

	static Tensor CumSum( Tensor t, int axis, bool exclusive, bool reverse )
	{
		if ( axis < 0 ) axis += t.Rank;
		var dim = t.Shape[axis];
		var inner = t.Shape.Skip( axis + 1 ).Aggregate( 1, ( a, b ) => a * b );
		var outer = t.Length / Math.Max( 1, dim * inner );
		var src = t.AsFloats();
		var r = ExecContext.AllocZeroed( t.Length );
		for ( var o = 0; o < outer; o++ )
			for ( var i = 0; i < inner; i++ )
			{
				var acc = 0f;
				for ( var s = 0; s < dim; s++ )
				{
					var k = reverse ? dim - 1 - s : s;
					var at = (o * dim + k) * inner + i;
					if ( exclusive ) { r[at] = acc; acc += src[at]; }
					else { acc += src[at]; r[at] = acc; }
				}
			}
		return t.IsInt ? Tensor.Int64( t.Shape, r.Select( v => (long)v ).ToArray() ) : Tensor.Float( t.Shape, r );
	}

	// ======================================================================== reductions

	enum ReduceOp { Mean, Sum, Max, Min, Prod, L2, SumSquare }

	static Tensor Reduce( OnnxNode n, Tensor[] i, ReduceOp op )
	{
		var t = i[0];
		long[] axes = i.Length > 1 && i[1] is not null ? i[1].AsLongs() : n.GetInts( "axes" );
		var keep = n.GetInt( "keepdims", 1 ) == 1;
		var noopEmpty = n.GetInt( "noop_with_empty_axes", 0 ) == 1;
		if ( (axes is null || axes.Length == 0) && noopEmpty ) return t;
		var set = axes is null || axes.Length == 0
			? Enumerable.Range( 0, t.Rank ).ToHashSet()
			: axes.Select( a => (int)(a < 0 ? a + t.Rank : a) ).ToHashSet();
		var outShapeKeep = t.Shape.Select( ( d, k ) => set.Contains( k ) ? 1 : d ).ToArray();
		var outN = Tensor.SizeOf( outShapeKeep );
		var src = t.AsFloats();
		var acc = new double[outN];
		var init = op switch { ReduceOp.Max => double.NegativeInfinity, ReduceOp.Min => double.PositiveInfinity, ReduceOp.Prod => 1.0, _ => 0.0 };
		Array.Fill( acc, init );
		// fast path: reduce over the trailing axes only
		var trailing = set.All( a => a >= t.Rank - set.Count );
		if ( trailing )
		{
			var inner = t.Length / Math.Max( 1, outN );
			for ( var o = 0; o < outN; o++ )
			{
				double a = init;
				var baseIdx = o * inner;
				for ( var k = 0; k < inner; k++ ) a = Accumulate( a, src[baseIdx + k], op );
				acc[o] = a;
			}
		}
		else
		{
			var outStrides = BroadcastStrides( outShapeKeep, t.Shape );
			ForEachBroadcast( t.Shape, outStrides, outStrides, ( o, io, _ ) => acc[io] = Accumulate( acc[io], src[o], op ) );
		}
		var count = t.Length / Math.Max( 1, outN );
		var r = new float[outN];
		for ( var o = 0; o < outN; o++ )
			r[o] = (float)(op switch { ReduceOp.Mean => acc[o] / count, ReduceOp.L2 => Math.Sqrt( acc[o] ), _ => acc[o] });
		var shape = keep ? outShapeKeep : t.Shape.Where( ( d, k ) => !set.Contains( k ) ).ToArray();
		return Tensor.Float( shape, r );
	}

	static double Accumulate( double a, float v, ReduceOp op ) => op switch
	{
		ReduceOp.Max => Math.Max( a, v ),
		ReduceOp.Min => Math.Min( a, v ),
		ReduceOp.Prod => a * v,
		ReduceOp.L2 or ReduceOp.SumSquare => a + (double)v * v,
		_ => a + v,
	};

	static Tensor ArgMax( Tensor t, int axis, bool keep )
	{
		if ( axis < 0 ) axis += t.Rank;
		var dim = t.Shape[axis];
		var inner = t.Shape.Skip( axis + 1 ).Aggregate( 1, ( a, b ) => a * b );
		var outer = t.Length / Math.Max( 1, dim * inner );
		var src = t.AsFloats();
		var r = new long[outer * inner];
		for ( var o = 0; o < outer; o++ )
			for ( var i = 0; i < inner; i++ )
			{
				var best = 0; var bv = float.NegativeInfinity;
				for ( var k = 0; k < dim; k++ ) { var v = src[(o * dim + k) * inner + i]; if ( v > bv ) { bv = v; best = k; } }
				r[o * inner + i] = best;
			}
		var shape = keep ? t.Shape.Select( ( d, k ) => k == axis ? 1 : d ).ToArray() : t.Shape.Where( ( d, k ) => k != axis ).ToArray();
		return Tensor.Int64( shape, r );
	}

	// ======================================================================== normalization / softmax

	static Tensor Softmax( Tensor t, int axis, ExecContext ctx, bool log )
	{
		if ( axis < 0 ) axis += t.Rank;
		var dim = t.Shape[axis];
		var inner = t.Shape.Skip( axis + 1 ).Aggregate( 1, ( a, b ) => a * b );
		var outer = t.Length / Math.Max( 1, dim * inner );
		var src = t.F;
		var r = ExecContext.AllocZeroed( t.Length );
		void Row( int o, int i )
		{
			var max = float.NegativeInfinity;
			for ( var k = 0; k < dim; k++ ) max = MathF.Max( max, src[(o * dim + k) * inner + i] );
			if ( float.IsNegativeInfinity( max ) ) max = 0;
			var sum = 0f;
			for ( var k = 0; k < dim; k++ ) { var at = (o * dim + k) * inner + i; var e = MathF.Exp( src[at] - max ); r[at] = e; sum += e; }
			if ( log ) { var ls = MathF.Log( sum ); for ( var k = 0; k < dim; k++ ) { var at = (o * dim + k) * inner + i; r[at] = src[at] - max - ls; } }
			else { var inv = sum > 0 ? 1f / sum : 0f; for ( var k = 0; k < dim; k++ ) r[(o * dim + k) * inner + i] *= inv; }
		}
		var rows = outer * inner;
		if ( (long)rows * dim > 32768 ) Parallel.For( 0, rows, ctx.Parallel, x => Row( x / inner, x % inner ) );
		else for ( var x = 0; x < rows; x++ ) Row( x / inner, x % inner );
		return Tensor.Float( t.Shape, r );
	}

	static Tensor LayerNorm( Tensor x, Tensor scale, Tensor bias, int axis, float eps, ExecContext ctx )
	{
		if ( axis < 0 ) axis += x.Rank;
		var norm = x.Shape.Skip( axis ).Aggregate( 1, ( a, b ) => a * b );
		var rows = x.Length / Math.Max( 1, norm );
		var src = x.F; var r = ExecContext.Alloc( x.Length );
		var g = scale.F; var b = bias?.F;
		void Row( int row )
		{
			var o = row * norm;
			double mean = 0; for ( var k = 0; k < norm; k++ ) mean += src[o + k]; mean /= norm;
			double var = 0; for ( var k = 0; k < norm; k++ ) { var d = src[o + k] - mean; var += d * d; } var /= norm;
			var inv = (float)(1.0 / Math.Sqrt( var + eps ));
			var m = (float)mean;
			for ( var k = 0; k < norm; k++ )
				r[o + k] = (src[o + k] - m) * inv * g[g.Length == 1 ? 0 : k] + (b is null ? 0f : b[b.Length == 1 ? 0 : k]);
		}
		if ( rows > 64 ) Parallel.For( 0, rows, ctx.Parallel, Row ); else for ( var i = 0; i < rows; i++ ) Row( i );
		return Tensor.Float( x.Shape, r );
	}

	static Tensor RmsNorm( Tensor x, Tensor scale, int axis, float eps, ExecContext ctx )
	{
		if ( axis < 0 ) axis += x.Rank;
		var norm = x.Shape.Skip( axis ).Aggregate( 1, ( a, b ) => a * b );
		var rows = x.Length / Math.Max( 1, norm );
		var src = x.F; var r = ExecContext.Alloc( x.Length ); var g = scale.F;
		Parallel.For( 0, rows, ctx.Parallel, row =>
		{
			var o = row * norm;
			double ss = 0; for ( var k = 0; k < norm; k++ ) ss += (double)src[o + k] * src[o + k];
			var inv = (float)(1.0 / Math.Sqrt( ss / norm + eps ));
			for ( var k = 0; k < norm; k++ ) r[o + k] = src[o + k] * inv * g[k];
		} );
		return Tensor.Float( x.Shape, r );
	}

	// ======================================================================== matrix multiply


	static Tensor MatMulBias( Tensor a, Tensor b, Tensor bias, ExecContext ctx )
	{
		if ( a.Rank < 2 || (long)b.Shape[0] * b.Shape[1] <= 64 * 64 )
			return FastBinary( MatMul( a, b, ctx ), bias, BinOp.Add, FastKernels.Op.Add, ctx );
		return MatMulPacked( a, b, ctx, bias.F );
	}

	/// <summary>A[..., K] x B[K, N] with B packed for the register-blocked GEMM (cached when B is a constant).</summary>
	static Tensor MatMulPacked( Tensor a, Tensor b, ExecContext ctx, float[] bias = null )
	{
		int K = b.Shape[0], N = b.Shape[1];
		var M = a.Length / Math.Max( 1, K );
		FastKernels.PackedMatrix packed;
		if ( ctx.Constants.Contains( b ) )
		{
			lock ( ctx.Packed )
				if ( !ctx.Packed.TryGetValue( b, out packed ) ) ctx.Packed[b] = packed = FastKernels.Pack( b.F, K, N );
		}
		else packed = FastKernels.Pack( b.F, K, N );
		var c = ExecContext.Alloc( M * N );
		FastKernels.Gemm( a.F, 0, M, packed, c, 0, ctx, bias );
		return Tensor.Float( a.Shape.Take( a.Rank - 1 ).Append( N ).ToArray(), c );
	}

	/// <summary>NumPy matmul with batch broadcasting.</summary>
	public static Tensor MatMul( Tensor a, Tensor b, ExecContext ctx )
	{
		if ( b.Rank == 2 && a.Rank >= 2 && a.Shape[^1] == b.Shape[0] && a.IsFloat && b.IsFloat && (long)b.Shape[0] * b.Shape[1] > 64 * 64 ) return MatMulPacked( a, b, ctx );
		var aShape = a.Rank == 1 ? new[] { 1, a.Shape[0] } : a.Shape;
		var bShape = b.Rank == 1 ? new[] { b.Shape[0], 1 } : b.Shape;
		int M = aShape[^2], K = aShape[^1], N = bShape[^1];
		if ( bShape[^2] != K ) throw new ArgumentException( $"MatMul shapes [{string.Join( ",", a.Shape )}] x [{string.Join( ",", b.Shape )}] don't match." );
		var batchA = aShape.Take( aShape.Length - 2 ).ToArray();
		var batchB = bShape.Take( bShape.Length - 2 ).ToArray();
		var batch = BroadcastShape( batchA, batchB );
		var batches = Tensor.SizeOf( batch );
		var outShape = batch.Concat( new[] { M, N } ).ToArray();
		var c = ExecContext.AllocZeroed( batches * M * N );
		var sa = BroadcastStrides( batchA, batch );
		var sb = BroadcastStrides( batchB, batch );
		var aOff = new int[batches]; var bOff = new int[batches];
		{
			var idx = new int[batch.Length];
			for ( var p = 0; p < batches; p++ )
			{
				int oa = 0, ob = 0;
				for ( var d = 0; d < batch.Length; d++ ) { oa += idx[d] * sa[d]; ob += idx[d] * sb[d]; }
				aOff[p] = oa * M * K; bOff[p] = ob * K * N;
				for ( var d = batch.Length - 1; d >= 0; d-- ) { if ( ++idx[d] < batch[d] ) break; idx[d] = 0; }
			}
		}
		var af = a.AsFloats(); var bf = b.AsFloats();
		var rows = batches * M;
		var work = (long)rows * K * N;
		// When B is shared by all batches, treat the batch as extra rows of A (one big GEMM).
		void Row( int r )
		{
			var p = r / M; var i = r % M;
			RowAxpy( af, aOff[p] + i * K, bf, bOff[p], c, r * N, K, N );
		}
		if ( work > 1 << 16 ) Parallel.For( 0, rows, ctx.Parallel, Row );
		else for ( var r = 0; r < rows; r++ ) Row( r );
		var shape = outShape;
		if ( a.Rank == 1 ) shape = shape.Take( shape.Length - 2 ).Concat( new[] { N } ).ToArray();
		if ( b.Rank == 1 ) shape = shape.Take( shape.Length - 1 ).ToArray();
		return Tensor.Float( shape, c );
	}

	/// <summary>c[co..co+N] = sum_k a[ao+k] * b[bo + k*N .. +N].</summary>
	static void RowAxpy( float[] a, int ao, float[] b, int bo, float[] c, int co, int K, int N )
	{
		var cs = c.AsSpan( co, N );
		var cv = MemoryMarshal.Cast<float, Vector<float>>( cs );
		var tail = cv.Length * Vector<float>.Count;
		for ( var k = 0; k < K; k++ )
		{
			var s = a[ao + k];
			if ( s == 0f ) continue;
			var bs = b.AsSpan( bo + k * N, N );
			var bv = MemoryMarshal.Cast<float, Vector<float>>( bs );
			var sv = new Vector<float>( s );
			for ( var j = 0; j < cv.Length; j++ ) cv[j] += bv[j] * sv;
			for ( var j = tail; j < N; j++ ) cs[j] += s * bs[j];
		}
	}

	static Tensor Gemm( OnnxNode n, Tensor[] i, ExecContext ctx )
	{
		var a = i[0]; var b = i[1];
		var alpha = n.GetFloat( "alpha", 1f ); var beta = n.GetFloat( "beta", 1f );
		if ( n.GetInt( "transA", 0 ) == 1 ) a = Transpose( a, new long[] { 1, 0 } );
		if ( n.GetInt( "transB", 0 ) == 1 )
		{
			if ( !ctx.TransposedCache.TryGetValue( b, out var bt ) )
			{
				{
					ctx.TransposedCache[b] = bt = Transpose( b, new long[] { 1, 0 } );
					ctx.Pool.Release( bt.F ); // cached across runs: never recycled
				}
				if ( ctx.Constants.Contains( b ) ) ctx.Constants.Add( bt );
			}
			b = bt;
		}
		var y = MatMul( a, b, ctx );
		if ( alpha != 1f ) y = Unary( y, v => v * alpha );
		if ( i.Length > 2 && i[2] is not null )
		{
			var cterm = beta == 1f ? i[2] : Unary( i[2], v => v * beta );
			y = Binary( y, cterm, BinOp.Add );
		}
		return y;
	}
}
