using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>
/// UniMate's second naming stage. Upstream runs its name rule (<see cref="UniMateNames"/>) and then a language model
/// (data_process/joint_annotation/names_clean_llm.py, names_correct_llm.py) that turns every joint into a label of a
/// fixed anatomical vocabulary; the model's name embedding was trained on those labels. This is that stage without the
/// language model: the corrections its instructions spell out, each kept only as far as it moves the port's labels
/// toward the labels in UniMate's published data (NamingAgreementTests: the 74 Truebones animals and 200 Objaverse rigs).
/// </summary>
public static class UniMateNaming
{
	/// <summary>Where a joint sits in the rig's anatomy (from the rest-pose analysis).</summary>
	public readonly record struct Place( string Limb, int Index, int Count, bool BodyRoot );

	static readonly Regex Trailing = new( @"(?i)(nub|_?end|tip)$", RegexOptions.Compiled );

	/// <summary>
	/// Corrects rule labels. <paramref name="raw"/> and <paramref name="rule"/> are per joint; <paramref name="place"/>
	/// gives each joint's position in its limb (null where the analysis has none).
	/// </summary>
	public static string[] Correct( IReadOnlyList<string> raw, IReadOnlyList<string> rule, IReadOnlyList<int> parents, IReadOnlyList<Place?> place, string objectType )
	{
		var n = rule.Count;
		var result = rule.ToArray();
		for ( var i = 0; i < n; i++ )
		{
			var label = result[i];
			if ( IsNumeric( raw[i] ) ) continue; // rule 8: unnamed bones pass through
			// rule 3: Blender deform prefixes ("DEF-spine") the rule reads as part of the name
			var m = Regex.Match( raw[i] ?? "", @"^(?i:DEF|ORG|MCH)[-_.](.+)$" );
			if ( m.Success ) label = UniMateNames.Clean( m.Groups[1].Value, objectType );
			// rule 2: a container word ahead of the part it holds ("Appendage Tongue", "Arm Shoulder", "Base Spine")
			label = DropContainer( label );
			if ( place[i] is { BodyRoot: true } && label == "Hip" ) label = "Hips";
			// rules 6-7, 10: a label outside the vocabulary reduced to the canonical part it names
			label = ToVocabulary( label );

			// rule 5: a side the raw name carries that the rule lost ("FangR_00_", "ear0_right", "ArmRCollarbone")
			var side = SideOf( label );
			if ( side is null && RawSide( raw[i] ) is { } found )
				label = found + " " + StripSideWords( label );
			else if ( side is null )
				label = StripSideWords( label );

			// rule 5 (quadruped markers between side and part): a raw Mid is Middle, a raw Rear on a leg is Hind
			label = LegMarker( raw[i], label );

			// rule 7: chain tips ("Bip01_Xtra03Nub", "Ponytail2Nub") are "<part> End"
			if ( Trailing.IsMatch( raw[i] ) && !label.EndsWith( " End", StringComparison.Ordinal ) && !label.EndsWith( "End", StringComparison.Ordinal ) )
				label += " End";

			result[i] = label;
		}

		// chains the rule gives one generic name ("Left Leg" x4 down a crab leg): segments by position, as UniMate's data
		// names them - Thigh, Shin, Foot, Toe (legs), Upper Arm, Forearm, Hand (arms; UniMate's data has no plain "Arm")
		var children = Enumerable.Range( 0, n ).ToLookup( i => parents[i] );
		var done = new bool[n];
		for ( var i = 0; i < n; i++ )
		{
			var b = Base( result[i] );
			if ( done[i] || b is not ("Leg" or "Arm") ) continue;
			if ( parents[i] >= 0 && Base( result[parents[i]] ) == b && result[parents[i]] == result[i] ) continue; // not a chain start
			var chain = new List<int> { i };
			for ( var at = i; ; )
			{
				var next = children[at].Where( c => result[c] == result[i] ).ToList();
				if ( next.Count != 1 ) break;
				chain.Add( at = next[0] );
			}
			foreach ( var c in chain ) done[c] = true;
			if ( b == "Arm" )
			{
				string[] arm = { "Upper Arm", "Forearm", "Hand" };
				for ( var k = 0; k < chain.Count; k++ ) result[chain[k]] = Prefix( result[chain[k]] ) + arm[Math.Min( k, arm.Length - 1 )];
			}
			else if ( chain.Count >= 2 )
			{
				string[] leg = { "Thigh", "Shin", "Foot", "Toe" };
				for ( var k = 0; k < chain.Count; k++ ) result[chain[k]] = Prefix( result[chain[k]] ) + leg[Math.Min( k, leg.Length - 1 )];
			}
			else if ( chain.Count == 1 && parents[i] >= 0 && Base( result[parents[i]] ) == "Thigh" )
				result[i] = Prefix( result[i] ) + "Shin";
		}
		return result;
	}

	static readonly HashSet<string> Containers = new( StringComparer.Ordinal ) { "Appendage", "Head", "Base", "Arm", "Leg", "Bone", "Body" };

	/// <summary>"Left Appendage Tongue" -> "Left Tongue": a container word before a known part (not "Head End").</summary>
	static string DropContainer( string label )
	{
		var side = SideOf( label );
		var words = (side is null ? label : label[(side.Length + 1)..]).Split( ' ' ).ToList();
		while ( words.Count > 1 && Containers.Contains( words[0] ) && words[1] != "End" && Known( string.Join( ' ', words.Skip( 1 ) ) ) )
			words.RemoveAt( 0 );
		return (side is null ? "" : side + " ") + string.Join( ' ', words );
	}

	/// <summary>A part UniMate's vocabulary has, bare or with a side ("Claw" is there as "Left Claw").</summary>
	static bool Known( string part ) => UniMateVocabulary.Names.Contains( part ) || UniMateVocabulary.Names.Contains( "Left " + part );

	static readonly HashSet<string> Qualifiers = new( StringComparer.Ordinal ) { "Left", "Right", "Front", "Back", "Middle", "Rear", "Hind", "Inner", "Outer", "Upper", "Lower" };

	/// <summary>
	/// A label UniMate never trained on, brought to the vocabulary when a canonical part is in it: digits of a foot are
	/// toes ("Left Foot Pinky Finger" -> "Left Toe"), "Eye Lid" is "Eyelid", Bottom/Top are Lower/Upper (rule 7), and
	/// words that are no body part ("Corner", "Membrane") go. Labels already in the vocabulary, and ones with nothing
	/// canonical in them, stay as they are.
	/// </summary>
	static string ToVocabulary( string label )
	{
		if ( UniMateVocabulary.Names.Contains( label ) ) return label;
		// the vocabulary's own synonyms first ("Right Leg Upper" is a thigh)
		var aligned = UniMateVocabulary.Align( label );
		if ( UniMateVocabulary.Names.Contains( aligned ) ) return aligned;
		var text = " " + label + " ";
		text = text.Replace( " Eye Lid ", " Eyelid " ).Replace( " Eye Brow ", " Eyebrow " ).Replace( " Bottom ", " Lower " ).Replace( " Top ", " Upper " );
		text = Regex.Replace( text, @" Foot (Thumb |Index |Middle |Ring |Pinky )?Finger ", " Toe " );
		var words = text.Trim().Split( ' ' ).ToList();
		if ( UniMateVocabulary.Names.Contains( string.Join( ' ', words ) ) ) return string.Join( ' ', words );
		// keep side/qualifier words and the longest canonical part; drop the rest
		var core = words.Where( w => !Qualifiers.Contains( w ) && w != "End" ).ToList();
		for ( var len = core.Count; len >= 1; len-- )
			for ( var start = 0; start + len <= core.Count; start++ )
			{
				var part = string.Join( ' ', core.Skip( start ).Take( len ) );
				var bare = UniMateVocabulary.Names.Contains( part );
				if ( !bare ) continue;
				var kept = words.Where( w => Qualifiers.Contains( w ) ).ToList();
				var candidate = string.Join( ' ', kept.Append( part ).Concat( words.Contains( "End" ) ? new[] { "End" } : Array.Empty<string>() ) );
				// prefer the composed label when UniMate knows it ("Left Upper Eyelid"), else side + part
				if ( UniMateVocabulary.Names.Contains( candidate ) ) return candidate;
				var sided = (SideOf( label ) is { } s ? s + " " : "") + part;
				return sided;
			}
		return label;
	}

	static readonly HashSet<string> LegParts = new( StringComparer.Ordinal ) { "Thigh", "Shin", "Foot", "Leg", "Toe", "Knee", "Ankle" };

	/// <summary>"Bip01_R_Thigh_Mid" -> "Right Middle Thigh", "Bip01_L_Calf_Rear" -> "Left Hind Shin".</summary>
	static string LegMarker( string raw, string label )
	{
		var tokens = System.Text.RegularExpressions.Regex.Split( raw ?? "", @"[^A-Za-z]+|(?<=[a-z])(?=[A-Z])" ).Where( t => t.Length > 0 ).Select( t => t.ToLowerInvariant() ).ToHashSet();
		var side = SideOf( label );
		var words = (side is null ? label : label[(side.Length + 1)..]).Split( ' ' ).ToList();
		var end = words.Count > 1 && words[^1] == "End";
		var part = end ? words[^2] : words.LastOrDefault();
		if ( part is null || !LegParts.Contains( part ) ) return label;
		// a rule-stage Rear on a leg is the vocabulary's Hind ("Hind Thigh"; Rear stays for hooves)
		var rear = words.IndexOf( "Rear" );
		if ( rear >= 0 ) words[rear] = "Hind";
		else if ( !words.Any( Qualifiers.Contains ) )
		{
			string marker = tokens.Contains( "mid" ) || tokens.Contains( "middle" ) ? "Middle" : tokens.Contains( "rear" ) || tokens.Contains( "hind" ) ? "Hind" : null;
			if ( marker is not null ) words.Insert( 0, marker );
		}
		return (side is null ? "" : side + " ") + string.Join( ' ', words );
	}

	static bool IsNumeric( string raw ) => Regex.IsMatch( raw ?? "", @"^_?\d+_?$" );

	static string SideOf( string label ) => label.StartsWith( "Left ", StringComparison.Ordinal ) ? "Left"
		: label.StartsWith( "Right ", StringComparison.Ordinal ) ? "Right" : null;

	/// <summary>Side words the rule left inside or after the label ("Ear Right").</summary>
	static string StripSideWords( string label ) => string.Join( ' ', label.Split( ' ' ).Where( w => w is not ("Left" or "Right") ) );

	/// <summary>A side the raw name encodes in the ways rule 5 lists that upstream's rule doesn't read.</summary>
	static string RawSide( string raw )
	{
		var r = raw ?? "";
		if ( Regex.IsMatch( r, @"(?i)(^|[^a-z])left([^a-z]|$)" ) ) return "Left";
		if ( Regex.IsMatch( r, @"(?i)(^|[^a-z])right([^a-z]|$)" ) ) return "Right";
		// a capital L/R closing a lowercase word: "FangR_00_", "harabireL", "ArmRCollarbone"
		var m = Regex.Match( r, @"[a-z]([LR])(?=[A-Z_.\d]|$)" );
		if ( m.Success ) return m.Groups[1].Value == "L" ? "Left" : "Right";
		// a lone l/r token: "Bone_L.001", "ear_r"
		m = Regex.Match( r, @"(?:^|[_.\s-])([LlRr])(?=[_.\s\d-]|$)" );
		if ( m.Success ) return char.ToUpperInvariant( m.Groups[1].Value[0] ) == 'L' ? "Left" : "Right";
		// a leg's corner code ("F_/B_ for quadrupeds"): front/back/rear/hind with a side, "Leg_FL1", "leg.RR.2", "LF_foot"
		m = Regex.Match( r, @"(?:^|[_.\s-])(?:[FBRH]([LR])|([LR])[FBH])(?=[_.\s\d-]|$)" );
		if ( m.Success ) return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) == "L" ? "Left" : "Right";
		return null;
	}

	/// <summary>The label without its side ("Left Leg" -> "Leg").</summary>
	static string Base( string label ) => SideOf( label ) is null ? label : label[(label.IndexOf( ' ' ) + 1)..];

	static string Prefix( string label ) => SideOf( label ) is { } s ? s + " " : "";
}
