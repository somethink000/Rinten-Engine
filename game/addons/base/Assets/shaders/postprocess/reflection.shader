//Screen-space reflections via hierarchical depth trace; misses fade into the probe or sky

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//How far off the surface the ray starts, so it does not hit itself.
	float g_flBias < Attribute( "Bias" ); Default( 0.02 ); >;

	//How many times the ray reads the chain at most - see ScreenSpaceReflections.ReflectionSteps.
	float g_flSteps < Attribute( "Steps" ); Default( 48.0 ); >;

	//The chain of the nearest depth, by its place in the table.
	float g_flDepths < Attribute( "Depths" ); >;

}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

//The solid world as it now stands, reflections and all, at the size it is drawn into - for the water and the glass drawn
//after it, which trace their own rays and cannot read the picture they are drawn into. See ScreenReflected in frame.slang.
[shader("fragment")]
float4 CopyPs( PostV2F i ) : SV_Target0
{
	return float4( SceneColour( i.uv ), 1.0 );
}

//The first level of the chain: the nearest of the four depths under each of its texels - it is half the depth's size each way, as the reflection is.
[shader("fragment")]
float4 NearestPs( PostV2F i ) : SV_Target0
{
	float2 half = 0.5 * g_vFrameSize.zw;

	return min(
		min( SceneDepthRaw( i.uv + float2( -half.x, -half.y ) ), SceneDepthRaw( i.uv + float2( half.x, -half.y ) ) ),
		min( SceneDepthRaw( i.uv + float2( -half.x, half.y ) ), SceneDepthRaw( i.uv + float2( half.x, half.y ) ) ) );
}

//The trace itself is ScreenTrace in screen_trace.slang, which frame.slang brings in - water and glass trace with the same one.

//Nearest full-res pixel a half-res pixel stands for, so edges don't blend depth and normals
float2 StandsFor( float2 uv )
{
	uint wide, tall, levels;
	g_textures[uint( g_vFramePictures.z )].GetDimensions( 0u, wide, tall, levels );
	float2 size = float2( float( wide ), float( tall ) );

	return ( floor( uv * size ) + 0.5 ) / size;
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	//What it found, less what was there - and in the alpha which plane it was traced from: how far the eye stands off the plane of that pixel, which
	//the lay weighs its four by; less than nought where it was no mirror to trace from. How much of a mirror is the lay's, at the world's own size.
	float2 uv = StandsFor( i.uv );
	float4 surface = SceneSurface( uv );

	//Nothing worth tracing below a point where the result would be invisible anyway.
	if ( surface.a <= 0.01 || g_flDepths <= 0.0 ) return float4( 0.0, 0.0, 0.0, -1.0 );

	float depth = SceneDepthRaw( uv );
	if ( depth >= 1.0 ) return float4( 0.0, 0.0, 0.0, -1.0 );

	float3 camera = EYE_FRAME( camera ).xyz;
	float3 place = PlaceOf( uv, depth );
	float3 normal = normalize( ( surface.rgb * 2.0 ) - 1.0 );

	//Off the eye's side of it, so above nought for anything it can see.
	float plane = max( dot( camera - place, normal ), 0.0 );

	float3 toEye = normalize( camera - place );
	float3 ray = reflect( -toEye, normal );

	ScreenHit hit = ScreenTrace( uint( g_flDepths ), uint( g_vFramePictures.z ), uint( g_vFramePictures.y ),
		place + ( normal * g_flBias ), ray, EYE_FRAME( worldToClip ), EYE_FRAME( clipToWorld ), camera,
		clamp( int( g_flSteps ), 8, 128 ) );

	if ( !hit.found ) return float4( 0.0, 0.0, 0.0, plane );

	//Found colour minus what the surface already reflects; may go negative, which is correct
	float3 already = EnvironmentAt( ProbesHere( place ), place, normal, ray, 0.0 );

	//The surface's alpha is the weight that environment went in with, the sky's reach in this place
	//included - see surface_shading.slang - so what goes back in is weighed alike, by the lay.

	//And seen through the air between the surface and the eye, as the rest of the surface was: the fog's own transmittance there.
	float2 screen = uv * g_frame[0].settings.yz;
	float3 through = ApplyFog( float3( 1.0, 1.0, 1.0 ), place, camera, screen ) - ApplyFog( float3( 0.0, 0.0, 0.0 ), place, camera, screen );

	return float4( ( SceneColour( hit.uv ) - already ) * through * hit.fade, plane );
}
