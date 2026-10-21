//Screen-space reflections via hierarchical depth trace; misses go on as hardware rays where traced (RayPs), else fade into
//the probe or sky. Rough surfaces send one GGX ray a texel a frame, which ResolvePs gathers over neighbours and frames

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

//The full frame's alone: it reads the table of pictures - see ScreenSpaceReflections
#define ENGINE_FULL_FRAME 1

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

	//This frame's turn of the rough rays, a whole number.
	float g_flTurn < Attribute( "Turn" ); >;

	//For ResolvePs: this frame's trace, the last resolve, and whether that is this view's at this size
	float g_flReflection < Attribute( "Reflection" ); >;
	float g_flHistory < Attribute( "History" ); >;
	float g_flAccumulate < Attribute( "Accumulate" ); >;

}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

#include "ray_hit.slang"

#ifndef PORTABLE
//A hardware ray where the screen has nothing: what leaves the front of the first thing it meets
bool RayFound( float3 from, float3 way, out float3 light )
{
	light = 0.0;

	if ( g_frame[0].rays.z < 0.5 ) return false;

	RaytracingAccelerationStructure scene = RayScene();

	RayDesc ray;
	ray.Origin = from;
	ray.Direction = way;
	ray.TMin = 0.0;
	ray.TMax = 1000.0;

	RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
	query.TraceRayInline( scene, RAY_FLAG_NONE, 0xFF, ray );
	query.Proceed();

	if ( query.CommittedStatus() != COMMITTED_TRIANGLE_HIT || !query.CommittedTriangleFrontFace() ) return false;

	float3 corners[3] = query.CommittedGetIntersectionTriangleVertexPositions();
	float3x4 toWorld = query.CommittedObjectToWorld3x4();

	float3 a = mul( toWorld, float4( corners[0], 1.0 ) );
	float3 b = mul( toWorld, float4( corners[1], 1.0 ) );
	float3 c = mul( toWorld, float4( corners[2], 1.0 ) );

	float3 n = normalize( cross( b - a, c - a ) );
	if ( dot( n, way ) > 0.0 ) n = -n;

	light = HitLight( scene, from + ( way * query.CommittedRayT() ), n, query.CommittedInstanceID(), 0.0 );
	return true;
}
#endif

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

//Below this roughness a surface is a mirror: one ray, nothing gathered over frames
static const float RoughFrom = 0.05;

//Interleaved gradient noise, nought to one
float Noise( float2 pixel ) { return frac( 52.9829189 * frac( dot( pixel, float2( 0.06711056, 0.00583715 ) ) ) ); }

//A microfacet normal the eye sees, GGX of width alpha, from two numbers nought to one - Heitz 2018's visible normals
float3 Facet( float3 normal, float3 toEye, float alpha, float2 u )
{
	//Duff et al.'s basis about the normal
	float side = normal.z >= 0.0 ? 1.0 : -1.0;
	float a = -1.0 / ( side + normal.z );
	float b = normal.x * normal.y * a;
	float3 tangent = float3( 1.0 + side * normal.x * normal.x * a, side * b, -side * normal.x );
	float3 bitangent = float3( b, side + normal.y * normal.y * a, -normal.y );

	float3 v = float3( dot( toEye, tangent ), dot( toEye, bitangent ), dot( toEye, normal ) );
	float3 stretched = normalize( float3( alpha * v.x, alpha * v.y, v.z ) );

	float across = ( stretched.x * stretched.x ) + ( stretched.y * stretched.y );
	float3 t1 = across > 0.0 ? float3( -stretched.y, stretched.x, 0.0 ) * rsqrt( across ) : float3( 1.0, 0.0, 0.0 );
	float3 t2 = cross( stretched, t1 );

	float r = sqrt( u.x );
	float phi = 6.2831853 * u.y;
	float p1 = r * cos( phi );
	float p2 = r * sin( phi );
	float s = 0.5 * ( 1.0 + stretched.z );
	p2 = ( ( 1.0 - s ) * sqrt( max( 1.0 - p1 * p1, 0.0 ) ) ) + ( s * p2 );

	float3 h = ( p1 * t1 ) + ( p2 * t2 ) + ( sqrt( max( 1.0 - p1 * p1 - p2 * p2, 0.0 ) ) * stretched );
	h = normalize( float3( alpha * h.x, alpha * h.y, max( h.z, 0.0 ) ) );

	return normalize( ( h.x * tangent ) + ( h.y * bitangent ) + ( h.z * normal ) );
}

//Nearest full-res pixel a half-res pixel stands for, so edges don't blend depth and normals
float2 StandsFor( float2 uv )
{
	uint wide, tall, levels;
	g_textures[uint( g_vFramePictures.z )].GetDimensions( 0u, wide, tall, levels );
	float2 size = float2( float( wide ), float( tall ) );

	return ( floor( uv * size ) + 0.5 ) / size;
}

//The trace, and where rays is set a hardware ray past what the screen holds - see MainPs and RayPs
float4 Reflected( PostV2F i, bool rays )
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

	//A rough surface reflects about a visible-microfacet normal, drawn anew each texel and frame
	float rough = SceneRoughness( uv );

	if ( rough > RoughFrom )
	{
		float2 pixel = i.position.xy + ( g_flTurn * float2( 5.588238, 7.243241 ) );
		float2 u = float2( Noise( pixel ), Noise( pixel + float2( 47.0, 17.0 ) ) );

		float3 facet = Facet( normal, toEye, rough * rough, u );
		float3 drawn = reflect( -toEye, facet );

		if ( dot( drawn, normal ) > 0.0 ) ray = drawn;
	}

	ScreenHit hit = ScreenTrace( uint( g_flDepths ), uint( g_vFramePictures.z ), uint( g_vFramePictures.y ),
		place + ( normal * g_flBias ), ray, EYE_FRAME( worldToClip ), EYE_FRAME( clipToWorld ), camera,
		clamp( int( g_flSteps ), 8, 128 ) );

	float3 found = hit.found ? SceneColour( hit.uv ) : 0.0;
	float fade = hit.found ? hit.fade : 1.0;

	if ( !hit.found )
	{
#ifdef PORTABLE
		return float4( 0.0, 0.0, 0.0, plane );
#else
		float3 from = place + ( normal * ( g_flBias + ( 0.002 * length( place - camera ) ) ) );
		if ( !rays || !RayFound( from, ray, found ) ) return float4( 0.0, 0.0, 0.0, plane );
#endif
	}

	//Found colour minus what the surface already reflects; may go negative, which is correct
	float3 already = EnvironmentAt( ProbesHere( place ), place, normal, ray, 0.0 );

	//The surface's alpha is the weight that environment went in with, the sky's reach in this place
	//included - see surface_shading.slang - so what goes back in is weighed alike, by the lay.

	//And seen through the air between the surface and the eye, as the rest of the surface was: the fog's own transmittance there.
	float2 screen = uv * g_frame[0].settings.yz;
	float3 through = ApplyFog( float3( 1.0, 1.0, 1.0 ), place, camera, screen ) - ApplyFog( float3( 0.0, 0.0, 0.0 ), place, camera, screen );

	return float4( ( found - already ) * through * fade, plane );
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	return Reflected( i, false );
}

//MainPs with the hardware rays behind it, where the card traces them - see ScreenSpaceReflections
[shader("fragment")]
float4 RayPs( PostV2F i ) : SV_Target0
{
	return Reflected( i, true );
}

//MainPs's rays gathered: a rough texel takes neighbours on its plane, then history clamped to their span
//A mirror's one ray passes on; alpha stays the plane the lay weighs by
[shader("fragment")]
float4 ResolvePs( PostV2F i ) : SV_Target0
{
	uint wide, tall, levels;
	g_textures[uint( g_flReflection )].GetDimensions( 0u, wide, tall, levels );
	int2 size = int2( wide, tall );
	int2 at = min( int2( i.position.xy ), size - 1 );

	float4 now = g_textures[uint( g_flReflection )].Load( int3( at, 0 ) );
	if ( now.a < 0.0 ) return now;

	float2 uv = StandsFor( i.uv );
	float rough = SceneRoughness( uv );
	if ( rough <= RoughFrom ) return now;

	float depth = SceneDepthRaw( uv );
	float3 camera = EYE_FRAME( camera ).xyz;
	float3 place = PlaceOf( uv, depth );

	//How far off this texel's plane a neighbour may lie and still count - as the lay asks of the four it weighs
	float within = 0.02 + ( 0.01 * length( camera - place ) );

	float3 sum = 0.0;
	float3 m2 = 0.0;
	float count = 0.0;

	[unroll]
	for ( int y = -1; y <= 1; y++ )
	{
		[unroll]
		for ( int x = -1; x <= 1; x++ )
		{
			float4 one = g_textures[uint( g_flReflection )].Load( int3( clamp( at + int2( x, y ), int2( 0, 0 ), size - 1 ), 0 ) );
			if ( one.a < 0.0 || abs( one.a - now.a ) > within ) continue;

			sum += one.rgb;
			m2 += one.rgb * one.rgb;
			count += 1.0;
		}
	}

	float3 mean = sum / max( count, 1.0 );
	float3 sigma = sqrt( max( ( m2 / max( count, 1.0 ) ) - ( mean * mean ), 0.0 ) );

	if ( g_flAccumulate < 0.5 ) return float4( mean, now.a );

	//Where this point was a frame ago by the view's move; its reflection moves near enough with it when rough
	float4 before = mul( float4( place, 1.0 ), EYE_FRAME( previousWorldToClip ) );
	float2 was = before.w > 1e-6 ? ( ( before.xy / before.w ) * 0.5 ) + 0.5 : float2( -1.0, -1.0 );

	if ( any( was <= 0.0 ) || any( was >= 1.0 ) ) return float4( mean, now.a );

	float4 history = g_textures[uint( g_flHistory )].SampleLevel( was, 0.0 );
	if ( history.a < 0.0 ) return float4( mean, now.a );

	float3 kept = clamp( history.rgb, mean - ( sigma * 1.5 ), mean + ( sigma * 1.5 ) );

	return float4( lerp( kept, mean, 0.15 ), now.a );
}
