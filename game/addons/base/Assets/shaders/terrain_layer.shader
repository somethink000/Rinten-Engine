//Terrain layer material: its PARAMETERS define tiling/blend; draws only previews, not the ground

FEATURES
{
	//The same two the standard surface has, and for the same reason: a layer with
	//no normal map does not want the sample or the tangent frame that goes with it.
	Feature( F_NORMAL_MAP, 0..1, "Maps" ) < Map( Normal ); >;
	//Roughness feature on by default; off drops the map and its sample entirely
	Feature( F_ROUGHNESS, 0..1, "Maps" ) < Default( 1 ); >;

}

PARAMETERS
{
	//Standard material maps, one texture each; Default applies when the material names none
	Texture2D g_tRoughness < Default( 0.5 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;
	Texture2D g_tOcclusion < Default( 1.0 ); UiGroup( "Maps" ); >;

	float g_flRoughness < Default( 1.0 ); Range( 0, 2 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;
	float g_flMetalness < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Maps" ); >;
	float g_flEmissionStrength < Default( 0.0 ); Range( 0, 8 ); UiGroup( "Maps" ); >;
	float g_flAlphaCutoff < Default( 0.5 ); Range( 0, 1 ); UiGroup( "Rendering" ); >;

	//How large one turn of this layer's pictures is on the ground. One is the size these materials
	//were authored against - see LayerTile in terrain.shader, which is the metres that comes to.
	float g_flUvScale < Default( 1.0 ); Range( 0.01, 64.0 ); UiGroup( "Terrain" ); >;

	//How hard the normal map bites. Divided into the sampled slope rather than multiplied by it, which is what the terrain has always done.
	float g_flNormalStrength < Default( 1.0 ); Range( 0.1, 10.0 ); UiGroup( "Terrain" ); >;

	//What this layer's height counts for when the ground decides which of two layers takes a pixel. Only read where the terrain blends by height at all.
	float g_flHeightBlend < Default( 1.0 ); Range( 0.1, 10.0 ); UiGroup( "Terrain" ); >;

	//How far the ground is pushed out along its normal where this layer is painted. Nothing without a height map to push it by.
	float g_flDisplacement < Default( 0.0 ); Range( 0.0, 10.0 ); UiGroup( "Terrain" ); >;

	//Whether the tiling is broken up, for a layer whose repeat is obvious from the air - sand, gravel, anything without features to hide the seam.
	bool g_bNoTiling < Default( 0 ); UiGroup( "Terrain" ); >;

	//Height per texel for height blending and displacement; 0.5 is flat
	Texture2D g_tHeight < Default( 0.5 ); UiGroup( "Maps" ); >;
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

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

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

	//Tiled at the layer's own scale, so what the preview shows is the repeat somebody is about
	//to paint the ground with rather than one turn of the picture stretched over a sphere.
	o.uv = i.texCoord0.xy / max( g_flUvScale, 1e-4 );

	DEPTH_ID_SET( o, i.texCoord0.xy );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

//The pixel body, which the classic entry at the end and the engine's cluster entry both run.
Out Shade( V2F i, bool front )
{
	float2 uv = i.uv;
#ifdef DEPTH_ONLY
	//Depth pre-pass writes no colour; the guard keeps the lit stage out of depth builds
	Out nothing = (Out)0;
	return nothing;
#endif


	float3 n = normalize( i.normal );

	float4 texel = g_color.Sample( uv );

#if F_NORMAL_MAP
	float3 t = TangentAgainst( n, i.tangent );
	float3 b = cross( n, t ) * i.bitangentSign;

	float3 sampled = g_normal.Sample( uv ).rgb * 2.0 - 1.0;

	//How hard the slope bites: the sideways half of the normal is what says which way the surface tilts, so
	//scaling it and normalising again steepens or flattens the map without changing what it is a map of.
	sampled.xy *= g_flNormalStrength;
	sampled = normalize( sampled );

	float3 normal = normalize( sampled.x * t + sampled.y * b + sampled.z * n );
#else
	float3 normal = n;
#endif

	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = n;
	surface.albedo = texel.rgb * g_tint.rgb;
	surface.alpha = 1.0;
#if F_ROUGHNESS
	//The picture read at face value, times the number beside it - see complex.shader.
	surface.roughness = clamp( SampleUv( g_tRoughness, uv ).r * g_flRoughness, 0.04, 1.0 );
#else
	//Off: entirely matte, the same as the other surfaces.
	surface.roughness = 1.0;
#endif
	surface.metalness = saturate( g_flMetalness );
	surface.occlusion = saturate( SampleUv( g_tOcclusion, uv ).r );
	surface.emission = float3( 0.0, 0.0, 0.0 );
	surface.uv = uv;
	surface.id = i.depthId;

	//Ground is never in the baked atlas, and neither is a preview of it.
	surface.lightmap = float2( 0.0, 0.0 );
	surface.screen = i.position.xy;

	Shaded lit = ShadeSurface( surface );

	Out o;
	o.colour = float4( lit.colour, 1.0 );

#ifdef TARGET_SCENE
	o.surface = SurfaceWord( normal, lit.roughness, lit.mirror );
#endif

	//Tonemap here when this is the last pass; alpha is forced to 1 as Linux composites the window
#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), 1.0 );
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
