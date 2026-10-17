//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Picks next frame's shading rate per tile from this frame's output; coarse where change is invisible

[[vk::binding(0,0)]] RWByteAddressBuffer g_rates;
//The picture table at set 1 - see VkPipelines.ComputeCreateOverTable.
[[vk::binding(0,1)]] Sampler2D g_textures[];

[[vk::push_constant]]
cbuffer RatePush
{
	//Where the picture is in the table, and its size.
	uint g_picture;
	uint g_width;
	uint g_height;

	//The map's squares across and down, how many pixels a square is, and how many bytes a row of the map takes in the buffer.
	uint g_across;
	uint g_down;
	uint g_texel;
	uint g_rowBytes;

	//Whether the picture reads back as linear light.
	uint g_linear;

	//The largest change between neighbours a coarse fragment may hide, in the display's own units, nought to one.
	float g_step;
	uint3 g_pad;

	//The world's clip space into the frame before's, the x, y and w columns - see VkFrame.LayRates
	float4 g_back[3];
};

//Sixteen pixels square a group, whichever the map's square is: four squares of eight or one of sixteen. A group a square of eight was thirty-five
//thousand groups a frame for a hundred reads each, and what they cost was being groups - 0.2 ms at 1920 by 1164, against 0.04 for the reads.
static const uint GroupPixels = 16;

//Whether each square in the group has anything too rough for a coarser fragment, across and down: two words a square, at most four squares.
groupshared uint g_rough[8];

//The group's pixels and the two past its right and bottom edges, each read once: every pixel is
//compared with the one two along and the one two down, and those are mostly its neighbours' own pixels.
static const uint TileSide = GroupPixels + 2;
groupshared float g_luma[TileSide * TileSide];

//Where what is at uv now was a frame before, at the far plane: the view's turn, which is most of what moves a picture
float2 Back( float2 uv )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, 1.0, 1.0 );
	float3 before = float3( dot( clip, g_back[0] ), dot( clip, g_back[1] ), dot( clip, g_back[2] ) );

	return before.z > 1e-6 ? ( ( ( before.xy / before.z ) * 0.5 ) + 0.5 ) - uv : float2( 0.0, 0.0 );
}

//A pixel of the world, g_width by g_height, read from the picture shown at whatever size it was shown, where it will be next
//frame if the view keeps turning as it did: a step back there is a step on from here
float Luma( int2 at, float2 ahead )
{
	at = clamp( at, int2( 0, 0 ), int2( int( g_width ) - 1, int( g_height ) - 1 ) );

	float2 uv = saturate( ( ( float2( at ) + 0.5 ) / float2( g_width, g_height ) ) + ahead );
	float3 c = g_textures[g_picture].SampleLevel( uv, 0.0 ).rgb;
	float y = dot( c, float3( 0.299, 0.587, 0.114 ) );

	//Back into the display's steps where the view decoded them - of the brightness rather than of each channel, which is one
	//power instead of three and near enough: what is compared is a step against a threshold, not a colour against a colour.
	return g_linear != 0 ? pow( max( y, 0.0 ), 1.0 / 2.2 ) : y;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main( uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID, uint flat : SV_GroupIndex )
{
	int2 origin = int2( group.xy * GroupPixels );

	if ( flat < 8 ) g_rough[flat] = 0;

	//Once for the group, about its middle: the turn moves sixteen pixels alike
	float2 ahead = Back( ( float2( origin ) + ( GroupPixels * 0.5 ) ) / float2( g_width, g_height ) );

	for ( uint i = flat; i < TileSide * TileSide; i += 64u )
		g_luma[i] = Luma( origin + int2( i % TileSide, i / TileSide ), ahead );

	GroupMemoryBarrierWithGroupSync();

	//Each thread two by two pixels; which square of the map they are in.
	uint2 first = lane.xy * 2u;
	uint square = g_texel >= 16u ? 0u : ( ( first.y / 8u ) * 2u ) + ( first.x / 8u );

	uint roughX = 0;
	uint roughY = 0;

	for ( uint y = 0; y < 2; y++ )
	{
		for ( uint x = 0; x < 2; x++ )
		{
			uint2 in_ = first + uint2( x, y );
			int2 at = origin + int2( in_ );
			if ( at.x >= int( g_width ) || at.y >= int( g_height ) ) continue;

			float here = g_luma[( in_.y * TileSide ) + in_.x];
			float across = abs( g_luma[( in_.y * TileSide ) + in_.x + 2u] - here ) * 0.5;
			float down = abs( g_luma[( ( in_.y + 2u ) * TileSide ) + in_.x] - here ) * 0.5;

			if ( across > g_step ) roughX = 1;
			if ( down > g_step ) roughY = 1;
		}
	}

	if ( roughX != 0 ) InterlockedOr( g_rough[square * 2u], 1u );
	if ( roughY != 0 ) InterlockedOr( g_rough[( square * 2u ) + 1u], 1u );

	GroupMemoryBarrierWithGroupSync();

	//One thread a square writes it.
	uint squares = g_texel >= 16u ? 1u : 4u;
	if ( flat >= squares ) return;

	uint2 cell = ( group.xy * ( GroupPixels / g_texel ) ) + uint2( flat % 2u, flat / 2u );
	if ( cell.x >= g_across || cell.y >= g_down ) return;

	//A fragment size: log2 of the width in bits two and three, of the height in bits nought and one - see VkFovea.
	uint rate = ( g_rough[flat * 2u] == 0 ? 4u : 0u ) | ( g_rough[( flat * 2u ) + 1u] == 0 ? 1u : 0u );

	uint at = ( cell.y * g_rowBytes ) + cell.x;
	g_rates.InterlockedOr( at & ~3u, rate << ( ( at & 3u ) * 8u ) );
}
