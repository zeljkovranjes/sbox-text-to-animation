using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>
/// One external-data file (ONNX "location") holding every large weight of the UniMate graphs, already in the
/// layout the graphs use (Linear weights transposed). Graphs for any skeleton reference it by offset, so
/// building a graph for a new rig only writes a small .onnx file. An index (name -> offset, length, shape)
/// sits next to it as JSON.
/// </summary>
public sealed class WeightBlob : IDisposable
{
	public sealed record Entry( long Offset, long Length, int[] Shape );

	readonly Dictionary<string, Entry> _entries;
	readonly FileStream _writer;
	public string Path { get; }
	public string FileName => System.IO.Path.GetFileName( Path );
	public bool Writable => _writer is not null;

	WeightBlob( string path, Dictionary<string, Entry> entries, FileStream writer )
	{
		Path = path;
		_entries = entries;
		_writer = writer;
	}

	static string IndexPath( string path ) => path + ".json";

	/// <summary>Opens an existing blob read-only.</summary>
	public static WeightBlob Open( string path )
	{
		var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>( File.ReadAllText( IndexPath( path ) ) )
			?? throw new InvalidDataException( "Empty weight index." );
		return new WeightBlob( path, entries, null );
	}

	public static bool Exists( string path ) => File.Exists( path ) && File.Exists( IndexPath( path ) );

	/// <summary>Starts a new blob (written by graph building, completed with <see cref="Commit"/>).</summary>
	public static WeightBlob Create( string path )
	{
		Directory.CreateDirectory( System.IO.Path.GetDirectoryName( path )! );
		return new WeightBlob( path, new Dictionary<string, Entry>( StringComparer.Ordinal ), new FileStream( path + ".part", FileMode.Create, FileAccess.Write ) );
	}

	public bool TryGet( string name, out Entry entry ) => _entries.TryGetValue( name, out entry );

	/// <summary>Returns the entry for <paramref name="name"/>, appending the data when the blob is being written.</summary>
	public Entry GetOrAdd( string name, int[] shape, float[] data )
	{
		if ( _entries.TryGetValue( name, out var e ) ) return e;
		if ( _writer is null ) throw new InvalidOperationException( $"Weight '{name}' is not in {FileName}; rebuild the model files." );
		var pad = (int)((4096 - _writer.Position % 4096) % 4096);
		if ( pad > 0 ) _writer.Write( new byte[pad] );
		var bytes = MemoryMarshal.AsBytes( data.AsSpan() );
		e = new Entry( _writer.Position, bytes.Length, shape );
		_writer.Write( bytes );
		_entries[name] = e;
		return e;
	}

	/// <summary>Finishes a blob being written: flushes, writes the index, moves it into place.</summary>
	public void Commit()
	{
		if ( _writer is null ) return;
		_writer.Flush();
		_writer.Dispose();
		File.WriteAllText( IndexPath( Path ), JsonSerializer.Serialize( _entries ) );
		File.Move( Path + ".part", Path, overwrite: true );
	}

	public void Dispose()
	{
		if ( _writer is not null && _writer.CanWrite ) _writer.Dispose();
	}
}
