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
