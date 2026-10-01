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
			init.Raw = null; // keep only the decoded copy
		}
		_order = TopologicalOrder( model.Graph );
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
			try { outputs = OnnxOps.Run( node, args, _ctx ); }
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				throw new InvalidOperationException( $"{node} failed ({string.Join( ", ", args.Select( a => a?.ToString() ?? "-" ) )}): {e.Message}", e );
			}
			for ( var k = 0; k < node.Outputs.Length && k < outputs.Length; k++ )
				if ( node.Outputs[k].Length > 0 ) values[node.Outputs[k]] = outputs[k];
			// release values nobody needs any more
			foreach ( var name in node.Inputs )
				if ( name.Length > 0 && _lastUse.TryGetValue( name, out var last ) && last == i && !keep.Contains( name ) && !_constants.ContainsKey( name ) )
					values.Remove( name );
			if ( progress is not null && (i & 15) == 0 ) progress( (i + 1f) / _order.Count );
		}
		progress?.Invoke( 1f );
		return OutputNames.ToDictionary( n => n, n => values.TryGetValue( n, out var t ) ? t : throw new InvalidOperationException( $"Output \"{n}\" was not produced." ) );
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
