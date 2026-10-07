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
