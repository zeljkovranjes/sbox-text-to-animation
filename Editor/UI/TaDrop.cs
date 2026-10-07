#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>Files dropped from disk or the asset browser, filtered by extension.</summary>
public static class TaDrop
{
	public static readonly string[] ModelExtensions = { ".vmdl", ".fbx" };
	public static readonly string[] AnimationExtensions = { ".fbx", ".bvh", ".glb", ".gltf", ".dmx" };

	public static IReadOnlyList<string> Paths( DragData data, string[] extensions )
	{
		bool IsAnimationFile( string path )
			=> !string.IsNullOrEmpty( path ) && extensions.Contains( System.IO.Path.GetExtension( path ).ToLowerInvariant() );
		var paths = new List<string>();
		if ( data is null )
			return paths;
		try
		{
			if ( data.Files is { Length: > 0 } files )
				paths.AddRange( files.Where( IsAnimationFile ) );
			else if ( data.HasFileOrFolder && IsAnimationFile( data.FileOrFolder ) )
				paths.Add( data.FileOrFolder );
			if ( data.Assets is { Count: > 0 } assets )
			{
				foreach ( var asset in assets )
				{
					var path = asset?.AssetPath;
					if ( IsAnimationFile( path ) && AssetSystem.FindByPath( path )?.AbsolutePath is { } absolute )
						paths.Add( absolute );
				}
			}
		}
		catch ( Exception )
		{
			// A drag with nothing we understand.
		}
		return paths.Distinct( StringComparer.OrdinalIgnoreCase ).ToList();
	}
}
