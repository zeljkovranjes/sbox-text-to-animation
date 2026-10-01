using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// The timeline under the viewport: a ruler with the playhead, a range selection, and three lanes - pinned
/// frames (poses kept when regenerating), keys (pose edits of the selected bones, or of all bones) and events
/// (footsteps). Click or drag to scrub, Shift+drag to select a range, drag a key to move it, right click for
/// every timeline action, wheel to zoom.
/// </summary>
public sealed class TimelineWidget : Widget
{
	readonly EditorSession _session;
	public Action<Menu, int> BuildContextMenu { get; set; }

	const float RulerHeight = 22f;
	const float LaneHeight = 18f;
	const float LabelWidth = 64f;
	static readonly string[] Lanes = { "Pinned", "Keys", "Events" };

	float _zoom = 1f;      // 1 = whole clip fits
	float _scroll;         // first visible frame
	enum Drag { None, Scrub, Range, Key }
	Drag _drag;
	int _dragStartFrame;
	int _keyFrom, _keyTo;

	public TimelineWidget( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		MinimumHeight = RulerHeight + LaneHeight * Lanes.Length + 10;
		MouseTracking = true;
		FocusMode = FocusMode.Click;
		_session.Changed += c => { if ( (c & (SessionChange.Playhead | SessionChange.ClipData | SessionChange.ActiveClip | SessionChange.Selection)) != 0 ) Update(); };
	}

	int FrameCount => Math.Max( 1, _session.ActiveClip?.FrameCount ?? 1 );
	float TrackLeft => LabelWidth;
	float TrackWidth => Math.Max( 10f, Width - LabelWidth - 8f );
	float VisibleFrames => Math.Max( 4f, (FrameCount - 1) / _zoom );
	float PixelsPerFrame => TrackWidth / Math.Max( 1f, VisibleFrames );

	float FrameToX( float frame ) => TrackLeft + (frame - _scroll) * PixelsPerFrame;
	float XToFrame( float x ) => _scroll + (x - TrackLeft) / PixelsPerFrame;
	int SnapFrame( float x ) => Math.Clamp( (int)MathF.Round( XToFrame( x ) ), 0, FrameCount - 1 );

	IEnumerable<int> KeyFrames()
	{
		var clip = _session.ActiveClip;
		if ( clip is null ) return Enumerable.Empty<int>();
		if ( _session.SelectedBones.Count == 0 ) return clip.Keys.AllKeyFrames;
		var names = _session.SelectedBones.Select( b => _session.Rig.Skeleton[b].Name ).ToList();
		return names.SelectMany( clip.Keys.KeyFrames ).Distinct().OrderBy( f => f );
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground );
		Paint.DrawRect( LocalRect, 4 );

		var clip = _session.ActiveClip;
		if ( clip is null || clip.FrameCount == 0 )
		{
			Paint.SetPen( Theme.TextLight );
			Paint.SetDefaultFont( 8 );
			Paint.DrawText( LocalRect, "No animation selected", TextFlag.Center );
			return;
		}

		var fps = clip.Fps;
		var trackRect = new Rect( TrackLeft, 0, TrackWidth, Height );

		// range selection
		if ( _session.Range is { } range )
		{
			Paint.SetBrush( TaStyle.Accent.WithAlpha( .14f ) );
			var x0 = FrameToX( range.Start ); var x1 = FrameToX( range.End );
			Paint.DrawRect( new Rect( x0, 0, Math.Max( 2, x1 - x0 ), Height ) );
		}

		// ruler ticks: choose a step that keeps labels ~60px apart
		Paint.SetDefaultFont( 7 );
		var step = NiceStep( 60f / PixelsPerFrame );
		var first = (int)MathF.Floor( _scroll / step ) * step;
		for ( var f = first; f <= Math.Min( FrameCount - 1, _scroll + VisibleFrames + step ); f += step )
		{
			if ( f < 0 ) continue;
			var x = FrameToX( f );
			if ( x < TrackLeft - 1 || x > Width ) continue;
			Paint.SetPen( Color.White.WithAlpha( .12f ), 1 );
			Paint.DrawLine( new Vector2( x, RulerHeight - 6 ), new Vector2( x, Height ) );
			Paint.SetPen( Theme.TextLight );
			Paint.DrawText( new Rect( x + 3, 2, 70, RulerHeight - 6 ), $"{f}  {f / fps:0.0#}s", TextFlag.LeftCenter );
		}

		// lanes
		for ( var lane = 0; lane < Lanes.Length; lane++ )
		{
			var y = RulerHeight + lane * LaneHeight;
			Paint.SetPen( Color.White.WithAlpha( .05f ), 1 );
			Paint.DrawLine( new Vector2( 0, y ), new Vector2( Width, y ) );
			Paint.SetPen( Theme.TextLight );
			Paint.SetDefaultFont( 7, 600 );
			Paint.DrawText( new Rect( 8, y, LabelWidth - 10, LaneHeight ), Lanes[lane].ToUpperInvariant(), TextFlag.LeftCenter );
		}

		// pinned frames
		foreach ( var pin in clip.PinnedFrames ) Diamond( FrameToX( pin ), RulerHeight + LaneHeight * 0.5f, 5f, Theme.Yellow );
		// keys
		var keyColor = _session.SelectedBones.Count == 0 ? TaStyle.Accent.WithAlpha( .7f ) : TaStyle.AccentLight;
		foreach ( var key in KeyFrames() )
		{
			var x = _drag == Drag.Key && key == _keyFrom ? FrameToX( _keyTo ) : FrameToX( key );
			Diamond( x, RulerHeight + LaneHeight * 1.5f, 5f, keyColor );
		}
		// events
		foreach ( var e in clip.Events )
		{
			var x = FrameToX( e.Frame );
			var y = RulerHeight + LaneHeight * 2f;
			Paint.ClearPen();
			Paint.SetBrush( (e.Foot == "1" ? Color.Lerp( TaStyle.Accent, Color.White, .4f ) : TaStyle.Accent).WithAlpha( .85f ) );
			Paint.DrawRect( new Rect( x - 1.5f, y + 3, 3, LaneHeight - 6 ), 1 );
		}

		// playhead
		var px = FrameToX( _session.Playhead );
		Paint.SetPen( TaStyle.Accent, 2 );
		Paint.DrawLine( new Vector2( px, 0 ), new Vector2( px, Height ) );
		Paint.ClearPen();
		Paint.SetBrush( TaStyle.Accent );
		Paint.DrawRect( new Rect( px - 14, 1, 28, 14 ), 3 );
		Paint.SetPen( Color.White );
		Paint.SetDefaultFont( 7, 600 );
		Paint.DrawText( new Rect( px - 14, 1, 28, 14 ), _session.CurrentFrame.ToString(), TextFlag.Center );
		Paint.SetPen( Theme.ControlBackground.Lighten( .4f ), 1 );
		Paint.ClearBrush();
		Paint.DrawRect( LocalRect.Shrink( .5f ), 4 );
	}

	static void Diamond( float x, float y, float r, Color color )
	{
		Paint.ClearPen();
		Paint.SetBrush( color );
		Paint.DrawPolygon( new[] { new Vector2( x, y - r ), new Vector2( x + r, y ), new Vector2( x, y + r ), new Vector2( x - r, y ) } );
	}

	static int NiceStep( float framesPerLabel )
	{
		foreach ( var s in new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 } )
			if ( s >= framesPerLabel ) return s;
		return 1200;
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( _session.ActiveClip is null ) return;
		if ( e.RightMouseButton )
		{
			var frame = SnapFrame( e.LocalPosition.x );
			var menu = new Menu( this );
			BuildContextMenu?.Invoke( menu, frame );
			menu.OpenAtCursor();
			return;
		}
		if ( !e.LeftMouseButton ) return;
		var mx = e.LocalPosition.x;
		var f = SnapFrame( mx );
		var lane = (int)((e.LocalPosition.y - RulerHeight) / LaneHeight);
		if ( lane == 1 && KeyFrames().Any( k => MathF.Abs( FrameToX( k ) - mx ) < 6 ) )
		{
			_drag = Drag.Key;
			_keyFrom = _keyTo = KeyFrames().OrderBy( k => MathF.Abs( FrameToX( k ) - mx ) ).First();
			return;
		}
		if ( e.HasShift )
		{
			_drag = Drag.Range;
			_dragStartFrame = f;
			_session.SetRange( f, f );
			return;
		}
		_drag = Drag.Scrub;
		_session.Playing = false;
		_session.Seek( f );
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		var f = SnapFrame( e.LocalPosition.x );
		switch ( _drag )
		{
			case Drag.Scrub: _session.Seek( f ); break;
			case Drag.Range: _session.SetRange( _dragStartFrame, f ); break;
			case Drag.Key: _keyTo = f; Update(); break;
		}
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _drag == Drag.Key && _keyTo != _keyFrom )
		{
			var bones = _session.SelectedBones.Count == 0 ? null : _session.SelectedBones.Select( b => _session.Rig.Skeleton[b].Name ).ToList();
			var from = _keyFrom; var to = _keyTo;
			_session.Edit( $"Move keys {from} → {to}", c => c.Keys.MoveKeys( from, to, bones ) );
		}
		_drag = Drag.None;
		Update();
	}

	protected override void OnWheel( WheelEvent e )
	{
		base.OnWheel( e );
		var anchor = XToFrame( e.Position.x );
		_zoom = Math.Clamp( _zoom * (e.Delta > 0 ? 1.15f : 1f / 1.15f), 1f, Math.Max( 1f, FrameCount / 8f ) );
		_scroll = Math.Clamp( anchor - (e.Position.x - TrackLeft) / PixelsPerFrame, 0, Math.Max( 0, FrameCount - 1 - VisibleFrames ) );
		Update();
		e.Accept();
	}

	protected override void OnDoubleClick( MouseEvent e )
	{
		base.OnDoubleClick( e );
		var lane = (int)((e.LocalPosition.y - RulerHeight) / LaneHeight);
		var f = SnapFrame( e.LocalPosition.x );
		if ( lane == 0 ) TogglePin( f );
	}

	public void TogglePin( int frame )
	{
		_session.Edit( _session.ActiveClip?.PinnedFrames.Contains( frame ) == true ? $"Unpin frame {frame}" : $"Pin frame {frame}",
			c => { if ( !c.PinnedFrames.Remove( frame ) ) c.PinnedFrames.Add( frame ); } );
	}

	public void ResetZoom() { _zoom = 1f; _scroll = 0; Update(); }
}
