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
