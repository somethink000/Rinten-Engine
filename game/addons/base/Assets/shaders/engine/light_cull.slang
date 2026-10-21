//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Tiled and depth-binned light culling (CoreLights): a group per screen tile writes a bitmask of the lamps whose
//sphere touches its column, and the groups after the tiles write each depth bin's first and last lamp - the lamps
//come sorted by view depth, so a bin's lamps are one run of indices and a pixel walks its tile's mask over that run

[[vk::binding(0,0)]] RWByteAddressBuffer g_lights;

[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_worldToView;

	//How wide the view is at unit depth, where the bins start, and how many bins a factor of e of depth is worth
	float4 g_screen;
	//Tiles across and down, bins, lights
	float4 g_cells;
	//Suns before the lamps, where the masks and the bins start in words, and words a mask
	float4 g_where;
};

//Words of the largest mask, CoreLights.MostLights over 32
static const uint MostWords = 128;

groupshared uint s_mask[MostWords];

//How far the light at this index reaches, and where it is in view space
float4 LampInView( uint index )
{
	float4 position = asfloat( g_lights.Load4( 64 + ( index * 80 ) ) );
	return float4( mul( float4( position.xyz, 1.0 ), g_worldToView ).xyz, position.w );
}

[shader("compute")]
[numthreads(64, 1, 1)]
void main( uint3 group : SV_GroupID, uint thread : SV_GroupIndex )
{
	uint tilesX = uint( g_cells.x );
	uint tilesY = uint( g_cells.y );
	uint bins = uint( g_cells.z );
	uint lightCount = uint( g_cells.w );

	uint suns = uint( g_where.x );
	uint masksAt = uint( g_where.y );
	uint binsAt = uint( g_where.z );
	uint maskWords = min( uint( g_where.w ), MostWords );

	float hx = g_screen.x;
	float hy = g_screen.y;
	float near = g_screen.z;
	float scale = g_screen.w;

	uint tiles = tilesX * tilesY;

	if ( group.x >= tiles )
	{
		uint bin = ( ( group.x - tiles ) * 64 ) + thread;
		if ( bin >= bins ) return;

		//Bin 0 takes everything nearer than the grid's start, the last everything past its end
		float first = bin == 0 ? 0.0 : near * exp( float( bin ) / scale );
		float past = bin + 1 >= bins ? 1e9 : near * exp( float( bin + 1 ) / scale );

		uint lo = 0xFFFF;
		uint hi = 0;

		for ( uint i = suns; i < lightCount; ++i )
		{
			float4 lamp = LampInView( i );
			float deep = -lamp.z;

			if ( deep + lamp.w < first || deep - lamp.w > past ) continue;

			lo = min( lo, i );
			hi = max( hi, i );
		}

		g_lights.Store( ( binsAt + bin ) * 4, lo | ( hi << 16 ) );
		return;
	}

	for ( uint w = thread; w < maskWords; w += 64 ) s_mask[w] = 0;

	GroupMemoryBarrierWithGroupSync();

	uint tx = group.x % tilesX;
	uint ty = group.x / tilesX;

	float x0 = ( float( tx ) / tilesX ) * 2.0 - 1.0;
	float x1 = ( float( tx + 1 ) / tilesX ) * 2.0 - 1.0;
	float y0 = ( float( ty ) / tilesY ) * 2.0 - 1.0;
	float y1 = ( float( ty + 1 ) / tilesY ) * 2.0 - 1.0;

	//The four sides of the tile, as planes through the eye. A point at depth d sits at ndc.x = p.x / ( hx * d ), so the edge at x0 is p.x + x0
	//* hx * p.z = nought - and which side of it is inside depends on the sign of hx, because the projection turns the picture over on its y.
	float sx = hx >= 0.0 ? 1.0 : -1.0;
	float sy = hy >= 0.0 ? 1.0 : -1.0;

	float3 planeA = normalize( float3( sx, 0.0, sx * x0 * hx ) );
	float3 planeB = normalize( float3( -sx, 0.0, -sx * x1 * hx ) );
	float3 planeC = normalize( float3( 0.0, sy, sy * y0 * hy ) );
	float3 planeD = normalize( float3( 0.0, -sy, -sy * y1 * hy ) );

	for ( uint i = suns + thread; i < lightCount; i += 64 )
	{
		float4 lamp = LampInView( i );

		if ( -lamp.z + lamp.w < near ) continue;
		if ( dot( planeA, lamp.xyz ) < -lamp.w ) continue;
		if ( dot( planeB, lamp.xyz ) < -lamp.w ) continue;
		if ( dot( planeC, lamp.xyz ) < -lamp.w ) continue;
		if ( dot( planeD, lamp.xyz ) < -lamp.w ) continue;

		InterlockedOr( s_mask[i >> 5], 1u << ( i & 31u ) );
	}

	GroupMemoryBarrierWithGroupSync();

	uint at = masksAt + ( group.x * maskWords );
	for ( uint w = thread; w < maskWords; w += 64 ) g_lights.Store( ( at + w ) * 4, s_mask[w] );
}
