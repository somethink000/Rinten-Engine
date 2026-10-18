//The ring of relief round the eye: each triangle of the ground near it cut into smaller ones, so many a side as Terrain.Relief asked,
//and every corner lifted along its normal by the layers it wears - their height maps times their displacement - fading to nought at
//the ring's edge. Written as the vertices a mesh carries, in the terrain's metres, marked as the ring's; see Vertex in terrain.shader.

FEATURES
{
}

PARAMETERS
{
	//How many triangles there are, and which picture holds the terrain's numbers.
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

[numthreads( 64, 1, 1 )]
void MainCs( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= uint( g_vRelief.x ) ) return;

	uint4 cut = g_triangles[id.x];
	uint n = max( cut.x, 1u );

	Corner a = g_corners[( id.x * 3u ) + 0u];
	Corner b = g_corners[( id.x * 3u ) + 1u];
	Corner c = g_corners[( id.x * 3u ) + 2u];

	Ring ring = RingOf();
	Ground g = GroundPaint();
	uint most = min( g.layers, MaxLayers );

	//How long a cut's side is: the coarsest level of a height map worth reading for it.
	float texel = max( max( distance( a.place.xyz, b.place.xyz ), distance( a.place.xyz, c.place.xyz ) ), 1e-3 ) / float( n );

	for ( uint i = 0u; i <= n; i++ )
	{
		for ( uint j = 0u; j <= n - i; j++ )
		{
			float wb = float( i ) / float( n );
			float wc = float( j ) / float( n );
			float wa = 1.0 - wb - wc;

			float3 place = ( a.place.xyz * wa ) + ( b.place.xyz * wb ) + ( c.place.xyz * wc );
			float3 normal = normalize( ( a.normal.xyz * wa ) + ( b.normal.xyz * wb ) + ( c.normal.xyz * wc ) );
			float4 paint = ( a.paint * wa ) + ( b.paint * wb ) + ( c.paint * wc );
			float4 paintMore = ( a.paintMore * wa ) + ( b.paintMore * wb ) + ( c.paintMore * wc );

			//The layers' lifts weighed by their paint, as the paint weighs everything else of them.
			float weights[MaxLayers] = { paint.x, paint.y, paint.z, paint.w, paintMore.x, paintMore.y, paintMore.z, paintMore.w };
			float total = 0.0;
			for ( uint w = 0u; w < MaxLayers; w++ ) total += w < most ? weights[w] : 0.0;

			float lift = 0.0;
			float share = RingShare( ring, place );

			if ( share > 0.0 && total > 1e-3 )
			{
				float3 sides = TriplanarSides( normal );

				for ( uint l = 0u; l < most; l++ )
					if ( weights[l] > 0.01 ) lift += LayerLiftAround( l, place, sides, texel ) * ( weights[l] / total );
			}

			place += normal * ( lift * share );

			uint at = ( cut.y + Step( i, j, n ) ) * VertexBytes;

			g_vertices.Store3( at, asuint( place ) );
			g_vertices.Store( at + 12u, Byte( paint.x ) | ( Byte( paint.y ) << 8 ) | ( Byte( paintMore.z ) << 16 ) | ( Byte( paintMore.w ) << 24 ) );
			g_vertices.Store( at + 16u, VertexFrameEncode( normal, float4( 1.0, 0.0, 0.0, -1.0 ) ) );
			g_vertices.Store4( at + 20u, asuint( float4( paint.z, paint.w, 0.0, 0.0 ) ) );
			g_vertices.Store4( at + 36u, asuint( float4( paintMore.x, paintMore.y, 1.0, 0.0 ) ) );
		}
	}

	//Two triangles a step, the second where the row has room for it, turned the way the triangle they were cut from is.
	uint index = cut.z * 4u;

	for ( uint i = 0u; i < n; i++ )
	{
		for ( uint j = 0u; j < n - i; j++ )
		{
			g_indices.Store3( index, cut.y + uint3( Step( i, j, n ), Step( i + 1u, j, n ), Step( i, j + 1u, n ) ) );
			index += 12u;

			if ( j + 1u < n - i )
			{
				g_indices.Store3( index, cut.y + uint3( Step( i + 1u, j, n ), Step( i + 1u, j + 1u, n ), Step( i, j + 1u, n ) ) );
				index += 12u;
			}
		}
	}
}
#endif
