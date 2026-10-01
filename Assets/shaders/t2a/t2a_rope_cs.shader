HEADER
{
	DevShader = true;
	Description = "Text to Animation: rotary position embedding";
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
	// x cos + sign * x[perm] sin, one element per thread
	// P: count, d, positions, perm[d], sign[d]
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> X < Attribute( "X" ); >;
	StructuredBuffer<float> Cos < Attribute( "Cos" ); >;
	StructuredBuffer<float> Sin < Attribute( "Sin" ); >;
	RWStructuredBuffer<float> Out < Attribute( "Out" ); >;

	[numthreads( 256, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint e = id.x + id.y * 262144;
		if ( e >= (uint)P[0] ) return;
		int d = P[1];
		int row = (int)e / d;
		int i = (int)e % d;
		int ob = row * d;
		int pb = ( row % P[2] ) * d;
		Out[e] = X[ob + i] * Cos[pb + i] + (float)P[3 + d + i] * X[ob + P[3 + i]] * Sin[pb + i];
	}
}
