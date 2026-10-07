#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TextToAnimation.EditorTools.Inference.Runtime;

/// <summary>Central directory of a zip file read through ranges (Zip64 aware). Only STORED entries are readable.</summary>
public sealed class RemoteZip
{
	public sealed record Entry( string Name, long LocalHeaderOffset, long CompressedSize, long Size, uint Crc32, int Method );

	public IRangeSource Source { get; }
	public IReadOnlyDictionary<string, Entry> Entries { get; }

	RemoteZip( IRangeSource source, Dictionary<string, Entry> entries ) { Source = source; Entries = entries; }

	public static async Task<RemoteZip> OpenAsync( IRangeSource source, CancellationToken token )
	{
		var tailLength = (int)Math.Min( source.Length, 65536 + 22 );
		var tail = await source.ReadAsync( source.Length - tailLength, tailLength, token );
		var eocd = -1;
		for ( var i = tail.Length - 22; i >= 0; i-- )
			if ( BitConverter.ToUInt32( tail, i ) == 0x06054b50 ) { eocd = i; break; }
		if ( eocd < 0 ) throw new InvalidDataException( "Not a zip file (no end of central directory)." );
		long cdOffset = BitConverter.ToUInt32( tail, eocd + 16 );
		long cdSize = BitConverter.ToUInt32( tail, eocd + 12 );
		long count = BitConverter.ToUInt16( tail, eocd + 10 );
		if ( cdOffset == 0xFFFFFFFF || count == 0xFFFF || cdSize == 0xFFFFFFFF )
		{
			// Zip64: locator sits 20 bytes before the EOCD
			var loc = eocd - 20;
			if ( loc < 0 || BitConverter.ToUInt32( tail, loc ) != 0x07064b50 ) throw new InvalidDataException( "Zip64 locator missing." );
			var z64Offset = (long)BitConverter.ToUInt64( tail, loc + 8 );
			var z64 = await source.ReadAsync( z64Offset, 56, token );
			if ( BitConverter.ToUInt32( z64, 0 ) != 0x06064b50 ) throw new InvalidDataException( "Zip64 end record missing." );
			count = (long)BitConverter.ToUInt64( z64, 32 );
			cdSize = (long)BitConverter.ToUInt64( z64, 40 );
			cdOffset = (long)BitConverter.ToUInt64( z64, 48 );
		}
		if ( cdSize > 64 * 1024 * 1024 ) throw new InvalidDataException( "Zip central directory is too large." );
		var cd = await source.ReadAsync( cdOffset, (int)cdSize, token );
		var entries = new Dictionary<string, Entry>( StringComparer.Ordinal );
		var p = 0;
		for ( long e = 0; e < count; e++ )
		{
			if ( BitConverter.ToUInt32( cd, p ) != 0x02014b50 ) throw new InvalidDataException( "Corrupt zip central directory." );
			var method = BitConverter.ToUInt16( cd, p + 10 );
			var crc = BitConverter.ToUInt32( cd, p + 16 );
			long csize = BitConverter.ToUInt32( cd, p + 20 );
			long usize = BitConverter.ToUInt32( cd, p + 24 );
			var nameLen = BitConverter.ToUInt16( cd, p + 28 );
			var extraLen = BitConverter.ToUInt16( cd, p + 30 );
			var commentLen = BitConverter.ToUInt16( cd, p + 32 );
			long offset = BitConverter.ToUInt32( cd, p + 42 );
			var name = Encoding.UTF8.GetString( cd, p + 46, nameLen );
			// Zip64 extra field overrides 0xFFFFFFFF values in order: size, compressed size, offset
			var x = p + 46 + nameLen; var xEnd = x + extraLen;
			while ( x + 4 <= xEnd )
			{
				var id = BitConverter.ToUInt16( cd, x ); var len = BitConverter.ToUInt16( cd, x + 2 );
				if ( id == 1 )
				{
					var q = x + 4;
					if ( usize == 0xFFFFFFFF ) { usize = (long)BitConverter.ToUInt64( cd, q ); q += 8; }
					if ( csize == 0xFFFFFFFF ) { csize = (long)BitConverter.ToUInt64( cd, q ); q += 8; }
					if ( offset == 0xFFFFFFFF ) { offset = (long)BitConverter.ToUInt64( cd, q ); }
				}
				x += 4 + len;
			}
			entries[name] = new Entry( name, offset, csize, usize, crc, method );
			p += 46 + nameLen + extraLen + commentLen;
		}
		return new RemoteZip( source, entries );
	}

	/// <summary>Absolute offset of an entry's data (after its local header).</summary>
	public async Task<long> DataOffsetAsync( Entry entry, CancellationToken token )
	{
		var header = await Source.ReadAsync( entry.LocalHeaderOffset, 30, token );
		if ( BitConverter.ToUInt32( header, 0 ) != 0x04034b50 ) throw new InvalidDataException( "Corrupt zip local header." );
		return entry.LocalHeaderOffset + 30 + BitConverter.ToUInt16( header, 26 ) + BitConverter.ToUInt16( header, 28 );
	}

	public async Task<byte[]> ReadEntryAsync( Entry entry, CancellationToken token )
	{
		if ( entry.Method != 0 ) throw new NotSupportedException( "Only uncompressed zip entries can be read." );
		var data = await Source.ReadAsync( await DataOffsetAsync( entry, token ), checked((int)entry.Size), token );
		if ( Crc32.Compute( data ) != entry.Crc32 ) throw new InvalidDataException( $"{entry.Name} failed its CRC check." );
		return data;
	}
}
