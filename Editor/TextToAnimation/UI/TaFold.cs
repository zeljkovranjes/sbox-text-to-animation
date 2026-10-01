using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// A collapsible section: a slim header (chevron, blue icon, title, optional note on the right) over a body
/// that only shows when open. Open/closed is remembered per section for the editor session, so a panel
/// keeps the shape the user left it in.
/// </summary>
public sealed class TaFold : Widget
{
	static readonly Dictionary<string, bool> Remembered = new();

	readonly string _key;
	readonly FoldHeader _header;
	bool _open;

	public Widget Body { get; }
	public Layout Content => Body.Layout;

	public TaFold( Widget parent, string icon, string title, bool open = false, string key = null ) : base( parent )
	{
		_key = key ?? title;
		_open = Remembered.TryGetValue( _key, out var remembered ) ? remembered : open;
		Layout = Layout.Column();
		_header = Layout.Add( new FoldHeader( this, icon, title ) );
		Body = Layout.Add( new Widget( this ) { Layout = Layout.Column() }, 1 );
		Body.Layout.Margin = new Sandbox.UI.Margin( 12, 2, 12, 12 );
		Body.Layout.Spacing = 6;
		Body.Visible = _open;
	}

	public bool Open
	{
		get => _open;
		set
		{
			if ( _open == value ) return;
			_open = value;
			Remembered[_key] = value;
			Body.Visible = value;
			_header.Update();
		}
	}

	/// <summary>Small text on the right of the header (a count, a warning).</summary>
	public string Note
	{
		get => _header.Note;
		set { _header.Note = value; _header.Update(); }
	}

	public Color NoteColor { get => _header.NoteColor; set { _header.NoteColor = value; _header.Update(); } }

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.SetPen( Theme.ControlBackground.Lighten( .25f ), 1 );
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
	}

	sealed class FoldHeader : Widget
	{
		readonly TaFold _fold;
		readonly string _icon, _title;
		public string Note = "";
		public Color NoteColor = Theme.TextLight;

		public FoldHeader( TaFold fold, string icon, string title ) : base( fold )
		{
			_fold = fold;
			_icon = icon;
			_title = title;
			FixedHeight = 34;
			Cursor = CursorShape.Finger;
			MouseTracking = true;
		}

		protected override void OnMouseEnter() => Update();
		protected override void OnMouseLeave() => Update();

		protected override void OnMouseReleased( MouseEvent e )
		{
			base.OnMouseReleased( e );
			if ( e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) _fold.Open = !_fold.Open;
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			if ( Paint.HasMouseOver )
			{
				Paint.ClearPen();
				Paint.SetBrush( Color.White.WithAlpha( .03f ) );
				Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
			}
			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( new Rect( 8, 0, 16, Height ), _fold.Open ? "expand_more" : "chevron_right", 16, TextFlag.Center );
			Paint.SetPen( TaStyle.Accent );
			Paint.DrawIcon( new Rect( 28, 0, 18, Height ), _icon, 16, TextFlag.Center );
			Paint.SetDefaultFont( 9, 600 );
			Paint.SetPen( _fold.Open || Paint.HasMouseOver ? Theme.Text : Theme.Text.WithAlpha( .8f ) );
			Paint.DrawText( new Rect( 52, 0, Width - 140, Height ), _title, TextFlag.LeftCenter );
			if ( string.IsNullOrEmpty( Note ) ) return;
			Paint.SetDefaultFont( 8 );
			Paint.SetPen( NoteColor );
			Paint.DrawText( new Rect( Width - 150, 0, 140, Height ), Note, TextFlag.RightCenter );
		}
	}
}
