using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TextToAnimation.Editor.Inference.Onnx;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>Files of an installed UniMate model.</summary>
public sealed record UniMateFiles( string Directory )
{
	public string WeightBlob => Path.Combine( Directory, $"unimate_v2_g{UniMateGraphBuilder.FormatVersion}.weights" );
	public string T5Encoder => Path.Combine( Directory, "t5_encoder.onnx" );
	public string T5Tokenizer => Path.Combine( Directory, "t5_tokenizer.json" );
	public string Graphs => Path.Combine( Directory, $"graphs_g{UniMateGraphBuilder.FormatVersion}" );
}

/// <summary>Conditioning tensors of one skeleton, computed once (the prepare graph's outputs).</summary>
public sealed class PreparedSkeleton
{
	public required UniMateSkeleton Skeleton { get; init; }
	public required Dictionary<string, Tensor> Tensors { get; init; }
}

/// <summary>
/// ODE integrators for the flow: Euler (1 model call per step, 1st order), Heun (2 calls, 2nd order) and
/// Adams-Bashforth 2 (1 call per step, 2nd order: reuses the previous step's velocity).
/// </summary>
public enum Integrator { Euler, Heun, AdamsBashforth2 }

/// <summary>Sampling settings.</summary>
public sealed class SampleSettings
{
	public int Steps { get; init; } = 24;
	public float Guidance { get; init; } = 3f;
	public Integrator Method { get; init; } = Integrator.Euler;
	/// <summary>
	/// Time-grid shift (1 = uniform). Above 1 the steps crowd towards the noisy start of the flow, where the
	/// velocity changes fastest: t' = s t / (1 + (s - 1) t).
	/// </summary>
	public float TimeShift { get; init; } = 1f;
	/// <summary>Start time of the flow (0 = pure noise; &gt;0 starts from a noised copy of <see cref="Known"/>: variations).</summary>
	public float StartTime { get; init; }
	/// <summary>Known normalised motion (J,12,T) for constraints / variations.</summary>
	public float[] Known { get; init; }
	/// <summary>(J,12,T) mask of values held to <see cref="Known"/> (replacement sampling).</summary>
	public bool[] Keep { get; init; }
}

/// <summary>
/// The UniMate model running on the managed ONNX runtime: the flan-t5 text encoder plus the denoiser's
/// prepare/step graphs (generated in C# per skeleton from the shared weight blob), and the flow-matching
/// sampler with classifier-free guidance and replacement constraints.
/// </summary>
public sealed class UniMateModel
{
	public const int Frames = 60;
	public const float Fps = 30f;
	const int Batch = 2; // conditional + unconditional rows

	readonly UniMateFiles _files;
	readonly WeightBlob _blob;
	readonly UniMateArch _arch = UniMateArch.V2;
	public T5TextEncoder Text { get; }
	readonly Dictionary<int, (OnnxSession Prepare, OnnxSession Step)> _graphs = new();
	readonly object _lock = new();

	UniMateModel( UniMateFiles files, WeightBlob blob, T5TextEncoder text )
	{
		_files = files;
		_blob = blob;
		Text = text;
	}

	public static UniMateModel Load( UniMateFiles files, CancellationToken token = default )
	{
		if ( !WeightBlob.Exists( files.WeightBlob ) ) throw new FileNotFoundException( "The UniMate weights are not prepared.", files.WeightBlob );
		var blob = WeightBlob.Open( files.WeightBlob );
		token.ThrowIfCancellationRequested();
		var text = new T5TextEncoder( OnnxSession.Load( files.T5Encoder, token ), T5Tokenizer.Load( files.T5Tokenizer ) );
		return new UniMateModel( files, blob, text );
	}

	/// <summary>
	/// Writes the shared weight blob from checkpoint weights (EMA, PyTorch names): every weight in the layout the
	/// graphs use. Done once at install; afterwards graphs for any skeleton are generated without the checkpoint.
	/// </summary>
	public static void WriteWeights( IReadOnlyDictionary<string, Runtime.WeightTensor> weights, string blobPath )
	{
		using var blob = WeightBlob.Create( blobPath );
		var builder = new UniMateGraphBuilder( weights, UniMateArch.V2, blob );
		builder.BuildPrepare( 22, null, null );
		builder.BuildStep( Batch, 22, Frames, null, null );
		blob.Commit();
	}

	/// <summary>Builds (or reuses) the graphs for a joint count; graph files are cached on disk.</summary>
	(OnnxSession Prepare, OnnxSession Step) Graphs( int joints, CancellationToken token )
	{
		lock ( _lock )
		{
			if ( _graphs.TryGetValue( joints, out var g ) ) return g;
			Directory.CreateDirectory( _files.Graphs );
			var prepPath = Path.Combine( _files.Graphs, $"prepare_j{joints}.onnx" );
			var stepPath = Path.Combine( _files.Graphs, $"step_b{Batch}_j{joints}_t{Frames}.onnx" );
			var builder = new UniMateGraphBuilder( null, _arch, _blob );
			if ( !File.Exists( prepPath ) ) WriteAtomic( prepPath, builder.BuildPrepare( joints, null, null ) );
			if ( !File.Exists( stepPath ) ) WriteAtomic( stepPath, builder.BuildStep( Batch, joints, Frames, null, null ) );
			OnnxSession Load( string path )
			{
				var model = OnnxModel.Parse( File.ReadAllBytes( path ) );
				model.BaseDirectory = _files.Directory; // the weight blob lives in the model folder
				return new OnnxSession( model, token );
			}
			// keep at most two skeleton sizes in memory (the step graph holds ~300 MB of weights)
			if ( _graphs.Count >= 2 ) _graphs.Remove( _graphs.Keys.First() );
			g = (Load( prepPath ), Load( stepPath ));
			_graphs[joints] = g;
			return g;
		}
	}

	static void WriteAtomic( string path, byte[] bytes )
	{
		File.WriteAllBytes( path + ".tmp", bytes );
		File.Move( path + ".tmp", path, overwrite: true );
	}

	/// <summary>Runs the prepare graph for a skeleton (joint names are embedded with T5).</summary>
	public PreparedSkeleton Prepare( UniMateSkeleton s, UniMateStats stats, CancellationToken token )
	{
		var (prepare, _) = Graphs( s.Count, token );
		var outputs = prepare.Run( ConditioningInputs( s, stats, token ), token );
		return new PreparedSkeleton { Skeleton = s, Tensors = outputs };
	}

	/// <summary>
	/// The skeleton conditioning the network gets (upstream create_sample_condition: normalised T-pose features with
	/// identity rotations, the parents' copies, relations, graph distances, depths, spectral features, joint-name
	/// embeddings).
	/// </summary>
	public Dictionary<string, Tensor> ConditioningInputs( UniMateSkeleton s, UniMateStats stats, CancellationToken token )
	{
		var J = s.Count;
		var tpos = new float[J * 12]; var parentRow = new float[J * 12];
		for ( var j = 0; j < J; j++ )
		{
			var row = new double[12];
			row[0] = s.TPose[j].X; row[1] = s.TPose[j].Y; row[2] = s.TPose[j].Z;
			row[3] = 1; row[7] = 1; // identity 6D
			for ( var c = 0; c < 12; c++ ) tpos[j * 12 + c] = stats.Normalize( j, c, row[c] );
		}
		for ( var j = 0; j < J; j++ )
		{
			var src = s.Parents[j] < 0 ? j : s.Parents[j];
			Array.Copy( tpos, src * 12, parentRow, j * 12, 12 );
		}
		var rel = new long[J * J]; var dist = new long[J * J];
		for ( var i = 0; i < J; i++ ) for ( var k = 0; k < J; k++ ) { rel[i * J + k] = s.Relations[i, k]; dist[i * J + k] = s.GraphDist[i, k]; }
		var spec = new float[J * 8];
		for ( var j = 0; j < J; j++ ) for ( var c = 0; c < 8; c++ ) spec[j * 8 + c] = s.Spectral[j, c];
		var names = new float[J * T5TextEncoder.Width];
		for ( var j = 0; j < J; j++ ) Array.Copy( Text.Encode( s.CleanNames[j], token ), 0, names, j * T5TextEncoder.Width, T5TextEncoder.Width );
		return new Dictionary<string, Tensor>
		{
			["tpos"] = Tensor.Float( new[] { J, 12 }, tpos ),
			["tpos_parent"] = Tensor.Float( new[] { J, 12 }, parentRow ),
			["joint_rel"] = Tensor.Int64( new[] { J, J }, rel ),
			["graph_dist"] = Tensor.Int64( new[] { J, J }, dist ),
			["depth"] = Tensor.Int64( new[] { J }, s.Depths.ToArray() ),
			["spectral"] = Tensor.Float( new[] { J, 8 }, spec ),
			["name_emb"] = Tensor.Float( new[] { J, T5TextEncoder.Width }, names ),
		};
	}

	/// <summary>
	/// Flow-matching sample: x(0) = noise, dx/dt = v(x,t) with classifier-free guidance, integrated to t = 1.
	/// Values under <see cref="SampleSettings.Keep"/> follow the known motion along the interpolant
	/// (replacement sampling, as upstream). Returns the normalised motion (J,12,T).
	/// </summary>
	public float[] Sample( PreparedSkeleton prep, float[] caption, float[] noise, SampleSettings settings,
		Action<float> progress, CancellationToken token )
	{
		var J = prep.Skeleton.Count;
		var n = J * 12 * Frames;
		if ( noise.Length != n ) throw new ArgumentException( "Noise has the wrong size." );
		var (_, step) = Graphs( J, token );
		var feed = new Dictionary<string, Tensor>( prep.Tensors );
		var captions = new float[Batch * T5TextEncoder.Width];
		Array.Copy( caption, captions, T5TextEncoder.Width ); // row 1 stays zero: unconditional
		feed["caption_emb"] = Tensor.Float( new[] { Batch, T5TextEncoder.Width }, captions );

		var steps = Math.Max( 1, settings.Steps );
		var t0 = Math.Clamp( settings.StartTime, 0f, 0.98f );
		var x = new float[n];
		if ( t0 > 0 && settings.Known is { } known0 )
			for ( var i = 0; i < n; i++ ) x[i] = t0 * known0[i] + (1 - t0) * noise[i];
		else Array.Copy( noise, x, n );
		Replace( x, t0 );

		float[] Velocity( float[] state, float t )
		{
			var input = new float[Batch * n];
			Array.Copy( state, 0, input, 0, n );
			Array.Copy( state, 0, input, n, n );
			feed["x"] = Tensor.Float( new[] { Batch, J, 12, Frames }, input );
			feed["t"] = Tensor.Float( new[] { Batch }, new[] { t, t } );
			var v = step.Run( feed, token )["v"].F;
			var result = new float[n];
			var g = settings.Guidance;
			for ( var i = 0; i < n; i++ ) result[i] = v[n + i] + g * (v[i] - v[n + i]); // v_u + g (v_c - v_u)
			return result;
		}

		void Replace( float[] state, float t )
		{
			if ( settings.Keep is not { } keep || settings.Known is not { } k ) return;
			for ( var i = 0; i < n; i++ )
				if ( keep[i] ) state[i] = (1 - t) * noise[i] + t * k[i];
		}

		// time grid from t0 to 1 (optionally shifted towards the start)
		var grid = new float[steps + 1];
		var shift = settings.TimeShift > 0f ? settings.TimeShift : 1f;
		for ( var k = 0; k <= steps; k++ )
		{
			var u = (float)k / steps;
			var w = shift == 1f ? u : shift * u / (1f + (shift - 1f) * u);
			grid[k] = t0 + (1f - t0) * w;
		}
		float[] previousV = null;
		var previousDt = 0f;
		for ( var s = 0; s < steps; s++ )
		{
			token.ThrowIfCancellationRequested();
			var t = grid[s];
			var dt = grid[s + 1] - t;
			var v1 = Velocity( x, t );
			if ( settings.Method == Integrator.Heun && s < steps - 1 )
			{
				var xp = new float[n];
				for ( var i = 0; i < n; i++ ) xp[i] = x[i] + dt * v1[i];
				Replace( xp, t + dt );
				var v2 = Velocity( xp, t + dt );
				for ( var i = 0; i < n; i++ ) x[i] += dt * 0.5f * (v1[i] + v2[i]);
			}
			else if ( settings.Method == Integrator.AdamsBashforth2 && previousV is not null )
			{
				// variable-step AB2: extrapolate the velocity to the middle of the step from the last two
				var r = dt / (2f * previousDt);
				for ( var i = 0; i < n; i++ ) x[i] += dt * ((1f + r) * v1[i] - r * previousV[i]);
			}
			else
			{
				for ( var i = 0; i < n; i++ ) x[i] += dt * v1[i];
			}
			previousV = v1;
			previousDt = dt;
			Replace( x, t + dt );
			progress?.Invoke( (s + 1f) / steps );
		}
		return x;
	}

	/// <summary>Standard normal noise (J,12,T) from a seed (Box-Muller).</summary>
	public static float[] Noise( int joints, int seed )
	{
		var rng = new Random( seed );
		var n = joints * 12 * Frames;
		var r = new float[n];
		for ( var i = 0; i < n; i += 2 )
		{
			var u1 = 1.0 - rng.NextDouble(); var u2 = rng.NextDouble();
			var mag = Math.Sqrt( -2.0 * Math.Log( u1 ) );
			r[i] = (float)(mag * Math.Cos( 2 * Math.PI * u2 ));
			if ( i + 1 < n ) r[i + 1] = (float)(mag * Math.Sin( 2 * Math.PI * u2 ));
		}
		return r;
	}
}
