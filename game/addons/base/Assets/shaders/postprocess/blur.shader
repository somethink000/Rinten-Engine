//Full-frame blur from the frame mip chain with a bicubic read; one pass, four taps

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	float g_flSize < Default( 1.0 ); Range( 0, 1 ); UiGroup( "Blur" ); >;
}

#include "postprocess.slang"

//How big one level of the chain is, the way the hardware makes it - halved and rounded down, never past a pixel.
float2 LevelSize( float level )
{
	return max( floor( g_vFrameSize.xy / exp2( level ) ), 1.0 );
}

//One level read as a smooth surface: a cubic B-spline out of four bilinear taps. The same filter bloom.shader uses, and documented there.
float3 SmoothLevel( float2 uv, float level )
{
	float2 size = LevelSize( level );
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

	float3 a = FrameMip( g_vFramePictures.x, float2( t0.x, t0.y ), level ).rgb;
	float3 b = FrameMip( g_vFramePictures.x, float2( t1.x, t0.y ), level ).rgb;
	float3 c = FrameMip( g_vFramePictures.x, float2( t0.x, t1.y ), level ).rgb;
	float3 d = FrameMip( g_vFramePictures.x, float2( t1.x, t1.y ), level ).rgb;

	return ( ( a * s0.x + b * s1.x ) * s0.y ) + ( ( c * s0.x + d * s1.x ) * s1.y );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	//Mip level from the slider, scaled by frame width so a setting looks the same at any resolution
	float widest = max( 1.0, log2( max( g_vFrameSize.x, 1.0 ) / 60.0 ) );
	float level = saturate( g_flSize ) * widest;

	//The top level read straight: nothing to smooth, and the cubic would only soften a picture that was asked to be left alone.
	if ( level < 0.01 ) return float4( SceneColour( i.uv ), 1.0 );

	return float4( SmoothLevel( i.uv, level ), 1.0 );
}
