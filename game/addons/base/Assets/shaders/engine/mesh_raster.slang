//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Cluster depth raster, one indexed draw per list; variants: plain, ALPHA cutout, VIEW debug colours

#include "mesh_tables.slang"
#include "vertex_frame.slang"

#ifdef ALPHA
//For the texture LOD bias alone - see CutOut.
#include "frame_data.slang"
#endif


[[vk::binding(0,2)]] StructuredBuffer<Params> g_params;
[[vk::binding(1,2)]] StructuredBuffer<Instance> g_instances;
[[vk::binding(2,2)]] StructuredBuffer<ClusterInfo> g_clusters;
[[vk::binding(3,2)]] StructuredBuffer<uint> g_clusterVertices;
[[vk::binding(4,2)]] StructuredBuffer<uint> g_clusterTriangles;
#ifdef ALPHA
[[vk::binding(5,2)]] ByteAddressBuffer g_vertices;

//Where the corner is - posed or not - beside where its texture goes: a posed thing's position is only in the
//side the skin pass writes, and the texture only in the mesh's own vertex. See CoreGeometryPool.FrameSide.
[[vk::binding(8,2)]] StructuredBuffer<float> g_positions;
#else
[[vk::binding(5,2)]] StructuredBuffer<float> g_positions;
#endif
//Read and never written here - and said so: a vertex stage may only store into a buffer where the device's
//vertexPipelineStoresAndAtomics is on, and the validation layer named every raster pipeline while this was RW.
[[vk::binding(6,2)]] StructuredBuffer<uint> g_visible;
[[vk::binding(7,2)]] StructuredBuffer<uint> g_materialData;

#ifdef ALPHA
[[vk::binding(0,3)]] Sampler2D g_textures[];
#endif

[[vk::push_constant]]
cbuffer Push
{
	uint g_list;
	uint g_view;

	//VIEW only: how the clusters are coloured - see DrawClusterView.
	uint g_mode;
	uint g_pad1;
};

struct V2P
{
	float4 position : SV_Position;
#ifdef ALPHA
	nointerpolation uint material : TEXCOORD2;
	float2 uv : TEXCOORD1;
#endif
#ifdef VIEW
	nointerpolation uint instance : TEXCOORD3;
	nointerpolation uint cluster : TEXCOORD4;
#endif
};

//Index encodes the entry in the high bits and the cluster vertex in the low bits (see Append)
[shader("vertex")]
V2P MainVs( uint vid : SV_VertexID )
{
	//Out of the list this draw is of - an index names an entry counted from its own list's start: see Append, and the bin pass for the lights'.
	Params params = g_params[0];
	uint pair = params.lists[g_list].base + ( ( vid >> EntryShift ) * 2 );
	uint index = g_visible[pair];
	uint cluster = ClusterOf( g_visible[pair + 1] );
	ClusterInfo held = g_clusters[cluster];
	Instance instance = g_instances[index];
	uint rest = g_clusterVertices[held.words.x + ( vid & CornerMask )];
	uint vertex = PosedVertex( instance, rest );

	V2P o;

#ifdef ALPHA
	float3 position = float3( g_positions[vertex * 3], g_positions[( vertex * 3 ) + 1], g_positions[( vertex * 3 ) + 2] );
	o.uv = asfloat( g_vertices.Load2( ( rest * EngineVertexBytes ) + EngineVertexTexCoord0 ) );
	o.material = g_materialData[params.materialsAt + instance.materialFirst + ( held.words.w & 0xFFFFu )];
#else
	float3 position = float3( g_positions[vertex * 3], g_positions[( vertex * 3 ) + 1], g_positions[( vertex * 3 ) + 2] );
#endif

	//Precise, and word for word what the shading pass works out - see LoadClusterCorner in cluster_main.slang,
	//which tests this depth for the fragment that won it and so must not be fused differently.
	precise float3 world = mul( float4( position, 1.0 ), WorldOf( instance ) ).xyz;
	precise float4 clip = mul( float4( world, 1.0 ), params.views[g_view].worldToClip );
	o.position = clip;

#ifdef VIEW
	o.instance = index;
	o.cluster = cluster;
#endif

	return o;
}

#ifdef MESH
//Mesh-shader depth path: one workgroup per visible entry, triangles read from the cluster directly
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(128, 1, 1)]
void MainMs( uint thread : SV_GroupIndex, uint3 group : SV_GroupID,
	out vertices V2P verts[128], out indices uint3 tris[124] )
{
	Params params = g_params[0];
	ListInfo list = params.lists[g_list];
	uint entry = ( group.y * 65535u ) + group.x;

	//The camera's lists hand over only what the pre-pass draws, packed - see Append; a light's list is drawn whole.
	bool camera = g_list < params.retestList;
	uint count = camera
		? min( g_visible[( g_list * 4 ) + 2], list.indexCapacity )
		: min( g_visible[( g_list * 4 ) + 1], list.capacity );

	if ( entry >= count ) { SetMeshOutputCounts( 0, 0 ); return; }

	if ( camera ) entry = g_visible[list.indexBase + entry];

	uint index = g_visible[list.base + ( entry * 2 )];
	uint packed = g_visible[list.base + ( entry * 2 ) + 1];

	ClusterInfo held = g_clusters[ClusterOf( packed )];
	Instance instance = g_instances[index];
	uint material = g_materialData[params.materialsAt + instance.materialFirst + ( held.words.w & 0xFFFFu )];

	uint vertices = held.words.z & 0xFFFFu;
	uint triangles = held.words.z >> 16;

	SetMeshOutputCounts( vertices, triangles );

	if ( thread < vertices )
	{
		uint rest = g_clusterVertices[held.words.x + thread];
		uint vertex = PosedVertex( instance, rest );
		float3 position = float3( g_positions[vertex * 3], g_positions[( vertex * 3 ) + 1], g_positions[( vertex * 3 ) + 2] );

		//Word for word what the indexed stage and the shading pass work out.
		precise float3 world = mul( float4( position, 1.0 ), WorldOf( instance ) ).xyz;
		precise float4 clip = mul( float4( world, 1.0 ), params.views[g_view].worldToClip );

		V2P o;
		o.position = clip;
#ifdef ALPHA
		o.uv = asfloat( g_vertices.Load2( ( rest * EngineVertexBytes ) + EngineVertexTexCoord0 ) );
		o.material = material;
#endif
		verts[thread] = o;
	}

	if ( thread < triangles )
	{
		uint at = ( held.words.y + thread ) * 3;

		tris[thread] = uint3( TriangleByte( g_clusterTriangles, at ), TriangleByte( g_clusterTriangles, at + 1 ),
			TriangleByte( g_clusterTriangles, at + 2 ) );
	}
}
#endif

#ifdef ALPHA
void CutOut( V2P i, bool front )
{
	//Cutout list draws unculled for two-sided leaves; discard back faces of one-sided materials here
	if ( !front && ( g_materialData[i.material * 4] & MaterialTwoSided ) == 0 ) discard;

	uint slot = g_materialData[( i.material * 4 ) + 1];
	float cutoff = asfloat( g_materialData[( i.material * 4 ) + 2] );
	if ( slot == 0 || cutoff <= 0.0 ) return;
	//At the level the material's own cut reads it - see SampleUv in frame.slang, which an upscaler biases sharper.
	//The shading is tested equal against this depth, so a leaf cut at another level here would leave a rim of holes.
	float alpha = g_textures[NonUniformResourceIndex( slot )].SampleBias( i.uv, g_frame[0].viewInfo.w ).a;
	if ( alpha < cutoff ) discard;
}
#endif

#ifdef VIEW
//A colour a number: hues a golden turn apart, so clusters built next to each other never look alike,
//and three bands of brightness so the hues that come round again still differ
float3 Hued( uint n )
{
	float hue = frac( float( n ) * 0.61803399 );
	float3 rgb = saturate( abs( frac( hue + float3( 0.0, 2.0 / 3.0, 1.0 / 3.0 ) ) * 6.0 - 3.0 ) - 1.0 );
	float value = 1.0 - ( float( ( n * 2654435761u ) >> 30 ) * 0.15 );
	return lerp( float3( 1.0, 1.0, 1.0 ), rgb, 0.85 ) * value;
}

struct ViewOut
{
	float4 colour : SV_Target0;
	float4 normal : SV_Target1;
};

//Debug overlay colouring: mode 1 per cluster, mode 2 per DAG level, mode 3 per instance
[shader("fragment")]
ViewOut MainPs( V2P i, uint primitive : SV_PrimitiveID )
{
	float3 colour = Hued( i.cluster );

	if ( g_mode == 2 )
	{
		uint level = g_clusters[i.cluster].words.w >> 16;
		float3 ladder[8] = {
			float3( 0.15, 0.65, 0.20 ), float3( 0.35, 0.75, 0.20 ),
			float3( 0.75, 0.80, 0.20 ), float3( 0.90, 0.60, 0.15 ),
			float3( 0.95, 0.40, 0.15 ), float3( 0.90, 0.20, 0.25 ),
			float3( 0.80, 0.20, 0.60 ), float3( 0.60, 0.25, 0.85 ) };
		colour = ladder[min( level, 7u )];
	}
	else if ( g_mode == 3 )
	{
		colour = Hued( i.instance + 1 );
	}

	//The triangle, kept faintly, so the shape still reads without hiding where a cluster ends
	float shade = 0.9 + ( ( primitive % 3u ) * 0.05 );

	ViewOut o;
	o.colour = float4( colour * shade, 1.0 );
	o.normal = float4( 0.0, 0.0, 0.0, 0.0 );
	return o;
}
#else
[shader("fragment")]
void MainPs( V2P i, bool front : SV_IsFrontFace )
{
#ifdef ALPHA
	CutOut( i, front );
#endif
}
#endif
