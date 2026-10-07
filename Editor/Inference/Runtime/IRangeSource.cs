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

/// <summary>Random access to a file's bytes (a local file, or HTTP range requests).</summary>
public interface IRangeSource
{
	long Length { get; }
	Task<byte[]> ReadAsync( long offset, int count, CancellationToken token );
	/// <summary>Copies [offset, offset+count) into a stream, reporting bytes copied.</summary>
	Task CopyAsync( long offset, long count, Stream destination, Action<long>? progress, CancellationToken token );
}
