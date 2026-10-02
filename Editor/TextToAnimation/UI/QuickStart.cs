using System;
using System.Linq;
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

	// written as UniMate's captions are ("An object <does something>.": its README's advice for prompts), which is
	// what the model was trained on; the chip shows the short label
	static readonly (string Label, string Prompt)[] HumanExamples =
	{
		("walk forward", "An object walks forward."), ("jump in place", "An object jumps in place."),
		("wave hello", "An object waves hello."), ("sit down", "An object sits down on a chair."),
		("look around", "An object stands idle and looks around."),
	};
	static readonly (string Label, string Prompt)[] CreatureExamples =
	{
		("walk forward", "An object walks forward."), ("run", "An object runs forward."),
		("look around", "An object stands idle and looks around."), ("turn left", "An object turns to the left."),
		("jump", "An object jumps."),
	};

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
	public string[] Examples => (_humanoid == true ? HumanExamples : CreatureExamples).Select( e => e.Prompt ).ToArray();

	/// <summary>Uses example <paramref name="index"/> as if it were clicked.</summary>
	public void Pick( int index ) => Picked?.Invoke( Examples[Math.Clamp( index, 0, Examples.Length - 1 )] );

	void Rebuild()
	{
		var humanoid = _session.HasModel && _session.Rig.IsHumanoid;
		if ( _humanoid == humanoid ) return;
		_humanoid = humanoid;
		_chips.Layout.Clear( true );
		foreach ( var (label, prompt) in humanoid ? HumanExamples : CreatureExamples )
			_chips.Layout.Add( new ChipButton( _chips, label, "auto_awesome", () => Picked?.Invoke( prompt ), $"Use \"{prompt}\" as the prompt" ) { Arrow = false } );
		_chips.Layout.AddStretchCell();
	}
}
