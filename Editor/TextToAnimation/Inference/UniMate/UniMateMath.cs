using System;
using System.Numerics;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>A 3x3 rotation matrix acting on column vectors (row-major storage), as in UniMate's numpy code.</summary>
public struct M3
{
	public float M00, M01, M02, M10, M11, M12, M20, M21, M22;

	public static M3 Identity => new() { M00 = 1, M11 = 1, M22 = 1 };

	public static M3 FromRows( float a, float b, float c, float d, float e, float f, float g, float h, float i )
		=> new() { M00 = a, M01 = b, M02 = c, M10 = d, M11 = e, M12 = f, M20 = g, M21 = h, M22 = i };

	public static M3 FromColumns( Vector3 x, Vector3 y, Vector3 z )
		=> FromRows( x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z );

	public Vector3 Column( int i ) => i switch
	{
		0 => new Vector3( M00, M10, M20 ),
		1 => new Vector3( M01, M11, M21 ),
		_ => new Vector3( M02, M12, M22 ),
	};

	public M3 Transposed => FromRows( M00, M10, M20, M01, M11, M21, M02, M12, M22 );

	public static Vector3 operator *( M3 m, Vector3 v ) => new(
		m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
		m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
		m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z );

	public static M3 operator *( M3 a, M3 b ) => FromColumns( a * b.Column( 0 ), a * b.Column( 1 ), a * b.Column( 2 ) );

	/// <summary>Rotation matrix of a (normalised) quaternion.</summary>
	public static M3 FromQuaternion( Quaternion q )
	{
		q = Quaternion.Normalize( q );
		float w = q.W, x = q.X, y = q.Y, z = q.Z;
		return FromRows(
			1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
			2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
			2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y) );
	}

	/// <summary>Shepperd's method with the same branch logic as UniMate's Motion library.</summary>
	public Quaternion ToQuaternion()
	{
		double d0 = M00, d1 = M11, d2 = M22;
		double qw = Math.Sqrt( Math.Max( 0, (1 + d0 + d1 + d2) / 4 ) );
		double qx = Math.Sqrt( Math.Max( 0, (1 + d0 - d1 - d2) / 4 ) );
		double qy = Math.Sqrt( Math.Max( 0, (1 - d0 + d1 - d2) / 4 ) );
		double qz = Math.Sqrt( Math.Max( 0, (1 - d0 - d1 + d2) / 4 ) );
		static double S( double v ) => Math.Sign( v );
		var max = Math.Max( Math.Max( qw, qx ), Math.Max( qy, qz ) );
		double w, x, y, z;
		if ( max == qw ) { w = qw; x = qx * S( M21 - M12 ); y = qy * S( M02 - M20 ); z = qz * S( M10 - M01 ); }
		else if ( max == qx ) { w = qw * S( M21 - M12 ); x = qx; y = qy * S( M10 + M01 ); z = qz * S( M02 + M20 ); }
		else if ( max == qy ) { w = qw * S( M02 - M20 ); x = qx * S( M10 + M01 ); y = qy; z = qz * S( M21 + M12 ); }
		else { w = qw * S( M10 - M01 ); x = qx * S( M20 + M02 ); y = qy * S( M21 + M12 ); z = qz; }
		return new Quaternion( (float)x, (float)y, (float)z, (float)w );
	}

	/// <summary>6D rotation: the first two columns.</summary>
	public void To6D( Span<float> d )
	{
		d[0] = M00; d[1] = M10; d[2] = M20;
		d[3] = M01; d[4] = M11; d[5] = M21;
	}

	/// <summary>Gram-Schmidt from a 6D rotation (columns x, y, z).</summary>
	public static M3 From6D( ReadOnlySpan<float> d )
	{
		var a = new Vector3( d[0], d[1], d[2] );
		var b = new Vector3( d[3], d[4], d[5] );
		var x = Normalize( a );
		var z = Normalize( Vector3.Cross( x, b ) );
		var y = Vector3.Cross( z, x );
		return FromColumns( x, y, z );
	}

	static Vector3 Normalize( Vector3 v )
	{
		var l = v.Length();
		return l > 1e-12f ? v / l : new Vector3( 1, 0, 0 );
	}
}

public static class UniMateMath
{
	/// <summary>Shortest rotation taking direction <paramref name="a"/> to <paramref name="b"/> (UniMate's q_between).</summary>
	public static Quaternion Between( Vector3 a, Vector3 b )
	{
		var axis = Vector3.Cross( a, b );
		var w = MathF.Sqrt( a.LengthSquared() * b.LengthSquared() ) + Vector3.Dot( a, b );
		return Quaternion.Normalize( new Quaternion( axis, w ) );
	}

	public static Quaternion Inverse( Quaternion q ) => Quaternion.Conjugate( q );

	/// <summary>Rotates a vector (q v q^-1).</summary>
	public static Vector3 Rotate( Quaternion q, Vector3 v ) => Vector3.Transform( v, q );

	/// <summary>Hemisphere-aligns q to <paramref name="reference"/>.</summary>
	public static Quaternion Align( Quaternion q, Quaternion reference ) => Quaternion.Dot( q, reference ) < 0 ? Quaternion.Negate( q ) : q;
}
