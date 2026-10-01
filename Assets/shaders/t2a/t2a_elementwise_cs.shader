HEADER
{
	DevShader = true;
	Description = "Text to Animation: broadcast elementwise";
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
	// P: count, mode, shape[6], strideA[6], strideB[6], strideC[6]
	// mode: 0 add, 1 mul, 2 a*b+c, 3 silu, 4 sin, 5 cos, 6 sub, 7 div
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> A < Attribute( "A" ); >;
	StructuredBuffer<float> B < Attribute( "B" ); >;
	StructuredBuffer<float> C < Attribute( "C" ); >;
	RWStructuredBuffer<float> Out < Attribute( "Out" ); >;

	[numthreads( 256, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint i = id.x + id.y * 262144;
		if ( i >= (uint)P[0] ) return;
		int rem = (int)i;
		int ia = 0;
		int ib = 0;
		int ic = 0;
		[unroll]
		for ( int k = 5; k >= 0; k-- )
		{
			int n = P[2 + k];
			int q = rem % n;
			rem /= n;
			ia += q * P[8 + k];
			ib += q * P[14 + k];
			ic += q * P[20 + k];
		}
		float a = A[ia];
		int mode = P[1];
		float r;
		if ( mode == 0 ) r = a + B[ib];
		else if ( mode == 1 ) r = a * B[ib];
		else if ( mode == 2 ) r = a * B[ib] + C[ic];
		else if ( mode == 3 ) r = a / ( 1.0 + exp( -a ) );
		else if ( mode == 4 ) r = sin( a );
		else if ( mode == 5 ) r = cos( a );
		else if ( mode == 6 ) r = a - B[ib];
		else r = a / B[ib];
		Out[i] = r;
	}
}
