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
/// (footsteps). Click or drag the ruler to scrub; drag across the lanes to highlight frames, and the range bar above the
/// timeline keeps, deletes, repeats or reverses them; drag a key to move it; right click for every
/// timeline action (split, trim before/after, reverse…); wheel to zoom.
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
	enum Drag { None, Scrub, Pending, Range, Key }
	Drag _drag;
	int _dragStartFrame;
	float _pressX;
	int? _anchor; // the first point of a two-click highlight
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
		var started = FrameProbe.Now;
		try { PaintTimeline(); }
		finally { FrameProbe.Add( "timeline paint", FrameProbe.Now - started ); }
	}

	void PaintTimeline()
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
			// only the lanes: the ruler numbers and the lane labels stay clear
			var x0 = MathF.Max( FrameToX( range.Start ), TrackLeft ); var x1 = MathF.Max( FrameToX( range.End ), TrackLeft );
			Paint.ClearPen();
			Paint.SetBrush( TaStyle.Accent.WithAlpha( .2f ) );
			Paint.DrawRect( new Rect( x0, RulerHeight, Math.Max( 2, x1 - x0 ), Height - RulerHeight ) );
			foreach ( var x in new[] { FrameToX( range.Start ), FrameToX( range.End ) } )
			{
				if ( x < TrackLeft ) continue;
				Paint.SetPen( TaStyle.AccentLight, 2 );
				Paint.DrawLine( new Vector2( x, RulerHeight ), new Vector2( x, Height ) );
				Paint.ClearPen();
				Paint.SetBrush( TaStyle.AccentLight );
				Paint.DrawRect( new Rect( x - 3, Height - 16, 6, 12 ), 2 ); // grab handle
			}
		}
		else if ( _anchor is int a && _drag != Drag.Range )
		{
			// the first point of a two-point highlight
			var ax = FrameToX( a );
			Paint.SetPen( TaStyle.AccentLight.WithAlpha( .5f ), 1 );
			Paint.DrawLine( new Vector2( ax, RulerHeight ), new Vector2( ax, Height ) );
		}

		// ruler ticks: choose a step that keeps labels ~60px apart
		Paint.SetDefaultFont( 7 );
		var step = NiceStep( 60f / PixelsPerFrame );
		var first = (int)MathF.Floor( _scroll / step ) * step;
		var badge = FrameToX( _session.Playhead );
		for ( var f = first; f <= Math.Min( FrameCount - 1, _scroll + VisibleFrames + step ); f += step )
		{
			if ( f < 0 ) continue;
			var x = FrameToX( f );
			if ( x < TrackLeft - 1 || x > Width ) continue;
			Paint.SetPen( Color.White.WithAlpha( .12f ), 1 );
			Paint.DrawLine( new Vector2( x, RulerHeight - 6 ), new Vector2( x, Height ) );
			// labels never hide under the playhead badge or run off the right edge
			var label = $"{f}  {f / fps:0.0#}s";
			var w = Paint.MeasureText( label ).x;
			if ( x + 3 + w > Width - 4 ) continue;
			if ( x + 3 + w > badge - 16 && x + 3 < badge + 16 ) continue;
			Paint.SetPen( Theme.TextLight );
			Paint.DrawText( new Rect( x + 3, 2, w + 2, RulerHeight - 6 ), label, TextFlag.LeftCenter );
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
		_session.Playing = false;
		// grab an edge of the highlight to adjust it
		if ( _session.Range is { } r )
		{
			if ( MathF.Abs( mx - FrameToX( r.Start ) ) < 6 ) { _drag = Drag.Range; _dragStartFrame = r.End; e.Accepted = true; return; }
			if ( MathF.Abs( mx - FrameToX( r.End ) ) < 6 ) { _drag = Drag.Range; _dragStartFrame = r.Start; e.Accepted = true; return; }
		}
		if ( e.HasShift )
		{
			// two points: highlight from the first click (or the playhead) to here; dragging keeps adjusting
			var from = _anchor ?? _session.CurrentFrame;
			_drag = Drag.Range;
			_dragStartFrame = from;
			_session.SetRange( Math.Min( from, f ), Math.Max( from, f ) );
			e.Accepted = true;
				return;
		}
		if ( e.LocalPosition.y > RulerHeight )
		{
			// in the lanes: a click scrubs and sets the first point, a drag highlights frames
			_drag = Drag.Pending;
			_dragStartFrame = f;
			_anchor = f;
			_pressX = mx;
			_session.Seek( f );
			e.Accepted = true;
			return;
		}
		_drag = Drag.Scrub;
		_session.Seek( f );
		e.Accepted = true;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		var f = SnapFrame( e.LocalPosition.x );
		if ( _drag == Drag.None )
			Cursor = _session.Range is { } hr && (MathF.Abs( e.LocalPosition.x - FrameToX( hr.Start ) ) < 6 || MathF.Abs( e.LocalPosition.x - FrameToX( hr.End ) ) < 6)
				? CursorShape.SizeH : CursorShape.Arrow;
		switch ( _drag )
		{
			case Drag.Scrub: _session.Seek( f ); break;
			case Drag.Pending when MathF.Abs( e.LocalPosition.x - _pressX ) > 4:
				_drag = Drag.Range;
				_session.SetRange( Math.Min( _dragStartFrame, f ), Math.Max( _dragStartFrame, f ) );
				break;
			case Drag.Range: _session.SetRange( Math.Min( _dragStartFrame, f ), Math.Max( _dragStartFrame, f ) ); break;
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
		if ( _drag == Drag.Pending ) _session.SetRange( null, null ); // a plain click clears the highlight
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

	/// <summary>Sets the start (in) or end (out) of the highlight at <paramref name="frame"/> (I / O keys).</summary>
	public void SetInOut( int frame, bool isIn )
	{
		var r = _session.Range;
		var start = isIn ? frame : r?.Start ?? _anchor ?? frame;
		var end = isIn ? r?.End ?? frame : frame;
		if ( start > end ) (start, end) = (end, start);
		if ( isIn ) _anchor = frame;
		if ( start == end ) { _anchor = frame; Update(); return; } // one point so far: it waits for the other
		_session.SetRange( start, end );
	}

	/// <summary>Runs an edit on the highlighted frames, then clears the highlight.</summary>
	public static void RangeEdit( EditorSession session, string label, Action<AnimClip, int, int> op )
	{
		if ( session.Range is not { } r ) return;
		session.Edit( label, c => op( c, r.Start, r.End ) );
		session.SetRange( null, null );
	}

	/// <summary>Copies the highlighted frames into a new animation next to this one.</summary>
	public static void CopyRangeToNewClip( EditorSession session )
	{
		if ( session.Range is not { } r || session.ActiveClip is not { } clip ) return;
		var copy = clip.Duplicate( $"{clip.Name} ({r.Start}-{r.End})" );
		ClipOps.Crop( copy, session.Rig, r.Start, r.End );
		session.SetRange( null, null );
		session.AddClip( copy );
	}
}
