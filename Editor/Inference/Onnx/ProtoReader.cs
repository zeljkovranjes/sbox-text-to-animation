using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

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
