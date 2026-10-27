//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Editor grid drawn onto mesh faces as a second pass; lines come from the pixel, one pixel wide

#include "vertex_frame.slang"

struct VIn
{
	[[vk::location(0)]] float3 position;

	//The engine vertex's packed normal and tangent - see vertex_frame.slang.
	[[vk::location(2)]] uint frame;
};

struct V2F
{
	float4 position : SV_Position;

	//Where the pixel is in the world, and which way its surface faces. The grid is the world's, so the
	//world position is the whole of what it is worked out from; the normal only picks which two axes.
	float3 world : TEXCOORD0;
	float3 normal : TEXCOORD1;
};

//Exactly 128 bytes: world matrix as three columns frees 16 bytes for cell, major, strength, bias
[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_worldToClip;

	float4 g_worldX;
	float4 g_worldY;
	float4 g_worldZ;

	//How far apart the cells are, in metres - the editor's grid spacing.
	float g_cell;

	//How many cells to a major line. Ten, as the ground grid had.
	float g_majorEvery;

	//How strongly the whole thing is laid on.
	float g_opacity;

	//Towards the camera, in clip depth, so the grid wins against the surface it lies on. The surface is drawn by
	//another pass with another shader; the two agree on where the triangle is to the last bit or two, not exactly.
	float g_bias;
};

[shader("vertex")]
V2F MainVs( VIn i )
{
	float4 local = float4( i.position, 1.0 );

	float3 world = float3( dot( local, g_worldX ), dot( local, g_worldY ), dot( local, g_worldZ ) );

	V2F o;

	o.world = world;

	//Good enough to choose an axis with, which is all it is for: a mesh scaled unevenly
	//would want the inverse transpose, and picking the largest of three does not care.
	float3 normal = VertexFrameNormal( i.frame );
	o.normal = float3( dot( normal, g_worldX.xyz ), dot( normal, g_worldY.xyz ), dot( normal, g_worldZ.xyz ) );

	o.position = mul( float4( world, 1.0 ), g_worldToClip );
	//Towards the eye, which a reversed depth counts upward - see NearDepth in frame.slang
	o.position.z += g_bias * o.position.w;

	return o;
}

//Pixel coverage of the nearest grid line, one pixel wide; fades out once cells get too small
float Coverage( float2 cells )
{
	float2 width = fwidth( cells );

	float2 distance = abs( frac( cells - 0.5 ) - 0.5 ) / max( width, 1e-6 );

	float line = 1.0 - saturate( min( distance.x, distance.y ) );

	//One at four pixels to a cell and wider, nothing at two and narrower.
	float apart = max( width.x, width.y );

	return line * saturate( ( 0.5 - apart ) * 4.0 );
}

[shader("fragment")]
float4 MainPs( V2F i ) : SV_Target
{
	float3 facing = abs( normalize( i.normal ) );

	//The two world axes the face lies most nearly in. The third is the one it faces down, and the planes at right angles to that one never
	//cut it - on a floor they are the horizontal ones, which is why a floor gets a square grid and not three families of lines crossing.
	float2 at = ( facing.x >= facing.y && facing.x >= facing.z ) ? i.world.zy
		: ( facing.y >= facing.z ) ? i.world.xz
		: i.world.xy;

	float cell = max( g_cell, 0.0001 );

	float minor = Coverage( at / cell );
	float major = Coverage( at / ( cell * max( g_majorEvery, 2.0 ) ) );

	//A light grey, in linear light: this lands on a target that carries the curve, so a byte written as it
	//was authored comes out lighter than it was meant to - see the axes in CoreSceneDrawer for the same sum.
	const float3 ink = float3( 0.52, 0.55, 0.62 );

	float strength = max( minor * 0.30, major * 0.60 ) * g_opacity;

	if ( strength <= 0.004 ) discard;

	return float4( ink, strength );
}
