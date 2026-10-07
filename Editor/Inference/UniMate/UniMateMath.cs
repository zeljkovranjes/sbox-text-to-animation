using System;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

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
