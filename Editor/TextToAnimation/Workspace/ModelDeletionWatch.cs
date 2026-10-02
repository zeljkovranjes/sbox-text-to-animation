using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Workspace;

namespace TextToAnimation.Editor.Workspace;

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

/// <summary>Watches the project's Assets folder for model files going away (see <see cref="DeletedModels"/>).</summary>
public static class ModelDeletionWatch
{
	static FileSystemWatcher _watcher;
	static string _root;
	public static DeletedModels Gone { get; } = new();

	/// <summary>Starts watching <paramref name="assetsRoot"/> (model paths are relative to it). Safe to call often.</summary>
	public static void Ensure( string assetsRoot )
	{
		if ( string.IsNullOrEmpty( assetsRoot ) || !Directory.Exists( assetsRoot ) ) return;
		if ( _watcher is not null && string.Equals( _root, assetsRoot, StringComparison.OrdinalIgnoreCase ) ) return;
		_watcher?.Dispose();
		_root = assetsRoot;
		string Rel( string full ) => Path.GetRelativePath( assetsRoot, full );
		bool IsModel( string full ) => full?.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) == true;
		var w = new FileSystemWatcher( assetsRoot ) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
		w.Deleted += ( _, e ) => { if ( IsModel( e.FullPath ) ) Gone.Deleted( Rel( e.FullPath ), DateTime.UtcNow ); };
		w.Created += ( _, e ) => { if ( IsModel( e.FullPath ) ) Gone.Created( Rel( e.FullPath ), DateTime.UtcNow ); };
		w.Renamed += ( _, e ) =>
		{
			if ( IsModel( e.OldFullPath ) ) Gone.Deleted( Rel( e.OldFullPath ), DateTime.UtcNow );
			if ( IsModel( e.FullPath ) ) Gone.Created( Rel( e.FullPath ), DateTime.UtcNow );
		};
		w.EnableRaisingEvents = true;
		_watcher = w;
	}
}
