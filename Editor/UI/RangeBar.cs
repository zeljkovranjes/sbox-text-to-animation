using System;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// The actions for the highlighted frames, shown in the transport row while a highlight exists (so it never
/// covers the lanes): which frames, then keep, delete, repeat, reverse, copy to a new animation, clear.
/// </summary>
public sealed class RangeBar : Widget
{
	readonly EditorSession _session;
	readonly Label _label;
	readonly System.Collections.Generic.List<(TaButton Button, string Text)> _buttons = new();
	bool _compact;

	public RangeBar( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		Layout = Layout.Row();
		Layout.Spacing = 4;
		FixedHeight = TaStyle.ControlHeight;
		_label = Layout.Add( new Label( "", this ) { ToolTip = "The highlighted frames" } );
		_label.SetStyles( $"color: {TaStyle.AccentLight.Hex}; font-weight: 600;" );
		Layout.AddSpacingCell( 4 );
		void Add( string text, string icon, Action action, string tip )
		{
			var button = Layout.Add( new TaButton( this, text, icon, action, text.Length > 0 ? $"{text}: {tip}" : tip ) );
			_buttons.Add( (button, text) );
		}
		Add( "Keep", "crop", () => TimelineWidget.RangeEdit( session, "Keep selection", ( c, a, b ) => ClipOps.Crop( c, session.Rig, a, b ) ), "Keep only the highlighted frames" );
		Add( "Delete", "delete", () => TimelineWidget.RangeEdit( session, "Delete selection", ( c, a, b ) => ClipOps.DeleteSection( c, session.Rig, a, b ) ), "Cut the highlighted frames out (the motion joins up)" );
		Add( "Repeat", "repeat", () => TimelineWidget.RangeEdit( session, "Repeat selection", ( c, a, b ) => ClipOps.DuplicateSection( c, session.Rig, a, b ) ), "Play the highlighted frames twice" );
		Add( "Reverse", "swap_horiz", () => TimelineWidget.RangeEdit( session, "Reverse selection", ( c, a, b ) => ClipOps.ReverseSection( c, session.Rig, a, b ) ), "Play the highlighted frames backwards" );
		Add( "", "content_copy", () => TimelineWidget.CopyRangeToNewClip( session ), "Copy the highlighted frames into a new animation" );
		Add( "", "close", () => session.SetRange( null, null ), "Clear the highlight (Esc)" );
		Visible = false;
	}

	/// <summary>Width with every button labelled (estimated the way the buttons size themselves).</summary>
	public float FullWidth
	{
		get
		{
			var width = 8f + 6.2f * "000–000 · 0.00 s".Length;
			foreach ( var (_, text) in _buttons ) width += 4 + (text.Length == 0 ? TaStyle.ControlHeight : 20 + 21 + 6.2f * text.Length);
			return width;
		}
	}

	/// <summary>Icons only (names in the tooltips), for when the row is too narrow for the labels.</summary>
	public bool Compact
	{
		get => _compact;
		set
		{
			if ( _compact == value ) return;
			_compact = value;
			foreach ( var (button, text) in _buttons ) button.Text = value ? "" : text;
		}
	}

	/// <summary>Shows the bar for the current highlight, or hides it when there is none.</summary>
	public void Refresh()
	{
		var clip = _session.ActiveClip;
		if ( _session.Range is not { } r || clip is null ) { Visible = false; return; }
		var frames = r.End - r.Start + 1;
		_label.Text = _compact ? $"{r.Start}–{r.End}" : $"{r.Start}–{r.End} · {frames / clip.Fps:0.00} s";
		Visible = true;
	}
}
