using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TextToAnimation.Editor.Inference.Onnx;

/// <summary>
/// Runs an ONNX graph with the managed kernels in <see cref="OnnxOps"/>. Initializers are decoded once
/// when the session is created; intermediate values are released as soon as their last consumer ran.
/// Thread-safe for sequential use from one worker thread at a time.
/// </summary>
public sealed class OnnxSession
{
	public OnnxModel Model { get; }
	readonly Dictionary<string, Tensor> _constants = new( StringComparer.Ordinal );
	readonly List<OnnxNode> _order;
	readonly Dictionary<string, int> _lastUse = new( StringComparer.Ordinal );
	readonly ExecContext _ctx = new();

	public IReadOnlyList<string> InputNames { get; }
	public IReadOnlyList<string> OutputNames { get; }

	/// <summary>When set, accumulates time per operator type (diagnostics).</summary>
	public Dictionary<string, double> Profile { get; set; }

	/// <summary>Worker threads used by the heavy kernels.</summary>
	public int MaxThreads { get => _ctx.MaxThreads; set => _ctx.MaxThreads = Math.Max( 1, value ); }

	public OnnxSession( OnnxModel model, CancellationToken token = default )
	{
		Model = model;
		var unsupported = model.Graph.Nodes.Select( n => n.OpType ).Where( op => !OnnxOps.Supports( op ) ).Distinct().ToList();
		if ( unsupported.Count > 0 )
			throw new NotSupportedException( $"The model uses operators the managed runtime doesn't implement: {string.Join( ", ", unsupported )}." );
		foreach ( var init in model.Graph.Initializers )
		{
			token.ThrowIfCancellationRequested();
			_constants[init.Name] = Tensor.FromInitializer( init, model.BaseDirectory );
			_ctx.Constants.Add( _constants[init.Name] );
			init.Raw = null; // keep only the decoded copy
		}
		_order = Fuse( TopologicalOrder( model.Graph ) );
		for ( var i = 0; i < _order.Count; i++ )
			foreach ( var input in _order[i].Inputs ) if ( input.Length > 0 ) _lastUse[input] = i;
		InputNames = model.Graph.Inputs.Select( v => v.Name ).Where( n => !_constants.ContainsKey( n ) ).ToList();
		OutputNames = model.Graph.Outputs.Select( v => v.Name ).ToList();
	}

	public static OnnxSession Load( string path, CancellationToken token = default ) => new( OnnxModel.Load( path ), token );

	/// <summary>Looks up a constant (weights) by name, for code that needs raw tensors.</summary>
	public Tensor Constant( string name ) => _constants.TryGetValue( name, out var t ) ? t : null;

	/// <summary>Runs the graph. <paramref name="progress"/> receives the fraction of nodes executed.</summary>
	public Dictionary<string, Tensor> Run( IReadOnlyDictionary<string, Tensor> inputs, CancellationToken token = default, Action<float> progress = null )
	{
		var values = new Dictionary<string, Tensor>( _constants, StringComparer.Ordinal );
		foreach ( var (name, tensor) in inputs ) values[name] = tensor;
		foreach ( var name in InputNames )
			if ( !values.ContainsKey( name ) ) throw new ArgumentException( $"Missing model input \"{name}\"." );
		var keep = new HashSet<string>( OutputNames, StringComparer.Ordinal );
		// pooled buffers: how many live tensors use each one (views from Reshape & co share a buffer)
		var refs = new Dictionary<float[], int>( ReferenceEqualityComparer.Instance );
		var pool = _ctx.Pool;
		void AddRef( Tensor t )
		{
			if ( t?.F is { Length: > 0 } f && pool.Owns( f ) ) refs[f] = refs.GetValueOrDefault( f ) + 1;
		}
		void DropRef( Tensor t )
		{
			if ( t?.F is not { Length: > 0 } f || !refs.TryGetValue( f, out var n ) ) return;
			if ( --n > 0 ) { refs[f] = n; return; }
			refs.Remove( f );
			pool.Return( f );
		}
		var previous = ExecContext.Current;
		ExecContext.Current = _ctx;
		try
		{
		for ( var i = 0; i < _order.Count; i++ )
		{
			token.ThrowIfCancellationRequested();
			var node = _order[i];
			var args = new Tensor[node.Inputs.Length];
			for ( var k = 0; k < args.Length; k++ )
			{
				var name = node.Inputs[k];
				if ( name.Length == 0 ) continue; // omitted optional input
				if ( !values.TryGetValue( name, out args[k] ) )
					throw new InvalidOperationException( $"{node} needs \"{name}\" which hasn't been computed." );
			}
			Tensor[] outputs;
			var started = Profile is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
			try { outputs = OnnxOps.Run( node, args, _ctx ); }
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				throw new InvalidOperationException( $"{node} failed ({string.Join( ", ", args.Select( a => a?.ToString() ?? "-" ) )}): {e.Message}", e );
			}
			if ( Profile is not null )
			{
				var ms = System.Diagnostics.Stopwatch.GetElapsedTime( started ).TotalMilliseconds;
				Profile[node.OpType] = Profile.GetValueOrDefault( node.OpType ) + ms;
			}
			for ( var k = 0; k < node.Outputs.Length && k < outputs.Length; k++ )
				if ( node.Outputs[k].Length > 0 )
				{
					values[node.Outputs[k]] = outputs[k];
					AddRef( outputs[k] );
				}
				else DropRef( null );
			// release values nobody needs any more (their pooled buffers go back for reuse)
			foreach ( var name in node.Inputs.Distinct() )
				if ( name.Length > 0 && _lastUse.TryGetValue( name, out var last ) && last == i && !keep.Contains( name ) && !_constants.ContainsKey( name )
					&& values.Remove( name, out var dead ) )
					DropRef( dead );
			if ( progress is not null && (i & 15) == 0 ) progress( (i + 1f) / _order.Count );
		}
		progress?.Invoke( 1f );
		var result = OutputNames.ToDictionary( n => n, n => values.TryGetValue( n, out var t ) ? t : throw new InvalidOperationException( $"Output \"{n}\" was not produced." ) );
		// outputs now belong to the caller; anything else still pooled (unused outputs of multi-output nodes) is returned
		var given = new HashSet<float[]>( result.Values.Where( t => t.F is not null ).Select( t => t.F ), ReferenceEqualityComparer.Instance );
		foreach ( var f in refs.Keys.ToList() )
		{
			if ( given.Contains( f ) ) pool.Release( f );
			else pool.Return( f );
		}
		return result;
		}
		finally { ExecContext.Current = previous; }
	}

	/// <summary>
	/// Runtime-only fusions (the ONNX file is unchanged): MatMul by a constant matrix followed by Add of a
	/// constant bias becomes one GEMM with a bias epilogue; Sigmoid(x)*x becomes SiLU.
	/// </summary>
	List<OnnxNode> Fuse( List<OnnxNode> order )
	{
		var consumers = new Dictionary<string, List<OnnxNode>>( StringComparer.Ordinal );
		foreach ( var n in order )
			foreach ( var i in n.Inputs )
			{
				if ( i.Length == 0 ) continue;
				if ( !consumers.TryGetValue( i, out var list ) ) consumers[i] = list = new List<OnnxNode>();
				list.Add( n );
			}
		var outputs = new HashSet<string>( Model.Graph.Outputs.Select( o => o.Name ), StringComparer.Ordinal );
		bool SingleUse( string value, out OnnxNode user )
		{
			user = null;
			if ( outputs.Contains( value ) || !consumers.TryGetValue( value, out var list ) || list.Count != 1 ) return false;
			user = list[0];
			return true;
		}
		var producer = new Dictionary<string, OnnxNode>( StringComparer.Ordinal );
		foreach ( var n in order ) foreach ( var o in n.Outputs ) producer[o] = n;
		var removed = new HashSet<OnnxNode>( ReferenceEqualityComparer.Instance );
		// a fused node is emitted where the LAST node it replaces was, so every input already exists
		var replaceAt = new Dictionary<OnnxNode, OnnxNode>( ReferenceEqualityComparer.Instance );
		var result = new List<OnnxNode>( order.Count );
		foreach ( var n in order )
		{
			if ( replaceAt.TryGetValue( n, out var fusedHere ) ) { result.Add( fusedHere ); continue; }
			if ( removed.Contains( n ) ) continue;
			// RoPE: Add( Mul(x, cos), Mul(MatMul(x, R), sin) ) with R a signed permutation (rotate_half)
			if ( n.OpType == "MatMul" && _constants.TryGetValue( n.Inputs[1], out var rmat ) && FastKernels.RotationMap( rmat ) is not null
				&& SingleUse( n.Outputs[0], out var mulS ) && mulS.OpType == "Mul"
				&& SingleUse( mulS.Outputs[0], out var addR ) && addR.OpType == "Add" )
			{
				var x = n.Inputs[0];
				var sin = mulS.Inputs[0] == n.Outputs[0] ? mulS.Inputs[1] : mulS.Inputs[0];
				var otherOut = addR.Inputs[0] == mulS.Outputs[0] ? addR.Inputs[1] : addR.Inputs[0];
				if ( producer.TryGetValue( otherOut, out var mulC ) && mulC.OpType == "Mul" && SingleUse( otherOut, out _ )
					&& (mulC.Inputs[0] == x || mulC.Inputs[1] == x) )
				{
					var cos = mulC.Inputs[0] == x ? mulC.Inputs[1] : mulC.Inputs[0];
					removed.Add( mulS ); removed.Add( mulC );
					replaceAt[addR] = new OnnxNode { Name = n.Name + "+rope", OpType = "Rope", Inputs = new[] { x, cos, sin, n.Inputs[1] }, Outputs = addR.Outputs };
					continue;
				}
			}
			// a*b + c in one pass (adaLN modulate, gated residuals)
			if ( n.OpType == "Mul" && SingleUse( n.Outputs[0], out var addM ) && addM.OpType == "Add" && addM.Inputs[0] != addM.Inputs[1] && !replaceAt.ContainsKey( addM ) && !removed.Contains( addM ) )
			{
				var c = addM.Inputs[0] == n.Outputs[0] ? addM.Inputs[1] : addM.Inputs[0];
				replaceAt[addM] = new OnnxNode { Name = n.Name + "+add", OpType = "MulAdd", Inputs = new[] { n.Inputs[0], n.Inputs[1], c }, Outputs = addM.Outputs };
				continue;
			}
			if ( n.OpType == "MatMul" && _constants.TryGetValue( n.Inputs[1], out var w ) && w.Rank == 2
				&& SingleUse( n.Outputs[0], out var add ) && add.OpType == "Add" )
			{
				var other = add.Inputs[0] == n.Outputs[0] ? add.Inputs[1] : add.Inputs[0];
				if ( _constants.TryGetValue( other, out var bias ) && bias.Length == w.Shape[1] && bias.IsFloat )
				{
					replaceAt[add] = new OnnxNode { Name = n.Name + "+bias", OpType = "MatMulBias", Inputs = new[] { n.Inputs[0], n.Inputs[1], other }, Outputs = add.Outputs };
					continue;
				}
			}
			if ( n.OpType == "Sigmoid" && SingleUse( n.Outputs[0], out var mul ) && mul.OpType == "Mul"
				&& (mul.Inputs[0] == n.Inputs[0] || mul.Inputs[1] == n.Inputs[0]) )
			{
				replaceAt[mul] = new OnnxNode { Name = n.Name + "+silu", OpType = "SiLU", Inputs = new[] { n.Inputs[0] }, Outputs = mul.Outputs };
				continue;
			}
			result.Add( n );
		}
		return result;
	}

	static List<OnnxNode> TopologicalOrder( OnnxGraphProto graph )
	{
		// ONNX requires nodes to be topologically sorted already; verify and fall back to sorting.
		var available = new HashSet<string>( graph.Initializers.Select( i => i.Name ).Concat( graph.Inputs.Select( i => i.Name ) ), StringComparer.Ordinal ) { "" };
		var sorted = true;
		foreach ( var n in graph.Nodes )
		{
			if ( n.Inputs.Any( i => !available.Contains( i ) ) ) { sorted = false; break; }
			foreach ( var o in n.Outputs ) available.Add( o );
		}
		if ( sorted ) return graph.Nodes.ToList();

		available = new HashSet<string>( graph.Initializers.Select( i => i.Name ).Concat( graph.Inputs.Select( i => i.Name ) ), StringComparer.Ordinal ) { "" };
		var pending = graph.Nodes.ToList();
		var result = new List<OnnxNode>();
		while ( pending.Count > 0 )
		{
			var ready = pending.Where( n => n.Inputs.All( available.Contains ) ).ToList();
			if ( ready.Count == 0 ) throw new InvalidOperationException( "The ONNX graph has a cycle or an undefined input." );
			foreach ( var n in ready ) { result.Add( n ); pending.Remove( n ); foreach ( var o in n.Outputs ) available.Add( o ); }
		}
		return result;
	}
}
