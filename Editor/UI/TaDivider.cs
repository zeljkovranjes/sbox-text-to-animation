#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>A hairline between rows of a card.</summary>
public sealed class TaDivider : Widget
{
	public TaDivider( Widget parent ) : base( parent ) => FixedHeight = 7;

	protected override void OnPaint()
	{
		Paint.SetPen( Theme.ControlBackground.Lighten( .35f ), 1 );
		Paint.DrawLine( new Vector2( 0, Height * .5f ), new Vector2( Width, Height * .5f ) );
	}
}
