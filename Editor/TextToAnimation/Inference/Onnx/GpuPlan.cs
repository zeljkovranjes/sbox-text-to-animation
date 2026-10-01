using System;
using System.Collections.Generic;
using System.Linq;

namespace TextToAnimation.Editor.Inference.Onnx;

/// <summary>
/// A graph run turned into a flat list of GPU kernel launches. Built from one traced CPU run (shapes are fixed for
/// a graph, so the trace fixes every launch); executed by a backend (the editor's compute shaders, or the test
/// emulator) on float buffers that stay on the device between launches. Only six kernels:
/// <list type="bullet">
/// <item>Copy - strided copy (Transpose, Slice, Concat, Split, Expand, Identity); Reshape is a view</item>
/// <item>Elementwise - broadcast Add / Mul / MulAdd / Sub / Div and SiLU / Sin / Cos</item>
/// <item>Gemm - A[M,K] x B[K,N] (+ bias[N])</item>
/// <item>Attention - softmax(Q K^T scale + mask) V per head (online softmax, any key count)</item>
/// <item>RmsNorm - over the last axis, times a scale vector</item>
/// <item>Rope - x cos + (x R) sin with R a signed permutation</item>
/// </list>
/// A graph using anything else (or shapes a kernel doesn't take) has no plan; it runs on the CPU.
/// </summary>
public sealed class GpuPlan
{
	public enum Kernel { Copy, Elementwise, Gemm, Attention, RmsNorm, Rope }

	public enum EltMode { Add = 0, Mul = 1, MulAdd = 2, SiLU = 3, Sin = 4, Cos = 5, Sub = 6, Div = 7 }

	/// <summary>A device buffer: a weight (uploaded once), a graph input (uploaded per run) or a working buffer.</summary>
	public sealed record Buffer( int Id, int Length, string Constant, string Input );

	/// <summary>
	/// One launch: kernel, integer parameters, bound buffers (kernel-specific order; the last is written) and thread
	/// counts. <see cref="Value"/> names the graph value the launch writes (diagnostics compare it with the CPU).
	/// </summary>
	public sealed record Launch( Kernel Kernel, int[] Params, int[] Buffers, int ThreadsX, int ThreadsY, string Value );

	public const int Rank = 6;              // copy / elementwise index space (left-padded)
	public const int FlatThreads = 256 * 1024; // flat kernels: index = x + y * FlatThreads (256-thread groups)
	public const int AttentionThreads = 64 * 1024;
	public const int RowsPerDispatchRow = 1024; // RmsNorm: one 64-thread group per row
	public const int MaxHeadDim = 128;      // the attention kernel keeps a head in registers

	public List<Buffer> Buffers { get; } = new();
	public List<Launch> Launches { get; } = new();
	public Dictionary<string, (int Buffer, int[] Shape)> Outputs { get; } = new( StringComparer.Ordinal );
	public Dictionary<string, (int Buffer, int[] Shape)> Inputs { get; } = new( StringComparer.Ordinal );

	/// <summary>Why the last <see cref="Build"/> returned null.</summary>
	public static string LastRefusal { get; private set; }

	/// <summary>A node as the trace saw it.</summary>
	public sealed record TracedNode( OnnxNode Node, int[][] InShapes, long[][] InInts, bool[] InFloat, int[][] OutShapes );

	/// <summary>Records the nodes of a run (attach to <see cref="OnnxSession.Trace"/>).</summary>
	public sealed class Recorder
	{
		public List<TracedNode> Nodes { get; } = new();

		/// <summary>The CPU's value of every float node output (only when <see cref="KeepValues"/>; for diagnostics).</summary>
		public Dictionary<string, float[]> Values { get; } = new( StringComparer.Ordinal );
		public bool KeepValues { get; init; }

		public void Record( OnnxNode node, Tensor[] args, Tensor[] outputs )
		{
			if ( KeepValues )
				for ( var k = 0; k < outputs.Length && k < node.Outputs.Length; k++ )
					if ( outputs[k] is { IsFloat: true } o && node.Outputs[k].Length > 0 ) Values[node.Outputs[k]] = o.F.ToArray();
			Nodes.Add( new TracedNode( node,
				args.Select( a => a?.Shape.ToArray() ).ToArray(),
				args.Select( a => a is null || a.IsFloat ? null : a.AsLongs() ).ToArray(),
				args.Select( a => a?.IsFloat ?? false ).ToArray(),
				outputs.Select( o => o?.Shape.ToArray() ).ToArray() ) );
		}
	}

	sealed class Refused : Exception { public Refused( string m ) : base( m ) { } }

	/// <summary>The plan for a traced run of <paramref name="session"/>, or null (see <see cref="LastRefusal"/>).</summary>
	public static GpuPlan Build( OnnxSession session, Recorder trace )
	{
		try
		{
			LastRefusal = null;
			return new Builder( session, trace ).Plan;
		}
		catch ( Refused r )
		{
			LastRefusal = r.Message;
			return null;
		}
	}

	sealed class Builder
	{
		public readonly GpuPlan Plan = new();
		readonly OnnxSession _session;
		readonly Dictionary<string, int> _buffer = new( StringComparer.Ordinal ); // value -> buffer
		readonly Dictionary<string, int[]> _shape = new( StringComparer.Ordinal );
		readonly Dictionary<int, int> _lastUse = new();     // working buffer -> last launch-node index reading it
		readonly HashSet<int> _pinned = new();              // buffers that are never recycled (inputs, weights, outputs)
		readonly Dictionary<int, Stack<int>> _free = new();  // length -> free working buffers

		public Builder( OnnxSession session, Recorder trace )
		{
			_session = session;
			var nodes = trace.Nodes;
			var outputs = new HashSet<string>( session.OutputNames, StringComparer.Ordinal );

			// which value each name aliases (views share the buffer of what they view)
			var root = new Dictionary<string, string>( StringComparer.Ordinal );
			string Root( string n ) => root.TryGetValue( n, out var r ) ? Root( r ) : n;
			foreach ( var t in nodes )
				if ( t.Node.OpType is "Reshape" or "Identity" or "Squeeze" or "Unsqueeze" or "Flatten" && t.InFloat[0] )
					root[t.Node.Outputs[0]] = t.Node.Inputs[0];
			var lastUseOfRoot = new Dictionary<string, int>( StringComparer.Ordinal );
			for ( var i = 0; i < nodes.Count; i++ )
				foreach ( var input in nodes[i].Node.Inputs )
					if ( input.Length > 0 ) lastUseOfRoot[Root( input )] = i;
			var keepRoots = new HashSet<string>( outputs.Select( Root ), StringComparer.Ordinal );

			for ( var i = 0; i < nodes.Count; i++ )
			{
				var t = nodes[i];
				var n = t.Node;
				for ( var k = 0; k < n.Inputs.Length; k++ )
				{
					var name = n.Inputs[k];
					if ( name.Length == 0 || !t.InFloat[k] || _buffer.ContainsKey( name ) ) continue;
					_shape[name] = t.InShapes[k];
					var size = Size( t.InShapes[k] );
					if ( _session.Constant( name ) is { IsFloat: true } )
						_buffer[name] = Pin( NewBuffer( size, constant: name ) );
					else if ( _session.InputNames.Contains( name ) )
					{
						_buffer[name] = Pin( NewBuffer( size, input: name ) );
						Plan.Inputs[name] = (_buffer[name], t.InShapes[k]);
					}
					else if ( root.ContainsKey( name ) && _buffer.TryGetValue( Root( name ), out var viewed ) ) _buffer[name] = viewed;
					else throw new Refused( $"{n.OpType} reads \"{name}\" which nothing produced on the device." );
				}
				for ( var k = 0; k < n.Outputs.Length; k++ )
					if ( n.Outputs[k].Length > 0 && t.OutShapes.Length > k && t.OutShapes[k] is { } s ) _shape[n.Outputs[k]] = s;

				Emit( t );

				// working buffers nobody reads any more go back to the pool (after the outputs took theirs)
				foreach ( var input in n.Inputs.Distinct() )
				{
					if ( input.Length == 0 ) continue;
					var r = Root( input );
					if ( lastUseOfRoot.GetValueOrDefault( r, -1 ) != i || keepRoots.Contains( r ) ) continue;
					if ( _buffer.TryGetValue( r, out var b ) && !_pinned.Contains( b ) ) Release( b );
				}
			}
			foreach ( var name in session.OutputNames )
			{
				if ( !_buffer.TryGetValue( name, out var b ) && !_buffer.TryGetValue( Root( name ), out b ) )
					throw new Refused( $"Output \"{name}\" isn't on the device." );
				Plan.Outputs[name] = (b, _shape[name]);
			}
		}

		static int Size( int[] shape ) => shape.Aggregate( 1, ( a, d ) => a * d );

		int Pin( int b ) { _pinned.Add( b ); return b; }

		int NewBuffer( int length, string constant = null, string input = null )
		{
			var id = Plan.Buffers.Count;
			Plan.Buffers.Add( new Buffer( id, Math.Max( 1, length ), constant, input ) );
			return id;
		}

		int Working( int length )
		{
			length = Math.Max( 1, length );
			if ( _free.TryGetValue( length, out var stack ) && stack.Count > 0 ) return stack.Pop();
			return NewBuffer( length );
		}

		void Release( int b )
		{
			var len = Plan.Buffers[b].Length;
			if ( !_free.TryGetValue( len, out var stack ) ) _free[len] = stack = new Stack<int>();
			if ( !stack.Contains( b ) ) stack.Push( b );
		}

		int Out( TracedNode t, int k = 0 )
		{
			var name = t.Node.Outputs[k];
			var b = Working( Size( t.OutShapes[k] ) );
			_buffer[name] = b;
			return b;
		}

		int In( TracedNode t, int k ) => _buffer.TryGetValue( t.Node.Inputs[k], out var b ) ? b
			: throw new Refused( $"{t.Node.OpType} input {k} isn't a device buffer." );

		string _value = "";
		void Add( Kernel kernel, int[] p, int[] buffers, int x, int y ) => Plan.Launches.Add( new Launch( kernel, p, buffers, x, y, _value ) );

		static (int X, int Y) Flat( int count, int perRow )
		{
			var x = Math.Min( count, perRow );
			x = (x + 255) / 256 * 256;
			return (Math.Max( 256, x ), Math.Max( 1, (count + perRow - 1) / perRow ));
		}

		static int[] Pad( int[] shape )
		{
			if ( shape.Length > Rank ) throw new Refused( $"rank {shape.Length} > {Rank}" );
			return Enumerable.Repeat( 1, Rank - shape.Length ).Concat( shape ).ToArray();
		}

		static int[] Contiguous( int[] shape ) => Tensor.Strides( shape );

		/// <summary>Strides of <paramref name="shape"/> broadcast to the (padded) output shape: 0 along broadcast axes.</summary>
		static int[] BroadcastStrides( int[] shape, int[] outPadded )
		{
			var padded = Pad( shape );
			var strides = Contiguous( padded );
			for ( var d = 0; d < Rank; d++ ) if ( padded[d] == 1 && outPadded[d] != 1 ) strides[d] = 0;
			return strides;
		}

		void Copy( int src, int dst, int[] region, int[] srcStrides, int srcBase, int[] dstStrides, int dstBase )
		{
			var count = Size( region );
			if ( count == 0 ) return;
			var p = new List<int> { count, srcBase, dstBase };
			p.AddRange( region ); p.AddRange( srcStrides ); p.AddRange( dstStrides );
			var (x, y) = Flat( count, FlatThreads );
			Add( Kernel.Copy, p.ToArray(), new[] { src, dst }, x, y );
		}

		void Elementwise( EltMode mode, int[] outShape, int outBuffer, params (int Buffer, int[] Shape)[] ins )
		{
			var op = Pad( outShape );
			var count = Size( outShape );
			var p = new List<int> { count, (int)mode };
			p.AddRange( op );
			for ( var k = 0; k < 3; k++ ) p.AddRange( k < ins.Length ? BroadcastStrides( ins[k].Shape, op ) : new int[Rank] );
			var bufs = new[] { ins[0].Buffer, ins.Length > 1 ? ins[1].Buffer : ins[0].Buffer, ins.Length > 2 ? ins[2].Buffer : ins[0].Buffer, outBuffer };
			var (x, y) = Flat( count, FlatThreads );
			Add( Kernel.Elementwise, p.ToArray(), bufs, x, y );
		}

		void Emit( TracedNode t )
		{
			var n = t.Node;
			_value = n.Outputs.Length > 0 ? n.Outputs[0] : "";
			switch ( n.OpType )
			{
				case "Reshape": case "Identity": case "Squeeze": case "Unsqueeze": case "Flatten":
					if ( !t.InFloat[0] ) throw new Refused( $"{n.OpType} on integers" );
					return; // a view: shares the input's buffer

				case "Add": case "Mul": case "Sub": case "Div":
				{
					var mode = n.OpType switch { "Add" => EltMode.Add, "Mul" => EltMode.Mul, "Sub" => EltMode.Sub, _ => EltMode.Div };
					Elementwise( mode, t.OutShapes[0], Out( t ), (In( t, 0 ), t.InShapes[0]), (In( t, 1 ), t.InShapes[1]) );
					return;
				}
				case "MulAdd":
					Elementwise( EltMode.MulAdd, t.OutShapes[0], Out( t ), (In( t, 0 ), t.InShapes[0]), (In( t, 1 ), t.InShapes[1]), (In( t, 2 ), t.InShapes[2]) );
					return;
				case "SiLU": case "Sin": case "Cos":
				{
					var mode = n.OpType switch { "SiLU" => EltMode.SiLU, "Sin" => EltMode.Sin, _ => EltMode.Cos };
					Elementwise( mode, t.OutShapes[0], Out( t ), (In( t, 0 ), t.InShapes[0]) );
					return;
				}

				case "Transpose":
				{
					var perm = n.GetInts( "perm" )?.Select( v => (int)v ).ToArray() ?? Enumerable.Range( 0, t.InShapes[0].Length ).Reverse().ToArray();
					var inStrides = Contiguous( t.InShapes[0] );
					var lead = Rank - perm.Length;
					if ( lead < 0 ) throw new Refused( "Transpose rank" );
					var region = Pad( t.OutShapes[0] );
					var src = new int[Rank];
					for ( var d = 0; d < perm.Length; d++ ) src[lead + d] = inStrides[perm[d]];
					Copy( In( t, 0 ), Out( t ), region, src, 0, Contiguous( region ), 0 );
					return;
				}
				case "Expand":
				{
					var region = Pad( t.OutShapes[0] );
					Copy( In( t, 0 ), Out( t ), region, BroadcastStrides( t.InShapes[0], region ), 0, Contiguous( region ), 0 );
					return;
				}
				case "Slice":
				{
					var shape = t.InShapes[0];
					var rank = shape.Length;
					var startsIn = t.InInts[1]; var endsIn = t.InInts[2];
					var axes = t.InInts.Length > 3 && t.InInts[3] is not null ? t.InInts[3] : Enumerable.Range( 0, startsIn.Length ).Select( x => (long)x ).ToArray();
					var stepsIn = t.InInts.Length > 4 && t.InInts[4] is not null ? t.InInts[4] : Enumerable.Repeat( 1L, startsIn.Length ).ToArray();
					var starts = new long[rank]; var steps = Enumerable.Repeat( 1L, rank ).ToArray();
					for ( var k = 0; k < axes.Length; k++ )
					{
						var a = (int)(axes[k] < 0 ? axes[k] + rank : axes[k]);
						var dim = shape[a];
						long s = startsIn[k];
						if ( s < 0 ) s += dim;
						s = stepsIn[k] > 0 ? Math.Clamp( s, 0, dim ) : Math.Clamp( s, 0, dim - 1 );
						starts[a] = s; steps[a] = stepsIn[k];
					}
					var inStrides = Contiguous( shape );
					var srcBase = 0;
					var src = new int[Rank];
					for ( var d = 0; d < rank; d++ )
					{
						srcBase += (int)starts[d] * inStrides[d];
						src[Rank - rank + d] = inStrides[d] * (int)steps[d];
					}
					var region = Pad( t.OutShapes[0] );
					Copy( In( t, 0 ), Out( t ), region, src, srcBase, Contiguous( region ), 0 );
					return;
				}
				case "Concat":
				{
					var outShape = t.OutShapes[0];
					var axis = (int)n.GetInt( "axis", 0 );
					if ( axis < 0 ) axis += outShape.Length;
					var o = Out( t );
					var dstStrides = Pad( outShape ).Length == Rank ? Contiguous( Pad( outShape ) ) : null;
					var lead = Rank - outShape.Length;
					var offset = 0;
					for ( var k = 0; k < n.Inputs.Length; k++ )
					{
						if ( n.Inputs[k].Length == 0 || t.InShapes[k] is null ) continue;
						var region = Pad( t.InShapes[k] );
						Copy( In( t, k ), o, region, Contiguous( region ), 0, dstStrides, offset * dstStrides[lead + axis] );
						offset += t.InShapes[k][axis];
					}
					return;
				}
				case "Split":
				{
					var shape = t.InShapes[0];
					var axis = (int)n.GetInt( "axis", 0 );
					if ( axis < 0 ) axis += shape.Length;
					var inStrides = Pad( shape ).Length == Rank ? Contiguous( Pad( shape ) ) : null;
					var lead = Rank - shape.Length;
					var start = 0;
					for ( var k = 0; k < n.Outputs.Length; k++ )
					{
						if ( t.OutShapes[k] is null ) continue;
						var region = Pad( t.OutShapes[k] );
						var o = n.Outputs[k].Length > 0 ? Out( t, k ) : Working( Size( t.OutShapes[k] ) );
						_value = n.Outputs[k];
						Copy( In( t, 0 ), o, region, inStrides, start * inStrides[lead + axis], Contiguous( region ), 0 );
						start += t.OutShapes[k][axis];
					}
					return;
				}

				case "MatMulBias": case "MatMul":
				{
					var b = t.InShapes[1];
					if ( b is null || b.Length != 2 || t.InShapes[0].Length < 2 ) throw new Refused( $"{n.OpType} with B of rank {b?.Length}" );
					int K = b[0], N = b[1];
					var M = Size( t.InShapes[0] ) / K;
					var hasBias = n.OpType == "MatMulBias";
					var o = Out( t );
					var a = In( t, 0 );
					Add( Kernel.Gemm, new[] { M, K, N, hasBias ? 1 : 0 }, new[] { a, In( t, 1 ), hasBias ? In( t, 2 ) : a, o },
						(N + 63) / 64 * 16, (M + 63) / 64 * 16 );
					return;
				}

				case "Attention":
				{
					var q = t.InShapes[0]; var k = t.InShapes[1]; var v = t.InShapes[2];
					if ( q.Length != 4 || q[3] != k[3] || q[3] != v[3] || q[3] > MaxHeadDim || q[3] % 4 != 0 ) throw new Refused( "Attention head size" );
					int N = q[0], Hq = q[1], Sq = q[2], Hk = k[1], Sk = k[2];
					var scale = n.Attributes.ContainsKey( "scale" ) ? n.GetFloat( "scale", 1f ) : 1f / MathF.Sqrt( q[3] );
					var hasMask = n.Inputs.Length > 3 && n.Inputs[3].Length > 0;
					int maskN = 0, maskH = 0;
					if ( hasMask )
					{
						var ms = t.InShapes[3];
						var rank = ms.Length;
						var hDim = rank >= 3 ? ms[rank - 3] : 1;
						var nDim = rank >= 4 ? ms[rank - 4] : 1;
						maskH = hDim == 1 ? 0 : Sq * Sk;
						maskN = nDim == 1 ? 0 : hDim * Sq * Sk;
					}
					var o = Out( t );
					var qb = In( t, 0 );
					var rows = N * Hq * Sq;
					var (x, y) = Flat( rows, AttentionThreads );
					Add( Kernel.Attention, new[] { rows, Hq, Hk, Sq, Sk, BitConverter.SingleToInt32Bits( scale ), hasMask ? 1 : 0, maskN, maskH, q[3] },
						new[] { qb, In( t, 1 ), In( t, 2 ), hasMask ? In( t, 3 ) : qb, o }, Math.Max( 64, (x + 63) / 64 * 64 ), y );
					return;
				}

				case "RMSNormalization":
				{
					var shape = t.InShapes[0];
					var axis = (int)n.GetInt( "axis", -1 );
					if ( axis < 0 ) axis += shape.Length;
					var norm = 1;
					for ( var d = axis; d < shape.Length; d++ ) norm *= shape[d];
					var rows = Size( shape ) / norm;
					var g = Size( t.InShapes[1] );
					if ( g != norm && g != 1 ) throw new Refused( "RMSNormalization scale shape" );
					Add( Kernel.RmsNorm, new[] { rows, norm, BitConverter.SingleToInt32Bits( n.GetFloat( "epsilon", 1e-5f ) ), g },
						new[] { In( t, 0 ), In( t, 1 ), Out( t ) }, 64 * Math.Min( rows, RowsPerDispatchRow ), (rows + RowsPerDispatchRow - 1) / RowsPerDispatchRow );
					return;
				}

				case "Rope":
				{
					var r = _session.Constant( n.Inputs[3] ) ?? throw new Refused( "Rope rotation isn't a constant" );
					var map = FastKernels.RotationMap( r ) ?? throw new Refused( "Rope rotation isn't a signed permutation" );
					var shape = t.InShapes[0];
					var d = shape[^1];
					var positions = shape[^2];
					if ( Size( t.InShapes[1] ) != positions * d || Size( t.InShapes[2] ) != positions * d ) throw new Refused( "Rope cos/sin rows" );
					var count = Size( shape );
					var p = new List<int> { count, d, positions };
					p.AddRange( map.Src );
					p.AddRange( map.Sign.Select( s => (int)MathF.Round( s ) ) );
					var (x, y) = Flat( count, FlatThreads );
					Add( Kernel.Rope, p.ToArray(), new[] { In( t, 0 ), In( t, 1 ), In( t, 2 ), Out( t ) }, x, y );
					return;
				}

				default:
					throw new Refused( $"no GPU kernel for {n.OpType}" );
			}
		}
	}
}
