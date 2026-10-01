using System;
using System.Collections.Generic;
using System.Linq;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Session;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.UI;

/// <summary>What a prompt does to the open animation.</summary>
public enum EditIntent { Change, FillBetween, Variations, New }

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

		var seed = options.Seed ?? Random.Shared.Next( 1, 99999 );
		var request = GenerationFlow.BuildRequest( session, mode, new[] { prompt }, options.Seconds, seed, options.Takes,
			options.Guidance, options.Steps, options.VariationStrength, keep );
		var shortName = prompt.Length > 0 ? GenerationFlow.NameFromPrompt( prompt ) : null;
		var name = mode switch
		{
			GenerationMode.TextToMotion => shortName ?? "Generated",
			GenerationMode.InBetween => $"{target!.Name} (in-between)",
			GenerationMode.Variation when intent == EditIntent.Variations => $"{target!.Name} (variation)",
			_ => shortName is null ? $"{target!.Name} (changed)" : $"{target!.Name} · {shortName}",
		};
		return (request, name);
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
