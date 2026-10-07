using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// Tabs in the style of the inspector's feature tabs: the selected tab is a raised, lighter box with a blue
/// icon and a sliding blue underline; the others are dimmed text that brightens on hover.
/// </summary>
public sealed class TaTabBar : Widget
{
	readonly List<Tab> _tabs = new();
	int _selected;
	float _underlineX, _underlineW;
	bool _underlineInit;

	public Action<int> SelectedChanged { get; set; }

	/// <summary>Tabs share the width evenly (side panels) instead of fitting their text (top bar).</summary>
	public bool Stretch { get; set; }

	public TaTabBar( Widget parent, float height = 32 ) : base( parent )
	{
		FixedHeight = height;
		Layout = Layout.Row();
		Layout.Spacing = 2;
	}

	public void Add( string text, string icon, string tooltip = null )
	{
		var tab = new Tab( this, _tabs.Count, text, icon ) { ToolTip = tooltip };
		_tabs.Add( tab );
		Layout.Add( tab, Stretch ? 1 : 0 );
	}

	public int SelectedIndex
	{
		get => _selected;
		set
		{
			value = Math.Clamp( value, 0, Math.Max( 0, _tabs.Count - 1 ) );
			if ( value == _selected ) return;
			_selected = value;
			Update();
			foreach ( var t in _tabs ) t.Update();
			SelectedChanged?.Invoke( value );
		}
	}

	public void SetEnabled( int index, bool enabled, string tooltip = null )
	{
		if ( index < 0 || index >= _tabs.Count ) return;
		_tabs[index].Enabled = enabled;
		if ( tooltip is not null ) _tabs[index].ToolTip = tooltip;
		_tabs[index].Update();
	}

	protected override void OnPaint()
	{
		if ( _tabs.Count == 0 ) return;
		var target = _tabs[_selected];
		var x = target.Position.x + 10;
		var w = target.Width - 20;
		if ( !_underlineInit ) { _underlineX = x; _underlineW = w; _underlineInit = true; }
		_underlineX = MathX.Lerp( _underlineX, x, MathF.Min( 1f, 18f * RealTime.Delta ) );
		_underlineW = MathX.Lerp( _underlineW, w, MathF.Min( 1f, 18f * RealTime.Delta ) );
		if ( MathF.Abs( _underlineX - x ) > .5f || MathF.Abs( _underlineW - w ) > .5f ) Update();
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( TaStyle.Accent );
		Paint.DrawRect( new Rect( _underlineX, Height - 2, _underlineW, 2 ), 1 );
	}

	sealed class Tab : Widget
	{
		readonly TaTabBar _bar;
		readonly int _index;
		readonly string _text;
		readonly string _icon;

		public Tab( TaTabBar bar, int index, string text, string icon ) : base( bar )
		{
			_bar = bar;
			_index = index;
			_text = text;
			_icon = icon;
			Cursor = CursorShape.Finger;
			MouseTracking = true;
			Paint.SetDefaultFont( 9, 500 );
			MinimumWidth = MathF.Ceiling( 14 + 18 + 6 + Paint.MeasureText( text ).x + 16 );
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
			if ( Enabled && e.LeftMouseButton && LocalRect.IsInside( e.LocalPosition ) ) _bar.SelectedIndex = _index;
		}

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;
			Paint.TextAntialiasing = true;
			var selected = _bar._selected == _index;
			var alpha = selected ? 1f : !Enabled ? .2f : Paint.HasMouseOver ? .75f : .45f;
			if ( selected )
			{
				Paint.ClearPen();
				Paint.SetBrush( Color.White.WithAlpha( .06f ) );
				Paint.DrawRect( LocalRect.Shrink( 0, 2, 0, 0 ), 6 );
			}
			else if ( Paint.HasMouseOver && Enabled )
			{
				Paint.ClearPen();
				Paint.SetBrush( Color.White.WithAlpha( .03f ) );
				Paint.DrawRect( LocalRect.Shrink( 0, 2, 0, 0 ), 6 );
			}
			Paint.SetDefaultFont( 9, selected ? 600 : 500 );
			var textWidth = Paint.MeasureText( _text ).x;
			var group = 18 + 6 + textWidth;
			var x = MathF.Max( 8, (Width - group) * .5f );
			Paint.SetPen( (selected ? TaStyle.AccentLight : Theme.Text).WithAlpha( alpha ) );
			Paint.DrawIcon( new Rect( x, 0, 18, Height ), _icon, 16, TextFlag.Center );
			Paint.SetPen( Theme.Text.WithAlpha( alpha ) );
			Paint.DrawText( new Rect( x + 24, 0, textWidth + 4, Height ), _text, TextFlag.LeftCenter );
		}
	}
}
