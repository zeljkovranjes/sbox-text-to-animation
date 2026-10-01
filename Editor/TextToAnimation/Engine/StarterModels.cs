using System;
using System.Collections.Generic;
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
	/// Makes a model from a rigged FBX: the FBX goes to Assets/models/&lt;name&gt;/ with its textures (sidecar
	/// files, a textures folder, or images embedded in the FBX), a .vmat is generated per material, and a .vmdl
	/// is written that keeps every bone (unweighted helpers too), scales the file's units to s&amp;box inches and
	/// remaps the materials - then it is compiled.
	/// </summary>
	public static Task<VmdlCompiler.CompileResult> ImportFbxAsync( string fbxPath )
	{
		if ( AssetsRoot is null ) throw new InvalidOperationException( "Open a project first." );
		var bytes = File.ReadAllBytes( fbxPath );
		var name = Path.GetFileNameWithoutExtension( fbxPath );
		var vmdlPath = DefaultTarget( name );
		var folder = Path.GetDirectoryName( vmdlPath )!;
		Directory.CreateDirectory( folder );
		var fbxDest = Path.Combine( folder, Path.GetFileNameWithoutExtension( vmdlPath ) + ".fbx" );
		File.Copy( fbxPath, fbxDest, true );

		// textures first: generated materials reference them, and the mesh compile bakes material references in
		FbxMaterials.CopySidecarTextures( Path.GetDirectoryName( fbxPath ), folder,
			FbxMaterials.ExtractFbxMaterials( bytes ).SelectMany( m => m.TextureReferences ) );
		FbxMaterials.ExtractEmbeddedTextures( bytes, folder );
		var remaps = FbxMaterials.GenerateMissingVmats( fbxDest );

		var relative = Path.GetRelativePath( AssetsRoot, fbxDest ).Replace( '\\', '/' );
		var scale = 0.3937f * FbxUnitScaleCm( bytes );
		var text = Vmdl.VmdlWriter.GenerateStandalone( "", Array.Empty<Vmdl.AnimEntry>(), scale, "",
			meshFilePath: relative, materialRemaps: remaps );
		text = KeepAllBones( text, FbxBoneNames( bytes ) );
		File.WriteAllText( vmdlPath, text );
		return VmdlCompiler.RegisterAndCompileAsync( vmdlPath, new[] { fbxDest } );
	}

	/// <summary>The skeleton bones (LimbNode / Null joints) an FBX declares, by name.</summary>
	public static List<string> FbxBoneNames( byte[] fbx )
	{
		var names = new List<string>();
		try
		{
			var objects = Formats.Fbx.FbxTokenizer.Parse( fbx ).Child( "Objects" );
			foreach ( var model in objects?.ChildrenNamed( "Model" ) ?? Enumerable.Empty<Formats.Fbx.FbxNode>() )
			{
				if ( model.Properties.Count < 3 || model.Properties[1] is not string raw || model.Properties[2] is not string kind ) continue;
				if ( kind != "LimbNode" && kind != "Root" ) continue;
				names.Add( Formats.Fbx.FbxNode.SplitName( raw ).Name );
			}
		}
		catch { }
		return names;
	}

	/// <summary>
	/// Marks every bone "do not discard" (like the Citizen's bone markup): ModelDoc otherwise drops bones no vertex
	/// is weighted to - often the top bone - which splits the skeleton into separate trees (a shark whose head stays
	/// behind while its body swims away). Bone names are written as the engine stores them too ('.' becomes '_').
	/// </summary>
	public static string KeepAllBones( string vmdlText, IReadOnlyCollection<string> bones )
	{
		if ( bones.Count == 0 ) return vmdlText;
		var doc = Vmdl.Kv3.Parse( vmdlText );
		if ( doc.Root is not Vmdl.KvObject root || root.GetOrNull( "rootNode" ) is not Vmdl.KvObject rootNode
			|| rootNode.GetOrNull( "children" ) is not Vmdl.KvArray children ) return vmdlText;
		var list = children.Items.OfType<Vmdl.KvObject>().FirstOrDefault( n => n.GetString( "_class" ) == "BoneMarkupList" );
		if ( list is null )
		{
			list = new Vmdl.KvObject { ["_class"] = new Vmdl.KvString( "BoneMarkupList" ), ["children"] = new Vmdl.KvArray(), ["bone_cull_type"] = new Vmdl.KvString( "None" ) };
			children.Items.Add( list );
		}
		if ( list.GetOrNull( "children" ) is not Vmdl.KvArray markups ) return vmdlText;
		var names = bones.SelectMany( n => new[] { n, string.Concat( n.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ) ) } )
			.Distinct( StringComparer.Ordinal );
		foreach ( var name in names )
		{
			markups.Items.Add( new Vmdl.KvObject
			{
				["_class"] = new Vmdl.KvString( "BoneMarkup" ),
				["target_bone"] = new Vmdl.KvString( name ),
				["ignore_Translation"] = new Vmdl.KvBool( false ),
				["ignore_rotation"] = new Vmdl.KvBool( false ),
				["do_not_discard"] = new Vmdl.KvBool( true ),
			} );
		}
		return Vmdl.Kv3.Serialize( doc );
	}

	/// <summary>Centimetres per FBX unit (GlobalSettings UnitScaleFactor; 1 = cm, 100 = m). 1 when absent.</summary>
	public static float FbxUnitScaleCm( byte[] fbx )
	{
		try
		{
			var props = Formats.Fbx.FbxTokenizer.Parse( fbx ).Child( "GlobalSettings" )?.Child( "Properties70" );
			var p = props?.ChildrenNamed( "P" ).FirstOrDefault( n => n.Properties.FirstOrDefault() as string == "UnitScaleFactor" );
			if ( p is not null && p.Properties.Count >= 5 && Convert.ToSingle( p.Properties[4], System.Globalization.CultureInfo.InvariantCulture ) is var v && v > 0 && float.IsFinite( v ) )
				return v;
		}
		catch { }
		return 1f;
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
