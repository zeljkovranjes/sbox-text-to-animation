HEADER
{
	DevShader = true;
	Description = "Text to Animation: RMS normalisation";
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
	// one 64-thread group per row: x / sqrt(mean(x^2) + eps) * g
	// P: rows, norm, eps bits, scale length (norm or 1)
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> X < Attribute( "X" ); >;
	StructuredBuffer<float> G < Attribute( "G" ); >;
	RWStructuredBuffer<float> Out < Attribute( "Out" ); >;

	groupshared float Red[64];

	[numthreads( 64, 1, 1 )]
	void MainCs( uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID )
	{
		int row = gid.x + gid.y * 1024;
		if ( row >= P[0] ) return;
		int n = P[1];
		int b = row * n;
		float ss = 0;
		for ( int i = tid.x; i < n; i += 64 )
		{
			float v = X[b + i];
			ss += v * v;
		}
		Red[tid.x] = ss;
		GroupMemoryBarrierWithGroupSync();
		[unroll]
		for ( int s = 32; s > 0; s >>= 1 )
		{
			if ( (int)tid.x < s ) Red[tid.x] += Red[tid.x + s];
			GroupMemoryBarrierWithGroupSync();
		}
		float inv = 1.0 / sqrt( Red[0] / n + asfloat( P[2] ) );
		bool one = P[3] == 1;
		for ( int i2 = tid.x; i2 < n; i2 += 64 ) Out[b + i2] = X[b + i2] * inv * G[one ? 0 : i2];
	}
}
