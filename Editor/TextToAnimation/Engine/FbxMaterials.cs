using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.Editor.Engine;

/// <summary>
/// Material and texture detection for rigged FBX models, vendored from humanoid-retargeter's EditorPipeline:
/// reads the material-to-texture links the FBX authors (by object id, so file names don't matter), copies
/// sidecar textures, extracts embedded ones, and writes a .vmat per material (complex.shader with colour,
/// normal, roughness, metalness, AO, emissive and opacity, alpha-test for hair/lashes/fur) plus the
/// MaterialGroupList remaps the vmdl needs.
/// </summary>
public static partial class FbxMaterials
{
	public sealed class SourceMaterialInfo
	{
		public string Name { get; init; }
		public string ImportName { get; init; }
		public IEnumerable<string> TextureReferences => new[] { ColorTexture, NormalTexture,
			RoughnessTexture, MetalnessTexture, OcclusionTexture, EmissiveTexture, OpacityTexture }
			.Where( path => !string.IsNullOrWhiteSpace( path ) );
		public string ColorTexture { get; set; }
		public System.Numerics.Vector3? ColorFactor { get; set; }
		public bool VertexColors { get; set; }
		public string NormalTexture { get; set; }
		public string RoughnessTexture { get; set; }
		public string MetalnessTexture { get; set; }
		public string OcclusionTexture { get; set; }
		public string EmissiveTexture { get; set; }
		public string OpacityTexture { get; set; }
		public bool AlphaTest { get; set; }
		public bool Translucent { get; set; }
		public bool DoubleSided { get; set; }
		public float AlphaCutoff { get; set; } = 0.5f;
	}

	/// <summary>Reads the material→texture links authored in an FBX. Matching through
	/// object IDs makes texture filenames irrelevant (TrumpLPmat → tumpLPcolors.png).</summary>
	public static List<SourceMaterialInfo> ExtractFbxMaterials( byte[] data )
	{
		var root = TextToAnimation.Editor.Formats.Fbx.FbxTokenizer.Parse( data );
		var objects = root.Child( "Objects" );
		var connections = root.Child( "Connections" );
		var materials = new Dictionary<long, SourceMaterialInfo>();
		var textures = new Dictionary<long, string>();
		var videos = new Dictionary<long, string>();
		var doubleSidedModels = new HashSet<long>();

		if ( objects is not null )
		{
			foreach ( var node in objects.Children )
			{
				if ( node.Properties.Count < 2 || node.Properties[0] is not (long or int)
					|| node.Properties[1] is not string rawName )
					continue;
				var id = node.Prop<long>( 0 );
				if ( node.Name == "Model" && string.Equals(
					node.Child( "Culling" )?.Properties.FirstOrDefault() as string,
					"CullingOff", StringComparison.OrdinalIgnoreCase ) )
					doubleSidedModels.Add( id );
				if ( node.Name == "Material" )
				{
					materials[id] = new SourceMaterialInfo
					{
						Name = TextToAnimation.Editor.Formats.Fbx.FbxNode.SplitName( rawName ).Name,
						// Native FBX import retains literal Class:: prefixes in binary exports.
						ImportName = rawName.Split( '\0' )[0],
						ColorFactor = TextToAnimation.Editor.Formats.Fbx.FbxMaterialColor.Read( node ),
					};
				}
				else if ( node.Name is "Texture" or "Video" )
				{
					var file = node.Children.FirstOrDefault( child =>
						child.Name.Equals( "RelativeFilename", StringComparison.OrdinalIgnoreCase ) )
						?? node.Children.FirstOrDefault( child =>
							child.Name.Equals( "FileName", StringComparison.OrdinalIgnoreCase )
							|| child.Name.Equals( "Filename", StringComparison.OrdinalIgnoreCase ) );
					if ( file?.Properties.FirstOrDefault() is string path )
					{
						if ( node.Name == "Texture" ) textures[id] = path;
						else videos[id] = path;
					}
				}
			}
		}

		if ( connections is null )
			return materials.Values.ToList();

		var coloredGeometry = objects?.Children.Where( n => n.Name == "Geometry"
			&& TextToAnimation.Editor.Formats.Fbx.FbxMaterialColor.HasVertexColors( n ) )
			.Select( n => n.Prop<long>( 0 ) ).ToHashSet() ?? new HashSet<long>();
		var coloredModels = connections.ChildrenNamed( "C" )
			.Where( n => n.Properties.Count >= 3 && n.Properties[0] is "OO"
				&& n.Properties[1] is long or int && n.Properties[2] is long or int
				&& coloredGeometry.Contains( n.Prop<long>( 1 ) ) )
			.Select( n => n.Prop<long>( 2 ) ).ToHashSet();

		// Video objects commonly carry the only usable filename and parent a Texture.
		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| kind != "OO" || connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			if ( videos.TryGetValue( source, out var file ) && !textures.ContainsKey( target ) )
				textures[target] = file;
		}

		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			// FBX stores sidedness on the mesh model, not its material.
			if ( kind == "OO" && coloredModels.Contains( target )
				&& materials.TryGetValue( source, out var coloredMaterial ) )
				coloredMaterial.VertexColors = true;
			if ( kind == "OO" && doubleSidedModels.Contains( target )
				&& materials.TryGetValue( source, out var boundMaterial ) )
				boundMaterial.DoubleSided = true;
			if ( !textures.TryGetValue( source, out var file )
				|| !materials.TryGetValue( target, out var material ) )
				continue;
			var channel = kind == "OP" && connection.Properties.Count >= 4
				&& connection.Properties[3] is string property ? property : "DiffuseColor";
			if ( channel.Contains( "transparent", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "transparency", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "opacity", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "alpha", StringComparison.OrdinalIgnoreCase ) )
			{
				material.OpacityTexture ??= file;
				material.Translucent = true;
			}
			else if ( channel.Contains( "normal", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "bump", StringComparison.OrdinalIgnoreCase ) )
				material.NormalTexture ??= file;
			else if ( channel.Contains( "rough", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "gloss", StringComparison.OrdinalIgnoreCase ) )
				material.RoughnessTexture ??= file;
			else if ( channel.Contains( "metal", StringComparison.OrdinalIgnoreCase ) )
				material.MetalnessTexture ??= file;
			else if ( channel.Contains( "occlusion", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "ambient", StringComparison.OrdinalIgnoreCase ) )
				material.OcclusionTexture ??= file;
			else if ( channel.Contains( "emissive", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "emission", StringComparison.OrdinalIgnoreCase ) )
				material.EmissiveTexture ??= file;
			else if ( channel.Contains( "diffuse", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "color", StringComparison.OrdinalIgnoreCase ) )
				material.ColorTexture ??= file;
		}
		return materials.Values.ToList();
	}


	public static IReadOnlyDictionary<string, string> GenerateMissingVmats( string modelAbsolutePath )
	{
		try
		{
			var assetsPath = Project.Current?.GetAssetsPath();
			if ( assetsPath is null )
				return null;

			var modelBytes = File.ReadAllBytes( modelAbsolutePath );
			var directory = Path.GetDirectoryName( modelAbsolutePath );
			var extension = Path.GetExtension( modelAbsolutePath ).ToLowerInvariant();
			var materialInfo = extension == ".fbx"
				? ExtractFbxMaterials( modelBytes )
				: new List<SourceMaterialInfo>();
			var materials = materialInfo.Select( m => m.Name ).ToList();
			if ( materials.Count == 0 )
			{
				// An FBX with NO material objects at all (bare Blender export): the engine
				// derives the material slot from the GEOMETRY name and then wants
				// '<geometry>.vmat' (observed: mesh 'Cube' -> Missing vmat "cube.vmat",
				// an unsatisfiable illegal-resource lookup). Stub those instead.
				materials = extension == ".fbx"
					? ExtractFbxObjectNames( modelBytes, "Geometry" )
					.Select( n => n.ToLowerInvariant() )
					.Distinct()
					.ToList()
					: new List<string>();
				if ( materials.Count == 0 )
					return null;
				Log.Info( $"[text-to-animation] FBX carries no materials - stubbing vmats "
					+ $"for its geometry slots: {string.Join( ", ", materials )}" );
			}

			var textures = new List<string>();
			foreach ( var pattern in new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.dds", "*.webp" } )
			{
				textures.AddRange( Directory.GetFiles( directory, pattern ) );
				var texturesDir = Path.Combine( directory, "textures" );
				if ( Directory.Exists( texturesDir ) )
					textures.AddRange( Directory.GetFiles( texturesDir, pattern, SearchOption.AllDirectories ) );
			}

			textures.RemoveAll( path => path.EndsWith( "_hr.png", StringComparison.OrdinalIgnoreCase )
				|| path.EndsWith( "_hr_alpha.png", StringComparison.OrdinalIgnoreCase ) );

			// Tokens shared by most of the texture set (the character's base name, e.g.
			// dante+dark) must never decide a match on their own - "mi_danteDark_vest"
			// would otherwise take the hair texture purely on those (observed: the hair
			// mask ended up on the eyes). A match needs at least one DISTINCTIVE token.
			var tokenCounts = new Dictionary<string, int>( StringComparer.Ordinal );
			foreach ( var candidate in textures )
			{
				foreach ( var token in NameTokens( Path.GetFileNameWithoutExtension( candidate ) ) )
					tokenCounts[token] = tokenCounts.GetValueOrDefault( token ) + 1;
			}
			var ubiquitous = tokenCounts
				.Where( kv => kv.Value >= 2 && kv.Value * 2 >= textures.Count )
				.Select( kv => kv.Key )
				.ToHashSet( StringComparer.Ordinal );

			var remaps = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var material in materials )
			{
				var safeMaterial = new string( material
					.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ).ToArray() );
				if ( string.IsNullOrEmpty( safeMaterial ) )
					safeMaterial = "material";
				var vmatPath = Path.Combine( directory, safeMaterial + ".vmat" );
				// Remap the BARE reference the mesh carries to the real file, generated or
				// pre-existing (resource paths are lowercase by engine convention).
				remaps[material.ToLowerInvariant() + ".vmat"] =
					Path.GetRelativePath( assetsPath, vmatPath ).Replace( '\\', '/' ).ToLowerInvariant();
				var authored = materialInfo.FirstOrDefault( m =>
					string.Equals( m.Name, material, StringComparison.OrdinalIgnoreCase ) );
				if ( !string.IsNullOrEmpty( authored?.ImportName ) )
					remaps[authored.ImportName.ToLowerInvariant() + ".vmat"] = remaps[material.ToLowerInvariant() + ".vmat"];
				if ( File.Exists( vmatPath ) )
				{
					// Files still carrying the auto-generated header are ours to UPGRADE -
					// vmats from an older library version keep old defects forever
					// otherwise (user report: opaque eyelashes generated before alpha-test
					// support existed). Deleting the header line makes manual edits
					// permanent.
					try
					{
						using var reader = new StreamReader( vmatPath );
						if ( reader.ReadLine()?.Contains( "Auto-generated by text-to-animation" ) != true )
							continue;
					}
					catch
					{
						continue;
					}
				}

				// Suffix conventions collected from real exports (Sketchfab rips, Unity
				// packs, Blender/Substance/Marmoset outputs) - the user's assets keep
				// arriving with new ones, so every known spelling is listed.
				var color = FindAuthoredTexture( authored?.ColorTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{
						"_d", "_dm", "_dif", "_diff", "_diffuse", "diffuse",
						"_alb", "_albedo", "albedo", "_basecolor", "_base_color", "basecolor",
						"_bc", "_col", "_color", "_colour", "color", "_clr", "_base",
					} )
					// Stand-in when the set ships no diffuse for this material (real case:
					// a vest with only a specular map): a distinctively NAMED non-normal
					// map carries the garment's actual detail and reads far better than
					// flat placeholder white.
					?? BestTextureMatch( material, textures, new[]
					{
						"_s", "_spec", "_specular", "_m", "_metal", "_metallic", "_metalness",
						"_ao", "_occlusion", "_mask", "_e", "_emissive", "_emission", "_glow",
					} )
					// Last resort: ANY distinctively named non-normal image. Plain-named
					// texture sets carry no suffix at all (real case: material "homer"
					// shipping "homer.png" - both suffix passes skipped it and the model
					// rendered untextured).
					?? BestTextureMatch( material, textures
						.Where( t => !Path.GetFileNameWithoutExtension( t )
							.ToLowerInvariant().EndsWith( "_n" ) )
						.ToList(), new[] { "" } )
					?? SingleColorTexture( textures );
				var normal = FindAuthoredTexture( authored?.NormalTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_n", "_nrm", "_nm", "_nor", "_norm", "_normal", "normal", "_normalmap", "_bump" } );
				var rough = FindAuthoredTexture( authored?.RoughnessTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_r", "_rough", "_roughness", "roughness", "_g", "_gloss", "_glossiness" } );
				var metal = FindAuthoredTexture( authored?.MetalnessTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_m", "_metal", "_metallic", "_metalness", "metallic" } );
				var occlusion = FindAuthoredTexture( authored?.OcclusionTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_ao", "_occlusion", "_ambientocclusion" } );
				var emissive = FindAuthoredTexture( authored?.EmissiveTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_e", "_emissive", "_emission", "_glow" } );
				var opacity = FindAuthoredTexture( authored?.OpacityTexture, textures )
					?? BestTextureMatch( material, textures, new[]
					{ "_a", "_alpha", "_opacity", "_trans", "_transparency" } );

				// Card/strand geometry (lashes, hair, brows, anything the modeler named
				// "masked") is authored for alpha testing - rendered opaque it shows as
				// solid white sheets (user report: "the makeup around the eye is white").
				var materialTokens = NameTokens( material );
				var alphaTest = authored?.AlphaTest == true || (authored?.Translucent != true
					&& materialTokens.Any( token =>
						token is "mask" or "masked" or "lash" or "lashes" or "eyelash" or "eyelashes"
							or "hair" or "hairs" or "brow" or "brows" or "eyebrow" or "eyebrows"
							or "fur" or "feather" or "feathers" ));
				var translucent = authored?.Translucent == true || opacity is not null;
				if ((alphaTest || translucent) && opacity is null)
					opacity = color; // common packed RGBA texture (including glTF baseColor)
				if (opacity is not null && string.Equals(opacity, color, StringComparison.OrdinalIgnoreCase))
				{
					opacity = ExtractPackedAlpha(opacity);
					// Exporters often connect the diffuse image to opacity even when every
					// pixel is opaque. Do not put that surface in the translucent sorting pass.
					if ( opacity is null )
					{
						alphaTest = false;
						translucent = false;
					}
				}

				color = PrepareTexture( color );
				normal = PrepareTexture( normal );
				rough = PrepareTexture( rough );
				metal = PrepareTexture( metal );
				occlusion = PrepareTexture( occlusion );
				emissive = PrepareTexture( emissive );
				opacity = PrepareTexture( opacity );

				var builder = new System.Text.StringBuilder();
				builder.AppendLine( "// Auto-generated by text-to-animation from the target model's material list." );
				builder.AppendLine( "// Regenerated on conversion while this header stays - DELETE THE LINE ABOVE to make manual edits permanent." );
				builder.AppendLine( "Layer0" );
				builder.AppendLine( "{" );
				builder.AppendLine( authored?.VertexColors == true && color is null
					? "\tshader \"shaders/vertex_color.shader\""
					: "\tshader \"shaders/complex.shader\"" );
				if ( alphaTest )
				{
					builder.AppendLine( "\tF_ALPHA_TEST 1" );
					builder.AppendLine( $"\tg_flAlphaTestReference \"{(authored?.AlphaCutoff ?? 0.5f).ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture )}\"" );
				}
				if ( translucent )
					builder.AppendLine( "\tF_TRANSLUCENT 1" );
				if ( authored?.DoubleSided == true )
					builder.AppendLine( "\tF_RENDER_BACKFACES 1" );
				builder.AppendLine( $"\tTextureColor \"{(color ?? "materials/default/default_color.tga")}\"" );
				if ( color is null && authored?.ColorFactor is { } tint )
					builder.AppendLine( FormattableString.Invariant(
						$"\tg_vColorTint \"[{tint.X:R} {tint.Y:R} {tint.Z:R} 1]\"" ) );
				if ( opacity is not null )
					builder.AppendLine( $"\tTextureTranslucency \"{opacity}\"" );
				builder.AppendLine( $"\tTextureNormal \"{(normal ?? "materials/default/default_normal.tga")}\"" );
				builder.AppendLine( $"\tTextureRoughness \"{(rough ?? "materials/default/default_rough.tga")}\"" );
				if ( metal is not null )
				{
					builder.AppendLine( "\tF_METALNESS_TEXTURE 1" );
					builder.AppendLine( $"\tTextureMetalness \"{metal}\"" );
				}
				if ( occlusion is not null )
					builder.AppendLine( $"\tTextureAmbientOcclusion \"{occlusion}\"" );
				if ( emissive is not null )
				{
					builder.AppendLine( "\tF_SELF_ILLUM 1" );
					builder.AppendLine( $"\tTextureSelfIllumMask \"{emissive}\"" );
				}
				builder.AppendLine( "}" );
				File.WriteAllText( vmatPath, builder.ToString() );
				Try( () => AssetSystem.RegisterFile( vmatPath ) );
				Log.Info( $"[text-to-animation] generated material {Path.GetFileName( vmatPath )} "
					+ $"(color: {color ?? "default"}, normal: {normal ?? "default"}, rough: {rough ?? "default"})" );
			}

			return remaps.Count > 0 ? remaps : null;

			// The texture compiler rejects JPEG's .jpeg extension and WebP. Keep matching
			// against the authored files, then convert the selected image for every channel.
			string PrepareTexture( string relative )
			{
				if ( relative is null || Path.GetExtension( relative ).ToLowerInvariant() is not (".jpeg" or ".webp") )
					return relative;
				var source = Path.Combine( assetsPath, relative );
				var output = source + "_hr.png";
				using var bitmap = SkiaSharp.SKBitmap.Decode( source )
					?? throw new FormatException( $"Cannot decode texture '{relative}'." );
				using var image = SkiaSharp.SKImage.FromBitmap( bitmap );
				using var data = image.Encode( SkiaSharp.SKEncodedImageFormat.Png, 100 );
				using ( var stream = File.Open( output, FileMode.Create, FileAccess.Write, FileShare.Read ) )
					data.SaveTo( stream );
				Try( () => AssetSystem.RegisterFile( output ) );
				return Path.GetRelativePath( assetsPath, output ).Replace( '\\', '/' );
			}

			string FindAuthoredTexture( string reference, List<string> candidates )
			{
				if ( string.IsNullOrEmpty( reference ) )
					return null;
				var decoded = Uri.UnescapeDataString( reference.Split( '?', '#' )[0] )
					.Replace( '/', Path.DirectorySeparatorChar );
				if ( !Path.IsPathRooted( decoded ) )
				{
					var direct = Path.GetFullPath( Path.Combine( directory, decoded ) );
					var root = Path.GetFullPath( directory ).TrimEnd( Path.DirectorySeparatorChar )
						+ Path.DirectorySeparatorChar;
					if ( direct.StartsWith( root, StringComparison.OrdinalIgnoreCase ) && File.Exists( direct ) )
						return Path.GetRelativePath( assetsPath, direct ).Replace( '\\', '/' );
				}
				var name = Path.GetFileName( decoded );
				var stem = Path.GetFileNameWithoutExtension( name );
				var match = candidates.FirstOrDefault( candidate =>
					string.Equals( Path.GetFileName( candidate ), name, StringComparison.OrdinalIgnoreCase ) )
					?? candidates.FirstOrDefault( candidate => string.Equals(
						Path.GetFileNameWithoutExtension( candidate ), stem,
						StringComparison.OrdinalIgnoreCase ) );
				return match is null ? null
					: Path.GetRelativePath( assetsPath, match ).Replace( '\\', '/' );
			}

			// complex.shader's TextureTranslucency input reads a grayscale image; it does
			// not implicitly select TextureColor.A. Preserve packed-RGBA materials by
			// extracting that authored alpha channel beside the copied source texture.
			string ExtractPackedAlpha( string relative )
			{
				try
				{
					var source = Path.GetFullPath( Path.Combine(
						assetsPath, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
					using var bitmap = SkiaSharp.SKBitmap.Decode( source );
					if ( bitmap is null || bitmap.Width == 0 || bitmap.Height == 0 )
						return null;

					var hasAlpha = false;
					for ( var y = 0; y < bitmap.Height && !hasAlpha; y++ )
					{
						for ( var x = 0; x < bitmap.Width; x++ )
						{
							if ( bitmap.GetPixel( x, y ).Alpha < 255 )
							{
								hasAlpha = true;
								break;
							}
						}
					}
					if ( !hasAlpha )
						return null;

					using var mask = new SkiaSharp.SKBitmap(
						bitmap.Width, bitmap.Height, SkiaSharp.SKColorType.Rgba8888,
						SkiaSharp.SKAlphaType.Opaque );
					for ( var y = 0; y < bitmap.Height; y++ )
					{
						for ( var x = 0; x < bitmap.Width; x++ )
						{
							var alpha = bitmap.GetPixel( x, y ).Alpha;
							mask.SetPixel( x, y, new SkiaSharp.SKColor( alpha, alpha, alpha ) );
						}
					}

					var output = Path.Combine( Path.GetDirectoryName( source ),
						Path.GetFileNameWithoutExtension( source ) + "_hr_alpha.png" );
					using var image = SkiaSharp.SKImage.FromBitmap( mask );
					using var data = image.Encode( SkiaSharp.SKEncodedImageFormat.Png, 100 );
					using ( var stream = File.Open( output, FileMode.Create, FileAccess.Write, FileShare.Read ) )
						data.SaveTo( stream );
					Try( () => AssetSystem.RegisterFile( output ) );
					return Path.GetRelativePath( assetsPath, output ).Replace( '\\', '/' );
				}
				catch ( Exception e )
				{
					Log.Warning( $"[text-to-animation] packed alpha extraction failed: {e.Message}" );
					return null;
				}
			}

			string SingleColorTexture( List<string> candidates )
			{
				var plausible = candidates.Where( candidate =>
				{
					var tokens = NameTokens( Path.GetFileNameWithoutExtension( candidate ) );
					return !tokens.Any( token => token is "n" or "nrm" or "normal" or "bump"
						or "rough" or "roughness" or "gloss" or "metal" or "metallic"
						or "metalness" or "ao" or "occlusion" );
				} ).ToList();
				return plausible.Count == 1
					? Path.GetRelativePath( assetsPath, plausible[0] ).Replace( '\\', '/' )
					: null;
			}

			string BestTextureMatch( string material, List<string> candidates, string[] suffixes )
			{
				var materialTokens = NameTokens( material );
				var scored = new List<(string Path, int Score)>();
				foreach ( var candidate in candidates )
				{
					// Tokenize the RAW stem: lower-casing first would erase its camelCase
					// boundaries ("t_danteDark_head_d" -> one "dantedark" token that can
					// never match the material's dante+dark tokens - observed as the head
					// and lower body rendering untextured white while the arms worked).
					var stem = Path.GetFileNameWithoutExtension( candidate );
					// Numbered layer variants ("eye_diff", "eye_diff2", "eye_diff3") are
					// all diffuse CANDIDATES - suffixes match with trailing digits ignored.
					var stemNoDigits = stem.TrimEnd( '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' );
					if ( !suffixes.Any( s => stem.EndsWith( s, StringComparison.OrdinalIgnoreCase )
						|| stemNoDigits.EndsWith( s, StringComparison.OrdinalIgnoreCase ) ) )
						continue;
					var shared = NameTokens( stem ).Where( materialTokens.Contains ).ToList();
					var distinctive = shared.Count( t => !ubiquitous.Contains( t ) );
					scored.Add( (candidate, distinctive * 10 + shared.Count) );
				}
				if ( scored.Count == 0 )
					return null;

				var bestScore = scored.Max( c => c.Score );
				var top = scored.Where( c => c.Score == bestScore ).ToList();
				var variantSet = top.All( c => SameVariantFamily( top[0].Path, c.Path ) );

				// A DISTINCTIVE shared token (score >= 10) always wins. Base-name-only
				// overlap is accepted only when unambiguous: single-set exports name
				// everything '<character>_*' ("sonic_mat" + "sonic_diff" is the only
				// diffuse that shares anything - a correct match the old distinctive-only
				// rule rejected, body rendered untextured). Base-only ties across
				// DIFFERENT names stay rejected (the case that mapped hair onto the
				// eyes); numbered variants of ONE name are a layer set, decided below.
				if ( bestScore == 0 || (bestScore < 10 && !variantSet) )
					return null;

				// Composite-shader layer sets ship several same-named images (a mobile
				// eye: gray ball with black pupil + white sclera mask + catchlight dot;
				// the game blends them in a custom shader). A single stand-in must be the
				// layer a viewer would call "the texture": the one with the BRIGHTEST
				// CENTRAL region - masks and catchlights are black-centered, and the
				// pupil-hole layer rendered Sonic's eyes solid black.
				var best = variantSet && top.Count > 1
					? top.OrderByDescending( CenterBrightness ).First().Path
					: top[0].Path;
				if ( variantSet && top.Count > 1 )
					Log.Info( $"[text-to-animation] '{material}': picked "
						+ $"{Path.GetFileName( best )} from {top.Count} layer variants by center brightness" );
				return Path.GetRelativePath( assetsPath, best ).Replace( '\\', '/' );
			}

			static bool SameVariantFamily( string a, string b )
			{
				static string Family( string p ) => Path.GetFileNameWithoutExtension( p )
					.TrimEnd( '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' )
					.ToLowerInvariant();
				return Family( a ) == Family( b );
			}

			// Mean luminance of the central half of the image, sparsely sampled.
			static float CenterBrightness( (string Path, int Score) candidate )
			{
				try
				{
					using var bitmap = SkiaSharp.SKBitmap.Decode( candidate.Path );
					if ( bitmap is null || bitmap.Width == 0 || bitmap.Height == 0 )
						return -1f;
					float sum = 0;
					var samples = 0;
					var stepX = Math.Max( 1, bitmap.Width / 32 );
					var stepY = Math.Max( 1, bitmap.Height / 32 );
					for ( var y = bitmap.Height / 4; y < bitmap.Height * 3 / 4; y += stepY )
					{
						for ( var x = bitmap.Width / 4; x < bitmap.Width * 3 / 4; x += stepX )
						{
							var c = bitmap.GetPixel( x, y );
							sum += (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue)
								* (c.Alpha / 255f) / 255f;
							samples++;
						}
					}
					return samples > 0 ? sum / samples : -1f;
				}
				catch
				{
					return -1f; // undecodable: rank below anything readable
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[text-to-animation] vmat generation failed: {e.Message}" );
			return null;
		}
	}


	static HashSet<string> NameTokens( string name )
	{
		var tokens = new HashSet<string>( StringComparer.Ordinal );
		var current = new System.Text.StringBuilder();
		void Commit()
		{
			if ( current.Length > 0 )
			{
				var token = current.ToString().ToLowerInvariant();
				if ( token is not ("mi" or "m" or "t" or "tex") )
					tokens.Add( token );
				current.Clear();
			}
		}
		for ( var i = 0; i < name.Length; i++ )
		{
			var c = name[i];
			if ( !char.IsLetterOrDigit( c ) )
			{
				Commit();
				continue;
			}
			if ( char.IsUpper( c ) && current.Length > 0 && char.IsLower( name[i - 1] ) )
				Commit();
			current.Append( c );
		}
		Commit();
		return tokens;
	}

	/// <summary>Material object names from an FBX (see <see cref="ExtractFbxObjectNames"/>).</summary>

	internal static List<string> ExtractFbxObjectNames( byte[] data, string className )
	{
		var names = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		void Add( string name )
		{
			name = name.Trim();
			if ( name.Length is > 0 and <= 80 && seen.Add( name ) )
				names.Add( name );
		}

		// Binary: <name> 0x00 0x01 <Class>
		var marker = System.Text.Encoding.ASCII.GetBytes( "\0\u0001" + className );
		for ( var i = IndexOfBytes( data, marker, 0 ); i >= 0; i = IndexOfBytes( data, marker, i + 1 ) )
		{
			var start = i;
			while ( start > 0 && data[start - 1] >= 0x20 && data[start - 1] < 0x7f )
				start--;
			if ( i - start is > 0 and <= 80 )
				Add( System.Text.Encoding.ASCII.GetString( data, start, i - start ) );
		}

		// ASCII: <Class>::<name>
		var ascii = System.Text.Encoding.ASCII.GetBytes( className + "::" );
		for ( var i = IndexOfBytes( data, ascii, 0 ); i >= 0; i = IndexOfBytes( data, ascii, i + 1 ) )
		{
			var start = i + ascii.Length;
			var end = start;
			while ( end < data.Length && data[end] != (byte)'"' && data[end] >= 0x20 && data[end] < 0x7f )
				end++;
			if ( end - start is > 0 and <= 80 )
				Add( System.Text.Encoding.ASCII.GetString( data, start, end - start ) );
		}

		return names;
	}

	static int IndexOfBytes( byte[] haystack, byte[] needle, int from )
	{
		for ( var i = Math.Max( from, 0 ); i <= haystack.Length - needle.Length; i++ )
		{
			var match = true;
			for ( var j = 0; j < needle.Length; j++ )
			{
				if ( haystack[i + j] != needle[j] ) { match = false; break; }
			}
			if ( match )
				return i;
		}
		return -1;
	}


	static readonly string[] TextureExtensions =
		{ ".png", ".jpg", ".jpeg", ".tga", ".dds", ".webp", ".vmat", ".vtex" };

	/// <summary>Copies texture sidecars of a picked target model into the output folder:
	/// loose image files next to it, and a "textures" folder next to it or next to its
	/// parent (the source/-plus-textures/ layout). Per-file best effort - a failed texture
	/// must never fail the conversion.</summary>
	public static void CopySidecarTextures( string sourceDir, string destDir, IEnumerable<string> references = null )
	{
		try
		{
			if ( sourceDir is null || destDir is null )
				return;
			sourceDir = Path.GetFullPath( sourceDir );
			destDir = Path.GetFullPath( destDir );
			if ( string.Equals( sourceDir, destDir, StringComparison.OrdinalIgnoreCase ) )
				return;

			// Preserve model-local authored paths; do not flatten filenames or copy an
			// arbitrary parent directory when an exporter supplies an external path.
			foreach ( var reference in references ?? Enumerable.Empty<string>() )
			{
				var sourceFile = Path.GetFullPath( Path.Combine( sourceDir, reference.Replace( '\\', '/' ) ) );
				var relative = Path.GetRelativePath( sourceDir, sourceFile );
				if ( Path.IsPathRooted( relative ) || relative == ".."
					|| relative.StartsWith( ".." + Path.DirectorySeparatorChar ) || !File.Exists( sourceFile ) )
					continue;
				var destFile = Path.Combine( destDir, relative );
				Try( () =>
				{
					Directory.CreateDirectory( Path.GetDirectoryName( destFile ) );
					File.Copy( sourceFile, destFile, true );
					AssetSystem.RegisterFile( destFile );
					return true;
				} );
			}

			// Every copied file must be REGISTERED: assets copied onto disk mid-session are
			// unknown to the asset system, so the material chain cannot generate their vtex
			// resources - the renderer then logs "Texture manager doesn't know about
			// texture ...generated.vtex" MANY TIMES PER FRAME, which is both the
			// purple/black flicker and a preview running at ~2 fps (user report).
			foreach ( var file in Directory.GetFiles( sourceDir ) )
			{
				if ( !TextureExtensions.Contains( Path.GetExtension( file ).ToLowerInvariant() ) )
					continue;
				var destFile = Path.Combine( destDir, Path.GetFileName( file ) );
				Try( () => { File.Copy( file, destFile, true ); return true; } );
				Try( () => AssetSystem.RegisterFile( destFile ) );
			}

			foreach ( var candidate in new[]
			{
				Path.Combine( sourceDir, "textures" ),
				Path.Combine( Path.GetDirectoryName( sourceDir ) ?? sourceDir, "textures" ),
			} )
			{
				if ( !Directory.Exists( candidate ) )
					continue;
				var destTextures = Path.Combine( destDir, "textures" );
				Directory.CreateDirectory( destTextures );
				foreach ( var file in Directory.GetFiles( candidate, "*", SearchOption.AllDirectories ) )
				{
					var relative = Path.GetRelativePath( candidate, file );
					var destFile = Path.Combine( destTextures, relative );
					Try( () =>
					{
						Directory.CreateDirectory( Path.GetDirectoryName( destFile ) );
						File.Copy( file, destFile, true );
						return true;
					} );
					Try( () => AssetSystem.RegisterFile( destFile ) );
				}
				break; // first existing candidate wins
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[text-to-animation] sidecar texture copy failed: {e.Message}" );
		}
	}


	static T Try<T>( Func<T> getter )
	{
		try { return getter(); }
		catch { return default; }
	}
}
