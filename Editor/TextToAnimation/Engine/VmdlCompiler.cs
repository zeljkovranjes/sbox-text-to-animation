using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.Engine;

/// <summary>
/// Compiles a vmdl and proves it worked (adapted from humanoid-retargeter's EditorPipeline): inputs settle
/// before compiling, the base model is loaded first, freshness is judged by the compiled file's timestamp,
/// abandoned recompiles ("quiet inputs") are retried, and compiler errors are read from the editor log.
/// </summary>
public static class VmdlCompiler
{
	/// <summary>Longer than the engine's ~1270 ms "quiet inputs" window.</summary>
	const int InputSettleDelayMs = 2000;
	const int DefensiveInputSettleDelayMs = 4000;

	public sealed class CompileResult
	{
		public bool Compiled { get; set; }
		public Asset Asset { get; set; }
		public string Error { get; set; }
		/// <summary>Something the user should know about how the model was made (e.g. a unit fix).</summary>
		public string Note { get; set; }
	}

	/// <summary>Registers the files (absolute paths), then compiles <paramref name="vmdlAbsolute"/>.</summary>
	public static async Task<CompileResult> RegisterAndCompileAsync( string vmdlAbsolute, IEnumerable<string> extraFiles, float timeoutSeconds = 180f )
	{
		var result = new CompileResult();
		await EngineThread.SwitchToMainThread();
		foreach ( var file in extraFiles ) EngineThread.Try( () => AssetSystem.RegisterFile( file ) );
		result.Asset = AssetSystem.RegisterFile( vmdlAbsolute );
		if ( result.Asset is null )
		{
			result.Error = $"The asset system did not accept {Path.GetFileName( vmdlAbsolute )}.";
			return result;
		}

		var baseModel = BaseModelOf( vmdlAbsolute );
		if ( !string.IsNullOrEmpty( baseModel ) ) EngineThread.Try( () => Model.Load( baseModel ) );
		var installResident = EnginePaths.IsUnderEngineInstallIgnoringProject( vmdlAbsolute );
		var settle = installResident ? DefensiveInputSettleDelayMs : InputSettleDelayMs;
		await EngineThread.DelayOnMain( settle );

		var fileName = Path.GetFileName( vmdlAbsolute );
		var logOffset = EngineLog.Length();
		result.Compiled = await CompileAndWaitAsync( result.Asset, timeoutSeconds, logOffset, fileName );
		for ( var retry = 0; retry < (installResident ? 2 : 1) && !result.Compiled && EngineLog.ShowsAbandonedRecompile( logOffset, fileName ); retry++ )
		{
			Log.Warning( $"[text-to-animation] the engine abandoned the recompile of {fileName} - retrying" );
			await EngineThread.DelayOnMain( settle );
			logOffset = EngineLog.Length();
			result.Compiled = await CompileAndWaitAsync( result.Asset, timeoutSeconds, logOffset, fileName );
		}
		if ( !result.Compiled )
			result.Error = EngineLog.CompileErrors( logOffset, new[] { fileName, ".dmx" } ) ?? $"{fileName} did not compile (see the console for resourcecompiler output).";
		return result;
	}

	public static async Task<bool> CompileAndWaitAsync( Asset asset, float timeoutSeconds, long logOffset, string watchFileName )
	{
		await EngineThread.SwitchToMainThread();
		string Resolve()
		{
			var path = EngineThread.Try( () => asset.GetCompiledFile( true ) );
			if ( path is not null ) return path;
			var source = EngineThread.Try( () => asset.AbsolutePath );
			return string.IsNullOrEmpty( source ) ? null : source + "_c";
		}
		var compiled = Resolve();
		DateTime? stamp = null;
		try { if ( compiled is not null && File.Exists( compiled ) ) stamp = File.GetLastWriteTimeUtc( compiled ); } catch { }

		try { asset.Compile( full: true ); }
		catch ( Exception e ) { Log.Warning( $"[text-to-animation] asset.Compile threw: {e.Message}" ); }

		bool Fresh()
		{
			try { return compiled is not null && File.Exists( compiled ) && (stamp is not { } s || File.GetLastWriteTimeUtc( compiled ) > s); }
			catch { return false; }
		}

		var sw = Stopwatch.StartNew();
		while ( sw.Elapsed.TotalSeconds < timeoutSeconds )
		{
			await EngineThread.SwitchToMainThread();
			if ( EngineThread.Try( () => asset.IsCompileFailed ) ) return false;
			compiled = EngineThread.Try( () => asset.GetCompiledFile( true ) ) ?? compiled;
			if ( Fresh() ) return true;
			if ( compiled is null && stamp is null && EngineThread.Try( () => asset.IsCompiled && asset.HasCompiledFile ) ) return true;
			if ( logOffset >= 0 && watchFileName is not null && EngineLog.ShowsAbandonedRecompile( logOffset, watchFileName ) ) return false;
			await Task.Delay( 250 );
		}
		return Fresh();
	}

	public static string BaseModelOf( string vmdlAbsolute )
	{
		try
		{
			var match = System.Text.RegularExpressions.Regex.Match( File.ReadAllText( vmdlAbsolute ), @"base_model_name\s*=\s*""([^""]*)""" );
			return match.Success ? match.Groups[1].Value : null;
		}
		catch { return null; }
	}
}

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
