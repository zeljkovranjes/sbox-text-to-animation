using System;
using System.Collections.Generic;
using System.Linq;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;
using TextToAnimation.Core.Generation;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// Turns a prompt plus the composer's options into a generation request, for the home conversation and the
/// editor's prompt alike, so both behave the same way.
/// </summary>
public static class PromptRequests
{
	public static string IntentName( EditIntent intent ) => intent switch
	{
		EditIntent.FillBetween => "Fill between pins",
		EditIntent.Variations => "Variations",
		EditIntent.New => "New animation",
		_ => "Change this animation",
	};

	/// <summary>Builds the request; <paramref name="target"/> null (or <see cref="EditIntent.New"/>) makes a new animation.</summary>
	public static (GenerationRequest Request, string Name) Build( EditorSession session, string prompt, PromptOptions options, AnimClip target, EditIntent intent )
	{
		prompt = (prompt ?? "").Trim();
		var rig = session.Rig;
		var mode = GenerationMode.TextToMotion;
		IReadOnlyCollection<int> keep = null;
		if ( target is not null && intent != EditIntent.New )
		{
			session.SelectClip( target );
			switch ( intent )
			{
				case EditIntent.FillBetween: mode = GenerationMode.InBetween; break;
				case EditIntent.Variations: mode = GenerationMode.Variation; break;
				default:
					var scope = rig?.IsHumanoid == true || options.Scope is ChangeScope.Unlocked or ChangeScope.SelectedBones ? options.Scope : ChangeScope.WholeBody;
					if ( scope == ChangeScope.WholeBody ) mode = GenerationMode.Variation;
					else
					{
						mode = GenerationMode.TextEdit;
						keep = scope == ChangeScope.Unlocked ? null : KeepFor( session, scope );
					}
					break;
			}
		}
		else if ( intent == EditIntent.New ) session.SelectClip( null );

		// a new animation written one step per line (Shift+Enter) is a sequence: each line continues the one before,
		// as upstream's motion expansion takes a list of prompts; a single line is one caption, whole
		var lines = prompt.Split( LineBreaks ).Select( l => l.Trim() ).Where( l => l.Length > 0 ).ToList();
		if ( mode == GenerationMode.TextToMotion && lines.Count > 1 ) mode = GenerationMode.Expansion;
		var prompts = mode == GenerationMode.Expansion ? lines : new List<string> { prompt };

		var seed = options.Seed ?? Random.Shared.Next( 1, 99999 );
		var request = GenerationFlow.BuildRequest( session, mode, prompts, options.Seconds, seed, options.Takes,
			options.Guidance, options.Steps, options.VariationStrength, keep, options.CleanUp );
		var shortName = mode == GenerationMode.Expansion ? SequenceName( lines )
			: prompt.Length > 0 ? GenerationFlow.NameFromPrompt( prompt ) : null;
		var name = mode switch
		{
			GenerationMode.TextToMotion or GenerationMode.Expansion => shortName ?? "Generated",
			GenerationMode.InBetween => $"{target!.Name} (in-between)",
			GenerationMode.Variation when intent == EditIntent.Variations => $"{target!.Name} (variation)",
			_ => shortName is null ? $"{target!.Name} (changed)" : $"{target!.Name} · {shortName}",
		};
		return (request, name);
	}

	/// <summary>Every way the prompt box can break a line (Shift+Enter may give Qt's line or paragraph separator).</summary>
	static readonly char[] LineBreaks = { (char)10, (char)13, (char)0x2028, (char)0x2029 };

	/// <summary>A sequence's name: its steps in order ("Walks forward, turns around, runs forward"), shortened when long.</summary>
	public static string SequenceName( IReadOnlyList<string> steps )
	{
		var names = steps.Select( GenerationFlow.NameFromPrompt ).Select( ( n, i ) => i == 0 || n.Length == 0 ? n : char.ToLowerInvariant( n[0] ) + n[1..] );
		return GenerationFlow.NameFromPrompt( string.Join( ", ", names ) );
	}

	/// <summary>Bones that keep their motion when only <paramref name="scope"/> changes.</summary>
	public static IReadOnlyCollection<int> KeepFor( EditorSession session, ChangeScope scope )
	{
		var rig = session.Rig;
		var all = Enumerable.Range( 0, rig.Skeleton.Count ).Where( rig.IsMotionBone );
		if ( scope == ChangeScope.SelectedBones )
		{
			var free = session.SelectedBones.SelectMany( rig.Descendants ).ToHashSet();
			return all.Where( b => !free.Contains( b ) ).ToArray();
		}
		var regions = scope switch
		{
			ChangeScope.UpperBody => new[] { BodyRegion.Spine, BodyRegion.Head, BodyRegion.ArmL, BodyRegion.ArmR, BodyRegion.HandL, BodyRegion.HandR },
			ChangeScope.LowerBody => new[] { BodyRegion.Root, BodyRegion.LegL, BodyRegion.LegR },
			ChangeScope.Arms => new[] { BodyRegion.ArmL, BodyRegion.ArmR, BodyRegion.HandL, BodyRegion.HandR },
			_ => Array.Empty<BodyRegion>(),
		};
		return all.Where( b => !regions.Contains( rig.RegionOf( b ) ) ).ToArray();
	}
}
