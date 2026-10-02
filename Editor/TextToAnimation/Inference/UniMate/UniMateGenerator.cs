using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Generation;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using TextToAnimation.Workspace;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// UniMate behind the editor's <see cref="IMotionGenerator"/> interface. All modes run on 2-second windows
/// (UniMate's native 60 frames at 30 fps); longer clips are covered by overlapping windows, each one encoded
/// from the current result so the overlap is held fixed and the root path continues smoothly.
/// </summary>
public sealed class UniMateGenerator : IMotionGenerator
{
	const int Window = UniMateModel.Frames;
	const int Overlap = 10;
	readonly UniMateModel _model;
	readonly Dictionary<string, (UniMateRig Rig, PreparedSkeleton Prep)> _rigs = new();
	readonly object _lock = new();

	public UniMateGenerator( UniMateModel model ) => _model = model;

	public string Name => "UniMate";

	public GeneratorCapabilities Capabilities { get; } = new()
	{
		Modes = new[] { GenerationMode.TextToMotion, GenerationMode.InBetween, GenerationMode.TextEdit, GenerationMode.Expansion, GenerationMode.Variation },
		NativeFps = UniMateModel.Fps,
		MaxSegmentSeconds = Window / UniMateModel.Fps,
		DefaultSeconds = 2f,
		DefaultGuidance = 3f,
	};

	public IReadOnlyList<string> Validate( MotionRig rig ) => UniMateRig.Validate( rig );

	public RigFamily DetectFamily( MotionRig rig ) => UniMateRig.DetectFamily( rig );

	(UniMateRig Rig, PreparedSkeleton Prep) Prepare( MotionRig rig, RigFamily family, CancellationToken token )
	{
		if ( family == RigFamily.Auto ) family = UniMateRig.DetectFamily( rig );
		var key = AnimationWorkspace.Fingerprint( rig.Skeleton ) + rig.Skeleton.RestWorld.Sum( x => x.Pos.X + x.Pos.Y * 3 + x.Pos.Z * 7 ).ToString( "R" ) + "|" + family;
		lock ( _lock )
		{
			if ( _rigs.TryGetValue( key, out var cached ) ) return cached;
			var uniRig = UniMateRig.Build( rig, family );
			var prep = _model.Prepare( uniRig.Skeleton, UniMateStats.For( uniRig.Family ), token );
			return _rigs[key] = (uniRig, prep);
		}
	}

	public Task<IReadOnlyList<GeneratedMotion>> GenerateAsync( MotionRig rig, GenerationRequest request,
		Action<GenerationProgress> progress, CancellationToken token )
		=> Task.Run( () => Generate( rig, request, progress, token ), token );

	IReadOnlyList<GeneratedMotion> Generate( MotionRig rig, GenerationRequest request, Action<GenerationProgress> progress, CancellationToken token )
	{
		progress?.Invoke( new GenerationProgress( "Preparing the skeleton", 0f ) );
		var (uniRig, prep) = Prepare( rig, request.RigFamily, token );
		var takes = Math.Max( 1, request.Count );
		var steps = request.Steps > 0 ? request.Steps : 24;
		var guidance = request.Guidance > 0 ? request.Guidance : 3f;
		var results = new List<GeneratedMotion>();

		// source motion at 30 fps (for in-betweening, editing and variations)
		List<XForm[]> source = null;
		var pins = new List<int>();
		if ( request.SourceFrames is { Count: > 1 } src )
		{
			var scale = UniMateModel.Fps / Math.Max( 1f, request.SourceFps );
			var count = Math.Max( 2, (int)MathF.Round( (src.Count - 1) * scale ) + 1 );
			source = new List<XForm[]>( count );
			for ( var f = 0; f < count; f++ ) source.Add( ClipOps.Sample( src, f / scale ) );
			pins = request.KeepFrames.Select( f => (int)MathF.Round( f * scale ) ).Where( f => f >= 0 && f < count ).Distinct().OrderBy( f => f ).ToList();
		}

		for ( var take = 0; take < takes; take++ )
		{
			token.ThrowIfCancellationRequested();
			var seed = request.Seed + take * 7919;
			var notes = new List<string>();
			List<XForm[]> frames;
			void Report( string stage, float fraction ) => progress?.Invoke( new GenerationProgress(
				takes > 1 ? $"{stage} (take {take + 1} of {takes})" : stage, (take + Math.Clamp( fraction, 0, 1 )) / takes ) );

			switch ( request.Mode )
			{
				case GenerationMode.TextToMotion:
				case GenerationMode.Expansion:
				{
					var segments = request.Prompts.Where( p => !string.IsNullOrWhiteSpace( p ) ).ToList();
					if ( request.Mode == GenerationMode.TextToMotion && segments.Count == 1 ) segments = UniMatePrompt.SplitSteps( segments[0] );
					if ( segments.Count == 0 ) throw new InvalidOperationException( "Describe the motion first." );
					// a single prompt longer than one window repeats as continuing segments
					var wanted = request.DurationSeconds > 0 ? request.DurationSeconds : Capabilities.DefaultSeconds;
					while ( segments.Count * (Window - Overlap) + Overlap < wanted * UniMateModel.Fps && segments.Count < 20 )
						segments.Add( segments[^1] );
					frames = TextChain( uniRig, prep, segments.Select( UniMatePrompt.ToCaption ).ToList(), seed, steps, guidance, Report, token );
					if ( request.Mode == GenerationMode.TextToMotion && wanted > 0 )
					{
						var keep = Math.Clamp( (int)MathF.Round( wanted * UniMateModel.Fps ) + 1, 2, frames.Count );
						frames = frames.GetRange( 0, keep );
					}
					break;
				}
				case GenerationMode.InBetween:
				{
					if ( source is null ) throw new InvalidOperationException( "In-betweening needs an animation with pinned frames." );
					if ( pins.Count < 2 ) throw new InvalidOperationException( "Pin at least two frames (the poses to keep) on the timeline." );
					var caption = UniMatePrompt.ToCaption( request.Prompts.FirstOrDefault() ?? "" );
					frames = Chain( uniRig, prep, null, seed, steps, guidance, source, pins, null, 0f, Report, token, caption );
					break;
				}
				case GenerationMode.TextEdit:
				{
					if ( source is null ) throw new InvalidOperationException( "Editing needs an existing animation." );
					var caption = UniMatePrompt.ToCaption( request.Prompts.FirstOrDefault() ?? "" );
					if ( caption.Length == 0 ) throw new InvalidOperationException( "Describe the new motion for the unlocked bones." );
					var keepJoints = uniRig.JointsForBones( request.KeepBones );
					if ( keepJoints.Count == 0 ) notes.Add( "No bones were locked, so the whole body was regenerated." );
					frames = Chain( uniRig, prep, null, seed, steps, guidance, source, null, keepJoints, 0f, Report, token, caption );
					break;
				}
				case GenerationMode.Variation:
				{
					if ( source is null ) throw new InvalidOperationException( "Variations need an existing animation." );
					var caption = UniMatePrompt.ToCaption( request.Prompts.FirstOrDefault() ?? "An object moves." );
					// start part-way along the flow from a noised copy of the source (SDEdit): low strength stays close
					var t0 = Math.Clamp( 1f - request.VariationStrength, 0.05f, 0.9f );
					frames = Chain( uniRig, prep, null, seed, steps, guidance, source, null, null, t0, Report, token, caption );
					break;
				}
				default:
					throw new NotSupportedException( $"{request.Mode} is not supported." );
			}

			// derived bones: twist helpers follow their limbs. Bones UniMate animates are excluded - they already
			// carry generated motion, and re-deriving them (the retargeter's "inline" follow) would override it and
			// stretch the bones below them
			TwistBoneFollow.Apply( frames, rig.Rig, uniRig.AnimatedBones );
			// back to the workspace frame rate
			var output = Resample( frames, UniMateModel.Fps, request.OutputFps );
			if ( request.CleanUp )
			{
				var cleaned = ClipCleanup.CleanGenerated( output, rig, request.OutputFps );
				if ( cleaned.Length > 0 ) notes.Add( cleaned );
			}
			EnforceConstraints( output, request );
			results.Add( new GeneratedMotion { Frames = output, Fps = request.OutputFps, Seed = seed, Notes = notes } );
		}
		progress?.Invoke( new GenerationProgress( "Done", 1f ) );
		return results;
	}

	/// <summary>
	/// Text to motion as upstream generates it (sample.py with motion expansion): the first window freely (dopri5),
	/// each further window with its first <see cref="Overlap"/> frames held to the previous window's last ones
	/// (replacement sampling, in the network's feature space), the windows' features joined at the seams and decoded
	/// once - so the root path and the poses run on continuously across windows.
	/// </summary>
	List<XForm[]> TextChain( UniMateRig uniRig, PreparedSkeleton prep, List<string> captions, int seed, int steps, float guidance,
		Action<string, float> report, CancellationToken token )
	{
		var J = uniRig.Count;
		const int T = Window;
		var stats = UniMateStats.For( uniRig.Family );
		var windows = captions.Count;
		var total = T + (windows - 1) * (T - Overlap);
		var feat = new float[total, J, 12];
		float[] previous = null;
		for ( var w = 0; w < windows; w++ )
		{
			token.ThrowIfCancellationRequested();
			var embedding = _model.Text.Encode( captions[w], token );
			var noise = UniMateModel.Noise( J, seed + w * 104729 );
			SampleSettings settings;
			if ( previous is null ) settings = FreeSampler( steps, guidance );
			else
			{
				var known = new float[J * 12 * T];
				var keep = new bool[J * 12 * T];
				for ( var jc = 0; jc < J * 12; jc++ )
					for ( var f = 0; f < Overlap; f++ )
					{
						known[jc * T + f] = previous[jc * T + T - Overlap + f];
						keep[jc * T + f] = true;
					}
				settings = ConstrainedSampler( steps, guidance, known, keep, 0f );
			}
			var windowIndex = w;
			var x = _model.Sample( prep, embedding, noise, settings,
				f => report( windows > 1 ? $"Generating part {windowIndex + 1} of {windows}" : "Generating", (windowIndex + f) / windows ), token );
			previous = x;
			var wf = UniMateFeatures.FromModel( x, J, T, stats );
			var start = w == 0 ? 0 : Overlap;
			var at = w == 0 ? 0 : T + (w - 1) * (T - Overlap);
			for ( var f = start; f < T; f++ )
				for ( var j = 0; j < J; j++ )
					for ( var c = 0; c < 12; c++ )
						feat[at + f - start, j, c] = wf[f, j, c];
		}
		var motion = UniMateFeatures.Decode( feat, uniRig.Skeleton.Parents );
		return uniRig.ToFrames( UniMateFeatures.ToSource( motion, uniRig.Skeleton ), null );
	}

	/// <summary>
	/// Free sampling as upstream samples (Sampler.sample_ode: dopri5 integrated to convergence, rtol 1e-3, atol 1e-6).
	/// "Fast" loosens the tolerance (rtol 1e-2: within 0.004 of upstream's motion on a body of diameter 2, a few
	/// dozen calls fewer). Fixed-step shortcuts are not offered: 16 Adams-Bashforth calls land 0.46 away from
	/// upstream's motion, 50 Euler steps 0.13 (DatasetMotionTests, T2A_SAMPLER_STUDY).
	/// </summary>
	static SampleSettings FreeSampler( int steps, float guidance ) => steps <= 12
		? new SampleSettings { Method = Integrator.Dopri5, Guidance = guidance, RelativeTolerance = 1e-2, AbsoluteTolerance = 1e-5 }
		: new SampleSettings { Method = Integrator.Dopri5, Guidance = guidance };

	/// <summary>
	/// Sampling with held values (in-betweening, editing, window seams) as upstream does it (inbetween_sample_ode):
	/// fixed-step Euler with replacement, 50 steps over the flow (fewer when a variation starts part-way along it).
	/// </summary>
	static SampleSettings ConstrainedSampler( int steps, float guidance, float[] known, bool[] keep, float startTime ) =>
		new() { Method = Integrator.Euler, Steps = Math.Max( 8, (int)MathF.Round( 50 * (1 - startTime) ) ), Guidance = guidance, Known = known, Keep = keep, StartTime = startTime };

	/// <summary>
	/// Generates window after window. Each window is encoded from the current result (source frames, or the
	/// motion generated so far) and holds: pinned frames, kept joints, and the overlap with the previous window.
	/// </summary>
	List<XForm[]> Chain( UniMateRig uniRig, PreparedSkeleton prep, List<string> captions, int seed, int steps, float guidance,
		List<XForm[]> source, List<int> pins, HashSet<int> keepJoints, float startTime,
		Action<string, float> report, CancellationToken token, string singleCaption = null )
	{
		var J = uniRig.Count;
		var length = source?.Count ?? (Window + (captions.Count - 1) * (Window - Overlap) + 1);
		var windows = new List<int>();
		for ( var s = 0; ; s += Window - Overlap )
		{
			windows.Add( s );
			if ( s + Window >= length - 1 ) break;
		}
		var result = source is not null ? AnimClip.CopyFrames( source ) : new List<XForm[]>();
		var rest = uniRig.Motion.Skeleton.Bones.Select( b => b.RestLocal ).ToArray();
		for ( var w = 0; w < windows.Count; w++ )
		{
			token.ThrowIfCancellationRequested();
			var start = windows[w];
			var caption = singleCaption ?? captions[Math.Min( w, captions.Count - 1 )];
			var embedding = _model.Text.Encode( caption, token );
			var noise = UniMateModel.Noise( J, seed + w * 104729 );
			var keep = new bool[J * 12 * Window];
			var anyKeep = false;
			void KeepFrame( int f ) { for ( var j = 0; j < J; j++ ) for ( var c = 0; c < 12; c++ ) keep[(j * 12 + c) * Window + f] = true; anyKeep = true; }
			if ( pins is not null ) foreach ( var p in pins ) if ( p >= start && p < start + Window ) KeepFrame( p - start );
			if ( keepJoints is not null )
				foreach ( var j in keepJoints ) for ( var c = 0; c < 12; c++ ) for ( var f = 0; f < Window; f++ ) { keep[(j * 12 + c) * Window + f] = true; anyKeep = true; }
			if ( w > 0 ) for ( var f = 0; f < Overlap; f++ ) KeepFrame( f );

			float[] known = null;
			UniMateFeatures.Alignment alignment = null;
			var needsKnown = anyKeep || startTime > 0;
			if ( needsKnown )
			{
				// T+1 frames of the current result for this window (padded with the last available frame)
				var span = new List<XForm[]>( Window + 1 );
				for ( var f = 0; f <= Window; f++ )
				{
					var i = Math.Min( start + f, result.Count - 1 );
					span.Add( result.Count > 0 ? result[Math.Max( 0, i )] : rest );
				}
				var (pos, rot) = uniRig.JointWorld( span );
				var (feat, align) = UniMateFeatures.Encode( pos, rot, uniRig.Skeleton );
				alignment = align;
				known = UniMateFeatures.ToModel( feat, UniMateStats.For( uniRig.Family ), Window );
			}
			var settings = ConstrainedSampler( steps, guidance, known, anyKeep ? keep : null, startTime );
			var windowIndex = w;
			var x = _model.Sample( prep, embedding, noise, settings,
				f => report( windows.Count > 1 ? $"Generating part {windowIndex + 1} of {windows.Count}" : "Generating", (windowIndex + f) / windows.Count ), token );

			var motion = UniMateFeatures.Decode( UniMateFeatures.FromModel( x, J, Window, UniMateStats.For( uniRig.Family ) ), uniRig.Skeleton.Parents );
			if ( alignment is not null ) UniMateFeatures.Unalign( motion, alignment );
			var src = UniMateFeatures.ToSource( motion, uniRig.Skeleton );
			// bones UniMate doesn't animate (fingers, helpers) keep the source pose
			var baseFrames = source is not null ? Enumerable.Range( 0, Window ).Select( f => source[Math.Min( start + f, source.Count - 1 )] ).ToList() : null;
			var generated = uniRig.ToFrames( src, baseFrames );
			// write the window into the result
			for ( var f = 0; f < Window; f++ )
			{
				var target = start + f;
				if ( target < result.Count ) result[target] = generated[f];
				else result.Add( generated[f] );
			}
		}
		if ( source is not null && result.Count > source.Count ) result = result.GetRange( 0, source.Count );
		return result;
	}

	/// <summary>Pinned poses (in-betweening) and locked bones (editing) come back exactly as they were.</summary>
	static void EnforceConstraints( List<XForm[]> output, GenerationRequest request )
	{
		if ( request.SourceFrames is not { Count: > 1 } src ) return;
		var keepPins = request.Mode == GenerationMode.InBetween && request.KeepFrames.Count > 0;
		var keepBones = request.Mode == GenerationMode.TextEdit && request.KeepBones.Count > 0;
		if ( !keepPins && !keepBones ) return;
		// the source on the output's frame grid
		var scale = request.OutputFps / Math.Max( 1f, request.SourceFps );
		IReadOnlyList<XForm[]> source = src;
		if ( MathF.Abs( scale - 1f ) > 1e-4f )
		{
			var grid = new List<XForm[]>( output.Count );
			for ( var f = 0; f < output.Count; f++ ) grid.Add( ClipOps.Sample( src, f / scale ) );
			source = grid;
		}
		if ( keepBones ) GenerationConstraints.RestoreBones( output, source, request.KeepBones );
		if ( keepPins ) GenerationConstraints.RestorePins( output, source, request.KeepFrames.Select( f => (int)MathF.Round( f * scale ) ) );
	}


	static List<XForm[]> Resample( List<XForm[]> frames, float fromFps, float toFps )
	{
		if ( MathF.Abs( fromFps - toFps ) < 0.01f || frames.Count < 2 ) return frames;
		var duration = (frames.Count - 1) / fromFps;
		var count = Math.Max( 2, (int)MathF.Round( duration * toFps ) + 1 );
		var result = new List<XForm[]>( count );
		for ( var f = 0; f < count; f++ ) result.Add( ClipOps.Sample( frames, f * fromFps / toFps ) );
		return result;
	}
}
