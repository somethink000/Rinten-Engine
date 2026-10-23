//Terrain: a baked mesh painted by 8 per-vertex weights (colour + both UVs) for the chunk's palette and a mask over the top, triplanar layers

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

	//The layer of each of the vertex's eight weights, six bits a slot, four a number (PaletteNumbers); 0 to 7 unset
	//Then the paint mask's table and atlas, nought for none
	float4 g_vPalette < Attribute( "TerrainPalette" ); Default4( 794688.0, 1859908.0, 0.0, 0.0 ); >;

	//And the tile the mask's table starts at, x then z, and how many tiles it is across and deep - see Terrain.Mask.
	float4 g_vMask < Attribute( "TerrainMask" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
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

	//Which layers those are - the chunk's palette, or a ring corner's own.
	nointerpolation float2 palette : TEXCOORD5;
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

//Metres the ring casts from under itself - more than the moving casters' map's depth steps on flat ground.
static const float RingShadowSink = 0.05;

//The vertex body, which the classic entry below and the engine's cluster entry both run -
//see cluster_surface.slang. Nothing is moved: the ground is where its vertices say it is.
V2F Vertex( VIn i )
{
	row_major float4x4 modelToWorld = g_modelToWorld;

	float3 local = i.position;
	float3 world;
	float2 palette = g_vPalette.xy;

#ifdef PORTABLE
	world = mul( float4( local, 1.0 ), modelToWorld ).xyz;
#else
	Ring ring = RingOf();

	//A relief ring corner (marked in uv1.z) is in the terrain's metres with no matrix; the ground's corners under the ring
	//sink a little so the lifted ground stays over them, and nothing sinks past the ring's edge
	if ( i.texCoord1.z > 0.5 )
	{
		world = ( local * ring.scale ) + ring.at;
		palette = i.texCoord0.zw;

		//Into a light's map from a little under itself: what it lifts casts, where it lifts nothing it does not shade itself.
		if ( LightLooks() ) world -= i.normal * RingShadowSink;
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
	o.palette = palette;

	DEPTH_ID_SET( o, float2( 0.0, 0.0 ) );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

//Metres from the eye the ground starts being shaded more cheaply, and over how many it gets there: a side and the
//heaviest layers rather than every side and layer, and no blend by height - what a far slope's tiny mips show anyway.
static const float FarFrom = 120.0;
static const float FarBand = 40.0;

//The sides narrowed to the one the surface faces most as the ground gets far, the rest let go of once they weigh little.
float3 FarSides( float3 sides, float far )
{
	sides = pow( sides, 1.0 + ( 7.0 * far ) );
	sides /= max( sides.x + sides.y + sides.z, 1e-5 );

	float least = 0.05 + ( 0.3 * far );
	sides = float3( sides.x < least ? 0.0 : sides.x, sides.y < least ? 0.0 : sides.y, sides.z < least ? 0.0 : sides.z );

	return sides / max( sides.x + sides.y + sides.z, 1e-5 );
}

//The pixel body, which the classic entry at the end and the engine's cluster entry both run.
Out Shade( V2F i, bool front )
{
	Ground g = GroundPaint();

	float3 facing = normalize( i.normal );
	if ( !front ) facing = -facing;

	float far = saturate( ( distance( i.world, EYE_FRAME( camera ).xyz ) - FarFrom ) / FarBand );

	float3 sides = TriplanarSides( facing );
	if ( far > 0.0 ) sides = FarSides( sides, far );

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
#ifdef PORTABLE
		//The layers past the ones this frame can bind are not there at all, so ground painted only with those reads as unpainted.
		uint most = min( g.layers, PortableLayers );
#else
		uint most = g.layers;
#endif

		MaskPlace mask;
		mask.table = uint( g_vPalette.z + 0.5 );
		mask.atlas = uint( g_vPalette.w + 0.5 );
		mask.origin = int2( round( g_vMask.xy ) );
		mask.extent = int2( round( g_vMask.zw ) );

		Worn p = PaintAt( i.paint, i.paintMore, i.palette, mask, i.local, facing.y, most );

		//Far off, a layer weighing little is not read at all; what is left shares its weight.
		if ( far > 0.0 )
		{
			float least = 0.3 * far * max( max( p.weight.x, p.weight.y ), max( p.weight.z, p.weight.w ) );
			p.weight = float4( p.weight.x < least ? 0.0 : p.weight.x, p.weight.y < least ? 0.0 : p.weight.y,
				p.weight.z < least ? 0.0 : p.weight.z, p.weight.w < least ? 0.0 : p.weight.w );
		}

		//The layer standing higher takes the pixel, so a boundary between stones and sand is sand between the stones rather than a fade.
		//Its height alone first, a number a layer, so no layer's whole read is held while the others are read.
		if ( g.blendByHeight && far < 1.0 )
		{
			float4 heights = float4( 0.0, 0.0, 0.0, 0.0 );
			float highest = 1e-4;

			for ( uint h = 0u; h < PlaceLayers; h++ )
			{
				if ( p.weight[h] <= 0.0 ) continue;

				heights[h] = LayerHeightAround( p.layer[h], i.local, dxLocal, dyLocal, sides );
				highest = max( highest, heights[h] );
			}

			float depth = lerp( 0.3, 0.02, saturate( g.sharpness ) );
			float top = 0.0;

			for ( uint a = 0u; a < PlaceLayers; a++ )
				if ( p.weight[a] > 0.0 ) top = max( top, ( heights[a] / highest ) + p.weight[a] );

			top -= depth;

			float4 painted = p.weight / max( dot( p.weight, float4( 1.0, 1.0, 1.0, 1.0 ) ), 1e-6 );

			for ( uint b = 0u; b < PlaceLayers; b++ )
				if ( p.weight[b] > 0.0 ) p.weight[b] = max( ( heights[b] / highest ) + p.weight[b] - top, 0.0 );

			//Fading back to the paint alone over the far band, so the blend ends where it costs nothing to see it end.
			p.weight = lerp( p.weight / max( dot( p.weight, float4( 1.0, 1.0, 1.0, 1.0 ) ), 1e-6 ), painted, far );
		}

		float total = max( dot( p.weight, float4( 1.0, 1.0, 1.0, 1.0 ) ), 1e-6 );

		albedo = float3( 0.0, 0.0, 0.0 );
		normalOnTerrain = float3( 0.0, 0.0, 0.0 );
		roughness = 0.0;
		occlusion = 0.0;
		metalness = 0.0;

		//Then each layer read and added in turn.
		for ( uint m = 0u; m < PlaceLayers; m++ )
		{
			if ( p.weight[m] <= 0.0 ) continue;

			float w = p.weight[m] / total;
			Splatted worn = SampleLayerAround( p.layer[m], i.local, facing, dxLocal, dyLocal, sides );

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
