HEADER
{
	DevShader = true;
	Description = "Text to Animation: matrix multiply with bias";
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
	// C[M,N] = A[M,K] x B[K,N] (+ bias[N]); a 64x64 tile per group, 4x4 outputs per thread
	// P: M, K, N, hasBias
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> A < Attribute( "A" ); >;
	StructuredBuffer<float> Bm < Attribute( "Bm" ); >;
	StructuredBuffer<float> GemmBias < Attribute( "GemmBias" ); >;
	RWStructuredBuffer<float> Out < Attribute( "Out" ); >;

	groupshared float As[16][64];
	groupshared float Bs[16][64];

	[numthreads( 16, 16, 1 )]
	void MainCs( uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID )
	{
		int M = P[0];
		int K = P[1];
		int N = P[2];
		int m0 = gid.y * 64;
		int n0 = gid.x * 64;
		int t = tid.y * 16 + tid.x;
		float acc[4][4];
		[unroll] for ( int r = 0; r < 4; r++ ) [unroll] for ( int c = 0; c < 4; c++ ) acc[r][c] = 0;

		for ( int k0 = 0; k0 < K; k0 += 16 )
		{
			[unroll]
			for ( int l = 0; l < 4; l++ )
			{
				int idx = t + l * 256;
				int am = idx / 16;
				int ak = idx % 16;
				As[ak][am] = ( m0 + am < M && k0 + ak < K ) ? A[( m0 + am ) * K + k0 + ak] : 0;
				int bk = idx / 64;
				int bn = idx % 64;
				Bs[bk][bn] = ( k0 + bk < K && n0 + bn < N ) ? Bm[( k0 + bk ) * N + n0 + bn] : 0;
			}
			GroupMemoryBarrierWithGroupSync();
			[unroll]
			for ( int kk = 0; kk < 16; kk++ )
			{
				float a[4];
				float b[4];
				[unroll] for ( int r2 = 0; r2 < 4; r2++ ) a[r2] = As[kk][tid.y + 16 * r2];
				[unroll] for ( int c2 = 0; c2 < 4; c2++ ) b[c2] = Bs[kk][tid.x + 16 * c2];
				[unroll] for ( int r3 = 0; r3 < 4; r3++ ) [unroll] for ( int c3 = 0; c3 < 4; c3++ ) acc[r3][c3] = mad( a[r3], b[c3], acc[r3][c3] );
			}
			GroupMemoryBarrierWithGroupSync();
		}
		[unroll]
		for ( int r4 = 0; r4 < 4; r4++ )
		{
			int m = m0 + tid.y + 16 * r4;
			[unroll]
			for ( int c4 = 0; c4 < 4; c4++ )
			{
				int n = n0 + tid.x + 16 * c4;
				if ( m < M && n < N ) Out[m * N + n] = acc[r4][c4] + ( P[3] != 0 ? GemmBias[n] : 0 );
			}
		}
	}
}
