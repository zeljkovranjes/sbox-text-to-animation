using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// The Text to Animation editor: pick a model, then create, import, generate and edit its animations and save
/// them back into the model. Left: the model's animations. Center: the live model and the timeline. Right:
/// Generate / Edit / Pose / Save. The main actions are always in the top bar.
/// </summary>
public sealed class TextToAnimationWindow : Widget
{
	public const string Title = "Text to Animation";
	public const string Icon = "auto_awesome";

	static TextToAnimationWindow _instance;
	public static TextToAnimationWindow Instance => _instance.IsValid() ? _instance : null;

	public EditorSession Session { get; } = new();
	public GenerationFlow Flow { get; }
	public SaveFlow Save { get; }

	// layout
	Button _modelButton;
	Label _modelPath;
	IconButton _undo, _redo;
	Button _newButton, _saveButton;
	TaButton _importButton, _replaceButton, _exportButton;
	ClipListPanel _clips;
	TaCard _viewCard;
	Label _clipTitle;
	TaPill _clipPill;
	Widget _viewHost;
	public AnimationViewport Viewport { get; private set; }
	ProcessingIndicator _indicator;
	TaDropZone _pickModel;
	TimelineWidget _timeline;
	IconButton _play, _loop;
	Label _time;
	ComboBox _speed;
	SegmentedControl _tabs;
	Widget[] _panels;
	public GeneratePanel GeneratePanel { get; private set; }
	public EditPanel EditPanel { get; private set; }
	public PosePanel PosePanel { get; private set; }
	public SavePanel SavePanel { get; private set; }
	TaStatusDot _statusDot;
	Label _status;
	TaButton _cancel;
	string _lastProgress = "";

	public TextToAnimationWindow( Widget parent ) : base( parent )
	{
		_instance = this;
		Name = "TextToAnimation";
		WindowTitle = Title;
		SetWindowIcon( Icon );
		MinimumSize = new Vector2( 1080, 640 );
		FocusMode = FocusMode.Click;
		Flow = new GenerationFlow( Session );
		Save = new SaveFlow( Session );
		Layout = Layout.Column();
		Layout.Margin = 10;
		Layout.Spacing = 8;
		BuildTopBar();
		BuildBody();
		BuildStatus();

		Session.Changed += OnSessionChanged;
		Session.Status += SetStatus;
		Flow.Progress += line => { _lastProgress = line; _indicator?.SetMessage( line ); };
		GeneratorService.Instance.StateChanged += () => MainThread.Queue( RefreshAll );
		RefreshAll();
		SetStatus( "Choose a model to start.", Tone.Neutral );
	}

	// ------------------------------------------------------------------ window hosting

	[Event( "tools.editorwindow.createview" )]
	static void RegisterViewMenu( Menu menu ) => EditorWindow.DockManager.RegisterDockType( new DockManager.DockInfo
	{
		Title = Title,
		Icon = Icon,
		CreateAction = () => { Open(); return null; },
	} );

	[Event( "tools.editorwindow.postcreateview" )]
	static void ConfigureViewMenu( Menu menu )
	{
		var option = menu.GetOption( Title );
		if ( option is null ) return;
		option.Toggled = null;
		option.Checkable = false;
		option.Triggered = () => Open();
	}

	/// <summary>Opens (or raises) the editor as a floating window.</summary>
	public static TextToAnimationWindow Open()
	{
		if ( Instance is { } existing )
		{
			existing.GetWindow().Show();
			existing.Show();
			existing.GetWindow().Raise();
			return existing;
		}
		var dialog = new Dialog( null );
		dialog.Window.Title = Title;
		dialog.Window.SetWindowIcon( Icon );
		dialog.Layout = Layout.Column();
		var window = dialog.Layout.Add( new TextToAnimationWindow( dialog ), 1 );
		dialog.Window.MinimumSize = new Vector2( 1080, 640 );
		dialog.Window.Size = new Vector2( 1480, 900 );
		dialog.Show();
		return window;
	}

	[Event( "asset.contextmenu", Priority = 55 )]
	static void OnAssetContextMenu( AssetContextMenu e )
	{
		var models = e.SelectedList.Select( a => a.Asset ).Where( a => a is not null && a.AssetType == AssetType.Model ).ToList();
		if ( models.Count != 1 ) return;
		var asset = models[0];
		e.Menu.AddOption( "Animate with Text to Animation", Icon, () => _ = Open().OpenModelAsync( asset ) );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		Session.FlushSave();
		Flow.Cancel();
		if ( _instance == this ) _instance = null;
	}

	// ------------------------------------------------------------------ layout

	void BuildTopBar()
	{
		var bar = Layout.AddRow();
		bar.Spacing = 8;
		_modelButton = bar.Add( new Button( "Choose model…", "view_in_ar" ) { FixedHeight = 30, MinimumWidth = 200, ToolTip = "The model (.vmdl) whose animations you're working on" } );
		_modelButton.Clicked = PickModel;
		_modelPath = bar.Add( TaStyle.Muted( new Label( "", this ) { FixedHeight = 30 }, small: true ), 1 );
		_undo = bar.Add( TaStyle.Icon( this, "undo", Session.UndoEdit, "Undo (Ctrl+Z)", 30 ) );
		_redo = bar.Add( TaStyle.Icon( this, "redo", Session.RedoEdit, "Redo (Ctrl+Y)", 30 ) );
		bar.AddSpacingCell( 8 );
		_newButton = bar.Add( new Button.Primary( "New Animation" ) { Icon = "add", Tint = TaStyle.Accent, FixedHeight = 30, ToolTip = "Start a new animation (then describe it in Generate)" } );
		_newButton.Clicked = NewAnimation;
		_importButton = bar.Add( new TaButton( this, "Import Existing", "download", ImportExisting, "Bring in an animation the model already has (or one from another model)", 30 ) );
		bar.AddSpacingCell( 8 );
		_replaceButton = bar.Add( new TaButton( this, "Replace Existing", "swap_horiz", ReplaceExisting, "Overwrite one of the model's animations with the open one", 30 ) );
		_exportButton = bar.Add( new TaButton( this, "Export", "file_download", ExportClip, "Write the open animation as a .dmx file", 30 ) );
		_saveButton = bar.Add( new Button.Primary( "Save to VMDL" ) { Icon = "save", Tint = TaStyle.Accent, FixedHeight = 30, ToolTip = "Save the open animation into the model (Ctrl+S)" } );
		_saveButton.Clicked = () => _ = Save.SaveAsync( Session.ActiveClip );
	}

	void BuildBody()
	{
		var body = Layout.AddRow( 1 );
		body.Spacing = 8;

		_clips = body.Add( new ClipListPanel( this, Session ) { FixedWidth = 250 } );
		_clips.NewClip = NewAnimation;
		_clips.ImportExisting = ImportExisting;
		_clips.SaveClip = c => _ = Save.SaveAsync( c );
		_clips.ExportClip = c => { Session.SelectClip( c ); ExportClip(); };

		var center = body.AddColumn( 1 );
		center.Spacing = 8;
		_viewCard = center.Add( new TaCard( this ), 1 );
		var vh = _viewCard.Header( "accessibility_new", "" );
		_clipTitle = vh.Add( new Label( "", _viewCard ) );
		_clipTitle.SetStyles( "font-weight: 600;" );
		_clipPill = vh.Add( new TaPill( _viewCard, "", TaStyle.Accent ) );
		vh.AddStretchCell();
		vh.Add( TaStyle.Toggle( _viewCard, "accessibility", true, on => { if ( Viewport is not null ) Viewport.ShowModel = on; }, "Show the model" ) );
		vh.Add( TaStyle.Toggle( _viewCard, "polyline", true, on => { if ( Viewport is not null ) Viewport.ShowSkeleton = on; }, "Show bones (amber = locked)" ) );
		vh.Add( TaStyle.Toggle( _viewCard, "route", true, on => { if ( Viewport is not null ) Viewport.ShowTrajectory = on; }, "Show the root path" ) );
		vh.Add( TaStyle.Toggle( _viewCard, "grid_on", true, on => { if ( Viewport is not null ) Viewport.ShowGround = on; }, "Show the floor" ) );
		vh.Add( TaStyle.Toggle( _viewCard, "videocam", true, on => { if ( Viewport is not null ) Viewport.FollowCharacter = on; }, "Camera follows the character" ) );
		vh.Add( TaStyle.Icon( _viewCard, "center_focus_strong", () => Viewport?.FrameCharacter(), "Frame the character (double click the view)" ) );

		_viewHost = _viewCard.Layout.Add( new Widget( _viewCard ) { Layout = Layout.Column() }, 1 );
		Viewport = _viewHost.Layout.Add( new AnimationViewport( _viewHost, Session ), 1 );
		_indicator = _viewHost.Layout.Add( new ProcessingIndicator( _viewHost ), 1 );
		_indicator.Visible = false;
		_pickModel = _viewHost.Layout.Add( new TaDropZone( _viewHost, "Choose a model to animate",
			"Pick any .vmdl from your project (or drag one here). Its animations open in the list on the left; new ones are generated with UniMate and saved back into it.",
			"Choose Model…", "view_in_ar", TaDrop.ModelExtensions, paths => _ = OpenModelPathAsync( paths[0] ), PickModel ), 1 );

		// timeline + transport
		var timelineCard = center.Add( new TaCard( this ) { FixedHeight = 132 } );
		var transport = timelineCard.Layout.AddRow();
		transport.Spacing = 4;
		transport.Add( TaStyle.Icon( timelineCard, "first_page", () => Session.Seek( 0 ), "Go to start (Home)" ) );
		transport.Add( TaStyle.Icon( timelineCard, "chevron_left", () => Step( -1 ), "Previous frame (Left)" ) );
		_play = transport.Add( TaStyle.Icon( timelineCard, "play_arrow", TogglePlay, "Play / pause (Space)" ) );
		transport.Add( TaStyle.Icon( timelineCard, "chevron_right", () => Step( 1 ), "Next frame (Right)" ) );
		transport.Add( TaStyle.Icon( timelineCard, "last_page", () => Session.Seek( Session.ActiveClip?.FrameCount - 1 ?? 0 ), "Go to end (End)" ) );
		_loop = transport.Add( TaStyle.Toggle( timelineCard, "repeat", true, on => Session.LoopPreview = on, "Loop the preview" ) );
		_speed = transport.Add( TaStyle.Framed( new ComboBox( timelineCard ) { FixedWidth = 76 } ) );
		foreach ( var s in new[] { 0.25f, 0.5f, 1f, 2f } )
		{
			var speed = s;
			_speed.AddItem( $"{speed:0.##}x", null, () => Session.PlaybackSpeed = speed, selected: speed == 1f );
		}
		_time = transport.Add( TaStyle.Muted( new Label( "", timelineCard ) ), 1 );
		transport.Add( TaStyle.Muted( new Label( "Shift+drag: select · double click Pinned: pin · right click: more", timelineCard ), small: true ) );
		_timeline = timelineCard.Layout.Add( new TimelineWidget( timelineCard, Session ), 1 );
		_timeline.BuildContextMenu = BuildTimelineMenu;

		// right panels
		var side = body.AddColumn();
		side.Spacing = 8;
		_tabs = side.Add( new SegmentedControl( this ) { FixedHeight = 30, FixedWidth = 360 } );
		_tabs.AddOption( "Generate", "auto_awesome" );
		_tabs.AddOption( "Edit", "content_cut" );
		_tabs.AddOption( "Pose", "accessibility_new" );
		_tabs.AddOption( "Save", "save" );
		_tabs.OnSelectedChanged = _ => ShowTab( _tabs.SelectedIndex );
		var scroll = side.Add( new ScrollArea( this ) { FixedWidth = 360 }, 1 );
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		scroll.SetStyles( "background-color: transparent;" );
		var canvas = new Widget( scroll ) { Layout = Layout.Column() };
		canvas.Layout.Margin = new Sandbox.UI.Margin( 0, 0, 10, 0 );
		canvas.SetStyles( "background-color: transparent;" );
		scroll.Canvas = canvas;
		GeneratePanel = canvas.Layout.Add( new GeneratePanel( canvas, Session, Flow ) );
		EditPanel = canvas.Layout.Add( new EditPanel( canvas, Session ) );
		PosePanel = canvas.Layout.Add( new PosePanel( canvas, Session ) );
		SavePanel = canvas.Layout.Add( new SavePanel( canvas, Session, Save ) { ReplaceExisting = ReplaceExisting, ExportFiles = ExportClip } );
		canvas.Layout.AddStretchCell();
		_panels = new Widget[] { GeneratePanel, EditPanel, PosePanel, SavePanel };
		ShowTab( 0 );
	}

	void BuildStatus()
	{
		var strip = Layout.AddRow();
		strip.Spacing = 8;
		_statusDot = strip.Add( new TaStatusDot( this ) );
		_status = strip.Add( new Label( "", this ) { FixedHeight = 24 }, 1 );
		_cancel = strip.Add( new TaButton( this, "Cancel", "close", () => Flow.Cancel(), "Stop the download or generation", 24 ) );
		_cancel.Visible = false;
	}

	public void ShowTab( int index )
	{
		for ( var i = 0; i < _panels.Length; i++ ) _panels[i].Visible = i == index;
		if ( _tabs.SelectedIndex != index ) _tabs.SelectedIndex = index;
	}

	// ------------------------------------------------------------------ actions

	void PickModel()
	{
		var picker = AssetPicker.Create( this, AssetType.Model );
		picker.Window.Title = "Choose the model to animate";
		picker.OnAssetPicked = assets => { if ( assets.FirstOrDefault() is { } asset ) _ = OpenModelAsync( asset ); };
		picker.Show();
	}

	async Task OpenModelPathAsync( string path )
	{
		var asset = AssetSystem.FindByPath( path );
		if ( asset is null ) { SetStatus( $"{Path.GetFileName( path )} isn't a model in this project.", Tone.Red ); return; }
		await OpenModelAsync( asset );
	}

	public async Task OpenModelAsync( Asset asset )
	{
		if ( Session.Busy ) return;
		SetStatus( $"Opening {asset.Name}…", Tone.Accent );
		var error = await Session.OpenModelAsync( asset );
		await EngineThread.SwitchToMainThread();
		if ( error is not null ) { SetStatus( error, Tone.Red ); return; }
		Viewport.SetModel( Session.Model );
		var warnings = Session.LoadWarnings.Concat( Session.Rig.Problems ).ToList();
		SetStatus( warnings.Count > 0 ? string.Join( " ", warnings )
			: Session.Workspace.Clips.Count == 0 ? $"{asset.Name} is ready. Describe an animation in Generate, or Import Existing."
			: $"Opened {asset.Name} with {Session.Workspace.Clips.Count} animations.", warnings.Count > 0 ? Tone.Amber : Tone.Accent );
	}

	void NewAnimation()
	{
		if ( !Session.HasModel ) { PickModel(); return; }
		Session.NewEmptyClip();
		ShowTab( 0 );
		SetStatus( "New animation: describe it in Generate (or import one and edit it).", Tone.Accent );
	}

	void ImportExisting()
	{
		if ( !Session.HasModel ) { PickModel(); return; }
		new ImportDialog( this, Session ).Show();
	}

	void ReplaceExisting()
	{
		if ( Session.ActiveClip is null ) { SetStatus( "Open an animation first.", Tone.Amber ); return; }
		new ReplaceDialog( this, Session, Save ).Show();
	}

	void ExportClip()
	{
		var clip = Session.ActiveClip;
		if ( clip is null ) { SetStatus( "Open an animation first.", Tone.Amber ); return; }
		var assets = Project.Current?.GetAssetsPath() ?? "";
		var start = Path.Combine( assets, "animations", clip.EffectiveSequenceName + ".dmx" );
		var file = EditorUtility.SaveFileDialog( "Export animation", "dmx", start );
		if ( string.IsNullOrEmpty( file ) ) return;
		Save.Export( clip, Path.GetDirectoryName( file )! );
	}

	void TogglePlay()
	{
		if ( Session.ActiveClip is null ) return;
		if ( !Session.Playing && Session.CurrentFrame >= Session.ActiveClip.FrameCount - 1 && !Session.LoopPreview ) Session.Seek( 0 );
		Session.Playing = !Session.Playing;
		RefreshTransport();
	}

	void Step( int delta )
	{
		Session.Playing = false;
		Session.Seek( Session.CurrentFrame + delta );
		RefreshTransport();
	}

	void BuildTimelineMenu( Menu menu, int frame )
	{
		var clip = Session.ActiveClip;
		if ( clip is null ) return;
		menu.AddOption( "Go to this frame", "my_location", () => Session.Seek( frame ) );
		menu.AddOption( clip.PinnedFrames.Contains( frame ) ? "Unpin this frame" : "Pin this frame", "push_pin", () => _timeline.TogglePin( frame ) );
		menu.AddOption( "Key selected bones here", "key", () => { Session.Seek( frame ); PosePanelAddKey(); } );
		menu.AddOption( "Delete keys here", "key_off", () => Session.Edit( $"Delete keys at {frame}", c => c.Keys.RemoveKeysAt( frame ) ) );
		menu.AddSeparator();
		menu.AddOption( "Split here", "call_split", () => { Session.Seek( frame ); ShowTab( 1 ); } );
		if ( Session.Range is { } r )
		{
			menu.AddOption( "Keep only the selection", "crop", () => Session.Edit( "Trim to selection", c => ClipOps.Crop( c, Session.Rig, r.Start, r.End ) ) );
			menu.AddOption( "Delete the selection", "delete", () => Session.Edit( "Delete section", c => ClipOps.DeleteSection( c, Session.Rig, r.Start, r.End ) ) );
			menu.AddOption( "Repeat the selection", "content_copy", () => Session.Edit( "Duplicate section", c => ClipOps.DuplicateSection( c, Session.Rig, r.Start, r.End ) ) );
			menu.AddOption( "Clear selection", "deselect", () => Session.SetRange( null, null ) );
		}
		menu.AddSeparator();
		menu.AddOption( "Zoom to fit", "fit_screen", _timeline.ResetZoom );
	}

	void PosePanelAddKey()
	{
		if ( Session.SelectedBones.Count == 0 ) { SetStatus( "Select bones first.", Tone.Amber ); return; }
		var frame = Session.CurrentFrame;
		var bones = Session.SelectedBones.Select( b => Session.Rig.Skeleton[b].Name ).ToList();
		Session.Edit( $"Key frame {frame}", c => { foreach ( var n in bones ) c.Keys.SetKey( n, frame, c.Keys.Evaluate( n, frame ) ); } );
	}

	// ------------------------------------------------------------------ keyboard

	protected override void OnKeyPress( KeyEvent e )
	{
		base.OnKeyPress( e );
		var handled = true;
		switch ( e.Key )
		{
			case KeyCode.Space: TogglePlay(); break;
			case KeyCode.Left: Step( -1 ); break;
			case KeyCode.Right: Step( 1 ); break;
			case KeyCode.Home: Session.Seek( 0 ); break;
			case KeyCode.End: Session.Seek( Session.ActiveClip?.FrameCount - 1 ?? 0 ); break;
			case KeyCode.Z when e.HasCtrl && e.HasShift: Session.RedoEdit(); break;
			case KeyCode.Z when e.HasCtrl: Session.UndoEdit(); break;
			case KeyCode.Y when e.HasCtrl: Session.RedoEdit(); break;
			case KeyCode.S when e.HasCtrl: _ = Save.SaveAsync( Session.ActiveClip ); break;
			case KeyCode.D when e.HasCtrl: if ( Session.ActiveClip is { } c ) Session.Duplicate( c ); break;
			case KeyCode.K: PosePanelAddKey(); break;
			case KeyCode.P: _timeline.TogglePin( Session.CurrentFrame ); break;
			case KeyCode.Escape: Session.SelectBone( null ); Session.SetRange( null, null ); break;
			default: handled = false; break;
		}
		if ( handled ) e.Accepted = true;
	}

	// ------------------------------------------------------------------ refresh

	[EditorEvent.Frame]
	void Frame()
	{
		if ( !this.IsValid() ) return;
		_indicator?.Tick();
		if ( Session.Playing ) RefreshTransport();
	}

	void OnSessionChanged( SessionChange change )
	{
		if ( (change & (SessionChange.Model | SessionChange.Busy | SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.ClipList | SessionChange.Undo)) != 0 ) RefreshAll();
		else if ( (change & SessionChange.Playhead) != 0 ) RefreshTransport();
		if ( (change & SessionChange.Model) != 0 && Session.Model is not null ) Viewport.SetModel( Session.Model );
	}

	void RefreshAll()
	{
		var hasModel = Session.HasModel;
		var clip = Session.ActiveClip;
		var busy = Session.Busy;
		_modelButton.Text = hasModel ? Session.ModelAsset.Name : "Choose model…";
		_modelPath.Text = hasModel ? Session.ModelAsset.Path : "";
		_undo.Enabled = Session.Undo?.CanUndo == true && !busy;
		_redo.Enabled = Session.Undo?.CanRedo == true && !busy;
		_undo.ToolTip = Session.Undo?.UndoLabel is { } u ? $"Undo {u} (Ctrl+Z)" : "Undo (Ctrl+Z)";
		_redo.ToolTip = Session.Undo?.RedoLabel is { } r ? $"Redo {r} (Ctrl+Y)" : "Redo (Ctrl+Y)";
		_newButton.Enabled = !busy;
		_importButton.Enabled = hasModel && !busy;
		_saveButton.Enabled = clip is not null && !busy;
		_replaceButton.Enabled = clip is not null && !busy;
		_exportButton.Enabled = clip is not null && !busy;
		_modelButton.Enabled = !busy;

		var showIndicator = busy && Flow.Running;
		_indicator.Visible = showIndicator;
		_indicator.Busy = showIndicator;
		if ( showIndicator && _lastProgress.Length == 0 ) _indicator.SetMessage( Session.BusyText + "…" );
		if ( !busy ) _lastProgress = "";
		Viewport.Visible = hasModel && !showIndicator;
		_pickModel.Visible = !hasModel && !showIndicator;
		_cancel.Visible = Flow.Running;

		_clipTitle.Text = clip?.Name ?? (hasModel ? "No animation open" : "");
		_clipPill.Set( clip is null ? "" : clip.Origin switch
		{
			ClipOrigin.Generated => "GENERATED",
			ClipOrigin.Imported or ClipOrigin.ImportedFile => "IMPORTED",
			ClipOrigin.Duplicated => "COPY",
			_ => "NEW",
		}, TaStyle.Accent );
		RefreshTransport();
	}

	void RefreshTransport()
	{
		var clip = Session.ActiveClip;
		_play.Icon = Session.Playing ? "pause" : "play_arrow";
		_play.Update();
		_time.Text = clip is null ? "" : $"Frame {Session.CurrentFrame} / {clip.FrameCount - 1}   ·   {Session.CurrentFrame / clip.Fps:0.00} s / {clip.Duration:0.00} s   ·   {clip.Fps:0} fps";
	}

	public void SetStatus( string text, Tone tone )
	{
		var color = TaStyle.Tone( tone );
		_status.Text = text;
		_status.ToolTip = text;
		_status.SetStyles( $"color: {(tone == Tone.Neutral ? Theme.Text : color).Hex};" );
		_statusDot.Color = tone == Tone.Neutral ? TaStyle.Accent : color;
	}
}
