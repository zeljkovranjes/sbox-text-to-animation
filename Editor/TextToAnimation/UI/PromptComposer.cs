using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.UI;

/// <summary>Which part of an existing animation a text change applies to.</summary>
public enum ChangeScope { WholeBody, UpperBody, LowerBody, Arms, SelectedBones, Unlocked }

/// <summary>The settings under the prompt. Simple users never need to touch them.</summary>
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
/// The prompt: a large multi-line box (Enter sends, Shift+Enter adds a line), a round send button that
/// becomes a stop button while generating, and compact option chips underneath. When an animation is the
/// target, a chip above the text says so ("Changing Walk · whole body") and can be cleared.
/// </summary>
public sealed class PromptComposer : Widget
{
	readonly PromptBox _box;
	readonly SendButton _send;
	readonly Widget _targetRow;
	readonly ChipButton _targetChip;
	readonly ChipButton _scopeChip;
	readonly ChipButton _lengthChip;
	readonly ChipButton _takesChip;
	readonly ChipButton _qualityChip;
	readonly ChipButton _advancedChip;
	string _target;
	string _disabledReason;
	bool _busy;

	public PromptOptions Options { get; } = new();

	/// <summary>Called with the trimmed prompt when the user sends it.</summary>
	public Action<string> Submitted { get; set; }
	/// <summary>Called when the user presses stop while busy.</summary>
	public Action StopRequested { get; set; }
	/// <summary>Called when the user clears the target chip.</summary>
	public Action TargetCleared { get; set; }
	/// <summary>The body parts offered by the scope chip (humanoid regions by default).</summary>
	public List<ChangeScope> ScopeChoices { get; set; } = new() { ChangeScope.WholeBody, ChangeScope.UpperBody, ChangeScope.LowerBody, ChangeScope.Arms };
	/// <summary>Allows sending an empty prompt (variations, in-betweens).</summary>
	public bool AllowEmpty { get; set; }
	readonly Layout _extraChips;

	public PromptComposer( Widget parent, string placeholder ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 14, 10, 10, 10 );
		Layout.Spacing = 6;

		_targetRow = Layout.Add( new Widget( this ) { Layout = Layout.Row(), Visible = false } );
		_targetRow.Layout.Spacing = 6;
		_targetChip = _targetRow.Layout.Add( new ChipButton( _targetRow, "", "edit", null, "Clear: describe a new animation instead", closable: true ) );
		_targetChip.Clicked = () => TargetCleared?.Invoke();
		_scopeChip = _targetRow.Layout.Add( new ChipButton( _targetRow, "", "accessibility_new", ScopeMenu, "Which part of the body the change applies to" ) );
		_targetRow.Layout.AddStretchCell();

		_box = Layout.Add( new PromptBox( this, Send ) { PlaceholderText = placeholder }, 1 );

		var bottom = Layout.AddRow();
		bottom.Spacing = 6;
		_extraChips = bottom.AddRow();
		_extraChips.Spacing = 6;
		_lengthChip = bottom.Add( new ChipButton( this, "", "schedule", LengthMenu, "Length of a new animation" ) );
		_takesChip = bottom.Add( new ChipButton( this, "", "content_copy", TakesMenu, "How many versions to make" ) );
		_qualityChip = bottom.Add( new ChipButton( this, "", "tune", QualityMenu, "More steps: smoother and more accurate, but slower" ) );
		_advancedChip = bottom.Add( new ChipButton( this, "", "more_horiz", AdvancedMenu, "Seed, prompt strength and variation strength" ) );
		bottom.AddStretchCell();
		_send = bottom.Add( new SendButton( this, () => { if ( _busy ) StopRequested?.Invoke(); else Send(); } ) );
		MinimumHeight = 120;
		RefreshChips();
	}

	public string Text
	{
		get => _box.PlainText ?? "";
		set => _box.PlainText = value ?? "";
	}

	public void FocusPrompt() => _box.Focus();

	/// <summary>The text input (tests post key events to it).</summary>
	public Widget Input => _box;

	/// <summary>Sends the prompt as if the user pressed Enter.</summary>
	public void Submit() => Send();

	/// <summary>Shows what the next prompt changes, or null for a new animation.</summary>
	public void SetTarget( string clipName )
	{
		_target = clipName;
		_targetRow.Visible = clipName is not null;
		RefreshChips();
		RefreshPlaceholder();
	}

	public string Target => _target;

	/// <summary>Adds a chip at the start of the option row (e.g. what the prompt does).</summary>
	public ChipButton AddChip( string text, string icon, Action clicked, string tooltip )
		=> _extraChips.Add( new ChipButton( this, text, icon, clicked, tooltip ) );

	/// <summary>Shows the length chip (new animations) or not.</summary>
	public bool ShowLength { get => _showLength; set { _showLength = value; RefreshChips(); } }
	bool _showLength = true;

	/// <summary>Re-reads the options into the chips (after changing <see cref="Options"/> in code).</summary>
	public void RefreshOptions() => RefreshChips();

	/// <summary>Whether the body-part chip is offered (humanoid rigs).</summary>
	public bool ShowScope
	{
		get => _showScope;
		set { _showScope = value; _scopeChip.Visible = value; if ( !value ) Options.Scope = ChangeScope.WholeBody; RefreshChips(); }
	}
	bool _showScope = true;

	/// <summary>Disables sending, with the reason shown in the box (null enables).</summary>
	public void SetDisabledReason( string reason )
	{
		_disabledReason = reason;
		_box.Editable = reason is null;
		_send.Enabled = reason is null || _busy;
		RefreshPlaceholder();
		Update();
	}

	public bool Busy
	{
		get => _busy;
		set { _busy = value; _send.Stop = value; _send.Enabled = value || _disabledReason is null; _send.Update(); }
	}

	string _placeholder;
	public string Placeholder { get => _placeholder; set { _placeholder = value; RefreshPlaceholder(); } }

	void RefreshPlaceholder()
	{
		_box.PlaceholderText = _disabledReason ?? (_target is not null
			? $"Describe how {_target} should change… e.g. \"wave with the right hand\""
			: _placeholder ?? "Describe a motion… e.g. \"walk cautiously forward, look behind, then run\"");
	}

	void Send()
	{
		if ( _busy || _disabledReason is not null ) return;
		var text = Text.Trim();
		if ( text.Length == 0 && !AllowEmpty ) { _box.Focus(); return; }
		Submitted?.Invoke( text );
	}

	void RefreshChips()
	{
		_lengthChip.Text = $"{Options.Seconds:0.#} s";
		_lengthChip.Visible = _target is null && _showLength;
		_takesChip.Text = Options.Takes == 1 ? "1 take" : $"{Options.Takes} takes";
		_qualityChip.Text = Options.Steps <= 12 ? "Fast" : Options.Steps >= 40 ? "Best" : "Standard";
		_targetChip.Text = _target is null ? "" : $"Changing {_target}";
		_scopeChip.Text = ScopeName( Options.Scope );
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
	{
		var option = menu.AddOption( text, selected ? "check" : null, () => { apply(); RefreshChips(); } );
	}

	void LengthMenu()
	{
		var menu = new Menu( this );
		foreach ( var s in new[] { 2f, 3f, 4f, 6f, 8f, 10f } )
			Choose( menu, $"{s:0} seconds", MathF.Abs( Options.Seconds - s ) < 0.01f, () => Options.Seconds = s );
		menu.OpenAtCursor();
	}

	void TakesMenu()
	{
		var menu = new Menu( this );
		for ( var n = 1; n <= 4; n++ )
		{
			var count = n;
			Choose( menu, count == 1 ? "1 take" : $"{count} takes", Options.Takes == count, () => Options.Takes = count );
		}
		menu.OpenAtCursor();
	}

	void QualityMenu()
	{
		var menu = new Menu( this );
		Choose( menu, "Fast (12 steps)", Options.Steps == 12, () => Options.Steps = 12 );
		Choose( menu, "Standard (24 steps)", Options.Steps == 24, () => Options.Steps = 24 );
		Choose( menu, "Best (40 steps)", Options.Steps == 40, () => Options.Steps = 40 );
		menu.OpenAtCursor();
	}

	void ScopeMenu()
	{
		var menu = new Menu( this );
		foreach ( var s in ScopeChoices )
		{
			var scope = s;
			Choose( menu, ScopeName( scope ), Options.Scope == scope, () => Options.Scope = scope );
		}
		menu.OpenAtCursor();
	}

	void AdvancedMenu()
	{
		var menu = new Menu( this );
		menu.AddHeading( "Prompt strength" );
		foreach ( var g in new[] { 2f, 3f, 4.5f } )
			Choose( menu, g switch { 2f => "Loose", 3f => "Normal", _ => "Strict" }, MathF.Abs( Options.Guidance - g ) < 0.01f, () => Options.Guidance = g );
		menu.AddSeparator();
		menu.AddHeading( "Variations stay" );
		foreach ( var v in new[] { 0.3f, 0.5f, 0.75f } )
			Choose( menu, v switch { 0.3f => "Close to the original", 0.5f => "Somewhat different", _ => "Very different" }, MathF.Abs( Options.VariationStrength - v ) < 0.01f, () => Options.VariationStrength = v );
		menu.AddSeparator();
		menu.AddHeading( "Seed" );
		Choose( menu, "Random each time", Options.Seed is null, () => Options.Seed = null );
		Choose( menu, Options.Seed is int fixedSeed ? $"Fixed: {fixedSeed}" : "Fixed (reproducible)", Options.Seed is not null,
			() => Options.Seed ??= Random.Shared.Next( 1, 99999 ) );
		menu.OpenAtCursor();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		var focused = _box.IsFocused;
		Paint.SetPen( focused ? TaStyle.Accent : Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, .16f ), focused ? 1.5f : 1f );
		Paint.SetBrush( Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, .04f ) );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 12 );
	}

	/// <summary>The text area: Enter sends, Shift+Enter is a new line.</summary>
	sealed class PromptBox : TextEdit
	{
		readonly Action _send;

		public PromptBox( Widget parent, Action send ) : base( parent )
		{
			_send = send;
			SetStyles( "background-color: transparent; border: none; font-size: 15px;" );
			VerticalScrollbarMode = ScrollbarMode.Off;
			HorizontalScrollbarMode = ScrollbarMode.Off;
			MinimumHeight = 48;
		}

		protected override void OnKeyPress( KeyEvent e )
		{
			if ( (e.Key == KeyCode.Enter || e.Key == KeyCode.Return) && !e.HasShift )
			{
				e.Accepted = true;
				_send();
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
			FixedSize = 34;
			Cursor = CursorShape.Finger;
			MouseTracking = true;
			ToolTip = "Generate (Enter)";
		}

		protected override void OnMouseEnter() => Update();
		protected override void OnMouseLeave() => Update();

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			if ( Enabled && e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) _clicked();
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			Paint.ClearPen();
			var color = !Enabled ? Color.White.WithAlpha( .12f ) : Paint.HasMouseOver ? TaStyle.AccentLight : TaStyle.Accent;
			Paint.SetBrush( color );
			Paint.DrawRect( LocalRect.Shrink( 1 ), 17 );
			Paint.SetPen( Enabled ? Color.White : Theme.TextDisabled );
			Paint.DrawIcon( LocalRect, Stop ? "stop" : "arrow_upward", 20, TextFlag.Center );
			ToolTip = Stop ? "Stop" : "Generate (Enter)";
		}
	}
}

/// <summary>A small rounded chip (icon, text, optional ▾ or ×) for options and context.</summary>
public sealed class ChipButton : Widget
{
	string _text;
	readonly string _icon;
	readonly bool _closable;
	public Action Clicked { get; set; }

	public ChipButton( Widget parent, string text, string icon, Action clicked, string tooltip, bool closable = false ) : base( parent )
	{
		_icon = icon;
		_closable = closable;
		Clicked = clicked;
		ToolTip = tooltip;
		FixedHeight = 26;
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
			Paint.SetDefaultFont( 8 );
			FixedWidth = MathF.Ceiling( 10 + 16 + 4 + _text.Length * 6.4f + 18 );
			Update();
		}
	}

	protected override void OnMouseEnter() => Update();
	protected override void OnMouseLeave() => Update();

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( Enabled && e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) Clicked?.Invoke();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		var hover = Paint.HasMouseOver && Enabled;
		Paint.SetPen( Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, hover ? .24f : .13f ), 1 );
		Paint.SetBrush( Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, hover ? .1f : .05f ) );
		Paint.DrawRect( LocalRect.Shrink( .5f ), 13 );
		Paint.SetPen( Enabled ? Theme.TextLight : Theme.TextDisabled );
		Paint.DrawIcon( new Rect( 8, 0, 16, Height ), _icon, 14, TextFlag.Center );
		Paint.SetDefaultFont( 8 );
		Paint.SetPen( Enabled ? Theme.Text : Theme.TextDisabled );
		var textWidth = Paint.MeasureText( _text ).x;
		var wanted = MathF.Ceiling( 8 + 16 + 4 + textWidth + 22 );
		if ( MathF.Abs( wanted - FixedWidth ) > .5f ) FixedWidth = wanted;
		Paint.DrawText( new Rect( 28, 0, textWidth + 2, Height ), _text, TextFlag.LeftCenter );
		Paint.SetPen( Theme.TextLight );
		Paint.DrawIcon( new Rect( Width - 20, 0, 14, Height ), _closable ? "close" : "expand_more", 13, TextFlag.Center );
	}
}
