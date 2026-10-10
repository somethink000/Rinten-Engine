//A sky from a panorama: an equirectangular HDR picture (.exr, .hdr) drawn round the world. The table its ambient
//reads and its reflections are the engine's, made from the same picture and numbers - see CorePanoramaSky

PARAMETERS
{
	//The panorama, two to one, the horizon across its middle. Linear light: an .exr or .hdr compiles to RGB9E5
	Texture2D g_tSkyTexture < Default( 0.0 ); UiGroup( "Sky" ); >;

	//Multiplies the picture, for the sky and for what it lights alike
	float g_flBrightness < Default( 1.0 ); Range( 0, 16 ); UiGroup( "Sky" ); >;

	//Degrees about up, to put the picture's sun where the scene's is
	float g_flRotation < Default( 0.0 ); Range( 0, 360 ); UiGroup( "Sky" ); >;
}

//A sky, and one drawn from a panorama - see CoreResources.IsSky and IsPanorama
#define ENGINE_SKY 1
#define ENGINE_SKY_PANORAMA 1

#include "frame.slang"

struct V2F
{
	float4 position : SV_Position;

	//The world ray for this pixel, interpolated across the screen triangle
	float3 direction : TEXCOORD0;

#ifdef STEREO
	//Which eye; the sky carries its own, having no DEPTH_ID_FIELD - see Eye() in frame_data.slang
	nointerpolation uint eyeId : TEXCOORD1;
#endif
};

//View direction without the camera's position, so the sky does not drift far from the origin
float3 SkyRay( float2 clip )
{
	float3 ray = mul( float4( clip, 1.0, 1.0 ), EYE_FRAME( rayToWorld ) ).xyz;

	if ( dot( ray, ray ) > 0.0 ) return ray;

	float4 world = mul( float4( clip, 1.0, 1.0 ), EYE_FRAME( clipToWorld ) );

	return ( world.xyz / world.w ) - EYE_FRAME( camera ).xyz;
}

[shader("vertex")]
V2F MainVs( uint id : SV_VertexID )
{
	//One triangle covering the screen: (-1,-1), (3,-1), (-1,3) in clip space
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );
	float2 clip = ( corner * 2.0 ) - 1.0;

	V2F o;

	//At the far plane, behind everything solid
	o.position = float4( clip, 1.0, 1.0 );
	o.direction = SkyRay( clip );

#ifdef STEREO
	o.eyeId = ViewOfThisVertex();
#endif

	return o;
}

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

[shader("fragment")]
Out MainPs( V2F i )
{
#ifdef STEREO
	g_eyeId = i.eyeId;
#endif

	if ( !HasSky() ) discard;

#ifdef SKY_CAPTURE
	float3 direction = PanoramaDirection( i.position.xy / SkyCaptureSize );
#else
	float3 direction = normalize( i.direction );
#endif

	//The scene's SkyBox2D lays its tint in with these; a preview with no scene draws the material's own
	bool laid = g_frame[0].panorama.w > 0.5;
	float turn = laid ? g_frame[0].panorama.y : radians( g_flRotation );
	float3 light = laid ? g_frame[0].panoramaLight.rgb : float3( g_flBrightness, g_flBrightness, g_flBrightness );

	float2 uv = PanoramaUv( direction, turn );

	//Where u wraps from one to nought its derivative is a whole turn; taking that back out keeps the level the
	//neighbours chose, rather than a seam of the smallest one. Scaled the way UvDx is for the upscaler's bias
	float2 dx = ddx( uv );
	float2 dy = ddy( uv );

	dx.x -= round( dx.x );
	dy.x -= round( dy.x );

	float bias = exp2( MipBias() );

	float3 colour = g_tSkyTexture.SampleGrad( uv, dx * bias, dy * bias ).rgb * light;

	//A fog volume between the eye and the sky fogs the sky too; not in the capture, which has no screen
#ifndef SKY_CAPTURE
	if ( kFogOn != 0 )
	{
		float3 eye = EYE_FRAME( camera ).xyz;
		colour = VolumeFog( colour, eye + direction * 4000.0, eye, i.position.xy );
	}
#endif

	Out o;
	o.colour = float4( colour, 1.0 );

#ifdef TARGET_SCENE
	//No normal and no reflectivity: what reads this attachment takes a zero normal as nothing there
	o.surface = float4( 0.0, 0.0, 0.0, 0.0 );
#endif

	//Opaque, or the Linux window shows through
#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), 1.0 );
#endif

	return o;
}
