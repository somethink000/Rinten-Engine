//Water: Fresnel blend of what's below and the sky, depth fade via DepthPicture, scrolling ripples

FEATURES
{
	//The ripples. On by the material naming a normal map, like everywhere else; off is a still sheet, which a puddle in a room may be.
	Feature( F_NORMAL_MAP, 0..1, "Maps" ) < Map( Normal ); >;

	//Water is see-through by being water.
	Feature( F_TRANSLUCENT, 0..1, "Rendering" ) < Default( 1 ); >;
}

PARAMETERS
{
	//The water's own colour: what a shallow reach over a pale bed looks like, and what the deep goes to. Both
	//are what the water adds, over what is seen through it - a tannin stream is brown, a mountain one green-blue.
	float3 g_vShallowColour < Default3( 0.08, 0.14, 0.10 ); UiGroup( "Water" ); >;
	float3 g_vDeepColour < Default3( 0.02, 0.05, 0.045 ); UiGroup( "Water" ); >;

	//How far down, in metres, the water has become all its own colour and nothing of the bed shows. Small for a clear stream, large for a lake.
	float g_flDepthFade < Default( 0.6 ); Range( 0.05, 20 ); UiGroup( "Water" ); >;

	//How polished the surface is. Low: the sun is a tight glint on every ripple, which is what moving water does.
	float g_flRoughness < Default( 0.08 ); Range( 0.02, 1 ); UiGroup( "Water" ); >;

	//How much of the sky the surface mirrors, on top of the Fresnel curve. Under a canopy the sky the table
	//holds is not the sky the water sees - it sees leaves - and a full mirror of it is a white sheet in a forest.
	float g_flSkyReflection < Default( 0.5 ); Range( 0, 1 ); UiGroup( "Water" ); >;

	//The ripples: how big, in metres a repeat of the normal map covers; which way and how fast the water
	//flows, in metres a second; and how strong the map is read, which is how choppy the surface looks.
	float g_flRippleScale < Default( 1.5 ); Range( 0.1, 50 ); UiGroup( "Ripples" ); Feature( F_NORMAL_MAP ); >;
	float2 g_vFlow < Default2( 0.0, 0.25 ); UiGroup( "Ripples" ); Feature( F_NORMAL_MAP ); >;
	float g_flRippleStrength < Default( 0.6 ); Range( 0, 2 ); UiGroup( "Ripples" ); Feature( F_NORMAL_MAP ); >;
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

#define ENGINE_WORLD_PUSH 1
#include "frame.slang"
#include "surface_shading.slang"

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

Out Shade( V2F i )
{
	float3 toEye = EYE_FRAME( camera ).xyz - i.world;
	float away = max( length( toEye ), 1e-6 );
	toEye /= away;

	float3 n = normalize( i.normal );

	//Seen from underneath, the surface is still a surface.
	if ( dot( n, toEye ) < 0.0 ) n = -n;

#if F_NORMAL_MAP
	//Two copies of the ripples, on the world's own ground plane so a sheet of any shape wears them evenly, going
	//the way the flow goes and a little across it, at different sizes, so the pattern never repeats as a pattern.
	float2 ground = i.world.xz / g_flRippleScale;
	float2 drift = g_vFlow * g_frame[0].time.z / g_flRippleScale;

	float3 t = TangentAgainst( n, i.tangent );
	float3 b = cross( n, t ) * i.bitangentSign;

	float3 one = g_normal.Sample( ground + drift ).rgb * 2.0 - 1.0;
	float3 two = g_normal.Sample( ( ground * 1.9 ) - ( drift * 1.3 ) + float2( 0.37, 0.61 ) ).rgb * 2.0 - 1.0;

	float3 sampled = normalize( float3( ( one.xy + two.xy ) * g_flRippleStrength, one.z * two.z ) );
	float3 normal = normalize( sampled.x * t + sampled.y * b + sampled.z * n );
#else
	float3 normal = n;
#endif

	//How far down the bed is under this pixel - the depth the pre-pass wrote, turned back into a
	//point, against the surface - and what of the water's own colour and body that depth gives it.
	float below = g_flDepthFade;

	if ( HasDepthBehind() )
	{
		float raw = DepthBehind( i.position.xy );

		if ( raw < 1.0 )
		{
			//The view distance to the bed less the view distance to the surface: what the ray crosses inside the water.
			float3 bed = WorldBehind( i.position.xy, raw );
			float bedAway = length( EYE_FRAME( camera ).xyz - bed );

			below = max( bedAway - away, 0.0 );
		}
	}

	float body = 1.0 - exp( -below / max( g_flDepthFade, 0.01 ) );

	//The Fresnel curve, water's own: two per cent looked straight into, everything at a glance along it.
	const float f0 = 0.02;

	float facing = saturate( dot( normal, toEye ) );
	float mirrored = f0 + ( ( 1.0 - f0 ) * pow( 1.0 - facing, 5.0 ) );

	//What the water is over what is under it: its colour by depth, seen where the surface is not a mirror; the sky where it is.
	float3 colour = lerp( g_vShallowColour, g_vDeepColour, body ) * g_tint.rgb;

	//What lies the mirror way: the room where a probe covers the water, the sky where none does - the same environment
	//every surface reflects, so a pool indoors shows the room and not a sky it cannot see. See EnvironmentAt.
	float3 mirrorWay = reflect( -toEye, normal );
	float mirrorRough = clamp( g_flRoughness, 0.02, 1.0 );
	float skyLeft;
	float3 probed = ProbesPart( ProbesHere( i.world ), i.world, mirrorWay, mirrorRough, skyLeft );
	float3 lookAt = normalize( lerp( mirrorWay, normal, mirrorRough * mirrorRough ) );
	float3 open = HasSky() ? SkyReflected( lookAt, mirrorRough ) : SceneAmbient( float3( 0.0, 0.0, 0.0 ) );
	float3 sky = probed + ( open * skyLeft );

	//Sky Reflection holds back only the open sky, which a canopy hides; a probe shows what is really there
	float allowed = mirrored * lerp( 1.0, g_flSkyReflection, skyLeft );

	//Lit as a polished dark surface: the sun's glint off the ripples and the sky's ambient in the body, with the fog the
	//frame has. The reflection of the sky goes in as emission, at the Fresnel share and what the material allows of it.
	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = n;
	surface.albedo = colour;
	surface.alpha = 1.0;
	surface.roughness = clamp( g_flRoughness, 0.02, 1.0 );
	surface.metalness = 0.0;
	surface.occlusion = 1.0;

	//Subtract the sky the engine already reflects so the pond doesn't mirror it twice
	float3 already = EnvBrdf( float3( 0.04, 0.04, 0.04 ), surface.roughness, facing );

	surface.emission = sky * max( allowed - already, 0.0 );
	surface.uv = i.uv;
	surface.id = i.depthId;
	surface.lightmap = float2( 0.0, 0.0 );
	surface.screen = i.position.xy;

	Shaded lit = ShadeSurface( surface );

	//And what the mirror way meets on the screen - the bank, the trees, a boat - in place of the room or the sky, at the weight that environment went in
	//with: the engine's own reflection and the emission's share above. Seen through the air in front of the water as the rest of it is. See ScreenReflected.
	float4 found = ScreenReflected( i.world, normal, reflect( -toEye, normal ) );

	if ( found.a > 0.0 )
	{
		float weight = lit.mirror + LightStrength( max( allowed - already, 0.0 ) );
		float3 through = ApplyFog( 1.0.xxx, i.world, EYE_FRAME( camera ).xyz, i.position.xy ) - ApplyFog( 0.0.xxx, i.world, EYE_FRAME( camera ).xyz, i.position.xy );

		lit.colour = max( lit.colour + ( ( found.rgb - sky ) * weight * found.a * through ), 0.0 );
	}

	//How much of what is under it shows: the body of the water by its depth, and the mirror on
	//top of that whatever the depth - nothing at the bank, where the bed comes up to the surface.
	float alpha = saturate( body + ( ( 1.0 - body ) * mirrored ) ) * g_tint.a;

	Out o;
	o.colour = float4( lit.colour, alpha );

#ifdef TARGET_SCENE
	//Not a surface anything after this should treat as one - see glass.shader.
	o.surface = float4( 0.0, 0.0, 0.0, 0.0 );
#endif

#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( true, o.colour.a ) );
#endif

	return o;
}

//Two-argument Shade overload needed for the cluster and instanced entry points to compile
Out Shade( V2F i, bool front )
{
	return Shade( i );
}

[shader("fragment")]
Out MainPs( V2F i )
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

	return Shade( i );
}
