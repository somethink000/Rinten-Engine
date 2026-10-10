//The sky drawn into the sky light's octahedral capture by its own MainPs, built with SKY_CAPTURE - see CoreSkyLight.
//One triangle a face of the octahedron: SkyLightRay is linear across each, so the direction comes through exact

//A quadrant's inner face (upper half) and outer face (lower half), as corners of the quadrant's own square
static const float2 SkyCaptureFaces[6] =
{
	float2( 0.0, 0.0 ), float2( 1.0, 0.0 ), float2( 0.0, 1.0 ),
	float2( 1.0, 0.0 ), float2( 1.0, 1.0 ), float2( 0.0, 1.0 ),
};

[shader("vertex")]
V2F MainSkyCaptureVs( uint id : SV_VertexID )
{
	//Four quadrants of six corners; CoreSkyLight.CaptureVertices
	uint quadrant = id / 6u;
	uint corner = id % 6u;

	float2 side = float2( ( quadrant & 1u ) != 0u ? -1.0 : 1.0, ( quadrant & 2u ) != 0u ? -1.0 : 1.0 );

	//A mirrored quadrant turns its triangles over; swapping two corners keeps every one facing the same way
	if ( side.x * side.y < 0.0 && ( corner % 3u ) != 0u ) corner = ( corner % 3u ) == 1u ? corner + 1u : corner - 1u;

	float2 p = SkyCaptureFaces[corner] * side;

	V2F o;

	//Vulkan's clip y runs down, as the picture's v does
	o.position = float4( p, 0.0, 1.0 );
	o.direction = SkyLightRay( ( p * 0.5 ) + 0.5 );

	return o;
}
