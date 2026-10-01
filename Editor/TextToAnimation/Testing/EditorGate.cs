using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Editor.Session;
using TextToAnimation.Editor.UI;
using TextToAnimation.Generation;
using TextToAnimation.Vmdl;

namespace TextToAnimation.Editor.Testing;

/// <summary>
/// End-to-end test gate run inside a real sbox-dev.exe by dev/editor-rig/run_gate.ps1. It drives the editor
/// window through the same code paths a user does (open model, import, edit, undo, generate, save to the
/// vmdl with compile + playback verification, replace, export, reload) and writes a JSON report.
/// Armed only by the T2A_GATE environment variable PLUS a one-shot marker file, and only inside the
/// t2a-editor-rig scratch project, so it can never act in a real session.
/// </summary>
public static class EditorGate
{
	static bool _started;
	static string _resultPath;
	static readonly Dictionary<string, object> Result = new();
	static readonly List<string> Lines = new();
	static readonly Stopwatch Clock = Stopwatch.StartNew();

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( _started ) return;
		_started = true;
		_resultPath = Environment.GetEnvironmentVariable( "T2A_GATE" );
		if ( string.IsNullOrWhiteSpace( _resultPath ) ) return;
		var marker = _resultPath + ".arm";
		try
		{
			if ( !File.Exists( marker ) ) return; // leaked env var: never run
			File.Delete( marker );
		}
		catch { return; }
		_ = RunAsync();
	}

	static void Note( string message )
	{
		var line = $"[{Clock.Elapsed.TotalSeconds:0.0}s] {message}";
		Lines.Add( line );
		global::Sandbox.Internal.GlobalGameNamespace.Log.Info( "[t2a-gate] " + message );
		Flush();
	}

	static void Set( string key, object value ) { Result[key] = value; Flush(); }

	static void Flush()
	{
		try
		{
			Result["log"] = Lines.ToList();
			File.WriteAllText( _resultPath, JsonSerializer.Serialize( Result, new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch { }
	}

	static async Task<bool> WaitUntil( Func<bool> condition, double seconds )
	{
		var sw = Stopwatch.StartNew();
		while ( sw.Elapsed.TotalSeconds < seconds )
		{
			await EngineThread.SwitchToMainThread();
			if ( EngineThread.Try( condition ) ) return true;
			await EngineThread.DelayOnMain( 250 );
		}
		return false;
	}

	static async Task RunAsync()
	{
		Set( "started", true );
		var passed = false;
		try { passed = await RunStepsAsync(); }
		catch ( Exception e ) { Note( $"EXCEPTION: {e}" ); }
		await EngineThread.SwitchToMainThread();
		Set( "passed", passed );
		Set( "completed", true );
		await Task.Delay( 500 );
		await EngineThread.SwitchToMainThread();
		try { EditorUtility.Quit( true ); } catch ( Exception e ) { Note( $"Quit threw {e.Message}" ); }
		await Task.Delay( 15000 );
		Environment.Exit( passed ? 0 : 1 );
	}

	static async Task<bool> RunStepsAsync()
	{
		if ( !await WaitUntil( () => Project.Current is not null && AssetSystem.All.Any(), 180 ) ) { Note( "asset system never became ready" ); return false; }
		var root = Project.Current.GetRootPath() ?? "";
		if ( root.IndexOf( "t2a-editor-rig", StringComparison.OrdinalIgnoreCase ) < 0 ) { Note( $"REFUSING: '{root}' is not the gate scratch project" ); return false; }
		var assets = Project.Current.GetAssetsPath();
		Note( $"project {root}" );
		var checks = new Dictionary<string, bool>();
		void Check( string name, bool ok, string detail = "" ) { checks[name] = ok; Set( "checks", checks ); Note( $"{(ok ? "PASS" : "FAIL")} {name} {detail}" ); }

		// ---- 1. the start page (drop / choose a VMDL, or start fresh), then "New from Citizen Human":
		//         a copy of the stock .vmdl in the project, compiled and opened
		await EngineThread.SwitchToMainThread();
		var window = TextToAnimationWindow.Open();
		await EngineThread.DelayOnMain( 500 );
		Check( "start page shows first", window.ShowsStartPage && !window.Session.HasModel );
		if ( Environment.GetEnvironmentVariable( "T2A_GATE_SHOWCASE" ) == "1" ) await EngineThread.DelayOnMain( 5000 );
		var folder = Path.Combine( assets, "t2a_gate" );
		Directory.CreateDirectory( folder );
		var vmdlPath = Path.Combine( folder, "gate_human.vmdl" );
		var started = Stopwatch.StartNew();
		await window.CreateFromStarterAsync( StarterModels.CitizenHuman, vmdlPath );
		await EngineThread.SwitchToMainThread();
		Note( $"new model from Citizen Human in {started.Elapsed.TotalSeconds:0.0} s" );
		var copied = File.Exists( vmdlPath ) && File.ReadAllText( vmdlPath ) == StarterModels.SourceText( StarterModels.CitizenHuman );
		Check( "new from Citizen Human copies the vmdl", copied );
		var asset = AssetSystem.FindByPath( vmdlPath );
		Check( "test model compiles", asset is not null && asset.IsCompiled && window.Session.HasModel, window.Session.ModelAsset?.Path ?? "" );
		if ( !window.Session.HasModel ) return false;
		Check( "start page hides once a model is open", !window.ShowsStartPage );
		Check( "an empty workspace starts with a new-animation prompt", window.EditPrompt.Target is null );

		// ---- 2. the opened model
		var session = window.Session;
		Check( "model opens", session.HasModel );
		Check( "humanoid recognised", session.Rig?.IsHumanoid == true, string.Join( "; ", session.Rig?.Problems ?? new List<string>() ) );
		Set( "bones", session.Rig?.Skeleton.Count ?? 0 );
		Set( "sequences", session.Sequences.Count );
		Check( "inherited animations listed", session.Sequences.Count > 10, $"{session.Sequences.Count}" );
		Set( "up", session.Rig.Up.ToString() );
		Set( "forward", session.Rig.Forward.ToString() );
		Check( "engine up is +Z", session.Rig.Up == System.Numerics.Vector3.UnitZ );

		// ---- 3. import an existing animation
		var walkName = session.Sequences.Select( s => s.Name ).FirstOrDefault( n => n.Contains( "walk", StringComparison.OrdinalIgnoreCase ) )
			?? session.Sequences[0].Name;
		var imported = await session.ImportSequenceAsync( walkName );
		var frames = imported.Frames;
		var moves = frames.Count > 2 && Fixtures.MaxRotationChange( frames, session.Rig ) > 5f;
		Check( "import samples a moving animation", moves, $"{walkName}: {frames.Count} frames" );
		var quality = ClipQuality.Analyze( imported, session.Rig );
		Set( "importIssues", quality.Select( q => $"{q.Severity} {q.Code}: {q.Message}" ).ToList() );
		Check( "imported animation has no quality errors", quality.All( q => q.Severity != IssueSeverity.Error ) );

		// ---- 4. editing + undo/redo
		var before = imported.FrameCount;
		session.Edit( "Reverse", c => ClipOps.Reverse( c, session.Rig ) );
		session.Edit( "Crop", c => ClipOps.Crop( c, session.Rig, 0, Math.Max( 1, before / 2 ) ) );
		var cropped = imported.FrameCount;
		session.UndoEdit();
		Check( "undo restores", imported.FrameCount == before, $"{cropped} -> {imported.FrameCount}" );
		session.RedoEdit();
		Check( "redo re-applies", imported.FrameCount == cropped );
		session.UndoEdit(); session.UndoEdit();

		// ---- 5. viewport renders the posed model
		session.SelectClip( imported );
		await EngineThread.DelayOnMain( 500 );
		var shots = Path.Combine( Path.GetDirectoryName( _resultPath )!, "gate_shots" );
		Directory.CreateDirectory( shots );
		try
		{
			File.WriteAllBytes( Path.Combine( shots, "viewport_imported.png" ), window.Viewport.RenderToPng() );
			Check( "viewport renders", true );
		}
		catch ( Exception e ) { Check( "viewport renders", false, e.Message ); }

		// ---- 6. generate from the editor's prompt: type a prompt and press Enter
		GeneratorService.Instance.Refresh();
		AnimClip generated = null;
		if ( GeneratorService.Instance.State == ModelState.Ready )
		{
			window.SetIntent( UI.EditIntent.New );
			var composer = window.EditPrompt;
			composer.Options.Seconds = 2f;
			composer.Options.Steps = 12;
			composer.Options.Seed = 7;
			composer.Options.Takes = 1;
			composer.Text = "walk forward";
			var beforeGenerate = session.Workspace.Clips.ToList();
			composer.Input.Focus();
			composer.Input.PostKeyEvent( KeyCode.Enter );
			var enterStarted = await WaitUntil( () => window.Flow.Running, 10 );
			Check( "Enter in the prompt starts generating", enterStarted );
			if ( !enterStarted ) composer.Submit();
			await EngineThread.DelayOnMain( 500 );
			await WaitUntil( () => !window.Flow.Running && !session.Busy, 600 );
			await EngineThread.DelayOnMain( 300 );
			generated = session.Workspace.Clips.Except( beforeGenerate ).FirstOrDefault();
			Check( "UniMate generates", generated?.Origin == ClipOrigin.Generated, generated?.Name ?? "" );
			Check( "the result opens in the editor", generated is not null && session.ActiveClip == generated );
			Check( "the prompt clears after sending", composer.Text.Length == 0 );
			if ( generated is not null )
			{
				var issues = ClipQuality.Analyze( generated, session.Rig );
				Set( "generatedIssues", issues.Select( q => $"{q.Severity} {q.Code}: {q.Message}" ).ToList() );
				Check( "generated animation has no quality errors", issues.All( q => q.Severity != IssueSeverity.Error ) );
				var path = RootTools.HipsTrajectory( generated.Frames, session.Rig );
				var travel = RootTools.Horizontal( session.Rig, path[^1] - path[0] );
				Set( "generatedTravel", travel.ToString() );
				Check( "generated walk travels forward", System.Numerics.Vector3.Dot( travel, session.Rig.Forward ) > session.Rig.Cm( 30f ), travel.ToString() );
				await EngineThread.DelayOnMain( 400 );
				File.WriteAllBytes( Path.Combine( shots, "viewport_generated.png" ), window.Viewport.RenderToPng() );
			}
		}
		else Note( $"UniMate not installed ({GeneratorService.Instance.State}) - generation skipped" );

		// ---- 7. save into the vmdl: compile + playback verification (scale/orientation/root)
		var toSave = generated ?? imported;
		session.SelectClip( toSave );
		var saved = await window.Save.SaveAsync( toSave );
		await EngineThread.SwitchToMainThread();
		Check( "save to vmdl compiles and plays back", saved, toSave.EffectiveSequenceName );
		await session.RefreshModelAsync();
		Check( "saved sequence is in the model", session.Model.AnimationNames.Contains( toSave.EffectiveSequenceName ) );

		// ---- 8. re-save updates the same sequence (no duplicate)
		session.Edit( "Trim end", c => ClipOps.Crop( c, session.Rig, 0, c.FrameCount - 3 ) );
		var resaved = await window.Save.SaveAsync( toSave );
		await EngineThread.SwitchToMainThread();
		var count = Kv3Sequences( vmdlPath ).Count( n => n == toSave.EffectiveSequenceName );
		Check( "re-save updates in place", resaved && count == 1, $"{count} entries" );

		// ---- 9. replace an existing sequence with another clip
		var replaced = await window.Save.ReplaceAsync( imported, toSave.EffectiveSequenceName );
		await EngineThread.SwitchToMainThread();
		Check( "replace existing works", replaced );

		// ---- 10. export
		var exportDir = Path.Combine( assets, "t2a_gate_export" );
		window.Save.Export( imported, exportDir );
		Check( "export writes dmx + vmdl", Directory.Exists( exportDir ) && Directory.GetFiles( exportDir, "*.dmx" ).Length == 1 && Directory.GetFiles( exportDir, "*.vmdl" ).Length == 1 );

		// ---- 11. workspace persists across reopen
		var clipCount = session.Workspace.Clips.Count;
		session.FlushSave();
		var reopened = new EditorSession();
		var error = await reopened.OpenModelAsync( asset );
		Check( "workspace reloads", error is null && reopened.Workspace.Clips.Count == clipCount, $"{reopened.Workspace?.Clips.Count}/{clipCount}" );

		// ---- 12. backups exist
		var backups = Path.Combine( root, "text_to_animation", "backups" );
		Check( "backups kept", Directory.Exists( backups ) && Directory.GetFiles( backups ).Length > 0 );

		// ---- 12b. "New from Citizen" (the stylised character) also copies, compiles and opens as a humanoid
		var citizenPath = Path.Combine( folder, "gate_citizen.vmdl" );
		var citizen = await StarterModels.CreateFromStarterAsync( StarterModels.Citizen, citizenPath );
		await EngineThread.SwitchToMainThread();
		var citizenSession = new EditorSession();
		var citizenError = citizen.Compiled ? await citizenSession.OpenModelAsync( citizen.Asset ) : citizen.Error;
		await EngineThread.SwitchToMainThread();
		Check( "new from Citizen compiles and opens", citizenError is null && citizenSession.Rig?.IsHumanoid == true && citizenSession.Sequences.Count > 10,
			citizenError ?? $"{citizenSession.Sequences.Count} sequences" );

		// ---- 13. posing: a key on one bone moves it to the requested pose, undo removes it
		session.SelectClip( imported );
		var arm = session.Rig.Skeleton.IndexOf( "arm_upper_R" );
		var keyFrame = Math.Min( 10, imported.FrameCount - 1 );
		var baseLocal = session.ActiveFrames[keyFrame][arm];
		var posed = new Maths.XForm( baseLocal.Pos, System.Numerics.Quaternion.Normalize( System.Numerics.Quaternion.CreateFromAxisAngle( System.Numerics.Vector3.UnitX, 0.5f ) * baseLocal.Rot ) );
		session.SelectBone( arm );
		session.BeginInteractiveEdit( "Pose arm_upper_R" );
		session.SetPoseKey( arm, keyFrame, posed );
		session.EndInteractiveEdit();
		var poseError = Maths.MathQ.AngleBetween( session.ActiveFrames[keyFrame][arm].Rot, posed.Rot ) * 180f / MathF.PI;
		Check( "pose key reaches the requested pose", imported.Keys.KeyedBones.Contains( "arm_upper_R" ) && poseError < 0.5f, $"{poseError:0.000}°" );
		session.UndoEdit();
		var undoError = Maths.MathQ.AngleBetween( session.ActiveFrames[keyFrame][arm].Rot, baseLocal.Rot ) * 180f / MathF.PI;
		Check( "undo removes the pose key", !imported.Keys.KeyedBones.Contains( "arm_upper_R" ) && undoError < 0.01f, $"{undoError:0.0000}°" );

		// ---- 14. bone locks
		var legL = session.Rig.Skeleton.IndexOf( "leg_upper_L" );
		session.SelectBone( legL );
		BoneLocks.LockSelected( session );
		Check( "lock selected", imported.LockedBones.SetEquals( new[] { "leg_upper_L" } ), string.Join( ",", imported.LockedBones ) );
		BoneLocks.LockHierarchy( session );
		Check( "lock hierarchy", imported.LockedBones.Contains( "leg_lower_L" ) && imported.LockedBones.Contains( "ankle_L" ), $"{imported.LockedBones.Count} bones" );
		BoneLocks.LockAllExcept( session );
		Check( "lock all except selected", !imported.LockedBones.Contains( "leg_lower_L" ) && imported.LockedBones.Contains( "arm_upper_R" ) && imported.LockedBones.Contains( "pelvis" ), $"{imported.LockedBones.Count} bones" );
		session.SelectBone( null );
		BoneLocks.Unlock( session );
		Check( "unlock all", imported.LockedBones.Count == 0 );

		// ---- 15. every generation mode on the real model
		if ( generated is not null )
			await RunModeChecksAsync( window, session, generated, Check );

		// ---- 15b. the editor's prompt: "change only the arms" keeps the legs exactly
		if ( generated is not null )
		{
			window.OpenEditor( generated );
			window.SetIntent( UI.EditIntent.Change );
			var editPrompt = window.EditPrompt;
			editPrompt.Options.Scope = UI.ChangeScope.Arms;
			editPrompt.Options.Steps = 12;
			editPrompt.Options.Seed = 5;
			editPrompt.Options.Takes = 1;
			var beforeEdit = session.Workspace.Clips.ToList();
			await window.RunEditPromptAsync( "wave with both hands" );
			await EngineThread.SwitchToMainThread();
			var armsChanged = session.Workspace.Clips.Except( beforeEdit ).FirstOrDefault();
			if ( armsChanged is null ) Check( "editor prompt changes only the arms", false, "no result" );
			else
			{
				var rig = session.Rig;
				var legs = Enumerable.Range( 0, rig.Skeleton.Count ).Where( b => rig.IsMotionBone( b ) && rig.RegionOf( b ) is BodyRegion.LegL or BodyRegion.LegR ).ToList();
				var arms = Enumerable.Range( 0, rig.Skeleton.Count ).Where( b => rig.IsMotionBone( b ) && rig.RegionOf( b ) is BodyRegion.ArmL or BodyRegion.ArmR ).ToList();
				var source = generated.EvaluateFrames( rig.Skeleton );
				float Max( IEnumerable<int> bones )
				{
					var worst = 0f;
					for ( var f = 0; f < Math.Min( source.Count, armsChanged.FrameCount ); f++ )
						foreach ( var b in bones ) worst = MathF.Max( worst, Maths.MathQ.AngleBetween( source[f][b].Rot, armsChanged.Frames[f][b].Rot ) );
					return worst * 180f / MathF.PI;
				}
				var legError = Max( legs );
				var armChange = Max( arms );
				Check( "editor prompt changes only the arms", legs.Count > 4 && legError < 0.01f && armChange > 5f, $"legs {legError:0.0000}°, arms {armChange:0.0}°" );
			}
		}

		// ---- 16. showcase for window screenshots (driver -Capture): each editor tab
		if ( Environment.GetEnvironmentVariable( "T2A_GATE_SHOWCASE" ) == "1" )
		{
			session.SelectClip( generated ?? imported );
			session.SelectBone( session.Rig.Skeleton.IndexOf( "arm_upper_R" ) );
			session.Playing = true;
			foreach ( var tab in new[] { 0, 1, 2 } )
			{
				window.ShowTab( tab );
				Note( $"showcase editor tab {tab}" );
				await EngineThread.DelayOnMain( 4000 );
			}
		}

		return checks.Values.All( v => v );
	}

	/// <summary>
	/// In-betweening, text-guided editing, variations, multi-step sequences and cancellation, each through the
	/// same request builder and flow as the Generate panel, checked against what the user asked to keep.
	/// </summary>
	static async Task RunModeChecksAsync( TextToAnimationWindow window, EditorSession session, AnimClip source, Action<string, bool, string> check )
	{
		var rig = session.Rig;
		var flow = window.Flow;
		const int Steps = 12;

		async Task<List<AnimClip>> Generate( GenerationMode mode, string[] prompts, int count, string name, float strength = 0.5f )
		{
			await EngineThread.SwitchToMainThread();
			session.SelectClip( source );
			var request = GenerationFlow.BuildRequest( session, mode, prompts, 2f, 11, count, 3f, Steps, strength );
			var problem = GenerationFlow.Validate( request );
			if ( problem is not null ) { Note( $"{mode} request invalid: {problem}" ); return new List<AnimClip>(); }
			var before = session.Workspace.Clips.ToList();
			var sw = Stopwatch.StartNew();
			var ok = await flow.GenerateAsync( request, replace: false, name );
			await EngineThread.SwitchToMainThread();
			Note( $"{mode}: {(ok ? "ok" : "failed")} in {sw.Elapsed.TotalSeconds:0.0} s" );
			return ok ? session.Workspace.Clips.Except( before ).ToList() : new List<AnimClip>();
		}
		bool NoErrors( AnimClip c ) => ClipQuality.Analyze( c, rig ).All( q => q.Severity != IssueSeverity.Error );
		float MaxAngle( IReadOnlyList<Maths.XForm[]> a, IReadOnlyList<Maths.XForm[]> b, IEnumerable<int> bones, IEnumerable<int> frames )
		{
			var worst = 0f;
			foreach ( var f in frames ) foreach ( var bone in bones )
				worst = MathF.Max( worst, Maths.MathQ.AngleBetween( a[f][bone].Rot, b[f][bone].Rot ) );
			return worst * 180f / MathF.PI;
		}
		var motionBones = Enumerable.Range( 0, rig.Skeleton.Count ).Where( rig.IsMotionBone ).ToList();
		var sourceFrames = source.EvaluateFrames( rig.Skeleton );
		var all = Enumerable.Range( 0, source.FrameCount ).ToList();

		// in-betweening: pinned poses come back exactly, the frames between are regenerated
		session.SelectClip( source );
		var pins = new[] { 0, source.FrameCount / 2, source.FrameCount - 1 };
		session.Edit( "Pin frames", c => { c.PinnedFrames.Clear(); foreach ( var p in pins ) c.PinnedFrames.Add( p ); } );
		var inbetween = (await Generate( GenerationMode.InBetween, new[] { "" }, 1, "Gate in-between" )).FirstOrDefault();
		if ( inbetween is null ) check( "in-between generates", false, "" );
		else
		{
			var pinError = MaxAngle( inbetween.Frames, sourceFrames, Enumerable.Range( 0, rig.Skeleton.Count ), pins );
			var hipsError = pins.Max( p => (inbetween.Frames[p][rig.HipsIndex].Pos - sourceFrames[p][rig.HipsIndex].Pos).Length() );
			check( "in-between keeps pinned poses exactly", inbetween.FrameCount == source.FrameCount && pinError < 0.01f && hipsError < 0.01f, $"{pinError:0.0000}° {hipsError:0.0000} in" );
			check( "in-between has no quality errors", NoErrors( inbetween ), "" );
		}
		session.SelectClip( source );
		session.Edit( "Unpin", c => c.PinnedFrames.Clear() );

		// text-guided editing: both legs locked, the rest follows a new prompt
		session.SelectClip( source );
		session.SelectBones( new[] { rig.Skeleton.IndexOf( "leg_upper_L" ), rig.Skeleton.IndexOf( "leg_upper_R" ) } );
		BoneLocks.LockHierarchy( session );
		var locked = source.LockedBones.Select( rig.Skeleton.IndexOf ).Where( i => i >= 0 ).ToList();
		var edited = (await Generate( GenerationMode.TextEdit, new[] { "wave with the right hand" }, 1, "Gate edit" )).FirstOrDefault();
		if ( edited is null ) check( "text edit generates", false, "" );
		else
		{
			var lockedError = MaxAngle( edited.Frames, sourceFrames, locked, all );
			var free = motionBones.Except( locked ).ToList();
			var changed = MaxAngle( edited.Frames, sourceFrames, free, all );
			check( "text edit keeps locked bones exactly", locked.Count >= 6 && lockedError < 0.01f, $"{locked.Count} bones, {lockedError:0.0000}°" );
			check( "text edit changes the unlocked bones", changed > 5f, $"{changed:0.0}°" );
			check( "text edit has no quality errors", NoErrors( edited ), "" );
		}
		session.SelectClip( source );
		session.SelectBone( null );
		BoneLocks.Unlock( session );

		// variations: two takes, both different from the source and from each other
		var takes = await Generate( GenerationMode.Variation, new[] { "" }, 2, "Gate variation", 0.5f );
		if ( takes.Count != 2 ) check( "variations generate two takes", false, $"{takes.Count}" );
		else
		{
			var fromSource = MaxAngle( takes[0].Frames, sourceFrames, motionBones, all.Where( f => f < takes[0].FrameCount ) );
			var between = MaxAngle( takes[0].Frames, takes[1].Frames, motionBones, Enumerable.Range( 0, Math.Min( takes[0].FrameCount, takes[1].FrameCount ) ) );
			check( "variations generate two takes", takes.All( t => t.FrameCount == source.FrameCount ), string.Join( ",", takes.Select( t => t.Name ) ) );
			check( "variations differ from the source and each other", fromSource > 2f && between > 2f, $"{fromSource:0.0}° / {between:0.0}°" );
			check( "variations have no quality errors", takes.All( NoErrors ), "" );
		}

		// multi-step sequence: three prompts chained into one longer motion
		var sequence = (await Generate( GenerationMode.Expansion, new[] { "walk forward", "turn around", "walk forward" }, 1, "Gate sequence" )).FirstOrDefault();
		check( "multi-step sequence generates a longer motion", sequence is not null && sequence.Duration > 4f, sequence is null ? "" : $"{sequence.Duration:0.00} s" );
		if ( sequence is not null ) check( "multi-step sequence has no quality errors", NoErrors( sequence ), "" );

		// cancellation: nothing is added and the editor is usable again
		await EngineThread.SwitchToMainThread();
		session.SelectClip( source );
		var clipsBefore = session.Workspace.Clips.Count;
		var request = GenerationFlow.BuildRequest( session, GenerationMode.TextToMotion, new[] { "jump" }, 4f, 3, 1, 3f, 40, 0.5f );
		var task = flow.GenerateAsync( request, replace: false, "Gate cancelled" );
		await EngineThread.DelayOnMain( 1500 );
		var wasBusy = session.Busy;
		flow.Cancel();
		var result = await task;
		await EngineThread.SwitchToMainThread();
		check( "generation can be cancelled", wasBusy && !result && !session.Busy && session.Workspace.Clips.Count == clipsBefore, $"busy={wasBusy} result={result}" );
	}

	static List<string> Kv3Sequences( string vmdlPath )
	{
		var doc = Kv3.Parse( File.ReadAllText( vmdlPath ) );
		return ClipVmdlWriter.ExistingAnimFiles( (KvObject)((KvObject)doc.Root)["rootNode"] ).Keys.ToList();
	}

	static class Fixtures
	{
		public static float MaxRotationChange( List<Maths.XForm[]> frames, MotionRig rig )
		{
			var worst = 0f;
			for ( var b = 0; b < rig.Skeleton.Count; b++ )
				worst = MathF.Max( worst, Maths.MathQ.AngleBetween( frames[0][b].Rot, frames[frames.Count / 2][b].Rot ) );
			return worst * 180f / MathF.PI;
		}
	}
}
