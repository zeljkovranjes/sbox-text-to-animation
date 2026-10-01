using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Editor.Formats.Fbx;

namespace TextToAnimation.Editor.Engine;

public static partial class FbxMaterials
{
	/// <summary>
	/// Writes the images an FBX carries inside itself ("embed textures" exports, most Sketchfab and Mixamo
	/// downloads) into <paramref name="destDir"/>/textures under their authored file names, so the material
	/// detection finds them like any sidecar texture. Existing files are left alone. Returns the files written.
	/// </summary>
	public static List<string> ExtractEmbeddedTextures( byte[] fbx, string destDir )
	{
		var written = new List<string>();
		try
		{
			var root = FbxTokenizer.Parse( fbx );
			var objects = root.Child( "Objects" );
			if ( objects is null ) return written;
			foreach ( var video in objects.ChildrenNamed( "Video" ) )
			{
				if ( video.Child( "Content" )?.Properties.FirstOrDefault() is not byte[] bytes || bytes.Length < 16 ) continue;
				var file = video.Child( "RelativeFilename" )?.Properties.FirstOrDefault() as string
					?? video.Child( "Filename" )?.Properties.FirstOrDefault() as string
					?? video.Child( "FileName" )?.Properties.FirstOrDefault() as string;
				// keep the authored name (the material links match it by file name or stem); exporters of packed
				// images often write a bare name without an image extension, so add the one the bytes carry
				var name = Path.GetFileName( (file ?? "").Replace( '\\', '/' ) );
				if ( string.IsNullOrWhiteSpace( name ) && video.Properties.Count > 1 && video.Properties[1] is string raw )
					name = FbxNode.SplitName( raw ).Name;
				if ( string.IsNullOrWhiteSpace( name ) ) name = $"embedded_{written.Count}";
				if ( !TextureExtensions.Contains( Path.GetExtension( name ).ToLowerInvariant() ) )
					name = Path.GetFileNameWithoutExtension( name ) + ImageExtension( bytes );
				name = string.Concat( name.Select( c => Path.GetInvalidFileNameChars().Contains( c ) ? '_' : c ) );
				var dest = Path.Combine( destDir, "textures", name );
				if ( File.Exists( dest ) ) continue;
				Directory.CreateDirectory( Path.GetDirectoryName( dest )! );
				File.WriteAllBytes( dest, bytes );
				Try( () => { AssetSystem.RegisterFile( dest ); return true; } );
				written.Add( dest );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[text-to-animation] embedded texture extraction failed: {e.Message}" );
		}
		return written;
	}

	static string ImageExtension( byte[] b )
	{
		if ( b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ) return ".png";
		if ( b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ) return ".jpg";
		return ".tga";
	}
}
