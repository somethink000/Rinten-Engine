//Glass pane: mostly see-through, turning to a mirror at grazing angles (Fresnel); no refraction

FEATURES
{
	//Rippled, frosted or cracked glass; on when the material names a normal map
	Feature( F_NORMAL_MAP, 0..1, "Maps" ) < Map( Normal ); >;

	//Both sides drawn: a windscreen is seen from inside, a window in a wall is not
	Feature( F_BACKFACES, 0..1, "Rendering" );

	//A pane is see-through by being a pane, so this one is on to begin with - see CoreSceneRenderer.DrawFlags.
	Feature( F_TRANSLUCENT, 0..1, "Rendering" ) < Default( 1 ); >;
}

PARAMETERS
{
	//The one the engine has always carried, in its usual place. Low, because a pane is polished: this is the
	//difference between a window and a bathroom window, and past about a third there is no glass left in it.
	float g_flRoughness < Default( 0.05 ); Range( 0, 1 ); UiGroup( "Glass" ); >;

	//How much light the pane holds back regardless of angle; 0.1 is a clean window
	float g_flOpacity < Default( 0.1 ); Range( 0, 1 ); UiGroup( "Glass" ); >;

	//Reflectance when viewed straight on; 0.04 is real glass, more for showy shop windows
	float g_flReflectance < Default( 0.04 ); Range( 0.02, 0.5 ); UiGroup( "Glass" ); >;
}

#include "depth_id.slang"

struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;
	float3 world : TEXCOORD2;
	float3 normal : NORMAL;
	float3 tangent : TANGENT;
	float bitangentSign : TEXCOORD3;
	float2 uv : TEXCOORD0;
};

//The push block every world draw carries is the engine's, not this shader's - see frame.slang.
#define ENGINE_WORLD_PUSH 1

#include "frame.slang"
#include "surface_shading.slang"

//The vertex body, which the classic entry below and the engine's cluster entry both run - see cluster_surface.slang.
V2F Vertex( VIn i )
{
	float3 position = i.position;
	float3 normal = i.normal;
	float3 tangent = i.tangent.xyz;

	row_major float4x4 modelToWorld = g_modelToWorld;

#ifdef SKINNED
	float4x4 skin = SkinAt( i.bones, i.weights );

	position = mul( float4( position, 1.0 ), skin ).xyz;
	normal = mul( float4( normal, 0.0 ), skin ).xyz;
	tangent = mul( float4( tangent, 0.0 ), skin ).xyz;
#endif

	float3 world = mul( float4( position, 1.0 ), modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.world = world;
	o.normal = mul( float4( normal, 0.0 ), modelToWorld ).xyz;
	o.tangent = mul( float4( tangent, 0.0 ), modelToWorld ).xyz;
	o.bitangentSign = i.tangent.w < 0.0 ? -1.0 : 1.0;
	o.uv = i.texCoord0.xy;

	DEPTH_ID_SET( o, i.texCoord0.xy );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

//The pixel body, which the classic entry at the end and the engine's cluster
//entry both run. Which side of the pane this is does not come into it - see below.
Out Shade( V2F i, bool front )
{
	float2 uv = i.uv;
#ifdef DEPTH_ONLY
	//Depth pre-pass writes no colour; the guard keeps the lit stage out of depth builds
	Out nothing = (Out)0;
	return nothing;
#endif


	float3 toEye = EYE_FRAME( camera ).xyz - i.world;
	float away = max( length( toEye ), 1e-6 );

	toEye /= away;

	float3 n = normalize( i.normal );

	//Flip the normal toward the eye instead of SV_IsFrontFace; right for thin sheets, wrong for solids
	if ( dot( n, toEye ) < 0.0 ) n = -n;

	float4 texel = g_color.Sample( uv );

#if F_NORMAL_MAP
	float3 t = TangentAgainst( n, i.tangent );
	float3 b = cross( n, t ) * i.bitangentSign;

	float3 sampled = g_normal.Sample( uv ).rgb * 2.0 - 1.0;

	float3 normal = normalize( sampled.x * t + sampled.y * b + sampled.z * n );
#else
	float3 normal = n;
#endif

	//Schlick Fresnel: reflectance rises from f0 face-on to 1 at grazing angles
	float f0 = g_flReflectance;
	float facing = saturate( dot( normal, toEye ) );
	float mirrored = f0 + ( ( 1.0 - f0 ) * pow( 1.0 - facing, 5.0 ) );

	//And how much of this pixel is therefore the pane rather than what is behind it:
	//what it holds back at any angle, and then the mirror on top of what is left.
	float hold = saturate( g_flOpacity );
	float alpha = saturate( hold + ( ( 1.0 - hold ) * mirrored ) );

	//The pane's own colour: the map, the material's tint, and the draw's.
	float3 body = texel.rgb * g_tint.rgb;

	//Metalness 1 with albedo = reflectance shades it as a pure mirror: no diffuse or ambient term
	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = n;
	surface.albedo = float3( f0, f0, f0 );
	surface.alpha = alpha;
	surface.roughness = clamp( g_flRoughness, 0.02, 1.0 );
	surface.metalness = 1.0;
	surface.occlusion = 1.0;

	//Pane tint lit by sky and probes, sent through emission since a mirror has no diffuse
	float3 ambient = SceneAmbient( ProbesOver( ProbesHere( i.world ), HasSky() ? SkyAmbient( normal ) * SkyAmbientScale() : float3( 0.0, 0.0, 0.0 ),
		i.world, normal, 1.0, false ) );

	surface.emission = body * ambient * hold;

	surface.uv = uv;
	surface.id = i.depthId;

	//Nothing is baked into a pane: in a bake it is a hole the light went through
	surface.lightmap = float2( 0.0, 0.0 );
	surface.screen = i.position.xy;

	Shaded lit = ShadeSurface( surface );

	//What the mirror direction meets on screen replaces the probe or sky reflection - see ScreenReflected
	float3 bounce = reflect( -toEye, surface.normal );
	float4 found = ScreenReflected( i.world, surface.normal, bounce );

	if ( found.a > 0.0 )
	{
		float3 was = EnvironmentAt( ProbesHere( i.world ), i.world, surface.normal, bounce, surface.roughness );
		float3 through = ApplyFog( 1.0.xxx, i.world, EYE_FRAME( camera ).xyz, i.position.xy ) - ApplyFog( 0.0.xxx, i.world, EYE_FRAME( camera ).xyz, i.position.xy );

		lit.colour = max( lit.colour + ( ( found.rgb - was ) * lit.mirror * found.a * through ), 0.0 );
	}

	Out o;

	//Divide by alpha so the blend doesn't scale the reflection twice; fog counts toward the share
	float haze = kFogOn != 0 ? FogAmount( i.world, EYE_FRAME( camera ).xyz ) : 0.0;

	float share = saturate( haze + ( ( 1.0 - haze ) * alpha * g_tint.a ) );

	o.colour = float4( lit.colour / max( share, 1e-3 ), share );

#ifdef TARGET_SCENE
	//Left empty on purpose: SSR would read this pane's normal against the depth behind it
	o.surface = float4( 0.0, 0.0, 0.0, 0.0 );
#endif

	//Tonemap here when this is the last pass; alpha matters since Linux composites the window
#ifdef TARGET_SCREEN
	//A pane always blends, so what it leaves in alpha is its own - see ScreenAlpha in frame.slang.
	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( true, o.colour.a ) );
#endif

	return o;
}

[shader("fragment")]
Out MainPs( V2F i )
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

	return Shade( i, true );
}

//No refraction (no frame copy to sample) and tint only darkens, since the blend can't multiply
