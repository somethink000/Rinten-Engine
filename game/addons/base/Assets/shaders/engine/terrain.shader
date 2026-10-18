//Terrain: a baked mesh painted by 8 per-vertex weights (colour + both UVs), triplanar layers

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//Which picture holds the terrain's numbers, as its place in the table of pictures. Set by Terrain for the material it makes; nothing to edit.
	float g_flParams < Default( 0.0 ); Range( 0, 65535 ); UiGroup( "Terrain" ); >;

	//The same picture bound on the material, where the frame has no table to find it in by number
	//- the portable one. Set by Terrain there and not otherwise; see NamePortablePictures.
	Texture2D g_tParams < Default( 0.0 ); UiGroup( "Terrain" ); >;
}

#include "depth_id.slang"

struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;

	//Where the pixel is in the world, for the lighting.
	float3 world : TEXCOORD2;

	//Where it is on the terrain, in the terrain's own metres: what the layers are laid out over, so they stay put when the terrain is moved.
	float3 local : TEXCOORD0;

	//Which way the vertex faces, in the terrain's own space.
	float3 normal : TEXCOORD1;

	//How much of each layer is painted here: the first four, and the last four.
	//Gathered from the three places the vertex keeps them - see the head of this file.
	float4 paint : TEXCOORD3;
	float4 paintMore : TEXCOORD4;
};

//The push block every world draw carries is the engine's, not this shader's - see frame.slang.
#define ENGINE_WORLD_PUSH 1

#include "frame.slang"
#include "half.slang"
#include "surface_shading.slang"

#include "engine/terrain_layers.slang"

//The grid a terrain wears before anybody has painted anything on it.
void ProceduralGrid( float2 local, out float3 albedo, out float roughness )
{
	float2 p = local / GridSquare;

	float2 w = fwidth( p ) + 0.001;
	float2 e = 2.0 * ( abs( frac( ( p - 0.5 * w ) * 0.5 ) - 0.5 )
		- abs( frac( ( p + 0.5 * w ) * 0.5 ) - 0.5 ) ) / w;

	float v = 0.5 - 0.5 * e.x * e.y;

	albedo = float3( 0.7, 0.7, 0.7 ) + v * 0.3;
	roughness = 0.8 + ( 1.0 - v ) * 0.2;
}

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

//The vertex body, which the classic entry below and the engine's cluster entry both run -
//see cluster_surface.slang. Nothing is moved: the ground is where its vertices say it is.
V2F Vertex( VIn i )
{
	row_major float4x4 modelToWorld = g_modelToWorld;

	float3 local = i.position;
	float3 world;

#ifdef PORTABLE
	world = mul( float4( local, 1.0 ), modelToWorld ).xyz;
#else
	Ring ring = RingOf();

	//A corner of the ring of relief, marked so in the second coordinate's third place: in the terrain's metres, drawn with no
	//matrix of its own - see terrain_relief.shader. The ground's own corners under the ring sink a little along their normal,
	//so the lifted ground, which only ever rises, is over them; nothing sinks past the ring's edge, where the two are one surface.
	if ( i.texCoord1.z > 0.5 )
	{
		world = ( local * ring.scale ) + ring.at;
	}
	else
	{
		if ( ring.reach > 0.0 )
			local -= i.normal * ( ring.sink * saturate( ( ring.reach + ( ring.fade * 0.5 ) - distance( local, ring.eye ) ) / ( ring.fade * 0.5 ) ) );

		world = mul( float4( local, 1.0 ), modelToWorld ).xyz;
	}
#endif

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.world = world;
	o.local = local;
	o.normal = i.normal;
	o.paint = float4( i.colour.xy, i.texCoord0.xy );
	//Layers 4 and 5 in the second coordinate, 6 and 7 in the colour's other half - see TerrainChunkBaker.
	o.paintMore = float4( i.texCoord1.xy, i.colour.zw );

	DEPTH_ID_SET( o, float2( 0.0, 0.0 ) );

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
	Ground g = GroundPaint();

	float3 facing = normalize( i.normal );
	if ( !front ) facing = -facing;

	float3 sides = TriplanarSides( facing );

	float3 dxLocal = ddx( i.local );
	float3 dyLocal = ddy( i.local );

	float3 albedo = float3( 1.0, 1.0, 1.0 );
	float3 normalOnTerrain = facing;
	float roughness = 1.0;
	float occlusion = 1.0;
	float metalness = 0.0;

	if ( g.layers == 0u )
	{
		ProceduralGrid( i.local.xz, albedo, roughness );
	}
	else
	{
		float weights[MaxLayers] = { i.paint.x, i.paint.y, i.paint.z, i.paint.w, i.paintMore.x, i.paintMore.y, i.paintMore.z, i.paintMore.w };

#ifdef PORTABLE
		//The layers past the ones this frame can bind are not there at all, so ground painted only with those reads as unpainted.
		for ( uint p = PortableLayers; p < MaxLayers; p++ ) weights[p] = 0.0;
#endif

		//A vertex nobody painted wears the first layer.
		float painted = 0.0;
		for ( uint s = 0u; s < MaxLayers; s++ ) painted += weights[s];
		if ( painted < 1e-3 ) { weights[0] = 1.0; painted = 1.0; }

#ifdef PORTABLE
		uint most = min( g.layers, PortableLayers );
#else
		uint most = min( g.layers, MaxLayers );
#endif

		//A layer under a hundredth is a fetch of three pictures for nothing.
		for ( uint l = 0u; l < MaxLayers; l++ )
		{
			weights[l] = l < most ? weights[l] / painted : 0.0;
			if ( weights[l] < 0.01 ) weights[l] = 0.0;
		}

		//The layer standing higher takes the pixel, so a boundary between stones and sand is sand between the stones rather than a fade.
		//Its height alone first, a number a layer, so no layer's whole read is held while the others are read.
		if ( g.blendByHeight )
		{
			float heights[MaxLayers] = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
			float highest = 1e-4;

			for ( uint h = 0u; h < MaxLayers; h++ )
			{
				if ( weights[h] <= 0.0 ) continue;

				heights[h] = LayerHeightAround( h, i.local, dxLocal, dyLocal, sides );
				highest = max( highest, heights[h] );
			}

			float depth = lerp( 0.3, 0.02, saturate( g.sharpness ) );
			float top = 0.0;

			for ( uint a = 0u; a < MaxLayers; a++ )
				if ( weights[a] > 0.0 ) top = max( top, ( heights[a] / highest ) + weights[a] );

			top -= depth;

			for ( uint b = 0u; b < MaxLayers; b++ )
				if ( weights[b] > 0.0 ) weights[b] = max( ( heights[b] / highest ) + weights[b] - top, 0.0 );
		}

		float total = 0.0;
		for ( uint t = 0u; t < MaxLayers; t++ ) total += weights[t];
		total = max( total, 1e-6 );

		albedo = float3( 0.0, 0.0, 0.0 );
		normalOnTerrain = float3( 0.0, 0.0, 0.0 );
		roughness = 0.0;
		occlusion = 0.0;
		metalness = 0.0;

		//Then each layer read and added in turn.
		for ( uint m = 0u; m < MaxLayers; m++ )
		{
			if ( weights[m] <= 0.0 ) continue;

			float w = weights[m] / total;
			Splatted worn = SampleLayerAround( m, i.local, facing, dxLocal, dyLocal, sides );

			albedo += worn.albedo * w;
			normalOnTerrain += worn.normal * w;
			roughness += worn.roughness * w;
			occlusion += worn.occlusion * w;
			metalness += worn.metalness * w;
		}

		normalOnTerrain = normalize( normalOnTerrain );
	}

	//Out of the terrain into the world, which is where everything else is: a chunk stands where the terrain does, so its matrix is the terrain's.
	float3 normal = normalize( mul( float4( normalOnTerrain, 0.0 ), g_modelToWorld ).xyz );

	//And the way the surface itself faces, before any layer's map bent it: what a ray leaving it is offset
	//along. Left unsaid it is whatever was in the register, and the lighting normalises it into a black terrain.
	float3 geometric = normalize( mul( float4( facing, 0.0 ), g_modelToWorld ).xyz );

	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = geometric;
	surface.albedo = albedo * g_tint.rgb;
	surface.alpha = 1.0;
	surface.roughness = clamp( roughness, 0.04, 1.0 );
	surface.metalness = saturate( metalness );
	surface.occlusion = occlusion;
	surface.emission = float3( 0.0, 0.0, 0.0 );
	surface.uv = i.local.xz;
	surface.id = i.depthId;
	surface.lightmap = float2( 0.0, 0.0 );
	surface.screen = i.position.xy;

	Shaded lit = ShadeSurface( surface );

	Out o;
	o.colour = float4( lit.colour, 1.0 );

#ifdef TARGET_SCENE
	o.surface = float4( normal * 0.5 + 0.5, lit.mirror );
#endif

	//And, where this pass is the last one over the picture, the light the surface was shaded in turned into what the screen can show - see
	//FinishToScreen in frame.slang. Opaque, for the reason every surface here gives: what is left in alpha is what the desktop shows through the window.
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
