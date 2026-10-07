using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Engine;
using TextToAnimation.EditorTools.Session;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.EditorTools.UI;

/// <summary>
/// The live model view: the workspace model posed with the active clip at the playhead, the bone overlay
/// (blue = selected, amber = locked for regeneration), the root trajectory and the floor. Left click picks
/// bones and drags the rotate handle of the selected bone (an undoable key on the current frame); right drag
/// orbits, middle drag pans, the wheel zooms, double click frames the character.
/// </summary>
public sealed class AnimationViewport : SceneRenderingWidget
{
	readonly EditorSession _session;
	CameraComponent _camera;
	SceneModel _sceneModel;
	Model _model;
	int[] _boneMap = Array.Empty<int>();
	Vector3[] _bindScale = Array.Empty<Vector3>();
	XForm[] _world = Array.Empty<XForm>();
	XForm[] _pose = Array.Empty<XForm>();
	bool _dragging;

	float _yaw = 35f, _pitch = 12f, _distance = 140f;
	Vector3 _target = new( 0, 0, 36 );
	Vector3 _pan;
	Vector2 _lastMouse;

	public bool ShowSkeleton { get; set; } = true;
	public bool ShowTrajectory { get; set; } = true;
	public bool ShowGround { get; set; } = true;
	public bool ShowModel { get; set; } = true;
	public bool FollowCharacter { get; set; } = true;

	/// <summary>
	/// When set, the engine plays this compiled sequence of the model itself (no bone overrides) at the session
	/// playhead - the ground truth of what a saved animation looks like in game.
	/// </summary>
	public string EngineSequence { get; set; }

	/// <summary>
	/// Bones the model's own constraints drive at runtime (from its vmdl and prefabs). For a clip whose compiled
	/// sequence is current, the view takes these from the engine itself, so the preview shows the game's result.
	/// </summary>
	public IReadOnlySet<string> ConstraintDriven { get; set; } = new HashSet<string>();

	/// <summary>The compiled sequence holding the active clip unchanged (set by the window), or null.</summary>
	public string SavedSequence { get; set; }

	/// <summary>
	/// The preview's constraint-driven bones against the engine playing <paramref name="sequence"/> at the playhead,
	/// both relative to <paramref name="anchor"/> (the game may extract root motion): the largest position difference.
	/// </summary>
	public float PreviewVsGameOnDrivenBones( string sequence, string anchor )
	{
		var rig = _session.Rig;
		if ( rig is null || !_sceneModel.IsValid() || _model is null ) return float.NaN;
		var s = rig.Skeleton;
		var a = s.IndexOf( anchor );
		if ( a < 0 || _boneMap[a] < 0 || _session.ActiveFrames is not { Count: > 0 } frames ) return float.NaN;
		// the pose at the current playhead (the paint tick may not have run since the last seek)
		SamplePose( frames, _session.Playhead, _pose );
		FkUtil.ToWorld( _pose, s, _world );
		ApplyPose();
		var preview = Enumerable.Range( 0, s.Count ).Select( b => _boneMap[b] >= 0 ? _sceneModel.GetBoneWorldTransform( _boneMap[b] ) : default ).ToArray();
		var saved = EngineSequence;
		EngineSequence = sequence;
		ApplyPose();
		var game = Enumerable.Range( 0, s.Count ).Select( b => _boneMap[b] >= 0 ? _sceneModel.GetBoneWorldTransform( _boneMap[b] ) : default ).ToArray();
		EngineSequence = saved;
		ApplyPose();
		var worst = 0f; var worstOther = 0f; string worstBone = "", worstOtherBone = "";
		for ( var b = 0; b < s.Count; b++ )
		{
			if ( _boneMap[b] < 0 ) continue;
			var d = (preview[a].ToLocal( preview[b] ).Position - game[a].ToLocal( game[b] ).Position).Length;
			if ( ConstraintDriven.Contains( s[b].Name ) ) { if ( d > worst ) { worst = d; worstBone = s[b].Name; } }
			else if ( d > worstOther ) { worstOther = d; worstOtherBone = s[b].Name; }
		}
		LastDrivenReport = $"worst {worstBone}; other bones max {worstOther:0.000} in ({worstOtherBone})";
		return worst;
	}

	/// <summary>Which bones the last <see cref="PreviewVsGameOnDrivenBones"/> found furthest apart.</summary>
	public string LastDrivenReport { get; private set; } = "";

	/// <summary>The sequence the active clip was saved as, when it hasn't changed since (its compiled copy is current).</summary>
	string CurrentSavedSequence()
	{
		var clip = _session.ActiveClip;
		return clip?.SavedUtc is not null && ClipListPanel.SavedRevisions.TryGetValue( clip.Id, out var revision ) && revision == clip.Revision
			? clip.EffectiveSequenceName : null;
	}

	/// <summary>True when the last pose took the constraint-driven bones from the engine.</summary>
	public bool UsedEngineConstraints { get; private set; }

	public AnimationViewport( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		MinimumSize = new Vector2( 320, 260 );
		MouseTracking = true;
		FocusMode = FocusMode.Click;

		Scene = Scene.CreateEditorScene();
		using ( Scene.Push() )
		{
			_camera = new GameObject( true, "camera" ).GetOrAddComponent<CameraComponent>( false );
			_camera.BackgroundColor = Theme.ControlBackground;
			_camera.ZNear = 1f;
			_camera.ZFar = 20000f;
			_camera.FieldOfView = 45f;
			_camera.Enabled = true;
		}
		Camera = _camera;
		var world = Scene.SceneWorld;
		new ScenePointLight( world, new Vector3( 120, 100, 160 ), 900, Color.White * 3.0f ).ShadowsEnabled = false;
		new ScenePointLight( world, new Vector3( -140, -100, 110 ), 900, Color.White * 1.6f ).ShadowsEnabled = false;
		new ScenePointLight( world, new Vector3( 0, 160, 60 ), 700, Color.White * 0.9f ).ShadowsEnabled = false;
	}

	/// <summary>Loads the workspace model into the view.</summary>
	/// <summary>
	/// Shows <paramref name="model"/>. Always rebuilds the scene model and the bone map: a save recompiles the
	/// model in place (the same Model object) and the compiler may reorder its bones, so a cached map would pose
	/// the wrong bones. The camera is only reframed for a different model.
	/// </summary>
	public void SetModel( Model model )
	{
		var reframe = model?.ResourcePath != _model?.ResourcePath;
		_model = model;
		_sceneModel?.Delete();
		_sceneModel = null;
		DeleteEngineModel();
		if ( model is null || model.IsError || _session.Rig is null ) return;
		DeleteEngineModel();
		_sceneModel = new SceneModel( Scene.SceneWorld, model, Transform.Zero ) { UseAnimGraph = false };
		var skeleton = _session.Rig.Skeleton;
		_boneMap = skeleton.Bones.Select( b => model.Bones.GetBone( b.Name )?.Index ?? -1 ).ToArray();
		_bindScale = skeleton.Bones.Select( b => model.Bones.GetBone( b.Name )?.LocalTransform.Scale ?? Vector3.One ).ToArray();
		_world = new XForm[skeleton.Count];
		_pose = new XForm[skeleton.Count];
		if ( reframe ) FrameCharacter();
	}

	/// <summary>Frames the whole character (double click does the same).</summary>
	public void FrameCharacter()
	{
		var rig = _session.Rig;
		if ( _model is not null && !_model.IsError && _model.Bounds.Size.Length > 1f )
		{
			// any creature: frame its whole body (a fox is long and low, a person tall and narrow)
			var size = _model.Bounds.Size;
			var extent = MathF.Max( size.z, MathF.Max( size.x, size.y ) * 0.75f );
			_distance = MathF.Max( extent * 1.9f, 30f );
			_target = new Vector3( 0, 0, size.z * 0.5f );
		}
		else
		{
			var height = rig is null ? 72f : MathF.Max( rig.HipHeight * 2.1f, 20f );
			_distance = height * 1.9f;
			_target = new Vector3( 0, 0, height * 0.5f );
		}
		_pan = Vector3.Zero;
		_followCenter = null;
	}

	public void SetView( float yaw, float pitch ) { _yaw = yaw; _pitch = pitch; }

	protected override void PreFrame()
	{
		var started = FrameProbe.Now;
		try { ViewFrame(); }
		finally { FrameProbe.Add( "viewport", FrameProbe.Now - started ); }
	}

	void ViewFrame()
	{
		Scene.EditorTick( RealTime.Now, RealTime.Delta );
		GizmoInstance.Input.IsHovered = IsActiveWindow && IsUnderMouse;
		UpdateGizmoInputs( GizmoInstance.Input.IsHovered );

		var frames = _session.ActiveFrames;
		var rig = _session.Rig;
		if ( rig is null || frames is null || frames.Count == 0 )
		{
			_gizmoHovered = false;
			UpdateCamera( _target );
			if ( ShowGround ) DrawGround( Vector3.Zero, 0f, 40f );
			return;
		}

		SamplePose( frames, _session.Playhead, _pose );
		FkUtil.ToWorld( _pose, rig.Skeleton, _world );
		ApplyPose();

		var hips = rig.HipsIndex >= 0 ? rig.HipsIndex : rig.RootIndex;
		var hipsPos = ToVec( _world[hips].Pos );
		var groundZ = GroundHeight( rig );
		UpdateCamera( FollowCharacter ? new Vector3( hipsPos.x, hipsPos.y, _target.z ) : _target );

		if ( ShowGround ) DrawGround( hipsPos, groundZ, MathF.Max( rig.HipHeight * 2f, 30f ) );
		if ( ShowTrajectory ) DrawTrajectory( frames, rig, groundZ );
		if ( ShowSkeleton || !ShowModel ) DrawSkeleton( rig );
		DrawBonePicking( rig );
		DrawPoseGizmo( rig );
		_gizmoHovered = Gizmo.HasHovered;
	}

	// whether a bone dot or pose handle is under the mouse, as of the last drawn frame: Gizmo.HasHovered only works
	// while the gizmos are being drawn, not in mouse events
	bool _gizmoHovered;

	static Vector3 ToVec( System.Numerics.Vector3 v ) => new( v.X, v.Y, v.Z );

	static void SamplePose( List<XForm[]> frames, float playhead, XForm[] into )
	{
		var a = Math.Clamp( (int)MathF.Floor( playhead ), 0, frames.Count - 1 );
		var b = Math.Min( a + 1, frames.Count - 1 );
		var t = playhead - a;
		var fa = frames[a]; var fb = frames[b];
		var n = Math.Min( into.Length, fa.Length );
		for ( var i = 0; i < n; i++ )
			into[i] = t <= 1e-4f ? fa[i] : new XForm( System.Numerics.Vector3.Lerp( fa[i].Pos, fb[i].Pos, t ),
				System.Numerics.Quaternion.Normalize( System.Numerics.Quaternion.Slerp( fa[i].Rot, fb[i].Rot, t ) ) );
	}

	SceneWorld _engineWorld;
	SceneModel _engineModel;
	string _engineModelSequence;

	/// <summary>
	/// The engine's own pose of compiled <paramref name="sequence"/> at <paramref name="seconds"/> (its animation and
	/// the model's constraints), on a hidden model that only ever plays that sequence: switching sequences on a model
	/// crossfades from the previous one, which an editor (that never advances time) would never finish.
	/// </summary>
	SceneModel EngineModelAt( string sequence, float seconds )
	{
		// recreated until it really plays the sequence (one made while a save was recompiling the model has none)
		if ( !_engineModel.IsValid() || _engineModelSequence != sequence || _engineModel.CurrentSequence.Name != sequence )
		{
			DeleteEngineModel();
			_engineWorld = new SceneWorld();
			// the compiled model as it is now (a save recompiles it; the view's instance can lag behind)
			var compiled = Model.Load( _model.ResourcePath );
			_engineModel = new SceneModel( _engineWorld, compiled is { IsError: false } ? compiled : _model, Transform.Zero ) { UseAnimGraph = false };
			_engineModel.CurrentSequence.Name = sequence;
			_engineModel.Update( 0f );
			_engineModelSequence = sequence;
		}
		_engineModel.CurrentSequence.Time = seconds;
		_engineModel.Update( 0f );
		return _engineModel;
	}

	/// <summary>
	/// Every bone's world transform on an engine-played model, chained from the parent-space bones that
	/// <see cref="SceneModel.Update"/> evaluates (the world transforms it reports refresh only when the scene renders).
	/// </summary>
	Transform[] EngineWorld( SceneModel engine )
	{
		var count = _model.BoneCount;
		var world = new Transform[count];
		var state = new byte[count]; // 0 todo, 1 in progress (a parent loop cuts there), 2 done
		Transform Of( int b )
		{
			if ( state[b] == 2 ) return world[b];
			var local = engine.GetParentSpaceBone( b );
			var parent = _model.GetBoneParent( b );
			state[b] = 1;
			world[b] = parent < 0 || parent >= count || state[parent] == 1 ? local : Of( parent ).ToWorld( local );
			state[b] = 2;
			return world[b];
		}
		for ( var b = 0; b < count; b++ ) Of( b );
		return world;
	}

	void DeleteEngineModel()
	{
		if ( _engineModel.IsValid() ) _engineModel.Delete();
		_engineWorld?.Delete();
		_engineModel = null;
		_engineWorld = null;
		_engineModelSequence = null;
	}

	void ApplyPose()
	{
		if ( !_sceneModel.IsValid() ) return;
		_sceneModel.RenderingEnabled = ShowModel;
		if ( !string.IsNullOrEmpty( EngineSequence ) )
		{
			var engine = EngineWorld( EngineModelAt( EngineSequence, _session.Playhead / (_session.ActiveClip?.Fps ?? 30f) ) );
			for ( var b = 0; b < engine.Length; b++ ) _sceneModel.SetBoneOverride( b, engine[b] );
			_sceneModel.Update( 0f );
			return;
		}
		var final = new Transform[_boneMap.Length];
		for ( var i = 0; i < _boneMap.Length; i++ ) final[i] = ModelBridge.ToTransform( _world[i], _bindScale[i] );
		UsedEngineConstraints = false;
		var saved = SavedSequence ?? CurrentSavedSequence();
		if ( ConstraintDriven.Count > 0 && !string.IsNullOrEmpty( saved ) && _model.AnimationNames.Contains( saved ) )
		{
			// the engine evaluates the model's constraints only while playing a compiled sequence: play the saved one at
			// this time, take each driven bone relative to its parent, and place it on this pose (root motion kept)
			var engine = EngineWorld( EngineModelAt( saved, _session.Playhead / (_session.ActiveClip?.Fps ?? 30f) ) );
			var skeleton = _session.Rig.Skeleton;
			var moved = new bool[skeleton.Count];
			for ( var i = 0; i < skeleton.Count; i++ ) // parents come before children
			{
				var p = skeleton[i].ParentIndex;
				if ( _boneMap[i] < 0 || p < 0 || _boneMap[p] < 0 ) continue;
				if ( ConstraintDriven.Contains( skeleton[i].Name ) )
				{
					var local = engine[_boneMap[p]].ToLocal( engine[_boneMap[i]] );
					final[i] = final[p].ToWorld( local );
					moved[i] = UsedEngineConstraints = true;
				}
				else if ( moved[p] )
				{
					// below a driven bone: keep this pose's local transform under the engine-placed parent
					final[i] = final[p].ToWorld( ModelBridge.ToTransform( _world[p], _bindScale[p] ).ToLocal( ModelBridge.ToTransform( _world[i], _bindScale[i] ) ) );
					moved[i] = true;
				}
			}
		}
		for ( var i = 0; i < _boneMap.Length; i++ )
		{
			if ( _boneMap[i] < 0 ) continue;
			_sceneModel.SetBoneOverride( _boneMap[i], final[i] );
		}
		_sceneModel.Update( 0f ); // flush overrides now (otherwise the pose lags a frame)
	}

	/// <summary>
	/// The engine's own pose while playing compiled <paramref name="sequence"/> (animation + the model's constraints):
	/// world transforms of every bone and of the named attachments, at the given frames. Ground truth for the
	/// constraint evaluator.
	/// </summary>
	public object EnginePoses( string sequence, IEnumerable<int> frames, float fps, IReadOnlyList<string> attachments )
	{
		if ( _model is null || !_sceneModel.IsValid() ) return null;
		var saved = EngineSequence;
		EngineSequence = null;
		var result = new List<object>();
		static float[] P( Transform t ) => new[] { t.Position.x, t.Position.y, t.Position.z, t.Rotation.x, t.Rotation.y, t.Rotation.z, t.Rotation.w };
		foreach ( var f in frames )
		{
			var engineModel = EngineModelAt( sequence, f / fps );
			var engine = EngineWorld( engineModel );
			// the pose the sequence stores (before constraints), in the same model space as the engine's bones
			var stored = new Dictionary<string, float[]>();
			if ( _session.ActiveFrames is { Count: > 0 } clipFrames && _session.Rig is { } rig )
			{
				var pose = new XForm[rig.Skeleton.Count]; var world = new XForm[rig.Skeleton.Count];
				SamplePose( clipFrames, f, pose );
				FkUtil.ToWorld( pose, rig.Skeleton, world );
				for ( var i = 0; i < rig.Skeleton.Count; i++ )
					if ( _boneMap[i] >= 0 ) stored[rig.Skeleton[i].Name] = P( ModelBridge.ToTransform( world[i], _bindScale[i] ) );
			}
			result.Add( new
			{
				frame = f,
				stored,
				bones = Enumerable.Range( 0, _model.BoneCount ).ToDictionary( b => _model.GetBoneName( b ), b => P( engine[b] ) ),
				attachments = attachments.Select( a => (a, t: (object)engineModel.GetAttachment( a, true )) ).Where( x => x.t is Transform ).ToDictionary( x => x.a, x => P( (Transform)x.t ) ),
			} );
		}
		EngineSequence = saved;
		return result;
	}

	/// <summary>The floor the model stands on: z = 0 for normal models, or its lowest rest point when that is below.</summary>
	static float GroundHeight( MotionRig rig )
	{
		var lowest = float.MaxValue;
		for ( var i = 0; i < rig.Skeleton.Count; i++ ) lowest = MathF.Min( lowest, rig.Skeleton.RestWorld[i].Pos.Z );
		return lowest < 0f ? lowest : 0f;
	}

	// ---------------------------------------------------------------- camera

	Vector3? _followCenter;

	void UpdateCamera( Vector3 goal )
	{
		_followCenter = _followCenter is { } c ? Vector3.Lerp( c, goal, 1f - MathF.Exp( -RealTime.Delta / 0.3f ) ) : goal;
		var center = _followCenter.Value + _pan;
		var yaw = MathX.DegreeToRadian( _yaw );
		var pitch = MathX.DegreeToRadian( _pitch );
		var dir = new Vector3( MathF.Cos( yaw ) * MathF.Cos( pitch ), MathF.Sin( yaw ) * MathF.Cos( pitch ), MathF.Sin( pitch ) ).Normal;
		_camera.WorldPosition = center + dir * _distance;
		_camera.WorldRotation = Rotation.LookAt( -dir, Vector3.Up );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		_lastMouse = e.LocalPosition;
		// left-drag on empty space looks around (on a bone or a gizmo handle it picks or poses instead)
		_orbiting = e.LeftMouseButton && !_gizmoHovered;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( e.LeftMouseButton ) _orbiting = false;
	}

	bool _orbiting;

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		var delta = e.LocalPosition - _lastMouse;
		_lastMouse = e.LocalPosition;
		if ( (e.ButtonState & MouseButtons.Right) != 0 || (_orbiting && (e.ButtonState & MouseButtons.Left) != 0) )
		{
			_yaw -= delta.x * 0.4f;
			_pitch = Math.Clamp( _pitch + delta.y * 0.3f, -15f, 85f );
		}
		else if ( (e.ButtonState & MouseButtons.Middle) != 0 )
		{
			var scale = _distance * 0.0016f;
			_pan += (_camera.WorldRotation.Left * delta.x + _camera.WorldRotation.Up * delta.y) * scale;
		}
	}

	protected override void OnWheel( WheelEvent e )
	{
		base.OnWheel( e );
		_distance = Math.Clamp( _distance * (e.Delta > 0 ? 0.9f : 1.1f), 10f, 4000f );
		e.Accept();
	}

	protected override void OnDoubleClick( MouseEvent e )
	{
		base.OnDoubleClick( e );
		if ( e.LeftMouseButton && !_gizmoHovered ) FrameCharacter();
	}

	// ---------------------------------------------------------------- overlays

	void DrawGround( Vector3 center, float z, float size )
	{
		using var scope = Gizmo.Scope( "t2a-ground" );
		Gizmo.Transform = Transform.Zero;
		Gizmo.Draw.IgnoreDepth = false;
		Gizmo.Draw.LineThickness = 1f;
		var step = MathF.Max( size / 6f, 2f );
		const int cells = 12;
		var block = step * 4f;
		var ox = MathF.Round( center.x / block ) * block;
		var oy = MathF.Round( center.y / block ) * block;
		var extent = cells * step;
		for ( var i = -cells; i <= cells; i++ )
		{
			var fade = 1f - MathF.Abs( i ) / (cells + 1f);
			Gizmo.Draw.Color = Color.White.WithAlpha( (i == 0 ? .14f : .06f) * fade + .012f );
			var o = i * step;
			Gizmo.Draw.Line( new Vector3( ox + o, oy - extent, z ), new Vector3( ox + o, oy + extent, z ) );
			Gizmo.Draw.Line( new Vector3( ox - extent, oy + o, z ), new Vector3( ox + extent, oy + o, z ) );
		}
	}

	void DrawTrajectory( List<XForm[]> frames, MotionRig rig, float z )
	{
		var path = RootTools.HipsTrajectory( frames, rig );
		using var scope = Gizmo.Scope( "t2a-trajectory" );
		Gizmo.Transform = Transform.Zero;
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		var accent = TaStyle.Accent;
		var current = _session.CurrentFrame;
		for ( var f = 1; f < path.Length; f++ )
		{
			var past = f <= current;
			Gizmo.Draw.Color = accent.WithAlpha( past ? .9f : .35f );
			Gizmo.Draw.Line( new Vector3( path[f - 1].X, path[f - 1].Y, z + .2f ), new Vector3( path[f].X, path[f].Y, z + .2f ) );
		}
		// heading ticks every half second
		var every = Math.Max( 1, (int)MathF.Round( (_session.ActiveClip?.Fps ?? 30f) * 0.5f ) );
		Gizmo.Draw.LineThickness = 1.5f;
		for ( var f = 0; f < frames.Count; f += every )
		{
			var heading = RootTools.RootHeading( frames, rig, f );
			var dir = System.Numerics.Vector3.Transform( rig.Forward, RootTools.Yaw( rig, heading ) );
			var p = new Vector3( path[f].X, path[f].Y, z + .2f );
			Gizmo.Draw.Color = Color.White.WithAlpha( .35f );
			Gizmo.Draw.Line( p, p + new Vector3( dir.X, dir.Y, 0 ) * rig.Cm( 12f ) );
		}
		// pinned frames (in-between constraints)
		if ( _session.ActiveClip is { } clip )
		{
			Gizmo.Draw.Color = Theme.Yellow.WithAlpha( .9f );
			foreach ( var pin in clip.PinnedFrames.Where( p => p < path.Length ) )
				Gizmo.Draw.LineCircle( new Vector3( path[pin].X, path[pin].Y, z + .2f ), Vector3.Up, rig.Cm( 6f ), 0, 360, 4 );
		}
		var at = path[Math.Clamp( current, 0, path.Length - 1 )];
		Gizmo.Draw.Color = accent;
		Gizmo.Draw.LineCircle( new Vector3( at.X, at.Y, z + .2f ), Vector3.Up, rig.Cm( 10f ), 0, 360, 24 );
	}

	void DrawSkeleton( MotionRig rig )
	{
		var clip = _session.ActiveClip;
		using var scope = Gizmo.Scope( "t2a-skeleton" );
		Gizmo.Transform = Transform.Zero;
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2f;
		for ( var i = 0; i < rig.Skeleton.Count; i++ )
		{
			var parent = rig.Skeleton[i].ParentIndex;
			if ( parent < 0 || !rig.IsMotionBone( i ) ) continue;
			var locked = clip?.LockedBones.Contains( rig.Skeleton[i].Name ) == true;
			var selected = _session.SelectedBones.Contains( i );
			Gizmo.Draw.Color = selected ? TaStyle.AccentLight : locked ? Theme.Yellow.WithAlpha( .95f ) : TaStyle.Accent.WithAlpha( .55f );
			Gizmo.Draw.Line( ToVec( _world[parent].Pos ), ToVec( _world[i].Pos ) );
		}
	}

	void DrawBonePicking( MotionRig rig )
	{
		var clip = _session.ActiveClip;
		var radius = MathF.Max( rig.Cm( 1.6f ), 0.45f );
		var clicked = false;
		for ( var i = 0; i < rig.Skeleton.Count; i++ )
		{
			if ( !rig.IsMotionBone( i ) ) continue;
			using ( Gizmo.Scope( $"t2a-bone/{i}", new Transform( ToVec( _world[i].Pos ) ) ) )
			{
				Gizmo.Hitbox.DepthBias = 0.01f;
				Gizmo.Hitbox.Sphere( new Sphere( 0, radius ) );
				Gizmo.Hitbox.TrySetHovered( 0f );
				var selected = _session.SelectedBones.Contains( i );
				var locked = clip?.LockedBones.Contains( rig.Skeleton[i].Name ) == true;
				if ( ShowSkeleton || selected || Gizmo.IsHovered )
				{
					Gizmo.Draw.IgnoreDepth = true;
					Gizmo.Draw.Color = selected ? TaStyle.AccentLight : Gizmo.IsHovered ? Color.White : locked ? Theme.Yellow : TaStyle.Accent.WithAlpha( .7f );
					Gizmo.Draw.LineSphere( new Sphere( 0, radius ), 6 );
				}
				if ( Gizmo.IsHovered && Gizmo.WasLeftMousePressed && !clicked )
				{
					clicked = true;
					var additive = global::Editor.Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl ) || global::Editor.Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift );
					_session.SelectBone( i, additive );
				}
			}
		}
		// click on empty space clears the selection (but not while a handle is used)
		if ( !clicked && Gizmo.WasLeftMousePressed && !Gizmo.HasHovered && !Gizmo.Pressed.Any && IsUnderMouse )
			_session.SelectBone( null );
	}

	void DrawPoseGizmo( MotionRig rig )
	{
		if ( _session.PrimaryBone is not int bone || _session.ActiveClip is not { } clip || _session.Busy )
		{
			EndDrag();
			return;
		}
		var name = rig.Skeleton[bone].Name;
		var parent = rig.Skeleton[bone].ParentIndex;
		var world = _world[bone];
		using ( Gizmo.Scope( $"t2a-pose/{bone}", new Transform( ToVec( world.Pos ), new Rotation( world.Rot.X, world.Rot.Y, world.Rot.Z, world.Rot.W ) ) ) )
		{
			Gizmo.Hitbox.DepthBias = 0.01f;
			var moved = false;
			XForm newWorld = world;
			if ( Gizmo.Control.Rotate( "rot", Rotation.Identity, out var delta ) )
			{
				var d = new System.Numerics.Quaternion( delta.x, delta.y, delta.z, delta.w );
				newWorld = new XForm( world.Pos, MathQ.Normalize( world.Rot * d ) );
				moved = true;
			}
			// the motion root and the hips can also be moved
			if ( (bone == rig.RootIndex || bone == rig.HipsIndex) && Gizmo.Control.Position( "pos", Vector3.Zero, out var move ) )
			{
				var worldMove = new Rotation( world.Rot.X, world.Rot.Y, world.Rot.Z, world.Rot.W ) * move;
				newWorld = new XForm( newWorld.Pos + new System.Numerics.Vector3( worldMove.x, worldMove.y, worldMove.z ), newWorld.Rot );
				moved = true;
			}
			if ( moved )
			{
				if ( !_dragging )
				{
					_dragging = true;
					_session.BeginInteractiveEdit( $"Pose {name}" );
				}
				var parentWorld = parent < 0 ? XForm.Identity : _world[parent];
				var targetLocal = XForm.ToLocal( parentWorld, newWorld );
				_session.SetPoseKey( bone, _session.CurrentFrame, targetLocal );
			}
			if ( _dragging && !Gizmo.Pressed.Any ) EndDrag();
		}
	}

	void EndDrag()
	{
		if ( !_dragging ) return;
		_dragging = false;
		_session.EndInteractiveEdit();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		_sceneModel?.Delete();
		DeleteEngineModel();
		Scene?.Destroy();
		Scene = null;
	}

	/// <summary>Renders the view to PNG bytes (used by the test gate for visual checks).</summary>
	public byte[] RenderToPng( int width = 640, int height = 480 )
	{
		var bitmap = new Bitmap( width, height );
		_camera.RenderToBitmap( bitmap );
		return bitmap.ToPng();
	}
}
