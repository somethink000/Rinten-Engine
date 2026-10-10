//Bloom passes: threshold at full res written at half, mip chain, gather, then additive Apply

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//How far past white a colour has to be to spill, by its brightest channel.
	float g_flThreshold < Default( 1.0 ); Range( 0, 8 ); UiGroup( "Bloom" ); >;

	//What was kept - BrightPs's picture, with its chain of halvings under it - by its place in the table, and its top's size in texels.
	float g_flKept < Attribute( "Kept" ); >;
	float2 g_vKeptSize < Attribute( "KeptSize" ); >;

	//The frame's size: the finest level the glow reads follows its width.
	float2 g_vFrameWide < Attribute( "FrameSize" ); >;

	//How much each wider level counts against the one before it.
	float g_flFalloff < Attribute( "Falloff" ); Default( 1.0 ); >;

	//The wide half GatherCs worked out, for FinishCs to add the near half to.
	float g_flCoarse < Attribute( "Coarse" ); >;

	//The strength, times the tint.
	float3 g_vScale < Attribute( "Scale" ); Default3( 1.0, 1.0, 1.0 ); >;

	//The glow FinishCs made, by its place in the table, for Apply to lay over the frame; and how - one added, two screened, three the glow alone.
	float g_flGlow < Attribute( "Glow" ); >;
	float g_flMode < Attribute( "Mode" ); Default( 1.0 ); >;

	//Where the two compute passes store: the coarse glow, then the glow.
	RWTexture2D g_tOut < Attribute( "Out" ); Format( "compact" ); >;
}

#include "postprocess.slang"

//---- BrightPs ---------------------------------------------------------------

//How bright a colour is for the purpose of deciding what spills. The largest channel rather than a luminance: a saturated red light at four
//times over white is something that glows, and weighting it by how little the eye is owed for red would say it is dimmer than a grey wall.
float Brightest( float3 colour )
{
	return max( colour.r, max( colour.g, colour.b ) );
}

//Soft-knee threshold so a surface crossing the line doesn't pop a halo on at once
float Keep( float brightness, float threshold )
{
	//Half the threshold wide, which leaves the curve tangent to both ends of it.
	float knee = max( threshold * 0.5, 1e-4 );

	float soft = clamp( brightness - threshold + knee, 0.0, 2.0 * knee );
	soft = ( soft * soft ) / ( 4.0 * knee );

	return max( soft, brightness - threshold );
}

//3x3 average from four taps with Karis weighting so one fireflies pixel can't dominate
float3 Kept( float3 a, float3 b, float3 c, float3 d )
{
	float wa = 1.0 / ( 1.0 + Brightest( a ) );
	float wb = 1.0 / ( 1.0 + Brightest( b ) );
	float wc = 1.0 / ( 1.0 + Brightest( c ) );
	float wd = 1.0 / ( 1.0 + Brightest( d ) );

	float3 colour = ( ( a * wa ) + ( b * wb ) + ( c * wc ) + ( d * wd ) ) /
		max( wa + wb + wc + wd, 1e-4 );

	//Only what is over the line, in the light the world was shaded in - which is why this belongs before the
	//tonemap and not after it. Scaled rather than subtracted per channel, so what spills keeps the colour it had.
	float brightness = Brightest( colour );

	return colour * ( Keep( brightness, g_flThreshold ) / max( brightness, 1e-4 ) );
}

[shader("fragment")]
float4 BrightPs( PostV2F i ) : SV_Target0
{
	//A texel of the frame, which is half a texel of the picture being written: g_vFrameSize is the half size, the picture's own.
	float2 step = g_vFrameSize.zw * 0.5;

	//The nine corners the four pixels under this texel take their taps at.
	float3 c00 = SceneColour( i.uv + float2( -1.0, -1.0 ) * step );
	float3 c10 = SceneColour( i.uv + float2(  0.0, -1.0 ) * step );
	float3 c20 = SceneColour( i.uv + float2(  1.0, -1.0 ) * step );
	float3 c01 = SceneColour( i.uv + float2( -1.0,  0.0 ) * step );
	float3 c11 = SceneColour( i.uv );
	float3 c21 = SceneColour( i.uv + float2(  1.0,  0.0 ) * step );
	float3 c02 = SceneColour( i.uv + float2( -1.0,  1.0 ) * step );
	float3 c12 = SceneColour( i.uv + float2(  0.0,  1.0 ) * step );
	float3 c22 = SceneColour( i.uv + float2(  1.0,  1.0 ) * step );

	//Each pixel kept on its own, then the four averaged - the box the first halving of a full-size picture would have been.
	float3 kept =
		Kept( c00, c10, c01, c11 ) +
		Kept( c10, c20, c11, c21 ) +
		Kept( c01, c11, c02, c12 ) +
		Kept( c11, c21, c12, c22 );

	return float4( kept * 0.25, 1.0 );
}

//---- the chain of what was kept ----------------------------------------------

//Reads one kept mip level (top is level one); fractional levels blend
float3 Level( float2 uv, float level )
{
	return FrameMip( g_flKept, uv, level - 1.0 ).rgb;
}

//The finest level the window of seven starts at: the widest level always about
//fifteen texels across, so a halo is the same share of the screen at every size.
float FirstLevel()
{
	return max( 1.0, log2( max( g_vFrameWide.x, 1.0 ) / 15.0 ) - 6.0 );
}

//How much each of the seven levels counts, the finest first, and their total.
void Weights( out float w[7], out float total )
{
	//A level is an average over four times the area of the one above, so an even share falls off as one over
	//the distance squared - the glare round a real light; above that the wide levels hang in the air as haze.
	w[0] = 1.0;

	for ( int k = 1; k < 7; k++ ) w[k] = w[k - 1] * g_flFalloff;

	total = w[0] + w[1] + w[2] + w[3] + w[4] + w[5] + w[6];
}

//---- GatherCs -----------------------------------------------------------------

//Bicubic B-spline read in four taps: no creases at texel edges, no dark rings
float3 SmoothLevel( float2 uv, float level )
{
	float2 size = g_vKeptSize / exp2( level - 1.0 );
	float2 texel = 1.0 / size;

	float2 coord = uv * size - 0.5;
	float2 whole = floor( coord );
	float2 f = coord - whole;

	float2 f2 = f * f;
	float2 f3 = f2 * f;

	float2 w0 = ( 1.0 / 6.0 ) * ( -f3 + 3.0 * f2 - 3.0 * f + 1.0 );
	float2 w1 = ( 1.0 / 6.0 ) * ( 3.0 * f3 - 6.0 * f2 + 4.0 );
	float2 w2 = ( 1.0 / 6.0 ) * ( -3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0 );
	float2 w3 = ( 1.0 / 6.0 ) * f3;

	float2 s0 = w0 + w1;
	float2 s1 = w2 + w3;

	float2 t0 = ( whole - 0.5 + w1 / s0 ) * texel;
	float2 t1 = ( whole + 1.5 + w3 / s1 ) * texel;

	float3 a = Level( float2( t0.x, t0.y ), level );
	float3 b = Level( float2( t1.x, t0.y ), level );
	float3 c = Level( float2( t0.x, t1.y ), level );
	float3 d = Level( float2( t1.x, t1.y ), level );

	return ( ( a * s0.x + b * s1.x ) * s0.y ) + ( ( c * s0.x + d * s1.x ) * s1.y );
}

//The wide half of the glow, at a quarter of the frame: the coarse levels are blurred far wider than a
//quarter-size texel before they are read at all, so working them out once per sixteen pixels loses nothing.
[shader("compute")]
[numthreads(16, 16, 1)]
void GatherCs( uint3 id : SV_DispatchThreadID )
{
	uint2 size = uint2( g_vFrameSize.xy );

	if ( id.x >= size.x || id.y >= size.y ) return;

	float2 uv = ( float2( id.xy ) + 0.5 ) * g_vFrameSize.zw;
	float first = FirstLevel();

	float w[7];
	float total;
	Weights( w, total );

	float3 coarse =
		SmoothLevel( uv, first + 2.0 ) * w[2] +
		SmoothLevel( uv, first + 3.0 ) * w[3] +
		SmoothLevel( uv, first + 4.0 ) * w[4] +
		SmoothLevel( uv, first + 5.0 ) * w[5] +
		SmoothLevel( uv, first + 6.0 ) * w[6];

	g_tOut[id.xy] = float4( coarse / total, 1.0 );
}

//---- FinishCs -----------------------------------------------------------------

//Final glow: two finest levels read directly plus the coarse gather, times strength and tint
[shader("compute")]
[numthreads(16, 16, 1)]
void FinishCs( uint3 id : SV_DispatchThreadID )
{
	uint2 size = uint2( g_vFrameSize.xy );

	if ( id.x >= size.x || id.y >= size.y ) return;

	float2 uv = ( float2( id.xy ) + 0.5 ) * g_vFrameSize.zw;
	float first = FirstLevel();

	float w[7];
	float total;
	Weights( w, total );

	float3 glow = ( ( Level( uv, first ) * w[0] ) + ( Level( uv, first + 1.0 ) * w[1] ) ) / total +
		FramePicture( g_flCoarse, uv ).rgb;

	g_tOut[id.xy] = float4( max( glow, 0.0 ) * g_vScale, 1.0 );
}

//---- Apply --------------------------------------------------------------------

//The glow over the frame, in the light the world was shaded in.
float3 Apply( float3 colour, float2 uv )
{
	float3 glow = FramePicture( g_flGlow, uv ).rgb;

	//Added; screened, which is src (1 - dst) + dst; or the glow alone.
	if ( g_flMode < 1.5 ) return colour + glow;
	if ( g_flMode < 2.5 ) return colour + ( glow * ( 1.0 - colour ) );

	return glow;
}
