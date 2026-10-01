#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.UI;

/// <summary>Shared look of Text to Animation's windows: dark rounded cards on the window gray,
/// framed inputs, tinted pills and blue accents.</summary>
/// <summary>Color roles for pills and status lines.</summary>
public enum Tone { Accent, Amber, Red, Neutral }

public static class TaStyle
{
	public const float ControlHeight = 24f;

	/// <summary>Height of every labelled settings field (inputs and dropdowns alike).</summary>
	public const float FieldHeight = 28f;
	public const float Radius = 4f;

	public static Color ButtonFill => Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, .07f );
	public static Color InputEdge => Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, .17f );

	/// <summary>Inputs sit on the cards' dark gray; a darker fill and a thin edge keep them readable.</summary>
	public static T Framed<T>( T input ) where T : Widget
	{
		input.SetStyles( $"background-color: {Theme.WindowBackground.Hex}; border: 1px solid {InputEdge.Hex}; border-radius: {Radius}px; padding-left: 4px;" );
		input.FixedHeight = ControlHeight;
		return input;
	}

	public static Label Muted( Label label, bool small = false )
	{
		label.SetStyles( small ? $"color: {Theme.TextLight.Hex}; font-size: 11px;" : $"color: {Theme.TextLight.Hex};" );
		return label;
	}

	/// <summary>A labelled settings row: a muted caption of the given width, vertically centered
	/// on the caller's field (see <see cref="Field{T}"/>).</summary>
	public static Layout FieldRow( Widget owner, Layout parent, string caption, float width, string tooltip = null )
	{
		var row = parent.AddRow();
		row.Spacing = 6;
		var label = row.Add( Muted( new Label( caption, owner ) { FixedWidth = width, FixedHeight = FieldHeight, ToolTip = tooltip } ) );
		label.Alignment = TextFlag.LeftCenter;
		return row;
	}

	/// <summary>A framed settings input or dropdown at the shared field height.</summary>
	public static T Field<T>( T input ) where T : Widget
	{
		Framed( input );
		input.FixedHeight = FieldHeight;
		return input;
	}

	/// <summary>
	/// The library's accent: the editor theme's green with its hue turned to blue, so it has exactly the
	/// green's saturation and brightness (the blue counterpart of the retargeter's accent).
	/// </summary>
	public static Color Accent
	{
		get
		{
			var hsv = Theme.Green.ToHsv();
			return hsv.WithHue( AccentHue ).ToColor();
		}
	}

	/// <summary>Hue of <see cref="Accent"/> in degrees.</summary>
	public const float AccentHue = 212f;

	/// <summary>A lighter accent for text on dark fills.</summary>
	public static Color AccentLight => Color.Lerp( Accent, Color.White, .25f );

	/// <summary>Success uses the accent too: the theme is blue, never green.</summary>
	public static Color Success => Accent;

	public static Color Tone( Tone tone ) => tone switch
	{
		UI.Tone.Accent => Accent,
		UI.Tone.Amber => Theme.Yellow,
		UI.Tone.Neutral => Theme.TextLight,
		_ => Theme.Red,
	};

	public static IconButton Icon( Widget parent, string icon, Action clicked, string tooltip, float size = ControlHeight )
		=> new( icon, clicked, parent )
		{
			FixedSize = size,
			IconSize = 15,
			ToolTip = tooltip,
			Background = ButtonFill,
			Foreground = Theme.Text,
			BackgroundActive = TaStyle.Accent.WithAlpha( .2f ),
			ForegroundActive = TaStyle.Accent,
		};

	/// <summary>Square icon toggle (skeleton, source overlay, ground).</summary>
	public static IconButton Toggle( Widget parent, string icon, bool on, Action<bool> toggled, string tooltip )
	{
		var button = Icon( parent, icon, null, tooltip );
		button.IsToggle = true;
		button.IsActive = on;
		button.OnToggled = toggled;
		return button;
	}

	public static Checkbox Check( Layout parent, string text, bool value, Action<bool> changed, string tooltip = null )
	{
		var box = parent.Add( new Checkbox( text ) { Value = value, ToolTip = tooltip } );
		box.Clicked = () => changed( box.Value );
		return box;
	}

	/// <summary>An engine-unit distance for the read-outs (inches).</summary>
	public static string Inches( float value ) => $"{MathF.Abs( value ):0.0} in";
}

/// <summary>A rounded dark-gray panel with an optional header row (icon, title, then controls).</summary>
public class TaCard : Widget
{
	public TaCard( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 6;
	}

	public Layout Header( string icon, string title, string tooltip = null )
	{
		var row = Layout.AddRow();
		row.Spacing = 6;
		row.Add( new HeaderIcon( this, icon ) );
		var label = row.Add( new Label( title, this ) { ToolTip = tooltip, FixedHeight = TaStyle.ControlHeight } );
		label.SetStyles( "font-weight: 600;" );
		return row;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.SetPen( Theme.ControlBackground.Lighten( .25f ), 1 );
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
	}

	sealed class HeaderIcon : Widget
	{
		readonly string _icon;

		public HeaderIcon( Widget parent, string icon ) : base( parent )
		{
			_icon = icon;
			FixedSize = 18;
		}

		protected override void OnPaint()
		{
			Paint.SetPen( TaStyle.Accent );
			Paint.DrawIcon( LocalRect, _icon, 16 );
		}
	}
}

/// <summary>A rounded tinted label: profile and confidence, ground verdicts, counts.</summary>
public sealed class TaPill : Widget
{
	string _text = "";
	Color _color = Theme.TextLight;

	public TaPill( Widget parent, string text, Color color, string tooltip = null ) : base( parent )
	{
		FixedHeight = 18;
		ToolTip = tooltip;
		Set( text, color );
	}

	public void Set( string text, Color color, string tooltip = null )
	{
		text ??= "";
		if ( tooltip is not null )
			ToolTip = tooltip;
		_text = text;
		_color = color;
		FixedWidth = 6.6f * text.Length + 16;
		Visible = text.Length > 0;
		Update();
	}

	protected override void OnPaint()
	{
		if ( string.IsNullOrEmpty( _text ) )
			return;
		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( _color.WithAlpha( 0.18f ) );
		Paint.DrawRect( LocalRect, LocalRect.Height * 0.5f );
		Paint.SetPen( _color );
		Paint.SetDefaultFont( 7, 600 );
		Paint.DrawText( LocalRect, _text );
	}
}

/// <summary>One line of text that is cut short with "…" to the width it is given. A plain label
/// asks for its full text width, so one long line (an error message) widened every row of the
/// clip list past the panel and cut their buttons off.</summary>
public sealed class TaElidedLabel : Widget
{
	string _text = "";
	readonly float _size;
	readonly int _weight;
	Color _color;

	public TaElidedLabel( Widget parent, float size = 9, int weight = 400 ) : base( parent )
	{
		_size = size;
		_weight = weight;
		_color = Theme.Text;
		MinimumWidth = 20;
		FixedHeight = size + 9;
	}

	public string Text
	{
		get => _text;
		set { _text = value ?? ""; Update(); }
	}

	public Color Color
	{
		get => _color;
		set { _color = value; Update(); }
	}

	protected override void OnPaint()
	{
		Paint.SetDefaultFont( _size, _weight );
		Paint.SetPen( _color );
		Paint.DrawText( LocalRect, Paint.GetElidedText( _text, Width, ElideMode.Right, TextFlag.LeftCenter ), TextFlag.LeftCenter );
	}
}

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

/// <summary>Files dropped from disk or the asset browser, filtered by extension.</summary>
public static class TaDrop
{
	public static readonly string[] ModelExtensions = { ".vmdl" };
	public static readonly string[] AnimationExtensions = { ".fbx", ".bvh", ".glb", ".gltf", ".dmx" };

	public static IReadOnlyList<string> Paths( DragData data, string[] extensions )
	{
		bool IsAnimationFile( string path )
			=> !string.IsNullOrEmpty( path ) && extensions.Contains( System.IO.Path.GetExtension( path ).ToLowerInvariant() );
		var paths = new List<string>();
		if ( data is null )
			return paths;
		try
		{
			if ( data.Files is { Length: > 0 } files )
				paths.AddRange( files.Where( IsAnimationFile ) );
			else if ( data.HasFileOrFolder && IsAnimationFile( data.FileOrFolder ) )
				paths.Add( data.FileOrFolder );
			if ( data.Assets is { Count: > 0 } assets )
			{
				foreach ( var asset in assets )
				{
					var path = asset?.AssetPath;
					if ( IsAnimationFile( path ) && AssetSystem.FindByPath( path )?.AbsolutePath is { } absolute )
						paths.Add( absolute );
				}
			}
		}
		catch ( Exception )
		{
			// A drag with nothing we understand.
		}
		return paths.Distinct( StringComparer.OrdinalIgnoreCase ).ToList();
	}
}

/// <summary>The secondary button (the Weapon Importer's): a fill one step lighter than the card,
/// a hairline border and a hover lighten.</summary>
public sealed class TaButton : Widget
{
	string _text;
	readonly string _icon;

	public Action Clicked { get; set; }

	public TaButton( Widget parent, string text, string icon = null, Action clicked = null, string tooltip = null,
		float height = TaStyle.ControlHeight ) : base( parent )
	{
		_text = text ?? "";
		_icon = icon;
		Clicked = clicked;
		ToolTip = tooltip;
		FixedHeight = height;
		Cursor = CursorShape.Finger;
		MouseTracking = true;
		FocusMode = FocusMode.None;
		Measure();
	}

	public string Text
	{
		get => _text;
		set
		{
			_text = value ?? "";
			Measure();
			Update();
		}
	}

	const float PadX = 10f;
	const float IconSize = 16f;
	const float IconGap = 5f;

	void Measure() => FixedWidth = WidthFor( _text.Length == 0 ? 0 : 6.2f * _text.Length );

	float WidthFor( float textWidth )
	{
		if ( _text.Length == 0 )
			return string.IsNullOrEmpty( _icon ) ? PadX * 2 : TaStyle.ControlHeight;
		var icon = string.IsNullOrEmpty( _icon ) ? 0 : IconSize + IconGap;
		return MathF.Ceiling( PadX * 2 + icon + textWidth );
	}

	protected override void OnMouseEnter() => Update();
	protected override void OnMouseLeave() => Update();

	protected override void OnMousePress( MouseEvent e )
	{
		if ( e.LeftMouseButton )
			e.Accepted = true;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( !Enabled || !e.LeftMouseButton || !LocalRect.IsInside( e.LocalPosition ) )
			return;
		Clicked?.Invoke();
		e.Accepted = true;
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		var hover = Paint.HasMouseOver && Enabled;
		var edge = Color.Lerp( Theme.ControlBackground.WithAlpha( 1f ), Color.White, hover ? .25f : .15f );
		Paint.SetPen( edge, 1 );
		Paint.SetBrush( hover ? Color.Lerp( TaStyle.ButtonFill, Color.White, .06f ) : TaStyle.ButtonFill );
		Paint.DrawRect( LocalRect.Shrink( .5f ), TaStyle.Radius );

		Paint.SetPen( Enabled ? Theme.Text : Theme.TextDisabled );
		if ( _text.Length == 0 )
		{
			if ( !string.IsNullOrEmpty( _icon ) )
				Paint.DrawIcon( LocalRect, _icon, 15, TextFlag.Center );
			return;
		}
		Paint.SetDefaultFont( 8 );
		var textWidth = Paint.MeasureText( _text ).x;
		var wanted = WidthFor( textWidth );
		if ( MathF.Abs( wanted - FixedWidth ) > 0.5f )
			FixedWidth = wanted;
		var hasIcon = !string.IsNullOrEmpty( _icon );
		var group = textWidth + (hasIcon ? IconSize + IconGap : 0);
		var x = LocalRect.Left + MathF.Max( PadX, (LocalRect.Width - group) * 0.5f );
		if ( hasIcon )
		{
			Paint.DrawIcon( new Rect( x, LocalRect.Top, IconSize, LocalRect.Height ), _icon, 15, TextFlag.Center );
			x += IconSize + IconGap;
		}
		Paint.DrawText( new Rect( x, LocalRect.Top, LocalRect.Right - x, LocalRect.Height ), _text, TextFlag.LeftCenter );
	}
}

/// <summary>Small uppercase caption with a hairline, separating groups inside a card.</summary>
public sealed class TaSection : Widget
{
	readonly string _text;

	public TaSection( Widget parent, string text ) : base( parent )
	{
		_text = text.ToUpperInvariant();
		FixedHeight = 18;
	}

	protected override void OnPaint()
	{
		Paint.SetDefaultFont( 7, 600 );
		Paint.SetPen( Theme.TextLight );
		var size = Paint.MeasureText( _text );
		Paint.DrawText( new Rect( 0, 0, size.x + 2, Height ), _text, TextFlag.LeftCenter );
		Paint.SetPen( Theme.ControlBackground.Lighten( .45f ), 1 );
		Paint.DrawLine( new Vector2( size.x + 10, Height * .5f ), new Vector2( Width, Height * .5f ) );
	}
}

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
