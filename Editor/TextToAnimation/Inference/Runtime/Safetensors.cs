using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TextToAnimation.Editor.Inference.Runtime;

/// <summary>A named fp32 weight tensor.</summary>
public sealed record WeightTensor( string Name, int[] Shape, float[] Data )
{
	public int Length => Data.Length;
}

/// <summary>
/// Minimal safetensors reader/writer (8-byte little-endian header length, JSON header, raw little-endian
/// data). Supports F32, F16 and BF16 (converted to fp32).
/// </summary>
public static class Safetensors
{
	public static Dictionary<string, WeightTensor> Read( string path )
	{
		using var fs = File.OpenRead( path );
		var lenBytes = new byte[8];
		fs.ReadExactly( lenBytes );
		var headerLength = BitConverter.ToInt64( lenBytes, 0 );
		if ( headerLength <= 0 || headerLength > 100_000_000 ) throw new InvalidDataException( "Invalid safetensors header." );
		var header = new byte[headerLength];
		fs.ReadExactly( header );
		var dataStart = 8 + headerLength;
		using var doc = JsonDocument.Parse( header );
		var result = new Dictionary<string, WeightTensor>( StringComparer.Ordinal );
		foreach ( var entry in doc.RootElement.EnumerateObject() )
		{
			if ( entry.Name == "__metadata__" ) continue;
			var dtype = entry.Value.GetProperty( "dtype" ).GetString();
			var shape = entry.Value.GetProperty( "shape" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
			var offsets = entry.Value.GetProperty( "data_offsets" ).EnumerateArray().Select( e => e.GetInt64() ).ToArray();
			var count = shape.Aggregate( 1, ( a, b ) => a * b );
			var bytes = new byte[offsets[1] - offsets[0]];
			fs.Seek( dataStart + offsets[0], SeekOrigin.Begin );
			fs.ReadExactly( bytes );
			var data = new float[count];
			switch ( dtype )
			{
				case "F32": Buffer.BlockCopy( bytes, 0, data, 0, count * 4 ); break;
				case "F16": for ( var i = 0; i < count; i++ ) data[i] = (float)BitConverter.UInt16BitsToHalf( BitConverter.ToUInt16( bytes, i * 2 ) ); break;
				case "BF16": for ( var i = 0; i < count; i++ ) data[i] = BitConverter.Int32BitsToSingle( BitConverter.ToUInt16( bytes, i * 2 ) << 16 ); break;
				default: throw new NotSupportedException( $"safetensors dtype {dtype} is not supported." );
			}
			result[entry.Name] = new WeightTensor( entry.Name, shape, data );
		}
		return result;
	}

	/// <summary>Writes fp32 tensors (in the given order) to a safetensors file.</summary>
	public static void Write( string path, IEnumerable<WeightTensor> tensors )
	{
		var list = tensors.ToList();
		var header = new StringBuilder( "{" );
		long offset = 0;
		for ( var i = 0; i < list.Count; i++ )
		{
			var t = list[i];
			var bytes = (long)t.Length * 4;
			if ( i > 0 ) header.Append( ',' );
			header.Append( JsonSerializer.Serialize( t.Name ) ).Append( ":{\"dtype\":\"F32\",\"shape\":[" )
				.Append( string.Join( ",", t.Shape ) ).Append( "],\"data_offsets\":[" ).Append( offset ).Append( ',' ).Append( offset + bytes ).Append( "]}" );
			offset += bytes;
		}
		header.Append( '}' );
		var headerBytes = Encoding.UTF8.GetBytes( header.ToString() );
		var pad = (8 - headerBytes.Length % 8) % 8;
		var padded = new byte[headerBytes.Length + pad];
		Array.Copy( headerBytes, padded, headerBytes.Length );
		for ( var i = headerBytes.Length; i < padded.Length; i++ ) padded[i] = (byte)' ';
		var temp = path + ".tmp";
		using ( var fs = File.Create( temp ) )
		{
			fs.Write( BitConverter.GetBytes( (long)padded.Length ) );
			fs.Write( padded );
			foreach ( var t in list )
			{
				var raw = new byte[t.Length * 4];
				Buffer.BlockCopy( t.Data, 0, raw, 0, raw.Length );
				fs.Write( raw );
			}
		}
		File.Move( temp, path, overwrite: true );
	}
}
