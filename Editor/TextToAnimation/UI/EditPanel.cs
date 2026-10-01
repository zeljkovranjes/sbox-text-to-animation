using System;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>Right-panel "Edit" tab: clip settings, cutting, speed, root motion, looping, clean-up and quality.</summary>
public sealed class EditPanel : Widget
{
	readonly EditorSession _session;
	readonly LineEdit _name;
	readonly Label _info;
	readonly Checkbox _looping;
	readonly ComboBox _fps;
	float _fpsShown = -1f;
	readonly FloatSlider _speed;
	readonly Label _speedLabel;
	readonly Label _rangeLabel;
	readonly Widget _rangeButtons;
	readonly LineEdit _offsetForward, _offsetSide, _turn;
	readonly Checkbox _progressive;
	readonly FloatSlider _blend;
	readonly Label _blendLabel;
	readonly Widget _issues;
	bool _refreshing;

	public EditPanel( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		Layout = Layout.Column();
		Layout.Spacing = 8;

		// ---- clip
		var clipCard = Layout.Add( new TaCard( this ) );
		clipCard.Header( "movie", "Animation" ).AddStretchCell();
		var nameRow = TaStyle.FieldRow( clipCard, clipCard.Layout, "Name", 70f );
		_name = nameRow.Add( TaStyle.Field( new LineEdit( clipCard ) ), 1 );
		_name.EditingFinished += () => { if ( _session.ActiveClip is { } c && _name.Text.Trim() != c.Name ) _session.Rename( c, _name.Text ); };
		_info = clipCard.Layout.Add( TaStyle.Muted( new Label( "", clipCard ), small: true ) );
		var loopRow = clipCard.Layout.AddRow();
		loopRow.Spacing = 8;
		_looping = TaStyle.Check( loopRow, "Looping", false, v => { if ( !_refreshing ) _session.Edit( v ? "Loop on" : "Loop off", c => c.Looping = v ); }, "Plays on repeat in game" );
		loopRow.AddStretchCell();
		var fpsRow = TaStyle.FieldRow( clipCard, clipCard.Layout, "Frame rate", 70f, "Resample the animation (the duration stays the same)" );
		_fps = fpsRow.Add( TaStyle.Field( new ComboBox( clipCard ) ), 1 );
		var speedRow = TaStyle.FieldRow( clipCard, clipCard.Layout, "Speed", 70f, "Make the motion faster (>1) or slower (<1)" );
		_speed = speedRow.Add( new FloatSlider( clipCard ) { Minimum = 0.25f, Maximum = 3f, Value = 1f }, 1 );
		_speedLabel = speedRow.Add( TaStyle.Muted( new Label( "1.00x", clipCard ) { FixedWidth = 40 } ) );
		_speed.OnValueEdited = () => _speedLabel.Text = $"{_speed.Value:0.00}x";
		speedRow.Add( new TaButton( clipCard, "Apply", null, () =>
		{
			var factor = _speed.Value;
			if ( MathF.Abs( factor - 1f ) > 0.01f ) _session.Edit( $"Speed x{factor:0.00}", c => ClipOps.TimeScale( c, _session.Rig, factor ) );
			_speed.Value = 1f; _speedLabel.Text = "1.00x";
		}, "Apply the speed change" ) );

		// ---- cut
		var cutCard = Layout.Add( new TaCard( this ) );
		cutCard.Header( "content_cut", "Cut" ).AddStretchCell();
		_rangeLabel = cutCard.Layout.Add( TaStyle.Muted( new Label( "", cutCard ) { WordWrap = true }, small: true ) );
		_rangeButtons = cutCard.Layout.Add( new Widget( cutCard ) { Layout = Layout.Row() } );
		_rangeButtons.Layout.Spacing = 4;
		_rangeButtons.Layout.Add( new TaButton( _rangeButtons, "Keep", "crop", () => RangeEdit( "Trim to selection", ( c, a, b ) => ClipOps.Crop( c, _session.Rig, a, b ) ), "Trim the clip to the selected range" ) );
		_rangeButtons.Layout.Add( new TaButton( _rangeButtons, "Delete", "delete", () => RangeEdit( "Delete section", ( c, a, b ) => ClipOps.DeleteSection( c, _session.Rig, a, b ) ), "Remove the selected range (the motion joins up)" ) );
		_rangeButtons.Layout.Add( new TaButton( _rangeButtons, "Repeat", "content_copy", () => RangeEdit( "Duplicate section", ( c, a, b ) => ClipOps.DuplicateSection( c, _session.Rig, a, b ) ), "Insert a copy of the selected range after it" ) );
		var cutRow = cutCard.Layout.AddRow();
		cutRow.Spacing = 4;
		cutRow.Add( new TaButton( cutCard, "Split here", "call_split", Split, "Split into two animations at the playhead" ), 1 );
		cutRow.Add( new TaButton( cutCard, "Reverse", "swap_horiz", () => _session.Edit( "Reverse", c => ClipOps.Reverse( c, _session.Rig ) ), "Play the motion backwards" ), 1 );
		var trimRow = cutCard.Layout.AddRow();
		trimRow.Spacing = 4;
		trimRow.Add( new TaButton( cutCard, "Trim start", "first_page", () => _session.Edit( "Trim start", c => ClipOps.Crop( c, _session.Rig, _session.CurrentFrame, c.FrameCount - 1 ) ), "Remove everything before the playhead" ), 1 );
		trimRow.Add( new TaButton( cutCard, "Trim end", "last_page", () => _session.Edit( "Trim end", c => ClipOps.Crop( c, _session.Rig, 0, _session.CurrentFrame ) ), "Remove everything after the playhead" ), 1 );

		// ---- root motion
		var rootCard = Layout.Add( new TaCard( this ) );
		rootCard.Header( "route", "Root motion" ).AddStretchCell();
		var rootRow = rootCard.Layout.AddRow();
		rootRow.Spacing = 4;
		rootRow.Add( new TaButton( rootCard, "In place", "my_location", () => _session.Edit( "Make in place", c => ClipOps.MakeInPlace( c, _session.Rig ) ), "Remove travel: the character stays on the spot" ), 1 );
		rootRow.Add( new TaButton( rootCard, "Remove drift", "near_me_disabled", () => _session.Edit( "Remove root drift", c => ClipOps.RemoveRootDrift( c, _session.Rig ) ), "End where it started, facing the same way (idles, loops)" ), 1 );
		rootRow = rootCard.Layout.AddRow();
		rootRow.Spacing = 4;
		rootRow.Add( new TaButton( rootCard, "Reset start", "restart_alt", () => _session.Edit( "Reset start", c => ClipOps.ResetStart( c, _session.Rig ) ), "Start at the model's origin facing forward" ), 1 );
		var offRow = rootCard.Layout.AddRow();
		offRow.Spacing = 4;
		offRow.Add( TaStyle.Muted( new Label( "Move", rootCard ) { FixedWidth = 40 } ) );
		_offsetForward = offRow.Add( TaStyle.Field( new LineEdit( rootCard ) { PlaceholderText = "forward", ToolTip = "Forward (inches)" } ), 1 );
		_offsetSide = offRow.Add( TaStyle.Field( new LineEdit( rootCard ) { PlaceholderText = "left", ToolTip = "Left (inches)" } ), 1 );
		_turn = offRow.Add( TaStyle.Field( new LineEdit( rootCard ) { PlaceholderText = "turn °", ToolTip = "Turn (degrees, counter-clockwise)" } ), 1 );
		var applyRow = rootCard.Layout.AddRow();
		applyRow.Spacing = 8;
		_progressive = TaStyle.Check( applyRow, "Gradually (bend the path)", false, _ => { }, "Grow the offset from nothing at the first frame to the full amount at the last" );
		applyRow.AddStretchCell();
		applyRow.Add( new TaButton( rootCard, "Apply", null, ApplyOffset, "Move/turn the root" ) );

		// ---- loop
		var loopCard = Layout.Add( new TaCard( this ) );
		loopCard.Header( "all_inclusive", "Seamless loop" ).AddStretchCell();
		var blendRow = TaStyle.FieldRow( loopCard, loopCard.Layout, "Blend", 70f, "How much of the end is blended into the start" );
		_blend = blendRow.Add( new FloatSlider( loopCard ) { Minimum = 0.05f, Maximum = 1f, Value = 0.25f }, 1 );
		_blendLabel = blendRow.Add( TaStyle.Muted( new Label( "0.25 s", loopCard ) { FixedWidth = 44 } ) );
		_blend.OnValueEdited = () => _blendLabel.Text = $"{_blend.Value:0.00} s";
		loopCard.Layout.Add( new TaButton( loopCard, "Make seamless loop", "all_inclusive", () =>
		{
			var clip = _session.ActiveClip;
			if ( clip is null ) return;
			var frames = Math.Clamp( (int)MathF.Round( _blend.Value * clip.Fps ), 1, Math.Max( 1, clip.FrameCount - 2 ) );
			_session.Edit( "Make seamless loop", c => ClipOps.MakeSeamlessLoop( c, _session.Rig, frames ) );
		}, "Blend the end into the start so the loop doesn't pop (travel is kept)", 28 ) );

		// ---- clean up
		var cleanCard = Layout.Add( new TaCard( this ) );
		cleanCard.Header( "cleaning_services", "Clean up" ).AddStretchCell();
		var cleanRow = cleanCard.Layout.AddRow();
		cleanRow.Spacing = 4;
		cleanRow.Add( new TaButton( cleanCard, "Fix foot sliding", "do_not_step", () => _session.Edit( "Fix foot sliding", c => ClipCleanup.CleanFootSliding( c, _session.Rig ) ), "Lock planted feet to the floor" ) );
		cleanRow.Add( new TaButton( cleanCard, "Ground feet", "vertical_align_bottom", () => _session.Edit( "Ground feet", c => ClipCleanup.GroundFeet( c, _session.Rig ) ), "Move the clip so the feet touch the floor" ) );
		cleanCard.Layout.Add( new TaButton( cleanCard, "Detect footsteps", "directions_walk", () => _session.Edit( "Detect footsteps", c =>
		{
			var n = ClipCleanup.GenerateFootsteps( c, _session.Rig );
			_session.SetStatus( n == 0 ? "No footsteps found." : $"{n} footstep events added." );
		} ), "Add AE_FOOTSTEP events where the feet plant (used for footstep sounds)" ) );

		// ---- quality
		var qualityCard = Layout.Add( new TaCard( this ) );
		qualityCard.Header( "fact_check", "Quality check" ).AddStretchCell();
		_issues = qualityCard.Layout.Add( new Widget( qualityCard ) { Layout = Layout.Column() } );
		_issues.Layout.Spacing = 4;

		Layout.AddStretchCell();
		_session.Changed += c => { if ( (c & (SessionChange.ActiveClip | SessionChange.ClipData | SessionChange.Selection | SessionChange.Model | SessionChange.Busy)) != 0 ) Refresh(); };
		Refresh();
	}

	void RangeEdit( string label, Action<AnimClip, int, int> op )
	{
		if ( _session.Range is not { } r ) { _session.SetStatus( "Select a range first: Shift+drag on the timeline.", Tone.Amber ); return; }
		_session.Edit( label, c => op( c, r.Start, r.End ) );
		_session.SetRange( null, null );
	}

	void Split()
	{
		var clip = _session.ActiveClip;
		if ( clip is null ) return;
		var frame = _session.CurrentFrame;
		if ( frame <= 0 || frame >= clip.FrameCount - 1 ) { _session.SetStatus( "Move the playhead inside the animation to split it.", Tone.Amber ); return; }
		AnimClip second = null;
		if ( _session.Edit( "Split", c => second = ClipOps.Split( c, _session.Rig, frame, c.Name + " (part 2)" ) ) && second is not null )
			_session.AddClip( second, select: false );
	}

	void ApplyOffset()
	{
		static float Parse( string s ) => float.TryParse( s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) ? v : 0f;
		var fwd = Parse( _offsetForward.Text ); var side = Parse( _offsetSide.Text ); var turn = Parse( _turn.Text );
		if ( fwd == 0 && side == 0 && turn == 0 ) return;
		var rig = _session.Rig;
		var translation = rig.Forward * fwd + rig.Lateral * side;
		var progressive = _progressive.Value;
		_session.Edit( progressive ? "Bend root path" : "Offset root", c => ClipOps.OffsetRoot( c, rig, translation, turn, progressive ) );
		_offsetForward.Text = _offsetSide.Text = _turn.Text = "";
	}

	public void Refresh()
	{
		var clip = _session.ActiveClip;
		Enabled = clip is not null && !_session.Busy;
		if ( clip is null ) { _info.Text = "No animation open."; ClearIssues(); return; }
		_refreshing = true;
		try
		{
			if ( !_name.IsFocused ) _name.Text = clip.Name;
			_info.Text = $"{clip.FrameCount} frames · {clip.Duration:0.00} s · {clip.Events.Count} events";
			_looping.Value = clip.Looping;
			RefreshFps( clip.Fps );
		}
		finally { _refreshing = false; }
		_rangeLabel.Text = _session.Range is { } r
			? $"Selected frames {r.Start}–{r.End} ({(r.End - r.Start) / clip.Fps:0.00} s)."
			: "Select a range: Shift+drag on the timeline.";
		_rangeButtons.Enabled = _session.Range is not null;
		RefreshIssues( clip );
	}

	/// <summary>Lists the common rates plus the clip's own, with the clip's rate selected.</summary>
	void RefreshFps( float current )
	{
		if ( MathF.Abs( current - _fpsShown ) < 0.01f ) return;
		_fpsShown = current;
		_fps.Clear();
		var rates = new System.Collections.Generic.List<float> { 24f, 30f, 60f };
		if ( !rates.Any( r => MathF.Abs( r - current ) < 0.01f ) ) rates.Add( current );
		rates.Sort();
		foreach ( var rate in rates )
		{
			var f = rate;
			var isCurrent = MathF.Abs( f - current ) < 0.01f;
			_fps.AddItem( $"{f:0.##} fps", null, () => { if ( !_refreshing && MathF.Abs( f - _fpsShown ) > 0.01f ) _session.Edit( $"Resample to {f:0.##} fps", c => ClipOps.Resample( c, _session.Rig, f ) ); }, selected: isCurrent );
		}
	}

	void ClearIssues() => _issues.Layout.Clear( true );

	void RefreshIssues( AnimClip clip )
	{
		ClearIssues();
		var issues = ClipQuality.Analyze( clip, _session.Rig );
		if ( issues.Count == 0 )
		{
			var ok = _issues.Layout.Add( new Label( "No problems found.", _issues ) );
			ok.SetStyles( $"color: {TaStyle.AccentLight.Hex};" );
			return;
		}
		foreach ( var issue in issues.OrderByDescending( i => i.Severity ).Take( 8 ) )
		{
			var row = _issues.Layout.Add( new Widget( _issues ) { Layout = Layout.Row() } );
			row.Layout.Spacing = 6;
			var color = issue.Severity switch { IssueSeverity.Error => Theme.Red, IssueSeverity.Warning => Theme.Yellow, _ => Theme.TextLight };
			var icon = row.Layout.Add( new IconButton( issue.Severity == IssueSeverity.Error ? "error" : issue.Severity == IssueSeverity.Warning ? "warning" : "info", null, row )
				{ FixedSize = 20, IconSize = 15, Foreground = color, Background = Color.Transparent } );
			var label = row.Layout.Add( new Label( issue.Message, row ) { WordWrap = true }, 1 );
			label.SetStyles( $"color: {color.Hex}; font-size: 11px;" );
			if ( issue.Frame is int f )
			{
				label.ToolTip = $"Frame {f} - click to jump there";
				label.MouseClick += () => _session.Seek( f );
			}
			if ( issue.Fix != IssueFix.None )
			{
				var fix = issue.Fix;
				row.Layout.Add( new TaButton( row, "Fix", null, () => _session.Edit( $"Fix: {issue.Code}", c => ClipQuality.ApplyFix( c, _session.Rig, fix ) ), "Apply the automatic fix" ) );
			}
		}
	}
}
