//The relief ring: ground triangles near the eye cut as Terrain.Relief asked, corners lifted by their layers' heights
//fading at the edge; written as mesh vertices in the terrain's metres, marked as the ring's - see terrain.shader

FEATURES
{
}

PARAMETERS
{
	//Source triangles, the terrain's numbers picture, and the vertices and triangles they are cut into.
	float4 g_vRelief < Attribute( "Relief" ); >;
}

#include "postprocess.slang"

//The portable frame has no compute: its ground is drawn as it is, with no relief.
#ifdef PORTABLE
[numthreads( 64, 1, 1 )]
void MainCs( uint3 id : SV_DispatchThreadID )
{
}
#else
#define g_flParams ( g_vRelief.y )

#include "engine/terrain_layers.slang"
#include "vertex_frame.slang"

//One corner of a triangle the ring is built from - see TerrainRelief.Corner.
struct Corner
{
	float4 place;
	float4 normal;
	float4 paint;
	float4 paintMore;
	float4 palette;
};

//The triangles three corners at a time, each one's cuts a side and where its vertices and indices start - see TerrainRelief.
[[vk::binding( 12, 2 )]] StructuredBuffer<Corner> g_corners;
[[vk::binding( 13, 2 )]] StructuredBuffer<uint4> g_triangles;

//What is drawn: vertices as the device lays a mesh's out - see PackedVertex - and three indices a triangle.
[[vk::binding( 14, 2 )]] RWByteAddressBuffer g_vertices;
[[vk::binding( 15, 2 )]] RWByteAddressBuffer g_indices;

static const uint VertexBytes = 52u;

//A vertex's place in the triangle's own run, by its steps toward the second corner and the third.
uint Step( uint i, uint j, uint n )
{
	return ( i * ( n + 1u ) ) - ( ( i * ( i - 1u ) ) / 2u ) + j;
}

uint Byte( float value )
{
	return uint( saturate( value ) * 255.0 + 0.5 );
}

//The last triangle whose run starts at or before at, by its vertices' start (y) or its indices' start (z).
uint TriangleAt( uint at, bool byIndex )
{
	uint low = 0u;
	uint high = uint( g_vRelief.x ) - 1u;

	while ( low < high )
	{
		uint mid = ( low + high + 1u ) >> 1u;
		uint4 cut = g_triangles[mid];
		uint start = byIndex ? cut.z / 3u : cut.y;

		if ( start <= at ) low = mid;
		else high = mid - 1u;
	}

	return low;
}

//A lifted vertex of its triangle's cut, at i steps toward the second corner and j toward the third.
void WriteVertex( uint t, uint4 cut, uint n, uint i, uint j )
{
	Corner a = g_corners[( t * 3u ) + 0u];
	Corner b = g_corners[( t * 3u ) + 1u];
	Corner c = g_corners[( t * 3u ) + 2u];

	//A triangle is one chunk's, and wears its palette.
	float2 palette = a.palette.xy;

	//How long a cut's side is: the coarsest level of a height map worth reading for it.
	float texel = max( max( distance( a.place.xyz, b.place.xyz ), distance( a.place.xyz, c.place.xyz ) ), 1e-3 ) / float( n );

	float wb = float( i ) / float( n );
	float wc = float( j ) / float( n );
	float wa = 1.0 - wb - wc;

	float3 place = ( a.place.xyz * wa ) + ( b.place.xyz * wb ) + ( c.place.xyz * wc );
	float3 normal = normalize( ( a.normal.xyz * wa ) + ( b.normal.xyz * wb ) + ( c.normal.xyz * wc ) );
	float4 paint = ( a.paint * wa ) + ( b.paint * wb ) + ( c.paint * wc );
	float4 paintMore = ( a.paintMore * wa ) + ( b.paintMore * wb ) + ( c.paintMore * wc );

	//The layers' lifts weighed by their paint, as the pixel weighs everything else of them - the mask's among it.
	Ring ring = RingOf();
	Ground g = GroundPaint();

	//The level read by the corner's own distance, not its triangle's cuts: a corner two triangles share is lifted alike,
	//and a ring laid again a metre on lifts the same ground the same, where by cut counts it jumped a level at every lay
	if ( ring.cutPerMetre > 0.0 ) texel = max( distance( place, ring.eye ) * ring.cutPerMetre, 0.05 );

	float lift = 0.0;
	float share = RingShare( ring, place );

	if ( share > 0.0 && g.layers > 0u )
	{
		Worn p = PaintAt( paint, paintMore, palette, MaskPlaceOfParams(), place, normal.y, g.layers );
		float3 sides = TriplanarSides( normal );

		for ( uint l = 0u; l < PlaceLayers; l++ )
			if ( p.weight[l] > 0.0 ) lift += LayerLiftAround( p.layer[l], place, sides, texel ) * p.weight[l];
	}

	//A rise over the ground as well, fading with the lift: the pre-pass draws that ground where it lies, and a ring
	//corner on it would lose the depth to it - then the ground shows through, under its own shadow
	place += normal * ( ( lift + ring.rise ) * share );

	uint at = ( cut.y + Step( i, j, n ) ) * VertexBytes;

	g_vertices.Store3( at, asuint( place ) );
	g_vertices.Store( at + 12u, Byte( paint.x ) | ( Byte( paint.y ) << 8 ) | ( Byte( paintMore.z ) << 16 ) | ( Byte( paintMore.w ) << 24 ) );
	g_vertices.Store( at + 16u, VertexFrameEncode( normal, float4( 1.0, 0.0, 0.0, -1.0 ) ) );
	g_vertices.Store4( at + 20u, asuint( float4( paint.z, paint.w, palette ) ) );
	g_vertices.Store4( at + 36u, asuint( float4( paintMore.x, paintMore.y, 1.0, 0.0 ) ) );
}

//A thread an output vertex (the first z threads), then a thread an output triangle (the next w): a triangle cut sixteen to a side
//is 153 vertices, which one thread a source triangle wrote alone while the rest of its wave sat idle
[numthreads( 64, 1, 1 )]
void MainCs( uint3 id : SV_DispatchThreadID )
{
	uint vertices = uint( g_vRelief.z );

	if ( id.x < vertices )
	{
		uint t = TriangleAt( id.x, false );
		uint4 cut = g_triangles[t];
		uint n = max( cut.x, 1u );

		//Row i holds n + 1 - i vertices.
		uint k = id.x - cut.y;
		uint i = 0u;

		while ( k > n - i ) { k -= n + 1u - i; i++; }

		WriteVertex( t, cut, n, i, k );
		return;
	}

	uint q = id.x - vertices;
	if ( q >= uint( g_vRelief.w ) ) return;

	uint t = TriangleAt( q, true );
	uint4 cut = g_triangles[t];
	uint n = max( cut.x, 1u );

	//Row i holds 2 (n - i) - 1 triangles, a step's first then, where the row has room, its second.
	uint local = q - ( cut.z / 3u );
	uint r = local;
	uint i = 0u;

	while ( r >= ( 2u * ( n - i ) ) - 1u ) { r -= ( 2u * ( n - i ) ) - 1u; i++; }

	uint j = r >> 1u;

	//Turned the way the triangle they were cut from is.
	uint3 corners = ( r & 1u ) == 0u
		? uint3( Step( i, j, n ), Step( i + 1u, j, n ), Step( i, j + 1u, n ) )
		: uint3( Step( i + 1u, j, n ), Step( i + 1u, j + 1u, n ), Step( i, j + 1u, n ) );

	g_indices.Store3( ( cut.z + ( local * 3u ) ) * 4u, cut.y + corners );
}
#endif
