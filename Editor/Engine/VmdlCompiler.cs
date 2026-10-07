using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

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
