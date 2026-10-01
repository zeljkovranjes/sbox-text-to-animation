using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using TextToAnimation.Vmdl;

namespace TextToAnimation.Editor.Engine;

/// <summary>Outcome of a save: whether the model compiled and the clips play back as authored.</summary>
public sealed class SaveResult
{
	public bool Success { get; set; }
	public List<string> Errors { get; } = new();
	public List<string> Notes { get; } = new();
	public string VmdlPath { get; set; }
	public string BackupPath { get; set; }
	/// <summary>Largest difference (inches) between the compiled sequence and the clip, per sequence.</summary>
	public Dictionary<string, float> PlaybackError { get; } = new( StringComparer.Ordinal );
	public bool RolledBack { get; set; }
	/// <summary>Largest root rotation difference (degrees) between the compiled sequences and the clips.</summary>
	public float RootRotationError { get; set; }
	/// <summary>The root compensation this model needs, when the save measured a new one (store it with the workspace).</summary>
	public System.Numerics.Quaternion? LearnedRootCompensation { get; set; }
}

/// <summary>
/// Saves workspace clips into a model's vmdl: DMX files next to the model, AnimFile entries added or replaced
/// in place, a timestamped backup of the original vmdl, a verified compile, and a playback check that samples
/// every compiled sequence and compares it with the clip. If the compile fails or playback doesn't match, the
/// original vmdl is restored (originals are never lost).
/// </summary>
public static class VmdlSaveService
{
	/// <summary>Max acceptable joint difference (inches, root-relative) between the compiled clip and the editor clip.</summary>
	public const float PlaybackTolerance = 0.75f;

	/// <summary>Max acceptable root rotation difference (degrees) between the compiled clip and the editor clip.</summary>
	public const float RootRotationTolerance = 3f;

	public static async Task<SaveResult> SaveAsync( Asset modelAsset, MotionRig rig, IReadOnlyList<ClipSaveRequest> requests,
		Action<string> status, CancellationToken token, System.Numerics.Quaternion? rootCompensation = null )
	{
		var result = new SaveResult();
		await EngineThread.SwitchToMainThread();
		var vmdl = ModelBridge.SourcePathOf( modelAsset );
		if ( vmdl is null )
		{
			result.Errors.Add( "This model has no editable .vmdl source. Use \"Save as new animation model\" instead." );
			return result;
		}
		if ( EnginePaths.IsUnderEngineInstall( vmdl ) )
		{
			result.Errors.Add( "This model belongs to the s&box installation and can't be changed. Use \"Save as new animation model\" - it creates a model in your project that inherits this one." );
			return result;
		}
		result.VmdlPath = vmdl;
		var assetsRoot = AssetsRootOf( vmdl, modelAsset.Path );
		if ( assetsRoot is null )
		{
			result.Errors.Add( $"Couldn't work out the Assets folder of {modelAsset.Path}." );
			return result;
		}

		var original = File.ReadAllText( vmdl );
		result.BackupPath = Backup( vmdl, original );
		var compensation = rootCompensation ?? ClipVmdlWriter.DefaultRootCompensation;
		var attempt = await WriteCompileVerifyAsync( result, modelAsset, rig, requests, vmdl, assetsRoot, original, compensation, status, token );
		if ( attempt is { } offset )
		{
			// the compiled root turned by a constant rotation: this model's source wants a different root compensation
			// (it depends on how the file was authored). Rewrite once with the measured one.
			var corrected = MathQ.Normalize( compensation * System.Numerics.Quaternion.Conjugate( offset ) );
			status?.Invoke( "Adjusting to this model's orientation and compiling again…" );
			result.Errors.Clear();
			result.PlaybackError.Clear();
			attempt = await WriteCompileVerifyAsync( result, modelAsset, rig, requests, vmdl, assetsRoot, original, corrected, status, token );
			if ( result.Success ) result.LearnedRootCompensation = corrected;
			else if ( attempt is not null )
				result.Errors.Add( $"The saved animation plays turned by {result.RootRotationError:0} degrees on this model." );
		}
		if ( !result.Success && !result.RolledBack ) await RollBackAsync( result, vmdl, original, status );
		return result;
	}

	/// <summary>
	/// Writes the DMX files and the vmdl with <paramref name="compensation"/>, compiles, and checks every sequence
	/// plays back like its clip. Returns the root rotation offset when the only problem is a constant root turn
	/// (so the caller can retry with a corrected compensation); null otherwise (see <paramref name="result"/>).
	/// </summary>
	static async Task<System.Numerics.Quaternion?> WriteCompileVerifyAsync( SaveResult result, Asset modelAsset, MotionRig rig,
		IReadOnlyList<ClipSaveRequest> requests, string vmdl, string assetsRoot, string original, System.Numerics.Quaternion compensation,
		Action<string> status, CancellationToken token )
	{
		status?.Invoke( "Preparing animation files…" );
		VmdlSavePlan plan;
		try { plan = ClipVmdlWriter.Plan( original, modelAsset.Path, rig, requests, compensation ); }
		catch ( Exception e ) when ( e is InvalidOperationException or FormatException or VmdlAugmentException or ArgumentException )
		{
			result.Errors.Add( e is VmdlAugmentException augment ? $"Name collision: {string.Join( ", ", augment.Collisions )}" : e.Message );
			return null;
		}
		foreach ( var note in plan.Notes ) if ( !result.Notes.Contains( note ) ) result.Notes.Add( note );
		token.ThrowIfCancellationRequested();

		// ---- DMX files, then the vmdl LAST (the backup was taken by the caller)
		var written = new List<string>();
		try
		{
			foreach ( var file in plan.Files )
			{
				var abs = Path.Combine( assetsRoot, file.AssetPath.Replace( '/', Path.DirectorySeparatorChar ) );
				Directory.CreateDirectory( Path.GetDirectoryName( abs )! );
				File.WriteAllText( abs, file.Content );
				written.Add( abs );
			}
			File.WriteAllText( vmdl, plan.VmdlText );
		}
		catch ( Exception e )
		{
			result.Errors.Add( $"Writing files failed: {e.Message}" );
			TryRestore( vmdl, original );
			return null;
		}

		status?.Invoke( $"Compiling {Path.GetFileName( vmdl )}…" );
		var compile = await VmdlCompiler.RegisterAndCompileAsync( vmdl, written );
		if ( !compile.Compiled )
		{
			result.Errors.Add( compile.Error );
			return null;
		}

		status?.Invoke( "Checking the saved animations play back correctly…" );
		var model = await ReloadWithSequencesAsync( modelAsset.Path, plan.Sequences );
		if ( model is null )
		{
			result.Errors.Add( "The model compiled but its new sequences didn't appear." );
			return null;
		}
		var offsets = new List<System.Numerics.Quaternion>();
		foreach ( var (sequence, expected) in plan.Expected )
		{
			token.ThrowIfCancellationRequested();
			var fps = requests.FirstOrDefault( r => r.SequenceName == sequence )?.Clip.Fps ?? 30f;
			var (frames, _) = await ModelBridge.SampleSequenceAsync( model, sequence, rig.Skeleton, fps, token );
			result.PlaybackError[sequence] = Compare( rig, expected, frames );
			if ( RootOffset( rig, expected, frames ) is { } o ) offsets.Add( o );
		}
		var rootError = offsets.Count == 0 ? 0f : offsets.Max( o => MathQ.AngleBetween( o, System.Numerics.Quaternion.Identity ) ) * 180f / MathF.PI;
		result.RootRotationError = rootError;
		var bad = result.PlaybackError.Where( kv => kv.Value > PlaybackTolerance ).ToList();
		if ( bad.Count == 0 && rootError <= RootRotationTolerance )
		{
			result.Success = true;
			return null;
		}
		foreach ( var (seq, error) in bad )
			result.Errors.Add( $"\"{seq}\" plays back {error:0.0} in away from the edited clip after compiling." );
		// a constant turn of the whole root (same for every sequence) is a compensation problem we can fix
		if ( bad.Count == 0 && offsets.Count > 0 && offsets.All( o => MathQ.AngleBetween( o, offsets[0] ) * 180f / MathF.PI < 2f ) )
			return offsets[0];
		if ( rootError > RootRotationTolerance )
			result.Errors.Add( $"The saved animation plays turned by {rootError:0} degrees." );
		return null;
	}

	/// <summary>
	/// The constant rotation between the compiled root and the clip's root (model space), averaged over sampled
	/// frames; null when it isn't constant (then it isn't a compensation problem).
	/// </summary>
	public static System.Numerics.Quaternion? RootOffset( MotionRig rig, IReadOnlyList<XForm[]> expected, IReadOnlyList<XForm[]> actual )
	{
		var count = Math.Min( expected.Count, actual.Count );
		if ( count == 0 ) return null;
		var root = rig.RootIndex;
		var we = new XForm[rig.Skeleton.Count];
		var wa = new XForm[rig.Skeleton.Count];
		var samples = new List<System.Numerics.Quaternion>();
		var step = Math.Max( 1, count / 12 );
		for ( var f = 0; f < count; f += step )
		{
			FkUtil.ToWorld( expected[f], rig.Skeleton, we );
			FkUtil.ToWorld( actual[(int)MathF.Round( f * (actual.Count - 1f) / Math.Max( 1, expected.Count - 1 ) )], rig.Skeleton, wa );
			var d = MathQ.Normalize( wa[root].Rot * System.Numerics.Quaternion.Conjugate( we[root].Rot ) );
			if ( samples.Count > 0 && System.Numerics.Quaternion.Dot( d, samples[0] ) < 0 ) d = -d;
			samples.Add( d );
		}
		var mean = MathQ.Normalize( samples.Aggregate( new System.Numerics.Quaternion( 0, 0, 0, 0 ), ( a, q ) => a + q ) );
		return samples.All( q => MathQ.AngleBetween( q, mean ) * 180f / MathF.PI < 2f ) ? mean : null;
	}

	/// <summary>
	/// Writes the clips as DMX files (engine units, Z-up) into <paramref name="folder"/>, plus - when the folder is
	/// inside an Assets folder - a small vmdl that inherits the model and lists them, ready to use.
	/// </summary>
	public static List<string> Export( string folder, string modelAssetPath, MotionRig rig, IReadOnlyList<(string Sequence, AnimClip Clip, List<XForm[]> Frames)> clips,
		System.Numerics.Quaternion? rootCompensation = null )
	{
		Directory.CreateDirectory( folder );
		var files = new List<string>();
		var entries = new List<AnimEntry>();
		foreach ( var (sequence, clip, frames) in clips )
		{
			var path = Path.Combine( folder, sequence + ".dmx" );
			File.WriteAllText( path, ClipVmdlWriter.BuildDmx( rig, frames, clip.Fps, clip.Looping, sequence, 1f, rootCompensation ) );
			files.Add( path );
			entries.Add( new AnimEntry
			{
				Name = sequence, SourceFilename = sequence + ".dmx", Looping = clip.Looping,
				ExtractMotion = clip.Export.RootMotion == ClipRootMotion.Extract,
				Events = clip.Events.Select( e => new AnimEventEntry { EventClass = e.EventClass, Frame = e.Frame, Attachment = e.Attachment, Foot = e.Foot, Volume = e.Volume } ).ToList(),
			} );
		}
		var relative = AssetsRelative( folder );
		if ( relative is not null )
		{
			foreach ( var e in entries ) e.SourceFilename = (relative.Length > 0 ? relative + "/" : "") + e.SourceFilename;
			var vmdlPath = Path.Combine( folder, Path.GetFileNameWithoutExtension( modelAssetPath ) + "_animations.vmdl" );
			File.WriteAllText( vmdlPath, VmdlWriter.GenerateStandalone( modelAssetPath, entries, 1f, rig.Skeleton[rig.RootIndex].Name ) );
			files.Add( vmdlPath );
		}
		return files;
	}

	/// <summary>Assets-relative folder (forward slashes) when <paramref name="folder"/> is inside the project's Assets.</summary>
	public static string AssetsRelative( string folder )
	{
		var assets = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assets ) ) return null;
		var full = Path.GetFullPath( folder ).TrimEnd( Path.DirectorySeparatorChar );
		var root = Path.GetFullPath( assets ).TrimEnd( Path.DirectorySeparatorChar );
		if ( string.Equals( full, root, StringComparison.OrdinalIgnoreCase ) ) return "";
		return full.StartsWith( root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
			? full.Substring( root.Length + 1 ).Replace( '\\', '/' )
			: null;
	}

	/// <summary>Root-relative joint error: positions relative to the hips, so extracted root motion doesn't count.</summary>
	public static float Compare( MotionRig rig, IReadOnlyList<XForm[]> expected, IReadOnlyList<XForm[]> actual )
	{
		var count = Math.Min( expected.Count, actual.Count );
		if ( count == 0 ) return float.PositiveInfinity;
		var anchor = rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex;
		var we = new XForm[rig.Skeleton.Count];
		var wa = new XForm[rig.Skeleton.Count];
		var worst = 0f;
		var step = Math.Max( 1, count / 24 ); // ~24 sampled frames are plenty
		for ( var f = 0; f < count; f += step )
		{
			FkUtil.ToWorld( expected[f], rig.Skeleton, we );
			FkUtil.ToWorld( actual[(int)MathF.Round( f * (actual.Count - 1f) / Math.Max( 1, expected.Count - 1 ) )], rig.Skeleton, wa );
			for ( var b = 0; b < rig.Skeleton.Count; b++ )
			{
				if ( !rig.IsMotionBone( b ) ) continue;
				var de = System.Numerics.Vector3.Transform( we[b].Pos - we[anchor].Pos, System.Numerics.Quaternion.Conjugate( we[anchor].Rot ) );
				var da = System.Numerics.Vector3.Transform( wa[b].Pos - wa[anchor].Pos, System.Numerics.Quaternion.Conjugate( wa[anchor].Rot ) );
				worst = MathF.Max( worst, (de - da).Length() );
			}
		}
		return worst;
	}

	static async Task<Model> ReloadWithSequencesAsync( string modelPath, IReadOnlyList<string> sequences )
	{
		await EngineThread.SwitchToMainThread();
		for ( var i = 0; i < 40; i++ )
		{
			var model = Model.Load( modelPath );
			if ( model is not null && !model.IsError && sequences.All( model.AnimationNames.Contains ) ) return model;
			await EngineThread.DelayOnMain( 250 );
		}
		return null;
	}

	static async Task RollBackAsync( SaveResult result, string vmdl, string original, Action<string> status )
	{
		status?.Invoke( "Restoring the original model…" );
		if ( TryRestore( vmdl, original ) )
		{
			result.RolledBack = true;
			var compile = await VmdlCompiler.RegisterAndCompileAsync( vmdl, Array.Empty<string>() );
			result.Notes.Add( compile.Compiled ? "The original model was restored." : "The original vmdl text was restored; compile it again from the Asset Browser." );
		}
		else
		{
			result.Errors.Add( $"Restoring the original failed - a backup is at {result.BackupPath}." );
		}
	}

	static bool TryRestore( string vmdl, string original )
	{
		try { File.WriteAllText( vmdl, original ); return true; }
		catch { return false; }
	}

	static string Backup( string vmdl, string original )
	{
		try
		{
			var project = Project.Current?.GetRootPath() ?? Path.GetDirectoryName( vmdl )!;
			var dir = Path.Combine( project, "text_to_animation", "backups" );
			Directory.CreateDirectory( dir );
			var path = Path.Combine( dir, $"{Path.GetFileNameWithoutExtension( vmdl )}_{DateTime.Now:yyyyMMdd_HHmmss}.vmdl" );
			File.WriteAllText( path, original );
			return path;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[text-to-animation] could not back up {vmdl}: {e.Message}" );
			return null;
		}
	}

	/// <summary>The Assets folder a vmdl lives in, from its absolute path and assets-relative asset path.</summary>
	static string AssetsRootOf( string absolute, string assetPath )
	{
		var abs = absolute.Replace( '\\', '/' );
		var rel = assetPath.Replace( '\\', '/' ).TrimStart( '/' );
		return abs.EndsWith( rel, StringComparison.OrdinalIgnoreCase )
			? abs.Substring( 0, abs.Length - rel.Length ).TrimEnd( '/' ).Replace( '/', Path.DirectorySeparatorChar )
			: null;
	}
}
