using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.EditorTools.Engine;
using TextToAnimation.EditorTools.Session;

namespace TextToAnimation.EditorTools.UI;

/// <summary>Choose which of the model's sequences the open animation replaces.</summary>
public sealed class ReplaceDialog : Dialog
{
	public ReplaceDialog( Widget parent, EditorSession session, SaveFlow save ) : base( parent )
	{
		Window.WindowTitle = "Replace an existing animation";
		Window.SetWindowIcon( "swap_horiz" );
		Window.SetModal( true, true );
		MinimumWidth = 500;
		Layout = Layout.Column();
		Layout.Margin = 10;
		Layout.Spacing = 8;
		var clip = session.ActiveClip;
		var defined = session.Sequences.Where( s => s.DefinedInModel ).Select( s => s.Name ).ToList();

		var card = Layout.Add( new TaCard( this ) );
		card.Header( "swap_horiz", "Replace" ).AddStretchCell();
		card.Layout.Add( TaStyle.Muted( new Label( $"\"{clip?.Name}\" will be written over the animation you pick. Its settings (events, fades, activities) are kept; only the motion changes.", card ) { WordWrap = true }, small: true ) );
		var row = TaStyle.FieldRow( card, card.Layout, "Replace", 70f );
		var combo = row.Add( TaStyle.Field( new ComboBox( card ) ), 1 );
		string chosen = null;
		var preferred = clip?.SourceSequence;
		foreach ( var name in defined )
		{
			var n = name;
			combo.AddItem( n, "movie", () => chosen = n, selected: n == preferred );
			if ( n == preferred ) chosen = n;
		}
		chosen ??= defined.FirstOrDefault();
		var inherited = session.Sequences.Count( s => !s.DefinedInModel );
		if ( defined.Count == 0 )
			card.Layout.Add( new Label( "This model's own file defines no animations to replace" + (inherited > 0 ? $" ({inherited} come from its base model or prefabs)." : "."), card ) { WordWrap = true } )
				.SetStyles( $"color: {Theme.Yellow.Hex};" );
		else if ( inherited > 0 )
			card.Layout.Add( TaStyle.Muted( new Label( $"{inherited} inherited animations (base model / prefabs) can't be replaced in place - save as a new animation instead.", card ) { WordWrap = true }, small: true ) );
		card.Layout.Add( new TaSection( card, "Safety" ) );
		card.Layout.Add( TaStyle.Muted( new Label( "A backup of the vmdl is kept in text_to_animation/backups. If the model fails to compile or the animation doesn't play back correctly, the original is restored automatically.", card ) { WordWrap = true }, small: true ) );

		var footer = Layout.AddRow();
		footer.Spacing = 8;
		footer.AddStretchCell();
		footer.Add( new TaButton( this, "Cancel", null, Close, null, 28 ) );
		var replace = footer.Add( new Button.Primary( "Replace" ) { Icon = "swap_horiz", Tint = TaStyle.Accent, FixedHeight = 28, Enabled = defined.Count > 0 && clip is not null } );
		replace.Clicked = () =>
		{
			Close();
			if ( chosen is not null ) _ = save.ReplaceAsync( clip, chosen );
		};
		Window.AdjustSize();
	}
}
