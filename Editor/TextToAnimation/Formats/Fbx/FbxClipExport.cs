using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Maths;

namespace TextToAnimation.Editor.Formats.Fbx;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Animation clips to FBX (binary, 7.4): the skeleton alone with the clip, or a model's own source FBX - its mesh, skin,
/// materials and bones untouched - with its animation replaced by the clip. No Sandbox types: unit tested by reading
/// the written file back (FbxClipExportTests).
/// </summary>
public static class FbxClipExport
{
	/// <summary>One bone of the clip's skeleton: name, parent index (-1 = root), rest transform relative to the parent.</summary>
	public readonly record struct Bone( string Name, int Parent, XForm RestLocal );

	/// <summary>Engine units are inches; FBX says how many centimeters one unit is.</summary>
	public const double InchInCentimeters = 2.54;

	/// <summary>
	/// The skeleton (limb nodes, rest pose as the bind) and the clip as curves, in engine units (inches) and engine axes
	/// (Z up, X forward) as the file's global settings declare.
	/// </summary>
	public static byte[] WriteSkeleton( IReadOnlyList<Bone> bones, IReadOnlyList<XForm[]> frames, float fps, string clipName )
	{
		Validate( bones, frames );
		frames = Normalized( frames );
		var root = Node( "" );
		var header = Node( "FBXHeaderExtension" );
		header.Children.AddRange( new[] { Node( "FBXHeaderVersion", 1003 ), Node( "FBXVersion", 7400 ) } );
		root.Children.Add( header );
		var settings = Node( "GlobalSettings" );
		settings.Children.Add( Node( "Version", 1000 ) );
		var properties = Node( "Properties70" );
		properties.Children.AddRange( new[]
		{
			Property( "UpAxis", "int", 2 ), Property( "UpAxisSign", "int", 1 ),
			Property( "FrontAxis", "int", 1 ), Property( "FrontAxisSign", "int", -1 ),
			Property( "CoordAxis", "int", 0 ), Property( "CoordAxisSign", "int", 1 ),
			Property( "OriginalUpAxis", "int", 2 ), Property( "OriginalUpAxisSign", "int", 1 ),
			Property( "UnitScaleFactor", "double", InchInCentimeters ), Property( "OriginalUnitScaleFactor", "double", InchInCentimeters ),
			Property( "TimeMode", "enum", 14 ), Property( "CustomFrameRate", "double", (double)fps ),
		} );
		settings.Children.Add( properties );
		root.Children.Add( settings );
		var objects = Node( "Objects" );
		var connections = Node( "Connections" );
		long nextId = 1000;
		var ids = new long[bones.Count];
		for ( var b = 0; b < bones.Count; b++ )
		{
			ids[b] = nextId++;
			var model = Node( "Model", ids[b], bones[b].Name + "\0\u0001Model", "LimbNode" );
			model.Children.Add( Node( "Version", 232 ) );
			var bind = Node( "Properties70" );
			bind.Children.AddRange( new[]
			{
				Vector( "Lcl Translation", bones[b].RestLocal.Pos ),
				Vector( "Lcl Rotation", FbxEuler.QuaternionToEulerDegrees( bones[b].RestLocal.Rot, 0 ) ),
				Vector( "Lcl Scaling", Vector3.One ), Property( "RotationOrder", "enum", 0 ), Property( "InheritType", "enum", 1 ),
			} );
			model.Children.Add( bind );
			objects.Children.Add( model );
			var attribute = Node( "NodeAttribute", nextId++, bones[b].Name + "\0\u0001NodeAttribute", "LimbNode" );
			attribute.Children.Add( Node( "TypeFlags", "Skeleton" ) );
			objects.Children.Add( attribute );
			connections.Children.Add( Node( "C", "OO", attribute.Properties[0], ids[b] ) );
			connections.Children.Add( Node( "C", "OO", ids[b], bones[b].Parent < 0 ? 0L : ids[bones[b].Parent] ) );
		}
		var channels = new Channel[bones.Count];
		for ( var b = 0; b < bones.Count; b++ )
		{
			var bone = b;
			channels[b] = new Channel( ids[b],
				frames.Select( f => f[bone].Pos ).ToArray(),
				frames.Select( f => FbxEuler.QuaternionToEulerDegrees( f[bone].Rot, 0 ) ).ToArray(), null );
		}
		AddAnimation( objects, connections, channels, fps, frames.Count, clipName, ref nextId );
		root.Children.Add( objects );
		root.Children.Add( connections );
		return FbxBinaryWriter.Write( root );
	}

	/// <summary>
	/// <paramref name="sourceFbx"/> (binary or ASCII) with its animation replaced by the clip: every bone of the clip
	/// found by name among the file's models gets curves in that model's own space (its pre/post-rotation, pivots,
	/// rotation order and scale, the file's units and axes), so the file's mesh and skin play the clip as they are.
	/// The engine skeleton and the file's bones are matched by a similarity fit of their rest poses (the import's
	/// units, axis change and placement) plus each bone's own rest offset.
	/// </summary>
	public static byte[] WriteIntoSource( byte[] sourceFbx, IReadOnlyList<Bone> bones, IReadOnlyList<XForm[]> frames, float fps, string clipName, out string report )
	{
		Validate( bones, frames );
		frames = Normalized( frames );
		var root = FbxTokenizer.Parse( sourceFbx );
		if ( SourceVersion( root ) is > 0 and < 7000 and var version )
			throw new NotSupportedException( $"The model's FBX is version {version / 1000}.{version % 1000 / 100} (2010 or older), which keeps its transforms in a layout this export can't rewrite. Export the skeleton with the animation instead, or re-save the model as FBX 2011 or newer." );
		var scene = FbxScene.Build( root );
		// by name, and by the engine's form of it (its compiler turns '.' and other symbols into '_')
		var byName = new Dictionary<string, FbxObject>( StringComparer.Ordinal );
		foreach ( var m in scene.Models ) byName.TryAdd( m.Name, m );
		foreach ( var m in scene.Models ) byName.TryAdd( EngineName( m.Name ), m );
		var model = bones.Select( b => byName.TryGetValue( b.Name, out var m ) ? m : null ).ToArray();
		var matched = Enumerable.Range( 0, bones.Count ).Where( b => model[b] is not null ).ToList();
		if ( matched.Count < 2 ) throw new InvalidOperationException( "The source FBX has too few of the model's bones to carry the animation." );

		// rest poses: the file's models (all of them, so unmatched nodes in between are honoured) and the engine bones
		var transforms = scene.Models.ToDictionary( m => m.Id, m => FbxTransform.FromModel( scene, m ) );
		var fileRest = new Dictionary<long, Matrix4x4>();
		// the pose the skin was bound in (what the engine builds the skeleton from) where the file records it - a skin
		// cluster's TransformLink, else the bind Pose - else the models' default transforms
		Matrix4x4 FileRest( FbxObject m )
		{
			if ( fileRest.TryGetValue( m.Id, out var w ) ) return w;
			if ( scene.SkinBind.TryGetValue( m.Id, out var skin ) ) w = skin;
			else if ( scene.BindPose.TryGetValue( m.Id, out var pose ) ) w = pose;
			else w = transforms[m.Id].LocalMatrixDefault() * (m.ModelParent is { } p && transforms.ContainsKey( p.Id ) ? FileRest( p ) : Matrix4x4.Identity);
			return fileRest[m.Id] = w;
		}
		var engineRest = Worlds( bones, bones.Select( b => b.RestLocal ).ToArray() );

		// the import's mapping: file world = engine world * M (uniform scale, rotation, translation), fitted on the rest
		// positions of the matched bones; each bone's remaining offset C (its own frame and scale in the file) is constant
		// fitted on the bones whose bind pose the file records (end bones without one may sit elsewhere than the
		// engine put them; their own offset below still makes them follow)
		bool Bound( int b ) => scene.SkinBind.ContainsKey( model[b].Id ) || scene.BindPose.ContainsKey( model[b].Id );
		var fitOn = matched.Count( Bound ) >= 3 ? matched.Where( Bound ).ToList() : matched;
		var fit = Similarity( fitOn.Select( b => engineRest[b].Pos ).ToList(), fitOn.Select( b => FileRest( model[b] ).Translation ).ToList() );
		var offset = new Dictionary<int, Matrix4x4>();
		foreach ( var b in matched )
		{
			Matrix4x4.Invert( Matrix( engineRest[b] ) * fit.M, out var inv );
			offset[b] = FileRest( model[b] ) * inv;
		}

		// per frame, every model's world: matched bones from the clip, the rest following their parents at rest
		var boneOfModel = matched.ToDictionary( b => model[b].Id, b => b );
		var order = new List<FbxObject>();
		var seen = new HashSet<long>();
		void Visit( FbxObject m )
		{
			if ( !seen.Add( m.Id ) ) return;
			if ( m.ModelParent is { } p && transforms.ContainsKey( p.Id ) ) Visit( p );
			order.Add( m );
		}
		foreach ( var m in scene.Models ) Visit( m );
		var translations = matched.ToDictionary( b => b, _ => new Vector3[frames.Count] );
		var rotations = matched.ToDictionary( b => b, _ => new Vector3[frames.Count] );
		var worstScale = 0f;
		for ( var f = 0; f < frames.Count; f++ )
		{
			var engine = Worlds( bones, frames[f] );
			var world = new Dictionary<long, Matrix4x4>();
			foreach ( var m in order )
			{
				var parent = m.ModelParent is { } p && world.TryGetValue( p.Id, out var pw ) ? pw : Matrix4x4.Identity;
				if ( boneOfModel.TryGetValue( m.Id, out var b ) )
				{
					var w = offset[b] * Matrix( engine[b] ) * fit.M;
					world[m.Id] = w;
					Matrix4x4.Invert( parent, out var parentInv );
					var (t, r, scaleError) = Channels( transforms[m.Id], w * parentInv );
					translations[b][f] = t;
					rotations[b][f] = r;
					worstScale = MathF.Max( worstScale, scaleError );
				}
				else world[m.Id] = transforms[m.Id].LocalMatrixDefault() * parent;
			}
		}

		// out with the file's own animation, in with the clip
		var objects = root.Children.First( n => n.Name == "Objects" );
		var connections = root.Children.First( n => n.Name == "Connections" );
		var animation = new HashSet<string>( StringComparer.Ordinal ) { "AnimationStack", "AnimationLayer", "AnimationCurveNode", "AnimationCurve" };
		var removed = objects.Children.Where( n => animation.Contains( n.Name ) ).Select( n => Convert.ToInt64( n.Properties[0] ) ).ToHashSet();
		objects.Children.RemoveAll( n => animation.Contains( n.Name ) );
		connections.Children.RemoveAll( c => c.Properties.Count >= 3 && (removed.Contains( Convert.ToInt64( c.Properties[1] ) ) || removed.Contains( Convert.ToInt64( c.Properties[2] ) )) );
		root.Children.RemoveAll( n => n.Name == "Takes" );
		var nextId = scene.ObjectsById.Keys.DefaultIfEmpty( 0 ).Max() + 1000;
		var channels = matched.Select( b => new Channel( model[b].Id, translations[b], rotations[b], null ) ).ToArray();
		var added = AddAnimation( objects, connections, channels, fps, frames.Count, clipName, ref nextId );
		UpdateDefinitions( root, added );
		report = $"{matched.Count} of {bones.Count} bones animated in the source FBX (fit scale {fit.Scale:0.####}, rest fit error {fit.Error:0.###} file units"
			+ (worstScale > 0.01f ? $", bones scaled by up to {worstScale:P0} kept their rest scale" : "") + ")";
		return FbxBinaryWriter.Write( root );
	}

	/// <summary>A bone name as the engine's model compiler writes it: symbols other than '_' become '_'.</summary>
	public static string EngineName( string name ) => string.Concat( name.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ) );

	/// <summary>How many of <paramref name="boneNames"/> the file has as models (by name or the engine's form of it).</summary>
	public static int MatchingBones( byte[] sourceFbx, IEnumerable<string> boneNames )
	{
		try
		{
			var scene = FbxScene.Build( FbxTokenizer.Parse( sourceFbx ) );
			var names = scene.Models.SelectMany( m => new[] { m.Name, EngineName( m.Name ) } ).ToHashSet( StringComparer.Ordinal );
			return boneNames.Count( names.Contains );
		}
		catch ( Exception ) { return 0; }
	}

	/// <summary>The file's FBX version (FBXHeaderExtension/FBXVersion), or 0 when it doesn't say.</summary>
	public static int SourceVersion( FbxNode root ) =>
		root.Children.FirstOrDefault( n => n.Name == "FBXHeaderExtension" )?.Children.FirstOrDefault( n => n.Name == "FBXVersion" ) is { Properties.Count: > 0 } v
			? Convert.ToInt32( v.Properties[0] ) : 0;

	/// <summary>Whether <paramref name="sourceFbx"/> can take the clip (FBX 7.x, binary or ASCII).</summary>
	public static bool CanWriteInto( byte[] sourceFbx )
	{
		try { return SourceVersion( FbxTokenizer.Parse( sourceFbx ) ) is 0 or >= 7000; }
		catch ( Exception ) { return false; }
	}

	// ------------------------------------------------------------------ animation nodes

	/// <summary>One model's curves: translation and rotation (euler degrees in the model's order) per frame; scale constant.</summary>
	readonly record struct Channel( long ModelId, Vector3[] Translation, Vector3[] Rotation, Vector3[] Scale );

	/// <summary>Adds one AnimationStack/Layer and the curves; returns how many objects of each type were added.</summary>
	static Dictionary<string, int> AddAnimation( FbxNode objects, FbxNode connections, IReadOnlyList<Channel> channels, float fps, int frameCount, string clipName, ref long nextId )
	{
		var added = new Dictionary<string, int> { ["AnimationStack"] = 1, ["AnimationLayer"] = 1, ["AnimationCurveNode"] = 0, ["AnimationCurve"] = 0 };
		var times = Enumerable.Range( 0, frameCount ).Select( f => (long)Math.Round( f * (double)FbxAnimCurve.TicksPerSecond / fps ) ).ToArray();
		var stackId = nextId++;
		var layerId = nextId++;
		var stack = Node( "AnimationStack", stackId, clipName + "\0\u0001AnimStack", "" );
		var span = Node( "Properties70" );
		span.Children.AddRange( new[] { Property( "LocalStart", "KTime", 0L ), Property( "LocalStop", "KTime", times[^1] ),
			Property( "ReferenceStart", "KTime", 0L ), Property( "ReferenceStop", "KTime", times[^1] ) } );
		stack.Children.Add( span );
		objects.Children.Add( stack );
		objects.Children.Add( Node( "AnimationLayer", layerId, "BaseLayer\0\u0001AnimLayer", "" ) );
		connections.Children.Add( Node( "C", "OO", layerId, stackId ) );
		foreach ( var channel in channels )
		{
			foreach ( var (kind, values) in new[] { ("T", channel.Translation), ("R", channel.Rotation), ("S", channel.Scale) } )
			{
				if ( values is null ) continue;
				var curveNode = Node( "AnimationCurveNode", nextId++, kind + "\0\u0001AnimCurveNode", "" );
				objects.Children.Add( curveNode );
				added["AnimationCurveNode"]++;
				connections.Children.Add( Node( "C", "OO", curveNode.Properties[0], layerId ) );
				connections.Children.Add( Node( "C", "OP", curveNode.Properties[0], channel.ModelId,
					kind == "T" ? "Lcl Translation" : kind == "R" ? "Lcl Rotation" : "Lcl Scaling" ) );
				var defaults = Node( "Properties70" );
				curveNode.Children.Add( defaults );
				for ( var axis = 0; axis < 3; axis++ )
				{
					var component = "d|" + "XYZ"[axis];
					var samples = values.Select( v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z ).ToArray();
					// euler angles take the turn nearest the previous frame's (no 359 -> 1 degree spins between keys)
					if ( kind == "R" )
						for ( var f = 1; f < samples.Length; f++ ) samples[f] += 360f * MathF.Round( (samples[f - 1] - samples[f]) / 360f );
					defaults.Children.Add( Property( component, "Number", (double)samples[0] ) );
					var curve = Node( "AnimationCurve", nextId++, "\0\u0001AnimCurve", "" );
					curve.Children.AddRange( new[] { Node( "Default", (double)samples[0] ), Node( "KeyVer", 4008 ),
						Node( "KeyTime", (object)times ), Node( "KeyValueFloat", (object)samples ),
						Node( "KeyAttrFlags", (object)new[] { 4 } ), Node( "KeyAttrDataFloat", (object)new float[4] ),
						Node( "KeyAttrRefCount", (object)new[] { frameCount } ) } );
					objects.Children.Add( curve );
					added["AnimationCurve"]++;
					connections.Children.Add( Node( "C", "OP", curve.Properties[0], curveNode.Properties[0], component ) );
				}
			}
		}
		return added;
	}

	/// <summary>Keeps the Definitions section's object counts true after the animation was swapped.</summary>
	static void UpdateDefinitions( FbxNode root, Dictionary<string, int> added )
	{
		var definitions = root.Children.FirstOrDefault( n => n.Name == "Definitions" );
		if ( definitions is null ) return;
		foreach ( var (type, count) in added )
		{
			var entry = definitions.Children.FirstOrDefault( n => n.Name == "ObjectType" && n.Properties.Count > 0 && n.Properties[0] as string == type );
			if ( entry is null )
			{
				entry = Node( "ObjectType", type );
				entry.Children.Add( Node( "Count", count ) );
				definitions.Children.Add( entry );
				continue;
			}
			var countNode = entry.Children.FirstOrDefault( n => n.Name == "Count" );
			if ( countNode is null ) entry.Children.Add( Node( "Count", count ) );
			else { countNode.Properties.Clear(); countNode.Properties.Add( count ); }
		}
	}

	// ------------------------------------------------------------------ math

	/// <summary>
	/// Lcl channels of a model for its local matrix (row-vector), inverting
	/// <see cref="FbxTransform.LocalMatrix"/> with the model's static scale: translation and euler rotation.
	/// </summary>
	static (Vector3 Translation, Vector3 RotationDeg, float ScaleError) Channels( FbxTransform t, Matrix4x4 local )
	{
		// local = Sp^-1 S (Sp + Soff - Rp) R (Rp + Roff + T): take out the scale part first
		var pre = Matrix4x4.CreateTranslation( -t.ScalingPivot ) * Matrix4x4.CreateScale( t.LclScaling ) * Matrix4x4.CreateTranslation( t.ScalingPivot + t.ScalingOffset - t.RotationPivot );
		Matrix4x4.Invert( pre, out var preInv );
		var rest = preInv * local; // = R (Rp + Roff + T)
		Matrix4x4.Decompose( rest, out var scale, out var rotation, out var translation );
		var scaleError = MathF.Max( MathF.Abs( scale.X - 1 ), MathF.Max( MathF.Abs( scale.Y - 1 ), MathF.Abs( scale.Z - 1 ) ) );
		var euler = Quaternion.Normalize( Quaternion.Conjugate( t.PreRotation ) * rotation * t.PostRotation );
		return (translation - t.RotationPivot - t.RotationOffset, FbxEuler.QuaternionToEulerDegrees( euler, t.RotationOrder ), scaleError);
	}

	static Matrix4x4 Matrix( XForm x ) => Matrix4x4.CreateFromQuaternion( x.Rot ) * Matrix4x4.CreateTranslation( x.Pos );

	static XForm[] Worlds( IReadOnlyList<Bone> bones, IReadOnlyList<XForm> locals )
	{
		var world = new XForm[bones.Count];
		for ( var b = 0; b < bones.Count; b++ )
			world[b] = bones[b].Parent < 0 ? locals[b] : XForm.Compose( world[bones[b].Parent], locals[b] );
		return world;
	}

	/// <summary>
	/// The similarity transform (uniform scale, rotation, translation) best mapping <paramref name="from"/> onto
	/// <paramref name="to"/> in the least-squares sense (Umeyama), as a row-vector matrix: to ~ from * M.
	/// </summary>
	static (Matrix4x4 M, float Scale, float Error) Similarity( IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to )
	{
		var n = from.Count;
		var ca = from.Aggregate( Vector3.Zero, ( s, v ) => s + v ) / n;
		var cb = to.Aggregate( Vector3.Zero, ( s, v ) => s + v ) / n;
		// covariance H = sum (a - ca)(b - cb)^T, as doubles
		var h = new double[3, 3];
		double varA = 0;
		for ( var i = 0; i < n; i++ )
		{
			var a = from[i] - ca; var b = to[i] - cb;
			varA += a.LengthSquared();
			float[] av = { a.X, a.Y, a.Z }, bv = { b.X, b.Y, b.Z };
			for ( var r = 0; r < 3; r++ ) for ( var c = 0; c < 3; c++ ) h[r, c] += av[r] * bv[c];
		}
		// the rotation maximising trace(R H): Horn's quaternion method (largest eigenvector of the 4x4 N), by power iteration
		double sxx = h[0, 0], sxy = h[0, 1], sxz = h[0, 2], syx = h[1, 0], syy = h[1, 1], syz = h[1, 2], szx = h[2, 0], szy = h[2, 1], szz = h[2, 2];
		var N = new double[4, 4]
		{
			{ sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
			{ syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
			{ szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
			{ sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
		};
		// shift to make it positive definite, then power iterate
		var shift = 0.0; for ( var r = 0; r < 4; r++ ) for ( var c = 0; c < 4; c++ ) shift = Math.Max( shift, Math.Abs( N[r, c] ) );
		shift *= 4;
		var q = new double[] { 1, 0.1, 0.1, 0.1 };
		for ( var it = 0; it < 200; it++ )
		{
			var next = new double[4];
			for ( var r = 0; r < 4; r++ ) { next[r] = shift * q[r]; for ( var c = 0; c < 4; c++ ) next[r] += N[r, c] * q[c]; }
			var len = Math.Sqrt( next.Sum( v => v * v ) );
			for ( var r = 0; r < 4; r++ ) q[r] = next[r] / len;
		}
		// Horn's q = (w, x, y, z) rotates a onto b with column vectors: b = R a
		var rotation = Quaternion.Normalize( new Quaternion( (float)q[1], (float)q[2], (float)q[3], (float)q[0] ) );
		double dot = 0;
		for ( var i = 0; i < n; i++ ) dot += Vector3.Dot( Vector3.Transform( from[i] - ca, rotation ), to[i] - cb );
		var scale = varA > 1e-12 ? (float)(dot / varA) : 1f;
		var m = Matrix4x4.CreateScale( scale ) * Matrix4x4.CreateFromQuaternion( rotation );
		m.Translation = cb - Vector3.Transform( ca, m );
		var error = (float)Math.Sqrt( Enumerable.Range( 0, n ).Average( i => (Vector3.Transform( from[i], m ) - to[i]).LengthSquared() ) );
		return (m, scale, error);
	}

	static void Validate( IReadOnlyList<Bone> bones, IReadOnlyList<XForm[]> frames )
	{
		if ( bones.Count == 0 || frames.Count == 0 || frames.Any( f => f.Length != bones.Count ) )
			throw new ArgumentException( "The clip's frames must match the skeleton." );
	}

	/// <summary>Frames with unit rotations (euler extraction assumes them).</summary>
	static XForm[][] Normalized( IReadOnlyList<XForm[]> frames ) =>
		frames.Select( f => f.Select( x => new XForm( x.Pos, Quaternion.Normalize( x.Rot ) ) ).ToArray() ).ToArray();

	// ------------------------------------------------------------------ nodes

	static FbxNode Node( string name, params object[] values )
	{
		var node = new FbxNode( name );
		node.Properties.AddRange( values );
		return node;
	}

	static FbxNode Property( string name, string type, params object[] values ) => Node( "P", new object[] { name, type, "", "A" }.Concat( values ).ToArray() );

	static FbxNode Vector( string name, Vector3 v ) => Property( name, name, (double)v.X, (double)v.Y, (double)v.Z );
}
