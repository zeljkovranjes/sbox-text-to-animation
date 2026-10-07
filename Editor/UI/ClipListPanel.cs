using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// Left column: the animations of the open model's workspace. Click to open, double click to rename,
/// right click for clip actions. A dot shows whether the clip's latest version is saved into the model.
/// </summary>
public sealed class ClipListPanel : TaCard
{
	readonly EditorSession _session;
	readonly TaPill _count;
	readonly ScrollArea _scroll;
	readonly Widget _canvas;
	readonly Label _empty;
	readonly Widget _emptyHost;

	public Action NewClip { get; set; }
	public Action ImportExisting { get; set; }
	public Action<AnimClip> SaveClip { get; set; }
	public Action<AnimClip> ExportClip { get; set; }

	public ClipListPanel( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		var header = Header( "movie", "Animations" );
		_count = header.Add( new TaPill( this, "", Theme.TextLight ) );
		header.AddStretchCell();
		header.Add( TaStyle.Icon( this, "add", () => NewClip?.Invoke(), "New animation", 22 ) );
		header.Add( TaStyle.Icon( this, "download", () => ImportExisting?.Invoke(), "Import an existing animation from the model", 22 ) );

		// the empty-list hint, centred in the panel in place of the list
		_emptyHost = Layout.Add( new Widget( this ) { Layout = Layout.Column() }, 1 );
		_emptyHost.Layout.Margin = new Sandbox.UI.Margin( 12, 0, 12, 0 );
		_emptyHost.Layout.AddStretchCell();
		_empty = _emptyHost.Layout.Add( TaStyle.Muted( new Label( "", _emptyHost ) { WordWrap = true, Alignment = TextFlag.Center } ) );
		_emptyHost.Layout.AddStretchCell();
		_scroll = Layout.Add( new ScrollArea( this ), 1 );
		_scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		_scroll.SetStyles( "background-color: transparent;" );
		_canvas = new Widget( _scroll ) { Layout = Layout.Column() };
		_canvas.SetStyles( "background-color: transparent;" );
		_canvas.Layout.Spacing = 4;
		// equal on both sides: the scroll area keeps its own room for the scrollbar
		_canvas.Layout.Margin = new Sandbox.UI.Margin( 0, 0, 0, 0 );
		_scroll.Canvas = _canvas;

		_session.Changed += c =>
		{
			if ( (c & (SessionChange.ClipList | SessionChange.ActiveClip | SessionChange.Model)) != 0 ) Rebuild();
			else if ( (c & (SessionChange.ClipData | SessionChange.Busy)) != 0 ) foreach ( var row in _canvas.Children.OfType<ClipRow>() ) row.Update();
		};
		Rebuild();
	}

	/// <summary>True while the list is empty and shows its centred hint.</summary>
	public bool ShowsEmptyHint => _emptyHost.Visible;

	public void Rebuild()
	{
		_canvas.Layout.Clear( true );
		var ws = _session.Workspace;
		var clips = ws?.Clips ?? new List<AnimClip>();
		_count.Set( clips.Count == 0 ? "" : $"{clips.Count}", Theme.TextLight );
		_emptyHost.Visible = clips.Count == 0;
		_scroll.Visible = clips.Count > 0;
		_empty.Text = ws is null
			? "Choose a model to start."
			: "No animations yet.\nPress + to create one, or import one the model already has.";
		foreach ( var clip in clips ) _canvas.Layout.Add( new ClipRow( this, clip ) );
		_canvas.Layout.AddStretchCell();
	}

	internal void ShowMenu( AnimClip clip )
	{
		var menu = new Menu( this );
		menu.AddOption( "Rename…", "edit", () => Rename( clip ) );
		menu.AddOption( "Duplicate", "content_copy", () => _session.Duplicate( clip ) );
		menu.AddSeparator();
		menu.AddOption( "Save to VMDL", "save", () => SaveClip?.Invoke( clip ) );
		menu.AddOption( "Export…", "file_download", () => ExportClip?.Invoke( clip ) );
		menu.AddSeparator();
		menu.AddOption( "Delete", "delete", () =>
			Dialog.AskConfirm( () => _session.Delete( clip ),
				$"Delete \"{clip.Name}\" from this workspace? Anything already saved into the model stays in the model.",
				"Delete animation", "Delete", "Cancel" ) );
		menu.OpenAtCursor();
	}

	internal void Rename( AnimClip clip )
	{
		TaPrompt.Ask( this, "Rename animation", "Name", clip.Name, name => _session.Rename( clip, name ) );
	}

	sealed class ClipRow : Widget
	{
		readonly ClipListPanel _panel;
		readonly AnimClip _clip;
		readonly TaElidedLabel _name;
		readonly TaElidedLabel _details;

		public ClipRow( ClipListPanel panel, AnimClip clip ) : base( panel._canvas )
		{
			_panel = panel;
			_clip = clip;
			FixedHeight = 44;
			MouseTracking = true;
			Cursor = CursorShape.Finger;
			Layout = Layout.Column();
			// text clear of the origin icon on the left (6 + 18 + 4) and of the saved-state dot on the right (4 + 12 + 4)
			Layout.Margin = new Sandbox.UI.Margin( 28, 5, 20, 5 );
			Layout.Spacing = 1;
			_name = Layout.Add( new TaElidedLabel( this, 9, 600 ) );
			_details = Layout.Add( new TaElidedLabel( this, 8 ) { Color = Theme.TextLight } );
			Refresh();
		}

		void Refresh()
		{
			_name.Text = _clip.Name;
			var origin = _clip.Origin switch
			{
				ClipOrigin.Generated => "Generated",
				ClipOrigin.Imported => _clip.SourceSequence is { } s ? $"From {s}" : "Imported",
				ClipOrigin.ImportedFile => "Imported",
				ClipOrigin.Duplicated => "Copy",
				_ => "New",
			};
			_details.Text = $"{_clip.Duration:0.0#} s  ·  {_clip.Fps:0} fps  ·  {origin}{(_clip.Looping ? "  ·  Loop" : "")}";
			ToolTip = _clip.Name + "\n" + (_clip.SavedUtc is null ? "Not saved into the model yet" : $"Saved into the model as \"{_clip.EffectiveSequenceName}\"");
		}

		bool Active => _panel._session.ActiveClip == _clip;

		protected override void OnPaint()
		{
			Refresh();
			Paint.Antialiasing = true;
			var hover = Paint.HasMouseOver;
			Paint.SetPen( Active ? TaStyle.Accent.WithAlpha( .8f ) : Theme.ControlBackground.Lighten( .4f ), 1 );
			Paint.SetBrush( Active ? TaStyle.Accent.WithAlpha( .12f ) : hover ? Theme.WindowBackground.Lighten( .35f ) : Theme.WindowBackground );
			Paint.DrawRect( LocalRect.Shrink( .5f ), 5 );
			var icon = _clip.Origin == ClipOrigin.Generated ? "auto_awesome" : _clip.Origin is ClipOrigin.Imported or ClipOrigin.ImportedFile ? "download" : "movie";
			Paint.SetPen( Active ? TaStyle.AccentLight : Theme.TextLight );
			Paint.DrawIcon( new Rect( 6, (Height - 18) * .5f, 18, 18 ), icon, 16 );
			// saved state: blue = saved and unchanged, amber = changed since saved, none = never saved
			var dirty = _clip.SavedUtc is not null && _clip.Revision > 0 && _savedRevision( _clip ) != _clip.Revision;
			if ( _clip.SavedUtc is not null )
			{
				Paint.ClearPen();
				Paint.SetBrush( dirty ? Theme.Yellow : TaStyle.Accent );
				Paint.DrawCircle( new Vector2( Width - 10, 10 ), 6 );
			}
		}

		static int _savedRevision( AnimClip clip ) => SavedRevisions.TryGetValue( clip.Id, out var r ) ? r : clip.Revision;

		protected override void OnMouseClick( MouseEvent e )
		{
			base.OnMouseClick( e );
			if ( e.LeftMouseButton ) _panel._session.SelectClip( _clip );
		}

		protected override void OnMousePress( MouseEvent e )
		{
			base.OnMousePress( e );
			if ( e.RightMouseButton )
			{
				_panel._session.SelectClip( _clip );
				_panel.ShowMenu( _clip );
			}
		}

		protected override void OnDoubleClick( MouseEvent e )
		{
			base.OnDoubleClick( e );
			_panel.Rename( _clip );
		}
	}

	/// <summary>Clip revision at its last save into the model (for the "changed since saved" dot).</summary>
	public static readonly Dictionary<Guid, int> SavedRevisions = new();
}
