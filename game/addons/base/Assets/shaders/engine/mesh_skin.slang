//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Poses every vertex of a posed instance (shape sums, then 4-bone skin) into the pool, when it changed

#include "mesh_tables.slang"
#include "vertex_frame.slang"
#include "skin_blend.slang"
#include "mesh_deform.slang"


[[vk::binding(0,0)]] StructuredBuffer<Instance> g_instances;
//Pool frames and positions stored beside vertices, so posing writes only the 16 bytes that move
[[vk::binding(1,0)]] RWStructuredBuffer<uint> g_frames;
[[vk::binding(2,0)]] ByteAddressBuffer g_skin;
[[vk::binding(3,0)]] RWStructuredBuffer<float> g_positions;
[[vk::binding(4,0)]] StructuredBuffer<float4x4> g_palettes;

//What this instance's blend shapes add to each vertex, summed over the shapes that have a weight - a float3 of
//position and one of normal a vertex, a range of CoreSceneTables.ShapeSums that mesh_shapes.slang has just written.
[[vk::binding(5,0)]] ByteAddressBuffer g_shapeSums;

//Where each vertex stood before this posing, a float3 a vertex from g_wasAt - what the velocity pass moves it from
[[vk::binding(6,0)]] RWStructuredBuffer<float> g_was;

//Its ModelDeformer volumes, then for a rigid one an anchor a bone - see mesh_deform.slang and CoreSceneTables.Deformations
[[vk::binding(7,0)]] StructuredBuffer<float4> g_deform;

[[vk::push_constant]]
cbuffer SkinPush
{
	uint g_index;
	uint g_first;
	uint g_count;

	//Where this instance's range of g_was starts, in vertices
	uint g_wasAt;

	//Where this instance's sums start, in vertices, and whether it has any - nought where nothing
	//drives its shapes, and then the buffer above is whatever was to hand and is not read.
	uint g_sumsAt;
	uint g_shaped;

	//Whether it has a skeleton - see above. And whether its range holds a pose to keep in g_was first
	uint g_skinned;
	uint g_keepWas;

	//Where its volumes start in g_deform and how many; where its anchors start, nought for none
	uint g_deformAt;
	uint g_deformCount;
	uint g_anchorsAt;
	uint g_pad;
};

static const uint ShapeSumBytes = 24;


[shader("compute")]
[numthreads(128, 1, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_count ) return;

	Instance instance = g_instances[g_index];
	if ( ( instance.flags & InstancePosed ) == 0 ) return;

	uint source = g_first + id.x;
	uint posed = ( source - instance.posedSource ) + instance.posedFirst;

	float3 position = float3( g_positions[source * 3], g_positions[( source * 3 ) + 1], g_positions[( source * 3 ) + 2] );

	//The frame is one packed word - see vertex_frame.slang. Unpacked, posed and packed again; a vertex nothing moves keeps the word it had.
	uint frame = g_frames[source];

	float3 normal = 0;
	float4 tangent = 0;
	bool moved = false;

	//The shapes first: a blend shape is authored against the mesh as it was modelled, so its deltas
	//belong in that space and the bones carry the moved vertex the way they carry the still one.
	if ( g_shaped != 0 )
	{
		uint sum = ( g_sumsAt + id.x ) * ShapeSumBytes;
		float3 moveBy = asfloat( g_shapeSums.Load3( sum ) );
		float3 turnBy = asfloat( g_shapeSums.Load3( sum + 12 ) );

		if ( any( moveBy != 0.0 ) || any( turnBy != 0.0 ) )
		{
			position += moveBy;
			normal = VertexFrameNormal( frame ) + turnBy;
			tangent = VertexFrameTangent( frame );

			//Two normals added are not a normal - normalised as the classic stage does, so the two ways light
			//a moved face alike. The tangent is not moved there either; the packing lays it back on the plane.
			float along = dot( normal, normal );
			if ( along > 1e-8 ) normal *= rsqrt( along );

			moved = true;
		}
	}

	bool rigid = g_anchorsAt != 0 && g_skinned != 0;

	if ( g_deformCount > 0 && !rigid )
	{
		if ( !moved )
		{
			normal = VertexFrameNormal( frame );
			tangent = VertexFrameTangent( frame );
		}

		DeformApply( g_deform, g_deformAt, g_deformCount, position, normal, tangent );
		moved = true;
	}

	if ( g_skinned != 0 )
	{
		uint packedLinks = g_skin.Load( source * 8 );
		uint packedWeights = g_skin.Load( ( source * 8 ) + 4 );
		uint4 links = uint4( packedLinks & 255u, ( packedLinks >> 8 ) & 255u, ( packedLinks >> 16 ) & 255u, packedLinks >> 24 );
		float4 weights = float4( packedWeights & 255u, ( packedWeights >> 8 ) & 255u, ( packedWeights >> 16 ) & 255u, packedWeights >> 24 ) / 255.0;

		uint palette = instance.posedPad.x;
		uint bones = max( instance.posedPad.y, 1u );

		if ( g_deformCount > 0 && rigid ) position = DeformRigid( g_deform, g_deformAt, g_deformCount, g_anchorsAt, bones, links, weights, position );

		//Linked bones only: a slot with no weight often names bone 255.
		float4x4 first = g_palettes[palette + min( links.x, bones - 1 )];
		float4x4 skin = SkinBlend( first,
			weights.y > 0.0 ? g_palettes[palette + min( links.y, bones - 1 )] : first,
			weights.z > 0.0 ? g_palettes[palette + min( links.z, bones - 1 )] : first,
			weights.w > 0.0 ? g_palettes[palette + min( links.w, bones - 1 )] : first,
			weights );

		//Unweighted vertices keep their mesh position and frame instead of collapsing to the origin
		float total = weights.x + weights.y + weights.z + weights.w;

		if ( total > 0.0 )
		{
			if ( !moved )
			{
				normal = VertexFrameNormal( frame );
				tangent = VertexFrameTangent( frame );
			}

			position = mul( float4( position, 1.0 ), skin ).xyz;
			normal = mul( float4( normal, 0.0 ), skin ).xyz;
			tangent.xyz = mul( float4( tangent.xyz, 0.0 ), skin ).xyz;
			moved = true;
		}
	}

	if ( moved ) frame = VertexFrameEncode( normal, tangent );

	g_frames[posed] = frame;

	uint at = posed * 3;

	if ( g_keepWas != 0 )
	{
		uint was = ( g_wasAt + id.x ) * 3;
		g_was[was] = g_positions[at];
		g_was[was + 1] = g_positions[at + 1];
		g_was[was + 2] = g_positions[at + 2];
	}

	g_positions[at] = position.x;
	g_positions[at + 1] = position.y;
	g_positions[at + 2] = position.z;
}
