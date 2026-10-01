using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Vmdl;

namespace TextToAnimation.Editor.Session;

/// <summary>
/// Saving clips into the model, replacing existing sequences, exporting files and creating an inheriting
/// animation model. Originals are never overwritten unless the user picks Replace (and even then a backup is
/// kept and a failed compile restores them).
/// </summary>
public sealed class SaveFlow
{
	readonly EditorSession _session;
	public SaveFlow( EditorSession session ) => _session = session;

	/// <summary>Where a save would put the clip, for the Save tab's summary.</summary>
	public (string Sequence, bool Updates, string Problem) Plan( AnimClip clip )
	{
		if ( clip is null || !_session.HasModel ) return ("", false, "No animation open.");
		var vmdl = ModelBridge.SourcePathOf( _session.ModelAsset );
		if ( vmdl is null || EnginePaths.IsUnderEngineInstall( vmdl ) )
			return ("", false, "This model belongs to s&box itself and can't be changed. Saving creates a new animation model in your project that uses it.");
		var seq = SequenceFor( clip, out var updates );
		return (seq, updates, null);
	}

	/// <summary>The sequence name a save uses: the clip's own earlier save is updated, anything else gets a free name.</summary>
	string SequenceFor( AnimClip clip, out bool updatesOwn )
	{
		var wanted = clip.EffectiveSequenceName;
		var info = _session.Sequences.FirstOrDefault( s => string.Equals( s.Name, wanted, StringComparison.Ordinal ) );
		updatesOwn = info is not null && info.DefinedInModel && clip.SavedUtc is not null
			&& info.SourceFile.Replace( '\\', '/' ).Contains( "/" + ClipVmdlWriter.OutputFolderName + "/", StringComparison.OrdinalIgnoreCase );
		if ( updatesOwn || info is null ) return wanted;
		return _session.Workspace.UniqueSequenceName( wanted, _session.Sequences.Select( s => s.Name ), clip.Id );
	}

	/// <summary>Saves the clip into the model's vmdl (new sequence, or update of its own earlier save).</summary>
	public Task<bool> SaveAsync( AnimClip clip ) => SaveCoreAsync( clip, replaceSequence: null );

	/// <summary>Overwrites an existing sequence of the model with the clip (keeping the sequence's settings).</summary>
	public Task<bool> ReplaceAsync( AnimClip clip, string sequence ) => SaveCoreAsync( clip, sequence );

	async Task<bool> SaveCoreAsync( AnimClip clip, string replaceSequence )
	{
		if ( clip is null || _session.Busy || !_session.HasModel ) return false;
		var vmdl = ModelBridge.SourcePathOf( _session.ModelAsset );
		if ( vmdl is null || EnginePaths.IsUnderEngineInstall( vmdl ) ) return await SaveAsNewModelAsync( new[] { clip } );

		var sequence = replaceSequence ?? SequenceFor( clip, out var updates );
		var replace = replaceSequence is not null || _session.Sequences.Any( s => s.Name == sequence && s.DefinedInModel );
		_session.FlushSave();
		_session.SetBusy( true, $"Saving {sequence}" );
		try
		{
			var request = new ClipSaveRequest { Clip = clip, Frames = FinalFrames( clip ), SequenceName = sequence, ReplaceExisting = replace };
			var result = await VmdlSaveService.SaveAsync( _session.ModelAsset, _session.Rig, new[] { request }, t => _session.SetStatus( t ), CancellationToken.None,
				RootCompensation );
			await EngineThread.SwitchToMainThread();
			if ( result.LearnedRootCompensation is { } learned )
			{
				// remember how this model's animations must be oriented, so the next save is right first time
				_session.Workspace.RootCompensation = new[] { learned.X, learned.Y, learned.Z, learned.W };
				_session.FlushSave();
			}
			if ( !result.Success )
			{
				_session.SetStatus( string.Join( " ", result.Errors.Concat( result.Notes ) ), UI.Tone.Red );
				return false;
			}
			clip.SavedUtc = DateTime.UtcNow;
			clip.Export.SequenceName = sequence;
			if ( replaceSequence is not null ) clip.SourceSequence = replaceSequence;
			UI.ClipListPanel.SavedRevisions[clip.Id] = clip.Revision;
			_session.ScheduleSave( clip );
			await _session.RefreshModelAsync();
			var check = result.PlaybackError.TryGetValue( sequence, out var err ) ? $" Playback matches the editor (within {err:0.00} in)." : "";
			_session.SetStatus( $"Saved \"{sequence}\" into {Path.GetFileName( result.VmdlPath )}.{check}{(result.Notes.Count > 0 ? " " + string.Join( " ", result.Notes ) : "")}", UI.Tone.Accent );
			return true;
		}
		catch ( Exception e )
		{
			await EngineThread.SwitchToMainThread();
			Log.Warning( $"[text-to-animation] save failed: {e}" );
			_session.SetStatus( $"Saving failed: {e.Message}", UI.Tone.Red );
			return false;
		}
		finally
		{
			await EngineThread.SwitchToMainThread();
			_session.SetBusy( false );
		}
	}

	/// <summary>
	/// For models that can't be edited (engine content, compiled-only): writes the clips and a new vmdl that
	/// inherits the model (base_model_name) into the project's Assets/text_to_animation folder and compiles it.
	/// </summary>
	public async Task<bool> SaveAsNewModelAsync( IReadOnlyList<AnimClip> clips )
	{
		var assets = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assets ) ) { _session.SetStatus( "Open a project first.", UI.Tone.Red ); return false; }
		var modelName = Path.GetFileNameWithoutExtension( _session.ModelAsset.Path );
		var folder = Path.Combine( assets, "text_to_animation", AnimClip.SanitizeSequenceName( modelName ) );
		_session.SetBusy( true, "Creating animation model" );
		try
		{
			var items = clips.Select( c => (Sequence: c.EffectiveSequenceName, Clip: c, Frames: FinalFrames( c )) ).ToList();
			var files = VmdlSaveService.Export( folder, _session.ModelAsset.Path, _session.Rig, items, RootCompensation );
			var vmdl = files.FirstOrDefault( f => f.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) );
			if ( vmdl is null ) { _session.SetStatus( "Couldn't create the animation model.", UI.Tone.Red ); return false; }
			var compile = await VmdlCompiler.RegisterAndCompileAsync( vmdl, files.Where( f => f != vmdl ) );
			await EngineThread.SwitchToMainThread();
			if ( !compile.Compiled ) { _session.SetStatus( $"The animation model didn't compile: {compile.Error}", UI.Tone.Red ); return false; }
			foreach ( var c in clips ) { c.SavedUtc = DateTime.UtcNow; UI.ClipListPanel.SavedRevisions[c.Id] = c.Revision; _session.ScheduleSave( c ); }
			_session.SetStatus( $"Created {Path.GetFileName( vmdl )} - use it instead of {modelName} (it has all of {modelName}'s animations plus yours).", UI.Tone.Accent );
			return true;
		}
		catch ( Exception e )
		{
			await EngineThread.SwitchToMainThread();
			_session.SetStatus( $"Saving failed: {e.Message}", UI.Tone.Red );
			return false;
		}
		finally
		{
			await EngineThread.SwitchToMainThread();
			_session.SetBusy( false );
		}
	}

	/// <summary>The frames that get written: keys applied, and travel removed when the clip is saved "in place".</summary>
	List<Maths.XForm[]> FinalFrames( AnimClip clip )
	{
		if ( clip.Export.RootMotion != ClipRootMotion.InPlace ) return clip.EvaluateFrames( _session.Rig.Skeleton );
		var copy = clip.CloneDeep();
		copy.Frames = clip.EvaluateFrames( _session.Rig.Skeleton );
		copy.Keys.Clear();
		try { ClipOps.MakeInPlace( copy, _session.Rig ); } catch ( InvalidOperationException ) { }
		return copy.Frames;
	}

	/// <summary>Writes the clip as a DMX (plus a ready-to-use vmdl when the folder is in the project's Assets).</summary>
	public void Export( AnimClip clip, string folder )
	{
		if ( clip is null ) return;
		try
		{
			var files = VmdlSaveService.Export( folder, _session.ModelAsset.Path, _session.Rig,
				new[] { (clip.EffectiveSequenceName, clip, FinalFrames( clip )) }, RootCompensation );
			_session.SetStatus( $"Exported {string.Join( ", ", files.Select( Path.GetFileName ) )} to {folder}.", UI.Tone.Accent );
		}
		catch ( Exception e )
		{
			_session.SetStatus( $"Export failed: {e.Message}", UI.Tone.Red );
		}
	}

	/// <summary>The root compensation measured for this model (null until a save measured one).</summary>
	System.Numerics.Quaternion? RootCompensation => _session.Workspace?.RootCompensation is { Length: 4 } c
		? new System.Numerics.Quaternion( c[0], c[1], c[2], c[3] ) : null;
}
