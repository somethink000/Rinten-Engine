//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//SMAA 1x over the finished picture: edges, blend weights, then the blend onto the screen - see CoreSmaa. The searches
//and areas are the reference's, unchanged, in smaa/SMAA.h (MIT - see LICENSE.txt there). Every picture read is sRGB,
//so it reads back linear: edges take luma through a square root, and the blend mixes in linear light as it should.
//The full frame finds edges and weights in compute (SMAA_COMPUTE); the portable frame draws all three.

struct V2F
{
	float4 position : SV_Position;
};

[[vk::push_constant]]
cbuffer Push
{
	float4 g_metrics; //1/width, 1/height, width, height of the finished picture
};

//Edges: the finished picture. Weights: edges, area table, search table. Blend: the finished picture, weights.
[[vk::binding(0,0)]] Sampler2D g_color;
[[vk::binding(1,0)]] Sampler2D g_normal;
[[vk::binding(2,0)]] Sampler2D g_third;

#ifdef SMAA_COMPUTE
[[vk::binding(0,2)]] [format("rgba8")] RWTexture2D<float4> g_out;
#endif

//A material's sampler may repeat; half a texel inside is what a clamped one would read at the border
float4 Read( Sampler2D picture, float2 uv )
{
	float2 inside = g_metrics.xy * 0.5;
	return picture.SampleLevel( clamp( uv, inside, 1.0 - inside ), 0.0 );
}

#define SMAA_CUSTOM_SL 1
#define SMAA_PRESET_HIGH 1
#define SMAA_RT_METRICS g_metrics
#define SMAATexture2D( tex ) Sampler2D tex
#define SMAATexturePass2D( tex ) tex
#define SMAASampleLevelZero( tex, coord ) Read( tex, coord )
#define SMAASampleLevelZeroPoint( tex, coord ) Read( tex, coord )
#define SMAASampleLevelZeroOffset( tex, coord, offset ) Read( tex, ( coord ) + float2( offset ) * SMAA_RT_METRICS.xy )
#define SMAASample( tex, coord ) Read( tex, coord )
#define SMAASamplePoint( tex, coord ) Read( tex, coord )
#define SMAASampleOffset( tex, coord, offset ) Read( tex, ( coord ) + float2( offset ) * SMAA_RT_METRICS.xy )
#define SMAA_FLATTEN [flatten]
#define SMAA_BRANCH [branch]

#include "smaa/SMAA.h"

//Near the gamma the reference's thresholds are for
float Luma( float3 colour ) { return dot( sqrt( max( colour, 0.0 ) ), float3( 0.2126, 0.7152, 0.0722 ) ); }

float LumaAt( float2 uv ) { return Luma( Read( g_color, uv ).rgb ); }

//The reference's local contrast adaptation over lumas already read: an edge stays where it is the strongest near it
float2 Adapted( float2 edges, float2 delta, float centre, float2 leftTop, float2 rightBottom, float2 farLeftTop )
{
	float2 most = max( delta, abs( centre - rightBottom ) );
	most = max( most, abs( leftTop - farLeftTop ) );

	return edges * step( max( most.x, most.y ), SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta );
}

[shader("vertex")]
V2F MainVs( uint id : SV_VertexID )
{
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );

	V2F o;
	o.position = float4( ( corner * 2.0 ) - 1.0, 0.0, 1.0 );
	return o;
}

//From the fragment's place: GL flips the vertex and keeps rows bottom first, and the two cancel - see glow.slang
float2 Uv( V2F i ) { return i.position.xy * g_metrics.xy; }

//Left edge in red, top edge in green; nothing written where there is neither, over a cleared picture
[shader("fragment")]
float4 EdgesPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float2 t = g_metrics.xy;

	float centre = LumaAt( uv );
	float2 leftTop = float2( LumaAt( uv - float2( t.x, 0.0 ) ), LumaAt( uv - float2( 0.0, t.y ) ) );

	float2 delta = abs( centre - leftTop );
	float2 edges = step( SMAA_THRESHOLD, delta );

	if ( dot( edges, float2( 1.0, 1.0 ) ) == 0.0 ) discard;

	float2 rightBottom = float2( LumaAt( uv + float2( t.x, 0.0 ) ), LumaAt( uv + float2( 0.0, t.y ) ) );
	float2 farLeftTop = float2( LumaAt( uv - float2( 2.0 * t.x, 0.0 ) ), LumaAt( uv - float2( 0.0, 2.0 * t.y ) ) );

	return float4( Adapted( edges, delta, centre, leftTop, rightBottom, farLeftTop ), 0.0, 0.0 );
}

float4 Weights( float2 uv )
{
	float2 pixel;
	float4 offset[3];
	SMAABlendingWeightCalculationVS( uv, pixel, offset );

	return SMAABlendingWeightCalculationPS( uv, pixel, offset, g_color, g_normal, g_third, float4( 0.0, 0.0, 0.0, 0.0 ) );
}

//Only where an edge was found, over a cleared picture
[shader("fragment")]
float4 WeightsPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );

	if ( dot( Read( g_color, uv ).rg, float2( 1.0, 1.0 ) ) == 0.0 ) discard;

	return Weights( uv );
}

[shader("fragment")]
float4 BlendPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );

	float4 offset;
	SMAANeighborhoodBlendingVS( uv, offset );

	return SMAANeighborhoodBlendingPS( uv, offset, g_color, g_normal );
}

#ifdef SMAA_COMPUTE

//An 8x8 tile's lumas and the two columns and rows before it and one after, each read once
static const int Apron = 11;
groupshared float s_luma[Apron * Apron];

float Shared( int2 at ) { return s_luma[( at.y * Apron ) + at.x]; }

[shader("compute")]
[numthreads(8, 8, 1)]
void EdgesCs( uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID )
{
	int2 size = int2( g_metrics.zw );
	int2 origin = int2( group.xy * 8 ) - 2;
	int flat = int( thread.y * 8 + thread.x );

	for ( int k = flat; k < Apron * Apron; k += 64 )
	{
		int2 at = clamp( origin + int2( k % Apron, k / Apron ), int2( 0, 0 ), size - 1 );
		s_luma[k] = Luma( g_color.Load( int3( at, 0 ) ).rgb );
	}

	GroupMemoryBarrierWithGroupSync();

	int2 pixel = int2( group.xy * 8 + thread.xy );
	if ( any( pixel >= size ) ) return;

	int2 at = int2( thread.xy ) + 2;

	float centre = Shared( at );
	float2 leftTop = float2( Shared( at - int2( 1, 0 ) ), Shared( at - int2( 0, 1 ) ) );

	float2 delta = abs( centre - leftTop );
	float2 edges = step( SMAA_THRESHOLD, delta );

	if ( dot( edges, float2( 1.0, 1.0 ) ) != 0.0 )
	{
		float2 rightBottom = float2( Shared( at + int2( 1, 0 ) ), Shared( at + int2( 0, 1 ) ) );
		float2 farLeftTop = float2( Shared( at - int2( 2, 0 ) ), Shared( at - int2( 0, 2 ) ) );

		edges = Adapted( edges, delta, centre, leftTop, rightBottom, farLeftTop );
	}

	g_out[pixel] = float4( edges, 0.0, 0.0 );
}

//Every pixel written, as nothing clears the picture first; the searches run only where an edge was found
[shader("compute")]
[numthreads(8, 8, 1)]
void WeightsCs( uint3 id : SV_DispatchThreadID )
{
	int2 pixel = int2( id.xy );
	if ( any( pixel >= int2( g_metrics.zw ) ) ) return;

	float2 uv = ( float2( pixel ) + 0.5 ) * g_metrics.xy;
	float2 edges = g_color.Load( int3( pixel, 0 ) ).rg;

	float4 weights = float4( 0.0, 0.0, 0.0, 0.0 );
	if ( dot( edges, float2( 1.0, 1.0 ) ) != 0.0 ) weights = Weights( uv );

	g_out[pixel] = weights;
}

#endif
