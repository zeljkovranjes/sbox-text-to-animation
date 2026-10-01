using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TextToAnimation.Editor.Engine;

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
