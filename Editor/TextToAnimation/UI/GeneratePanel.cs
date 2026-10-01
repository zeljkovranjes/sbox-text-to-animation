using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Editor.Session;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// Right-panel "Generate" tab: describe a motion and generate it, or regenerate/edit/vary the open clip.
/// The model download card appears here until UniMate is installed.
/// </summary>
public sealed class GeneratePanel : Widget
{
	readonly EditorSession _session;
	readonly GenerationFlow _flow;

	readonly TaCard _modelCard;
	readonly Label _modelText;
	readonly Button _downloadButton;

	readonly SegmentedControl _mode;
	readonly Label _modeHint;
	readonly Label _promptCaption;
	readonly TextEdit _prompt;
	readonly Widget _stepsHost;
	readonly TaButton _addStep;
	readonly List<LineEdit> _stepEdits = new();
	readonly Widget _pinsRow;
	readonly Label _pinsLabel;
	readonly Widget _locksRow;
	readonly Label _locksLabel;
	readonly Widget _strengthRow;
	readonly FloatSlider _strength;
	readonly Widget _durationRow;
	readonly FloatSlider _duration;
	readonly Label _durationLabel;
	readonly ComboBox _takes;
	int _takeCount = 1;
	readonly ComboBox _quality;
	readonly LineEdit _seed;
	readonly FloatSlider _guidance;
	readonly Label _guidanceLabel;
	readonly Button _generate;
	readonly TaButton _regenerate;
	int _steps = 24;

	static readonly string[] ModeNames = { "Text", "Steps", "In-between", "Edit", "Vary" };
	static readonly GenerationMode[] Modes = { GenerationMode.TextToMotion, GenerationMode.Expansion, GenerationMode.InBetween, GenerationMode.TextEdit, GenerationMode.Variation };
	static readonly string[] Hints =
	{
		"Describe what the character does. Use \"then\" for a sequence: the parts flow into each other.",
		"One line per step. Each step is about two seconds and blends into the next.",
		"Pin the poses to keep on the timeline (double click the Pinned lane). UniMate fills the motion between them.",
		"Lock the bones to keep (Pose tab or below), describe the new motion, and the rest of the body is regenerated.",
		"New takes of the open animation. Low strength stays close to it; high strength only keeps the idea.",
	};

	public GeneratePanel( Widget parent, EditorSession session, GenerationFlow flow ) : base( parent )
	{
		_session = session;
		_flow = flow;
		Layout = Layout.Column();
		Layout.Spacing = 8;

		// ---- model download card (hidden once installed)
		_modelCard = Layout.Add( new TaCard( this ) );
		var mh = _modelCard.Header( "download", "UniMate model" );
		mh.AddStretchCell();
		_modelText = _modelCard.Layout.Add( TaStyle.Muted( new Label( "", _modelCard ) { WordWrap = true }, small: true ) );
		_downloadButton = _modelCard.Layout.Add( new Button.Primary( "Download" ) { Icon = "download", Tint = TaStyle.Accent, FixedHeight = 28 } );
		_downloadButton.Clicked = () => _ = _flow.DownloadAsync();

		// ---- generate card
		var card = Layout.Add( new TaCard( this ) );
		var header = card.Header( "auto_awesome", "Generate" );
		header.AddStretchCell();
		_mode = card.Layout.Add( new SegmentedControl( card ) { FixedHeight = 28 } );
		_mode.AddOption( "Text", "notes" );
		_mode.AddOption( "Steps", "format_list_numbered" );
		_mode.AddOption( "Between", "timeline" );
		_mode.AddOption( "Edit", "brush" );
		_mode.AddOption( "Vary", "shuffle" );
		_mode.OnSelectedChanged = _ => RefreshMode();
		_modeHint = card.Layout.Add( TaStyle.Muted( new Label( "", card ) { WordWrap = true }, small: true ) );

		_promptCaption = card.Layout.Add( new Label( "Describe the motion", card ) );
		_promptCaption.SetStyles( "font-weight: 600;" );
		_prompt = card.Layout.Add( new TextEdit( card ) { FixedHeight = 74 } );
		_prompt.SetStyles( $"background-color: {Theme.WindowBackground.Hex}; border: 1px solid {TaStyle.InputEdge.Hex}; border-radius: 4px;" );
		_prompt.PlainText = "Walk cautiously forward, look behind, then run.";

		// steps list
		_stepsHost = card.Layout.Add( new Widget( card ) { Layout = Layout.Column() } );
		_stepsHost.Layout.Spacing = 4;
		_addStep = card.Layout.Add( new TaButton( card, "Add step", "add", () => { AddStep( "" ); }, "Add another motion step" ) );
		_stepsHost.Visible = false;
		_addStep.Visible = false;
		foreach ( var s in new[] { "Walk cautiously forward", "Look behind", "Run forward" } ) AddStep( s );

		// pins / locks / strength rows
		_pinsRow = card.Layout.Add( new Widget( card ) { Layout = Layout.Row() } );
		_pinsRow.Layout.Spacing = 6;
		_pinsLabel = _pinsRow.Layout.Add( TaStyle.Muted( new Label( "", _pinsRow ) ), 1 );
		_pinsRow.Layout.Add( new TaButton( _pinsRow, "Pin frame", "push_pin", () => TogglePin(), "Pin (or unpin) the current frame's pose" ) );

		_locksRow = card.Layout.Add( new Widget( card ) { Layout = Layout.Column() } );
		_locksRow.Layout.Spacing = 4;
		_locksLabel = _locksRow.Layout.Add( TaStyle.Muted( new Label( "", _locksRow ) { WordWrap = true }, small: true ) );
		var lockButtons = _locksRow.Layout.AddRow();
		lockButtons.Spacing = 4;
		lockButtons.Add( new TaButton( _locksRow, "Lock", "lock", () => BoneLocks.LockSelected( _session ), "Keep the selected bones' motion" ) );
		lockButtons.Add( new TaButton( _locksRow, "Hierarchy", "account_tree", () => BoneLocks.LockHierarchy( _session ), "Keep the selected bones and everything under them" ) );
		lockButtons.Add( new TaButton( _locksRow, "All except", "flip", () => BoneLocks.LockAllExcept( _session ), "Keep everything except the selected bones" ) );
		lockButtons.Add( TaStyle.Icon( _locksRow, "lock_open", () => BoneLocks.Unlock( _session ), "Unlock the selected bones (all bones when nothing is selected)" ) );

		_strengthRow = card.Layout.Add( new Widget( card ) { Layout = Layout.Row() } );
		_strengthRow.Layout.Spacing = 6;
		_strengthRow.Layout.Add( TaStyle.Muted( new Label( "Strength", _strengthRow ) { FixedWidth = 70 } ) );
		_strength = _strengthRow.Layout.Add( new FloatSlider( _strengthRow ) { Minimum = 0.1f, Maximum = 0.95f, Value = 0.5f }, 1 );

		// common settings
		_durationRow = card.Layout.Add( new Widget( card ) { Layout = Layout.Row() } );
		_durationRow.Layout.Spacing = 6;
		_durationRow.Layout.Add( TaStyle.Muted( new Label( "Length", _durationRow ) { FixedWidth = 70 } ) );
		_duration = _durationRow.Layout.Add( new FloatSlider( _durationRow ) { Minimum = 1f, Maximum = 12f, Value = 4f }, 1 );
		_durationLabel = _durationRow.Layout.Add( TaStyle.Muted( new Label( "4.0 s", _durationRow ) { FixedWidth = 44 } ) );
		_duration.OnValueEdited = () => _durationLabel.Text = $"{_duration.Value:0.0} s";

		var takesRow = TaStyle.FieldRow( card, card.Layout, "Takes", 70f, "How many different versions to generate" );
		_takes = takesRow.Add( TaStyle.Field( new ComboBox( card ) ), 1 );
		for ( var n = 1; n <= 4; n++ )
		{
			var count = n;
			_takes.AddItem( count == 1 ? "1 take" : $"{count} takes", null, () => _takeCount = count, selected: count == 1 );
		}

		var qualityRow = TaStyle.FieldRow( card, card.Layout, "Quality", 70f, "More steps = smoother, more accurate motion, but slower" );
		_quality = qualityRow.Add( TaStyle.Field( new ComboBox( card ) ), 1 );
		_quality.AddItem( "Fast (12 steps)", "bolt", () => _steps = 12 );
		_quality.AddItem( "Standard (24 steps)", "tune", () => _steps = 24, selected: true );
		_quality.AddItem( "Best (40 steps)", "diamond", () => _steps = 40 );

		var seedRow = TaStyle.FieldRow( card, card.Layout, "Seed", 70f, "Same seed + same settings = same result" );
		_seed = seedRow.Add( TaStyle.Field( new LineEdit( card ) { Text = Random.Shared.Next( 1, 99999 ).ToString() } ), 1 );
		seedRow.Add( TaStyle.Icon( card, "casino", () => _seed.Text = Random.Shared.Next( 1, 99999 ).ToString(), "New random seed", 28 ) );

		var guidanceRow = TaStyle.FieldRow( card, card.Layout, "Guidance", 70f, "How strictly the motion follows the text" );
		_guidance = guidanceRow.Add( new FloatSlider( card ) { Minimum = 1.5f, Maximum = 6f, Value = 3f }, 1 );
		_guidanceLabel = guidanceRow.Add( TaStyle.Muted( new Label( "3.0", card ) { FixedWidth = 30 } ) );
		_guidance.OnValueEdited = () => _guidanceLabel.Text = $"{_guidance.Value:0.0}";

		var buttons = card.Layout.AddRow();
		buttons.Spacing = 8;
		_regenerate = buttons.Add( new TaButton( card, "Regenerate", "refresh", () => Run( replace: true ), "Replace the open animation with a new result (undo restores it)", 30 ) );
		_generate = buttons.Add( new Button.Primary( "Generate" ) { Icon = "auto_awesome", Tint = TaStyle.Accent, FixedHeight = 30 }, 1 );
		_generate.Clicked = () => Run( replace: false );

		Layout.AddStretchCell();

		_session.Changed += c => { if ( (c & (SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.Selection | SessionChange.Busy | SessionChange.Model)) != 0 ) Refresh(); };
		GeneratorService.Instance.StateChanged += () => MainThread.Queue( Refresh );
		RefreshMode();
	}

	GenerationMode Mode => Modes[Math.Clamp( _mode.SelectedIndex, 0, Modes.Length - 1 )];

	void AddStep( string text )
	{
		var row = new Widget( _stepsHost ) { Layout = Layout.Row() };
		row.Layout.Spacing = 4;
		var edit = row.Layout.Add( TaStyle.Field( new LineEdit( row ) { Text = text, PlaceholderText = "e.g. jump over something" } ), 1 );
		row.Layout.Add( TaStyle.Icon( row, "close", () => { _stepEdits.Remove( edit ); row.Destroy(); }, "Remove this step", 28 ) );
		_stepEdits.Add( edit );
		_stepsHost.Layout.Add( row );
	}

	void TogglePin()
	{
		var frame = _session.CurrentFrame;
		_session.Edit( _session.ActiveClip?.PinnedFrames.Contains( frame ) == true ? $"Unpin frame {frame}" : $"Pin frame {frame}",
			c => { if ( !c.PinnedFrames.Remove( frame ) ) c.PinnedFrames.Add( frame ); } );
	}

	void RefreshMode()
	{
		var mode = Mode;
		_modeHint.Text = Hints[Array.IndexOf( Modes, mode )];
		var steps = mode == GenerationMode.Expansion;
		_prompt.Visible = !steps;
		_promptCaption.Visible = !steps;
		_promptCaption.Text = mode switch
		{
			GenerationMode.InBetween => "Describe the motion (optional)",
			GenerationMode.TextEdit => "Describe the new motion",
			GenerationMode.Variation => "Describe the motion (optional)",
			_ => "Describe the motion",
		};
		_stepsHost.Visible = steps;
		_addStep.Visible = steps;
		_pinsRow.Visible = mode == GenerationMode.InBetween;
		_locksRow.Visible = mode == GenerationMode.TextEdit;
		_strengthRow.Visible = mode == GenerationMode.Variation;
		_durationRow.Visible = mode is GenerationMode.TextToMotion;
		Refresh();
	}

	public void Refresh()
	{
		var service = GeneratorService.Instance;
		var state = service.State;
		_modelCard.Visible = state != ModelState.Ready;
		var size = service.Backend.DownloadBytes;
		_modelText.Text = state switch
		{
			ModelState.Downloading => "Downloading… you can keep working while it downloads.",
			ModelState.Incomplete => $"The download didn't finish. Resume it ({size / 1e6:0} MB in total) - nothing already downloaded is fetched again.",
			ModelState.Failed => $"Something went wrong: {service.LastError}",
			ModelState.Loading => "Loading the model…",
			_ => $"{service.Backend.Name} runs on your computer inside the editor. It needs a one-time download of {size / 1e6:0} MB, shared by all your projects.",
		};
		_downloadButton.Text = state == ModelState.Incomplete ? "Resume download" : state == ModelState.Failed ? "Try again" : $"Download ({size / 1e6:0} MB)";
		_downloadButton.Enabled = state is ModelState.NotInstalled or ModelState.Incomplete or ModelState.Failed && !_session.Busy;

		var clip = _session.ActiveClip;
		var mode = Mode;
		var ready = state == ModelState.Ready && _session.HasModel && !_session.Busy;
		var needsClip = mode is GenerationMode.InBetween or GenerationMode.TextEdit or GenerationMode.Variation;
		_generate.Enabled = ready && (!needsClip || clip is not null);
		_regenerate.Enabled = ready && clip is not null;
		_generate.ToolTip = state != ModelState.Ready ? "Download the model first" : needsClip && clip is null ? "Open an animation first" : "Create new animation(s) - the open one is kept";

		if ( clip is not null )
		{
			_pinsLabel.Text = clip.PinnedFrames.Count switch { 0 => "No frames pinned", 1 => "1 frame pinned", var n => $"{n} frames pinned" };
			_locksLabel.Text = clip.LockedBones.Count == 0
				? "No bones locked: select bones in the view and lock them to keep their motion."
				: $"{clip.LockedBones.Count} bones locked (shown in amber) - their motion is kept.";
		}
	}

	GenerationRequest BuildRequest()
	{
		var clip = _session.ActiveClip;
		var mode = Mode;
		var prompts = mode == GenerationMode.Expansion
			? _stepEdits.Select( e => e.Text.Trim() ).Where( t => t.Length > 0 ).ToList()
			: new List<string> { _prompt.PlainText.Trim() };
		if ( mode is GenerationMode.Variation or GenerationMode.InBetween && string.IsNullOrWhiteSpace( prompts[0] ) && clip?.Generation?.Prompts.FirstOrDefault() is { } original )
			prompts[0] = original;
		var frames = clip is not null && mode is GenerationMode.InBetween or GenerationMode.TextEdit or GenerationMode.Variation ? _session.ActiveFrames : null;
		var keepBones = clip is null ? Array.Empty<int>() : clip.LockedBones.Select( _session.Rig.Skeleton.IndexOf ).Where( i => i >= 0 ).ToArray();
		return new GenerationRequest
		{
			Mode = mode,
			Prompts = prompts,
			DurationSeconds = mode == GenerationMode.TextToMotion ? _duration.Value : 0,
			OutputFps = clip?.Fps ?? _session.Workspace?.DefaultFps ?? 30f,
			Seed = int.TryParse( _seed.Text, out var seed ) ? seed : Random.Shared.Next(),
			Count = _takeCount,
			Guidance = _guidance.Value,
			Steps = _steps,
			SourceFrames = frames,
			SourceFps = clip?.Fps ?? 30f,
			KeepFrames = clip?.PinnedFrames.ToList() ?? new List<int>(),
			KeepBones = keepBones,
			VariationStrength = _strength.Value,
		};
	}

	void Run( bool replace )
	{
		var request = BuildRequest();
		if ( request.Mode is GenerationMode.TextToMotion or GenerationMode.TextEdit && string.IsNullOrWhiteSpace( request.Prompts.FirstOrDefault() ) )
		{
			_session.SetStatus( "Describe the motion first.", Tone.Amber );
			return;
		}
		if ( request.Mode == GenerationMode.Expansion && request.Prompts.Count == 0 )
		{
			_session.SetStatus( "Add at least one step.", Tone.Amber );
			return;
		}
		if ( request.Mode == GenerationMode.InBetween && request.KeepFrames.Count < 2 )
		{
			_session.SetStatus( "Pin at least two frames on the timeline first (double click the Pinned lane).", Tone.Amber );
			return;
		}
		var name = request.Mode switch
		{
			GenerationMode.Expansion => GenerationFlow.NameFromPrompt( string.Join( ", ", request.Prompts.Take( 2 ) ) ),
			GenerationMode.TextToMotion => GenerationFlow.NameFromPrompt( request.Prompts[0] ),
			_ => $"{_session.ActiveClip?.Name ?? "Animation"} ({ModeNames[Array.IndexOf( Modes, request.Mode )].ToLowerInvariant()})",
		};
		_ = _flow.GenerateAsync( request, replace, name );
		if ( int.TryParse( _seed.Text, out var s ) ) _seed.Text = (s + 1).ToString(); // next press gives a new take
	}
}
