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

namespace TextToAnimation.Editor.Inference.Runtime;

/// <summary>Random access to a file's bytes (a local file, or HTTP range requests).</summary>
public interface IRangeSource
{
	long Length { get; }
	Task<byte[]> ReadAsync( long offset, int count, CancellationToken token );
	/// <summary>Copies [offset, offset+count) into a stream, reporting bytes copied.</summary>
	Task CopyAsync( long offset, long count, Stream destination, Action<long>? progress, CancellationToken token );
}

public sealed class FileRangeSource : IRangeSource
{
	readonly string _path;
	public FileRangeSource( string path ) { _path = path; Length = new FileInfo( path ).Length; }
	public long Length { get; }

	public async Task<byte[]> ReadAsync( long offset, int count, CancellationToken token )
	{
		await using var fs = new FileStream( _path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true );
		fs.Seek( offset, SeekOrigin.Begin );
		var buffer = new byte[count];
		await fs.ReadExactlyAsync( buffer, token );
		return buffer;
	}

	public async Task CopyAsync( long offset, long count, Stream destination, Action<long>? progress, CancellationToken token )
	{
		await using var fs = new FileStream( _path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true );
		fs.Seek( offset, SeekOrigin.Begin );
		var buffer = new byte[1 << 20];
		long done = 0;
		while ( done < count )
		{
			var n = await fs.ReadAsync( buffer.AsMemory( 0, (int)Math.Min( buffer.Length, count - done ) ), token );
			if ( n <= 0 ) throw new EndOfStreamException();
			await destination.WriteAsync( buffer.AsMemory( 0, n ), token );
			done += n;
			progress?.Invoke( done );
		}
	}
}

/// <summary>HTTP range reads with a stall timeout and retries (redirects to the CDN are followed).</summary>
public sealed class HttpRangeSource : IRangeSource
{
	static readonly HttpClient Http = CreateClient();
	readonly string _url;
	static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds( 45 );

	static HttpClient CreateClient()
	{
		var client = new HttpClient( new HttpClientHandler { AllowAutoRedirect = true } ) { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd( "sbox-text-to-animation/1.0" );
		return client;
	}

	HttpRangeSource( string url, long length ) { _url = url; Length = length; }
	public long Length { get; }

	public static async Task<HttpRangeSource> OpenAsync( string url, long expectedLength, CancellationToken token )
	{
		// a 1-byte range request both validates the URL and reports the total size
		using var request = new HttpRequestMessage( HttpMethod.Get, url );
		request.Headers.Range = new RangeHeaderValue( 0, 0 );
		using var response = await Http.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, token );
		if ( response.StatusCode != System.Net.HttpStatusCode.PartialContent )
			throw new IOException( $"The download server didn't accept range requests ({(int)response.StatusCode})." );
		var total = response.Content.Headers.ContentRange?.Length ?? expectedLength;
		if ( expectedLength > 0 && total != expectedLength ) throw new IOException( $"Unexpected file size {total} (expected {expectedLength})." );
		return new HttpRangeSource( url, total );
	}

	public async Task<byte[]> ReadAsync( long offset, int count, CancellationToken token )
	{
		using var ms = new MemoryStream( count );
		await CopyAsync( offset, count, ms, null, token );
		return ms.ToArray();
	}

	public async Task CopyAsync( long offset, long count, Stream destination, Action<long>? progress, CancellationToken token )
	{
		long done = 0;
		for ( var attempt = 1; ; attempt++ )
		{
			try
			{
				using var request = new HttpRequestMessage( HttpMethod.Get, _url );
				request.Headers.Range = new RangeHeaderValue( offset + done, offset + count - 1 );
				using var stall = CancellationTokenSource.CreateLinkedTokenSource( token );
				stall.CancelAfter( StallTimeout );
				using var response = await Http.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, stall.Token );
				if ( response.StatusCode != System.Net.HttpStatusCode.PartialContent )
					throw new IOException( $"Download failed: HTTP {(int)response.StatusCode}." );
				await using var body = await response.Content.ReadAsStreamAsync( stall.Token );
				var buffer = new byte[1 << 16];
				while ( done < count )
				{
					stall.CancelAfter( StallTimeout );
					var n = await body.ReadAsync( buffer.AsMemory( 0, (int)Math.Min( buffer.Length, count - done ) ), stall.Token );
					if ( n <= 0 ) break;
					await destination.WriteAsync( buffer.AsMemory( 0, n ), token );
					done += n;
					progress?.Invoke( done );
				}
				if ( done >= count ) return;
				throw new IOException( "The connection closed early." );
			}
			catch ( Exception e ) when ( e is IOException or HttpRequestException or OperationCanceledException && !token.IsCancellationRequested )
			{
				if ( attempt >= 4 ) throw new IOException( $"Download failed after {attempt} attempts: {e.Message}", e );
				await Task.Delay( TimeSpan.FromSeconds( 2 * attempt ), token );
			}
		}
	}
}

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

/// <summary>CRC-32 (IEEE), incremental.</summary>
public static class Crc32
{
	static readonly uint[] Table = Enumerable.Range( 0, 256 ).Select( n =>
	{
		var c = (uint)n;
		for ( var k = 0; k < 8; k++ ) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
		return c;
	} ).ToArray();

	public static uint Compute( ReadOnlySpan<byte> data ) => Finish( Update( Start, data ) );
	public const uint Start = 0xFFFFFFFFu;
	public static uint Update( uint crc, ReadOnlySpan<byte> data )
	{
		foreach ( var b in data ) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
		return crc;
	}
	public static uint Finish( uint crc ) => crc ^ 0xFFFFFFFFu;
}
