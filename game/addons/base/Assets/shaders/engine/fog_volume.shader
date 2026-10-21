//Integrates fog once per frame into froxel slices; surfaces read scattered light and transmittance

FEATURES
{
}

PARAMETERS
{
	//The grid: froxels across and down, how many slices, and how far the farthest slice reaches in metres.
	float4 g_vGrid < Attribute( "Grid" ); >;

	//Slice tiling across/down and near distance; near must match the reader exactly, so it rides here
	float4 g_vTiles < Attribute( "Tiles" ); >;

	//The sheet.
	RWTexture2D g_tOut < Attribute( "Out" ); Format( "rgba16f" ); >;
}

#include "postprocess.slang"

//End depth of slice s, geometric; must match FogVolumeSlice in frame.slang exactly
float SliceEnd( float s )
{
	float slices = max( g_vGrid.z, 1.0 );
	float near = max( g_vTiles.z, 0.01 );
	float far = max( g_vGrid.w, near * 2.0 );

	return near * pow( far / near, s / slices );
}

//Where a ray enters and leaves one volume's box, in the ray's own units - the same slab test the march made,
//so the two agree about where the fog is. Nought and nought, an empty span, where the ray misses the box.
float2 FogVolumeSpan( FogVolume volume, float3 eye, float3 ray )
{
	float3 extents = volume.extents.xyz;
	float3 origin = TurnInto( volume.rotation, eye - volume.centre.xyz ) / extents;
	float3 direction = TurnInto( volume.rotation, ray ) / extents;

	float3 safe = max( abs( direction ), 1e-6 ) * sign( direction + 1e-7 );
	float3 near = ( -1.0 - origin ) / safe;
	float3 far = ( 1.0 - origin ) / safe;
	float3 enter = min( near, far );
	float3 leave = max( near, far );

	float t0 = max( max( enter.x, enter.y ), max( enter.z, 0.0 ) );
	float t1 = min( min( leave.x, leave.y ), leave.z );

	return t1 > t0 ? float2( t0, t1 ) : float2( 0.0, 0.0 );
}

//How dense one volume is at a point - one at its middle, nought at its faces, raised to its fade, times its strength. The march's own formula.
float FogVolumeDensityOf( FogVolume volume, float3 world )
{
	float3 local = TurnInto( volume.rotation, world - volume.centre.xyz ) / volume.extents.xyz;
	float edge = saturate( 1.0 - max( abs( local.x ), max( abs( local.y ), abs( local.z ) ) ) );

	return edge > 0.0 ? volume.centre.w * kFogVolumeDensity * pow( edge, volume.extents.w ) : 0.0;
}

//One thread per froxel column, marching outward so the running integral costs one step per slice
[shader("compute")]
[numthreads(8, 8, 1)]
void MainCs( uint3 id : SV_DispatchThreadID )
{
	uint2 grid = uint2( max( g_vGrid.x, 1.0 ), max( g_vGrid.y, 1.0 ) );

	if ( id.x >= grid.x || id.y >= grid.y ) return;

	uint slices = uint( max( g_vGrid.z, 1.0 ) );

	//The middle of this column on the screen, from nought to one, and the ray that goes through it.
	float2 uv = ( float2( id.xy ) + 0.5 ) / float2( grid );

	float3 eye = EYE_FRAME( camera ).xyz;

	//Ray through the froxel from the frame's own unprojection, so there's one idea of screen position
	float4 through4 = mul( float4( ( uv * 2.0 ) - 1.0, 0.5, 1.0 ), EYE_FRAME( clipToWorld ) );
	float3 view = normalize( ( through4.xyz / through4.w ) - eye );

	//And scaled so that one step along it raises the clip position's w by one: the slices are laid out in that w, which is exactly what
	//the reader computes for a surface - see FogVolumeSheet in frame.slang. The length is how many metres of air one of those steps is.
	float rate = max( mul( float4( view, 0.0 ), EYE_FRAME( worldToClip ) ).w, 1e-6 );
	float3 ray = view / rate;
	float rayLength = 1.0 / rate;

	bool sun = HasSun();
	float3 sunDirection = g_frame[0].sunDirection.xyz;
	float3 sunLight = g_frame[0].sunLight.rgb;
	float phase = sun ? FogPhase( dot( view, sunDirection ) ) : 0.0;

	float3 ambient = g_frame[0].ambient.rgb;

	float through = 1.0;
	float3 scattered = float3( 0.0, 0.0, 0.0 );

	//Clip the ray to each volume once so slices integrate only the part inside the fog
	int count = min( (int)g_frame[0].fogVolumeInfo.x, 8 );
	float2 spans[8];

	for ( int v = 0; v < count; ++v ) spans[v] = FogVolumeSpan( g_frame[0].fogVolumes[v], eye, ray );

	float last = 0.0;

	for ( uint s = 0; s < slices; ++s )
	{
		float end = SliceEnd( float( s + 1 ) );

		//What each volume puts into this slice: its density at the middle of the part of the slice inside it, times the length of that part.
		float depth = 0.0;
		float3 tint = float3( 0.0, 0.0, 0.0 );
		float3 centre = float3( 0.0, 0.0, 0.0 );

		for ( int v = 0; v < count; ++v )
		{
			float a = max( last, spans[v].x );
			float b = min( end, spans[v].y );
			if ( b <= a ) continue;

			FogVolume volume = g_frame[0].fogVolumes[v];

			float3 here = eye + ray * ( ( a + b ) * 0.5 );
			float optical = FogVolumeDensityOf( volume, here ) * ( b - a ) * rayLength;

			depth += optical;
			tint += volume.colour.rgb * optical;
			centre += here * optical;
		}

		last = end;

		if ( depth > 0.0 )
		{
			tint /= depth;
			centre /= depth;

			//Light at this froxel: ambient, lamps (only if the frame has any) and one sun shadow tap
			float3 light = ambient;

			if ( g_vTiles.w > 0.5 )
				light += FogLampLight( centre, view );

			if ( sun ) light += sunLight * phase * SunShadow( centre, sunDirection, sunDirection, false );

			float taken = 1.0 - exp( -depth );

			scattered += through * taken * light * tint;
			through *= 1.0 - taken;
		}

		//Into slice s's own tile of the sheet - see FogVolumeSheet in frame.slang, which reads it back the same way round.
		uint columns = uint( max( g_vTiles.x, 1.0 ) );

		uint2 tile = uint2( s % columns, s / columns );

		g_tOut[uint2( ( tile.x * grid.x ) + id.x, ( tile.y * grid.y ) + id.y )] = float4( scattered, through );
	}
}
