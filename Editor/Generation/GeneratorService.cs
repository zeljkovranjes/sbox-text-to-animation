using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Generation;

namespace TextToAnimation.EditorTools.Generation;

/// <summary>
/// Editor-wide access to the motion model: one shared instance per editor session (the model is large),
/// single-flight loading, and state for the UI. All heavy work runs on worker threads.
/// </summary>
public sealed class GeneratorService
{
	public static GeneratorService Instance { get; } = new();

	readonly SemaphoreSlim _gate = new( 1, 1 );
	IMotionGenerator _loaded;

	public IGeneratorBackend Backend { get; set; } = new UniMate.UniMateBackend();
	public ModelState State { get; private set; } = ModelState.NotInstalled;
	public string LastError { get; private set; }

	public event Action StateChanged;

	GeneratorService() { Refresh(); }

	public void Refresh()
	{
		if ( _loaded is not null ) { Set( ModelState.Ready ); return; }
		try { Set( Backend.Inspect() ); }
		catch ( Exception e ) { LastError = e.Message; Set( ModelState.Failed ); }
	}

	void Set( ModelState state )
	{
		State = state;
		// a listener's failure (a closed window's widgets) must never fail the download or load that changed the state
		foreach ( var listener in StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>() )
		{
			try { ((Action)listener)(); }
			catch ( Exception e ) { System.Diagnostics.Trace.TraceWarning( $"[text-to-animation] model state listener failed: {e.Message}" ); }
		}
	}

	public async Task<bool> InstallAsync( IProgress<string> progress, CancellationToken token )
	{
		await _gate.WaitAsync( token );
		try
		{
			Set( ModelState.Downloading );
			await Task.Run( () => Backend.InstallAsync( progress, token ), token );
			Set( Backend.Inspect() );
			return State is ModelState.Ready or ModelState.Loading;
		}
		catch ( OperationCanceledException )
		{
			Set( Backend.Inspect() );
			throw;
		}
		catch ( Exception e )
		{
			LastError = e.Message;
			Set( ModelState.Failed );
			return false;
		}
		finally { _gate.Release(); }
	}

	/// <summary>Loads the model once per editor session.</summary>
	public async Task<IMotionGenerator> GetGeneratorAsync( IProgress<string> progress, CancellationToken token )
	{
		if ( _loaded is not null ) return _loaded;
		await _gate.WaitAsync( token );
		try
		{
			if ( _loaded is not null ) return _loaded;
			Set( ModelState.Loading );
			_loaded = await Task.Run( () => Backend.LoadAsync( progress, token ), token );
			Set( ModelState.Ready );
			return _loaded;
		}
		catch ( OperationCanceledException )
		{
			Set( Backend.Inspect() );
			throw;
		}
		catch ( Exception e )
		{
			LastError = e.Message;
			Set( ModelState.Failed );
			throw;
		}
		finally { _gate.Release(); }
	}

	/// <summary>Runs a generation on a worker thread.</summary>
	public async Task<IReadOnlyList<GeneratedMotion>> GenerateAsync( MotionRig rig, GenerationRequest request,
		IProgress<string> loadProgress, IProgress<GenerationProgress> progress, CancellationToken token )
	{
		var generator = await GetGeneratorAsync( loadProgress, token );
		var problems = generator.Validate( rig );
		if ( problems.Count > 0 ) throw new InvalidOperationException( string.Join( " ", problems ) );
		return await Task.Run( () => generator.GenerateAsync( rig, request, progress is null ? null : progress.Report, token ), token );
	}

	public void Remove()
	{
		_loaded = null;
		Backend.Remove();
		Refresh();
	}
}
