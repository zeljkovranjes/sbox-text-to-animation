using System;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// The "?" card: how the timeline works and every keyboard shortcut, so nothing is only discoverable by hovering.
/// </summary>
public sealed class HelpPopup : PopupWidget
{
	/// <summary>How prompts are written (as UniMate's captions are).</summary>
	public static readonly (string Action, string How)[] Prompts =
	{
		("One animation", "One sentence for the whole motion: \"An object walks forward.\""),
		("A sequence", "One step per line (Shift+Enter): each line continues from the one before"),
	};

	/// <summary>Mouse gestures in the 3D preview.</summary>
	public static readonly (string Action, string How)[] Preview =
	{
		("Look around", "Drag empty space, or right-drag anywhere"),
		("Pan / zoom", "Middle-drag pans, the wheel zooms"),
		("Reframe the character", "Double-click empty space"),
		("Pick a bone", "Click its dot"),
	};

	/// <summary>Mouse gestures on the timeline.</summary>
	public static readonly (string Action, string How)[] Timeline =
	{
		("Highlight frames", "Drag across the lanes, or click one point and Shift+click another"),
		("Adjust a highlight", "Drag its edges"),
		("Act on a highlight", "Keep, Delete, Repeat or Reverse in the row above the timeline"),
		("Split or trim", "Right click a frame"),
		("Pin a pose", "Double click the Pinned lane (pinned poses survive regeneration)"),
		("Move a key", "Drag it along the Keys lane"),
		("Zoom", "Mouse wheel over the timeline"),
	};

	/// <summary>Keyboard shortcuts (the window handles every one of these).</summary>
	public static readonly (string Keys, string Action)[] Keys =
	{
		("Enter", "Send the prompt (Shift+Enter for a new line)"),
		("Space", "Play / pause"),
		("Left / Right", "Previous / next frame"),
		("Home / End", "First / last frame"),
		("I / O", "Highlight from / to the playhead"),
		("Esc", "Clear the highlight and the bone selection"),
		("K", "Key the selected bones on this frame"),
		("P", "Pin / unpin this frame"),
		("Ctrl+Z / Ctrl+Y", "Undo / redo"),
		("Ctrl+D", "Duplicate the animation"),
		("Ctrl+S", "Save the animation into the model"),
		("F1", "This help"),
	};

	public HelpPopup( Widget parent ) : base( parent )
	{
		FixedWidth = 470;
		WindowTitle = "Text to Animation - Help";
		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 4;
		Section( "Prompts", Prompts );
		Section( "Preview", Preview );
		Section( "Timeline", Timeline );
		Layout.AddSpacingCell( 8 );
		Section( "Keyboard", Keys );
		AdjustSize();
	}

	void Section( string title, (string, string)[] rows )
	{
		var header = Layout.Add( new Label( title, this ) );
		header.SetStyles( $"font-weight: 600; color: {TaStyle.AccentLight.Hex};" );
		foreach ( var (left, right) in rows )
		{
			var row = Layout.AddRow();
			row.Spacing = 10;
			var key = row.Add( new Label( left, this ) { FixedWidth = 130 } );
			key.SetStyles( "font-weight: 600;" );
			row.Add( TaStyle.Muted( new Label( right, this ) { WordWrap = true } ), 1 );
		}
	}

	/// <summary>Opens above <paramref name="anchor"/>, right-aligned to it.</summary>
	public void OpenAbove( Widget anchor )
	{
		var r = anchor.ScreenRect;
		OpenAt( new Vector2( r.Right - Width, r.Top - Height - 8 ), animateOffset: new Vector2( 0, 8 ) );
	}
}
