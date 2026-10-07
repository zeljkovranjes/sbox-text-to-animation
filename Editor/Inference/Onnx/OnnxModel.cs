using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>
/// Reads an ONNX ModelProto (protobuf wire format) without any dependency: the graph, its initializers
/// (inline or external data) and value infos. Only the fields the interpreter uses are decoded.
/// </summary>
public sealed class OnnxModel
{
	public long IrVersion;
	public long Opset;
	public string Producer = "";
	public OnnxGraphProto Graph = new();
	/// <summary>Folder that external data locations are relative to.</summary>
	public string BaseDirectory = "";

	public static OnnxModel Load( string path )
	{
		var model = Parse( File.ReadAllBytes( path ) );
		model.BaseDirectory = Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? "";
		return model;
	}

	public static OnnxModel Parse( byte[] bytes )
	{
		var model = new OnnxModel();
		var r = new ProtoReader( bytes, 0, bytes.Length );
		while ( r.Next( out var field, out var wire ) )
		{
			switch ( field )
			{
				case 1: model.IrVersion = (long)r.Varint(); break;
				case 2: model.Producer = r.String(); break;
				case 7: model.Graph = ReadGraph( r.Sub() ); break;
				case 8:
				{
					var sub = r.Sub();
					string domain = "";
					long version = 0;
					while ( sub.Next( out var f, out var w ) )
					{
						if ( f == 1 ) domain = sub.String();
						else if ( f == 2 ) version = (long)sub.Varint();
						else sub.Skip( w );
					}
					if ( domain is "" or "ai.onnx" ) model.Opset = version;
					break;
				}
				default: r.Skip( wire ); break;
			}
		}
		return model;
	}

	static OnnxGraphProto ReadGraph( ProtoReader r )
	{
		var g = new OnnxGraphProto();
		while ( r.Next( out var field, out var wire ) )
		{
			switch ( field )
			{
				case 1: g.Nodes.Add( ReadNode( r.Sub() ) ); break;
				case 2: g.Name = r.String(); break;
				case 5: g.Initializers.Add( ReadTensor( r.Sub() ) ); break;
				case 11: g.Inputs.Add( ReadValueInfo( r.Sub() ) ); break;
				case 12: g.Outputs.Add( ReadValueInfo( r.Sub() ) ); break;
				default: r.Skip( wire ); break;
			}
		}
		return g;
	}

	static OnnxNode ReadNode( ProtoReader r )
	{
		var n = new OnnxNode();
		var inputs = new List<string>();
		var outputs = new List<string>();
		while ( r.Next( out var field, out var wire ) )
		{
			switch ( field )
			{
				case 1: inputs.Add( r.String() ); break;
				case 2: outputs.Add( r.String() ); break;
				case 3: n.Name = r.String(); break;
				case 4: n.OpType = r.String(); break;
				case 5: { var a = ReadAttribute( r.Sub() ); n.Attributes[a.Name] = a; break; }
				case 7: n.Domain = r.String(); break;
				default: r.Skip( wire ); break;
			}
		}
		n.Inputs = inputs.ToArray();
		n.Outputs = outputs.ToArray();
		return n;
	}

	static OnnxAttribute ReadAttribute( ProtoReader r )
	{
		var a = new OnnxAttribute();
		var ints = new List<long>();
		var floats = new List<float>();
		while ( r.Next( out var field, out var wire ) )
		{
			switch ( field )
			{
				case 1: a.Name = r.String(); break;
				case 2: a.Float = r.Fixed32Float(); break;
				case 3: a.Int = (long)r.Varint(); break;
				case 4: a.String = r.String(); break;
				case 5: a.Tensor = ReadTensor( r.Sub() ); break;
				case 6: a.Graph = ReadGraph( r.Sub() ); break;
				case 7:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) floats.Add( p.RawFloat() ); }
					else floats.Add( r.Fixed32Float() );
					break;
				case 8:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) ints.Add( (long)p.Varint() ); }
					else ints.Add( (long)r.Varint() );
					break;
				default: r.Skip( wire ); break;
			}
		}
		a.Ints = ints.ToArray();
		a.Floats = floats.ToArray();
		return a;
	}

	static OnnxInitializer ReadTensor( ProtoReader r )
	{
		var t = new OnnxInitializer();
		var dims = new List<long>();
		var floats = new List<float>();
		var int64s = new List<long>();
		var int32s = new List<int>();
		var dataLocationExternal = false;
		while ( r.Next( out var field, out var wire ) )
		{
			switch ( field )
			{
				case 1:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) dims.Add( (long)p.Varint() ); }
					else dims.Add( (long)r.Varint() );
					break;
				case 2: t.Type = (OnnxType)(int)r.Varint(); break;
				case 4:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) floats.Add( p.RawFloat() ); }
					else floats.Add( r.Fixed32Float() );
					break;
				case 5:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) int32s.Add( (int)(long)p.Varint() ); }
					else int32s.Add( (int)(long)r.Varint() );
					break;
				case 7:
					if ( wire == 2 ) { var p = r.Sub(); while ( !p.End ) int64s.Add( (long)p.Varint() ); }
					else int64s.Add( (long)r.Varint() );
					break;
				case 8: t.Name = r.String(); break;
				case 9: t.Raw = r.Bytes(); break;
				case 13:
				{
					var e = r.Sub();
					string key = null, value = null;
					while ( e.Next( out var f, out var w ) )
					{
						if ( f == 1 ) key = e.String();
						else if ( f == 2 ) value = e.String();
						else e.Skip( w );
					}
					if ( key == "location" ) t.ExternalLocation = value;
					else if ( key == "offset" ) t.ExternalOffset = long.Parse( value, System.Globalization.CultureInfo.InvariantCulture );
					else if ( key == "length" ) t.ExternalLength = long.Parse( value, System.Globalization.CultureInfo.InvariantCulture );
					break;
				}
				case 14: dataLocationExternal = r.Varint() == 1; break;
				default: r.Skip( wire ); break;
			}
		}
		t.Dims = dims.ToArray();
		if ( floats.Count > 0 ) t.FloatData = floats.ToArray();
		if ( int64s.Count > 0 ) t.Int64Data = int64s.ToArray();
		if ( int32s.Count > 0 ) t.Int32Data = int32s.ToArray();
		if ( !dataLocationExternal ) t.ExternalLocation = null;
		return t;
	}

	static OnnxValueInfo ReadValueInfo( ProtoReader r )
	{
		var v = new OnnxValueInfo();
		while ( r.Next( out var field, out var wire ) )
		{
			if ( field == 1 ) v.Name = r.String();
			else if ( field == 2 )
			{
				// TypeProto -> tensor_type(1) -> elem_type(1), shape(2) -> dim(1) -> dim_value(1) | dim_param(2)
				var type = r.Sub();
				while ( type.Next( out var tf, out var tw ) )
				{
					if ( tf != 1 ) { type.Skip( tw ); continue; }
					var tensor = type.Sub();
					var dims = new List<long>();
					while ( tensor.Next( out var f, out var w ) )
					{
						if ( f == 1 ) v.Type = (OnnxType)(int)tensor.Varint();
						else if ( f == 2 )
						{
							var shape = tensor.Sub();
							while ( shape.Next( out var sf, out var sw ) )
							{
								if ( sf != 1 ) { shape.Skip( sw ); continue; }
								var dim = shape.Sub();
								long value = -1;
								while ( dim.Next( out var df, out var dw ) )
								{
									if ( df == 1 ) value = (long)dim.Varint();
									else dim.Skip( dw );
								}
								dims.Add( value );
							}
						}
						else tensor.Skip( w );
					}
					v.Dims = dims.ToArray();
				}
			}
			else r.Skip( wire );
		}
		return v;
	}
}
