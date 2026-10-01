using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.Session;

/// <summary>
/// Runs model downloads and generations for the editor without ever blocking it: work happens on worker
/// threads, progress lines are marshalled to the main thread, everything can be cancelled, and failures leave
/// the workspace untouched. Results become new clips, or replace the active clip as one undoable edit.
/// </summary>
public sealed class GenerationFlow
{
	readonly EditorSession _session;
	CancellationTokenSource _cts;

	public GeneratorService Service => GeneratorService.Instance;

	/// <summary>Progress line for the loading indicator ("Downloading … · n% · … left" draws a bar).</summary>
	public event Action<string> Progress;

	/// <summary>True while a download or generation runs.</summary>
	public bool Running => _cts is not null;

	public GenerationFlow( EditorSession session ) => _session = session;

	public void Cancel() => _cts?.Cancel();

	void Report( string line ) => Progress?.Invoke( line );

	/// <summary>Downloads the model (the Download button).</summary>
	public async Task<bool> DownloadAsync()
	{
		if ( Running ) return false;
		_cts = new CancellationTokenSource();
		_session.SetBusy( true, "Downloading UniMate" );
		try
		{
			var progress = new EngineThread.MainThreadProgress<string>( Report );
			var ok = await Service.InstallAsync( progress, _cts.Token );
			await EngineThread.SwitchToMainThread();
			_session.SetStatus( ok ? "UniMate is installed - describe an animation and press Generate." : $"Download failed: {Service.LastError}", ok ? UI.Tone.Accent : UI.Tone.Red );
			return ok;
		}
		catch ( OperationCanceledException )
		{
			await EngineThread.SwitchToMainThread();
			_session.SetStatus( "Download paused. It resumes where it stopped next time.", UI.Tone.Amber );
			return false;
		}
		catch ( Exception e )
		{
			await EngineThread.SwitchToMainThread();
			_session.SetStatus( $"Download failed: {e.Message}", UI.Tone.Red );
			return false;
		}
		finally
		{
			await EngineThread.SwitchToMainThread();
			_cts?.Dispose();
			_cts = null;
			_session.SetBusy( false );
			Service.Refresh();
		}
	}

	/// <summary>
	/// Generates and applies the result. <paramref name="replace"/> overwrites the active clip (Regenerate);
	/// otherwise every take becomes a new clip next to it (originals are never touched).
	/// </summary>
	public async Task<bool> GenerateAsync( GenerationRequest request, bool replace, string baseName )
	{
		if ( Running || !_session.HasModel ) return false;
		var rig = _session.Rig;
		var target = _session.ActiveClip;
		if ( replace && target is null ) return false;
		_cts = new CancellationTokenSource();
		var started = DateTime.UtcNow;
		_session.SetBusy( true, "Generating" );
		try
		{
			var load = new EngineThread.MainThreadProgress<string>( Report );
			var progress = new EngineThread.MainThreadProgress<GenerationProgress>( p => Report( $"{p.Stage} · {p.Fraction * 100:0}%" ) );
			var results = await Service.GenerateAsync( rig, request, load, progress, _cts.Token );
			await EngineThread.SwitchToMainThread();
			var record = new GenerationRecord
			{
				Mode = request.Mode.ToString(), Prompts = request.Prompts.ToList(), Seed = request.Seed,
				DurationSeconds = request.DurationSeconds, Guidance = request.Guidance, Generator = Service.Backend.Name,
			};
			if ( replace )
			{
				var first = results[0];
				_session.ReplaceClipFrames( target, first.Frames, first.Fps, "Regenerate", c =>
				{
					c.Generation = record;
					if ( request.Mode != GenerationMode.InBetween ) c.PinnedFrames.Clear();
					ClipCleanup.GenerateFootsteps( c, rig );
				} );
				for ( var i = 1; i < results.Count; i++ ) AddResult( results[i], record, $"{target.Name} take {i + 1}", false );
			}
			else
			{
				for ( var i = 0; i < results.Count; i++ )
					AddResult( results[i], record, results.Count > 1 ? $"{baseName} {i + 1}" : baseName, i == 0 );
			}
			var notes = results.SelectMany( r => r.Notes ).Distinct().ToList();
			var seconds = (DateTime.UtcNow - started).TotalSeconds;
			_session.SetStatus( notes.Count > 0 ? string.Join( " ", notes ) : $"Generated in {seconds:0} s.", notes.Count > 0 ? UI.Tone.Amber : UI.Tone.Accent );
			return true;
		}
		catch ( OperationCanceledException )
		{
			await EngineThread.SwitchToMainThread();
			_session.SetStatus( "Generation cancelled.", UI.Tone.Neutral );
			return false;
		}
		catch ( Exception e )
		{
			await EngineThread.SwitchToMainThread();
			Log.Warning( $"[text-to-animation] generation failed: {e}" );
			_session.SetStatus( $"Generation failed: {e.Message}", UI.Tone.Red );
			return false;
		}
		finally
		{
			await EngineThread.SwitchToMainThread();
			_cts?.Dispose();
			_cts = null;
			_session.SetBusy( false );
		}
	}

	void AddResult( GeneratedMotion motion, GenerationRecord record, string name, bool select )
	{
		var clip = new AnimClip
		{
			Name = name,
			Fps = motion.Fps,
			Frames = motion.Frames,
			Origin = ClipOrigin.Generated,
			Generation = record.Clone(),
			Looping = false,
		};
		clip.Generation.Seed = motion.Seed;
		ClipCleanup.GenerateFootsteps( clip, _session.Rig );
		_session.AddClip( clip, select );
	}

	/// <summary>A short clip name from a prompt ("Walk cautiously forward, look behind" -> "Walk cautiously forward").</summary>
	public static string NameFromPrompt( string prompt )
	{
		var text = (prompt ?? "").Trim();
		var cut = text.IndexOfAny( new[] { ',', '.', ';' } );
		if ( cut > 0 ) text = text.Substring( 0, cut );
		var words = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Take( 4 ).ToArray();
		if ( words.Length == 0 ) return "Generated";
		var name = string.Join( ' ', words );
		return char.ToUpperInvariant( name[0] ) + name.Substring( 1 );
	}
}
