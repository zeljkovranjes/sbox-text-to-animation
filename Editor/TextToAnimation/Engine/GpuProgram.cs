using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Editor;
using Sandbox;
using TextToAnimation.Editor.Inference.Onnx;

namespace TextToAnimation.Editor.Engine;

/// <summary>
/// A <see cref="GpuPlan"/> on the GPU through the library's compute shaders (Assets/shaders/t2a): every buffer is
/// created once (weights uploaded once), every launch's attributes are bound once, and a run uploads the graph
/// inputs, dispatches the launches in order and reads back the outputs. GPU work happens on the main thread; a
/// call from a worker waits for it there.
/// </summary>
public sealed class GpuProgram : IGpuProgram
{
	static readonly Dictionary<GpuPlan.Kernel, (string Path, string[] Slots)> Kernels = new()
	{
		[GpuPlan.Kernel.Copy] = ("t2a/t2a_copy_cs", new[] { "Src", "Dst" }),
		[GpuPlan.Kernel.Elementwise] = ("t2a/t2a_elementwise_cs", new[] { "A", "B", "C", "Out" }),
		[GpuPlan.Kernel.Gemm] = ("t2a/t2a_gemm_cs", new[] { "A", "Bm", "GemmBias", "Out" }),
		[GpuPlan.Kernel.Attention] = ("t2a/t2a_attention_cs", new[] { "Q", "Kb", "V", "Mask", "Out" }),
		[GpuPlan.Kernel.RmsNorm] = ("t2a/t2a_rmsnorm_cs", new[] { "X", "G", "Out" }),
		[GpuPlan.Kernel.Rope] = ("t2a/t2a_rope_cs", new[] { "X", "Cos", "Sin", "Out" }),
	};

	static Dictionary<GpuPlan.Kernel, ComputeShader> _shaders;

	/// <summary>Registers the GPU path for inference (called once when the editor loads the model).</summary>
	public static void Register() => GpuAcceleration.Compiler ??= ( plan, session ) => OnMain( () => new GpuProgram( plan, session ) );

	readonly GpuPlan _plan;
	readonly GpuBuffer<float>[] _buffers;
	readonly List<GpuBuffer<int>> _params = new();
	readonly List<(ComputeShader Shader, RenderAttributes Attributes, int X, int Y, GpuBuffer<float> Written)> _launches = new();
	bool[] _flushBefore;
	bool _disposed;

	GpuProgram( GpuPlan plan, OnnxSession session )
	{
		_shaders ??= Kernels.ToDictionary( kv => kv.Key, kv => new ComputeShader( kv.Value.Path ) );
		_plan = plan;
		_buffers = new GpuBuffer<float>[plan.Buffers.Count];
		try
		{
			foreach ( var b in plan.Buffers )
			{
				var buffer = new GpuBuffer<float>( b.Length );
				_buffers[b.Id] = buffer;
				if ( b.Constant is not null ) buffer.SetData( session.Constant( b.Constant ).F.AsSpan() );
			}
			foreach ( var launch in plan.Launches )
			{
				var (_, slots) = Kernels[launch.Kernel];
				var p = new GpuBuffer<int>( launch.Params.Length );
				p.SetData( launch.Params.AsSpan() );
				_params.Add( p );
				var attributes = new RenderAttributes();
				attributes.Set( "P", p );
				for ( var k = 0; k < slots.Length; k++ ) attributes.Set( slots[k], _buffers[launch.Buffers[k]] );
				_launches.Add( (_shaders[launch.Kernel], attributes, launch.ThreadsX, launch.ThreadsY, _buffers[launch.Buffers[^1]]) );
			}
			// a launch that reads what an unfinished launch wrote must wait: flush the GPU before it
			var dirty = new HashSet<int>();
			_flushBefore = new bool[plan.Launches.Count];
			for ( var i = 0; i < plan.Launches.Count; i++ )
			{
				var bufs = plan.Launches[i].Buffers;
				if ( bufs.Take( bufs.Length - 1 ).Any( dirty.Contains ) || dirty.Contains( bufs[^1] ) )
				{
					_flushBefore[i] = true;
					dirty.Clear();
				}
				dirty.Add( bufs[^1] );
			}
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	/// <summary>
	/// Runs the plan. From the main thread it runs at once; from a worker (generation) it is done in slices of at
	/// most <see cref="SliceMs"/> per editor frame while the worker waits, so the editor keeps drawing during
	/// generation instead of stalling for a whole network call.
	/// </summary>
	public Dictionary<string, float[]> Run( IReadOnlyDictionary<string, Tensor> feed )
	{
		ObjectDisposedException.ThrowIf( _disposed, this );
		var job = new Job( this, feed );
		if ( ThreadSafe.IsMainThread )
		{
			while ( !job.Advance( double.MaxValue ) ) { }
			return job.Result;
		}
		lock ( Pending ) Pending.Enqueue( job );
		job.Done.Wait();
		job.Done.Dispose();
		if ( job.Error is not null ) throw new InvalidOperationException( job.Error.Message, job.Error );
		return job.Result;
	}

	/// <summary>Main-thread time given to GPU work per editor frame.</summary>
	public static double SliceMs { get; set; } = double.TryParse( Environment.GetEnvironmentVariable( "T2A_GPU_SLICE_MS" ), out var ms ) ? ms : 10;

	static readonly Queue<Job> Pending = new();

	[EditorEvent.Frame]
	static void Pump()
	{
		var deadline = Job.Now + SliceMs;
		while ( Job.Now < deadline )
		{
			Job job;
			lock ( Pending ) if ( !Pending.TryPeek( out job ) ) return;
			bool finished;
			try { finished = job._program._disposed ? throw new ObjectDisposedException( nameof( GpuProgram ) ) : job.Advance( deadline ); }
			catch ( Exception e ) { job.Error = e; finished = true; }
			if ( !finished ) return;
			lock ( Pending ) Pending.Dequeue();
			job.Done.Set();
		}
	}

	/// <summary>One run in progress: inputs uploaded, launches dispatched up to <see cref="_next"/>, then read back.</summary>
	sealed class Job
	{
		static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
		public static double Now => Clock.Elapsed.TotalMilliseconds;

		public readonly GpuProgram _program;
		readonly IReadOnlyDictionary<string, Tensor> _feed;
		int _next = -1;
		public Dictionary<string, float[]> Result;
		public Exception Error;
		public readonly ManualResetEventSlim Done = new();

		public Job( GpuProgram program, IReadOnlyDictionary<string, Tensor> feed ) { _program = program; _feed = feed; }

		/// <summary>Does work until <paramref name="deadline"/> (at least one step); true when the result is ready.</summary>
		public bool Advance( double deadline )
		{
			var p = _program;
			if ( _next < 0 )
			{
				foreach ( var (name, (buffer, _)) in p._plan.Inputs )
					p._buffers[buffer].SetData( _feed[name].F.AsSpan() );
				_next = 0;
			}
			do
			{
				if ( _next == p._launches.Count )
				{
					var result = new Dictionary<string, float[]>( StringComparer.Ordinal );
					foreach ( var (name, (buffer, shape)) in p._plan.Outputs )
					{
						var data = new float[shape.Aggregate( 1, ( a, d ) => a * d )];
						p._buffers[buffer].GetData( data.AsSpan() );
						result[name] = data;
					}
					Result = result;
					return true;
				}
				if ( p._flushBefore[_next] ) Graphics.FlushGPU();
				var (shader, attributes, x, y, _) = p._launches[_next];
				shader.DispatchWithAttributes( attributes, x, y, 1 );
				_next++;
			}
			while ( Now < deadline );
			return false;
		}
	}

	public string Diagnose( IReadOnlyDictionary<string, Tensor> feed, IReadOnlyDictionary<string, float[]> cpu ) => OnMain( () =>
	{
		foreach ( var (name, (buffer, _)) in _plan.Inputs )
			_buffers[buffer].SetData( feed[name].F.AsSpan() );
		for ( var i = 0; i < _launches.Count; i++ )
		{
			var (shader, attributes, x, y, written) = _launches[i];
			shader.DispatchWithAttributes( attributes, x, y, 1 ); // the readback below waits for it
			var launch = _plan.Launches[i];
			var last = i + 1 == _launches.Count || _plan.Launches[i + 1].Value != launch.Value;
			if ( !last || !cpu.TryGetValue( launch.Value, out var expected ) ) continue;
			var got = new float[expected.Length];
			_buffers[launch.Buffers[^1]].GetData( got.AsSpan() );
			var worst = 0f; var at = 0;
			for ( var k = 0; k < got.Length; k++ )
			{
				var d = MathF.Abs( got[k] - expected[k] );
				if ( !(d <= worst) ) { worst = d; at = k; }
			}
			var scaleOf = expected.Max( MathF.Abs ) + 1e-6f;
			if ( !(worst <= 1e-3f * Math.Max( 1f, scaleOf )) )
				return $"launch {i} {launch.Kernel} -> \"{launch.Value}\" off by {worst:G3} at {at} (gpu {got[at]:G4}, cpu {expected[at]:G4}); params [{string.Join( ",", launch.Params.Take( 30 ) )}] threads {x}x{y}";
		}
		return null;
	} );

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		OnMain( () =>
		{
			foreach ( var b in _buffers ) b?.Dispose();
			foreach ( var p in _params ) p.Dispose();
			return 0;
		} );
	}

	/// <summary>Runs <paramref name="work"/> on the main thread and waits for it.</summary>
	static T OnMain<T>( Func<T> work )
	{
		if ( ThreadSafe.IsMainThread ) return work();
		T result = default;
		Exception error = null;
		using var done = new ManualResetEventSlim();
		MainThread.Queue( () =>
		{
			try { result = work(); }
			catch ( Exception e ) { error = e; }
			finally { done.Set(); }
		} );
		done.Wait();
		if ( error is not null ) throw new InvalidOperationException( error.Message, error );
		return result;
	}
}
