using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.Editor.Inference.Onnx;

/// <summary>ONNX tensor element types used here (TensorProto.DataType).</summary>
public enum OnnxType { Undefined = 0, Float = 1, UInt8 = 2, Int8 = 3, Int32 = 6, Int64 = 7, String = 8, Bool = 9, Float16 = 10, Double = 11, BFloat16 = 16 }

/// <summary>A node attribute.</summary>
public sealed class OnnxAttribute
{
	public string Name = "";
	public long? Int;
	public float? Float;
	public string String;
	public long[] Ints = Array.Empty<long>();
	public float[] Floats = Array.Empty<float>();
	public OnnxInitializer Tensor;
	public OnnxGraphProto Graph;
}

public sealed class OnnxNode
{
	public string Name = "";
	public string OpType = "";
	public string Domain = "";
	public string[] Inputs = Array.Empty<string>();
	public string[] Outputs = Array.Empty<string>();
	public Dictionary<string, OnnxAttribute> Attributes = new( StringComparer.Ordinal );

	public long GetInt( string name, long fallback ) => Attributes.TryGetValue( name, out var a ) && a.Int is long v ? v : fallback;
	public float GetFloat( string name, float fallback ) => Attributes.TryGetValue( name, out var a ) && a.Float is float v ? v : fallback;
	public string GetString( string name, string fallback ) => Attributes.TryGetValue( name, out var a ) && a.String is { } v ? v : fallback;
	public long[] GetInts( string name ) => Attributes.TryGetValue( name, out var a ) ? a.Ints : null;
	public override string ToString() => $"{OpType} '{Name}'";
}

/// <summary>A constant tensor in the model file: inline bytes or a slice of an external data file.</summary>
public sealed class OnnxInitializer
{
	public string Name = "";
	public OnnxType Type;
	public long[] Dims = Array.Empty<long>();
	public byte[] Raw;
	public float[] FloatData;
	public long[] Int64Data;
	public int[] Int32Data;
	public string ExternalLocation;
	public long ExternalOffset;
	public long ExternalLength = -1;
}

public sealed class OnnxValueInfo
{
	public string Name = "";
	public OnnxType Type;
	/// <summary>Dimensions; -1 for symbolic.</summary>
	public long[] Dims = Array.Empty<long>();
}

public sealed class OnnxGraphProto
{
	public string Name = "";
	public List<OnnxNode> Nodes = new();
	public List<OnnxInitializer> Initializers = new();
	public List<OnnxValueInfo> Inputs = new();
	public List<OnnxValueInfo> Outputs = new();
}

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

/// <summary>Minimal protobuf wire-format reader over a byte range.</summary>
public struct ProtoReader
{
	readonly byte[] _data;
	int _pos;
	readonly int _end;

	public ProtoReader( byte[] data, int start, int end ) { _data = data; _pos = start; _end = end; }

	public bool End => _pos >= _end;

	public bool Next( out int field, out int wire )
	{
		if ( _pos >= _end ) { field = 0; wire = 0; return false; }
		var key = Varint();
		field = (int)(key >> 3);
		wire = (int)(key & 7);
		return true;
	}

	public ulong Varint()
	{
		ulong result = 0;
		for ( var shift = 0; shift < 64; shift += 7 )
		{
			if ( _pos >= _end ) throw new InvalidDataException( "Truncated ONNX file (varint)." );
			var b = _data[_pos++];
			result |= (ulong)(b & 0x7F) << shift;
			if ( (b & 0x80) == 0 ) return result;
		}
		throw new InvalidDataException( "Malformed varint in ONNX file." );
	}

	int Length()
	{
		var len = (long)Varint();
		if ( len < 0 || _pos + len > _end ) throw new InvalidDataException( "Truncated ONNX file (length)." );
		return (int)len;
	}

	public ProtoReader Sub()
	{
		var len = Length();
		var sub = new ProtoReader( _data, _pos, _pos + len );
		_pos += len;
		return sub;
	}

	public byte[] Bytes()
	{
		var len = Length();
		var b = new byte[len];
		Buffer.BlockCopy( _data, _pos, b, 0, len );
		_pos += len;
		return b;
	}

	public string String()
	{
		var len = Length();
		var s = Encoding.UTF8.GetString( _data, _pos, len );
		_pos += len;
		return s;
	}

	public float Fixed32Float() => RawFloat();

	public float RawFloat()
	{
		if ( _pos + 4 > _end ) throw new InvalidDataException( "Truncated ONNX file (float)." );
		var v = BitConverter.ToSingle( _data, _pos );
		_pos += 4;
		return v;
	}

	public void Skip( int wire )
	{
		switch ( wire )
		{
			case 0: Varint(); break;
			case 1: _pos += 8; break;
			case 2: { var len = Length(); _pos += len; break; }
			case 5: _pos += 4; break;
			default: throw new InvalidDataException( $"Unsupported protobuf wire type {wire}." );
		}
		if ( _pos > _end ) throw new InvalidDataException( "Truncated ONNX file." );
	}
}
