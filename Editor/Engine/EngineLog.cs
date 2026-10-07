using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>Reads the editor's log (resourcecompiler output lands there; the asset system exposes no error API).</summary>
public static class EngineLog
{
	public static string File()
	{
		try
		{
			var candidates = new List<string> { Path.Combine( Environment.CurrentDirectory, "logs", "sbox-dev.log" ) };
			var exeDir = Path.GetDirectoryName( Environment.ProcessPath );
			if ( exeDir is not null ) candidates.Add( Path.Combine( exeDir, "logs", "sbox-dev.log" ) );
			return candidates.FirstOrDefault( System.IO.File.Exists );
		}
		catch { return null; }
	}

	public static long Length()
	{
		try { var f = File(); return f is null ? -1 : new FileInfo( f ).Length; }
		catch { return -1; }
	}

	public static string Slice( long from )
	{
		try
		{
			var f = File();
			if ( f is null || from < 0 ) return null;
			using var fs = System.IO.File.Open( f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite );
			if ( fs.Length < from ) from = 0;
			fs.Seek( from, SeekOrigin.Begin );
			using var reader = new StreamReader( fs );
			return reader.ReadToEnd();
		}
		catch { return null; }
	}

	public static bool ShowsAbandonedRecompile( long from, string fileName )
		=> Slice( from )?.Split( '\n' ).Any( l => l.Contains( "abandoning recompile", StringComparison.OrdinalIgnoreCase )
			&& l.Contains( fileName, StringComparison.OrdinalIgnoreCase ) ) ?? false;

	public static string CompileErrors( long from, IReadOnlyList<string> fileNames )
	{
		var slice = Slice( from );
		if ( slice is null ) return null;
		var lines = slice.Split( '\n' ).Select( l => l.TrimEnd( '\r' ) )
			.Where( l => l.Length > 0
				&& (l.Contains( "error", StringComparison.OrdinalIgnoreCase ) || l.Contains( "failed", StringComparison.OrdinalIgnoreCase ))
				&& (fileNames.Any( n => l.Contains( n, StringComparison.OrdinalIgnoreCase ) )
					|| l.Contains( "resourcecompiler", StringComparison.OrdinalIgnoreCase ) || l.Contains( "ModelDoc", StringComparison.OrdinalIgnoreCase )) )
			.TakeLast( 12 ).ToList();
		return lines.Count == 0 ? null : string.Join( "\n", lines );
	}
}
