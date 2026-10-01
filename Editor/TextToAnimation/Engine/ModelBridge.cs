using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Maths;
using TextToAnimation.Rig;
using TextToAnimation.Vmdl;

namespace TextToAnimation.Editor.Engine;

using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

/// <summary>An animation sequence a model exposes, and where it comes from.</summary>
public sealed record SequenceInfo( string Name, bool DefinedInModel, string SourceFile, bool Looping, bool HasExtractMotion, bool IsDelta );

/// <summary>
/// Reads what the editor needs from a compiled model: the skeleton in engine space (inches, Z-up,
/// parent-local rest), its animation sequences and their poses. Main thread only.
/// </summary>
public static class ModelBridge
{
	/// <summary>Builds the engine-space skeleton of a model. <c>Bone.LocalTransform</c> is MODEL space despite
	/// its name (measured in humanoid-retargeter), so parent-local rest transforms are derived here.</summary>
	/// <summary>
	/// Why the compiled skeleton can't be used, or null. Uses only the name and parent lookups: on a skeleton whose
	/// parents loop, every engine call that walks the hierarchy (bone transforms, the bone list) never returns.
	/// </summary>
	public static string SkeletonProblem( Model model )
	{
		var count = model.BoneCount;
		var parents = new int[count];
		for ( var i = 0; i < count; i++ ) parents[i] = model.GetBoneParent( i );
		for ( var i = 0; i < count; i++ )
		{
			var steps = 0;
			for ( var p = parents[i]; p >= 0 && p < count; p = parents[p] )
			{
				if ( ++steps > count || p == i )
				{
					var unnamed = Enumerable.Range( 0, count ).Any( b => string.IsNullOrWhiteSpace( model.GetBoneName( b ) ) );
					return "s&box compiled this model's skeleton into a loop" + (unnamed ? " (a bone has no name - usually the root)" : "")
						+ ", so it can't be animated. Give every bone a name in your 3D app and export the FBX again.";
				}
			}
		}
		return null;
	}

	public static Skeleton SkeletonFromModel( Model model )
	{
		static XForm ToXForm( Transform t ) => new(
			new NVector3( t.Position.x, t.Position.y, t.Position.z ),
			NQuaternion.Normalize( new NQuaternion( t.Rotation.x, t.Rotation.y, t.Rotation.z, t.Rotation.w ) ) );
		// index-based and bounded: on some imported models the engine's bone tree enumeration (Bones.AllBones)
		// never returns, so it is not used. A parent chain that loops is cut (that bone becomes a root).
		var count = model.BoneCount;
		var names = new string[count];
		var parents = new int[count];
		var world = new XForm[count];
		for ( var i = 0; i < count; i++ )
		{
			names[i] = model.GetBoneName( i );
			parents[i] = model.GetBoneParent( i );
			if ( parents[i] < -1 || parents[i] >= count || parents[i] == i ) parents[i] = -1;
			world[i] = ToXForm( model.GetBoneTransform( i ) );
		}
		for ( var i = 0; i < count; i++ )
		{
			var steps = 0;
			for ( var p = parents[i]; p >= 0; p = parents[p] )
			{
				if ( ++steps > count ) { parents[i] = -1; break; } // a loop
			}
		}
		// duplicate names would make the parent links ambiguous: suffix them
		var seen = new HashSet<string>( StringComparer.Ordinal );
		for ( var i = 0; i < count; i++ )
		{
			var n = string.IsNullOrEmpty( names[i] ) ? $"bone_{i}" : names[i];
			for ( var k = 1; !seen.Add( n ); k++ ) n = $"{names[i]}_{k}";
			names[i] = n;
		}
		var defs = new List<BoneDefinition>( count );
		for ( var i = 0; i < count; i++ )
		{
			var p = parents[i];
			var local = p < 0 ? world[i] : XForm.ToLocal( world[p], world[i] );
			defs.Add( new BoneDefinition( names[i], p < 0 ? null : names[p], local ) );
		}
		return Skeleton.Create( defs );
	}

	public static XForm ToXForm( Transform t ) => new(
		new NVector3( t.Position.x, t.Position.y, t.Position.z ),
		NQuaternion.Normalize( new NQuaternion( t.Rotation.x, t.Rotation.y, t.Rotation.z, t.Rotation.w ) ) );

	public static Transform ToTransform( XForm x, Vector3 scale ) => new(
		new Vector3( x.Pos.X, x.Pos.Y, x.Pos.Z ), new Rotation( x.Rot.X, x.Rot.Y, x.Rot.Z, x.Rot.W ), scale );

	/// <summary>Loads a model and waits briefly for it to be ready (compiles can make it momentarily unavailable).</summary>
	public static async Task<Model> LoadAsync( string modelPath, int attempts = 20 )
	{
		await EngineThread.SwitchToMainThread();
		Model model = null;
		for ( var i = 0; i < attempts; i++ )
		{
			model = Model.Load( modelPath );
			if ( model is not null && !model.IsError && model.BoneCount > 0 ) return model;
			await EngineThread.DelayOnMain( 150 );
		}
		return model;
	}

	/// <summary>The model's sequences, marking those defined in its own vmdl (the ones that can be replaced).</summary>
	public static List<SequenceInfo> ListSequences( Model model, string vmdlText )
	{
		var own = new Dictionary<string, KvObject>( StringComparer.Ordinal );
		if ( !string.IsNullOrEmpty( vmdlText ) )
		{
			try { Collect( ((KvObject)Kv3.Parse( vmdlText ).Root), own ); }
			catch ( FormatException ) { /* unreadable vmdl: everything reads as inherited */ }
		}
		var result = new List<SequenceInfo>();
		foreach ( var name in model.AnimationNames )
		{
			if ( own.TryGetValue( name, out var node ) )
			{
				var children = node.GetOrNull( "children" ) as KvArray;
				var extract = children?.Items.OfType<KvObject>().Any( c => c.GetString( "_class" ) == "ExtractMotion" ) ?? false;
				var delta = children?.Items.OfType<KvObject>().Any( c => c.GetString( "_class" ) == "AnimSubtract" ) ?? false;
				var looping = node.GetOrNull( "looping" ) is KvBool { Value: true };
				result.Add( new SequenceInfo( name, true, node.GetString( "source_filename" ) ?? "", looping, extract, delta ) );
			}
			else
			{
				result.Add( new SequenceInfo( name, false, "", false, false, false ) );
			}
		}
		return result;
	}

	static void Collect( KvObject node, Dictionary<string, KvObject> into )
	{
		if ( node.GetString( "_class" ) == "AnimFile" && node.GetString( "name" ) is { Length: > 0 } name )
			into[name] = node;
		if ( node.GetOrNull( "children" ) is KvArray children )
			foreach ( var child in children.Items.OfType<KvObject>() ) Collect( child, into );
		if ( node.GetOrNull( "rootNode" ) is KvObject root ) Collect( root, into );
	}

	/// <summary>
	/// Samples a compiled sequence into parent-local engine-space frames on <paramref name="skeleton"/> (by bone
	/// name) at <paramref name="fps"/>. Uses the engine's own evaluation, so every encoding, inherited base-model
	/// sequences and constraints work. Yields every few frames so the editor stays responsive.
	/// </summary>
	public static async Task<(List<XForm[]> Frames, float Duration)> SampleSequenceAsync(
		Model model, string sequence, Skeleton skeleton, float fps, CancellationToken token )
	{
		await EngineThread.SwitchToMainThread();
		var map = skeleton.Bones.Select( b => model.Bones.GetBone( b.Name )?.Index ?? -1 ).ToArray();
		var world = new SceneWorld();
		var scene = new SceneModel( world, model, Transform.Zero ) { UseAnimGraph = false };
		try
		{
			scene.CurrentSequence.Name = sequence;
			scene.Update( 0 );
			var duration = scene.CurrentSequence.Duration;
			if ( !(duration > 0f) || !float.IsFinite( duration ) ) duration = 0f;
			var count = Math.Max( 1, (int)MathF.Round( duration * fps ) + 1 );
			var frames = new List<XForm[]>( count );
			for ( var f = 0; f < count; f++ )
			{
				token.ThrowIfCancellationRequested();
				scene.CurrentSequence.Time = count <= 1 ? 0f : duration * f / (count - 1);
				scene.Update( 0 );
				var frame = new XForm[skeleton.Count];
				for ( var b = 0; b < skeleton.Count; b++ )
					frame[b] = map[b] >= 0 ? ToXForm( scene.GetParentSpaceBone( map[b] ) ) : skeleton[b].RestLocal;
				frames.Add( frame );
				if ( f % 64 == 63 )
				{
					await Task.Delay( 1 );
					await EngineThread.SwitchToMainThread();
					if ( !scene.IsValid() ) throw new OperationCanceledException();
				}
			}
			return (frames, duration);
		}
		finally
		{
			if ( scene.IsValid() ) scene.Delete();
			world.Delete();
		}
	}

	/// <summary>Absolute path of a model's .vmdl source, or null when it has none (compiled-only content).</summary>
	public static string SourcePathOf( Asset asset )
	{
		var source = EngineThread.Try( () => asset.GetSourceFile( true ) );
		return !string.IsNullOrEmpty( source ) && File.Exists( source ) && source.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? source : null;
	}
}
