using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

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
	public static async Task<VmdlCompiler.CompileResult> ImportFbxAsync( string fbxPath )
	{
		if ( AssetsRoot is null ) throw new InvalidOperationException( "Open a project first." );
		var bytes = File.ReadAllBytes( fbxPath );
		var name = Path.GetFileNameWithoutExtension( fbxPath );
		var (meshes, skins) = FbxContents( bytes );
		if ( meshes == 0 )
			throw new InvalidOperationException( $"{Path.GetFileName( fbxPath )} has no mesh - it looks like an animation-only file. Drop the model's FBX (the one with the mesh) instead." );
		if ( skins == 0 )
			throw new InvalidOperationException( $"{Path.GetFileName( fbxPath )} has a mesh that isn't skinned to a skeleton, so it can't be animated. Rig it first (skin the mesh to an armature)." );
		var vmdlPath = DefaultTarget( name );
		var folder = Path.GetDirectoryName( vmdlPath )!;
		Directory.CreateDirectory( folder );
		var fbxDest = Path.Combine( folder, Path.GetFileNameWithoutExtension( vmdlPath ) + ".fbx" );
		File.Copy( fbxPath, fbxDest, true );

		// textures first: generated materials reference them, and the mesh compile bakes material references in
		// best effort: a file the texture reader can't follow still becomes a model (with plain materials)
		IEnumerable<string> referenced;
		try { referenced = FbxMaterials.ExtractFbxMaterials( bytes ).SelectMany( m => m.TextureReferences ).ToList(); }
		catch ( Exception e ) when ( e is FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException )
		{
			Log.Warning( $"[text-to-animation] couldn't read the materials in {Path.GetFileName( fbxPath )}: {e.Message}" );
			referenced = Array.Empty<string>();
		}
		FbxMaterials.CopySidecarTextures( Path.GetDirectoryName( fbxPath ), folder, referenced );
		try { FbxMaterials.ExtractEmbeddedTextures( bytes, folder ); }
		catch ( Exception e ) when ( e is FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException ) { }
		CopyTextureFolders( Path.GetDirectoryName( fbxPath ), folder );
		var remaps = FbxMaterials.GenerateMissingVmats( fbxDest );

		var relative = Path.GetRelativePath( AssetsRoot, fbxDest ).Replace( '\\', '/' );
		var scale = 0.3937f * FbxUnitScaleCm( bytes );
		var bones = FbxBoneNames( bytes );
		string Write( float s ) => KeepAllBones( Core.Vmdl.VmdlWriter.GenerateStandalone( "", Array.Empty<Core.Vmdl.AnimEntry>(), s, "",
			meshFilePath: relative, materialRemaps: remaps ), bones );
		File.WriteAllText( vmdlPath, Write( scale ) );
		var result = await VmdlCompiler.RegisterAndCompileAsync( vmdlPath, new[] { fbxDest } );
		if ( !result.Compiled ) return result;

		// many exporters (Houdini, some Blender setups) write metres without saying so: the file then reads as
		// centimetres and the model comes out a hundred times too small. A game creature is not under 4 inches.
		await EngineThread.SwitchToMainThread();
		var model = await ModelBridge.LoadAsync( result.Asset.Path );
		if ( model is not null && !model.IsError && ModelBridge.SkeletonProblem( model ) is { } broken )
			return new VmdlCompiler.CompileResult { Compiled = false, Asset = result.Asset, Error = broken };
		if ( model is not null && !model.IsError && model.BoneCount == 0 )
			return new VmdlCompiler.CompileResult { Compiled = false, Asset = result.Asset,
				Error = $"{Path.GetFileName( fbxPath )} is skinned, but s&box found no skeleton bones in it - it's usually an old-style rig skinned to ordinary objects. Convert those objects to an armature/joints in your 3D app and export again." };
		var size = model is null || model.IsError ? 0f : MathF.Max( model.Bounds.Size.x, MathF.Max( model.Bounds.Size.y, model.Bounds.Size.z ) );
		if ( size > 0f && size < 4f )
		{
			File.WriteAllText( vmdlPath, Write( scale * 100f ) );
			var rescaled = await VmdlCompiler.RegisterAndCompileAsync( vmdlPath, new[] { fbxDest } );
			if ( rescaled.Compiled )
			{
				rescaled.Note = $"{Path.GetFileName( fbxPath )} came out {size:0.##} in long - its units look like metres, so it was scaled up 100x. Change the ScaleAndMirror scale in the vmdl if that's wrong.";
				return rescaled;
			}
		}
		return result;
	}

	/// <summary>
	/// Downloads keep textures in folders with all kinds of names ("Textures_and_Materials", "Tex", "Materials",
	/// "Maps"): images in sibling or child folders whose name says so are copied to &lt;model&gt;/textures, so the
	/// material detection can match them to the FBX's materials.
	/// </summary>
	static void CopyTextureFolders( string sourceDir, string destDir )
	{
		if ( string.IsNullOrEmpty( sourceDir ) || !Directory.Exists( sourceDir ) ) return;
		static bool TextureLike( string dir )
		{
			var name = Path.GetFileName( dir ).ToLowerInvariant();
			return name.Contains( "tex" ) || name.Contains( "material" ) || name.Contains( "map" ) || name == "images";
		}
		var folders = Directory.GetDirectories( sourceDir ).Where( TextureLike ).ToList();
		var parent = Path.GetDirectoryName( sourceDir );
		if ( parent is not null && Directory.Exists( parent ) )
			folders.AddRange( Directory.GetDirectories( parent ).Where( d => TextureLike( d ) && !string.Equals( d, sourceDir, StringComparison.OrdinalIgnoreCase ) ) );
		var images = new[] { ".png", ".jpg", ".jpeg", ".tga", ".dds", ".webp" };
		foreach ( var folder in folders.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			foreach ( var file in Directory.GetFiles( folder, "*", SearchOption.AllDirectories ).Where( f => images.Contains( Path.GetExtension( f ).ToLowerInvariant() ) ).Take( 400 ) )
			{
				var dest = Path.Combine( destDir, "textures", Path.GetFileName( file ) );
				if ( File.Exists( dest ) ) continue;
				try
				{
					Directory.CreateDirectory( Path.GetDirectoryName( dest )! );
					File.Copy( file, dest );
					EngineThread.Try( () => AssetSystem.RegisterFile( dest ) );
				}
				catch ( IOException ) { }
			}
		}
	}

	/// <summary>An FBX object's type: its last string property ("Mesh", "Skin", "LimbNode").</summary>
	static string Kind( Formats.Fbx.FbxNode node ) => node.Properties.OfType<string>().LastOrDefault();

	/// <summary>How many mesh geometries and skin deformers an FBX holds (0 meshes: an animation-only file).</summary>
	public static (int Meshes, int Skins) FbxContents( byte[] fbx )
	{
		try
		{
			var objects = Formats.Fbx.FbxTokenizer.Parse( fbx ).Child( "Objects" );
			if ( objects is null ) return (0, 0);
			// FBX 7 keeps meshes in Geometry objects; FBX 6 inside Model objects (with their vertices). The type is
			// the last string property in both ("Mesh", "Skin", "LimbNode")
			var meshes = objects.ChildrenNamed( "Geometry" ).Count( g => Kind( g ) == "Mesh" )
				+ objects.ChildrenNamed( "Model" ).Count( m => Kind( m ) == "Mesh" && m.Child( "Vertices" ) is not null );
			var skins = objects.ChildrenNamed( "Deformer" ).Count( d => Kind( d ) == "Skin" );
			return (meshes, skins);
		}
		catch { return (1, 1); } // unreadable here: let the engine's importer decide
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
				var kind = Kind( model );
				if ( kind is not ("LimbNode" or "Limb" or "Root") ) continue;
				var raw = model.Properties.OfType<string>().FirstOrDefault();
				if ( raw is null ) continue;
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
		var doc = Core.Vmdl.Kv3.Parse( vmdlText );
		if ( doc.Root is not Core.Vmdl.KvObject root || root.GetOrNull( "rootNode" ) is not Core.Vmdl.KvObject rootNode
			|| rootNode.GetOrNull( "children" ) is not Core.Vmdl.KvArray children ) return vmdlText;
		var list = children.Items.OfType<Core.Vmdl.KvObject>().FirstOrDefault( n => n.GetString( "_class" ) == "BoneMarkupList" );
		if ( list is null )
		{
			list = new Core.Vmdl.KvObject { ["_class"] = new Core.Vmdl.KvString( "BoneMarkupList" ), ["children"] = new Core.Vmdl.KvArray(), ["bone_cull_type"] = new Core.Vmdl.KvString( "None" ) };
			children.Items.Add( list );
		}
		if ( list.GetOrNull( "children" ) is not Core.Vmdl.KvArray markups ) return vmdlText;
		var names = bones.SelectMany( n => new[] { n, string.Concat( n.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ) ) } )
			.Distinct( StringComparer.Ordinal );
		foreach ( var name in names )
		{
			markups.Items.Add( new Core.Vmdl.KvObject
			{
				["_class"] = new Core.Vmdl.KvString( "BoneMarkup" ),
				["target_bone"] = new Core.Vmdl.KvString( name ),
				["ignore_Translation"] = new Core.Vmdl.KvBool( false ),
				["ignore_rotation"] = new Core.Vmdl.KvBool( false ),
				["do_not_discard"] = new Core.Vmdl.KvBool( true ),
			} );
		}
		return Core.Vmdl.Kv3.Serialize( doc );
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
