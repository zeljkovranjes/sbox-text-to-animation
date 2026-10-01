using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.Generation;

/// <summary>Where the motion model is in its life cycle (drives the Generate panel's state).</summary>
public enum ModelState { NotInstalled, Downloading, Incomplete, Loading, Ready, Failed }

/// <summary>
/// A motion model the editor can download, load and run. The UI only talks to this and to
/// <see cref="IMotionGenerator"/>, so another model can be added by implementing this interface.
/// </summary>
public interface IGeneratorBackend
{
	string Name { get; }
	string Description { get; }

	/// <summary>Total download size in bytes (for the Download button).</summary>
	long DownloadBytes { get; }

	/// <summary>Fast check of what is on disk (no hashing).</summary>
	ModelState Inspect();

	/// <summary>Downloads and verifies the model files. Progress lines use the format
	/// "Downloading &lt;name&gt; · &lt;n&gt;% · &lt;size&gt; left" (the loading indicator turns them into a bar).</summary>
	Task InstallAsync( IProgress<string> progress, CancellationToken token );

	/// <summary>Prepares the model for inference (conversion and loading). Safe to call repeatedly.</summary>
	Task<IMotionGenerator> LoadAsync( IProgress<string> progress, CancellationToken token );

	/// <summary>Deletes the downloaded and converted files.</summary>
	void Remove();
}

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
		StateChanged?.Invoke();
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
