using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Editor;

namespace TextToAnimation.Editor.Engine;

/// <summary>Finding the files a vmdl refers to, and what the model itself drives at runtime.</summary>
public static class VmdlSources
{
	/// <summary>The "Assets" folder a source file sits in (a project's or an addon's); null when it isn't in one.</summary>
	public static string AssetsRootOf( string path )
	{
		for ( var dir = Path.GetDirectoryName( path ); !string.IsNullOrEmpty( dir ); dir = Path.GetDirectoryName( dir ) )
			if ( string.Equals( Path.GetFileName( dir ), "Assets", StringComparison.OrdinalIgnoreCase ) ) return dir;
		return null;
	}

	/// <summary>
	/// The file an asset-relative reference ("models/x/x.fbx") in <paramref name="vmdl"/> points to, found the way
	/// the engine finds it: under the vmdl's own Assets folder, through the asset system (project and mounted
	/// addons), or in an installed addon (Citizen's sources live there). Null when it can't be found.
	/// </summary>
	public static string Resolve( string relative, string vmdl )
	{
		var local = relative.Replace( '/', Path.DirectorySeparatorChar );
		var root = AssetsRootOf( vmdl );
		if ( root is not null && File.Exists( Path.Combine( root, local ) ) ) return Path.Combine( root, local );
		var asset = AssetSystem.FindByPath( relative );
		if ( asset?.AbsolutePath is { } abs && File.Exists( abs ) ) return abs;
		var addons = Path.Combine( Path.GetDirectoryName( Environment.ProcessPath ) ?? "", "addons" );
		if ( Directory.Exists( addons ) )
			foreach ( var addon in Directory.GetDirectories( addons ) )
			{
				var candidate = Path.Combine( addon, "Assets", local );
				if ( File.Exists( candidate ) ) return candidate;
			}
		return null;
	}

	/// <summary>The text of every .vmdl_prefab the vmdl includes (recursively, each once).</summary>
	public static List<string> IncludedPrefabTexts( string vmdlText, string vmdl )
	{
		var texts = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		void Scan( string text, int depth )
		{
			if ( string.IsNullOrEmpty( text ) || depth > 8 ) return;
			foreach ( Match m in Regex.Matches( text, @"target_file\s*=\s*""([^""]+\.vmdl_prefab)""" ) )
			{
				if ( !seen.Add( m.Groups[1].Value ) || Resolve( m.Groups[1].Value, vmdl ) is not { } prefab ) continue;
				string sub = null;
				try { sub = File.ReadAllText( prefab ); } catch ( IOException ) { }
				if ( sub is null ) continue;
				texts.Add( sub );
				Scan( sub, depth + 1 );
			}
		}
		Scan( vmdlText, 0 );
		return texts;
	}

	/// <summary>
	/// Bones the model's own animation constraints drive at runtime (AnimConstraintSlave targets in the vmdl and
	/// the prefabs it includes): whatever a sequence stores for them, the game shows the constraint's result.
	/// </summary>
	public static HashSet<string> ConstraintDrivenBones( string vmdlText, string vmdl )
	{
		var driven = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var text in IncludedPrefabTexts( vmdlText, vmdl ).Prepend( vmdlText ) )
			foreach ( Match m in Regex.Matches( text ?? "", @"_class\s*=\s*""AnimConstraintSlave""[^{}]*?parent_bone\s*=\s*""([^""]+)""", RegexOptions.Singleline ) )
				driven.Add( m.Groups[1].Value );
		return driven;
	}
}
