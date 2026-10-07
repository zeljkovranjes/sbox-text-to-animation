using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>
/// Which bones actually deform a mesh, read from the model's source FBX (its skin cluster deformers). UniMate's
/// exporter prunes a skeleton on exactly this ("skinned bones are never prunable"); the engine doesn't expose skin
/// weights, so they come from the file the vmdl was built from.
/// </summary>
public static class FbxSkin
{
	/// <summary>UniMate's _SKINNING_WEIGHT_EPS: a bone with no weight above this deforms nothing.</summary>
	public const double WeightEps = 1e-5;

	/// <summary>
	/// Per-bone skin weights (largest single, total) of a model, from the mesh FBX files its vmdl renders (all of
	/// them merged), keyed by the FBX bone name and by the engine's form of it ('.' and other symbols become '_').
	/// Null when no FBX could be read.
	/// </summary>
	public static Dictionary<string, (double Max, double Sum)> ForVmdl( string vmdlText, Func<string, string> resolve )
	{
		if ( string.IsNullOrEmpty( vmdlText ) || resolve is null ) return null;
		Dictionary<string, (double Max, double Sum)> all = null;
		foreach ( Match m in Regex.Matches( vmdlText, @"filename\s*=\s*""([^""]+\.fbx)""", RegexOptions.IgnoreCase ) )
		{
			var path = resolve( m.Groups[1].Value );
			if ( path is null || !File.Exists( path ) ) continue;
			Dictionary<string, (double Max, double Sum)> found;
			try { found = BoneWeights( File.ReadAllBytes( path ) ); }
			catch ( Exception e ) when ( e is FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException or IOException ) { continue; }
			if ( found is null ) continue;
			all ??= new Dictionary<string, (double Max, double Sum)>( StringComparer.Ordinal );
			foreach ( var (name, w) in found )
			{
				foreach ( var key in new[] { name, string.Concat( name.Select( c => char.IsLetterOrDigit( c ) || c == '_' ? c : '_' ) ) } )
				{
					var old = all.TryGetValue( key, out var o ) ? o : (0.0, 0.0);
					all[key] = (Math.Max( old.Item1, w.Max ), old.Item2 + w.Sum);
				}
			}
		}
		return all;
	}

	/// <summary>Names of the bones with any skin weight above <see cref="WeightEps"/>; null when the file has no skin.</summary>
	public static HashSet<string> SkinnedBones( byte[] fbx )
	{
		var weights = BoneWeights( fbx );
		return weights?.Where( kv => kv.Value.Max >= WeightEps ).Select( kv => kv.Key ).ToHashSet( StringComparer.Ordinal );
	}

	/// <summary>
	/// Per bone (by name): its largest single vertex weight (upstream's skinned test) and the sum of its weights
	/// (upstream's secondary-root choice). Null when the file has no skin clusters.
	/// </summary>
	public static Dictionary<string, (double Max, double Sum)> BoneWeights( byte[] fbx )
	{
		var root = Formats.Fbx.FbxTokenizer.Parse( fbx );
		var objects = root.Child( "Objects" );
		var connections = root.Child( "Connections" );
		if ( objects is null || connections is null ) return null;

		// FBX 7 numbers its objects, FBX 6 names them; key both the same way
		static string Key( object p ) => p switch { string s => s, null => "", _ => Convert.ToInt64( p ).ToString() };
		var boneName = new Dictionary<string, string>();
		var clusterWeights = new Dictionary<string, (double Max, double Sum)>();
		var clusters = 0;
		foreach ( var node in objects.Children )
		{
			if ( node.Properties.Count == 0 ) continue;
			var kind = node.Properties.OfType<string>().LastOrDefault();
			var raw = node.Properties.Count >= 2 && node.Properties[1] is string n7 && node.Properties[0] is not string ? n7 : node.Properties[0] as string;
			if ( node.Name == "Model" && kind is "LimbNode" or "Limb" or "Root" or "Null" && raw is not null )
				boneName[Key( node.Properties[0] )] = Formats.Fbx.FbxNode.SplitName( raw ).Name;
			else if ( node.Name == "Deformer" && kind == "Cluster" )
			{
				clusters++;
				var weights = node.Child( "Weights" );
				double max = 0, sum = 0;
				if ( weights is not null ) foreach ( var w in Numbers( weights ) ) { max = Math.Max( max, w ); sum += w; }
				clusterWeights[Key( node.Properties[0] )] = (max, sum);
			}
		}
		if ( clusters == 0 ) return null;

		// every bone counts (0 when no cluster names it); a bone may drive several meshes' clusters
		var result = boneName.Values.Distinct().ToDictionary( n => n, _ => (0.0, 0.0), StringComparer.Ordinal );
		foreach ( var c in connections.ChildrenNamed( "C" ).Concat( connections.ChildrenNamed( "Connect" ) ) )
		{
			// bone -> cluster ("OO", bone, cluster)
			if ( c.Properties.Count < 3 || c.Properties[0] is not "OO" ) continue;
			var from = Key( c.Properties[1] ); var to = Key( c.Properties[2] );
			if ( !clusterWeights.TryGetValue( to, out var w ) || !boneName.TryGetValue( from, out var name ) ) continue;
			var old = result[name];
			result[name] = (Math.Max( old.Item1, w.Max ), old.Item2 + w.Sum);
		}
		return result;
	}

	/// <summary>
	/// The skinned mesh's points per engine bone (index into <paramref name="names"/>), in the engine skeleton's rest
	/// space: the FBX files the vmdl renders, mapped onto the skeleton by the similarity that best fits the files' bind
	/// positions of the same bones onto the skeleton's rest heads. Null when no file could be read or fitted.
	/// </summary>
	public static Dictionary<int, IReadOnlyList<System.Numerics.Vector3>> PointsForVmdl( string vmdlText, Func<string, string> resolve,
		IReadOnlyList<string> names, IReadOnlyList<System.Numerics.Vector3> restHeads )
	{
		if ( string.IsNullOrEmpty( vmdlText ) || resolve is null ) return null;
		var index = new Dictionary<string, int>( StringComparer.Ordinal );
		for ( var i = 0; i < names.Count; i++ ) { index.TryAdd( names[i], i ); index.TryAdd( Formats.Fbx.FbxClipExport.EngineName( names[i] ), i ); }
		var result = new Dictionary<int, IReadOnlyList<System.Numerics.Vector3>>();
		foreach ( Match match in Regex.Matches( vmdlText, @"filename\s*=\s*""([^""]+\.fbx)""", RegexOptions.IgnoreCase ) )
		{
			var path = resolve( match.Groups[1].Value );
			if ( path is null || !File.Exists( path ) ) continue;
			(Dictionary<string, List<System.Numerics.Vector3>> Points, Dictionary<string, System.Numerics.Vector3> BindPositions)? found;
			try { found = MeshPoints( File.ReadAllBytes( path ) ); }
			catch ( Exception e ) when ( e is FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException or IOException ) { continue; }
			if ( found is not { } mesh ) continue;
			int Bone( string name ) => index.TryGetValue( name, out var i ) ? i : index.TryGetValue( Formats.Fbx.FbxClipExport.EngineName( name ), out i ) ? i : -1;
			var matched = mesh.BindPositions.Where( kv => Bone( kv.Key ) >= 0 ).ToList();
			if ( matched.Count < 3 ) continue;
			var (m, _, error) = Formats.Fbx.FbxClipExport.Similarity( matched.Select( kv => kv.Value ).ToList(), matched.Select( kv => restHeads[Bone( kv.Key )] ).ToList() );
			var size = restHeads.Max( h => h.Length() ) + 1e-3f;
			if ( !float.IsFinite( error ) || error > .05f * size ) continue; // not the skeleton's bind pose
			// the mesh must wrap the skeleton: a cloud far smaller or larger than it is in a space this reader got wrong
			var mapped = mesh.Points.Values.SelectMany( p => p ).Select( p => System.Numerics.Vector3.Transform( p, m ) ).ToList();
			if ( mapped.Count == 0 ) continue;
			float Extent( IEnumerable<System.Numerics.Vector3> v ) { var a = v.ToList(); var lo = a.Aggregate( System.Numerics.Vector3.Min ); var hi = a.Aggregate( System.Numerics.Vector3.Max ); return (hi - lo).Length(); }
			var ratio = Extent( mapped ) / MathF.Max( Extent( restHeads ), 1e-6f );
			if ( !(ratio > .5f && ratio < 4f) ) continue;
			foreach ( var (name, points) in mesh.Points )
			{
				var bone = Bone( name );
				if ( bone < 0 ) continue;
				var list = result.TryGetValue( bone, out var existing ) ? existing.ToList() : new List<System.Numerics.Vector3>();
				list.AddRange( points.Select( p => System.Numerics.Vector3.Transform( p, m ) ) );
				result[bone] = list;
			}
		}
		return result.Count > 0 ? result : null;
	}

	/// <summary>
	/// The skinned mesh as UniMate's Blender add-on samples it for collision capsules (rig.py mesh_capsules): every
	/// vertex assigned to the bone with its largest weight, when that weight is at least <paramref name="minWeight"/>,
	/// in the file's bind-pose global space; with each bone's bind position (to fit the file onto the engine skeleton).
	/// Null when the file has no skin.
	/// </summary>
	public static (Dictionary<string, List<System.Numerics.Vector3>> Points, Dictionary<string, System.Numerics.Vector3> BindPositions)? MeshPoints( byte[] fbx, double minWeight = .3 )
	{
		var root = Formats.Fbx.FbxTokenizer.Parse( fbx );
		var objects = root.Child( "Objects" );
		var connections = root.Child( "Connections" );
		if ( objects is null || connections is null ) return null;
		static string Key( object p ) => p switch { string s => s, null => "", _ => Convert.ToInt64( p ).ToString() };

		var nodes = new Dictionary<string, Formats.Fbx.FbxNode>();
		var boneName = new Dictionary<string, string>();
		var clusters = new List<string>();
		var skins = new HashSet<string>();
		foreach ( var node in objects.Children )
		{
			if ( node.Properties.Count == 0 ) continue;
			var key = Key( node.Properties[0] );
			nodes[key] = node;
			var kind = node.Properties.OfType<string>().LastOrDefault();
			var raw = node.Properties.Count >= 2 && node.Properties[1] is string n7 && node.Properties[0] is not string ? n7 : node.Properties[0] as string;
			if ( node.Name == "Model" && kind is "LimbNode" or "Limb" or "Root" or "Null" && raw is not null )
				boneName[key] = Formats.Fbx.FbxNode.SplitName( raw ).Name;
			else if ( node.Name == "Deformer" && kind == "Cluster" ) clusters.Add( key );
			else if ( node.Name == "Deformer" && kind == "Skin" ) skins.Add( key );
		}
		if ( clusters.Count == 0 ) return null;

		// links: bone -> cluster, cluster -> skin, skin -> geometry (FBX 7) or mesh model (FBX 6)
		var boneOfCluster = new Dictionary<string, string>();
		var skinOfCluster = new Dictionary<string, string>();
		var meshOfSkin = new Dictionary<string, string>();
		foreach ( var c in connections.ChildrenNamed( "C" ).Concat( connections.ChildrenNamed( "Connect" ) ) )
		{
			if ( c.Properties.Count < 3 || c.Properties[0] is not "OO" ) continue;
			var from = Key( c.Properties[1] ); var to = Key( c.Properties[2] );
			if ( boneName.ContainsKey( from ) && clusters.Contains( to ) ) boneOfCluster[to] = from;
			else if ( clusters.Contains( from ) && skins.Contains( to ) ) skinOfCluster[from] = to;
			else if ( skins.Contains( from ) ) meshOfSkin[from] = to;
		}

		static System.Numerics.Matrix4x4? MatrixOf( Formats.Fbx.FbxNode node )
		{
			if ( node is null ) return null;
			var v = Numbers( node ).ToArray();
			if ( v.Length != 16 ) return null;
			return new System.Numerics.Matrix4x4( (float)v[0], (float)v[1], (float)v[2], (float)v[3], (float)v[4], (float)v[5], (float)v[6], (float)v[7],
				(float)v[8], (float)v[9], (float)v[10], (float)v[11], (float)v[12], (float)v[13], (float)v[14], (float)v[15] );
		}

		// each skinned geometry's mesh node and that node's global bind transform (FBX 7: the scene graph - its bind pose
		// where recorded, else its default transforms up the hierarchy); exporters disagree on what a cluster's own
		// Transform holds, so it is only the fallback (FBX 6)
		var meshGlobal = new Dictionary<string, System.Numerics.Matrix4x4>();
		try
		{
			var scene = Formats.Fbx.FbxScene.Build( root );
			var transforms = new Dictionary<long, Formats.Fbx.FbxTransform>();
			System.Numerics.Matrix4x4 Global( Formats.Fbx.FbxObject model )
			{
				if ( scene.BindPose.TryGetValue( model.Id, out var pose ) ) return pose;
				if ( !transforms.TryGetValue( model.Id, out var t ) ) transforms[model.Id] = t = Formats.Fbx.FbxTransform.FromModel( scene, model );
				return t.LocalMatrixDefault() * (model.ModelParent is { } p ? Global( p ) : System.Numerics.Matrix4x4.Identity);
			}
			foreach ( var c in connections.ChildrenNamed( "C" ) )
			{
				if ( c.Properties.Count < 3 || c.Properties[0] is not "OO" ) continue;
				var from = Key( c.Properties[1] ); var to = Key( c.Properties[2] );
				if ( !meshOfSkin.ContainsValue( from ) || !long.TryParse( to, out var modelId ) ) continue;
				if ( scene.ObjectsById.TryGetValue( modelId, out var model ) && model.NodeType == "Model" ) meshGlobal[from] = Global( model );
			}
		}
		catch ( Exception e ) when ( e is FormatException or InvalidOperationException or InvalidCastException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException ) { }

		// per mesh vertex: its largest weight and that bone; its position in bind-pose global space
		var best = new Dictionary<(string Mesh, int Index), (double Weight, string Bone, System.Numerics.Vector3 Position)>();
		var bind = new Dictionary<string, System.Numerics.Vector3>();
		foreach ( var cluster in clusters )
		{
			if ( !boneOfCluster.TryGetValue( cluster, out var bone ) || !skinOfCluster.TryGetValue( cluster, out var skin ) || !meshOfSkin.TryGetValue( skin, out var mesh ) ) continue;
			var node = nodes[cluster];
			var name = boneName[bone];
			if ( MatrixOf( node.Child( "TransformLink" ) ) is { } link ) bind[name] = link.Translation;
			if ( !nodes.TryGetValue( mesh, out var meshNode ) || meshNode.Child( "Vertices" ) is not { } verticesNode ) continue;
			var vertices = Numbers( verticesNode ).ToArray();
			var transform = meshGlobal.TryGetValue( mesh, out var global ) ? global : MatrixOf( node.Child( "Transform" ) ) ?? System.Numerics.Matrix4x4.Identity;
			var indexes = node.Child( "Indexes" ) is { } i ? Numbers( i ).Select( x => (int)x ).ToArray() : Array.Empty<int>();
			var weights = node.Child( "Weights" ) is { } w ? Numbers( w ).ToArray() : Array.Empty<double>();
			for ( var k = 0; k < Math.Min( indexes.Length, weights.Length ); k++ )
			{
				var index = indexes[k];
				if ( index < 0 || index * 3 + 2 >= vertices.Length ) continue;
				var key = (mesh, index);
				if ( best.TryGetValue( key, out var old ) && old.Weight >= weights[k] ) continue;
				var local = new System.Numerics.Vector3( (float)vertices[index * 3], (float)vertices[index * 3 + 1], (float)vertices[index * 3 + 2] );
				best[key] = (weights[k], name, System.Numerics.Vector3.Transform( local, transform ));
			}
		}
		var points = new Dictionary<string, List<System.Numerics.Vector3>>( StringComparer.Ordinal );
		foreach ( var (_, (weight, bone, position)) in best )
		{
			if ( weight < minWeight ) continue;
			if ( !points.TryGetValue( bone, out var list ) ) points[bone] = list = new List<System.Numerics.Vector3>();
			list.Add( position );
		}
		return (points, bind);
	}

	/// <summary>Every number a node carries: one array (binary, ASCII 'a:' block) or a list of scalars (FBX 6 ASCII).</summary>
	static IEnumerable<double> Numbers( Formats.Fbx.FbxNode node )
	{
		foreach ( var p in node.Properties )
		{
			switch ( p )
			{
				case double[] d: foreach ( var v in d ) yield return v; break;
				case float[] f: foreach ( var v in f ) yield return v; break;
				case long[] l: foreach ( var v in l ) yield return v; break;
				case int[] n: foreach ( var v in n ) yield return v; break;
				case double v: yield return v; break;
				case float v: yield return v; break;
				case long v: yield return v; break;
				case int v: yield return v; break;
			}
		}
		var a = node.Child( "a" );
		if ( a is not null ) foreach ( var v in Numbers( a ) ) yield return v;
	}
}
