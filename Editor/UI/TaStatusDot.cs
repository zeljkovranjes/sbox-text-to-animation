#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>A small round light before the status text: blue while working, blue when fine, red on errors.</summary>
public sealed class TaStatusDot : Widget
{
	Color _color = Theme.TextLight;

	public TaStatusDot( Widget parent ) : base( parent )
	{
		FixedSize = 10;
	}

	public Color Color
	{
		get => _color;
		set
		{
			_color = value;
			Update();
		}
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( _color );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 4 );
	}
}
