using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Core.Workspace;

namespace TextToAnimation.EditorTools.Workspace;

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
