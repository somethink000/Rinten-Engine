//Outline and inner glow from the highlight mask, using a two-radius ring of taps; additive

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

STATE
{
	//Added to the frame: an outline is light laid on top of what is there.
	Blend( Additive );
}

FEATURES
{
}

PARAMETERS
{
	//How wide the line is, in pixels of the frame.
	float g_flWidth < Default( 4.0 ); Range( 0, 32 ); UiGroup( "Highlight" ); >;

	//How much of the colour to lay over the marked shape itself, rather than beside it. Nothing is an outline and nothing else.
	float g_flInside < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Highlight" ); >;

	//The picture the shapes were marked into, by its place in the table.
	float g_flMask < Attribute( "Mask" ); >;
}

#include "postprocess.slang"

//Sixteen places round a circle, as sines and cosines worked out once.
static const float2 kRing[16] =
{
	float2(  1.000,  0.000 ), float2(  0.924,  0.383 ),
	float2(  0.707,  0.707 ), float2(  0.383,  0.924 ),
	float2(  0.000,  1.000 ), float2( -0.383,  0.924 ),
	float2( -0.707,  0.707 ), float2( -0.924,  0.383 ),
	float2( -1.000,  0.000 ), float2( -0.924, -0.383 ),
	float2( -0.707, -0.707 ), float2( -0.383, -0.924 ),
	float2(  0.000, -1.000 ), float2(  0.383, -0.924 ),
	float2(  0.707, -0.707 ), float2(  0.924, -0.383 )
};

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float4 here = FramePicture( g_flMask, i.uv );

	//On the shape. The inside glow, if there is one, and no line: a line is what happens beside a shape.
	if ( here.a > 0.0 )
		return float4( here.rgb * here.a * g_flInside, 1.0 );

	float width = max( g_flWidth, 0.0 );

	if ( width <= 0.0 ) return float4( 0.0, 0.0, 0.0, 1.0 );

	float2 step = width * g_vFrameSize.zw;

	//The nearest marked pixel wins, and how visible it was decides how strong the line is - a shape behind a wall was marked at less
	//than one and its line comes out fainter, which is the whole of the difference between the two colours the effect was given.
	float3 colour = float3( 0.0, 0.0, 0.0 );
	float found = 0.0;

	for ( int tap = 0; tap < 16; ++tap )
	{
		//Two radii: the edge of the line and half way in, so a wide line is a line and not a ring of dots.
		float4 outer = FramePicture( g_flMask, i.uv + ( kRing[tap] * step ) );
		float4 inner = FramePicture( g_flMask, i.uv + ( kRing[tap] * step * 0.5 ) );

		float4 near = inner.a >= outer.a ? inner : outer;

		if ( near.a > found )
		{
			found = near.a;
			colour = near.rgb;
		}
	}

	return float4( colour * found, 1.0 );
}
