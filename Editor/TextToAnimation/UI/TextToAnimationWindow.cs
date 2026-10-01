using System;
using System.Collections.Generic;
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
/// Text to Animation. First a start page (drop or choose a .vmdl, or start fresh from a copy of an s&amp;box
/// character); then the editor: the animations on the left, the model, a prominent prompt and the timeline in
/// the middle, and the Edit / Pose / Export tools down the whole right side.
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
	// first page
	StartPage _start;
	ProcessingIndicator _firstLoad;
	string _starting;
	Widget _main;

	// top bar
	TaButton _modelButton;
	Widget _editorActions;
	IconButton _undo, _redo;
	Button _saveButton;
	TaButton _moreButton;

	// editor page
	ClipListPanel _clips;
	Label _clipTitle;
	TaPill _clipPill;
	Widget _viewHost;
	public AnimationViewport Viewport { get; private set; }
	ProcessingIndicator _indicator;
	TimelineWidget _timeline;
	IconButton _play, _loop;
	Label _time;
	ComboBox _speed;
	PromptComposer _editPrompt;
	ChipButton _intentChip;
	EditIntent _intent = EditIntent.Change;
	TaTabBar _sideTabs;
	Widget[] _panels;
	public EditPanel EditPanel { get; private set; }
	public PosePanel PosePanel { get; private set; }
	public SavePanel SavePanel { get; private set; }

	DownloadStrip _editorDownload;

	// status
	Widget _statusStrip;
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

		_start = Layout.Add( new StartPage( this, paths => _ = OpenModelPathAsync( paths[0] ), PickModel, ChooseFromDisk,
			() => _ = NewFromStarterAsync( StarterModels.Citizen ), () => _ = NewFromStarterAsync( StarterModels.CitizenHuman ), Flow ), 1 );
		_firstLoad = Layout.Add( new ProcessingIndicator( this ) { Visible = false }, 1 );
		_main = Layout.Add( new Widget( this ) { Layout = Layout.Column(), Visible = false }, 1 );
		_main.Layout.Spacing = 8;
		BuildTopBar();
		BuildEditor();
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

	/// <summary>Opens (or raises) the tool as a floating window.</summary>
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
		var bar = _main.Layout.AddRow();
		bar.Spacing = 8;
		_modelButton = bar.Add( new TaButton( this, "No character", "view_in_ar", ShowModelMenu, "The model (.vmdl) you're animating - click to switch", 30 ) );
		bar.AddStretchCell();

		_editorActions = bar.Add( new Widget( this ) { Layout = Layout.Row() } );
		_editorActions.Layout.Spacing = 6;
		_undo = _editorActions.Layout.Add( TaStyle.Icon( _editorActions, "undo", Session.UndoEdit, "Undo (Ctrl+Z)", 30 ) );
		_redo = _editorActions.Layout.Add( TaStyle.Icon( _editorActions, "redo", Session.RedoEdit, "Redo (Ctrl+Y)", 30 ) );
		_moreButton = _editorActions.Layout.Add( new TaButton( _editorActions, "More", "more_horiz", ShowMoreMenu, "New, import, replace, export", 30 ) );
		_saveButton = _editorActions.Layout.Add( new Button.Primary( "Save to VMDL" ) { Icon = "save", Tint = TaStyle.Accent, FixedHeight = 30, ToolTip = "Save the open animation into the model (Ctrl+S)" } );
		_saveButton.Clicked = () => _ = Save.SaveAsync( Session.ActiveClip );
	}

	void BuildEditor()
	{
		var body = _main.Layout.AddRow( 1 );
		body.Spacing = 8;

		_clips = body.Add( new ClipListPanel( this, Session ) { FixedWidth = 250 } );
		_clips.NewClip = () => { SetIntent( EditIntent.New ); _editPrompt.FocusPrompt(); };
		_clips.ImportExisting = ImportExisting;
		_clips.SaveClip = c => _ = Save.SaveAsync( c );
		_clips.ExportClip = c => { Session.SelectClip( c ); ExportClip(); };

		var center = body.AddColumn( 1 );
		center.Spacing = 8;
		var viewCard = center.Add( new TaCard( this ), 1 );
		var vh = viewCard.Header( "accessibility_new", "" );
		_clipTitle = vh.Add( new Label( "", viewCard ) );
		_clipTitle.SetStyles( "font-weight: 600;" );
		_clipPill = vh.Add( new TaPill( viewCard, "", TaStyle.Accent ) );
		vh.AddStretchCell();
		vh.Add( TaStyle.Toggle( viewCard, "accessibility", true, on => Viewport.ShowModel = on, "Show the model" ) );
		vh.Add( TaStyle.Toggle( viewCard, "polyline", true, on => Viewport.ShowSkeleton = on, "Show bones (amber = locked)" ) );
		vh.Add( TaStyle.Toggle( viewCard, "route", true, on => Viewport.ShowTrajectory = on, "Show the root path" ) );
		vh.Add( TaStyle.Toggle( viewCard, "grid_on", true, on => Viewport.ShowGround = on, "Show the floor" ) );
		vh.Add( TaStyle.Toggle( viewCard, "videocam", true, on => Viewport.FollowCharacter = on, "Camera follows the character" ) );
		vh.Add( TaStyle.Icon( viewCard, "center_focus_strong", () => Viewport.FrameCharacter(), "Frame the character (double click the view)" ) );
		_viewHost = viewCard.Layout.Add( new Widget( viewCard ) { Layout = Layout.Column() }, 1 );
		Viewport = _viewHost.Layout.Add( new AnimationViewport( _viewHost, Session ), 1 );
		_indicator = _viewHost.Layout.Add( new ProcessingIndicator( _viewHost ), 1 );
		_indicator.Visible = false;

		// the prompt: describe a new animation or a change to the open one
		_editorDownload = center.Add( new DownloadStrip( this, Flow ) );

		// the dock: prompt, transport, timeline
		var timelineCard = center.Add( new TaCard( this ) );
		_editPrompt = timelineCard.Layout.Add( new PromptComposer( timelineCard, "Describe a change…" ) );
		_editPrompt.ShowLength = false;
		_editPrompt.Submitted = prompt => _ = RunEditPromptAsync( prompt );
		_editPrompt.StopRequested = () => Flow.Cancel();
		_editPrompt.HistoryRequested = anchor => new PromptHistoryPopup( this, Session, prompt =>
		{
			_editPrompt.Text = prompt;
			_editPrompt.FocusPrompt();
		} ).OpenAbove( anchor );
		_intentChip = _editPrompt.AddChip( PromptRequests.IntentName( _intent ), "bolt", ShowIntentMenu, "What the prompt does: change this animation, fill between pinned frames, variations, or a new animation" );
		timelineCard.Layout.Add( new TaDivider( timelineCard ) );
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
		transport.Add( TaStyle.Icon( timelineCard, "help_outline", () => { }, "Timeline: drag across the lanes, or click one point and Shift+click another, to highlight frames - then keep, delete, repeat or reverse them with the bar that appears. Drag the highlight edges to adjust; I/O set them at the playhead. Right click to split or trim at a frame. Double click the Pinned lane to pin a pose." ) );
		_timeline = timelineCard.Layout.Add( new TimelineWidget( timelineCard, Session ) { FixedHeight = 84 } );
		_timeline.BuildContextMenu = BuildTimelineMenu;

		// right: the tools, full height
		var side = body.AddColumn();
		side.Spacing = 8;
		_sideTabs = side.Add( new TaTabBar( this, 32 ) { FixedWidth = 380, Stretch = true } );
		_sideTabs.Add( "Edit", "content_cut", "Trim, speed, loop, root motion, clean up" );
		_sideTabs.Add( "Pose", "accessibility_new", "Select bones, pose them, lock them" );
		_sideTabs.Add( "Export", "save", "How the animation is saved into the model" );
		_sideTabs.SelectedChanged = ShowTab;
		var scroll = side.Add( new ScrollArea( this ) { FixedWidth = 380 }, 1 );
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		scroll.SetStyles( "background-color: transparent;" );
		var canvas = new Widget( scroll ) { Layout = Layout.Column() };
		canvas.Layout.Margin = new Sandbox.UI.Margin( 0, 0, 10, 0 );
		canvas.SetStyles( "background-color: transparent;" );
		scroll.Canvas = canvas;
		EditPanel = canvas.Layout.Add( new EditPanel( canvas, Session ) );
		PosePanel = canvas.Layout.Add( new PosePanel( canvas, Session ) );
		SavePanel = canvas.Layout.Add( new SavePanel( canvas, Session, Save ) { ReplaceExisting = ReplaceExisting, ExportFiles = ExportClip } );
		canvas.Layout.AddStretchCell();
		_panels = new Widget[] { EditPanel, PosePanel, SavePanel };
		ShowTab( 0 );
	}

	void BuildStatus()
	{
		_statusStrip = Layout.Add( new Widget( this ) { Layout = Layout.Row() } );
		_statusStrip.Layout.Spacing = 8;
		_statusDot = _statusStrip.Layout.Add( new TaStatusDot( _statusStrip ) );
		_status = _statusStrip.Layout.Add( new Label( "", _statusStrip ) { FixedHeight = 24 }, 1 );
		_cancel = _statusStrip.Layout.Add( new TaButton( _statusStrip, "Cancel", "close", () => Flow.Cancel(), "Stop the download or generation", 24 ) );
		_cancel.Visible = false;
	}

	// ------------------------------------------------------------------ pages

	/// <summary>True while the first page (no model open) is showing.</summary>
	public bool ShowsStartPage => _start.Visible;

	/// <summary>The editor's prompt.</summary>
	public PromptComposer EditPrompt => _editPrompt;

	/// <summary>The timeline under the prompt.</summary>
	public TimelineWidget Timeline => _timeline;

	/// <summary>Opens <paramref name="clip"/> in the editor page.</summary>
	public void OpenEditor( AnimClip clip )
	{
		if ( clip is not null ) Session.SelectClip( clip );
	}

	/// <summary>Editor side tabs: 0 Edit, 1 Pose, 2 Export.</summary>
	public void ShowTab( int index )
	{
		index = Math.Clamp( index, 0, _panels.Length - 1 );
		for ( var i = 0; i < _panels.Length; i++ ) _panels[i].Visible = i == index;
		if ( _sideTabs.SelectedIndex != index ) _sideTabs.SelectedIndex = index;
	}

	// ------------------------------------------------------------------ model

	void ShowModelMenu()
	{
		var menu = new Menu( this );
		menu.AddOption( "Choose VMDL…", "folder_open", PickModel );
		menu.AddOption( "From disk…", "file_open", ChooseFromDisk );
		menu.AddSeparator();
		menu.AddOption( "New from Citizen…", "person_add", () => _ = NewFromStarterAsync( StarterModels.Citizen ) );
		menu.AddOption( "New from Citizen Human…", "person_add", () => _ = NewFromStarterAsync( StarterModels.CitizenHuman ) );
		menu.OpenAtCursor();
	}

	void PickModel()
	{
		var picker = AssetPicker.Create( this, AssetType.Model );
		picker.Window.Title = "Choose the model to animate";
		picker.OnAssetPicked = assets => { if ( assets.FirstOrDefault() is { } asset ) _ = OpenModelAsync( asset ); };
		picker.Show();
	}

	async Task OpenModelPathAsync( string path )
	{
		if ( path.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
		{
			await RunStartingAsync( $"Making a model from {Path.GetFileName( path )} (textures, materials, vmdl)", () => StarterModels.ImportFbxAsync( path ) );
			return;
		}
		if ( AssetSystem.FindByPath( path ) is { } asset ) { await OpenModelAsync( asset ); return; }
		await RunStartingAsync( $"Copying {Path.GetFileName( path )} into the project", () => StarterModels.CopyIntoProjectAsync( path ) );
	}

	/// <summary>Picks a .vmdl anywhere on disk; files outside the project are copied in first.</summary>
	void ChooseFromDisk()
	{
		var path = EditorUtility.OpenFileDialog( "Open model", "Models (*.vmdl *.fbx)", null );
		if ( !string.IsNullOrEmpty( path ) ) _ = OpenModelPathAsync( path );
	}

	/// <summary>Start fresh: copies a stock character's .vmdl into the project, compiles it and opens it.</summary>
	async Task NewFromStarterAsync( StarterModel starter )
	{
		if ( StarterModels.AssetsRoot is null ) { SetStatus( "Open a project first.", Tone.Red ); return; }
		var suggested = StarterModels.DefaultTarget( starter.Title.Replace( ' ', '_' ) );
		var target = EditorUtility.SaveFileDialog( $"New model from {starter.Title}", "vmdl", suggested );
		if ( string.IsNullOrEmpty( target ) ) return;
		if ( !target.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) target += ".vmdl";
		await CreateFromStarterAsync( starter, target );
	}

	/// <summary>Copies <paramref name="starter"/> to <paramref name="target"/> (in the project), compiles and opens it.</summary>
	public async Task CreateFromStarterAsync( StarterModel starter, string target )
	{
		if ( !StarterModels.IsInProject( target ) ) { SetStatus( "Save the new model inside this project's Assets folder.", Tone.Red ); return; }
		await RunStartingAsync( $"Creating {Path.GetFileName( target )} from {starter.Title}", () => StarterModels.CreateFromStarterAsync( starter, target ) );
	}

	/// <summary>Shows the first-load indicator while a model is copied and compiled, then opens it.</summary>
	async Task RunStartingAsync( string message, Func<Task<VmdlCompiler.CompileResult>> work )
	{
		if ( _starting is not null || Session.Busy ) return;
		_starting = message;
		_firstLoad.SetMessage( message + "…" );
		RefreshAll();
		try
		{
			var result = await work();
			await EngineThread.SwitchToMainThread();
			if ( !result.Compiled || result.Asset is null ) { SetStatus( result.Error ?? "The model did not compile.", Tone.Red ); return; }
			_firstLoad.SetMessage( $"Opening {result.Asset.Name}…" );
			await OpenModelAsync( result.Asset );
		}
		catch ( Exception e )
		{
			await EngineThread.SwitchToMainThread();
			SetStatus( e.Message, Tone.Red );
		}
		finally
		{
			await EngineThread.SwitchToMainThread();
			_starting = null;
			RefreshAll();
		}
	}

	/// <summary>Opens a model's workspace. Returns an error message, or null.</summary>
	public async Task<string> OpenModelAsync( Asset asset )
	{
		if ( Session.Busy ) return "Busy - try again in a moment.";
		var error = await Session.OpenModelAsync( asset );
		await EngineThread.SwitchToMainThread();
		if ( error is not null ) { SetStatus( error, Tone.Red ); return error; }
		Viewport.SetModel( Session.Model );
		var warnings = Session.LoadWarnings.Concat( Session.Rig.Problems ).ToList();
		SetIntent( Session.Workspace.Clips.Count == 0 ? EditIntent.New : EditIntent.Change );
		SetStatus( warnings.Count > 0 ? string.Join( " ", warnings )
			: Session.Workspace.Clips.Count == 0 ? $"{asset.Name} is ready. Describe an animation in the prompt, or import one with More > Import existing."
			: $"Opened {asset.Name} with {Session.Workspace.Clips.Count} animations.", warnings.Count > 0 ? Tone.Amber : Tone.Accent );
		RefreshAll();
		return null;
	}

	// ------------------------------------------------------------------ editor prompt

	void ShowIntentMenu()
	{
		var menu = new Menu( this );
		var clip = Session.ActiveClip;
		void Option( EditIntent intent, string tip, bool enabled )
		{
			var o = menu.AddOption( PromptRequests.IntentName( intent ), _intent == intent ? "check" : null, () => SetIntent( intent ) );
			o.Enabled = enabled;
			o.ToolTip = tip;
		}
		Option( EditIntent.Change, "Regenerate the chosen body part from the prompt; the rest keeps its motion", clip is not null );
		Option( EditIntent.FillBetween, clip is null || clip.PinnedFrames.Count < 2 ? "Pin at least two frames on the timeline first" : $"Keep the {clip.PinnedFrames.Count} pinned poses and regenerate the motion between them", clip?.PinnedFrames.Count >= 2 );
		Option( EditIntent.Variations, "New takes of this animation", clip is not null );
		Option( EditIntent.New, "A new animation from the prompt", true );
		menu.OpenAtCursor();
	}

	public void SetIntent( EditIntent intent )
	{
		_intent = intent;
		RefreshEditPrompt();
	}

	void RefreshEditPrompt()
	{
		var clip = Session.ActiveClip;
		var intent = clip is null ? EditIntent.New : _intent;
		var name = clip is null ? "" : clip.Name.Length > 24 ? clip.Name[..23] + "…" : clip.Name;
		_intentChip.Text = intent switch
		{
			EditIntent.Change => $"Change {name}",
			EditIntent.Variations => $"Variations of {name}",
			_ => PromptRequests.IntentName( intent ),
		};
		_editPrompt.ShowLength = intent == EditIntent.New;
		_editPrompt.AllowEmpty = intent is EditIntent.FillBetween or EditIntent.Variations;
		_editPrompt.SetTarget( intent == EditIntent.Change ? clip?.Name : null );
		var scopes = new List<ChangeScope> { ChangeScope.WholeBody };
		if ( Session.Rig?.IsHumanoid == true ) scopes.AddRange( new[] { ChangeScope.UpperBody, ChangeScope.LowerBody, ChangeScope.Arms } );
		scopes.Add( ChangeScope.SelectedBones );
		if ( clip?.LockedBones.Count > 0 ) scopes.Add( ChangeScope.Unlocked );
		_editPrompt.ScopeChoices = scopes;
		_editPrompt.ShowScope = true;
		if ( !scopes.Contains( _editPrompt.Options.Scope ) ) _editPrompt.Options.Scope = ChangeScope.WholeBody;
		_editPrompt.RefreshOptions();
		_editPrompt.Placeholder = intent switch
		{
			EditIntent.FillBetween => "Optional: describe the motion between the pinned poses…",
			EditIntent.Variations => "Optional: steer the variations…",
			EditIntent.New => "Describe a new animation…",
			_ => "Describe a change…",
		};
		string reason = null;
		if ( GeneratorService.Instance.State is not (ModelState.Ready or ModelState.Loading) ) reason = "Download the UniMate model on the Create page first.";
		else if ( !Session.HasModel ) reason = "Choose a character first.";
		_editPrompt.SetDisabledReason( reason );
		_editPrompt.Busy = Flow.Running;
	}

	public async Task RunEditPromptAsync( string prompt )
	{
		if ( Flow.Running || !Session.HasModel ) return;
		var clip = Session.ActiveClip;
		var intent = clip is null ? EditIntent.New : _intent;
		if ( _editPrompt.Options.Scope == ChangeScope.SelectedBones && Session.SelectedBones.Count == 0 && intent == EditIntent.Change )
		{
			SetStatus( "Select the bones to change in the view first (Pose tab).", Tone.Amber );
			return;
		}
		var (request, name) = PromptRequests.Build( Session, prompt, _editPrompt.Options, clip, intent );
		if ( GenerationFlow.Validate( request ) is { } problem ) { SetStatus( problem, Tone.Amber ); return; }
		_editPrompt.Text = "";
		await Flow.GenerateAsync( request, replace: false, name );
	}

	// ------------------------------------------------------------------ editor actions

	void ShowMoreMenu()
	{
		var menu = new Menu( this );
		var clip = Session.ActiveClip;
		menu.AddOption( "New empty animation", "add", () => Session.NewEmptyClip() ).Enabled = Session.HasModel;
		menu.AddOption( "Import existing…", "download", ImportExisting ).Enabled = Session.HasModel;
		menu.AddOption( "Duplicate", "content_copy", () => Session.Duplicate( clip ) ).Enabled = clip is not null;
		menu.AddSeparator();
		menu.AddOption( "Replace existing in model…", "swap_horiz", ReplaceExisting ).Enabled = clip is not null;
		menu.AddOption( "Export .dmx…", "file_download", ExportClip ).Enabled = clip is not null;
		menu.OpenAtCursor();
	}

	void ImportExisting()
	{
		if ( !Session.HasModel ) return;
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
		var rig = Session.Rig;
		if ( Session.Range is { } r )
		{
			menu.AddHeading( $"Frames {r.Start}–{r.End}" );
			menu.AddOption( "Keep only these frames", "crop", () => TimelineWidget.RangeEdit( Session, "Keep selection", ( c, a, b ) => ClipOps.Crop( c, rig, a, b ) ) );
			menu.AddOption( "Delete these frames", "delete", () => TimelineWidget.RangeEdit( Session, "Delete selection", ( c, a, b ) => ClipOps.DeleteSection( c, rig, a, b ) ) );
			menu.AddOption( "Repeat these frames", "repeat", () => TimelineWidget.RangeEdit( Session, "Repeat selection", ( c, a, b ) => ClipOps.DuplicateSection( c, rig, a, b ) ) );
			menu.AddOption( "Reverse these frames", "swap_horiz", () => TimelineWidget.RangeEdit( Session, "Reverse selection", ( c, a, b ) => ClipOps.ReverseSection( c, rig, a, b ) ) );
			menu.AddOption( "Copy to a new animation", "content_copy", () => TimelineWidget.CopyRangeToNewClip( Session ) );
			menu.AddOption( "Clear highlight", "deselect", () => Session.SetRange( null, null ) );
			menu.AddSeparator();
		}
		menu.AddHeading( $"Frame {frame}" );
		var inside = frame > 0 && frame < clip.FrameCount - 1;
		menu.AddOption( "Split here", "call_split", () => SplitAt( frame ) ).Enabled = inside;
		menu.AddOption( "Trim everything before", "first_page", () => Session.Edit( "Trim start", c => ClipOps.Crop( c, rig, frame, c.FrameCount - 1 ) ) ).Enabled = frame > 0;
		menu.AddOption( "Trim everything after", "last_page", () => Session.Edit( "Trim end", c => ClipOps.Crop( c, rig, 0, frame ) ) ).Enabled = frame < clip.FrameCount - 1;
		menu.AddOption( "Set highlight start (I)", "start", () => _timeline.SetInOut( frame, true ) );
		menu.AddOption( "Set highlight end (O)", "keyboard_tab", () => _timeline.SetInOut( frame, false ) );
		menu.AddSeparator();
		menu.AddOption( clip.PinnedFrames.Contains( frame ) ? "Unpin this pose" : "Pin this pose", "push_pin", () => _timeline.TogglePin( frame ) );
		menu.AddOption( "Key selected bones here", "key", () => { Session.Seek( frame ); AddKey(); } );
		menu.AddOption( "Delete keys here", "key_off", () => Session.Edit( $"Delete keys at {frame}", c => c.Keys.RemoveKeysAt( frame ) ) );
		menu.AddSeparator();
		menu.AddOption( "Reverse the whole animation", "swap_horiz", () => Session.Edit( "Reverse", c => ClipOps.Reverse( c, rig ) ) );
		menu.AddOption( "Zoom to fit", "fit_screen", _timeline.ResetZoom );
	}

	/// <summary>Splits the open animation at <paramref name="frame"/>; the second half becomes a new animation.</summary>
	void SplitAt( int frame )
	{
		var clip = Session.ActiveClip;
		if ( clip is null ) return;
		if ( frame <= 0 || frame >= clip.FrameCount - 1 ) { SetStatus( "Split inside the animation, not at its first or last frame.", Tone.Amber ); return; }
		AnimClip second = null;
		if ( Session.Edit( "Split", c => second = ClipOps.Split( c, Session.Rig, frame, c.Name + " (part 2)" ) ) && second is not null )
		{
			Session.AddClip( second, select: false );
			SetStatus( $"Split at frame {frame}: the rest is now \"{second.Name}\".", Tone.Accent );
		}
	}

	void AddKey()
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
		var editor = Session.HasModel;
		var handled = true;
		switch ( e.Key )
		{
			case KeyCode.Space: TogglePlay(); break;
			case KeyCode.Left when editor: Step( -1 ); break;
			case KeyCode.Right when editor: Step( 1 ); break;
			case KeyCode.Home when editor: Session.Seek( 0 ); break;
			case KeyCode.End when editor: Session.Seek( Session.ActiveClip?.FrameCount - 1 ?? 0 ); break;
			case KeyCode.Z when e.HasCtrl && e.HasShift: Session.RedoEdit(); break;
			case KeyCode.Z when e.HasCtrl: Session.UndoEdit(); break;
			case KeyCode.Y when e.HasCtrl: Session.RedoEdit(); break;
			case KeyCode.S when e.HasCtrl: _ = Save.SaveAsync( Session.ActiveClip ); break;
			case KeyCode.D when e.HasCtrl && editor: if ( Session.ActiveClip is { } c ) Session.Duplicate( c ); break;
			case KeyCode.K when editor: AddKey(); break;
			case KeyCode.P when editor: _timeline.TogglePin( Session.CurrentFrame ); break;
			case KeyCode.I when editor: _timeline.SetInOut( Session.CurrentFrame, true ); break;
			case KeyCode.O when editor: _timeline.SetInOut( Session.CurrentFrame, false ); break;
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
		Session.Tick( RealTime.Delta ); // playback (the views only draw it; there can be more than one)
		_indicator?.Tick();
		if ( _firstLoad?.Visible == true ) _firstLoad.Tick();
		if ( Session.Playing ) RefreshTransport();
	}

	void OnSessionChanged( SessionChange change )
	{
		if ( (change & (SessionChange.Model | SessionChange.Busy | SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.ClipList | SessionChange.Undo)) != 0 ) RefreshAll();
		else if ( (change & SessionChange.Playhead) != 0 ) RefreshTransport();
		if ( (change & SessionChange.Model) != 0 && Session.Model is not null )
		{
			Viewport.SetModel( Session.Model );
		}
	}

	void RefreshAll()
	{
		if ( _editPrompt is null ) return;
		var hasModel = Session.HasModel;
		var clip = Session.ActiveClip;
		var busy = Session.Busy;
		var editor = hasModel;
		var starting = _starting is not null || (!hasModel && busy);
		_main.Visible = hasModel;
		_start.Visible = !hasModel && !starting;
		_firstLoad.Visible = !hasModel && starting;
		_firstLoad.Busy = _firstLoad.Visible;

		_modelButton.Text = hasModel ? Session.ModelAsset.Name : "No character";
		_modelButton.ToolTip = hasModel ? $"{Session.ModelAsset.Path} - click to switch" : "Choose the model to animate";
		_modelButton.Enabled = !busy;
		_undo.Enabled = Session.Undo?.CanUndo == true && !busy;
		_redo.Enabled = Session.Undo?.CanRedo == true && !busy;
		_undo.ToolTip = Session.Undo?.UndoLabel is { } u ? $"Undo {u} (Ctrl+Z)" : "Undo (Ctrl+Z)";
		_redo.ToolTip = Session.Undo?.RedoLabel is { } r ? $"Redo {r} (Ctrl+Y)" : "Redo (Ctrl+Y)";
		_saveButton.Enabled = clip is not null && !busy;
		_moreButton.Enabled = hasModel && !busy;

		// editor view
		var showIndicator = busy && Flow.Running && editor;
		_indicator.Visible = showIndicator;
		_indicator.Busy = showIndicator;
		if ( showIndicator && _lastProgress.Length == 0 ) _indicator.SetMessage( Session.BusyText + "…" );
		if ( !busy ) _lastProgress = "";
		Viewport.Visible = hasModel && !showIndicator;
		_cancel.Visible = Flow.Running;
		_clipTitle.Text = clip?.Name ?? (hasModel ? "No animation open" : "");
		var pill = clip is null ? "" : clip.Origin switch
		{
			ClipOrigin.Generated => "GENERATED",
			ClipOrigin.Imported or ClipOrigin.ImportedFile => "IMPORTED",
			ClipOrigin.Duplicated => "COPY",
			_ => "NEW",
		};
		_clipPill.Set( pill, TaStyle.Accent );
		RefreshEditPrompt();

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
