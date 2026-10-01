using System;
using Editor;
using Sandbox;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// Shown in place of the timeline while no animation is open: says what to do next and offers example
/// prompts (fitted to the rig) that fill the prompt box with one click.
/// </summary>
public sealed class QuickStart : Widget
{
	readonly EditorSession _session;
	readonly Widget _chips;
	bool? _humanoid;

	/// <summary>Called with an example prompt when one is clicked.</summary>
	public Action<string> Picked { get; set; }

	static readonly string[] HumanExamples = { "walk forward", "jump in place", "wave hello", "sit down on a chair", "idle, looking around" };
	static readonly string[] CreatureExamples = { "walk forward", "run", "idle, looking around", "turn left", "jump" };

	public QuickStart( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		Layout = Layout.Column();
		Layout.Margin = new Sandbox.UI.Margin( 4, 6, 4, 2 );
		Layout.Spacing = 8;
		var title = Layout.Add( new Label( "Describe a motion above and press Enter, or start from an example:", this ) );
		title.SetStyles( $"color: {Theme.Text.Hex};" );
		_chips = Layout.Add( new Widget( this ) { Layout = Layout.Row() } );
		_chips.Layout.Spacing = 6;
		Layout.Add( TaStyle.Muted( new Label( "To edit an animation the model already has, pick it in the list on the left.", this ), small: true ) );
		Layout.AddStretchCell();
		_session.Changed += c => { if ( (c & SessionChange.Model) != 0 ) Rebuild(); };
		Rebuild();
	}

	/// <summary>The examples on show (fitted to the open model).</summary>
	public string[] Examples => _humanoid == true ? HumanExamples : CreatureExamples;

	/// <summary>Uses example <paramref name="index"/> as if it were clicked.</summary>
	public void Pick( int index ) => Picked?.Invoke( Examples[Math.Clamp( index, 0, Examples.Length - 1 )] );

	void Rebuild()
	{
		var humanoid = _session.HasModel && _session.Rig.IsHumanoid;
		if ( _humanoid == humanoid ) return;
		_humanoid = humanoid;
		_chips.Layout.Clear( true );
		foreach ( var example in humanoid ? HumanExamples : CreatureExamples )
		{
			var text = example;
			_chips.Layout.Add( new ChipButton( _chips, text, "auto_awesome", () => Picked?.Invoke( text ), $"Use \"{text}\" as the prompt" ) { Arrow = false } );
		}
		_chips.Layout.AddStretchCell();
	}
}
