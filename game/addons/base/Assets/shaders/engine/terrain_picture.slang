//The ground picture: each terrain chunk's top seen from above, a point every 64th of its side - its three heaviest
//layers, its height, its normal. What the ground is painted and lit by, so a far model or a coarse level of a chunk,
//whose corners moved, paints and lights the same places the whole one does. Any terrain shader can read it: where it
//is comes per draw as TerrainPicture, and in the terrain's numbers - see Terrain.Picture and TerrainChunkBaker.Picture

#ifndef TERRAIN_PICTURE_SLANG
#define TERRAIN_PICTURE_SLANG

#include "half.slang"

//Steps along a chunk's picture, its points a side, a point's texels: paint, then height and normal
static const int PictureCells = 64;
static const int PictureSide = 65;
static const int PicturePointTexels = 2;

//Under this a point's height says there is no ground there
static const float PictureNoGround = -64000.0;

//How far under the ground's top the picture still paints; below is a cave's floor, which its vertices paint
static const float PictureUnder = 1.0;

//Where it is: its atlas (nought for none), chunks a row of it, chunks across and deep, the first chunk's corner, their side
struct GroundPicture
{
	uint atlas;
	uint perRow;
	int2 chunks;
	float2 origin;
	float span;
};

//Out of the four whole numbers it is told as - see Terrain.PictureTold: the atlas and chunks a row, the chunks across
//and deep, their side in metres, and the first chunk's corner in chunks
GroundPicture GroundPictureOf( float4 told )
{
	uint place = uint( told.x + 0.5 );
	uint chunks = uint( told.y + 0.5 );
	uint corner = uint( told.w + 0.5 );

	GroundPicture o;
	o.atlas = place >> 8;
	o.perRow = max( place & 255u, 1u );
	o.chunks = int2( int( chunks >> 12 ), int( chunks & 4095u ) );
	o.span = told.z;
	o.origin = float2( float( int( corner >> 12 ) - 2048 ), float( int( corner & 4095u ) - 2048 ) ) * o.span;

	return o;
}

#ifndef PORTABLE
//Four bytes of a picture as the 32 bits they were written from, low byte first.
uint PictureBits( uint picture, int2 texel )
{
	float4 bytes = g_textures[NonUniformResourceIndex( picture )].Load( int3( texel, 0 ) );

	return uint( bytes.r * 255.0 + 0.5 )
		| ( uint( bytes.g * 255.0 + 0.5 ) << 8 )
		| ( uint( bytes.b * 255.0 + 0.5 ) << 16 )
		| ( uint( bytes.a * 255.0 + 0.5 ) << 24 );
}

//A direction out of two bytes folded onto the square y faces - see TerrainChunkBaker.Octahedral
float3 PictureNormal( uint bits )
{
	float2 e = ( float2( float( bits & 255u ), float( ( bits >> 8 ) & 255u ) ) / 255.0 ) * 2.0 - 1.0;
	float3 n = float3( e.x, 1.0 - abs( e.x ) - abs( e.y ), e.y );

	if ( n.y < 0.0 ) n.xz = ( 1.0 - abs( n.zx ) ) * float2( n.x >= 0.0 ? 1.0 : -1.0, n.z >= 0.0 ? 1.0 : -1.0 );

	return normalize( n );
}

//A point's paint: three layers and their weights, coming to one - see Terrain.PackPoint
void PicturePaint( uint packed, out uint3 layers, out float3 weights )
{
	layers = uint3( packed & 31u, ( packed >> 5 ) & 31u, ( packed >> 10 ) & 31u );

	float first = float( ( packed >> 15 ) & 0xFFu ) / 255.0;
	float second = float( ( packed >> 23 ) & 0xFFu ) / 255.0;

	weights = float3( first, second, max( 1.0 - first - second, 0.0 ) );
}

//The four points round a place in the terrain's metres, only near the ground's top and facing up: their paint words and
//how much each covers, and their normal bent into normal - in the terrain's space - where the ground faces up.
//Returns how much of the place their paint covers; a shader then lays each point's PicturePaint over its own by share
float GroundPictureRead( GroundPicture where, float3 local, float facingUp, inout float3 normal, out uint4 packed, out float4 shares )
{
	packed = uint4( 0u, 0u, 0u, 0u );
	shares = float4( 0.0, 0.0, 0.0, 0.0 );

	if ( where.atlas == 0u || where.span <= 0.0 ) return 0.0;

	float2 inChunks = ( local.xz - where.origin ) / where.span;
	int2 chunk = int2( floor( inChunks ) );

	if ( chunk.x < 0 || chunk.y < 0 || chunk.x >= where.chunks.x || chunk.y >= where.chunks.y ) return 0.0;

	uint slot = uint( ( chunk.y * where.chunks.x ) + chunk.x );
	int2 corner = int2( int( slot % where.perRow ) * PictureSide * PicturePointTexels, int( slot / where.perRow ) * PictureSide );

	float2 at = ( inChunks - float2( chunk ) ) * float( PictureCells );
	int2 base = clamp( int2( floor( at ) ), int2( 0, 0 ), int2( PictureCells - 1, PictureCells - 1 ) );
	float2 f = saturate( at - float2( base ) );

	//Under the top - a cave's floor - the vertices paint and light.
	int2 nearest = base + int2( f + 0.5 );
	float top = HalfToFloat( PictureBits( where.atlas, corner + int2( ( nearest.x * PicturePointTexels ) + 1, nearest.y ) ) & 0xFFFFu );

	if ( top < PictureNoGround || local.y < top - PictureUnder ) return 0.0;

	//Under a cave's roof the ground faces down, and there too; a wall keeps its own normal, which a top seen from above does not hold
	float upright = saturate( ( facingUp * 4.0 ) + 1.0 );
	float level = saturate( ( facingUp - 0.3 ) * 4.0 );

	float3 bent = float3( 0.0, 0.0, 0.0 );
	float bentShare = 0.0;
	float cover = 0.0;

	[unroll]
	for ( uint k = 0u; k < 4u; k++ )
	{
		int2 step = int2( k & 1u, k >> 1u );
		float share = ( step.x == 1 ? f.x : 1.0 - f.x ) * ( step.y == 1 ? f.y : 1.0 - f.y ) * upright;

		int2 point = base + step;
		uint rest = PictureBits( where.atlas, corner + int2( ( point.x * PicturePointTexels ) + 1, point.y ) );

		if ( share <= 0.0 || HalfToFloat( rest & 0xFFFFu ) < PictureNoGround ) continue;

		bent += PictureNormal( rest >> 16 ) * ( share * level );
		bentShare += share * level;

		packed[k] = PictureBits( where.atlas, corner + int2( point.x * PicturePointTexels, point.y ) );
		shares[k] = ( packed[k] >> 31 ) != 0u ? share : 0.0;
		cover += shares[k];
	}

	if ( bentShare > 0.0 ) normal = normalize( ( normal * ( 1.0 - bentShare ) ) + bent );

	return cover;
}
#else
//The portable frame reads no picture: the vertices paint and light everything
void PicturePaint( uint packed, out uint3 layers, out float3 weights )
{
	layers = uint3( 0u, 0u, 0u );
	weights = float3( 0.0, 0.0, 0.0 );
}

float GroundPictureRead( GroundPicture where, float3 local, float facingUp, inout float3 normal, out uint4 packed, out float4 shares )
{
	packed = uint4( 0u, 0u, 0u, 0u );
	shares = float4( 0.0, 0.0, 0.0, 0.0 );

	return 0.0;
}
#endif

#endif
