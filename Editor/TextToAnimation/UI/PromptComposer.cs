using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.UI;

/// <summary>Which part of an existing animation a text change applies to.</summary>
public enum ChangeScope { WholeBody, UpperBody, LowerBody, Arms, SelectedBones, Unlocked }

/// <summary>The settings behind the prompt. Simple users never need to touch them.</summary>
public sealed class PromptOptions
{
	public float Seconds { get; set; } = 4f;
	public int Takes { get; set; } = 1;
	public int Steps { get; set; } = 24;
	public float Guidance { get; set; } = 3f;
	/// <summary>Null = a new random seed per generation.</summary>
	public int? Seed { get; set; }
	public float VariationStrength { get; set; } = 0.5f;
	public ChangeScope Scope { get; set; } = ChangeScope.WholeBody;
}

/// <summary>
/// The prompt row at the top of the timeline dock: what the prompt does (a chip), the text field
/// (Enter sends, Shift+Enter adds a line and the field grows up to three lines), which body part a change
/// applies to, one settings chip (length, takes, quality, advanced) and a send button that becomes stop while
/// generating. Styled like the editor's other inputs so it reads as part of the dock.
/// </summary>
public sealed class PromptComposer : Widget
{
	const float LineHeight = 17f;
	const float SingleLine = 28f;

	readonly Layout _leading;
	readonly Field _field;
	readonly PromptBox _box;
	readonly ChipButton _scopeChip;
	readonly ChipButton _settingsChip;
	readonly SendButton _send;
	string _target;
	string _disabledReason;
	string _placeholder;
	bool _busy;
	bool _showScope = true;
	bool _showLength = true;

	public PromptOptions Options { get; } = new();

	/// <summary>Called with the trimmed prompt when the user sends it.</summary>
	public Action<string> Submitted { get; set; }
	/// <summary>Called when the user presses stop while busy.</summary>
	public Action StopRequested { get; set; }
	/// <summary>The body parts offered by the scope chip.</summary>
	public List<ChangeScope> ScopeChoices { get; set; } = new() { ChangeScope.WholeBody };
	/// <summary>Allows sending an empty prompt (variations, in-betweens).</summary>
	public bool AllowEmpty { get; set; }
	/// <summary>Called when the user clicks the history icon (shows past prompts).</summary>
	public Action<Widget> HistoryRequested { get; set; }

	public PromptComposer( Widget parent, string placeholder ) : base( parent )
	{
		_placeholder = placeholder;
		Layout = Layout.Row();
		Layout.Spacing = 6;
		_leading = Layout.AddRow();
		_leading.Spacing = 6;
		_field = Layout.Add( new Field( this ), 1 );
		_box = _field.Box;
		_box.Send = Send;
		_field.History.OnClick = () => HistoryRequested?.Invoke( _field.History );
		_box.TextChanged += _ => Grow();
		_scopeChip = Layout.Add( new ChipButton( this, "", "accessibility_new", ScopeMenu, "Which part of the body the change applies to" ) );
		_settingsChip = Layout.Add( new ChipButton( this, "", "tune", SettingsMenu, "Length, takes, quality and more" ) );
		_send = Layout.Add( new SendButton( this, () => { if ( _busy ) StopRequested?.Invoke(); else Send(); } ) );
		RefreshChips();
		RefreshPlaceholder();
		Grow();
	}

	public string Text
	{
		get => _box.PlainText ?? "";
		set { _box.PlainText = value ?? ""; Grow(); }
	}

	public void FocusPrompt() => _box.Focus();

	/// <summary>The text input (tests post key events to it).</summary>
	public Widget Input => _box;

	/// <summary>Sends the prompt as if the user pressed Enter.</summary>
	public void Submit() => Send();

	/// <summary>The animation a change applies to (for the placeholder), or null for a new animation.</summary>
	public void SetTarget( string clipName )
	{
		_target = clipName;
		RefreshChips();
		RefreshPlaceholder();
	}

	public string Target => _target;

	/// <summary>Adds a chip before the text field (e.g. what the prompt does).</summary>
	public ChipButton AddChip( string text, string icon, Action clicked, string tooltip )
		=> _leading.Add( new ChipButton( this, text, icon, clicked, tooltip ) );

	/// <summary>Whether the body-part chip is shown.</summary>
	public bool ShowScope
	{
		get => _showScope;
		set { _showScope = value; if ( !value ) Options.Scope = ChangeScope.WholeBody; RefreshChips(); }
	}

	/// <summary>Whether the settings offer a length (new animations).</summary>
	public bool ShowLength { get => _showLength; set { _showLength = value; RefreshChips(); } }

	/// <summary>Re-reads the options into the chips (after changing <see cref="Options"/> in code).</summary>
	public void RefreshOptions() => RefreshChips();

	/// <summary>Disables sending, with the reason shown in the field (null enables).</summary>
	public void SetDisabledReason( string reason )
	{
		_disabledReason = reason;
		_box.Editable = reason is null;
		_send.Enabled = reason is null || _busy;
		RefreshPlaceholder();
	}

	public bool Busy
	{
		get => _busy;
		set { _busy = value; _send.Stop = value; _send.Enabled = value || _disabledReason is null; _send.Update(); }
	}

	public string Placeholder { get => _placeholder; set { _placeholder = value; RefreshPlaceholder(); } }

	void RefreshPlaceholder() => _box.PlaceholderText = _disabledReason ?? _placeholder ?? "Describe a motion…";

	void Send()
	{
		if ( _busy || _disabledReason is not null ) return;
		var text = Text.Trim();
		if ( text.Length == 0 && !AllowEmpty ) { _box.Focus(); return; }
		Submitted?.Invoke( text );
	}

	/// <summary>One line by default; grows with Shift+Enter lines up to three.</summary>
	void Grow()
	{
		var lines = Math.Clamp( (Text.Count( c => c == '\n' ) + 1), 1, 3 );
		var height = SingleLine + (lines - 1) * LineHeight;
		_field.FixedHeight = height;
		FixedHeight = height;
	}

	void RefreshChips()
	{
		_scopeChip.Visible = _showScope && _target is not null;
		_scopeChip.Text = ScopeName( Options.Scope );
		var parts = new List<string>();
		if ( _showLength ) parts.Add( $"{Options.Seconds:0.#} s" );
		parts.Add( Options.Takes == 1 ? "1 take" : $"{Options.Takes} takes" );
		parts.Add( Options.Steps <= 12 ? "Fast" : Options.Steps >= 40 ? "Best" : "Standard" );
		_settingsChip.Text = string.Join( " · ", parts );
	}

	public static string ScopeName( ChangeScope scope ) => scope switch
	{
		ChangeScope.UpperBody => "Upper body",
		ChangeScope.LowerBody => "Legs",
		ChangeScope.Arms => "Arms",
		ChangeScope.SelectedBones => "Selected bones",
		ChangeScope.Unlocked => "Unlocked bones",
		_ => "Whole body",
	};

	void Choose( Menu menu, string text, bool selected, Action apply )
		=> menu.AddOption( text, selected ? "check" : null, () => { apply(); RefreshChips(); } );

	void ScopeMenu()
	{
		var menu = new Menu( this );
		menu.AddHeading( "The change applies to" );
		foreach ( var s in ScopeChoices )
		{
			var scope = s;
			Choose( menu, ScopeName( scope ), Options.Scope == scope, () => Options.Scope = scope );
		}
		menu.OpenAtCursor();
	}

	void SettingsMenu()
	{
		var menu = new Menu( this );
		if ( _showLength )
		{
			menu.AddHeading( "Length" );
			foreach ( var s in new[] { 2f, 3f, 4f, 6f, 8f, 10f } )
				Choose( menu, $"{s:0} seconds", MathF.Abs( Options.Seconds - s ) < 0.01f, () => Options.Seconds = s );
			menu.AddSeparator();
		}
		menu.AddHeading( "Takes" );
		for ( var n = 1; n <= 4; n++ )
		{
			var count = n;
			Choose( menu, count == 1 ? "1 take" : $"{count} takes", Options.Takes == count, () => Options.Takes = count );
		}
		menu.AddSeparator();
		menu.AddHeading( "Quality" );
		Choose( menu, "Fast (12 steps)", Options.Steps == 12, () => Options.Steps = 12 );
		Choose( menu, "Standard (24 steps)", Options.Steps == 24, () => Options.Steps = 24 );
		Choose( menu, "Best (40 steps)", Options.Steps == 40, () => Options.Steps = 40 );
		menu.AddSeparator();
		var advanced = menu.AddMenu( "Advanced", "more_horiz" );
		advanced.AddHeading( "Prompt strength" );
		foreach ( var g in new[] { 2f, 3f, 4.5f } )
			Choose( advanced, g switch { 2f => "Loose", 3f => "Normal", _ => "Strict" }, MathF.Abs( Options.Guidance - g ) < 0.01f, () => Options.Guidance = g );
		advanced.AddSeparator();
		advanced.AddHeading( "Variations are" );
		foreach ( var v in new[] { 0.3f, 0.5f, 0.75f } )
			Choose( advanced, v switch { 0.3f => "Close to the original", 0.5f => "Somewhat different", _ => "Very different" }, MathF.Abs( Options.VariationStrength - v ) < 0.01f, () => Options.VariationStrength = v );
		advanced.AddSeparator();
		advanced.AddHeading( "Seed" );
		Choose( advanced, "Random each time", Options.Seed is null, () => Options.Seed = null );
		Choose( advanced, Options.Seed is int fixedSeed ? $"Fixed: {fixedSeed}" : "Fixed (reproducible)", Options.Seed is not null,
			() => Options.Seed ??= Random.Shared.Next( 1, 99999 ) );
		menu.OpenAtCursor();
	}

	/// <summary>The input's frame: the editor's field look, a sparkle on the left, blue edge when focused.</summary>
	sealed class Field : Widget
	{
		public PromptBox Box { get; }
		public IconButton History { get; }

		public Field( Widget parent ) : base( parent )
		{
			Layout = Layout.Row();
			Layout.Margin = new Sandbox.UI.Margin( 26, 0, 2, 0 );
			Box = Layout.Add( new PromptBox( this ), 1 );
			History = Layout.Add( new IconButton( "history", null, this )
			{
				FixedSize = 24, IconSize = 16, Background = Color.Transparent, Foreground = Theme.TextLight,
				ToolTip = "Recent prompts",
			} );
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			var focused = Box.IsFocused;
			Paint.SetPen( focused ? TaStyle.Accent : TaStyle.InputEdge, 1 );
			Paint.SetBrush( Theme.WindowBackground );
			Paint.DrawRect( LocalRect.Shrink( .5f ), TaStyle.Radius );
			Paint.SetPen( focused ? TaStyle.AccentLight : TaStyle.Accent );
			Paint.DrawIcon( new Rect( 6, 0, 16, MathF.Min( Height, SingleLine ) ), "auto_awesome", 14, TextFlag.Center );
		}
	}

	/// <summary>The text area: Enter sends, Shift+Enter is a new line.</summary>
	sealed class PromptBox : TextEdit
	{
		public Action Send { get; set; }

		public PromptBox( Widget parent ) : base( parent )
		{
			SetStyles( "background-color: transparent; border: none; font-size: 13px; padding-top: 3px;" );
			VerticalScrollbarMode = ScrollbarMode.Off;
			HorizontalScrollbarMode = ScrollbarMode.Off;
		}

		protected override void OnKeyPress( KeyEvent e )
		{
			if ( (e.Key == KeyCode.Enter || e.Key == KeyCode.Return) && !e.HasShift )
			{
				e.Accepted = true;
				Send?.Invoke();
				return;
			}
			base.OnKeyPress( e );
		}

		protected override void OnFocus( FocusChangeReason reason ) { base.OnFocus( reason ); Parent?.Update(); }
		protected override void OnBlur( FocusChangeReason reason ) { base.OnBlur( reason ); Parent?.Update(); }
	}

	/// <summary>Round blue send button; a stop square while busy.</summary>
	sealed class SendButton : Widget
	{
		readonly Action _clicked;
		public bool Stop { get; set; }

		public SendButton( Widget parent, Action clicked ) : base( parent )
		{
			_clicked = clicked;
			FixedSize = 28;
			Cursor = CursorShape.Finger;
			MouseTracking = true;
			ToolTip = "Generate (Enter)";
		}

		protected override void OnMouseEnter() => Update();
		protected override void OnMouseLeave() => Update();

		protected override void OnMousePress( MouseEvent e )
		{
			if ( e.LeftMouseButton ) e.Accepted = true;
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			if ( Enabled && e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) _clicked();
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			Paint.ClearPen();
			Paint.SetBrush( !Enabled ? Color.White.WithAlpha( .1f ) : Paint.HasMouseOver ? TaStyle.AccentLight : TaStyle.Accent );
			Paint.DrawRect( LocalRect.Shrink( 1 ), 14 );
			Paint.SetPen( Enabled ? Color.White : Theme.TextDisabled );
			Paint.DrawIcon( LocalRect, Stop ? "stop" : "arrow_upward", 17, TextFlag.Center );
			ToolTip = Stop ? "Stop" : "Generate (Enter)";
		}
	}
}

/// <summary>A compact chip (icon, text, ▾) in the editor's button style, for options and modes.</summary>
public sealed class ChipButton : Widget
{
	string _text;
	readonly string _icon;
	public Action Clicked { get; set; }

	public ChipButton( Widget parent, string text, string icon, Action clicked, string tooltip ) : base( parent )
	{
		_icon = icon;
		Clicked = clicked;
		ToolTip = tooltip;
		FixedHeight = 28;
		Cursor = CursorShape.Finger;
		MouseTracking = true;
		Text = text;
	}

	public string Text
	{
		get => _text;
		set
		{
			_text = value ?? "";
			FixedWidth = MathF.Ceiling( 8 + 16 + 5 + _text.Length * 6.2f + 20 );
			Update();
		}
	}

	protected override void OnMouseEnter() => Update();
	protected override void OnMouseLeave() => Update();

	protected override void OnMousePress( MouseEvent e )
	{
		if ( e.LeftMouseButton ) e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( Enabled && e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) Clicked?.Invoke();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		var hover = Paint.HasMouseOver && Enabled;
		Paint.SetPen( Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, hover ? .25f : .15f ), 1 );
		Paint.SetBrush( hover ? Color.Lerp( TaStyle.ButtonFill, Color.White, .06f ) : TaStyle.ButtonFill );
		Paint.DrawRect( LocalRect.Shrink( .5f ), TaStyle.Radius );
		Paint.SetPen( Enabled ? TaStyle.AccentLight : Theme.TextDisabled );
		Paint.DrawIcon( new Rect( 8, 0, 16, Height ), _icon, 14, TextFlag.Center );
		Paint.SetDefaultFont( 8 );
		Paint.SetPen( Enabled ? Theme.Text : Theme.TextDisabled );
		var textWidth = Paint.MeasureText( _text ).x;
		var wanted = MathF.Ceiling( 8 + 16 + 5 + textWidth + 20 );
		if ( MathF.Abs( wanted - FixedWidth ) > .5f ) FixedWidth = wanted;
		Paint.DrawText( new Rect( 29, 0, textWidth + 2, Height ), _text, TextFlag.LeftCenter );
		Paint.SetPen( Theme.TextLight );
		Paint.DrawIcon( new Rect( Width - 18, 0, 14, Height ), "expand_more", 13, TextFlag.Center );
	}
}
