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
		if ( Environment.GetEnvironmentVariable( "T2A_GATE_ONLY_CREATURES" ) == "1" )
		{
			var onlyShots = Path.Combine( Path.GetDirectoryName( _resultPath )!, "gate_shots" );
			Directory.CreateDirectory( onlyShots );
			await RunCreatureChecksAsync( window, assets, onlyShots, Check );
			return checks.Values.All( v => v );
		}
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
		Check( "an empty workspace shows the quick start and the empty side panel", window.ShowsQuickStart && window.ShowsSideEmptyState );
		window.QuickStart.Pick( 0 );
		Check( "a quick-start example fills the prompt", window.EditPrompt.Text == window.QuickStart.Examples[0], window.EditPrompt.Text );
		window.EditPrompt.Text = "";

		// ---- 2. the opened model
		var session = window.Session;
		Check( "model opens", session.HasModel );
		DumpSkeleton( session.Rig, Path.Combine( Path.GetDirectoryName( _resultPath )!, "gate_skeleton.json" ) );
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
			var recorded = session.PromptHistory.FirstOrDefault();
			Check( "the prompt is remembered in the history", recorded?.Prompt == "walk forward" && generated is not null && recorded.ClipIds.Contains( generated.Id ), recorded?.Prompt ?? "" );
			var popup = new UI.PromptHistoryPopup( window, session, p => composer.Text = p );
			popup.OpenAbove( composer );
			await EngineThread.DelayOnMain( 300 );
			Check( "the history popup opens", popup.IsValid() && popup.Visible );
			popup.Close();
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
		Check( "prompt history persists", reopened.PromptHistory.Count == session.PromptHistory.Count && reopened.PromptHistory.Count > 0, $"{reopened.PromptHistory.Count}/{session.PromptHistory.Count}" );

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

		// ---- 15c. every Edit section opens and shows its contents
		window.ShowTab( 0 );
		var folds = window.EditPanel.Children.OfType<UI.TaFold>().ToList();
		var wasOpen = folds.Select( f => f.Open ).ToList();
		foreach ( var f in folds ) f.Open = true;
		await EngineThread.DelayOnMain( 400 );
		var empty = folds.Where( f => !f.Body.Visible || f.Body.Height < 20 ).Count();
		Check( "every Edit section opens with its contents", folds.Count >= 5 && empty == 0, $"{folds.Count} sections, {empty} empty" );
		for ( var i = 0; i < folds.Count; i++ ) folds[i].Open = wasOpen[i];

		// ---- 15d. the timeline: highlight two points, act on the highlight, undo
		if ( generated is not null )
		{
			session.SelectClip( generated );
			session.Seek( 10 );
			var widthBefore = window.GetWindow().Width;
			window.Timeline.SetInOut( 10, true );
			window.Timeline.SetInOut( 30, false );
			await EngineThread.DelayOnMain( 200 );
			Check( "I/O highlight frames on the timeline", session.Range is { Start: 10, End: 30 } && window.ShowsRangeBar, $"{session.Range}" );
			Check( "the range bar fits without widening the window", window.GetWindow().Width <= widthBefore + 0.5f, $"{widthBefore} -> {window.GetWindow().Width}" );
			Check( "the range bar keeps its labels at the default size", !window.RangeBarCompact );
			var highlightBefore = generated.EvaluateFrames( session.Rig.Skeleton );
			var spine = session.Rig.Skeleton.IndexOf( "spine_1" );
			UI.TimelineWidget.RangeEdit( session, "Reverse selection", ( c, from, to ) => ClipOps.ReverseSection( c, session.Rig, from, to ) );
			var after = generated.EvaluateFrames( session.Rig.Skeleton );
			var mirrored = Maths.MathQ.AngleBetween( highlightBefore[30][spine].Rot, after[10][spine].Rot ) < 1e-3f;
			Check( "reverse on the highlight plays those frames backwards", mirrored && session.Range is null && after.Count == highlightBefore.Count );
			session.UndoEdit();
			await EngineThread.DelayOnMain( 300 );
			Check( "clearing the highlight leaves the window size alone", window.GetWindow().Width <= widthBefore + 0.5f, $"{widthBefore} -> {window.GetWindow().Width}" );
			Check( "undo restores the highlight edit",Maths.MathQ.AngleBetween( highlightBefore[10][spine].Rot, generated.EvaluateFrames( session.Rig.Skeleton )[10][spine].Rot ) < 1e-4f );
		}

		// ---- 15e. creatures from FBX: vmdl, compile, size, generate, save (appended), engine playback
		await RunCreatureChecksAsync( window, assets, shots, Check );

		// ---- 16. showcase for window screenshots (driver -Capture): each editor tab
		if ( Environment.GetEnvironmentVariable( "T2A_GATE_SHOWCASE" ) == "1" )
		{
			async Task Width( string step ) { await EngineThread.DelayOnMain( 300 ); Note( $"showcase width after {step}: {window.GetWindow().Width}" ); }
			await Width( "start" );
			session.SelectClip( generated ?? imported );
			await Width( "select clip" );
			session.SelectBone( session.Rig.Skeleton.IndexOf( "arm_upper_R" ) );
			await Width( "select bone" );
			session.Playing = false;
			session.Seek( 20 );
			window.Timeline.SetInOut( 12, true );
			await Width( "in point" );
			window.Timeline.SetInOut( 34, false );
			await Width( "out point" );
			foreach ( var tab in new[] { 0, 1, 2 } )
			{
				window.ShowTab( tab );
				// the Edit tab with every section open, so the shot shows all of its controls
				if ( tab == 0 ) foreach ( var f in window.EditPanel.Children.OfType<UI.TaFold>() ) f.Open = true;
				Note( $"showcase editor tab {tab}" );
				await Width( $"tab {tab}" );
				await EngineThread.DelayOnMain( 4000 );
			}
			var shown = new UI.PromptHistoryPopup( window, session, _ => { } );
			shown.OpenAt( window.EditPrompt.ScreenRect.TopLeft + new Vector2( 300, -shown.Height - 8 ), animate: false );
			Note( "showcase prompt history" );
			await EngineThread.DelayOnMain( 4000 );
			shown.Close();
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

	/// <summary>A creature from an FBX: prompts to animate it with, and its real size (metres, from the FBX export).</summary>
	sealed record Creature( string Name, string[] Prompts, float HeightMeters, string Fbx = null );

	static readonly Creature[] Creatures =
	{
		new( "fox", new[] { "walk forward", "jump" }, 0.511f ),
		new( "brainstem", new[] { "walk forward", "wave" }, 1.7f ),
		new( "shark", new[] { "swim forward", "turn left" }, 0.889f ),
		new( "octopus", new[] { "wave its tentacles", "crawl forward" }, 0.687f ),
	};

	/// <summary>
	/// For each creature FBX (T2A_GATE_CREATURES folder): make a vmdl, compile it, check its size, open it, generate
	/// two animations, save both (appended, the model's own nodes untouched, engine playback verified) and render
	/// the bind pose and the compiled animations for review.
	/// </summary>
	static async Task RunCreatureChecksAsync( TextToAnimationWindow window, string assets, string shots, Action<string, bool, string> check )
	{
		var source = Environment.GetEnvironmentVariable( "T2A_GATE_CREATURES" );
		if ( string.IsNullOrEmpty( source ) || !Directory.Exists( source ) ) { Note( "no creature folder - creature checks skipped" ); return; }
		var session = window.Session;
		var report = new Dictionary<string, object>();
		// unseen rigs, dropped in as downloaded (no conversion, no fixes): T2A_GATE_EXTRA folder of *.fbx
		var extra = Environment.GetEnvironmentVariable( "T2A_GATE_EXTRA" );
		var list = Creatures.ToList();
		if ( Environment.GetEnvironmentVariable( "T2A_GATE_ONLY_EXTRA" ) == "1" ) list.Clear();
		if ( !string.IsNullOrEmpty( extra ) && Directory.Exists( extra ) )
			list.AddRange( Directory.GetFiles( extra, "*.fbx" ).Select( f => new Creature( Path.GetFileNameWithoutExtension( f ), new[] { "walk forward", "turn around" }, -1f, f ) ) );
		foreach ( var creature in list )
		{
			try { await RunCreatureAsync( window, shots, check, creature, source, session, report ); }
			catch ( Exception e )
			{
				await EngineThread.SwitchToMainThread();
				check( $"{creature.Name}: no crash", false, e.GetType().Name + ": " + e.Message );
			}
		}
	}

	static async Task RunCreatureAsync( TextToAnimationWindow window, string shots, Action<string, bool, string> check, Creature creature,
		string source, EditorSession session, Dictionary<string, object> report )
	{
		{
			var fbx = creature.Fbx ?? Path.Combine( source, creature.Name, creature.Name + ".fbx" );
			if ( !File.Exists( fbx ) ) { check( $"{creature.Name}: FBX present", false, fbx ); return; }
			// the user's path: drop a rigged FBX -> textures, materials, vmdl, compile
			VmdlCompiler.CompileResult compile;
			try { compile = await StarterModels.ImportFbxAsync( fbx ); }
			catch ( InvalidOperationException refused )
			{
				// not an animatable model (animation-only file, unskinned mesh): it must be refused with a reason
				await EngineThread.SwitchToMainThread();
				check( $"{creature.Name}: refused with a reason", refused.Message.Length > 20, refused.Message );
				return;
			}
			await EngineThread.SwitchToMainThread();
			var vmdlPath = compile.Asset?.AbsolutePath ?? "";
			var folder = Path.GetDirectoryName( vmdlPath ) ?? "";
			if ( !compile.Compiled && compile.Asset is not null && compile.Error is { Length: > 40 } )
			{
				check( $"{creature.Name}: refused with a reason", true, compile.Error );
				return;
			}
			check( $"{creature.Name}: vmdl from FBX compiles", compile.Compiled, compile.Error ?? "" );
			if ( !compile.Compiled ) return;
			var vmats = Directory.GetFiles( folder, "*.vmat" );
			var textured = vmats.Count( v => File.ReadAllLines( v ).Any( l => l.Contains( "TextureColor" ) && !l.Contains( "materials/default" ) ) );
			var tinted = vmats.Count( v => File.ReadAllLines( v ).Any( l => l.Contains( "g_vColorTint" ) ) );
			var hasImages = Directory.GetFiles( folder, "*.png", SearchOption.AllDirectories ).Concat( Directory.GetFiles( folder, "*.jpg", SearchOption.AllDirectories ) ).Any();
			// every material shows the model's look: an image, or the colour its material authors
			check( $"{creature.Name}: textures found and materials generated", hasImages && creature.HeightMeters > 0 ? textured > 0 : true, // unseen downloads may reference textures that were never published
				$"{textured} textured, {tinted} colour-only, {vmats.Length} materials" );

			Note( $"{creature.Name}: opening" );
			var error = await window.OpenModelAsync( compile.Asset );
			await EngineThread.SwitchToMainThread();
			if ( error is not null ) { check( $"{creature.Name}: opens", false, error ); return; }
			var model = session.Model;
			var heightIn = model.Bounds.Size.z;
			var expectedIn = creature.HeightMeters * 100f * 0.3937f;
			if ( creature.HeightMeters > 0 )
				check( $"{creature.Name}: compiled size matches the FBX", MathF.Abs( heightIn - expectedIn ) < 0.15f * expectedIn, $"{heightIn:0.0} in vs {expectedIn:0.0} in" );
			else
				check( $"{creature.Name}: compiled to a sensible size", model.Bounds.Size.Length is > 2f and < 2000f, $"{model.Bounds.Size}" );
			var rig = session.Rig;
			var a = rig.Analysis;
			report[creature.Name] = new
			{
				bones = rig.Skeleton.Count, humanoid = rig.IsHumanoid, family = Inference.UniMate.UniMateRig.DetectFamily( rig ).ToString(), facing = a.Facing.ToString(),
				limbs = a.Limbs.Select( l => $"{l.Kind} {l.Side}: {l.Chain.Count}" ).ToList(), problems = rig.Problems.ToList(),
			};
			Set( "creatures", report );
			var dumpModel = session.Model;
			_bindScaleOf = n => dumpModel.Bones.GetBone( n ) is { } bb ? new[] { bb.LocalTransform.Scale.x, bb.LocalTransform.Scale.y, bb.LocalTransform.Scale.z } : null;
			DumpSkeleton( rig, Path.Combine( shots, $"creature_{creature.Name}_skeleton.json" ) );
			var problems = UniMateRigProblems( rig );
			if ( problems.Count > 0 && rig.Skeleton.Count < 3 )
			{
				// too few bones for UniMate: it must say so, not fail obscurely
				check( $"{creature.Name}: refused generation with a reason", problems.All( p => p.Length > 20 ), string.Join( " ", problems ) );
				return;
			}
			check( $"{creature.Name}: skeleton read for generation", rig.Skeleton.Count > 2 && problems.Count == 0, $"{rig.Skeleton.Count} bones; {string.Join( " ", problems )}" );

			// the compiled model as modeldoc shows it: bind pose, no animation
			session.SelectClip( null );
			window.Viewport.FrameCharacter();
			window.Viewport.SetView( 35f, 12f );
			await EngineThread.DelayOnMain( 600 );
			File.WriteAllBytes( Path.Combine( shots, $"creature_{creature.Name}_bind.png" ), window.Viewport.RenderToPng() );

			// generate two animations and save both into the vmdl
			var made = new List<AnimClip>();
			foreach ( var prompt in creature.Prompts )
			{
				window.SetIntent( UI.EditIntent.New );
				window.EditPrompt.Options.Seconds = 2f;
				window.EditPrompt.Options.Steps = 12;
				window.EditPrompt.Options.Takes = 1;
				window.EditPrompt.Options.Seed = 3;
				var before = session.Workspace.Clips.ToList();
				await window.RunEditPromptAsync( prompt );
				await EngineThread.SwitchToMainThread();
				var clip = session.Workspace.Clips.Except( before ).FirstOrDefault();
				if ( clip is not null ) made.Add( clip );
			}
			foreach ( var clip in made ) DumpMotion( rig, clip, Path.Combine( shots, $"creature_{creature.Name}_{clip.EffectiveSequenceName}.motion.json" ) );
			check( $"{creature.Name}: generates from text", made.Count == creature.Prompts.Length && made.All( c => c.FrameCount > 30 ), string.Join( ", ", made.Select( c => c.Name ) ) );
			if ( made.Count == 0 ) return;
			var errors = made.SelectMany( c => ClipQuality.Analyze( c, rig ) ).Where( q => q.Severity == IssueSeverity.Error ).Select( q => q.Code ).ToList();
			check( $"{creature.Name}: generated animations have no quality errors", errors.Count == 0, string.Join( ",", errors ) );

			session.SelectClip( made[0] );
			window.Viewport.FrameCharacter();
			window.Viewport.SetView( 90f, 8f );
			foreach ( var f in Enumerable.Range( 0, 6 ).Select( k => k * (made[0].FrameCount - 1) / 5 ) )
			{
				session.Playing = false;
				session.Seek( f );
				await EngineThread.DelayOnMain( 350 );
				File.WriteAllBytes( Path.Combine( shots, $"creature_{creature.Name}_preview_{f:00}.png" ), window.Viewport.RenderToPng() );
			}
			var beforeSave = ModelNodes( File.ReadAllText( vmdlPath ) );
			var savedAll = true;
			foreach ( var clip in made )
			{
				var ok = await window.Save.SaveAsync( clip );
				await EngineThread.SwitchToMainThread();
				savedAll &= ok;
			}
			check( $"{creature.Name}: saves compile and play back like the preview", savedAll, "" );
			await session.RefreshModelAsync();
			var text = File.ReadAllText( vmdlPath );
			var names = made.Select( c => c.EffectiveSequenceName ).ToList();
			var appended = names.All( n => session.Model.AnimationNames.Contains( n ) ) && Kv3Sequences( vmdlPath ).Count( n => names.Contains( n ) ) == names.Count;
			var untouched = ModelNodes( text ) == beforeSave;
			check( $"{creature.Name}: animations appended, model nodes untouched", appended && untouched, $"{string.Join( ",", session.Model.AnimationNames.Take( 6 ) )}" );

			// the compiled animation as the engine plays it (sampled back from the model) for review
			var played = await session.ImportSequenceAsync( names[0] );
			await EngineThread.SwitchToMainThread();
			{
				// how do the sampled rotations differ from what we wrote? (local and world, per bone)
				var ours = made[0].EvaluateFrames( rig.Skeleton );
				var theirs = played.Frames;
				var fr = Math.Min( 10, Math.Min( ours.Count, theirs.Count ) - 1 );
				var wo = new Maths.XForm[rig.Skeleton.Count]; var wt = new Maths.XForm[rig.Skeleton.Count];
				Processing.FkUtil.ToWorld( ours[fr], rig.Skeleton, wo );
				Processing.FkUtil.ToWorld( theirs[fr], rig.Skeleton, wt );
				var rows = new List<string>();
				for ( var b = 0; b < rig.Skeleton.Count; b++ )
				{
					var dl = Maths.MathQ.AngleBetween( ours[fr][b].Rot, theirs[fr][b].Rot ) * 180f / MathF.PI;
					var dw = Maths.MathQ.AngleBetween( wo[b].Rot, wt[b].Rot ) * 180f / MathF.PI;
					var dp = (wo[b].Pos - wt[b].Pos).Length();
					var rest = Maths.MathQ.AngleBetween( rig.Skeleton[b].RestLocal.Rot, theirs[fr][b].Rot ) * 180f / MathF.PI;
					rows.Add( $"{rig.Skeleton[b].Name}: local {dl:0.0} world {dw:0.0} pos {dp:0.00} | sampled-vs-rest {rest:0.0}" );
				}
				Set( $"rotdiff_{creature.Name}", rows );
			}
			var turn = VmdlSaveService.RootOffset( rig, made[0].EvaluateFrames( rig.Skeleton ), played.Frames ) is { } off
				? Maths.MathQ.AngleBetween( off, System.Numerics.Quaternion.Identity ) * 180f / MathF.PI : 999f;
			check( $"{creature.Name}: the game plays it facing the way it was made", turn < 3f, $"{turn:0.0}° off; learned compensation {(session.Workspace.RootCompensation is { } rc ? string.Join( ",", rc.Select( v => v.ToString( "0.###" ) ) ) : "none")}" );
			session.SelectClip( played );
			window.Viewport.FrameCharacter();
			window.Viewport.SetView( 90f, 8f ); // side on: gaits read best from the side
			window.Viewport.FollowCharacter = true;
			foreach ( var f in Enumerable.Range( 0, 6 ).Select( k => k * (played.FrameCount - 1) / 5 ) )
			{
				session.Playing = false;
				session.Seek( f );
				await EngineThread.DelayOnMain( 350 );
				File.WriteAllBytes( Path.Combine( shots, $"creature_{creature.Name}_played_{f:00}.png" ), window.Viewport.RenderToPng() );
			}
			// the same frames played by the engine itself, no overrides: what the game shows
			window.Viewport.EngineSequence = names[0];
			foreach ( var f in Enumerable.Range( 0, 6 ).Select( k => k * (played.FrameCount - 1) / 5 ) )
			{
				session.Playing = false;
				session.Seek( f );
				await EngineThread.DelayOnMain( 350 );
				File.WriteAllBytes( Path.Combine( shots, $"creature_{creature.Name}_engine_{f:00}.png" ), window.Viewport.RenderToPng() );
			}
			window.Viewport.EngineSequence = null;
		}
	}

	/// <summary>World positions of every bone on every frame (for offline review of generated motion).</summary>
	static void DumpMotion( MotionRig rig, AnimClip clip, string path )
	{
		try
		{
			var world = new Maths.XForm[rig.Skeleton.Count];
			var frames = new List<float[]>();
			foreach ( var pose in clip.EvaluateFrames( rig.Skeleton ) )
			{
				Processing.FkUtil.ToWorld( pose, rig.Skeleton, world );
				frames.Add( world.SelectMany( x => new[] { x.Pos.X, x.Pos.Y, x.Pos.Z } ).ToArray() );
			}
			var dump = new
			{
				names = rig.Skeleton.Bones.Select( b => b.Name ).ToList(),
				parents = rig.Skeleton.Bones.Select( b => b.ParentIndex ).ToList(),
				fps = clip.Fps, prompt = clip.Generation?.Prompts.FirstOrDefault(), frames,
			};
			File.WriteAllText( path, JsonSerializer.Serialize( dump ) );
		}
		catch ( Exception e ) { Note( $"motion dump failed: {e.Message}" ); }
	}

	/// <summary>The vmdl without its animation list (to prove a save only appends animations).</summary>
	static string ModelNodes( string vmdlText )
	{
		var doc = Kv3.Parse( vmdlText );
		if ( doc.Root is KvObject root && root.GetOrNull( "rootNode" ) is KvObject node && node.GetOrNull( "children" ) is KvArray children )
			children.Items.RemoveAll( c => c is KvObject o && o.GetString( "_class" ) == "AnimationList" );
		return Kv3.Serialize( doc );
	}

	static List<string> UniMateRigProblems( MotionRig rig )
	{
		try { return Inference.UniMate.UniMateRig.Validate( rig ); }
		catch ( Exception e ) { return new List<string> { e.Message }; }
	}

	static Func<string, float[]> _bindScaleOf = _ => null;
	static float[] BindScale( string bone ) => _bindScaleOf( bone );

	/// <summary>Writes the engine skeleton (rest locals) and what the shape analysis made of it, for offline tests.</summary>
	static void DumpSkeleton( MotionRig rig, string path )
	{
		try
		{
			var s = rig.Skeleton;
			var a = rig.Analysis;
			var dump = new
			{
				bones = Enumerable.Range( 0, s.Count ).Select( i => new
				{
					name = s[i].Name, parent = s[i].ParentIndex,
					pos = new[] { s[i].RestLocal.Pos.X, s[i].RestLocal.Pos.Y, s[i].RestLocal.Pos.Z },
					rot = new[] { s[i].RestLocal.Rot.X, s[i].RestLocal.Rot.Y, s[i].RestLocal.Rot.Z, s[i].RestLocal.Rot.W },
					scale = BindScale( s[i].Name ),
				} ).ToList(),
				analysis = new
				{
					a.IsHumanoid,
					limbs = a.Limbs.Select( l => $"{l.Kind} {l.Side}: {string.Join( ",", l.Chain.Select( b => s[b].Name ) )}" ).ToList(),
					tails = a.Tails.Select( t => string.Join( ",", t.Select( b => s[b].Name ) ) ).ToList(),
					spine = string.Join( ",", a.SpineChain.Select( b => s[b].Name ) ),
					bodyRoot = s[a.BodyRoot].Name,
				},
			};
			File.WriteAllText( path, JsonSerializer.Serialize( dump, new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch ( Exception e ) { Note( $"skeleton dump failed: {e.Message}" ); }
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
