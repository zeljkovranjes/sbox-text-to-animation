#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>A centered icon, title and hint for a panel with nothing to show yet.</summary>
public sealed class TaEmptyState : Widget
{
	readonly string _icon, _title, _hint;

	public TaEmptyState( Widget parent, string icon, string title, string hint ) : base( parent )
	{
		_icon = icon; _title = title; _hint = hint;
		FixedHeight = 150;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( .35f ) );
		Paint.DrawRect( LocalRect.Shrink( .5f ), TaStyle.Radius + 2 );
		Paint.SetPen( TaStyle.Accent.WithAlpha( .8f ) );
		Paint.DrawIcon( new Rect( 0, 22, Width, 34 ), _icon, 30, TextFlag.Center );
		Paint.SetPen( Theme.Text );
		Paint.SetDefaultFont( 9, 600 );
		Paint.DrawText( new Rect( 0, 64, Width, 20 ), _title, TextFlag.Center );
		Paint.SetPen( Theme.TextLight );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( new Rect( 24, 88, Width - 48, 44 ), _hint, TextFlag.CenterTop | TextFlag.WordWrap );
	}
}
