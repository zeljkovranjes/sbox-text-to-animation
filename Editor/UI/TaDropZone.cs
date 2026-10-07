#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>The empty state of the clip list: a prompt and the add button. Accepts animation
/// files from the OS and from the asset browser.</summary>
public sealed class TaDropZone : Widget
{
	readonly Action<IReadOnlyList<string>> _add;
	int _hover;

	readonly string[] _extensions;

	public TaDropZone( Widget parent, string title, string subtitle, string buttonText, string buttonIcon, string[] extensions,
		Action<IReadOnlyList<string>> add, Action choose ) : base( parent )
	{
		_add = add;
		_extensions = extensions;
		AcceptDrops = true;
		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 6;
		Layout.AddStretchCell();
		var titleLabel = Layout.Add( new Label( title, this ) { Alignment = TextFlag.Center } );
		titleLabel.SetStyles( "font-weight: 600;" );
		Layout.Add( TaStyle.Muted( new Label( subtitle, this ) { Alignment = TextFlag.Center, WordWrap = true }, small: true ) );
		var row = Layout.AddRow();
		row.AddStretchCell();
		row.Add( new Button.Primary( buttonText ) { Icon = buttonIcon, Tint = TaStyle.Accent, FixedHeight = 28, Clicked = choose } );
		row.AddStretchCell();
		Layout.AddStretchCell();
	}

	public override void OnDragHover( DragEvent e )
	{
		var valid = TaDrop.Paths( e.Data, _extensions ).Count > 0;
		_hover = valid ? 1 : -1;
		if ( valid )
			e.Action = DropAction.Link;
		Update();
	}

	public override void OnDragDrop( DragEvent e )
	{
		_hover = 0;
		var paths = TaDrop.Paths( e.Data, _extensions );
		if ( paths.Count > 0 )
		{
			e.Action = DropAction.Link;
			_add( paths );
		}
		Update();
	}

	public override void OnDragLeave()
	{
		_hover = 0;
		Update();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.SetPen( _hover == 1 ? TaStyle.Accent : _hover < 0 ? Theme.Red : Theme.ControlBackground.Lighten( .45f ), _hover == 0 ? 1 : 2 );
		Paint.SetBrush( _hover == 1 ? TaStyle.Accent.WithAlpha( .06f ) : Theme.WindowBackground.WithAlpha( .5f ) );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
	}
}
