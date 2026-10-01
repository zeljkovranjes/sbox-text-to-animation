using System;
using System.Linq;

namespace TextToAnimation.Editor.Inference.Onnx;

/// <summary>
/// A dense row-major tensor for the managed ONNX interpreter. Element storage is one of float (all floating
/// types are computed in fp32), long (all integer types) or bool.
/// </summary>
public sealed class Tensor
{
	public OnnxType Type { get; }
	public int[] Shape { get; }
	public float[] F { get; }
	public long[] L { get; }
	public bool[] B { get; }

	public int Rank => Shape.Length;
	public int Length { get; }

	Tensor( OnnxType type, int[] shape, float[] f, long[] l, bool[] b )
	{
		Type = type;
		Shape = shape;
		F = f; L = l; B = b;
		Length = SizeOf( shape );
		var actual = f?.Length ?? l?.Length ?? b?.Length ?? 0;
		if ( actual != Length ) throw new ArgumentException( $"Tensor data length {actual} doesn't match shape [{string.Join( ",", shape )}]." );
	}

	public static Tensor Float( int[] shape, float[] data = null ) => new( OnnxType.Float, shape, data ?? new float[SizeOf( shape )], null, null );
	public static Tensor Int64( int[] shape, long[] data = null ) => new( OnnxType.Int64, shape, null, data ?? new long[SizeOf( shape )], null );
	public static Tensor Bool( int[] shape, bool[] data = null ) => new( OnnxType.Bool, shape, null, null, data ?? new bool[SizeOf( shape )] );
	public static Tensor Scalar( float v ) => Float( Array.Empty<int>(), new[] { v } );
	public static Tensor ScalarInt( long v ) => Int64( Array.Empty<int>(), new[] { v } );
	public static Tensor Vector( params long[] v ) => Int64( new[] { v.Length }, v );

	public bool IsFloat => F is not null;
	public bool IsInt => L is not null;
	public bool IsBool => B is not null;

	public Tensor WithShape( int[] shape )
	{
		if ( SizeOf( shape ) != Length ) throw new ArgumentException( $"Can't reshape [{string.Join( ",", Shape )}] to [{string.Join( ",", shape )}]." );
		return new Tensor( Type, shape, F, L, B );
	}

	/// <summary>Values as long (ints, or truncated floats) - for shape/index inputs.</summary>
	public long[] AsLongs() => L ?? F?.Select( v => (long)v ).ToArray() ?? B!.Select( v => v ? 1L : 0L ).ToArray();
	public float[] AsFloats() => F ?? L?.Select( v => (float)v ).ToArray() ?? B!.Select( v => v ? 1f : 0f ).ToArray();

	public static int SizeOf( int[] shape )
	{
		long n = 1;
		foreach ( var d in shape )
		{
			if ( d < 0 ) throw new ArgumentException( "Negative dimension." );
			n *= d;
		}
		if ( n > int.MaxValue ) throw new ArgumentException( "Tensor too large." );
		return (int)n;
	}

	public static int[] Strides( int[] shape )
	{
		var s = new int[shape.Length];
		var acc = 1;
		for ( var i = shape.Length - 1; i >= 0; i-- ) { s[i] = acc; acc *= shape[i]; }
		return s;
	}

	public override string ToString() => $"{Type}[{string.Join( ",", Shape )}]";

	/// <summary>Builds a tensor from a model initializer (external data is read from <paramref name="baseDir"/>).</summary>
	public static Tensor FromInitializer( OnnxInitializer init, string baseDir )
	{
		var shape = init.Dims.Select( d => checked((int)d) ).ToArray();
		var count = SizeOf( shape );
		byte[] raw = init.Raw;
		if ( init.ExternalLocation is { } location )
		{
			var path = System.IO.Path.Combine( baseDir, location );
			var bytes = ElementSize( init.Type ) * (long)count;
			var length = init.ExternalLength >= 0 ? init.ExternalLength : bytes;
			raw = new byte[length];
			using var fs = new System.IO.FileStream( path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1 << 16 );
			fs.Seek( init.ExternalOffset, System.IO.SeekOrigin.Begin );
			var read = 0;
			while ( read < raw.Length )
			{
				var n = fs.Read( raw, read, raw.Length - read );
				if ( n <= 0 ) throw new System.IO.EndOfStreamException( $"External data for {init.Name} is truncated." );
				read += n;
			}
		}
		switch ( init.Type )
		{
			case OnnxType.Float:
				if ( raw is not null ) { var f = new float[count]; Buffer.BlockCopy( raw, 0, f, 0, count * 4 ); return Float( shape, f ); }
				return Float( shape, init.FloatData ?? new float[count] );
			case OnnxType.Double:
			{
				var f = new float[count];
				for ( var i = 0; i < count; i++ ) f[i] = (float)BitConverter.ToDouble( raw, i * 8 );
				return Float( shape, f );
			}
			case OnnxType.Float16:
			{
				var f = new float[count];
				if ( raw is not null ) for ( var i = 0; i < count; i++ ) f[i] = (float)BitConverter.UInt16BitsToHalf( BitConverter.ToUInt16( raw, i * 2 ) );
				else for ( var i = 0; i < count; i++ ) f[i] = (float)BitConverter.UInt16BitsToHalf( (ushort)init.Int32Data[i] );
				return Float( shape, f );
			}
			case OnnxType.BFloat16:
			{
				var f = new float[count];
				for ( var i = 0; i < count; i++ )
				{
					var bits = raw is not null ? BitConverter.ToUInt16( raw, i * 2 ) : (ushort)init.Int32Data[i];
					f[i] = BitConverter.Int32BitsToSingle( bits << 16 );
				}
				return Float( shape, f );
			}
			case OnnxType.Int64:
				if ( raw is not null ) { var l = new long[count]; Buffer.BlockCopy( raw, 0, l, 0, count * 8 ); return Int64( shape, l ); }
				return Int64( shape, init.Int64Data ?? new long[count] );
			case OnnxType.Int32:
			case OnnxType.Int8:
			case OnnxType.UInt8:
			{
				var l = new long[count];
				if ( raw is not null )
					for ( var i = 0; i < count; i++ )
						l[i] = init.Type == OnnxType.Int32 ? BitConverter.ToInt32( raw, i * 4 ) : init.Type == OnnxType.Int8 ? (sbyte)raw[i] : raw[i];
				else if ( init.Int32Data is not null )
					for ( var i = 0; i < count; i++ ) l[i] = init.Int32Data[i];
				return Int64( shape, l );
			}
			case OnnxType.Bool:
			{
				var b = new bool[count];
				if ( raw is not null ) for ( var i = 0; i < count; i++ ) b[i] = raw[i] != 0;
				else if ( init.Int32Data is not null ) for ( var i = 0; i < count; i++ ) b[i] = init.Int32Data[i] != 0;
				return Bool( shape, b );
			}
			default:
				throw new NotSupportedException( $"Initializer {init.Name} has unsupported type {init.Type}." );
		}
	}

	public static int ElementSize( OnnxType type ) => type switch
	{
		OnnxType.Float or OnnxType.Int32 => 4,
		OnnxType.Double or OnnxType.Int64 => 8,
		OnnxType.Float16 or OnnxType.BFloat16 => 2,
		_ => 1,
	};
}
