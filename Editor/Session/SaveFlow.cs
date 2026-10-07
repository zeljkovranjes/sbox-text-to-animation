using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Engine;
using TextToAnimation.Core.Vmdl;

namespace TextToAnimation.EditorTools.Session;

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
				// say the model is safe: a failed save puts the original vmdl back
				var restored = result.RolledBack && !result.Notes.Any( e => e.Contains( "restored", StringComparison.OrdinalIgnoreCase ) ) ? " Nothing was changed: the model is back to how it was." : "";
				_session.SetStatus( string.Join( " ", result.Errors.Concat( result.Notes ) ) + restored, UI.Tone.Red );
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
	List<Core.Maths.XForm[]> FinalFrames( AnimClip clip )
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

	/// <summary>
	/// The model's source FBX that can take a clip: an FBX 7.x file its vmdl (or a prefab it includes) renders, the one
	/// holding most of the skeleton's bones; null when there is none (no FBX source, or only FBX 6 files).
	/// </summary>
	public string ModelFbx()
	{
		var vmdl = _session.ModelAsset is null ? null : ModelBridge.SourcePathOf( _session.ModelAsset );
		var text = _session.VmdlText;
		if ( vmdl is null || string.IsNullOrEmpty( text ) || _session.Rig is null ) return null;
		// reading and parsing the model's FBX files takes a moment: once per model and vmdl text
		var key = vmdl + "|" + text.GetHashCode();
		if ( _modelFbxKey == key ) return _modelFbx;
		_modelFbxKey = key;
		return _modelFbx = FindModelFbx( vmdl, text );
	}
	string _modelFbxKey, _modelFbx;

	string FindModelFbx( string vmdl, string text )
	{
		var texts = new List<string> { text };
		texts.AddRange( EngineThread.Try( () => VmdlSources.IncludedPrefabTexts( text, vmdl ) ) ?? new List<string>() );
		var bones = _session.Rig.Skeleton.Bones.Select( b => b.Name ).ToList();
		return texts.SelectMany( t => System.Text.RegularExpressions.Regex.Matches( t, @"filename\s*=\s*""([^""]+\.fbx)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase ) )
			.Select( m => EngineThread.Try( () => VmdlSources.Resolve( m.Groups[1].Value, vmdl ) ) )
			.Where( p => p is not null && File.Exists( p ) ).Distinct( StringComparer.OrdinalIgnoreCase )
			.Select( p => (Path: p, Bytes: EngineThread.Try( () => File.ReadAllBytes( p ) )) )
			.Where( f => f.Bytes is not null && Formats.Fbx.FbxClipExport.CanWriteInto( f.Bytes ) )
			.Select( f => (f.Path, Matches: Formats.Fbx.FbxClipExport.MatchingBones( f.Bytes, bones )) )
			.Where( f => f.Matches >= 2 )
			.OrderByDescending( f => f.Matches ).Select( f => f.Path ).FirstOrDefault();
	}

	/// <summary>
	/// Writes <paramref name="clip"/> as an FBX: the model's own source FBX (mesh, skin, materials) carrying the clip
	/// when <paramref name="withModel"/>, else the skeleton with the clip.
	/// </summary>
	public bool ExportFbx( AnimClip clip, string file, bool withModel )
	{
		if ( clip is null || _session.Rig is null ) return false;
		try
		{
			var bones = _session.Rig.Skeleton.Bones.Select( b => new Formats.Fbx.FbxClipExport.Bone( b.Name, b.ParentIndex, b.RestLocal ) ).ToList();
			var frames = FinalFrames( clip );
			byte[] bytes;
			var note = "";
			if ( withModel )
			{
				var source = ModelFbx() ?? throw new InvalidOperationException( "This model has no FBX 2011-or-newer source to carry the animation - export the skeleton with the animation instead." );
				bytes = Formats.Fbx.FbxClipExport.WriteIntoSource( File.ReadAllBytes( source ), bones, frames, clip.Fps, clip.EffectiveSequenceName, out var report );
				note = $" ({Path.GetFileName( source )}: {report})";
			}
			else bytes = Formats.Fbx.FbxClipExport.WriteSkeleton( bones, frames, clip.Fps, clip.EffectiveSequenceName );
			Directory.CreateDirectory( Path.GetDirectoryName( file )! );
			File.WriteAllBytes( file, bytes );
			_session.SetStatus( $"Exported {Path.GetFileName( file )}{note}.", UI.Tone.Accent );
			return true;
		}
		catch ( Exception e )
		{
			_session.SetStatus( $"FBX export failed: {e.Message}", UI.Tone.Red );
			return false;
		}
	}

	/// <summary>The root compensation measured for this model (null until a save measured one).</summary>
	System.Numerics.Quaternion? RootCompensation => _session.Workspace?.RootCompensation is { Length: 4 } c
		? new System.Numerics.Quaternion( c[0], c[1], c[2], c[3] ) : null;
}
