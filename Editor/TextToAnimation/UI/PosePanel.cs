using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Session;
using TextToAnimation.Maths;
using TextToAnimation.Processing;

namespace TextToAnimation.Editor.UI;

using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

/// <summary>
/// Right-panel "Pose" tab: the bone hierarchy (click to select, Ctrl/Shift to add), rotation sliders for the
/// selected bone (a key on the current frame, blended into the motion around it), keyframe tools, copy/paste
/// pose, and the bone locks used by text-guided editing.
/// </summary>
public sealed class PosePanel : Widget
{
	readonly EditorSession _session;
	readonly LineEdit _filter;
	readonly ScrollArea _treeScroll;
	readonly Widget _tree;
	readonly Label _boneTitle;
	readonly FloatSlider[] _angles = new FloatSlider[3];
	readonly Label[] _angleLabels = new Label[3];
	readonly FloatSlider _falloff;
	readonly Label _falloffLabel;
	readonly Label _keyInfo;
	Dictionary<string, XForm> _clipboard;
	bool _refreshing;
	bool _interactive;
	DateTime _lastSliderEdit;

	public PosePanel( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		Layout = Layout.Column();
		Layout.Spacing = 8;

		// ---- bones
		var bonesCard = Layout.Add( new TaFold( this, "account_tree", "Bones", open: true, key: "pose.bonesCard" ), 1 );
		_filter = bonesCard.Content.Add( TaStyle.Framed( new LineEdit( bonesCard ) { PlaceholderText = "Filter bones…" } ) );
		_filter.TextEdited += _ => RebuildTree();
		_treeScroll = bonesCard.Content.Add( new ScrollArea( bonesCard ), 1 );
		_treeScroll.MinimumHeight = 160;
		_treeScroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		_treeScroll.SetStyles( "background-color: transparent;" );
		_tree = new Widget( _treeScroll ) { Layout = Layout.Column() };
		_tree.Layout.Spacing = 1;
		_tree.Layout.Margin = new Sandbox.UI.Margin( 0, 0, 8, 0 );
		_treeScroll.Canvas = _tree;

		// ---- selected bone
		var poseCard = Layout.Add( new TaFold( this, "3d_rotation", "Pose", open: true, key: "pose.poseCard" ) );
		_boneTitle = poseCard.Content.Add( TaStyle.Muted( new Label( "", poseCard ) { WordWrap = true }, small: true ) );
		string[] axes = { "Bend (X)", "Twist (Y)", "Side (Z)" };
		for ( var i = 0; i < 3; i++ )
		{
			var row = TaStyle.FieldRow( poseCard, poseCard.Content, axes[i], 70f );
			var axis = i;
			_angles[i] = row.Add( new FloatSlider( poseCard ) { Minimum = -180f, Maximum = 180f, Value = 0f }, 1 );
			_angleLabels[i] = row.Add( TaStyle.Muted( new Label( "0°", poseCard ) { FixedWidth = 40 } ) );
			_angles[i].OnValueEdited = () => SliderEdited();
		}
		var keyRow = poseCard.Content.AddRow();
		keyRow.Spacing = 4;
		keyRow.Add( TaStyle.Icon( poseCard, "skip_previous", () => JumpKey( -1 ), "Previous key", 26 ) );
		keyRow.Add( new TaButton( poseCard, "Key", "key", AddKey, "Key the selected bones on this frame" ) );
		keyRow.Add( new TaButton( poseCard, "Delete key", "key_off", DeleteKey, "Remove the selected bones' keys on this frame" ) );
		keyRow.Add( TaStyle.Icon( poseCard, "skip_next", () => JumpKey( 1 ), "Next key", 26 ) );
		var copyRow = poseCard.Content.AddRow();
		copyRow.Spacing = 4;
		copyRow.Add( new TaButton( poseCard, "Copy pose", "content_copy", CopyPose, "Copy the selected bones' pose on this frame (all bones when nothing is selected)" ) );
		copyRow.Add( new TaButton( poseCard, "Paste pose", "content_paste", PastePose, "Key the copied pose on this frame" ) );
		copyRow.Add( new TaButton( poseCard, "Reset", "restart_alt", ResetPose, "Remove the selected bones' edits on every frame" ) );
		var falloffRow = TaStyle.FieldRow( poseCard, poseCard.Content, "Blend", 70f, "How many frames a key fades in and out over" );
		_falloff = falloffRow.Add( new FloatSlider( poseCard ) { Minimum = 1, Maximum = 60, Value = 8 }, 1 );
		_falloffLabel = falloffRow.Add( TaStyle.Muted( new Label( "8 fr", poseCard ) { FixedWidth = 40 } ) );
		_falloff.OnValueEdited = () =>
		{
			_falloffLabel.Text = $"{(int)_falloff.Value} fr";
			if ( !_refreshing && _session.ActiveClip is { } clip && clip.Keys.FalloffFrames != (int)_falloff.Value )
				_session.Edit( "Key blend", c => c.Keys.FalloffFrames = (int)_falloff.Value );
		};
		_keyInfo = poseCard.Content.Add( TaStyle.Muted( new Label( "", poseCard ) { WordWrap = true }, small: true ) );

		// ---- locks
		var lockCard = Layout.Add( new TaFold( this, "lock", "Lock for regeneration", open: true, key: "pose.lockCard" ) );
		lockCard.Content.Add( TaStyle.Muted( new Label( "Locked bones (amber) keep their motion when you use Generate → Edit.", lockCard ) { WordWrap = true }, small: true ) );
		var lockRow = lockCard.Content.AddRow();
		lockRow.Spacing = 4;
		lockRow.Add( new TaButton( lockCard, "Lock selected", "lock", () => BoneLocks.LockSelected( _session ) ) );
		lockRow.Add( new TaButton( lockCard, "Lock hierarchy", "account_tree", () => BoneLocks.LockHierarchy( _session ) ) );
		var lockRow2 = lockCard.Content.AddRow();
		lockRow2.Spacing = 4;
		lockRow2.Add( new TaButton( lockCard, "Lock all except selected", "flip", () => BoneLocks.LockAllExcept( _session ) ) );
		lockRow2.Add( new TaButton( lockCard, "Unlock", "lock_open", () => BoneLocks.Unlock( _session ), "Unlock the selection (all bones when nothing is selected)" ) );

		_session.Changed += c =>
		{
			if ( (c & (SessionChange.Model | SessionChange.ActiveClip)) != 0 ) RebuildTree();
			if ( (c & (SessionChange.Selection | SessionChange.ClipData | SessionChange.Playhead | SessionChange.ActiveClip | SessionChange.Busy)) != 0 ) Refresh();
			if ( (c & (SessionChange.Selection | SessionChange.ClipData)) != 0 ) foreach ( var row in _tree.Children.OfType<BoneRow>() ) row.Update();
		};
		RebuildTree();
		Refresh();
	}

	// ------------------------------------------------------------------ tree

	void RebuildTree()
	{
		_tree.Layout.Clear( true );
		var rig = _session.Rig;
		if ( rig is null ) return;
		var filter = _filter.Text?.Trim() ?? "";
		for ( var b = 0; b < rig.Skeleton.Count; b++ )
		{
			if ( !rig.IsMotionBone( b ) ) continue;
			if ( filter.Length > 0 && !rig.Skeleton[b].Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) ) continue;
			var depth = 0;
			for ( var p = rig.Skeleton[b].ParentIndex; p >= 0; p = rig.Skeleton[p].ParentIndex ) depth++;
			_tree.Layout.Add( new BoneRow( this, b, filter.Length > 0 ? 0 : depth ) );
		}
		_tree.Layout.AddStretchCell();
	}

	sealed class BoneRow : Widget
	{
		readonly PosePanel _panel;
		readonly int _bone;
		readonly int _depth;

		public BoneRow( PosePanel panel, int bone, int depth ) : base( panel._tree )
		{
			_panel = panel; _bone = bone; _depth = depth;
			FixedHeight = 20;
			MouseTracking = true;
			Cursor = CursorShape.Finger;
		}

		protected override void OnPaint()
		{
			var s = _panel._session;
			var name = s.Rig.Skeleton[_bone].Name;
			var selected = s.SelectedBones.Contains( _bone );
			var locked = s.ActiveClip?.LockedBones.Contains( name ) == true;
			var keyed = s.ActiveClip?.Keys.KeyFrames( name ).Count > 0;
			Paint.Antialiasing = true;
			Paint.ClearPen();
			if ( selected ) { Paint.SetBrush( TaStyle.Accent.WithAlpha( .22f ) ); Paint.DrawRect( LocalRect, 3 ); }
			else if ( Paint.HasMouseOver ) { Paint.SetBrush( Color.White.WithAlpha( .05f ) ); Paint.DrawRect( LocalRect, 3 ); }
			var x = 6f + Math.Min( _depth, 14 ) * 9f;
			Paint.SetPen( locked ? Theme.Yellow : selected ? TaStyle.AccentLight : Theme.TextLight );
			Paint.DrawIcon( new Rect( x, 2, 16, 16 ), locked ? "lock" : "radio_button_unchecked", 12 );
			Paint.SetDefaultFont( 8, selected ? 600 : 400 );
			Paint.SetPen( selected ? Theme.Text : Theme.Text.WithAlpha( .85f ) );
			Paint.DrawText( new Rect( x + 18, 0, Width - x - 40, Height ), name, TextFlag.LeftCenter );
			if ( keyed )
			{
				Paint.SetPen( TaStyle.AccentLight );
				Paint.DrawIcon( new Rect( Width - 20, 2, 16, 16 ), "key", 12 );
			}
		}

		protected override void OnMouseClick( MouseEvent e )
		{
			base.OnMouseClick( e );
			var additive = e.HasCtrl || e.HasShift;
			_panel._session.SelectBone( _bone, additive );
		}
	}

	// ------------------------------------------------------------------ pose sliders

	/// <summary>Euler XYZ (degrees) of a quaternion, for display.</summary>
	static NVector3 ToEuler( NQuaternion q )
	{
		q = NQuaternion.Normalize( q );
		var sinp = 2 * (q.W * q.Y - q.Z * q.X);
		var x = MathF.Atan2( 2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y) );
		var y = MathF.Abs( sinp ) >= 1 ? MathF.CopySign( MathF.PI / 2, sinp ) : MathF.Asin( sinp );
		var z = MathF.Atan2( 2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z) );
		const float deg = 180f / MathF.PI;
		return new NVector3( x * deg, y * deg, z * deg );
	}

	static NQuaternion FromEuler( float x, float y, float z )
	{
		const float rad = MathF.PI / 180f;
		var qx = NQuaternion.CreateFromAxisAngle( NVector3.UnitX, x * rad );
		var qy = NQuaternion.CreateFromAxisAngle( NVector3.UnitY, y * rad );
		var qz = NQuaternion.CreateFromAxisAngle( NVector3.UnitZ, z * rad );
		return NQuaternion.Normalize( qz * qy * qx );
	}

	/// <summary>The bone's own-axis rotation applied by the key at the current frame.</summary>
	NQuaternion KeyRotation( AnimClip clip, int bone, int frame )
	{
		var name = _session.Rig.Skeleton[bone].Name;
		var baseLocal = clip.Frames[frame][bone];
		var delta = clip.Keys.Evaluate( name, frame );
		// delta is pre-multiplied in parent space; express it about the bone's own axes
		return NQuaternion.Normalize( NQuaternion.Conjugate( baseLocal.Rot ) * delta.Rot * baseLocal.Rot );
	}

	void SliderEdited()
	{
		if ( _refreshing || _session.PrimaryBone is not int bone || _session.ActiveClip is not { } clip ) return;
		for ( var i = 0; i < 3; i++ ) _angleLabels[i].Text = $"{_angles[i].Value:0}°";
		if ( !_interactive )
		{
			_interactive = true;
			_session.BeginInteractiveEdit( $"Pose {_session.Rig.Skeleton[bone].Name}" );
			_ = EndInteractiveSoon();
		}
		_lastSliderEdit = DateTime.UtcNow;
		var frame = _session.CurrentFrame;
		var baseLocal = clip.Frames[frame][bone];
		var own = FromEuler( _angles[0].Value, _angles[1].Value, _angles[2].Value );
		var target = new XForm( baseLocal.Pos + clip.Keys.Evaluate( _session.Rig.Skeleton[bone].Name, frame ).Pos, MathQ.Normalize( baseLocal.Rot * own ) );
		_session.SetPoseKey( bone, frame, target );
	}

	async Task EndInteractiveSoon()
	{
		while ( (DateTime.UtcNow - _lastSliderEdit).TotalMilliseconds < 600 ) await EngineThread.DelayOnMain( 150 );
		_interactive = false;
		_session.EndInteractiveEdit();
	}

	// ------------------------------------------------------------------ keys

	IEnumerable<int> TargetBones() => _session.SelectedBones.Count > 0
		? _session.SelectedBones
		: Enumerable.Range( 0, _session.Rig.Skeleton.Count ).Where( _session.Rig.IsMotionBone );

	void AddKey()
	{
		if ( _session.SelectedBones.Count == 0 ) { _session.SetStatus( "Select bones first.", Tone.Amber ); return; }
		var frame = _session.CurrentFrame;
		var bones = _session.SelectedBones.ToList();
		_session.Edit( $"Key frame {frame}", c =>
		{
			foreach ( var b in bones )
			{
				var name = _session.Rig.Skeleton[b].Name;
				c.Keys.SetKey( name, frame, c.Keys.Evaluate( name, frame ) ); // keep the current pose exactly
			}
		} );
	}

	void DeleteKey()
	{
		var frame = _session.CurrentFrame;
		var bones = TargetBones().Select( b => _session.Rig.Skeleton[b].Name ).ToList();
		_session.Edit( $"Delete key {frame}", c => { foreach ( var n in bones ) c.Keys.RemoveKey( n, frame ); } );
	}

	void JumpKey( int direction )
	{
		var clip = _session.ActiveClip;
		if ( clip is null ) return;
		var frames = (_session.SelectedBones.Count > 0
			? _session.SelectedBones.SelectMany( b => clip.Keys.KeyFrames( _session.Rig.Skeleton[b].Name ) )
			: clip.Keys.AllKeyFrames ).Distinct().OrderBy( f => f ).ToList();
		var current = _session.CurrentFrame;
		int? target = direction > 0 ? frames.Where( f => f > current ).Cast<int?>().FirstOrDefault() : frames.Where( f => f < current ).Cast<int?>().LastOrDefault();
		if ( target is int t ) _session.Seek( t );
	}

	void CopyPose()
	{
		var frames = _session.ActiveFrames;
		if ( frames is null ) return;
		var frame = frames[_session.CurrentFrame];
		_clipboard = TargetBones().ToDictionary( b => _session.Rig.Skeleton[b].Name, b => frame[b] );
		_session.SetStatus( $"Copied the pose of {_clipboard.Count} bones." );
	}

	void PastePose()
	{
		if ( _clipboard is null || _session.ActiveClip is null ) { _session.SetStatus( "Copy a pose first.", Tone.Amber ); return; }
		var frame = _session.CurrentFrame;
		var paste = _clipboard;
		_session.Edit( $"Paste pose on frame {frame}", c =>
		{
			foreach ( var (name, local) in paste )
			{
				var b = _session.Rig.Skeleton.IndexOf( name );
				if ( b < 0 ) continue;
				var target = b == _session.Rig.RootIndex ? new XForm( c.Frames[frame][b].Pos, local.Rot ) : local; // keep travel
				c.Keys.SetKey( name, frame, KeyLayer.DeltaBetween( c.Frames[frame][b], target ) );
			}
		} );
	}

	void ResetPose()
	{
		var bones = TargetBones().Select( b => _session.Rig.Skeleton[b].Name ).ToList();
		_session.Edit( "Reset pose edits", c => { foreach ( var n in bones ) c.Keys.ClearBone( n ); } );
	}

	// ------------------------------------------------------------------ refresh

	void Refresh()
	{
		var clip = _session.ActiveClip;
		Enabled = clip is not null && !_session.Busy;
		if ( clip is null || _session.Rig is null ) return;
		_refreshing = true;
		try
		{
			_falloff.Value = clip.Keys.FalloffFrames;
			_falloffLabel.Text = $"{clip.Keys.FalloffFrames} fr";
			if ( _session.PrimaryBone is int bone )
			{
				var name = _session.Rig.Skeleton[bone].Name;
				var more = _session.SelectedBones.Count > 1 ? $" (+{_session.SelectedBones.Count - 1} more)" : "";
				_boneTitle.Text = $"{name}{more} · frame {_session.CurrentFrame}";
				if ( !_interactive )
				{
					var euler = ToEuler( KeyRotation( clip, bone, _session.CurrentFrame ) );
					_angles[0].Value = euler.X; _angles[1].Value = euler.Y; _angles[2].Value = euler.Z;
					for ( var i = 0; i < 3; i++ ) _angleLabels[i].Text = $"{_angles[i].Value:0}°";
				}
				var keys = clip.Keys.KeyFrames( name );
				_keyInfo.Text = keys.Count == 0 ? "No keys on this bone. Moving a slider (or the rotate handle in the view) adds one." : $"Keys at frames {string.Join( ", ", keys.Take( 12 ) )}{(keys.Count > 12 ? "…" : "")}";
			}
			else
			{
				_boneTitle.Text = "Click a bone in the view or the list to pose it.";
				_keyInfo.Text = clip.Keys.IsEmpty ? "" : $"{clip.Keys.KeyedBones.Count()} bones have pose edits.";
			}
			foreach ( var s in _angles ) s.Enabled = _session.PrimaryBone is not null;
		}
		finally { _refreshing = false; }
	}
}
