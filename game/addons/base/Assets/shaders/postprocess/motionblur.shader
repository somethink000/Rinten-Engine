//Per-pixel motion blur with velocity-weighted reconstruction, first in the post chain

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//The length of the smear, which is the shutter: low is a drift, high is a launch. One is what the camera actually did between the two frames.
	float g_flStrength < Attribute( "Strength" ); Default( 1.0 ); Range( 0, 8 ); UiGroup( "Motion Blur" ); >;

	//How much to hold the middle of the frame sharp and drive the smear out to the edges, which is the shape a lens gives it.
	float g_flEdgeBias < Attribute( "EdgeBias" ); Default( 0.0 ); Range( 0, 1 ); UiGroup( "Motion Blur" ); >;

	//The ceiling on the smear, in pixels of the frame. Without one a fast turn asks for a streak across the screen rather than a blur.
	float g_flMaxBlur < Attribute( "MaxBlur" ); Default( 64.0 ); Range( 4, 256 ); UiGroup( "Motion Blur" ); >;

	//How many taps to spend, four to sixteen - the quality setting.
	float g_flSamples < Attribute( "Samples" ); Default( 12.0 ); Range( 4, 16 ); UiGroup( "Motion Blur" ); >;

	//Nearer than this, in metres, the frame is held sharp: a first-person cabin, a viewmodel, hands on a wheel. Nothing at all at nought.
	float g_flNearSharp < Attribute( "NearSharp" ); Default( 0.0 ); Range( 0, 20 ); UiGroup( "Motion Blur" ); >;
}

#include "postprocess.slang"

//Interleaved gradient noise - a cheap dither along the smear, so that a dozen taps read as a smear rather than as a dozen copies.
float Ign( float2 at )
{
	return frac( 52.9829189 * frac( dot( at, float2( 0.06711056, 0.00583715 ) ) ) );
}

//Whether the camera stood still since the last frame - the same matrix both times. Then nothing moved but what wrote
//its own movement, and a pixel that wrote none is still without its depth being read and put through two matrices.
bool CameraStill()
{
	return all( EYE_FRAME( worldToClip )[0] == EYE_FRAME( previousWorldToClip )[0] ) &&
		all( EYE_FRAME( worldToClip )[1] == EYE_FRAME( previousWorldToClip )[1] ) &&
		all( EYE_FRAME( worldToClip )[2] == EYE_FRAME( previousWorldToClip )[2] ) &&
		all( EYE_FRAME( worldToClip )[3] == EYE_FRAME( previousWorldToClip )[3] );
}

//Metres behind a velocity writer still counted as it, for glass that writes motion but no depth
static const float kInteriorDepth = 4.5;

//How far this pixel moved across the screen, in pixels. What wrote its own movement is believed where the depth agrees that it is still the
//thing in front: a car behind a lamp post must not smear the lamp post. Everything else is the camera's own movement, worked out from the depth.
float2 MotionAt( float2 uv, bool still )
{
	float4 own = SceneMotionFull( uv );

	if ( own.w > 0.5 )
	{
		float scene = SceneDepth( uv );
		float behind = scene - own.z;

		//Asymmetric: nearer scene means occluded, use camera motion; further within window means glass
		float window = min( kInteriorDepth, own.z * 0.5 );

		if ( behind > -( scene * 0.05 + 0.4 ) && behind < window )
			return own.xy * g_vFrameSize.xy;
	}

	return still ? float2( 0.0, 0.0 ) : SceneMotionCamera( uv ) * g_vFrameSize.xy;
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float2 screen = i.uv * g_vFrameSize.xy;

	//Wider at the edges, sharper in the middle, where a lens does the same.
	float2 away = i.uv - 0.5;
	float edge = saturate( dot( away, away ) * 4.0 );
	float shutter = g_flStrength * lerp( 1.0 - g_flEdgeBias, 1.0, edge );

	bool still = CameraStill();

	float2 streak = MotionAt( i.uv, still ) * shutter;

	//The near sharp band: a cabin, a viewmodel. Held whatever it is doing, because it moves with the eye and a smear on it reads as a fault.
	if ( g_flNearSharp > 0.0 )
		streak *= saturate( SceneDepth( i.uv ) / g_flNearSharp - 1.0 );

	float length2 = length( streak );

	if ( length2 > g_flMaxBlur )
	{
		streak *= g_flMaxBlur / max( length2, 1e-4 );
		length2 = g_flMaxBlur;
	}

	//Less than a pixel is not a smear. A thing travelling with the camera lands here by itself: its movement across the screen is nothing.
	if ( length2 < 1.0 ) return float4( SceneColour( i.uv ), 1.0 );

	float2 along = streak / length2;
	float2 step = streak * g_vFrameSize.zw;
	float dither = Ign( screen );

	int taps = int( clamp( g_flSamples, 4.0, 16.0 ) );

	//The middle always counts, at its full weight.
	float3 colour = SceneColour( i.uv );
	float weight = 1.0;

	for ( int tap = 0; tap < taps; ++tap )
	{
		float t = ( ( float( tap ) + dither ) / float( taps ) ) - 0.5;

		float2 at = i.uv + ( step * t );

		//A tap reaches the middle only if its own movement carries it here. Something still beside
		//something moving does not join the smear, and two things moving different ways each keep their own.
		float2 theirs = MotionAt( at, still ) * shutter;
		float reach = min( abs( dot( theirs, along ) ), g_flMaxBlur );

		float away2 = abs( t ) * length2;
		float w = saturate( 1.0 - ( away2 - ( reach * 0.5 ) ) * 0.5 );

		colour += SceneColour( at ) * w;
		weight += w;
	}

	return float4( colour / weight, 1.0 );
}
