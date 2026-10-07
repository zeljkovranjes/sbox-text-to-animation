using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Core.Workspace;

namespace TextToAnimation.EditorTools.Workspace;

/// <summary>
/// Model files deleted (or renamed away) under the project's Assets folder, so their workspaces can be forgotten: a
/// new model made later at the same path must start with no animations. Opening can't always tell on its own -
/// Windows may give a file made again at a path the old file's creation time. A file that comes back within
/// <see cref="Replace"/> was saved by replacing it (an editor's write-then-swap), not deleted.
/// No Sandbox types: unit tested outside the editor.
/// </summary>
public sealed class DeletedModels
{
	public static readonly TimeSpan Replace = TimeSpan.FromSeconds( 2 );

	readonly Dictionary<string, DateTime> _gone = new();

	public void Deleted( string modelPath, DateTime now )
	{
		lock ( _gone ) _gone[AnimationWorkspace.NormalizePath( modelPath )] = now;
	}

	public void Created( string modelPath, DateTime now )
	{
		var key = AnimationWorkspace.NormalizePath( modelPath );
		lock ( _gone )
			if ( _gone.TryGetValue( key, out var at ) && now - at < Replace ) _gone.Remove( key );
	}

	/// <summary>Model paths deleted at least <see cref="Replace"/> ago (removed from the list).</summary>
	public List<string> Take( DateTime now )
	{
		lock ( _gone )
		{
			var done = _gone.Where( kv => now - kv.Value >= Replace ).Select( kv => kv.Key ).ToList();
			foreach ( var key in done ) _gone.Remove( key );
			return done;
		}
	}
}
