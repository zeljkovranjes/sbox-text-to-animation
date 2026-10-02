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
/// Dopri5: upstream's text-to-motion sampler (Sampler.sample_ode: torchdiffeq dopri5, adaptive, rtol 1e-3,
/// atol 1e-6, t from 0 to 1) - integrates the flow to convergence.
/// </summary>
public enum Integrator { Euler, Heun, AdamsBashforth2, Dopri5 }

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
	/// <summary>Dopri5 tolerances (upstream's sample_ode defaults).</summary>
	public double RelativeTolerance { get; init; } = 1e-3;
	public double AbsoluteTolerance { get; init; } = 1e-6;
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
			if ( _graphs.Count >= 2 )
			{
				var evicted = _graphs.Keys.First();
				_graphs.Remove( evicted );
				if ( _gpu.Remove( evicted, out var program ) ) program.Dispose();
			}
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
		=> Prepare( s, ConditioningInputs( s, stats, token ), token );

	/// <summary>Runs the prepare graph on given conditioning inputs (see <see cref="ConditioningInputs"/>).</summary>
	public PreparedSkeleton Prepare( UniMateSkeleton s, Dictionary<string, Tensor> conditioning, CancellationToken token )
	{
		var (prepare, _) = Graphs( s.Count, token );
		var outputs = prepare.Run( conditioning, token );
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
			var v = RunStep( J, step, feed, token );
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

		if ( settings.Method == Integrator.Dopri5 )
		{
			if ( settings.Keep is not null || t0 > 0 ) throw new ArgumentException( "Dopri5 samples from noise without constraints (upstream's text-to-motion)." );
			return Dopri5( x, Velocity, settings.RelativeTolerance, settings.AbsoluteTolerance, progress, token );
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

	readonly Dictionary<int, IGpuProgram> _gpu = new();
	readonly HashSet<int> _gpuRefused = new();

	/// <summary>
	/// One network call: on the GPU when a program for this skeleton size exists; otherwise on the CPU, and the first
	/// CPU call is traced into a GPU plan (shapes are fixed per size) which is then checked against that call's
	/// result before later calls use it. Any GPU failure falls back to the CPU for good.
	/// </summary>
	float[] RunStep( int joints, OnnxSession step, Dictionary<string, Tensor> feed, CancellationToken token )
	{
		IGpuProgram program;
		lock ( _lock ) _gpu.TryGetValue( joints, out program );
		if ( program is not null && GpuAcceleration.Enabled )
		{
			try { return program.Run( feed )["v"]; }
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				DisableGpu( joints, $"The GPU run failed ({e.Message}); using the CPU." );
			}
		}
		var tryGpu = GpuAcceleration.Enabled && GpuAcceleration.Compiler is not null;
		lock ( _lock ) tryGpu &= !_gpuRefused.Contains( joints ) && !_gpu.ContainsKey( joints );
		if ( !tryGpu ) return step.Run( feed, token )["v"].F;

		var recorder = new GpuPlan.Recorder();
		Dictionary<string, Tensor> cpu;
		lock ( step ) // the trace hook belongs to the session
		{
			step.Trace = recorder.Record;
			try { cpu = step.Run( feed, token ); }
			finally { step.Trace = null; }
		}
		var v = cpu["v"].F;
		var plan = GpuPlan.Build( step, recorder );
		if ( plan is null ) { Refuse( joints, $"No GPU plan: {GpuPlan.LastRefusal}." ); return v; }
		try
		{
			program = GpuAcceleration.Compiler( plan, step );
			var check = program.Run( feed )["v"];
			var worst = 0f;
			for ( var i = 0; i < v.Length; i++ ) worst = MathF.Max( worst, MathF.Abs( check[i] - v[i] ) );
			if ( !(worst <= GpuAcceleration.AgreementTolerance) )
			{
				// find the first kernel that disagrees (one more traced CPU run keeps every value)
				var values = new GpuPlan.Recorder { KeepValues = true };
				lock ( step )
				{
					step.Trace = values.Record;
					try { step.Run( feed, token ); }
					finally { step.Trace = null; }
				}
				var where = program.Diagnose( feed, values.Values );
				program.Dispose();
				Refuse( joints, $"The GPU result differs from the CPU by {worst:G3}; using the CPU. First difference: {where ?? "none found"}" );
				return v;
			}
			lock ( _lock ) _gpu[joints] = program;
			GpuAcceleration.Status = null;
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			program?.Dispose();
			Refuse( joints, $"The GPU couldn't be used ({e.Message}); using the CPU." );
		}
		return v;
	}

	void Refuse( int joints, string why )
	{
		lock ( _lock ) _gpuRefused.Add( joints );
		GpuAcceleration.Status = why;
	}

	void DisableGpu( int joints, string why )
	{
		lock ( _lock )
		{
			if ( _gpu.Remove( joints, out var p ) ) p.Dispose();
			_gpuRefused.Add( joints );
		}
		GpuAcceleration.Status = why;
	}

	/// <summary>
	/// torchdiffeq's dopri5 (Dormand-Prince 5(4) with its step control, initial step and 4th-order interpolation), as
	/// upstream's Sampler.sample_ode calls it: from t = 0 to 1, RMS error norm, safety 0.9, growth at most 10x,
	/// shrink at most 5x (none after an accepted step). The last step may pass t = 1; the result is interpolated there.
	/// </summary>
	public static float[] Dopri5( float[] y0, Func<float[], float, float[]> f, double rtol, double atol, Action<float> progress, CancellationToken token )
	{
		var n = y0.Length;
		double[] A = { 1 / 5.0, 3 / 10.0, 4 / 5.0, 8 / 9.0, 1.0, 1.0 };
		double[][] B =
		{
			new[] { 1 / 5.0 },
			new[] { 3 / 40.0, 9 / 40.0 },
			new[] { 44 / 45.0, -56 / 15.0, 32 / 9.0 },
			new[] { 19372 / 6561.0, -25360 / 2187.0, 64448 / 6561.0, -212 / 729.0 },
			new[] { 9017 / 3168.0, -355 / 33.0, 46732 / 5247.0, 49 / 176.0, -5103 / 18656.0 },
			new[] { 35 / 384.0, 0, 500 / 1113.0, 125 / 192.0, -2187 / 6784.0, 11 / 84.0 },
		};
		double[] E = { 35 / 384.0 - 1951 / 21600.0, 0, 500 / 1113.0 - 22642 / 50085.0, 125 / 192.0 - 451 / 720.0,
			-2187 / 6784.0 - -12231 / 42400.0, 11 / 84.0 - 649 / 6300.0, -1.0 / 60.0 };
		double[] Mid = { 6025192743 / 30085553152.0 / 2, 0, 51252292925 / 65400821598.0 / 2, -2691868925 / 45128329728.0 / 2,
			187940372067 / 1594534317056.0 / 2, -1776094331 / 19743644256.0 / 2, 11237099 / 235043384.0 / 2 };
		const double Safety = 0.9, IFactor = 10, DFactor = 0.2;
		const int Order = 5;
		double Rms( Func<int, double> v ) { var s = 0.0; for ( var i = 0; i < n; i++ ) { var x = v( i ); s += x * x; } return Math.Sqrt( s / n ); }

		// initial step (Hairer, Norsett & Wanner II.4), with the solver's order - 1 as torchdiffeq passes it
		var fy0 = f( y0, 0f );
		var d0 = Rms( i => y0[i] / (atol + Math.Abs( y0[i] ) * rtol ) );
		var d1 = Rms( i => fy0[i] / (atol + Math.Abs( y0[i] ) * rtol ) );
		var h0 = d0 < 1e-5 || d1 < 1e-5 ? 1e-6 : 0.01 * d0 / d1;
		var probe = new float[n];
		for ( var i = 0; i < n; i++ ) probe[i] = (float)(y0[i] + h0 * fy0[i]);
		var fProbe = f( probe, (float)h0 );
		var d2 = Rms( i => (fProbe[i] - fy0[i]) / (atol + Math.Abs( y0[i] ) * rtol ) ) / h0;
		var h1 = d1 <= 1e-15 && d2 <= 1e-15 ? Math.Max( 1e-6, h0 * 1e-3 ) : Math.Pow( 0.01 / Math.Max( d1, d2 ), 1.0 / Order );
		var dt = Math.Min( 100 * h0, h1 );

		var y = (float[])y0.Clone();
		var fy = fy0;
		double t = 0;
		var k = new float[7][];
		while ( true )
		{
			token.ThrowIfCancellationRequested();
			var t1 = t + dt;
			k[0] = fy;
			var yi = new float[n];
			for ( var s = 0; s < 6; s++ )
			{
				var b = B[s];
				for ( var i = 0; i < n; i++ )
				{
					var acc = 0.0;
					for ( var j = 0; j <= s; j++ ) acc += k[j][i] * (b[j] * dt);
					yi[i] = (float)(y[i] + acc);
				}
				var ts = A[s] == 1.0 ? t1 : t + A[s] * dt;
				k[s + 1] = f( yi, (float)ts );
				if ( s < 5 ) yi = new float[n];
			}
			// the 6th stage is the 5th-order solution (FSAL: k[6] is its derivative)
			var y1 = yi;
			var ratio = Rms( i =>
			{
				var err = 0.0;
				for ( var j = 0; j < 7; j++ ) err += k[j][i] * (dt * E[j]);
				return err / (atol + rtol * Math.Max( Math.Abs( y[i] ), Math.Abs( y1[i] ) ));
			} );
			var accept = ratio <= 1;
			var factor = ratio == 0 ? IFactor : Math.Min( IFactor, Math.Max( Safety / Math.Pow( ratio, 1.0 / Order ), accept ? 1.0 : DFactor ) );
			if ( accept )
			{
				if ( t1 >= 1.0 )
				{
					// 4th-order interpolation of this step at t = 1
					var x = (1.0 - t) / dt;
					var result = new float[n];
					for ( var i = 0; i < n; i++ )
					{
						var mid = 0.0;
						for ( var j = 0; j < 7; j++ ) mid += k[j][i] * (dt * Mid[j]);
						double ym = y[i] + mid, a0 = y[i], a1 = y1[i], f0 = k[0][i], f1 = k[6][i];
						var ca = 2 * dt * (f1 - f0) - 8 * (a1 + a0) + 16 * ym;
						var cb = dt * (5 * f0 - 3 * f1) + 18 * a0 + 14 * a1 - 32 * ym;
						var cc = dt * (f1 - 4 * f0) - 11 * a0 - 5 * a1 + 16 * ym;
						var cd = dt * f0;
						result[i] = (float)(a0 + x * cd + x * x * cc + x * x * x * cb + x * x * x * x * ca);
					}
					progress?.Invoke( 1f );
					return result;
				}
				y = y1;
				fy = k[6];
				t = t1;
				progress?.Invoke( (float)Math.Min( 0.99, t ) );
			}
			dt *= factor;
		}
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
