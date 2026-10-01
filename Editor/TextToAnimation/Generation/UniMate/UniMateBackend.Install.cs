using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Editor.Inference.Runtime;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.UniMate;

public sealed partial class UniMateBackend
{
	/// <summary>A pinned download: URL at a fixed repository commit, size and SHA-256.</summary>
	sealed record RemoteFile( string Name, string Url, long Size, string Sha256, string LocalName );

	static readonly RemoteFile T5Encoder = new( "text encoder",
		"https://huggingface.co/Xenova/flan-t5-base/resolve/c6a1b98a4a35e1063d88edaceef4dc52cf3b2784/onnx/encoder_model.onnx",
		438_697_388, "94c438df6ed8f9479f3b8ce43d4903cb4817d7c1e0d5c47b86a7a2ca9de621c9", "t5_encoder.onnx" );
	static readonly RemoteFile T5Tokenizer = new( "tokenizer",
		"https://huggingface.co/google/flan-t5-base/resolve/7bcac572ce56db69c1ea7c8af255c5d7c9672fc2/tokenizer.json",
		2_424_064, "fe2ebbbbde2985be723e0ce18217853e4020c5e9d35bd07be2c27ab9d3ead57a", "t5_tokenizer.json" );

	const string StampName = "installed.json";
	const string RootVariable = "TEXT_TO_ANIMATION_MODEL_ROOT";

	/// <summary>Where the model lives: %LOCALAPPDATA%\TextToAnimation\Models\unimate (shared by every project).</summary>
	public static string ModelDirectory
	{
		get
		{
			var root = Environment.GetEnvironmentVariable( RootVariable );
			if ( string.IsNullOrWhiteSpace( root ) )
			{
				var local = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
				if ( string.IsNullOrEmpty( local ) ) local = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local", "share" );
				root = Path.Combine( local, "TextToAnimation", "Models" );
			}
			return Path.Combine( root, "unimate" );
		}
	}

	UniMateFiles Files => new( ModelDirectory );

	/// <summary>Download size: text encoder + tokenizer + the EMA block of the checkpoint.</summary>
	public long DownloadBytes => T5Encoder.Size + T5Tokenizer.Size + UniMateCheckpoint.ExpectedEmaBytes;

	public ModelState Inspect()
	{
		var files = Files;
		var stamp = Path.Combine( files.Directory, StampName );
		if ( File.Exists( stamp ) && WeightBlob.Exists( files.WeightBlob ) && File.Exists( files.T5Encoder ) && File.Exists( files.T5Tokenizer ) )
			return ModelState.Ready;
		return Directory.Exists( files.Directory ) && Directory.EnumerateFileSystemEntries( files.Directory ).Any()
			? ModelState.Incomplete
			: ModelState.NotInstalled;
	}

	public async Task InstallAsync( IProgress<string> progress, CancellationToken token )
	{
		var files = Files;
		Directory.CreateDirectory( files.Directory );
		using var installLock = AcquireLock( files.Directory );
		var total = DownloadBytes;
		long before = 0;
		void Report( string name, long done )
		{
			var overall = Math.Clamp( before + done, 0, total );
			var percent = (int)(overall * 100 / Math.Max( 1, total ));
			var left = Math.Max( 0, total - overall );
			progress?.Report( $"Downloading {name} · {percent}% · {(left >= 1_000_000_000 ? $"{left / 1e9:0.0} GB" : $"{(left + 999_999) / 1_000_000} MB")} left" );
		}

		foreach ( var file in new[] { T5Tokenizer, T5Encoder } )
		{
			var target = Path.Combine( files.Directory, file.LocalName );
			if ( !File.Exists( target ) ) await DownloadFileAsync( file, target, done => Report( file.Name, done ), token );
			before += file.Size;
		}

		if ( !WeightBlob.Exists( files.WeightBlob ) )
		{
			var block = Path.Combine( files.Directory, "checkpoint_ema.part" );
			var source = await HttpRangeSource.OpenAsync( UniMateCheckpoint.Url, UniMateCheckpoint.FileSize, token );
			var weights = await UniMateCheckpoint.FetchAsync( source, block, ( done, _ ) => Report( "UniMate weights", done ), token );
			progress?.Report( "Preparing the model for your computer…" );
			await Task.Run( () => UniMateModel.WriteWeights( weights, files.WeightBlob ), token );
			TryDelete( block );
		}

		File.WriteAllText( Path.Combine( files.Directory, StampName ), JsonSerializer.Serialize( new
		{
			Model = "UniMate uniml3d_f60_v2",
			Checkpoint = UniMateCheckpoint.RepoCommit,
			Format = UniMateGraphBuilder.FormatVersion,
			InstalledUtc = DateTime.UtcNow,
		} ) );
		progress?.Report( "UniMate is ready." );
	}

	/// <summary>Resumable, verified download of one file (".part" next to the target, SHA-256 checked).</summary>
	static async Task DownloadFileAsync( RemoteFile file, string target, Action<long> progress, CancellationToken token )
	{
		var part = target + ".part";
		var have = File.Exists( part ) ? new FileInfo( part ).Length : 0;
		if ( have > file.Size ) { File.Delete( part ); have = 0; }
		if ( have < file.Size )
		{
			var source = await HttpRangeSource.OpenAsync( file.Url, file.Size, token );
			await using var fs = new FileStream( part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true );
			await source.CopyAsync( have, file.Size - have, fs, done => progress( have + done ), token );
		}
		progress( file.Size );
		string hash;
		await using ( var check = File.OpenRead( part ) )
			hash = Convert.ToHexString( await SHA256.HashDataAsync( check, token ) ).ToLowerInvariant();
		if ( hash != file.Sha256 )
		{
			File.Delete( part );
			throw new InvalidDataException( $"The {file.Name} download was corrupted (checksum mismatch). It was deleted - try again." );
		}
		File.Move( part, target, overwrite: true );
	}

	/// <summary>Prevents two editors installing into the same folder at once.</summary>
	static IDisposable AcquireLock( string directory )
	{
		var path = Path.Combine( directory, "install.lock" );
		try { return new FileStream( path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose ); }
		catch ( IOException ) { throw new IOException( "Another s&box editor is installing UniMate right now. Wait for it to finish." ); }
	}

	static void TryDelete( string path )
	{
		try { if ( File.Exists( path ) ) File.Delete( path ); } catch { }
	}

	public async Task<IMotionGenerator> LoadAsync( IProgress<string> progress, CancellationToken token )
	{
		if ( Inspect() != ModelState.Ready ) throw new InvalidOperationException( "UniMate isn't downloaded yet." );
		progress?.Report( "Loading UniMate…" );
		var model = await Task.Run( () => UniMateModel.Load( Files, token ), token );
		return new UniMateGenerator( model );
	}

	public void Remove()
	{
		var dir = ModelDirectory;
		if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true );
	}
}
