using System.Collections.Generic;
using System.Linq;

namespace TextToAnimation.Editor.Session;

/// <summary>
/// The lock actions for text-guided editing: locked bones keep their motion when the clip is regenerated
/// with a new prompt. Locks are stored per clip (by bone name) and are undoable edits.
/// </summary>
public static class BoneLocks
{
	public static void LockSelected( EditorSession s )
	{
		var bones = Names( s, s.SelectedBones );
		if ( bones.Count == 0 ) { s.SetStatus( "Select bones in the view first.", UI.Tone.Amber ); return; }
		s.Edit( $"Lock {Describe( bones )}", c => c.LockedBones.UnionWith( bones ) );
	}

	public static void LockHierarchy( EditorSession s )
	{
		if ( s.SelectedBones.Count == 0 ) { s.SetStatus( "Select bones in the view first.", UI.Tone.Amber ); return; }
		var bones = Names( s, s.SelectedBones.SelectMany( b => s.Rig.Descendants( b ) ) );
		s.Edit( $"Lock {Describe( bones )} and children", c => c.LockedBones.UnionWith( bones ) );
	}

	public static void LockAllExcept( EditorSession s )
	{
		if ( s.SelectedBones.Count == 0 ) { s.SetStatus( "Select the bones to regenerate first.", UI.Tone.Amber ); return; }
		var free = s.SelectedBones.SelectMany( b => s.Rig.Descendants( b ) ).ToHashSet();
		var bones = Names( s, Enumerable.Range( 0, s.Rig.Skeleton.Count ).Where( b => s.Rig.IsMotionBone( b ) && !free.Contains( b ) ) );
		s.Edit( "Lock everything except the selection", c => { c.LockedBones.Clear(); c.LockedBones.UnionWith( bones ); } );
	}

	/// <summary>Unlocks the selection, or every bone when nothing is selected.</summary>
	public static void Unlock( EditorSession s )
	{
		if ( s.ActiveClip is null ) return;
		if ( s.SelectedBones.Count == 0 ) s.Edit( "Unlock all bones", c => c.LockedBones.Clear() );
		else
		{
			var bones = Names( s, s.SelectedBones );
			s.Edit( $"Unlock {Describe( bones )}", c => c.LockedBones.ExceptWith( bones ) );
		}
	}

	static HashSet<string> Names( EditorSession s, IEnumerable<int> bones )
		=> bones.Where( b => b >= 0 && b < s.Rig.Skeleton.Count ).Select( b => s.Rig.Skeleton[b].Name ).ToHashSet();

	static string Describe( HashSet<string> bones ) => bones.Count == 1 ? bones.First() : $"{bones.Count} bones";
}
