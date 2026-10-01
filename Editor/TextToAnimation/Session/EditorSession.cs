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
using TextToAnimation.Editor.Workspace;
using TextToAnimation.Maths;
using TextToAnimation.Workspace;

namespace TextToAnimation.Editor.Session;

/// <summary>What changed, so panels only refresh what they show.</summary>
[Flags]
public enum SessionChange
{
	None = 0,
	Model = 1,
	ClipList = 2,
	ActiveClip = 4,
	ClipData = 8,
	Playhead = 16,
	Selection = 32,
	Undo = 64,
	Busy = 128,
}

/// <summary>
/// The editor's state, independent of any widget: the open model and its workspace, the active clip with
/// per-clip undo history, the playhead and selection. Every edit goes through <see cref="Edit"/>, which
/// records undo, bumps the revision, schedules an autosave and notifies listeners.
/// </summary>
public sealed class EditorSession
{
	public Asset ModelAsset { get; private set; }
	public Model Model { get; private set; }
	public MotionRig Rig { get; private set; }
	public AnimationWorkspace Workspace { get; private set; }
	public WorkspaceStore Store { get; private set; }
	public List<SequenceInfo> Sequences { get; private set; } = new();
	public string VmdlText { get; private set; }
	public List<string> LoadWarnings { get; } = new();

	public AnimClip ActiveClip { get; private set; }

	/// <summary>Final frames (keys applied) of the active clip, cached per revision.</summary>
	public List<XForm[]> ActiveFrames
	{
		get
		{
			if ( ActiveClip is null ) return null;
			if ( _framesCache is null || _framesRevision != ActiveClip.Revision || _framesClip != ActiveClip.Id )
			{
				_framesCache = ActiveClip.EvaluateFrames( Rig.Skeleton );
				_framesRevision = ActiveClip.Revision;
				_framesClip = ActiveClip.Id;
			}
			return _framesCache;
		}
	}
	List<XForm[]> _framesCache;
	int _framesRevision = -1;
	Guid _framesClip;

	public float Playhead { get; private set; }
	public int CurrentFrame => ActiveClip is null ? 0 : Math.Clamp( (int)MathF.Round( Playhead ), 0, Math.Max( 0, ActiveClip.FrameCount - 1 ) );
	public bool Playing { get; set; }
	public float PlaybackSpeed { get; set; } = 1f;
	public bool LoopPreview { get; set; } = true;

	/// <summary>Selected frame range on the timeline (inclusive), or null.</summary>
	public (int Start, int End)? Range { get; private set; }

	public HashSet<int> SelectedBones { get; } = new();
	public int? PrimaryBone { get; private set; }

	/// <summary>True while a long operation (generation, save, import) runs; edits are blocked.</summary>
	public bool Busy { get; private set; }
	public string BusyText { get; private set; } = "";

	public event Action<SessionChange> Changed;
	public event Action<string, UI.Tone> Status;

	readonly Dictionary<Guid, UndoStack> _undo = new();
	bool _saveScheduled;
	readonly HashSet<Guid> _dirtyClips = new();

	public bool HasModel => Rig is not null && Workspace is not null;
	public UndoStack Undo => ActiveClip is null ? null : UndoFor( ActiveClip );

	UndoStack UndoFor( AnimClip clip )
	{
		if ( !_undo.TryGetValue( clip.Id, out var stack ) ) _undo[clip.Id] = stack = new UndoStack();
		return stack;
	}

	public void Notify( SessionChange change ) => Changed?.Invoke( change );
	public void SetStatus( string text, UI.Tone tone = UI.Tone.Neutral ) => Status?.Invoke( text, tone );

	public void SetBusy( bool busy, string text = "" )
	{
		Busy = busy;
		BusyText = text;
		Notify( SessionChange.Busy );
	}

	// ------------------------------------------------------------------ model / workspace

	/// <summary>Root folder for workspaces of the open project.</summary>
	public static string WorkspaceRoot()
	{
		var project = Project.Current?.GetRootPath();
		return string.IsNullOrEmpty( project ) ? null : Path.Combine( project, "text_to_animation", "workspaces" );
	}

	/// <summary>Opens (or creates) the workspace of a model. Returns an error message or null.</summary>
	public async Task<string> OpenModelAsync( Asset asset )
	{
		await EngineThread.SwitchToMainThread();
		if ( asset is null ) return "No model selected.";
		FlushSave();
		var model = await ModelBridge.LoadAsync( asset.Path );
		if ( model is null || model.IsError ) return $"{asset.Path} could not be loaded.";
		if ( model.BoneCount == 0 ) return $"{asset.Name} has no skeleton - it can't be animated.";

		var skeleton = ModelBridge.SkeletonFromModel( model );
		var rig = MotionRig.Create( skeleton );
		var root = WorkspaceRoot();
		if ( root is null ) return "Open a project first.";
		var store = new WorkspaceStore( root );

		var vmdl = ModelBridge.SourcePathOf( asset );
		var text = vmdl is not null ? EngineThread.Try( () => File.ReadAllText( vmdl ) ) : null;

		LoadWarnings.Clear();
		AnimationWorkspace ws;
		var existing = store.FindWorkspaceFor( asset.Path );
		var fingerprint = AnimationWorkspace.Fingerprint( skeleton );
		if ( existing is { } id )
		{
			try
			{
				ws = store.Load( id, skeleton, LoadWarnings );
				if ( ws.SkeletonFingerprint.Length > 0 && ws.SkeletonFingerprint != fingerprint )
					LoadWarnings.Add( "The model's skeleton changed since this workspace was last used. Clips were remapped by bone name." );
			}
			catch ( Exception e )
			{
				LoadWarnings.Add( $"The workspace could not be read ({e.Message}); a new one was started. The old files were left in place." );
				ws = new AnimationWorkspace();
			}
		}
		else
		{
			ws = new AnimationWorkspace();
		}
		ws.ModelPath = asset.Path;
		ws.ModelName = asset.Name;
		ws.SkeletonFingerprint = fingerprint;

		ModelAsset = asset;
		Model = model;
		Rig = rig;
		Store = store;
		Workspace = ws;
		VmdlText = text;
		Sequences = ModelBridge.ListSequences( model, text );
		_undo.Clear();
		_dirtyClips.Clear();
		SelectedBones.Clear();
		PrimaryBone = null;
		Range = null;
		Playhead = 0;
		ActiveClip = ws.ActiveClipId is { } active ? ws.Find( active ) : ws.Clips.FirstOrDefault();
		store.Save( ws, skeleton, Array.Empty<AnimClip>() ); // registers the workspace in the index
		Notify( SessionChange.Model | SessionChange.ClipList | SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.Selection | SessionChange.Undo );
		return null;
	}

	/// <summary>Re-reads the model's sequence list after its vmdl changed.</summary>
	public async Task RefreshModelAsync()
	{
		if ( ModelAsset is null ) return;
		await EngineThread.SwitchToMainThread();
		Model = await ModelBridge.LoadAsync( ModelAsset.Path ) ?? Model;
		var vmdl = ModelBridge.SourcePathOf( ModelAsset );
		VmdlText = vmdl is not null ? EngineThread.Try( () => File.ReadAllText( vmdl ) ) : null;
		Sequences = ModelBridge.ListSequences( Model, VmdlText );
		Notify( SessionChange.Model );
	}

	// ------------------------------------------------------------------ clips

	public void SelectClip( AnimClip clip )
	{
		if ( clip == ActiveClip ) return;
		ActiveClip = clip;
		Workspace.ActiveClipId = clip?.Id;
		Playhead = 0;
		Range = null;
		ScheduleSave( null );
		Notify( SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.Playhead | SessionChange.Undo );
	}

	/// <summary>Adds a clip to the workspace and selects it.</summary>
	public AnimClip AddClip( AnimClip clip, bool select = true )
	{
		clip.Name = Workspace.UniqueName( clip.Name );
		ClipOps.Finish( clip, Rig );
		clip.Revision = 1;
		Workspace.Clips.Add( clip );
		ScheduleSave( clip );
		Notify( SessionChange.ClipList );
		if ( select ) SelectClip( clip );
		return clip;
	}

	/// <summary>A new clip holding the model's rest pose for one second.</summary>
	public AnimClip NewEmptyClip( string name = "New Animation" )
	{
		var fps = Workspace.DefaultFps;
		var rest = Rig.Skeleton.Bones.Select( b => b.RestLocal ).ToArray();
		var clip = new AnimClip { Name = name, Fps = fps, Origin = ClipOrigin.Empty };
		for ( var i = 0; i <= (int)fps; i++ ) clip.Frames.Add( (XForm[])rest.Clone() );
		return AddClip( clip );
	}

	public AnimClip Duplicate( AnimClip clip ) => AddClip( clip.Duplicate( clip.Name + " copy" ) );

	public void Rename( AnimClip clip, string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return;
		clip.Name = Workspace.UniqueName( name.Trim(), clip.Id );
		ScheduleSave( clip );
		Notify( SessionChange.ClipList | SessionChange.ActiveClip );
	}

	public void Delete( AnimClip clip )
	{
		var index = Workspace.Clips.IndexOf( clip );
		if ( index < 0 ) return;
		Workspace.Clips.RemoveAt( index );
		_undo.Remove( clip.Id );
		if ( ActiveClip == clip )
			SelectClip( Workspace.Clips.Count == 0 ? null : Workspace.Clips[Math.Min( index, Workspace.Clips.Count - 1 )] );
		ScheduleSave( null );
		Notify( SessionChange.ClipList );
	}

	/// <summary>Imports a model sequence as a new clip (the original stays untouched in the vmdl).</summary>
	public async Task<AnimClip> ImportSequenceAsync( string sequence, Model fromModel = null, CancellationToken token = default )
	{
		var source = fromModel ?? Model;
		var (frames, duration) = await ModelBridge.SampleSequenceAsync( source, sequence, Rig.Skeleton, Workspace.DefaultFps, token );
		var info = fromModel is null ? Sequences.FirstOrDefault( s => s.Name == sequence ) : null;
		var clip = new AnimClip
		{
			Name = sequence,
			Fps = Workspace.DefaultFps,
			Frames = frames,
			Origin = fromModel is null ? ClipOrigin.Imported : ClipOrigin.ImportedFile,
			SourceSequence = fromModel is null ? sequence : null,
			Looping = info?.Looping ?? false,
		};
		clip.Export.SequenceName = fromModel is null ? sequence : "";
		clip.Export.RootMotion = info?.HasExtractMotion == true ? ClipRootMotion.Extract : ClipRootMotion.Keep;
		return AddClip( clip );
	}

	// ------------------------------------------------------------------ edits

	/// <summary>Runs an edit on the active clip with undo, autosave and change notification.</summary>
	public bool Edit( string label, Action<AnimClip> edit, SessionChange extra = SessionChange.None )
	{
		var clip = ActiveClip;
		if ( clip is null || Busy ) return false;
		var undo = UndoFor( clip );
		undo.Record( clip, label );
		try
		{
			edit( clip );
		}
		catch ( Exception e ) when ( e is InvalidOperationException or ArgumentException )
		{
			undo.Undo( clip ); // revert the partial edit
			SetStatus( e.Message, UI.Tone.Amber );
			Notify( SessionChange.ClipData | SessionChange.Undo );
			return false;
		}
		clip.Revision++;
		if ( clip.FrameCount > 0 ) Playhead = Math.Clamp( Playhead, 0, clip.FrameCount - 1 );
		if ( Range is { } r && (r.End >= clip.FrameCount || r.Start >= clip.FrameCount) ) Range = null;
		ScheduleSave( clip );
		SetStatus( label, UI.Tone.Neutral );
		Notify( SessionChange.ClipData | SessionChange.Undo | extra );
		return true;
	}

	public void UndoEdit()
	{
		if ( ActiveClip is null || Busy ) return;
		var label = Undo.Undo( ActiveClip );
		if ( label is null ) return;
		AfterHistoryChange( ActiveClip, $"Undid {label}" );
	}

	public void RedoEdit()
	{
		if ( ActiveClip is null || Busy ) return;
		var label = Undo.Redo( ActiveClip );
		if ( label is null ) return;
		AfterHistoryChange( ActiveClip, $"Redid {label}" );
	}

	void AfterHistoryChange( AnimClip clip, string message )
	{
		Playhead = Math.Clamp( Playhead, 0, Math.Max( 0, clip.FrameCount - 1 ) );
		Range = null;
		ScheduleSave( clip );
		SetStatus( message );
		Notify( SessionChange.ClipData | SessionChange.Undo | SessionChange.ClipList );
	}

	/// <summary>Replaces the whole content of a clip (generation results) as one undoable edit.</summary>
	public void ReplaceClipFrames( AnimClip clip, List<XForm[]> frames, float fps, string label, Action<AnimClip> extra = null )
	{
		var undo = UndoFor( clip );
		undo.Record( clip, label );
		clip.Frames = frames;
		clip.Fps = fps;
		clip.Keys.Clear();
		clip.PinnedFrames.RemoveWhere( f => f >= frames.Count );
		extra?.Invoke( clip );
		ClipOps.Finish( clip, Rig );
		ScheduleSave( clip );
		if ( clip == ActiveClip )
		{
			Playhead = Math.Clamp( Playhead, 0, Math.Max( 0, clip.FrameCount - 1 ) );
			Range = null;
		}
		Notify( SessionChange.ClipData | SessionChange.Undo | SessionChange.ClipList );
	}

	// ------------------------------------------------------------------ playhead / selection

	public void Seek( float frame )
	{
		if ( ActiveClip is null ) return;
		Playhead = Math.Clamp( frame, 0, Math.Max( 0, ActiveClip.FrameCount - 1 ) );
		Notify( SessionChange.Playhead );
	}

	/// <summary>Advances playback by real time; returns true when the frame changed.</summary>
	public bool Tick( float deltaSeconds )
	{
		if ( !Playing || ActiveClip is null || ActiveClip.FrameCount < 2 ) return false;
		var last = ActiveClip.FrameCount - 1;
		var start = Range?.Start ?? 0;
		var end = Range?.End ?? last;
		var next = Playhead + deltaSeconds * ActiveClip.Fps * PlaybackSpeed;
		if ( next > end )
		{
			if ( LoopPreview ) next = start + (next - end) % Math.Max( 1, end - start );
			else { next = end; Playing = false; }
		}
		Playhead = next;
		Notify( SessionChange.Playhead );
		return true;
	}

	public void SetRange( int? start, int? end )
	{
		if ( start is null || end is null || ActiveClip is null ) Range = null;
		else
		{
			var a = Math.Clamp( Math.Min( start.Value, end.Value ), 0, ActiveClip.FrameCount - 1 );
			var b = Math.Clamp( Math.Max( start.Value, end.Value ), 0, ActiveClip.FrameCount - 1 );
			Range = a == b ? null : (a, b);
		}
		Notify( SessionChange.Selection );
	}

	public void SelectBone( int? bone, bool additive = false )
	{
		if ( !additive ) SelectedBones.Clear();
		if ( bone is int b )
		{
			if ( additive && SelectedBones.Contains( b ) ) SelectedBones.Remove( b );
			else SelectedBones.Add( b );
		}
		PrimaryBone = bone is int p && SelectedBones.Contains( p ) ? p : SelectedBones.Cast<int?>().FirstOrDefault();
		Notify( SessionChange.Selection );
	}

	public void SelectBones( IEnumerable<int> bones )
	{
		SelectedBones.Clear();
		foreach ( var b in bones ) SelectedBones.Add( b );
		PrimaryBone = SelectedBones.Cast<int?>().FirstOrDefault();
		Notify( SessionChange.Selection );
	}

	// ------------------------------------------------------------------ persistence

	/// <summary>Marks a clip dirty and saves the workspace on the next editor frame batch (debounced).</summary>
	public void ScheduleSave( AnimClip clip )
	{
		if ( clip is not null ) _dirtyClips.Add( clip.Id );
		if ( _saveScheduled ) return;
		_saveScheduled = true;
		_ = SaveSoonAsync();
	}

	async Task SaveSoonAsync()
	{
		await EngineThread.DelayOnMain( 750 );
		FlushSave();
	}

	/// <summary>Writes pending workspace changes now.</summary>
	public void FlushSave()
	{
		_saveScheduled = false;
		if ( Workspace is null || Store is null || Rig is null ) return;
		try
		{
			var dirty = Workspace.Clips.Where( c => _dirtyClips.Contains( c.Id ) ).ToList();
			Store.Save( Workspace, Rig.Skeleton, dirty );
			_dirtyClips.Clear();
		}
		catch ( Exception e )
		{
			SetStatus( $"Couldn't save the workspace: {e.Message}", UI.Tone.Red );
		}
	}
}
