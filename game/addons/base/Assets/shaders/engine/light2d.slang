//The 2D lighting's debug view over the world - see CoreLight2D.DrawDebug and Lighting2D.Debug

[[vk::push_constant]]
cbuffer Push
{
	//Which view z - the picture itself is g_color, and how the screen sits in it the frame's light2dScene and light2dSnap
	float4 g_where;
};

#include "frame.slang"

struct DebugV2F
{
	float4 position : SV_Position;
	float2 clip : TEXCOORD0;
};

[shader("vertex")]
DebugV2F DebugVs( uint id : SV_VertexID )
{
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );

	DebugV2F o;
	o.clip = ( corner * 2.0 ) - 1.0;
	o.position = float4( o.clip, 0.0, 1.0 );
	return o;
}

[shader("fragment")]
float4 DebugPs( DebugV2F i ) : SV_Target0
{
	//The screen is the middle of the picture, shrunk by the margin and moved onto its texels; both are rasterised the same way up
	float2 uv = ( ( ( i.clip * g_frame[0].light2dScene.xz ) + g_frame[0].light2dSnap.xy ) * 0.5 ) + 0.5;
	float4 seen = g_color.SampleLevel( uv, 0.0 );

	//The light: as it is
	if ( g_where.z > 1.5 ) return float4( seen.rgb, 1.0 );

	//The scene: what glows in its colour, what only blocks in grey
	float glow = max( seen.r, max( seen.g, seen.b ) );

	return float4( seen.rgb + ( ( 1.0 - saturate( glow ) ) * seen.a * 0.35 ), 1.0 );
}
