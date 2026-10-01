#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Editor.Inference.Runtime;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// Extracts the inference (EMA) weights from the official UniMate PyTorch checkpoint without downloading
/// the optimizer state: the zip directory and <c>data.pkl</c> are read with range requests, the pickle is
/// interpreted by a restricted reader (no code execution), and only the contiguous block of EMA storages
/// (~296 MB of the 1.19 GB file) is fetched. Every storage is CRC-32 checked against the zip directory.
/// </summary>
public static class UniMateCheckpoint
{
	public const string RepoCommit = "92710b30abc0a7708c9f280f38fd7e65448c4f95";
	public const string Url = "https://huggingface.co/Linzhan/UniMate/resolve/" + RepoCommit + "/unimate_uniml3d_f60_v2/checkpoints/checkpoint_step_100000.pt";
	public const long FileSize = 1_185_827_848;

	/// <summary>Bytes of the EMA block (known after reading the directory; this is the expected value).</summary>
	public const long ExpectedEmaBytes = 327_900_000;

	/// <param name="blockFile">Where the downloaded block is kept (resumed if present).</param>
	/// <param name="progress">(bytes done, bytes total) of the block download.</param>
	public static async Task<Dictionary<string, WeightTensor>> FetchAsync( IRangeSource source, string blockFile,
		Action<long, long>? progress, CancellationToken token )
	{
		var zip = await RemoteZip.OpenAsync( source, token );
		var pkl = zip.Entries.Values.SingleOrDefault( e => e.Name.EndsWith( "/data.pkl", StringComparison.Ordinal ) )
			?? throw new InvalidDataException( "The checkpoint has no data.pkl." );
		var prefix = pkl.Name.Substring( 0, pkl.Name.Length - "data.pkl".Length );
		var root = new TorchCheckpoint.DataReader( await zip.ReadEntryAsync( pkl, token ) ).Read() as Dictionary<object, object?>
			?? throw new InvalidDataException( "Unexpected checkpoint layout." );
		if ( !root.TryGetValue( "ema_state_dict", out var emaObj ) || emaObj is not Dictionary<object, object?> ema
			|| !ema.TryGetValue( "shadow_params", out var shadowObj ) || shadowObj is not List<object?> shadow )
			throw new InvalidDataException( "The checkpoint has no EMA weights." );
		var order = UniMateParamOrder.V2;
		if ( shadow.Count != order.Length ) throw new InvalidDataException( $"Expected {order.Length} EMA tensors, found {shadow.Count}." );

		var refs = new List<(string Name, TorchCheckpoint.TensorRef Ref, RemoteZip.Entry Entry)>();
		for ( var i = 0; i < shadow.Count; i++ )
		{
			if ( shadow[i] is not TorchCheckpoint.TensorRef t ) throw new InvalidDataException( $"EMA entry {i} is not a tensor." );
			if ( !t.Shape.SequenceEqual( order[i].Shape ) ) throw new InvalidDataException( $"EMA entry {i} ({order[i].Name}) has an unexpected shape." );
			if ( t.Storage.Dtype != "FloatStorage" ) throw new InvalidDataException( $"EMA entry {i} is not fp32." );
			if ( !zip.Entries.TryGetValue( prefix + "data/" + t.Storage.Key, out var entry ) ) throw new InvalidDataException( $"Missing storage for {order[i].Name}." );
			if ( entry.Method != 0 ) throw new InvalidDataException( "Compressed checkpoint storages aren't supported." );
			refs.Add( (order[i].Name, t, entry) );
		}

		// one contiguous range covering every EMA storage (local headers included)
		var start = refs.Min( r => r.Entry.LocalHeaderOffset );
		var end = refs.Max( r => r.Entry.LocalHeaderOffset + 30 + r.Entry.Name.Length + 1024 + r.Entry.CompressedSize );
		end = Math.Min( end, source.Length );
		var total = end - start;
		Directory.CreateDirectory( Path.GetDirectoryName( blockFile )! );
		var have = File.Exists( blockFile ) ? new FileInfo( blockFile ).Length : 0;
		if ( have > total ) { File.Delete( blockFile ); have = 0; }
		if ( have < total )
		{
			await using var fs = new FileStream( blockFile, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true );
			await source.CopyAsync( start + have, total - have, fs, done => progress?.Invoke( have + done, total ), token );
		}
		progress?.Invoke( total, total );

		// extract tensors from the block, verifying CRCs
		var result = new Dictionary<string, WeightTensor>( StringComparer.Ordinal );
		await using var block = new FileStream( blockFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true );
		var header = new byte[30];
		foreach ( var (name, t, entry) in refs )
		{
			token.ThrowIfCancellationRequested();
			block.Seek( entry.LocalHeaderOffset - start, SeekOrigin.Begin );
			await block.ReadExactlyAsync( header, token );
			if ( BitConverter.ToUInt32( header, 0 ) != 0x04034b50 ) throw new InvalidDataException( "The downloaded weights are corrupt (bad zip header)." );
			var dataOffset = entry.LocalHeaderOffset - start + 30 + BitConverter.ToUInt16( header, 26 ) + BitConverter.ToUInt16( header, 28 );
			var bytes = new byte[entry.Size];
			block.Seek( dataOffset, SeekOrigin.Begin );
			await block.ReadExactlyAsync( bytes, token );
			if ( Crc32.Compute( bytes ) != entry.Crc32 )
			{
				block.Dispose();
				File.Delete( blockFile );
				throw new InvalidDataException( $"The downloaded weights failed their checksum ({name}). They were deleted; try again." );
			}
			var count = t.Shape.Aggregate( 1, ( a, b ) => a * b );
			var data = new float[count];
			Buffer.BlockCopy( bytes, checked((int)(t.Offset * 4)), data, 0, count * 4 );
			result[name] = new WeightTensor( name, t.Shape, data );
		}
		return result;
	}
}
