using System;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;

namespace TextToAnimation.EditorTools.UI;

/// <summary>Right-panel "Save" tab: how the clip is written into the model, and the save/replace/export actions.</summary>
public sealed class SavePanel : Widget
{
	readonly EditorSession _session;
	readonly SaveFlow _save;
	readonly LineEdit _sequence;
	readonly ComboBox _root;
	readonly Checkbox _footsteps, _additive, _mirrored;
	readonly LineEdit _additiveFrame;
	readonly Label _summary;
	readonly Button _saveButton;
	bool _refreshing;

	public Action ReplaceExisting { get; set; }
	public Action ExportFiles { get; set; }

	public SavePanel( Widget parent, EditorSession session, SaveFlow save ) : base( parent )
	{
		_session = session;
		_save = save;
		Layout = Layout.Column();
		Layout.Spacing = 8;

		var card = Layout.Add( new TaFold( this, "save", "Save to model", open: true, key: "save.card" ) );
		var seqRow = TaStyle.FieldRow( card, card.Content, "Sequence", 80f, "The animation's name inside the model (what the animgraph and code use)" );
		_sequence = seqRow.Add( TaStyle.Field( new LineEdit( card ) ), 1 );
		_sequence.EditingFinished += () => Change( "Sequence name", c => c.Export.SequenceName = AnimClip.SanitizeSequenceName( _sequence.Text ) );

		var rootRow = TaStyle.FieldRow( card, card.Content, "Root motion", 80f, "How travel is stored in the saved sequence" );
		_root = rootRow.Add( TaStyle.Field( new ComboBox( card ) ), 1 );
		_root.AddItem( "Keep (moves in the animation)", "open_with", () => Change( "Root motion: keep", c => c.Export.RootMotion = ClipRootMotion.Keep ) );
		_root.AddItem( "Extract (drives the character)", "moving", () => Change( "Root motion: extract", c => c.Export.RootMotion = ClipRootMotion.Extract ) );
		_root.AddItem( "In place (no travel)", "my_location", () => Change( "Root motion: in place", c => c.Export.RootMotion = ClipRootMotion.InPlace ) );

		var extras = card.Content.AddColumn();
		extras.Spacing = 4;
		_footsteps = TaStyle.Check( extras, "Footstep events", true, v => Change( "Footsteps", c => c.Export.Footsteps = v ), "Save AE_FOOTSTEP events (footstep sounds)" );
		var addRow = extras.AddRow();
		addRow.Spacing = 6;
		_additive = TaStyle.Check( addRow, "Additive copy, reference frame", false, v => Change( "Additive copy", c => c.Export.AdditiveVariant = v ), "Also save <name>_delta: the motion relative to one frame, for layering on other animations" );
		_additiveFrame = addRow.Add( TaStyle.Framed( new LineEdit( card ) { Text = "0", FixedWidth = 44 } ) );
		_additiveFrame.EditingFinished += () => { if ( int.TryParse( _additiveFrame.Text, out var f ) ) Change( "Additive reference", c => c.Export.AdditiveReferenceFrame = Math.Max( 0, f ) ); };
		addRow.AddStretchCell();
		_mirrored = TaStyle.Check( extras, "Mirrored copy (left/right swapped)", false, v => Change( "Mirrored copy", c => c.Export.MirroredVariant = v ), "Also save <name>_mirror" );

		_summary = card.Content.Add( TaStyle.Muted( new Label( "", card ) { WordWrap = true }, small: true ) );
		var buttons = card.Content.AddRow();
		buttons.Spacing = 6;
		buttons.Add( new TaButton( card, "Replace…", "swap_horiz", () => ReplaceExisting?.Invoke(), "Overwrite one of the model's existing animations with this one", 30 ) );
		buttons.Add( new TaButton( card, "Export…", "file_download", () => ExportFiles?.Invoke(), "Write the animation as a .dmx file (and a ready-to-use .vmdl)", 30 ) );
		_saveButton = buttons.Add( new Button.Primary( "Save to VMDL" ) { Icon = "save", Tint = TaStyle.Accent, FixedHeight = 30 }, 1 );
		_saveButton.Clicked = () => _ = _save.SaveAsync( _session.ActiveClip );

		Layout.AddStretchCell();
		_session.Changed += c => { if ( (c & (SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.Model | SessionChange.Busy | SessionChange.ClipList)) != 0 ) Refresh(); };
		Refresh();
	}

	void Change( string label, Action<AnimClip> change )
	{
		if ( _refreshing || _session.ActiveClip is null ) return;
		change( _session.ActiveClip );
		_session.ScheduleSave( _session.ActiveClip );
		Refresh();
	}

	public void Refresh()
	{
		var clip = _session.ActiveClip;
		Enabled = clip is not null && !_session.Busy;
		if ( clip is null ) { _summary.Text = "Open an animation to save it."; return; }
		_refreshing = true;
		try
		{
			if ( !_sequence.IsFocused ) _sequence.Text = clip.EffectiveSequenceName;
			_root.CurrentIndex = (int)clip.Export.RootMotion;
			_footsteps.Value = clip.Export.Footsteps;
			_additive.Value = clip.Export.AdditiveVariant;
			_additiveFrame.Text = clip.Export.AdditiveReferenceFrame.ToString();
			_mirrored.Value = clip.Export.MirroredVariant;
		}
		finally { _refreshing = false; }
		var (sequence, updates, problem) = _save.Plan( clip );
		_summary.Text = problem ?? (updates
			? $"Updates \"{sequence}\", which this animation saved before."
			: sequence != clip.EffectiveSequenceName
				? $"The model already has \"{clip.EffectiveSequenceName}\", so this is saved as \"{sequence}\" (use Replace… to overwrite)."
				: $"Adds \"{sequence}\" to {System.IO.Path.GetFileName( _session.ModelAsset?.Path )}. Your model's other animations are untouched; a backup is kept.");
	}
}
