using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>Guards against writing into the s&amp;box installation (engine content is watched by the engine and crashed it natively).</summary>
public static class EnginePaths
{
	static string EngineRoot
	{
		get
		{
			try { var dir = Path.GetDirectoryName( Environment.ProcessPath ); return dir is null ? null : Path.GetFullPath( dir ); }
			catch { return null; }
		}
	}

	/// <summary>True for paths inside the s&amp;box install, except inside the currently open project.</summary>
	public static bool IsUnderEngineInstall( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		try
		{
			var full = Path.GetFullPath( path );
			var project = Project.Current?.GetRootPath();
			if ( !string.IsNullOrWhiteSpace( project ) )
			{
				var prefix = Path.GetFullPath( project ).TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
				if ( full.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) return false;
			}
			return IsUnderEngineInstallIgnoringProject( full );
		}
		catch { return false; }
	}

	public static bool IsUnderEngineInstallIgnoringProject( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		try
		{
			var full = Path.GetFullPath( path );
			var root = EngineRoot;
			if ( root is not null && full.StartsWith( root.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
				return true;
			return full.Replace( '/', '\\' ).IndexOf( @"steamapps\common\sbox\", StringComparison.OrdinalIgnoreCase ) >= 0;
		}
		catch { return false; }
	}
}
