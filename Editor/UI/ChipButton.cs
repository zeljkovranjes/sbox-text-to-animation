using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>A compact chip (icon, text, ▾) in the editor's button style, for options and modes.</summary>
public sealed class ChipButton : Widget
{
	string _text;
	readonly string _icon;
	public Action Clicked { get; set; }

	/// <summary>Draws the dropdown arrow (chips that open a menu); off for plain one-click chips.</summary>
	public bool Arrow { get; set; } = true;

	float ArrowRoom => Arrow ? 20 : 10;

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
			FixedWidth = MathF.Ceiling( 8 + 16 + 5 + _text.Length * 6.2f + ArrowRoom );
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
		var wanted = MathF.Ceiling( 8 + 16 + 5 + textWidth + ArrowRoom );
		if ( MathF.Abs( wanted - FixedWidth ) > .5f ) FixedWidth = wanted;
		Paint.DrawText( new Rect( 29, 0, textWidth + 2, Height ), _text, TextFlag.LeftCenter );
		if ( !Arrow ) return;
		Paint.SetPen( Theme.TextLight );
		Paint.DrawIcon( new Rect( Width - 18, 0, 14, Height ), "expand_more", 13, TextFlag.Center );
	}
}
