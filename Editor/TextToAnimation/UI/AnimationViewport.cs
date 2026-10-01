using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Session;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Editor.UI;

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
	public void SetModel( Model model )
	{
		if ( model == _model && _sceneModel.IsValid() ) return;
		_model = model;
		_sceneModel?.Delete();
		_sceneModel = null;
		if ( model is null || model.IsError || _session.Rig is null ) return;
		_sceneModel = new SceneModel( Scene.SceneWorld, model, Transform.Zero ) { UseAnimGraph = false };
		var skeleton = _session.Rig.Skeleton;
		_boneMap = skeleton.Bones.Select( b => model.Bones.GetBone( b.Name )?.Index ?? -1 ).ToArray();
		_bindScale = skeleton.Bones.Select( b => model.Bones.GetBone( b.Name )?.LocalTransform.Scale ?? Vector3.One ).ToArray();
		_world = new XForm[skeleton.Count];
		_pose = new XForm[skeleton.Count];
		FrameCharacter();
	}

	/// <summary>Frames the whole character (double click does the same).</summary>
	public void FrameCharacter()
	{
		var rig = _session.Rig;
		var height = rig is null ? 72f : MathF.Max( rig.HipHeight * 2.1f, 20f );
		_distance = height * 1.9f;
		_target = new Vector3( 0, 0, height * 0.5f );
		_pan = Vector3.Zero;
		_followCenter = null;
	}

	public void SetView( float yaw, float pitch ) { _yaw = yaw; _pitch = pitch; }

	protected override void PreFrame()
	{
		Scene.EditorTick( RealTime.Now, RealTime.Delta );
		GizmoInstance.Input.IsHovered = IsActiveWindow && IsUnderMouse;
		UpdateGizmoInputs( GizmoInstance.Input.IsHovered );

		var frames = _session.ActiveFrames;
		var rig = _session.Rig;
		if ( rig is null || frames is null || frames.Count == 0 )
		{
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
	}

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

	void ApplyPose()
	{
		if ( !_sceneModel.IsValid() ) return;
		_sceneModel.RenderingEnabled = ShowModel;
		for ( var i = 0; i < _boneMap.Length; i++ )
		{
			if ( _boneMap[i] < 0 ) continue;
			_sceneModel.SetBoneOverride( _boneMap[i], ModelBridge.ToTransform( _world[i], _bindScale[i] ) );
		}
		_sceneModel.Update( 0f ); // flush overrides now (otherwise the pose lags a frame)
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
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		var delta = e.LocalPosition - _lastMouse;
		_lastMouse = e.LocalPosition;
		if ( (e.ButtonState & MouseButtons.Right) != 0 )
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
		if ( e.LeftMouseButton && !Gizmo.HasHovered ) FrameCharacter();
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
