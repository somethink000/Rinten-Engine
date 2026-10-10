//The sky drawn into an equirectangular picture by its own MainPs, built with SKY_CAPTURE, for the sky light - see
//CoreSkyLight. The engine's skies take the exact direction from the pixel (SkyCaptureSize); for any other, a grid of
//cells, each corner its world direction, interpolates close to it

//Cells across and down; CoreSkyLight.CaptureVertices is six a cell
static const uint SkyCaptureColumns = 64u;
static const uint SkyCaptureRows = 32u;

static const float2 SkyCaptureCorners[6] =
{
	float2( 0.0, 0.0 ), float2( 1.0, 0.0 ), float2( 0.0, 1.0 ),
	float2( 1.0, 0.0 ), float2( 1.0, 1.0 ), float2( 0.0, 1.0 ),
};

[shader("vertex")]
V2F MainSkyCaptureVs( uint id : SV_VertexID )
{
	uint cell = id / 6u;
	float2 at = ( float2( cell % SkyCaptureColumns, cell / SkyCaptureColumns ) + SkyCaptureCorners[id % 6u] )
		/ float2( SkyCaptureColumns, SkyCaptureRows );

	V2F o;

	//Vulkan's clip y runs down, as the picture's v does
	o.position = float4( ( at * 2.0 ) - 1.0, 0.0, 1.0 );
	o.direction = PanoramaDirection( at );

	return o;
}
