HEADER
{
	DevShader = true;
	Description = "Text to Animation: attention (online softmax)";
}

MODES
{
	Default();
}

FEATURES
{
}

COMMON
{
	#include "common/shared.hlsl"
}

CS
{
	// one query row per thread: softmax(q K^T scale + mask) V with a running max (any key count)
	// P: rows, Hq, Hk, Sq, Sk, scale bits, hasMask, maskN, maskH, D (<= 128, a multiple of 4)
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> Q < Attribute( "Q" ); >;
	StructuredBuffer<float> Kb < Attribute( "Kb" ); >;
	StructuredBuffer<float> V < Attribute( "V" ); >;
	StructuredBuffer<float> Mask < Attribute( "Mask" ); >;
	RWStructuredBuffer<float> Out < Attribute( "Out" ); >;

	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint g = id.x + id.y * 65536;
		if ( g >= (uint)P[0] ) return;
		int Hq = P[1];
		int Hk = P[2];
		int Sq = P[3];
		int Sk = P[4];
		float scale = asfloat( P[5] );
		int D = P[9];
		int D4 = D / 4;
		int i = (int)g % Sq;
		int nh = (int)g / Sq;
		int n = nh / Hq;
		int h = nh % Hq;
		int hk = h / ( Hq / Hk );
		int qb = (int)g * D;
		int kb = ( n * Hk + hk ) * Sk * D;
		int mrow = n * P[7] + h * P[8] + i * Sk;

		float4 q[32];
		float4 acc[32];
		[unroll]
		for ( int c = 0; c < 32; c++ )
		{
			acc[c] = 0;
			q[c] = c < D4 ? float4( Q[qb + 4 * c], Q[qb + 4 * c + 1], Q[qb + 4 * c + 2], Q[qb + 4 * c + 3] ) : 0;
		}
		float mx = -3.402823466e+38;
		bool any = false;
		float l = 0;
		for ( int j = 0; j < Sk; j++ )
		{
			int kr = kb + j * D;
			float s = 0;
			[unroll]
			for ( int c2 = 0; c2 < 32; c2++ )
				if ( c2 < D4 ) s += dot( q[c2], float4( Kb[kr + 4 * c2], Kb[kr + 4 * c2 + 1], Kb[kr + 4 * c2 + 2], Kb[kr + 4 * c2 + 3] ) );
			s *= scale;
			if ( P[6] != 0 ) s += Mask[mrow + j];
			if ( s < -3.0e+38 ) continue; // a masked key (-inf)
			float mn = any ? max( mx, s ) : s;
			float corr = any ? exp( mx - mn ) : 0;
			float e = exp( s - mn );
			l = l * corr + e;
			[unroll]
			for ( int c3 = 0; c3 < 32; c3++ )
				if ( c3 < D4 ) acc[c3] = acc[c3] * corr + e * float4( V[kr + 4 * c3], V[kr + 4 * c3 + 1], V[kr + 4 * c3 + 2], V[kr + 4 * c3 + 3] );
			mx = mn;
			any = true;
		}
		float inv = l > 0 ? 1.0 / l : 0;
		[unroll]
		for ( int c4 = 0; c4 < 32; c4++ )
		{
			if ( c4 < D4 )
			{
				float4 r = acc[c4] * inv;
				Out[qb + 4 * c4] = r.x;
				Out[qb + 4 * c4 + 1] = r.y;
				Out[qb + 4 * c4 + 2] = r.z;
				Out[qb + 4 * c4 + 3] = r.w;
			}
		}
	}
}
