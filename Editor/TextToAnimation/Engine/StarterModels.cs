using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.Engine;

/// <summary>An s&amp;box character a new model can start from.</summary>
public sealed record StarterModel( string Title, string AssetPath, string Description );

/// <summary>
/// Gets a model into the project to work on: a copy of a stock s&amp;box character (the .vmdl is copied as is,
/// so it keeps the character's meshes, materials, animations and animgraph through their mount paths), or
/// a .vmdl picked from outside the project. The engine's own files are never modified.
/// </summary>
public static class StarterModels
{
	public static readonly StarterModel Citizen = new( "Citizen", "models/citizen/citizen.vmdl", "The stylised s&box character" );
	public static readonly StarterModel CitizenHuman = new( "Citizen Human", "models/citizen_human/citizen_human_male.vmdl", "The realistic s&box human" );

	/// <summary>The project's Assets folder (absolute), or null without a project.</summary>
	public static string AssetsRoot => Project.Current?.GetAssetsPath() is { Length: > 0 } p ? Path.GetFullPath( p ) : null;

	/// <summary>Where a new model goes by default: Assets/models/&lt;name&gt;/&lt;name&gt;.vmdl.</summary>
	public static string DefaultTarget( string name )
	{
		var root = AssetsRoot ?? "";
		var clean = string.Concat( (name ?? "").ToLowerInvariant().Select( c => char.IsLetterOrDigit( c ) ? c : '_' ) ).Trim( '_' );
		if ( clean.Length == 0 ) clean = "character";
		var path = Path.Combine( root, "models", clean, clean + ".vmdl" );
		for ( var n = 2; File.Exists( path ); n++ ) path = Path.Combine( root, "models", $"{clean}_{n}", $"{clean}_{n}.vmdl" );
		return path;
	}

	/// <summary>True when <paramref name="path"/> is inside the project's Assets folder.</summary>
	public static bool IsInProject( string path )
	{
		var root = AssetsRoot;
		if ( root is null || string.IsNullOrWhiteSpace( path ) ) return false;
		try { return Path.GetFullPath( path ).StartsWith( root.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ); }
		catch { return false; }
	}

	/// <summary>The source .vmdl text of a stock character.</summary>
	public static string SourceText( StarterModel starter )
	{
		foreach ( var candidate in SourceCandidates( starter ) )
		{
			try { if ( !string.IsNullOrEmpty( candidate ) && File.Exists( candidate ) && candidate.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) return File.ReadAllText( candidate ); }
			catch { }
		}
		throw new FileNotFoundException( $"The {starter.Title} model source ({starter.AssetPath}) was not found in the s&box install." );
	}

	static string[] SourceCandidates( StarterModel starter )
	{
		var fromAsset = EngineThread.Try( () => AssetSystem.FindByPath( starter.AssetPath )?.AbsolutePath );
		var exeDir = EngineThread.Try( () => Path.GetDirectoryName( Environment.ProcessPath ) );
		var fromInstall = exeDir is null ? null : Path.Combine( exeDir, "addons", "citizen", "Assets", starter.AssetPath.Replace( '/', Path.DirectorySeparatorChar ) );
		return new[] { fromAsset, fromInstall };
	}

	/// <summary>Copies a stock character to <paramref name="targetVmdl"/> (inside the project) and compiles it.</summary>
	public static Task<VmdlCompiler.CompileResult> CreateFromStarterAsync( StarterModel starter, string targetVmdl )
	{
		if ( !IsInProject( targetVmdl ) ) throw new InvalidOperationException( "Save the new model inside this project's Assets folder." );
		if ( File.Exists( targetVmdl ) ) throw new InvalidOperationException( $"{Path.GetFileName( targetVmdl )} already exists - pick another name." );
		var text = SourceText( starter );
		Directory.CreateDirectory( Path.GetDirectoryName( targetVmdl )! );
		File.WriteAllText( targetVmdl, text );
		return VmdlCompiler.RegisterAndCompileAsync( targetVmdl, Array.Empty<string>() );
	}

	/// <summary>
	/// Brings a .vmdl from outside the project in: it is copied to Assets/models/&lt;name&gt;/ and compiled. Its
	/// references (meshes, materials, animations) must resolve from the project, like any model.
	/// </summary>
	public static Task<VmdlCompiler.CompileResult> CopyIntoProjectAsync( string sourceVmdl )
	{
		if ( AssetsRoot is null ) throw new InvalidOperationException( "Open a project first." );
		var name = Path.GetFileNameWithoutExtension( sourceVmdl );
		var target = DefaultTarget( name );
		Directory.CreateDirectory( Path.GetDirectoryName( target )! );
		File.Copy( sourceVmdl, target );
		return VmdlCompiler.RegisterAndCompileAsync( target, Array.Empty<string>() );
	}
}
