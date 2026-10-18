//The terrain's painted layers - the numbers picture, a layer's pictures, its triplanar read, its height and how far it lifts the
//ground - and the ring of relief round the eye. Shared by the ground's own stages and the pass that builds the relief, see terrain_relief.shader.
//Included after frame.slang; reads the numbers picture by g_flParams, or g_tParams in the portable frame.

#ifndef ENGINE_TERRAIN_LAYERS_INCLUDED
#define ENGINE_TERRAIN_LAYERS_INCLUDED

#include "half.slang"

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

//The ring of relief round the eye - see Ring and Terrain.Relief.
static const int RowRing = 7;

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



//How much the surface faces along each axis of the terrain, sharpened so a slope is two projections
//for a narrow band and one either side of it, and what is left under a twentieth is not read at all.
float3 TriplanarSides( float3 facing )
{
	float3 sides = abs( facing );
	sides = max( sides - 0.35, float3( 0.0, 0.0, 0.0 ) );
	sides *= sides;
	sides /= max( sides.x + sides.y + sides.z, 1e-5 );
	sides = float3( sides.x < 0.05 ? 0.0 : sides.x, sides.y < 0.05 ? 0.0 : sides.y, sides.z < 0.05 ? 0.0 : sides.z );
	sides /= max( sides.x + sides.y + sides.z, 1e-5 );

	return sides;
}

//How far a layer lifts the ground where it is, in metres: its height map, nought to one, times its displacement. Nought where it has
//no height map or does not displace. The map read at a footprint of <paramref name="texel"/> metres, so a coarse lift reads a coarse level.
float LayerLift( uint index, float2 local, float texel )
{
	Layer layer = LayerAt( index );
	float displacement = HalfLow( Bits( int( min( index, 63u ) ), RowLayerPacks ) );

	if ( displacement <= 0.0 || layer.height == 0u ) return 0.0;

#ifdef PORTABLE
	return 0.0;
#else
	float2 uv = ( local / LayerTile ) * layer.uvScale;
	float2 dx = float2( ( texel / LayerTile ) * layer.uvScale, 0.0 );

	float4 deep = ( layer.flags & LayerNoTile ) != 0u
		? SampleUntiledOr( layer.height, UntileAt( uv, dx, dx.yx ), dx, dx.yx, float4( 0.5, 0.5, 0.5, 1.0 ) )
		: SampleOr( layer.height, uv, dx, dx.yx, float4( 0.5, 0.5, 0.5, 1.0 ) );

	return deep.r * displacement;
#endif
}

//And over the projections the surface faces.
float LayerLiftAround( uint index, float3 local, float3 sides, float texel )
{
	float lift = 0.0;

	if ( sides.y > 0.0 ) lift += LayerLift( index, local.xz, texel ) * sides.y;
	if ( sides.x > 0.0 ) lift += LayerLift( index, local.zy, texel ) * sides.x;
	if ( sides.z > 0.0 ) lift += LayerLift( index, local.xy, texel ) * sides.z;

	return lift;
}

//The ring of relief round the eye, as Terrain.Relief writes it: where the eye was when it was built, in the terrain's metres, how far
//it reaches and over how much of that the lift fades to nought, how far the ground's own corners sink under it, and where the terrain
//stands - a ring's corners are in the terrain's metres and drawn with no matrix of their own. Reaching nought where there is none.
struct Ring
{
	float3 eye;
	float reach;
	float fade;
	float sink;
	float3 scale;
	float3 at;
};

Ring RingOf()
{
	Ring o;

	o.eye = float3( asfloat( Bits( 0, RowRing ) ), asfloat( Bits( 1, RowRing ) ), asfloat( Bits( 2, RowRing ) ) );
	o.reach = asfloat( Bits( 3, RowRing ) );
	o.fade = max( asfloat( Bits( 4, RowRing ) ), 1e-3 );
	o.sink = asfloat( Bits( 5, RowRing ) );
	o.scale = float3( asfloat( Bits( 6, RowRing ) ), asfloat( Bits( 7, RowRing ) ), asfloat( Bits( 8, RowRing ) ) );
	o.at = float3( asfloat( Bits( 9, RowRing ) ), asfloat( Bits( 10, RowRing ) ), asfloat( Bits( 11, RowRing ) ) );

	return o;
}

//How much of the lift a place gets: all of it until the last of the reach, then less to nought at its edge.
float RingShare( Ring ring, float3 local )
{
	return saturate( ( ring.reach - distance( local, ring.eye ) ) / ring.fade );
}

#endif
