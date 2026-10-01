HEADER
{
	DevShader = true;
	Description = "Text to Animation: strided copy (transpose, slice, concat, split, expand)";
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
	// P: count, srcBase, dstBase, shape[6], srcStride[6], dstStride[6]
	StructuredBuffer<int> P < Attribute( "P" ); >;
	StructuredBuffer<float> Src < Attribute( "Src" ); >;
	RWStructuredBuffer<float> Dst < Attribute( "Dst" ); >;

	[numthreads( 256, 1, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint i = id.x + id.y * 262144;
		if ( i >= (uint)P[0] ) return;
		int rem = (int)i;
		int s = P[1];
		int d = P[2];
		[unroll]
		for ( int k = 5; k >= 0; k-- )
		{
			int n = P[3 + k];
			int c = rem % n;
			rem /= n;
			s += c * P[9 + k];
			d += c * P[15 + k];
		}
		Dst[d] = Src[s];
	}
}
