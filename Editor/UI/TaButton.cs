#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>The secondary button (the Weapon Importer's): a fill one step lighter than the card,
/// a hairline border and a hover lighten.</summary>
public sealed class TaButton : Widget
{
	string _text;
	readonly string _icon;

	public Action Clicked { get; set; }

	public TaButton( Widget parent, string text, string icon = null, Action clicked = null, string tooltip = null,
		float height = TaStyle.ControlHeight ) : base( parent )
	{
		_text = text ?? "";
		_icon = icon;
		Clicked = clicked;
		ToolTip = tooltip;
		FixedHeight = height;
		Cursor = CursorShape.Finger;
		MouseTracking = true;
		FocusMode = FocusMode.None;
		Measure();
	}

	public string Text
	{
		get => _text;
		set
		{
			_text = value ?? "";
			Measure();
			Update();
		}
	}

	const float PadX = 10f;
	const float IconSize = 16f;
	const float IconGap = 5f;

	bool _fill;

	/// <summary>Stretches to the space the layout gives it (at least its text width) instead of hugging the text.</summary>
	public bool Fill
	{
		get => _fill;
		set { _fill = value; Measure(); }
	}

	void Measure() => SetWidth( WidthFor( _text.Length == 0 ? 0 : 6.2f * _text.Length ) );

	void SetWidth( float width )
	{
		if ( !_fill ) { FixedWidth = width; return; }
		MinimumWidth = width;
		MaximumWidth = 16777215;
	}

	float WidthFor( float textWidth )
	{
		if ( _text.Length == 0 )
			return string.IsNullOrEmpty( _icon ) ? PadX * 2 : TaStyle.ControlHeight;
		var icon = string.IsNullOrEmpty( _icon ) ? 0 : IconSize + IconGap;
		return MathF.Ceiling( PadX * 2 + icon + textWidth );
	}

	protected override void OnMouseEnter() => Update();
	protected override void OnMouseLeave() => Update();

	protected override void OnMousePress( MouseEvent e )
	{
		if ( e.LeftMouseButton )
			e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( !Enabled || !e.LeftMouseButton || !LocalRect.IsInside( e.LocalPosition ) )
			return;
		Clicked?.Invoke();
		e.Accepted = true;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		var hover = Paint.HasMouseOver && Enabled;
		var edge = Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, hover ? .25f : .15f );
		Paint.SetPen( edge, 1 );
		Paint.SetBrush( hover ? Color.Lerp( TaStyle.ButtonFill, Color.White, .06f ) : TaStyle.ButtonFill );
		Paint.DrawRect( LocalRect.Shrink( .5f ), TaStyle.Radius );

		Paint.SetPen( Enabled ? Theme.Text : Theme.TextDisabled );
		if ( _text.Length == 0 )
		{
			if ( !string.IsNullOrEmpty( _icon ) )
				Paint.DrawIcon( LocalRect, _icon, 15, TextFlag.Center );
			return;
		}
		Paint.SetDefaultFont( 8 );
		var textWidth = Paint.MeasureText( _text ).x;
		var wanted = WidthFor( textWidth );
		if ( MathF.Abs( wanted - (_fill ? MinimumWidth : FixedWidth) ) > 0.5f )
			SetWidth( wanted );
		var hasIcon = !string.IsNullOrEmpty( _icon );
		var group = textWidth + (hasIcon ? IconSize + IconGap : 0);
		var x = LocalRect.Left + MathF.Max( PadX, (LocalRect.Width - group) * 0.5f );
		if ( hasIcon )
		{
			Paint.DrawIcon( new Rect( x, LocalRect.Top, IconSize, LocalRect.Height ), _icon, 15, TextFlag.Center );
			x += IconSize + IconGap;
		}
		Paint.DrawText( new Rect( x, LocalRect.Top, LocalRect.Right - x, LocalRect.Height ), _text, TextFlag.LeftCenter );
	}
}
