using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TextToAnimation.Rig;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// Skin weights per bone for a skeleton, which upstream's skeleton preparation prunes on ("skinned bones are never
/// prunable"). The engine doesn't expose them; the editor reads them from the model's source FBX and attaches them
/// here when it opens the model. Unknown (nothing attached) means every bone counts as skinned.
/// </summary>
public static class UniMateSkin
{
	sealed class Entry
	{
		public IReadOnlyDictionary<string, (double Max, double Sum)> Weights;
		public string ObjectType;
	}

	static readonly ConditionalWeakTable<Skeleton, Entry> _entries = new();

	/// <summary>Attaches per-bone (largest single, total) skin weights, and the model's name (upstream's object type).</summary>
	public static void Attach( Skeleton skeleton, IReadOnlyDictionary<string, (double Max, double Sum)> weights, string objectType )
	{
		if ( skeleton is null ) return;
		_entries.Remove( skeleton );
		_entries.Add( skeleton, new Entry { Weights = weights, ObjectType = objectType ?? "" } );
	}

	/// <summary>The weights attached to <paramref name="skeleton"/>, or null when unknown.</summary>
	public static IReadOnlyDictionary<string, (double Max, double Sum)> WeightsOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) ? e.Weights : null;

	/// <summary>The model name attached to <paramref name="skeleton"/> ("" when none).</summary>
	public static string ObjectTypeOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) ? e.ObjectType : "";
}
