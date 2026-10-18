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

//Terrain params picture: 32 bits per texel as RGBA8 bytes; layout must match Terrain.Params

static const float ParamsWide = 64.0;
static const float ParamsHigh = 8.0;

static const int RowTerrain = 0;
static const int RowLayerMaps = 1;
static const int RowLayerPacks = 2;
static const int RowLayerScale = 3;
static const int RowLayerBytes = 4;
static const int RowLayerStrengths = 5;
static const int RowLayerMaps2 = 6;

uint Bits( int x, int y )
{
	float2 uv = float2( ( float( x ) + 0.5 ) / ParamsWide, ( float( y ) + 0.5 ) / ParamsHigh );

#ifdef PORTABLE
	float4 bytes = g_tParams.SampleLevel( uv, 0.0 );
#else
	float4 bytes = g_textures[uint( g_flParams + 0.5 )].SampleLevel( uv, 0.0 );
#endif

	return uint( bytes.r * 255.0 + 0.5 )
		| ( uint( bytes.g * 255.0 + 0.5 ) << 8 )
		| ( uint( bytes.b * 255.0 + 0.5 ) << 16 )
		| ( uint( bytes.a * 255.0 + 0.5 ) << 24 );
}

uint Low( uint bits ) { return bits & 0xFFFFu; }
uint High( uint bits ) { return bits >> 16; }
float HalfLow( uint bits ) { return HalfToFloat( bits & 0xFFFFu ); }
float HalfHigh( uint bits ) { return HalfToFloat( bits >> 16 ); }

//The terrain's own numbers, in the order Terrain.Params writes them.
struct Ground
{
	uint layers;
	bool blendByHeight;
	float sharpness;
};

//Two fetches, of a picture eight texels tall that never leaves the cache.
Ground GroundPaint()
{
	Ground g = (Ground)0;

	uint flags = Bits( 5, RowTerrain );
	g.blendByHeight = ( flags & 1u ) != 0u;
	g.sharpness = float( High( flags ) ) / 65535.0;
	g.layers = Low( Bits( 6, RowTerrain ) );

	return g;
}

//Metres to one turn of a layer's texture at a scale of one: thirty-two inches, which is what these materials were authored against.
static const float LayerTile = 0.8128;

//And a square of the unpainted grid: sixty-four inches, on the same terms.
static const float GridSquare = 1.6256;

//A painted layer: which pictures it is made of, how often it repeats, and what it does with the light. Nought is not a picture
//- the device's table hands out slots from one - so a layer that named no map reads the default beside it instead. See SampleOr.
struct Layer
{
	uint colour;      //base colour in rgb
	uint normal;      //tangent normal in rgb
	uint height;      //how far into the surface a texel sits, in r
	uint roughnessMap; //how rough, in r - multiplied by roughness below
	uint occlusionMap; //how much light reaches, in r

	float uvScale;    //already inverted
	uint flags;

	float roughness;
	float metalness;
	float normalStrength;
	float heightStrength;
};

static const uint LayerNoTile = 1u;

//Six fetches, for everything the pixel stage wants of a layer.
Layer LayerAt( uint index )
{
	int at = int( min( index, 63u ) );

	uint maps = Bits( at, RowLayerMaps );
	uint packs = Bits( at, RowLayerPacks );
	uint bytes = Bits( at, RowLayerBytes );
	uint strengths = Bits( at, RowLayerStrengths );
	uint maps2 = Bits( at, RowLayerMaps2 );

	Layer o;
	o.colour = Low( maps );
	o.normal = High( maps );
	o.height = High( packs );
	o.uvScale = asfloat( Bits( at, RowLayerScale ) );
	o.roughnessMap = Low( maps2 );
	o.occlusionMap = High( maps2 );
	o.roughness = float( bytes & 0xFFu ) / 255.0 * 2.0;   //written halved: nought to two
	o.metalness = float( ( bytes >> 8 ) & 0xFFu ) / 255.0;
	o.flags = ( bytes >> 16 ) & 0xFFu;
	o.normalStrength = HalfLow( strengths );
	o.heightStrength = HalfHigh( strengths );

	return o;
}

//What one layer is, where the ground is being drawn.
struct Splatted
{
	float3 albedo;
	float3 normal;
	float roughness;
	float occlusion;
	float metalness;
};

//How many layers a vertex carries weights for - Terrain.MaxLayers, and the two change together.
static const uint MaxLayers = 8u;

#ifdef PORTABLE
//The portable frame has no table of pictures, so a layer is its colour alone and only the first three layers have one: bound as the material's
//own three pictures, white where the layer named none - see Terrain.NamePortablePictures. Their normals and heights are the flat stand-ins.
static const uint PortableLayers = 3u;

float4 LayerColour( uint index, float2 uv, float2 dx, float2 dy )
{
	if ( index == 0u ) return g_color.SampleGrad( uv, dx, dy );
	if ( index == 1u ) return g_normal.SampleGrad( uv, dx, dy );
	return g_emission.SampleGrad( uv, dx, dy );
}
#else
//One of a layer's pictures, or what to read where it has none.
float4 SampleOr( uint map, float2 uv, float2 dx, float2 dy, float4 instead )
{
	if ( map == 0u ) return instead;

	return g_textures[NonUniformResourceIndex( map )].SampleGrad( uv, dx, dy );
}
#endif

//NoTiling layers: hex-tiling style, three randomly offset reads blended to hide the repeat

//Which triangle of the grid a place is in, and how much of each corner it gets.
void TriangleGrid( float2 uv, out float3 weights, out int2 v1, out int2 v2, out int2 v3 )
{
	//Skewed so the grid is one of equilateral triangles about a metre of picture across, which is a repeat no eye picks out.
	float2 skewed = float2( uv.x - ( 0.57735027 * uv.y ), 1.15470054 * uv.y ) * 3.4641016;

	int2 base = int2( floor( skewed ) );
	float2 temp = frac( skewed );
	float3 t = float3( temp, 1.0 - temp.x - temp.y );

	if ( t.z > 0.0 )
	{
		weights = float3( t.z, t.y, t.x );
		v1 = base;
		v2 = base + int2( 0, 1 );
		v3 = base + int2( 1, 0 );
	}
	else
	{
		weights = float3( -t.z, 1.0 - t.y, 1.0 - t.x );
		v1 = base + int2( 1, 1 );
		v2 = base + int2( 1, 0 );
		v3 = base + int2( 0, 1 );
	}
}

//A shift for one corner of the grid, the same every time that corner is asked.
float2 CornerShift( int2 corner )
{
	float2 p = float2( corner );
	float2 r = float2( dot( p, float2( 127.1, 311.7 ) ), dot( p, float2( 269.5, 183.3 ) ) );

	return frac( sin( r ) * 43758.5453 );
}

//Where to read the three corners' worth of a layer's pictures, and how much of each to keep. A weight of nought is a corner that is not read at all.
struct Untiled
{
	float2 uv1, uv2, uv3;
	float3 weights;
};

//How much of a whole repeat of the picture one pixel may cover before the untiling is dropped. Past this the chain has averaged
//the picture down to a few texels, every one of the three samples reads the same thing, and their blend is any one of them.
static const float UntileFootprint = 0.5;

//Below this a corner's share would not move a byte of the answer, and reading it is a fetch of three pictures for
//nothing. The sharpening below leaves most of any triangle standing on one corner, so this is not a rare case.
static const float UntileLeast = 1.0 / 255.0;

Untiled UntileAt( float2 uv, float2 dx, float2 dy )
{
	Untiled o;

	//Far enough away, one sample, unshifted - which is what the tiled path reads, so there is nothing to see where it changes over.
	if ( max( dot( dx, dx ), dot( dy, dy ) ) > UntileFootprint * UntileFootprint )
	{
		o.uv1 = uv;
		o.uv2 = uv;
		o.uv3 = uv;
		o.weights = float3( 1.0, 0.0, 0.0 );

		return o;
	}

	int2 v1, v2, v3;
	TriangleGrid( uv, o.weights, v1, v2, v3 );

	o.uv1 = uv + CornerShift( v1 );
	o.uv2 = uv + CornerShift( v2 );
	o.uv3 = uv + CornerShift( v3 );

	//Sharpened: a flat blend of three pictures is a washed-out one, and most of any triangle is close to one corner or another.
	float3 w = o.weights * o.weights * o.weights;
	w /= max( dot( w, float3( 1.0, 1.0, 1.0 ) ), 1e-5 );

	//And what is left under a byte goes, so the fetch goes with it.
	w *= step( UntileLeast, w );
	o.weights = w / max( dot( w, float3( 1.0, 1.0, 1.0 ) ), 1e-5 );

	return o;
}

#ifdef PORTABLE
float4 LayerColourUntiled( uint index, Untiled where, float2 dx, float2 dy )
{
	float4 o = LayerColour( index, where.uv1, dx, dy ) * where.weights.x;

	if ( where.weights.y > 0.0 ) o += LayerColour( index, where.uv2, dx, dy ) * where.weights.y;
	if ( where.weights.z > 0.0 ) o += LayerColour( index, where.uv3, dx, dy ) * where.weights.z;

	return o;
}
#else
float4 SampleUntiledOr( uint map, Untiled where, float2 dx, float2 dy, float4 instead )
{
	if ( map == 0u ) return instead;

	float4 o = g_textures[NonUniformResourceIndex( map )].SampleGrad( where.uv1, dx, dy ) * where.weights.x;

	if ( where.weights.y > 0.0 ) o += g_textures[NonUniformResourceIndex( map )].SampleGrad( where.uv2, dx, dy ) * where.weights.y;
	if ( where.weights.z > 0.0 ) o += g_textures[NonUniformResourceIndex( map )].SampleGrad( where.uv3, dx, dy ) * where.weights.z;

	return o;
}
#endif

//Everything of a layer but its height, which the blend by height reads apart - see LayerHeight.
Splatted SampleLayer( uint index, float2 local, float2 ddxLocal, float2 ddyLocal )
{
	Layer layer = LayerAt( index );

	float2 uv = ( local / LayerTile ) * layer.uvScale;
	float2 dx = ( ddxLocal / LayerTile ) * layer.uvScale;
	float2 dy = ( ddyLocal / LayerTile ) * layer.uvScale;

	//Colour and normal, read the way the standard surface reads them - a flat normal standing in where this layer named none.
	float4 colour, mapped;
	float rough = 1.0, occ = 1.0;

#ifdef PORTABLE
	colour = ( layer.flags & LayerNoTile ) != 0u
		? LayerColourUntiled( index, UntileAt( uv, dx, dy ), dx, dy )
		: LayerColour( index, uv, dx, dy );
	mapped = float4( 0.5, 0.5, 1.0, 1.0 );
#else
	if ( ( layer.flags & LayerNoTile ) != 0u )
	{
		Untiled where = UntileAt( uv, dx, dy );

		colour = SampleUntiledOr( layer.colour, where, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) );
		mapped = SampleUntiledOr( layer.normal, where, dx, dy, float4( 0.5, 0.5, 1.0, 1.0 ) );
		rough = SampleUntiledOr( layer.roughnessMap, where, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) ).r;
		occ = SampleUntiledOr( layer.occlusionMap, where, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) ).r;
	}
	else
	{
		colour = SampleOr( layer.colour, uv, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) );
		mapped = SampleOr( layer.normal, uv, dx, dy, float4( 0.5, 0.5, 1.0, 1.0 ) );
		rough = SampleOr( layer.roughnessMap, uv, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) ).r;
		occ = SampleOr( layer.occlusionMap, uv, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) ).r;
	}
#endif

	//Sideways is what says which way the surface tilts, so scaling that half and normalising again is what makes a map bite harder or less.
	float3 normal = mapped.rgb * 2.0 - 1.0;
	normal.xy *= layer.normalStrength;

	Splatted o;
	o.albedo = colour.rgb;
	o.normal = normalize( normal );

	//Roughness is the layer's map times its number, as on every other surface; a layer without a map reads white and
	//its number alone. Occlusion is the map, one where there is none. A map not named costs no fetch - see SampleOr.
	o.roughness = clamp( rough * layer.roughness, 0.04, 1.0 );
	o.metalness = saturate( layer.metalness );
	o.occlusion = occ;

	return o;
}

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

	float3 world = mul( float4( i.position, 1.0 ), modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.world = world;
	o.local = i.position;
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

//Triplanar layer sample, reading only the sides the surface faces enough (just top on flat ground)
Splatted SampleLayerAround( uint index, float3 local, float3 facing, float3 dxLocal, float3 dyLocal, float3 sides )
{
	Splatted o = (Splatted)0;

	//The bend a layer's normal map asks for, as a slope along each axis of the terrain: what the
	//three projections have in common, and what lets them be added before anything is normalised.
	float3 bend = float3( 0.0, 0.0, 0.0 );

	if ( sides.y > 0.0 )
	{
		Splatted top = SampleLayer( index, local.xz, dxLocal.xz, dyLocal.xz );

		o.albedo += top.albedo * sides.y;
		o.roughness += top.roughness * sides.y;
		o.metalness += top.metalness * sides.y;
		o.occlusion += top.occlusion * sides.y;
		bend += float3( top.normal.x, 0.0, top.normal.y ) * sides.y;
	}

	if ( sides.x > 0.0 )
	{
		Splatted side = SampleLayer( index, local.zy, dxLocal.zy, dyLocal.zy );

		o.albedo += side.albedo * sides.x;
		o.roughness += side.roughness * sides.x;
		o.metalness += side.metalness * sides.x;
		o.occlusion += side.occlusion * sides.x;
		bend += float3( 0.0, side.normal.y, side.normal.x ) * sides.x;
	}

	if ( sides.z > 0.0 )
	{
		Splatted side = SampleLayer( index, local.xy, dxLocal.xy, dyLocal.xy );

		o.albedo += side.albedo * sides.z;
		o.roughness += side.roughness * sides.z;
		o.metalness += side.metalness * sides.z;
		o.occlusion += side.occlusion * sides.z;
		bend += float3( side.normal.x, side.normal.y, 0.0 ) * sides.z;
	}

	//The surface's own facing with the bend laid against it - the bend only ever leans it, which is all a normal map on a surface this rough is for.
	o.normal = normalize( facing + bend );

	return o;
}

//How high a layer stands where the ground is drawn: its height map alone, read the way SampleLayer reads it.
float LayerHeight( uint index, float2 local, float2 ddxLocal, float2 ddyLocal )
{
	Layer layer = LayerAt( index );

#ifdef PORTABLE
	return 0.5 * layer.heightStrength;
#else
	float2 uv = ( local / LayerTile ) * layer.uvScale;
	float2 dx = ( ddxLocal / LayerTile ) * layer.uvScale;
	float2 dy = ( ddyLocal / LayerTile ) * layer.uvScale;

	float4 deep = ( layer.flags & LayerNoTile ) != 0u
		? SampleUntiledOr( layer.height, UntileAt( uv, dx, dy ), dx, dy, float4( 0.5, 0.5, 0.5, 1.0 ) )
		: SampleOr( layer.height, uv, dx, dy, float4( 0.5, 0.5, 0.5, 1.0 ) );

	return deep.r * layer.heightStrength;
#endif
}

//And over the projections the surface faces, as SampleLayerAround weighs them.
float LayerHeightAround( uint index, float3 local, float3 dxLocal, float3 dyLocal, float3 sides )
{
	float height = 0.0;

	if ( sides.y > 0.0 ) height += LayerHeight( index, local.xz, dxLocal.xz, dyLocal.xz ) * sides.y;
	if ( sides.x > 0.0 ) height += LayerHeight( index, local.zy, dxLocal.zy, dyLocal.zy ) * sides.x;
	if ( sides.z > 0.0 ) height += LayerHeight( index, local.xy, dxLocal.xy, dyLocal.xy ) * sides.z;

	return height;
}

//The pixel body, which the classic entry at the end and the engine's cluster entry both run.
Out Shade( V2F i, bool front )
{
	Ground g = GroundPaint();

	float3 facing = normalize( i.normal );
	if ( !front ) facing = -facing;

	//How much the surface faces along each axis of the terrain, sharpened so a slope is two projections
	//for a narrow band and one either side of it, and what is left under a twentieth is not read at all.
	float3 sides = abs( facing );
	sides = max( sides - 0.35, float3( 0.0, 0.0, 0.0 ) );
	sides *= sides;
	sides /= max( sides.x + sides.y + sides.z, 1e-5 );
	sides = float3( sides.x < 0.05 ? 0.0 : sides.x, sides.y < 0.05 ? 0.0 : sides.y, sides.z < 0.05 ? 0.0 : sides.z );
	sides /= max( sides.x + sides.y + sides.z, 1e-5 );

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
