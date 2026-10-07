using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;
using TextToAnimation.EditorTools.Workspace;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// A small popup with the prompts sent for this model, newest first. Clicking one puts it back in the
/// prompt; the arrow opens the animation it made (when it still exists).
/// </summary>
public sealed class PromptHistoryPopup : PopupWidget
{
	readonly EditorSession _session;
	readonly Action<string> _reuse;
	readonly LineEdit _filter;
	readonly Widget _list;

	public PromptHistoryPopup( Widget parent, EditorSession session, Action<string> reuse ) : base( parent )
	{
		_session = session;
		_reuse = reuse;
		FixedWidth = 420;
		WindowTitle = "Text to Animation - Recent prompts";
		Layout = Layout.Column();
		Layout.Margin = 10;
		Layout.Spacing = 6;
		var header = Layout.AddRow();
		header.Spacing = 6;
		var title = header.Add( new Label( "Recent prompts", this ) );
		title.SetStyles( "font-weight: 600;" );
		header.AddStretchCell();
		header.Add( TaStyle.Muted( new Label( session.PromptHistory.Count == 0 ? "" : $"{session.PromptHistory.Count}", this ), small: true ) );
		_filter = Layout.Add( TaStyle.Framed( new LineEdit( this ) { PlaceholderText = "Search prompts…" } ) );
		_filter.TextEdited += _ => Rebuild();
		_filter.Visible = session.PromptHistory.Count > 6;
		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		scroll.SetStyles( "background-color: transparent;" );
		_list = new Widget( scroll ) { Layout = Layout.Column() };
		_list.SetStyles( "background-color: transparent;" );
		_list.Layout.Spacing = 2;
		scroll.Canvas = _list;
		FixedHeight = Math.Clamp( 74 + session.PromptHistory.Count * 48 + (_filter.Visible ? 32 : 0), 110, 420 );
		Rebuild();
	}

	/// <summary>Opens above <paramref name="anchor"/> (the prompt sits low in the window).</summary>
	public void OpenAbove( Widget anchor )
	{
		var cursor = global::Editor.Application.CursorPosition;
		OpenAt( new Vector2( cursor.x - 30, cursor.y - Height - 14 ), animateOffset: new Vector2( 0, 8 ) );
		if ( _filter.Visible ) _filter.Focus();
	}

	void Rebuild()
	{
		_list.Layout.Clear( true );
		var filter = _filter.Text?.Trim() ?? "";
		var entries = _session.PromptHistory.Where( e => filter.Length == 0 || e.Prompt.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToList();
		if ( entries.Count == 0 )
			_list.Layout.Add( TaStyle.Muted( new Label( _session.PromptHistory.Count == 0 ? "Prompts you send appear here." : "Nothing matches.", _list ) { Alignment = TextFlag.Center, FixedHeight = 40 } ) );
		foreach ( var entry in entries.Take( 200 ) )
		{
			var clip = entry.ClipIds.Select( id => _session.Workspace?.Find( id ) ).FirstOrDefault( c => c is not null );
			var e = entry;
			_list.Layout.Add( new Row( _list, entry, clip,
				() => { _reuse( e.Prompt ); Close(); },
				clip is null ? null : () => { _session.SelectClip( clip ); Close(); } ) );
		}
		_list.Layout.AddStretchCell();
	}

	static string Ago( DateTime utc )
	{
		var span = DateTime.UtcNow - utc;
		if ( span.TotalMinutes < 1 ) return "just now";
		if ( span.TotalHours < 1 ) return $"{(int)span.TotalMinutes} min ago";
		if ( span.TotalDays < 1 ) return $"{(int)span.TotalHours} h ago";
		if ( span.TotalDays < 30 ) return $"{(int)span.TotalDays} d ago";
		return utc.ToLocalTime().ToString( "d MMM yyyy" );
	}

	static string ModeName( string mode ) => mode switch
	{
		"InBetween" => "In-between",
		"TextEdit" => "Change",
		"Variation" => "Variation",
		"Expansion" => "Sequence",
		_ => "New",
	};

	sealed class Row : Widget
	{
		readonly PromptHistoryEntry _entry;
		readonly AnimClip _clip;
		readonly Action _reuse;

		public Row( Widget parent, PromptHistoryEntry entry, AnimClip clip, Action reuse, Action open ) : base( parent )
		{
			_entry = entry;
			_clip = clip;
			_reuse = reuse;
			FixedHeight = 46;
			Cursor = CursorShape.Finger;
			MouseTracking = true;
			ToolTip = $"{entry.Prompt}\nClick to use this prompt again";
			Layout = Layout.Row();
			Layout.Margin = new Sandbox.UI.Margin( 0, 0, 6, 0 );
			Layout.AddStretchCell();
			if ( open is not null )
				Layout.Add( TaStyle.Icon( this, "open_in_new", open, $"Open {clip.Name}", 26 ) );
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
			if ( e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) _reuse();
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			if ( Paint.HasMouseOver )
			{
				Paint.ClearPen();
				Paint.SetBrush( TaStyle.Accent.WithAlpha( .16f ) );
				Paint.DrawRect( LocalRect, 4 );
			}
			Paint.SetPen( TaStyle.AccentLight );
			Paint.DrawIcon( new Rect( 6, 6, 18, 18 ), "history", 15, TextFlag.Center );
			var width = Width - 40 - (_clip is null ? 8 : 34);
			Paint.SetDefaultFont( 9 );
			Paint.SetPen( Theme.Text );
			Paint.DrawText( new Rect( 30, 5, width, 18 ), _entry.Prompt, TextFlag.LeftCenter | TextFlag.SingleLine );
			Paint.SetDefaultFont( 8 );
			Paint.SetPen( Theme.TextLight );
			var made = _clip is not null ? _clip.Name : string.IsNullOrEmpty( _entry.ClipName ) ? "" : $"{_entry.ClipName} (deleted)";
			var meta = string.Join( "  ·  ", new[] { ModeName( _entry.Mode ), made, Ago( _entry.CreatedUtc ) }.Where( s => s.Length > 0 ) );
			Paint.DrawText( new Rect( 30, 24, width, 16 ), meta, TextFlag.LeftCenter | TextFlag.SingleLine );
		}
	}
}
