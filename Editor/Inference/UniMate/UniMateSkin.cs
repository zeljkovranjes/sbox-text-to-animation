using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TextToAnimation.Core.Rig;

namespace TextToAnimation.EditorTools.Inference.UniMate;

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
		public IReadOnlySet<string> Driven;
		public IReadOnlyDictionary<int, IReadOnlyList<System.Numerics.Vector3>> Points;
	}

	static readonly ConditionalWeakTable<Skeleton, Entry> _entries = new();

	/// <summary>Attaches per-bone (largest single, total) skin weights, and the model's name (upstream's object type).</summary>
	/// <param name="driven">Bones the model's own constraints drive at runtime (the engine poses them, not clips).</param>
	/// <param name="points">The skinned mesh's points per bone in the rest pose (UniMate's collision capsules are fitted to them).</param>
	public static void Attach( Skeleton skeleton, IReadOnlyDictionary<string, (double Max, double Sum)> weights, string objectType,
		IReadOnlySet<string> driven = null, IReadOnlyDictionary<int, IReadOnlyList<System.Numerics.Vector3>> points = null )
	{
		if ( skeleton is null ) return;
		_entries.Remove( skeleton );
		_entries.Add( skeleton, new Entry { Weights = weights, ObjectType = objectType ?? "", Driven = driven, Points = points } );
	}

	/// <summary>The mesh points per bone attached to <paramref name="skeleton"/>, or null when unknown.</summary>
	public static IReadOnlyDictionary<int, IReadOnlyList<System.Numerics.Vector3>> PointsOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) ? e.Points : null;

	/// <summary>The bones the model's constraints drive (empty when unknown).</summary>
	public static IReadOnlySet<string> DrivenOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) && e.Driven is not null ? e.Driven : new HashSet<string>();

	/// <summary>The weights attached to <paramref name="skeleton"/>, or null when unknown.</summary>
	public static IReadOnlyDictionary<string, (double Max, double Sum)> WeightsOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) ? e.Weights : null;

	/// <summary>The model name attached to <paramref name="skeleton"/> ("" when none).</summary>
	public static string ObjectTypeOf( Skeleton skeleton )
		=> skeleton is not null && _entries.TryGetValue( skeleton, out var e ) ? e.ObjectType : "";
}
