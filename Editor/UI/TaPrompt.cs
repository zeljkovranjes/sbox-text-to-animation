using System;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>A small modal text prompt in the library's style (rename, sequence names).</summary>
public static class TaPrompt
{
	public static void Ask( Widget parent, string title, string caption, string initial, Action<string> accepted )
	{
		var dialog = new Dialog( parent );
		dialog.Window.WindowTitle = title;
		dialog.Window.SetWindowIcon( "edit" );
		dialog.Window.SetModal( true, true );
		dialog.MinimumWidth = 380;
		dialog.Layout = Layout.Column();
		dialog.Layout.Margin = 10;
		dialog.Layout.Spacing = 8;
		var card = dialog.Layout.Add( new TaCard( dialog ) );
		var row = TaStyle.FieldRow( card, card.Layout, caption, 70f );
		var edit = row.Add( TaStyle.Field( new LineEdit( card ) { Text = initial ?? "" } ), 1 );
		var buttons = dialog.Layout.AddRow();
		buttons.Spacing = 8;
		buttons.AddStretchCell();
		buttons.Add( new TaButton( dialog, "Cancel", null, () => dialog.Close(), null, 28 ) );
		void Accept()
		{
			var text = edit.Text?.Trim();
			if ( string.IsNullOrEmpty( text ) ) return;
			dialog.Close();
			accepted( text );
		}
		buttons.Add( new Button.Primary( "OK" ) { Icon = "check", Tint = TaStyle.Accent, FixedHeight = 28, Clicked = Accept } );
		edit.ReturnPressed += Accept;
		dialog.Window.AdjustSize();
		dialog.Show();
		edit.Focus();
		edit.SelectAll();
	}
}
