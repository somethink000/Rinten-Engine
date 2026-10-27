//Depth of field: half-res prefilter with CoC in alpha, disc blur, then Apply blends it in

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//Where the focus is, in metres from the eye. Nothing is off.
	float g_flFocalDistance < Attribute( "FocalDistance" ); Default( 5.0 ); >;

	//How far from it a thing may be, in metres, before it is wholly blurred.
	float g_flFocusRange < Attribute( "FocusRange" ); Default( 12.0 ); >;

	//How wide the blur goes, in pixels of the frame.
	float g_flBlurSize < Attribute( "BlurSize" ); Default( 8.0 ); >;

	//How many of the sixteen places in the disc: four, eight or sixteen.
	float g_flTaps < Attribute( "Taps" ); Default( 16.0 ); >;

	//The frame's own size, which the blur is measured in.
	float2 g_vFrameWide < Attribute( "FrameSize" ); >;

	//What PrefilterPs made, by its place in the table, for MainPs to blur.
	float g_flSource < Attribute( "Source" ); >;

	//What MainPs made, by its place in the table, for Apply to mix over the frame.
	float g_flDefocused < Attribute( "Defocused" ); >;
}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

//Sixteen places in a disc, spiralled so that any prefix of them is still spread out - which
//is what lets the quality setting take the first four, the first eight or all of them.
static const float2 kDisc[16] =
{
	float2(  0.000,  0.000 ), float2(  0.541, -0.208 ),
	float2( -0.288,  0.700 ), float2( -0.512, -0.686 ),
	float2(  0.930,  0.316 ), float2( -0.869,  0.461 ),
	float2(  0.246, -0.947 ), float2(  0.383,  0.489 ),
	float2( -0.723, -0.166 ), float2(  0.121,  0.812 ),
	float2( -0.396, -0.404 ), float2(  0.699, -0.612 ),
	float2( -0.978, -0.107 ), float2(  0.058, -0.520 ),
	float2( -0.163,  0.281 ), float2(  0.836,  0.083 )
};

//How far out of focus a pixel is, from nothing to all of it - a distance in metres, worked out through the frame's
//own clip-to-world matrix, rather than a number that depends on where the near and far planes happen to be.
float Defocus( float2 uv )
{
	if ( g_flFocalDistance <= 0.0 || g_flBlurSize <= 0.0 ) return 0.0;

	float depth = SceneDepthRaw( uv );

	//The sky is as far away as anything gets.
	if ( IsFarDepth( depth ) ) return 1.0;

	float3 place = PlaceOf( uv, depth );
	float away = abs( length( place - EYE_FRAME( camera ).xyz ) - g_flFocalDistance );

	return saturate( away / max( g_flFocusRange, 0.0001 ) );
}

//The frame at half the size, and how far out of focus each pixel is.
[shader("fragment")]
float4 PrefilterPs( PostV2F i ) : SV_Target0
{
	return float4( SceneColour( i.uv ), Defocus( i.uv ) );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float4 centre = FramePicture( g_flSource, i.uv );
	float amount = centre.a;

	//Sharp: nothing to spread, and the alpha says so.
	if ( amount <= 0.01 ) return float4( centre.rgb, 0.0 );

	int taps = clamp( int( g_flTaps ), 4, 16 );

	//In pixels of the frame, whatever size this picture is.
	float2 step = ( amount * g_flBlurSize ) / max( g_vFrameWide, 1.0 );

	//The middle of the disc is the pixel itself, at its full weight.
	float3 sum = centre.rgb;
	float weight = 1.0;

	for ( int tap = 1; tap < taps; ++tap )
	{
		float4 there = FramePicture( g_flSource, i.uv + ( kDisc[tap] * step ) );

		//Weighted by how much the tap itself is spreading, which is what keeps a sharp thing from being dragged into the blur of what is behind it.
		float w = saturate( there.a / max( amount, 0.0001 ) );

		sum += there.rgb * w;
		weight += w;
	}

	return float4( sum / weight, amount );
}

//What is out of focus mixed over the frame, by how out of focus it is - a point
//effect, folded by the engine into whatever reads the frame next. See CorePointChain.
float3 Apply( float3 colour, float2 uv )
{
	float4 soft = FramePicture( g_flDefocused, uv );

	return lerp( colour, soft.rgb, saturate( soft.a ) );
}
