using System;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

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
		// Upstream (Motion's Quaternions.from_transforms) takes every component's magnitude from the diagonal and its
		// sign from the off-diagonals - exact in float64, but sqrt of a near-zero difference turns float32 rounding
		// (1e-7) into ~3e-4 for a small component. Same branch choice and signs, the other components from the
		// off-diagonals (the identical rotation in exact arithmetic, well conditioned in single precision).
		var max = Math.Max( Math.Max( qw, qx ), Math.Max( qy, qz ) );
		double w, x, y, z;
		var k = 1.0 / (4 * max);
		if ( max == qw ) { w = qw; x = (M21 - M12) * k; y = (M02 - M20) * k; z = (M10 - M01) * k; }
		else if ( max == qx ) { x = qx; w = (M21 - M12) * k; y = (M10 + M01) * k; z = (M02 + M20) * k; }
		else if ( max == qy ) { y = qy; w = (M02 - M20) * k; x = (M10 + M01) * k; z = (M21 + M12) * k; }
		else { z = qz; w = (M10 - M01) * k; x = (M20 + M02) * k; y = (M21 + M12) * k; }
		var n = Math.Sqrt( w * w + x * x + y * y + z * z );
		return new Quaternion( (float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n) );
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
