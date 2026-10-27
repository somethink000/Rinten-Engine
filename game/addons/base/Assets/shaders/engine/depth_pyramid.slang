//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Builds six depth pyramid levels per dispatch: each texel the farthest of the texels under it, read one by one

[[vk::binding(0,1)]] Sampler2D g_below;
[[vk::binding(1,1)]] RWTexture2D<float> g_level0;
[[vk::binding(2,1)]] RWTexture2D<float> g_level1;
[[vk::binding(3,1)]] RWTexture2D<float> g_level2;
[[vk::binding(4,1)]] RWTexture2D<float> g_level3;
[[vk::binding(5,1)]] RWTexture2D<float> g_level4;
[[vk::binding(6,1)]] RWTexture2D<float> g_level5;

[[vk::push_constant]]
cbuffer Push
{
	uint g_wide;
	uint g_tall;
	uint g_count;
	uint g_fromDepth;
	uint g_sourceWide;
	uint g_sourceTall;
	uint g_sourceLevel;

	//Source region origin and size; differ from the picture only when depth is on the screen buffer
	uint g_sourceX;
	uint g_sourceY;
	uint g_imageWide;
	uint g_imageTall;

	//What of the first level the depth fills; texels past it are far, so a level halves the one below exactly
	uint g_fillWide;
	uint g_fillTall;
};

groupshared float s_tile[32][32];

//The farthest depth below one texel of the first level: the texels of the source its footprint
//covers - a pair each way from the level below, whatever the depth's size is to the pyramid's.
float Farthest( uint2 at )
{
	//A level exactly half its source each way: the four texels under it, each read, the farthest kept. Not one filtered read -
	//a sampler that averages them says a gap past a near edge is near, and what shows through it is culled. The edge clamps
	//where the source is odd, which is the last texel twice - the same answer.
	bool halved = g_sourceWide <= 2u * g_fillWide && g_sourceWide + 1u >= 2u * g_fillWide &&
		g_sourceTall <= 2u * g_fillTall && g_sourceTall + 1u >= 2u * g_fillTall;

	if ( halved )
	{
		uint2 last = uint2( g_sourceWide, g_sourceTall ) - 1u;
		uint2 low = min( at * 2u, last );
		uint2 high = min( at * 2u + 1u, last );
		int2 origin = int2( g_sourceX, g_sourceY );
		int level = int( g_sourceLevel );

		return max(
			max( g_below.Load( int3( origin + int2( low ), level ) ).x, g_below.Load( int3( origin + int2( high.x, low.y ), level ) ).x ),
			max( g_below.Load( int3( origin + int2( low.x, high.y ), level ) ).x, g_below.Load( int3( origin + int2( high ), level ) ).x ) );
	}

	float2 scale = float2( g_sourceWide, g_sourceTall ) / float2( g_fillWide, g_fillTall );
	uint2 from = uint2( floor( float2( at ) * scale ) );
	uint2 to = min( uint2( ceil( float2( at + 1 ) * scale ) ), uint2( g_sourceWide, g_sourceTall ) );
	to = max( to, from + 1 );
	to = min( to, from + 8 );

	float farthest = 0.0;
	for ( uint y = from.y; y < to.y; y++ )
		for ( uint x = from.x; x < to.x; x++ )
			farthest = max( farthest,
				g_below.Load( int3( g_sourceX + x, g_sourceY + y, g_sourceLevel ) ).x );

	return farthest;
}

void Store( uint level, uint2 at, float value )
{
	if ( level == 1 ) g_level1[at] = value;
	else if ( level == 2 ) g_level2[at] = value;
	else if ( level == 3 ) g_level3[at] = value;
	else if ( level == 4 ) g_level4[at] = value;
	else g_level5[at] = value;
}

[shader("compute")]
[numthreads(16, 16, 1)]
void main( uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID )
{
	uint2 local = thread.xy * 2;
	uint2 origin = group.xy * 32;

	for ( uint dy = 0; dy < 2; dy++ )
		for ( uint dx = 0; dx < 2; dx++ )
		{
			uint2 at = origin + local + uint2( dx, dy );
			float value = 0.0;
			if ( at.x < g_wide && at.y < g_tall )
			{
				value = at.x < g_fillWide && at.y < g_fillTall ? Farthest( at ) : 1.0;
				g_level0[at] = value;
			}
			s_tile[local.y + dy][local.x + dx] = value;
		}

	//Each level after: a quarter of the threads, four texels each, in place in the tile - read, agree, write, agree.
	uint side = 16;
	for ( uint level = 1; level < g_count; level++ )
	{
		GroupMemoryBarrierWithGroupSync();

		bool mine = thread.x < side && thread.y < side;
		float value = 0.0;
		if ( mine )
		{
			uint2 at = thread.xy * 2;
			value = max( max( s_tile[at.y][at.x], s_tile[at.y][at.x + 1] ),
				max( s_tile[at.y + 1][at.x], s_tile[at.y + 1][at.x + 1] ) );
		}

		GroupMemoryBarrierWithGroupSync();

		if ( mine )
		{
			s_tile[thread.y][thread.x] = value;
			uint2 at = ( group.xy * side ) + thread.xy;
			uint2 size = max( uint2( g_wide, g_tall ) >> level, 1u );
			if ( at.x < size.x && at.y < size.y ) Store( level, at, value );
		}

		side >>= 1;
	}
}
