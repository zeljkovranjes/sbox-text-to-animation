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

		// ---- 1. a test model in the project: inherits the s&box human, so it has its skeleton, mesh and animations
		var folder = Path.Combine( assets, "t2a_gate" );
		Directory.CreateDirectory( folder );
		var vmdlPath = Path.Combine( folder, "gate_human.vmdl" );
		File.WriteAllText( vmdlPath, VmdlWriter.GenerateStandalone( "models/citizen_human/citizen_human_male.vmdl", Array.Empty<AnimEntry>(), 1f, "pelvis" ) );
		var compile = await VmdlCompiler.RegisterAndCompileAsync( vmdlPath, Array.Empty<string>() );
		Check( "test model compiles", compile.Compiled, compile.Error ?? "" );
		if ( !compile.Compiled ) return false;
		var asset = compile.Asset;

		// ---- 2. open the editor window and the model
		await EngineThread.SwitchToMainThread();
		var window = TextToAnimationWindow.Open();
		await window.OpenModelAsync( asset );
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
		await EngineThread.DelayOnMain( 500 );
		var shots = Path.Combine( Path.GetDirectoryName( _resultPath )!, "gate_shots" );
		Directory.CreateDirectory( shots );
		try
		{
			File.WriteAllBytes( Path.Combine( shots, "viewport_imported.png" ), window.Viewport.RenderToPng() );
			Check( "viewport renders", true );
		}
		catch ( Exception e ) { Check( "viewport renders", false, e.Message ); }

		// ---- 6. generate with UniMate (if installed on this machine)
		GeneratorService.Instance.Refresh();
		AnimClip generated = null;
		if ( GeneratorService.Instance.State == ModelState.Ready )
		{
			var ok = await window.Flow.GenerateAsync( new GenerationRequest
			{
				Mode = GenerationMode.TextToMotion, Prompts = new[] { "walk forward" }, DurationSeconds = 2f, OutputFps = 30f, Seed = 7, Steps = 12,
			}, replace: false, "Gate walk" );
			await EngineThread.SwitchToMainThread();
			generated = session.ActiveClip;
			Check( "UniMate generates", ok && generated?.Origin == ClipOrigin.Generated, generated?.Name ?? "" );
			if ( generated is not null )
			{
				var issues = ClipQuality.Analyze( generated, session.Rig );
				Set( "generatedIssues", issues.Select( q => $"{q.Severity} {q.Code}: {q.Message}" ).ToList() );
				Check( "generated animation has no quality errors", issues.All( q => q.Severity != IssueSeverity.Error ) );
				var path = RootTools.HipsTrajectory( generated.Frames, session.Rig );
				var travel = RootTools.Horizontal( session.Rig, path[^1] - path[0] );
				Set( "generatedTravel", travel.ToString() );
				Check( "generated walk travels forward", System.Numerics.Vector3.Dot( travel, session.Rig.Forward ) > session.Rig.Cm( 30f ), travel.ToString() );
				await EngineThread.DelayOnMain( 300 );
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

		return checks.Values.All( v => v );
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
