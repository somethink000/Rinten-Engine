//Foliage: complex.shader minus parallax, emission and taps, plus per-material vertex wind

//With wind on, vertices move, so skip the depth pre-pass and draw once in the shading pass
#define ENGINE_MOVES_VERTICES F_WIND

//Reads kFarShading: draws past the cut use a pipeline without normal and roughness maps - see CoreResources.ShadesFar
#define ENGINE_SHADES_FAR 1

FEATURES
{
	//The same maps the standard surface takes, and on to begin with for the same reason - see complex.shader,
	//where the shape of this is explained. Their gen_foliage.shader starts its roughness on in exactly this way.
	Feature( F_NORMAL_MAP, 0..1, "Maps" ) < Map( Normal ); Default( 1 ); >;
	//Roughness map, on by default; off drops the map, the value and the sample (fully matte)
	Feature( F_ROUGHNESS, 0..1, "Maps" ) < Default( 1 ); >;

	//Metalness and occlusion as complex.shader: one on for any metal, the other by its map
	Feature( F_METALNESS, 0..1, "Maps" ) < Flag( Metalness ); >;
	Feature( F_AO, 0..1, "Maps" ) < Map( g_tAO ); >;



	Feature( F_ALPHA, 0..1, "Rendering" );
	Feature( F_BACKFACES, 0..1, "Rendering" );

	//Whether it moves: off removes the clock, the vertex sample and per-vertex maths - a potted fern, a thumbnail
	Feature( F_WIND, 0..1, "Wind" );

	//Light through the leaf, most of what makes a canopy read as leaves; off for thick or waxy leaves
	Feature( F_TRANSMISSION, 0..1, "Leaf" );
}

PARAMETERS
{
	//Legacy material maps, kept in their first-slot places; Default is read when the material has none
	Texture2D g_tRoughness < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;
	Texture2D g_tAO < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_AO ); >;

	float g_flRoughness < Default( 0.85 ); Range( 0, 1 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;
	float g_flMetalness < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Maps" ); Feature( F_METALNESS ); >;
	Texture2D g_tMetalness < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_METALNESS ); >;
	float g_flAlphaCutoff < Default( 0.5 ); Range( 0, 1 ); UiGroup( "Rendering" ); Feature( F_ALPHA ); >;


	//Wind direction in the world (only where it points) and how fast it beats
	float3 g_vWindDirection < Default3( 1, 0, 0 ); UiGroup( "Wind" ); Feature( F_WIND ); >;
	float g_flWindSpeed < Default( 2.5 ); Range( 0, 10 ); UiGroup( "Wind" ); Feature( F_WIND ); >;

	//How far the top leans, in metres; six is a tree lying down
	float g_flWindStrength < Default( 0.15 ); Range( 0, 2 ); UiGroup( "Wind" ); Feature( F_WIND ); >;

	//Height above the origin that takes the full bend, in metres: the base stays planted, the tip sways
	float g_flWindHeight < Default( 2.0 ); Range( 0.05, 20 ); UiGroup( "Wind" ); Feature( F_WIND ); >;

	//Wind stiffness mask (red): white sways, black stays; defaults to white, bending by height alone
	Texture2D g_tWindMask < Default( 1.0 ); UiGroup( "Wind" ); Feature( F_WIND ); >;

	//How much light comes through the leaf, against what bounces off it. Nought is opaque; one is a leaf
	//as thin as they come, and past one is for a material that wants the glow louder than it would be.
	float g_flTransmission < Default( 0.6 ); Range( 0, 2 ); UiGroup( "Leaf" ); Feature( F_TRANSMISSION ); >;

	//What colour the light is by the time it is through. Leaf tissue takes the red and the blue and hands on a
	//yellow-green - a leaf against the sun is a brighter, warmer green than the same leaf lit from the front.
	float3 g_vTransmissionColour < Default3( 0.85, 1.0, 0.45 ); UiGroup( "Leaf" ); Feature( F_TRANSMISSION ); >;

	//How tightly the light through the leaf is thrown towards the eye. A higher number is a smaller,
	//brighter glow where the sun stands behind the leaf; a lower one spreads it over the whole shaded side.
	float g_flTransmissionSpread < Default( 4.0 ); Range( 1, 32 ); UiGroup( "Leaf" ); Feature( F_TRANSMISSION ); >;

	//Where the leaf is thick. Red, on the colour map's coordinates: white lets the light
	//through, black is a stem or a rib that does not. White where a material names no picture.
	Texture2D g_tThickness < Default( 1.0 ); UiGroup( "Leaf" ); Feature( F_TRANSMISSION ); >;

	//Per-copy hue/brightness spread from world position; 0 keeps every copy the material colour
	float g_flVariation < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Leaf" ); >;
}

#include "depth_id.slang"

//Full V2F in the depth build too; RADV strips unused outputs anyway, so trimming gained nothing
struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;
	float3 world : TEXCOORD2;
	float3 normal : NORMAL;
	float3 tangent : TANGENT;
	float bitangentSign : TEXCOORD3;
	float2 uv : TEXCOORD0;
	float2 lightmap : TEXCOORD4;

	//What this copy's colour is multiplied by - see g_flVariation.
	float3 variation : TEXCOORD5;
};

//The push block every world draw carries is the engine's, not this shader's - see frame.slang.
#define ENGINE_WORLD_PUSH 1

//The leaf's light through it, shaded with every light's - see Surface.transmission
#define SURFACE_TRANSMITS F_TRANSMISSION

#include "frame.slang"
#include "surface_shading.slang"

#if F_WIND
//Wind offset: a downwind lean with gusts, phase from object position so neighbours differ
float3 WindOffset( float3 local, float3 origin, float2 uv )
{
	//How much of the bend this vertex gets: how far above the object's own origin it sits,
	//against how tall the thing was said to be. Nothing at the base, all of it at the top.
	float height = saturate( local.y / max( g_flWindHeight, 0.01 ) );

	//Times whatever the mask says about this part of the picture. Level nought because a vertex stage
	//has no derivatives to choose a mip with, and the mask is a broad shape rather than a detail.
	float mask = height * g_tWindMask.SampleLevel( uv, 0.0 ).r;

	if ( mask <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//Scene clock, so pause and time scale apply; wall clock in an editor viewport - see CoreSceneRenderer.BuildFrame
	float beat = SceneSeconds() * g_flWindSpeed;

	//About a metre of world is a full phase turn, so close neighbours sway out of step; the factors are not a small
	//whole-number ratio, so plants on a grid do not fall back into step
	float phase = dot( origin, float3( 4.7, 0.0, 7.3 ) );

	//Two rates, not one: a single sine reads as a machine.
	float sway = sin( beat + phase ) * 0.6 + sin( beat * 1.7 + phase * 2.3 ) * 0.4;

	//Never back past upright - a third of the way over at the quietest, all the way at the top of a gust.
	float lean = 0.3 + 0.7 * ( 0.5 + 0.5 * sway );

	//The wind blows along the ground, which is x and z.
	float2 blow = normalize( g_vWindDirection.xz + float2( 1e-4, 1e-4 ) );

	float2 push = blow * ( lean * mask * g_flWindStrength );

	return float3( push.x, 0.0, push.y );
}
#endif

//The vertex as the interpolants: the material's own vertex body, which the classic
//entry below and the engine's cluster entry both run - see cluster_surface.slang.
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

	//Where this copy stands, from its matrix, so instanced grass gets an origin per blade for phase and tint
	float3 origin = mul( float4( 0.0, 0.0, 0.0, 1.0 ), modelToWorld ).xyz;

	//A small per-copy lean toward yellow or blue, lighter or darker, hashed from where it stands
	float3 variation = float3( 1.0, 1.0, 1.0 );

	if ( g_flVariation > 0.0 )
	{
		float3 seed = frac( sin( float3( dot( origin.xz, float2( 12.9898, 78.233 ) ), dot( origin.xz, float2( 39.346, 11.135 ) ),
			dot( origin.xz, float2( 73.156, 52.235 ) ) ) ) * 43758.5453 ) - 0.5;

		float warm = seed.x * 0.5 * g_flVariation;
		float bright = 1.0 + ( seed.y * 0.45 * g_flVariation );

		variation = float3( 1.0 + warm, 1.0 + ( seed.z * 0.12 * g_flVariation ), 1.0 - warm ) * bright;
	}

#if F_WIND

	//The bend is added in the world rather than before the matrix, so a plant leans downwind whichever
	//way it was placed - a mesh rotated to face the path would otherwise lean along the path.
	world += WindOffset( position, origin, i.texCoord0.xy );
#endif

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.uv = i.texCoord0.xy;
	o.world = world;
	o.normal = mul( float4( normal, 0.0 ), modelToWorld ).xyz;
	o.tangent = mul( float4( tangent, 0.0 ), modelToWorld ).xyz;
	o.bitangentSign = i.tangent.w < 0.0 ? -1.0 : 1.0;
	o.variation = variation;

	float2 chart = any( i.texCoord1.xy ) ? i.texCoord1.xy : i.texCoord0.xy;
	o.lightmap = ( saturate( chart ) * g_object.xy ) + g_object.zw;

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

//The pixel body, run by MainPs for a raster pixel and by MainClusterPs for a cluster's - see cluster_surface.slang
Out Shade( V2F i, bool front )
{
	float2 uv = i.uv;

	float3 n = normalize( i.normal );

#if F_BACKFACES
	//The underside of a leaf is lit as though it faced out. Without this the half of a plant
	//facing away from the sun is lit by what is behind it and reads as a hole in the plant.
	n = front ? n : -n;
#endif

	float4 texel = SampleUv( g_color, uv );

	float alpha = texel.a;

#if F_ALPHA
	{
		float cutoff = g_flAlphaCutoff;

		if ( cutoff > 0.0 )
		{
			//Small levels keep the leaf's coverage (CoreTextureFile.KeepCoverage), so alpha holds at any distance
			float opacity = texel.a;

			//How fast the cut-out changes across this pixel - see complex.shader. A full-screen pass has no
			//neighbour that is the same surface, so it reads one more tap a footprint along instead of asking one.
			float slope = fwidth( opacity );

			//How far over the line this pixel is, in pixels - which is the cut and the softening of its edge at
			//once. A cutout with a hard edge crawls as the eye moves; this is the cheapest thing that stops it.
			float edge = ( opacity - cutoff ) / max( slope, 0.001 ) + 0.5;

			//Under a temporal history, far leaves are cut against a threshold that turns every pixel and frame (hashed alpha,
			//Wyman & McGuire 2017): the history gathers their coverage, where a fixed cut drops a leaf thinner than a pixel whole
			float turn = g_frame[0].shadow.y;

			if ( turn > 0.5 )
			{
				float far = saturate( ( LevelOf( g_color, uv ) - 2.0 ) * 0.5 );
				float2 at = i.position.xy + ( ( turn - 1.0 ) * 5.588238 );
				float noise = frac( 52.9829189 * frac( dot( at, float2( 0.06711056, 0.00583715 ) ) ) );

				edge = lerp( edge, opacity >= noise ? 1.0 : 0.0, far );
			}

			if ( edge < 0.5 ) discard;

			alpha = saturate( edge );
		}
	}
#endif

#ifdef DEPTH_ONLY
	//The depth pre-pass has to run the cut above - a leaf that never discarded would write a solid card into the depth
	//and hide the plant behind it - and has no use for a single thing after it. See CoreShaderFile.Stage.DepthPixel.
	Out nothing = (Out)0;
	return nothing;
#endif

	float4 albedo = float4( texel.rgb * g_tint.rgb * i.variation, alpha * g_tint.a );

#if F_NORMAL_MAP
	//Far calls skip the normal map (kFarShading); at that distance it averages to the leaf normal
	float3 normal;

	if ( kFarShading != 0 )
	{
		normal = n;
	}
	else
	{
		float3 t = TangentAgainst( n, i.tangent );
		float3 b = cross( n, t ) * i.bitangentSign;

		float3 sampled = SampleUv( g_normal, uv ).rgb * 2.0 - 1.0;

		normal = normalize( sampled.x * t + sampled.y * b + sampled.z * n );
	}
#else
	float3 normal = n;
#endif

#if !F_ROUGHNESS
	float roughness = 1.0;
#else
	//The map times the number, as complex.shader; far draws take the number alone, the map's average at that size
	float roughness = kFarShading != 0
		? clamp( g_flRoughness, 0.04, 1.0 )
		: clamp( SampleUv( g_tRoughness, uv ).r * g_flRoughness, 0.04, 1.0 );
#endif

	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = n;
	surface.albedo = albedo.rgb;
	surface.alpha = albedo.a;
	surface.roughness = roughness;
#if F_METALNESS
	surface.metalness = saturate( SampleUv( g_tMetalness, uv ).r * g_flMetalness );
#else
	surface.metalness = 0.0;
#endif
#if F_AO
	surface.occlusion = saturate( SampleUv( g_tAO, uv ).r );
#else
	surface.occlusion = 1.0;
#endif
	surface.emission = float3( 0.0, 0.0, 0.0 );
	surface.uv = uv;
	surface.id = i.depthId;
	surface.lightmap = i.lightmap;
	surface.screen = i.position.xy;

#if F_TRANSMISSION
	//Light through the leaf in its own colour, from every light behind it - see ShadeSurfaceWith
	surface.transmission = albedo.rgb * g_vTransmissionColour * ( g_flTransmission * SampleUv( g_tThickness, uv ).r );
	surface.transmissionSpread = g_flTransmissionSpread;
#endif

	Shaded lit = ShadeSurface( surface );

	Out o;
	o.colour = float4( lit.colour, albedo.a );

#ifdef TARGET_SCENE
	o.surface = SurfaceWord( normal, lit.roughness, lit.mirror );
#endif

	//Tonemap when drawing straight to screen; alpha must be opaque or the Linux window shows through
#ifdef TARGET_SCREEN
	//The surface's own alpha where this draw blends - see ScreenAlpha in frame.slang
#if F_TRANSLUCENT
	bool blends = true;
#else
	bool blends = g_tint.a < 1.0;
#endif

	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( blends, albedo.a ) );
#endif

	return o;
}

[shader("fragment")]
Out MainPs( V2F i
#if F_BACKFACES
	, bool front : SV_IsFrontFace
#endif
	)
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

#if F_BACKFACES
	return Shade( i, front );
#else
	return Shade( i, true );
#endif
}
