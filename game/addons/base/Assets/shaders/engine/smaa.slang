//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//SMAA over the finished picture: edges, blend weights, then the blend - and T2x's resolve, S2x's passes, FXAA and
//CMAA2's apply, which take the same slot - see CoreSmaa. The searches and areas are the reference's, unchanged, in smaa/SMAA.h (MIT - see LICENSE.txt there). Every picture read is sRGB,
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

	//T2x's resolve: this frame's clip space into last frame's, its x, y and w rows; x of g_resolve how much of last frame.
	//S2x: y of g_resolve the exposure its edges take luma from light by, z the samples' slot in the bindless table
	float4 g_toBeforeX;
	float4 g_toBeforeY;
	float4 g_toBeforeW;
	float4 g_resolve;
};

//Edges: the finished picture. Weights: edges, area table, search table. Blend: the finished picture, weights.
[[vk::binding(0,0)]] Sampler2D g_color;
[[vk::binding(1,0)]] Sampler2D g_normal;
[[vk::binding(2,0)]] Sampler2D g_third;
[[vk::binding(3,0)]] Sampler2D g_fourth;

#ifdef SMAA_COMPUTE
//S2x: MSAA 2x's samples, read in place - full frame only, as is every pass that reads them
[[vk::binding(0,3)]] Sampler2DMS<float4> g_sampledWorlds[];
[[vk::binding(0,2)]] [format("rgba8")] RWTexture2D<float4> g_out;
[[vk::binding(1,2)]] [format("rgba8")] RWTexture2D<float4> g_second;
#endif

//A material's sampler may repeat; half a texel inside is what a clamped one would read at the border
float4 Read( Sampler2D picture, float2 uv )
{
	float2 inside = g_metrics.xy * 0.5;
	return picture.SampleLevel( clamp( uv, inside, 1.0 - inside ), 0.0 );
}

#define SMAA_CUSTOM_SL 1
#define SMAA_PRESET_HIGH 1
//No diagonal search: 0.05 ms of foliage edges for no difference seen, as Filmic SMAA ships - see Jimenez 2016
#define SMAA_DISABLE_DIAG_DETECTION 1
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

//Edges, area table and search table are colour, normal and third; subsample the offsets into the area table
float4 WeightsOf( float2 uv, Sampler2D edges, float4 subsample )
{
	float2 pixel;
	float4 offset[3];
	SMAABlendingWeightCalculationVS( uv, pixel, offset );

	return SMAABlendingWeightCalculationPS( uv, pixel, offset, edges, g_normal, g_third, subsample );
}

float4 Weights( float2 uv ) { return WeightsOf( uv, g_color, float4( 0.0, 0.0, 0.0, 0.0 ) ); }

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

	//Opaque: the full frame's compose leaves luma in alpha, which must not reach the window
	return float4( SMAANeighborhoodBlendingPS( uv, offset, g_color, g_normal ).rgb, 1.0 );
}

//SMAA T2x's resolve: this frame's blended picture and last frame's, drawn a half pixel apart, averaged - two samples a
//pixel. Last frame's is found through the camera, or where a body moved by itself through its written motion (fourth,
//w of g_resolve set when there is any) and only there held inside this frame's neighbourhood: on detail finer than a
//pixel the two half pixels differ by more than any 3x3 holds, so clamping a still pixel flickers.
//Colour: this frame, normal: last frame, third: depth
[shader("fragment")]
float4 ResolvePs( V2F i ) : SV_Target
{
	int2 pixel = int2( i.position.xy );
	float2 uv = ( float2( pixel ) + 0.5 ) * g_metrics.xy;
	float3 now = g_color.Load( int3( pixel, 0 ) ).rgb;

	float4 written = g_resolve.w > 0.5 ? g_fourth.Load( int3( pixel, 0 ) ) : float4( 0.0, 0.0, 0.0, 0.0 );
	bool moving = written.a > 0.5;

	float weight = g_resolve.x;
	float2 then = uv - written.xy;

	if ( !moving )
	{
		float4 clip = float4( ( uv * 2.0 ) - 1.0, min( g_third.Load( int3( pixel, 0 ) ).r, 1.0 ), 1.0 );
		float3 before = float3( dot( clip, g_toBeforeX ), dot( clip, g_toBeforeY ), dot( clip, g_toBeforeW ) );
		if ( before.z > 1e-6 ) then = ( ( before.xy / before.z ) * 0.5 ) + 0.5;
	}

	if ( any( then < 0.0 ) || any( then > 1.0 ) ) weight = 0.0;

	float3 last = Read( g_normal, then ).rgb;

	if ( moving )
	{
		int2 most = int2( g_metrics.zw ) - 1;
		float3 low = now;
		float3 high = now;

		[unroll]
		for ( int y = -1; y <= 1; y++ )
		{
			[unroll]
			for ( int x = -1; x <= 1; x++ )
			{
				float3 side = g_color.Load( int3( clamp( pixel + int2( x, y ), int2( 0, 0 ), most ), 0 ) ).rgb;
				low = min( low, side );
				high = max( high, side );
			}
		}

		last = clamp( last, low, high );
	}

	return float4( lerp( now, last, weight ), 1.0 );
}

//CMAA2's end: the pixels it changed (alpha set, stored in sRGB) over the finished picture - see cmaa2.slang.
//Colour: the finished picture, normal: what CMAA2 stored
[shader("fragment")]
float4 CmaaPs( V2F i ) : SV_Target
{
	int3 at = int3( int2( i.position.xy ), 0 );
	float4 changed = g_normal.Load( at );
	if ( changed.a == 0.0 ) return float4( g_color.Load( at ).rgb, 1.0 );

	float3 c = changed.rgb;
	return float4( select( c <= 0.04045, c / 12.92, pow( ( c + 0.055 ) / 1.055, 2.4 ) ), 1.0 );
}

//FXAA 3.11 Quality at its high preset (29): one pass from the finished picture, in place of SMAA's three
float LumaOf( float2 uv ) { return Read( g_color, uv ).a; }

static const int FxaaSteps = 12;
static const float FxaaStep[FxaaSteps] = { 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0 };

[shader("fragment")]
float4 FxaaPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float2 t = g_metrics.xy;

	//Luma is in alpha (the compose's): a gather at the corner after the pixel is it, east, south and south-east; one at
	//the corner before is north-west, north, west and it again
	float4 after = g_color.GatherAlpha( uv + 0.5 * t );
	float4 before = g_color.GatherAlpha( uv - 0.5 * t );

	float lumaM = after.w;
	float lumaE = after.z;
	float lumaS = after.x;
	float lumaSE = after.y;
	float lumaNW = before.w;
	float lumaN = before.z;
	float lumaW = before.x;

	float rangeMax = max( max( max( lumaN, lumaW ), max( lumaE, lumaS ) ), lumaM );
	float rangeMin = min( min( min( lumaN, lumaW ), min( lumaE, lumaS ) ), lumaM );
	float range = rangeMax - rangeMin;

	if ( range < max( 0.0625, rangeMax * 0.125 ) ) return float4( Read( g_color, uv ).rgb, 1.0 );

	float lumaNE = LumaOf( uv + float2( t.x, -t.y ) );
	float lumaSW = LumaOf( uv + float2( -t.x, t.y ) );

	float lumaNS = lumaN + lumaS;
	float lumaWE = lumaW + lumaE;
	float lumaNESE = lumaNE + lumaSE;
	float lumaNWNE = lumaNW + lumaNE;
	float lumaNWSW = lumaNW + lumaSW;
	float lumaSWSE = lumaSW + lumaSE;

	float edgeHorz = abs( -2.0 * lumaW + lumaNWSW ) + abs( -2.0 * lumaM + lumaNS ) * 2.0 + abs( -2.0 * lumaE + lumaNESE );
	float edgeVert = abs( -2.0 * lumaS + lumaSWSE ) + abs( -2.0 * lumaM + lumaWE ) * 2.0 + abs( -2.0 * lumaN + lumaNWNE );
	bool horzSpan = edgeHorz >= edgeVert;

	float lengthSign = horzSpan ? t.y : t.x;
	if ( !horzSpan ) { lumaN = lumaW; lumaS = lumaE; }

	float subpix = saturate( abs( ( ( lumaNS + lumaWE ) * 2.0 + lumaNWSW + lumaNESE ) / 12.0 - lumaM ) / range );

	float gradientN = lumaN - lumaM;
	float gradientS = lumaS - lumaM;
	bool pairN = abs( gradientN ) >= abs( gradientS );
	float gradientScaled = max( abs( gradientN ), abs( gradientS ) ) * 0.25;
	if ( pairN ) lengthSign = -lengthSign;

	float lumaEdge = 0.5 * ( pairN ? lumaN + lumaM : lumaS + lumaM );
	bool belowEdge = lumaM - lumaEdge < 0.0;

	//Along the edge both ways from the middle of it, until the luma leaves the pair it started between
	float2 posB = uv;
	if ( horzSpan ) posB.y += lengthSign * 0.5; else posB.x += lengthSign * 0.5;
	float2 along = horzSpan ? float2( t.x, 0.0 ) : float2( 0.0, t.y );

	float2 posN = posB - along * FxaaStep[0];
	float2 posP = posB + along * FxaaStep[0];
	float endN = LumaOf( posN ) - lumaEdge;
	float endP = LumaOf( posP ) - lumaEdge;
	bool doneN = abs( endN ) >= gradientScaled;
	bool doneP = abs( endP ) >= gradientScaled;

	[unroll]
	for ( int k = 1; k < FxaaSteps; k++ )
	{
		if ( doneN && doneP ) break;

		if ( !doneN ) { posN -= along * FxaaStep[k]; endN = LumaOf( posN ) - lumaEdge; doneN = abs( endN ) >= gradientScaled; }
		if ( !doneP ) { posP += along * FxaaStep[k]; endP = LumaOf( posP ) - lumaEdge; doneP = abs( endP ) >= gradientScaled; }
	}

	float dstN = horzSpan ? uv.x - posN.x : uv.y - posN.y;
	float dstP = horzSpan ? posP.x - uv.x : posP.y - uv.y;

	bool nearerN = dstN < dstP;
	bool goodSpan = ( ( nearerN ? endN : endP ) < 0.0 ) != belowEdge;
	float offset = goodSpan ? 0.5 - min( dstN, dstP ) / ( dstN + dstP ) : 0.0;

	float subpixBlend = ( -2.0 * subpix + 3.0 ) * subpix * subpix;
	offset = max( offset, subpixBlend * subpixBlend * 0.75 );

	if ( horzSpan ) uv.y += offset * lengthSign; else uv.x += offset * lengthSign;

	return float4( Read( g_color, uv ).rgb, 1.0 );
}

#ifdef SMAA_COMPUTE

//An 8x8 tile's lumas and the two columns and rows before it and one after, each read once - from alpha, where the
//full frame's compose left it; S2x's second sample's in s_second
static const int Apron = 11;
groupshared float s_luma[Apron * Apron];
groupshared float s_second[Apron * Apron];

float Shared( int2 at, int sample ) { int k = ( at.y * Apron ) + at.x; return sample == 0 ? s_luma[k] : s_second[k]; }

//SMAA's luma edges from the tile's lumas, with the reference's local contrast adaptation
float2 Found( int2 at, int sample )
{
	float centre = Shared( at, sample );
	float2 leftTop = float2( Shared( at - int2( 1, 0 ), sample ), Shared( at - int2( 0, 1 ), sample ) );

	float2 delta = abs( centre - leftTop );
	float2 edges = step( SMAA_THRESHOLD, delta );
	if ( dot( edges, float2( 1.0, 1.0 ) ) == 0.0 ) return edges;

	float2 rightBottom = float2( Shared( at + int2( 1, 0 ), sample ), Shared( at + int2( 0, 1 ), sample ) );
	float2 farLeftTop = float2( Shared( at - int2( 2, 0 ), sample ), Shared( at - int2( 0, 2 ), sample ) );

	return Adapted( edges, delta, centre, leftTop, rightBottom, farLeftTop );
}

//S2x's lumas, from light at the frame's exposure through a plain curve
float LightLuma( float3 light )
{
	float3 shown = light * g_resolve.y;
	return Luma( shown / ( 1.0 + shown ) );
}

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
		s_luma[k] = g_color.Load( int3( at, 0 ) ).a;
	}

	GroupMemoryBarrierWithGroupSync();

	int2 pixel = int2( group.xy * 8 + thread.xy );
	if ( any( pixel >= size ) ) return;

	g_out[pixel] = float4( Found( int2( thread.xy ) + 2, 0 ), 0.0, 0.0 );
}

//One of S2x's samples, clamped into the picture
float4 SampleOf( int2 at, int sample )
{
	return g_sampledWorlds[int( g_resolve.z )].Load( clamp( at, int2( 0, 0 ), int2( g_metrics.zw ) - 1 ), sample );
}

//SMAANeighborhoodBlendingPS on one sample in place: its bilinear reads are a lerp toward the neighbour
float4 BlendSample( int2 at, int sample, Sampler2D weights )
{
	int2 last = int2( g_metrics.zw ) - 1;
	float4 a;
	a.x = weights.Load( int3( min( at + int2( 1, 0 ), last ), 0 ) ).a;
	a.y = weights.Load( int3( min( at + int2( 0, 1 ), last ), 0 ) ).g;
	a.wz = weights.Load( int3( at, 0 ) ).xz;

	float4 colour = SampleOf( at, sample );
	if ( dot( a, float4( 1.0, 1.0, 1.0, 1.0 ) ) < 1e-5 ) return colour;

	bool h = max( a.x, a.z ) > max( a.y, a.w );
	float2 reach = h ? a.xz : a.yw;
	int2 along = h ? int2( 1, 0 ) : int2( 0, 1 );
	float2 share = reach / dot( reach, float2( 1.0, 1.0 ) );

	return share.x * lerp( colour, SampleOf( at + along, sample ), reach.x ) +
		share.y * lerp( colour, SampleOf( at - along, sample ), reach.y );
}

//SMAA S2x's end: each of MSAA 2x's samples blended by its own weights (colour, normal), the two averaged.
//Colour: the first sample's weights, normal: the second's
[shader("fragment")]
float4 S2xBlendPs( V2F i ) : SV_Target
{
	int2 at = int2( i.position.xy );
	return 0.5 * ( BlendSample( at, 0, g_color ) + BlendSample( at, 1, g_normal ) );
}

//S2x: both samples' edges in one pass, read from the multisampled world - the first into g_out, the second g_second
[shader("compute")]
[numthreads(8, 8, 1)]
void EdgesS2xCs( uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID )
{
	int2 size = int2( g_metrics.zw );
	int2 origin = int2( group.xy * 8 ) - 2;
	int flat = int( thread.y * 8 + thread.x );

	for ( int k = flat; k < Apron * Apron; k += 64 )
	{
		int2 at = origin + int2( k % Apron, k / Apron );
		s_luma[k] = LightLuma( SampleOf( at, 0 ).rgb );
		s_second[k] = LightLuma( SampleOf( at, 1 ).rgb );
	}

	GroupMemoryBarrierWithGroupSync();

	int2 pixel = int2( group.xy * 8 + thread.xy );
	if ( any( pixel >= size ) ) return;

	g_out[pixel] = float4( Found( int2( thread.xy ) + 2, 0 ), 0.0, 0.0 );
	g_second[pixel] = float4( Found( int2( thread.xy ) + 2, 1 ), 0.0, 0.0 );
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

//SMAA.h's subsampleIndices for MSAA 2x's two samples in their standard order: against MSAA 8x S2x 3.3 per edge pixel
//(MSAA 2x 3.9), 4x 3.2 - where SMAA.h's 4x table, written for a y running up, gave 3.4
static const float4 FirstSample = float4( 1.0, 1.0, 1.0, 0.0 );
static const float4 SecondSample = float4( 2.0, 2.0, 2.0, 0.0 );

//S2x: both samples' weights in one pass - the first's edges in colour, the second's in fourth
[shader("compute")]
[numthreads(8, 8, 1)]
void WeightsS2xCs( uint3 id : SV_DispatchThreadID )
{
	int2 pixel = int2( id.xy );
	if ( any( pixel >= int2( g_metrics.zw ) ) ) return;

	float2 uv = ( float2( pixel ) + 0.5 ) * g_metrics.xy;
	float2 first = g_color.Load( int3( pixel, 0 ) ).rg;
	float2 second = g_fourth.Load( int3( pixel, 0 ) ).rg;

	float4 weights = float4( 0.0, 0.0, 0.0, 0.0 );
	float4 secondWeights = weights;

	if ( dot( first, float2( 1.0, 1.0 ) ) != 0.0 ) weights = WeightsOf( uv, g_color, FirstSample );
	if ( dot( second, float2( 1.0, 1.0 ) ) != 0.0 ) secondWeights = WeightsOf( uv, g_fourth, SecondSample );

	g_out[pixel] = weights;
	g_second[pixel] = secondWeights;
}

#endif
