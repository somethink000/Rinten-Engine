//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Poses a morphed model's vertices once a frame for the portable frame: shapes, then 4-bone skin, into whole engine
//vertices every pass then draws as a still mesh - see CoreSceneDrawer.PoseAhead

#include "vertex_frame.slang"
#include "skin_blend.slang"

//The model's own vertices, 52 bytes each - see PackedVertex
[[vk::binding(0,0)]] ByteAddressBuffer g_vertices;

//Four bone links and four weights in a byte each, a vertex
[[vk::binding(1,0)]] ByteAddressBuffer g_skin;

//Where each vertex's blend shape deltas start and how many it has
[[vk::binding(2,0)]] ByteAddressBuffer g_runs;

//The deltas, 28 bytes each: shape, position, normal - see MorphDeltaStride in frame.slang
[[vk::binding(3,0)]] ByteAddressBuffer g_deltas;

//The frame's room: the bones and weights written into it for this model, and its posed vertices out
[[vk::binding(4,0)]] RWByteAddressBuffer g_room;
[[vk::binding(5,0)]] StructuredBuffer<float4x4> g_bones;
[[vk::binding(6,0)]] StructuredBuffer<float> g_weights;

[[vk::push_constant]]
cbuffer PosePush
{
	uint g_count;

	//Byte offset of the posed vertices in the room; the bones' and weights' offsets in their elements
	uint g_posedAt;
	uint g_bonesAt;
	uint g_weightsAt;

	uint g_boneCount;
	uint g_weightCount;
	uint g_skinned;
	uint g_deltaCount;
};

static const uint VertexStride = 52;
static const uint DeltaStride = 28;

//The same bounds the vertex stage keeps, so a bad run cannot hang the card
static const uint MorphsPerVertex = 64;

[shader("compute")]
[numthreads(64, 1, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_count ) return;

	uint from = id.x * VertexStride;

	float3 position = asfloat( g_vertices.Load3( from ) );
	uint colour = g_vertices.Load( from + 12 );
	uint frame = g_vertices.Load( from + 16 );
	uint4 first = g_vertices.Load4( from + 20 );
	uint4 second = g_vertices.Load4( from + 36 );

	float3 normal = VertexFrameNormal( frame );
	float4 tangent = VertexFrameTangent( frame );

	//Shapes in model space, before the bones, as the vertex stage adds them
	uint2 run = g_runs.Load2( id.x * 8 );
	uint count = run.x < g_deltaCount ? min( min( run.y, MorphsPerVertex ), g_deltaCount - run.x ) : 0;

	for ( uint d = 0; d < count; d++ )
	{
		uint at = ( run.x + d ) * DeltaStride;
		uint shape = g_deltas.Load( at );
		float weight = shape < g_weightCount ? g_weights[g_weightsAt + shape] : 0.0;

		position += asfloat( g_deltas.Load3( at + 4 ) ) * weight;
		normal += asfloat( g_deltas.Load3( at + 16 ) ) * weight;
	}

	float along = dot( normal, normal );
	if ( along > 1e-8 ) normal *= rsqrt( along );

	if ( g_skinned != 0 )
	{
		uint packedLinks = g_skin.Load( id.x * 8 );
		uint packedWeights = g_skin.Load( ( id.x * 8 ) + 4 );
		uint4 links = uint4( packedLinks & 255u, ( packedLinks >> 8 ) & 255u, ( packedLinks >> 16 ) & 255u, packedLinks >> 24 );
		float4 weights = float4( packedWeights & 255u, ( packedWeights >> 8 ) & 255u, ( packedWeights >> 16 ) & 255u, packedWeights >> 24 ) / 255.0;

		uint last = max( g_boneCount, 1u ) - 1u;

		//Linked bones only: a slot with no weight often names bone 255
		float4x4 one = g_bones[g_bonesAt + min( links.x, last )];
		float4x4 skin = SkinBlend( one,
			weights.y > 0.0 ? g_bones[g_bonesAt + min( links.y, last )] : one,
			weights.z > 0.0 ? g_bones[g_bonesAt + min( links.z, last )] : one,
			weights.w > 0.0 ? g_bones[g_bonesAt + min( links.w, last )] : one,
			weights );

		//Unweighted vertices keep their place instead of collapsing to the origin
		if ( weights.x + weights.y + weights.z + weights.w > 0.0 )
		{
			position = mul( float4( position, 1.0 ), skin ).xyz;
			normal = mul( float4( normal, 0.0 ), skin ).xyz;
			tangent.xyz = mul( float4( tangent.xyz, 0.0 ), skin ).xyz;
		}
	}

	uint to = g_posedAt + from;

	g_room.Store3( to, asuint( position ) );
	g_room.Store( to + 12, colour );
	g_room.Store( to + 16, VertexFrameEncode( normal, tangent ) );
	g_room.Store4( to + 20, first );
	g_room.Store4( to + 36, second );
}
