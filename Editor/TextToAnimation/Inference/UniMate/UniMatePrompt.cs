using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// Turns user prompts into UniMate's caption style. Training captions all read "An object walks forward."
/// (third person, the subject says nothing about the character), so "Walk cautiously forward, look behind,
/// then run." becomes three segments: "An object walks cautiously forward." / "An object looks behind." /
/// "An object runs."
/// </summary>
public static class UniMatePrompt
{
	static readonly Regex StepSplit = new( @"\s*(?:,\s*(?:and\s+)?then\b|;|\bthen\b|\band then\b|\bafter that\b|\bafterwards\b|\.\s+)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly Regex CommaSplit = new( @"\s*,\s*", RegexOptions.Compiled );
	static readonly HashSet<string> Subjects = new( StringComparer.OrdinalIgnoreCase )
	{
		"a", "an", "the", "he", "she", "they", "it", "character", "person", "man", "woman", "someone", "somebody", "object",
		"my", "this", "that", "our", "your", "his", "her", "their", "some", "one", "human", "humanoid", "bipedal", "biped",
	};
	/// <summary>Verbs of UniMate-style motion captions (base form), to find the verb after a free-form subject.</summary>
	static readonly HashSet<string> MotionVerbs = new( StringComparer.OrdinalIgnoreCase )
	{
		"walk", "run", "jog", "sprint", "jump", "hop", "leap", "skip", "punch", "kick", "strike", "hit", "slap", "swing",
		"throw", "catch", "wave", "clap", "dance", "spin", "turn", "rotate", "twist", "bend", "bow", "crouch", "squat",
		"kneel", "sit", "stand", "rise", "lie", "fall", "roll", "crawl", "climb", "step", "stride", "march", "sneak",
		"tiptoe", "limp", "stumble", "stagger", "trip", "slide", "lean", "stretch", "reach", "raise", "lift", "lower",
		"push", "pull", "grab", "pick", "carry", "hold", "drag", "point", "look", "nod", "shake", "flap", "fly", "glide",
		"swim", "slither", "stomp", "kneel", "flip", "cartwheel", "somersault", "dodge", "block", "attack",
		"fight", "salute", "shrug", "celebrate", "cheer", "cry", "laugh", "idle", "wait", "rest", "sleep", "yawn",
		"pace", "gallop", "trot", "canter", "prance", "pounce", "bite", "scratch", "sniff", "graze", "wag", "swim",
		"move", "go", "come", "do", "perform", "make", "take", "get", "play", "practice", "stretch", "warm",
		"swipe", "slash", "stab", "thrust", "parry", "shoot", "aim", "draw", "drink", "eat", "sweep", "mop", "dig",
		"pray", "meditate", "balance", "tumble", "vault", "skate", "ski", "paddle", "pedal", "sway",
		"bounce", "wiggle", "shiver", "tremble", "flinch", "recoil", "collapse", "die", "faint", "taunt", "flex",
	};
	static readonly HashSet<string> Manner = new( StringComparer.OrdinalIgnoreCase ) { "back", "front", "side", "forward", "backward" };
	static readonly HashSet<string> Adverbs = new( StringComparer.OrdinalIgnoreCase )
	{
		"slowly", "quickly", "carefully", "cautiously", "suddenly", "happily", "angrily", "gently", "briskly", "calmly", "nervously",
		"sadly", "proudly", "lazily", "confidently", "fast", "slow",
	};

	/// <summary>Splits a prompt into sequential steps ("A, then B; C").</summary>
	public static List<string> SplitSteps( string prompt )
	{
		var parts = StepSplit.Split( prompt ?? "" ).Select( p => p.Trim().Trim( '.', ',' ).Trim() ).Where( p => p.Length > 0 ).ToList();
		// "walk forward, look behind, then run": commas between verb phrases are steps too
		var result = new List<string>();
		foreach ( var part in parts )
		{
			var commas = CommaSplit.Split( part ).Where( p => p.Length > 0 ).ToList();
			if ( commas.Count > 1 && commas.All( c => !StartsWithSubject( c ) ) ) result.AddRange( commas );
			else result.Add( part );
		}
		return result;
	}

	/// <summary>One step to a caption: "Walk cautiously forward" -> "An object walks cautiously forward."</summary>
	public static string ToCaption( string step )
	{
		var s = (step ?? "").Trim().TrimEnd( '.', '!', '?' ).Trim();
		if ( s.Length == 0 ) return "";
		if ( s.StartsWith( "an object", StringComparison.OrdinalIgnoreCase ) ) return Sentence( s );
		var words = s.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).ToList();
		// drop a leading subject ("a person walks" / "a bipedal robot in armor punches") - every training caption reads
		// "An object <verb>s ...": the skeleton is the subject, whatever the prompt calls it
		if ( StartsWithSubject( s ) )
		{
			while ( words.Count > 1 && Subjects.Contains( words[0] ) ) words.RemoveAt( 0 );
			// the verb: a third-person form ("punches") or a motion verb in any form ("a human punch forward")
			var head = words.Take( 6 ).ToList();
			var verb = head.FindIndex( IsThirdPersonVerb );
			if ( verb < 0 ) verb = Math.Max( 0, head.FindIndex( MotionVerbs.Contains ) );
			// a manner word just before the verb stays ("a person back flips", "a man slowly walks")
			var keep = verb > 0 && (Adverbs.Contains( words[verb - 1] ) || Manner.Contains( words[verb - 1] )) ? 1 : 0;
			words.RemoveRange( 0, verb - keep );
			if ( !IsThirdPersonVerb( words[keep] ) ) words[keep] = ThirdPerson( words[keep].ToLowerInvariant() );
			if ( keep == 1 ) words[0] = words[0].ToLowerInvariant();
			return Sentence( "An object " + string.Join( ' ', words ) );
		}
		// imperative: conjugate the first verb (skipping a leading adverb)
		var verbIndex = words.Count > 1 && Adverbs.Contains( words[0] ) ? 1 : 0;
		words[verbIndex] = ThirdPerson( words[verbIndex].ToLowerInvariant() );
		if ( verbIndex == 1 ) words[0] = words[0].ToLowerInvariant();
		return Sentence( "An object " + string.Join( ' ', words ) );
	}

	/// <summary>"punches", "walks", "is", "does" - not "dangerous", "boss", "bus".</summary>
	static bool IsThirdPersonVerb( string word )
	{
		var w = word.ToLowerInvariant();
		if ( w is "is" or "has" or "does" or "goes" ) return true;
		return w.Length >= 3 && w.EndsWith( 's' ) && !w.EndsWith( "ss" ) && !w.EndsWith( "us" ) && !w.EndsWith( "is" ) && !w.EndsWith( "'s" );
	}

	static bool StartsWithSubject( string s )
	{
		var first = s.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? "";
		return Subjects.Contains( first );
	}

	static string Sentence( string s )
	{
		s = s.Trim();
		return s.EndsWith( '.' ) ? s : s + ".";
	}

	/// <summary>walk -> walks, wave -> waves, crouch -> crouches, cry -> cries, go -> goes, do -> does.</summary>
	public static string ThirdPerson( string verb )
	{
		if ( verb.Length == 0 ) return verb;
		if ( verb is "be" ) return "is";
		if ( verb is "have" ) return "has";
		if ( verb.EndsWith( 's' ) && !verb.EndsWith( "ss" ) ) return verb; // already conjugated
		if ( verb.EndsWith( "ss" ) || verb.EndsWith( "sh" ) || verb.EndsWith( "ch" ) || verb.EndsWith( 'x' ) || verb.EndsWith( 'z' ) || verb.EndsWith( 'o' ) ) return verb + "es";
		if ( verb.EndsWith( 'y' ) && verb.Length > 1 && !"aeiou".Contains( verb[^2] ) ) return verb[..^1] + "ies";
		return verb + "s";
	}
}
