//The scene through hardware rays as the post passes shade it (ray_light.shader, reflection.shader): the structure, a shadow
//ray, an instance's record and what leaves a hit. Each declares PlaceOf before this; nothing of it in the portable frame

//Where a point lands on the screen through a view, nought to one; false behind it or off it
bool ScreenOf( float3 world, float4x4 toClip, out float2 uv )
{
	float4 clip = mul( float4( world, 1.0 ), toClip );
	uv = 0.0;

	if ( clip.w <= 1e-4 ) return false;

	uv = ( ( clip.xy / clip.w ) * 0.5 ) + 0.5;

	return all( uv >= 0.0 ) && all( uv <= 1.0 );
}

#ifndef PORTABLE

RaytracingAccelerationStructure RayScene()
{
	uint64_t low = uint64_t( uint( g_frame[0].rays.x ) );
	uint64_t middle = uint64_t( uint( g_frame[0].rays.y ) );
	uint64_t high = uint64_t( uint( g_frame[0].rays.w ) );

	return RaytracingAccelerationStructure( low | ( middle << 24 ) | ( high << 48 ) );
}

//Whether anything stands between a point and a distance along a way
bool Blocked( RaytracingAccelerationStructure scene, float3 from, float3 way, float far )
{
	RayDesc ray;
	ray.Origin = from;
	ray.Direction = way;
	ray.TMin = 0.0;
	ray.TMax = far;

	RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
	query.TraceRayInline( scene, RAY_FLAG_NONE, 0xFF, ray );
	query.Proceed();

	return query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
}

//An instance's record, out of the colours picture: albedo, then glow - see CoreRays.WriteColours
float3 RecordOf( uint record, uint which )
{
	uint picture = uint( g_frame[0].rays.z );
	float2 size;
	g_textures[picture].GetDimensions( size.x, size.y );

	uint across = max( uint( size.x ), 1u );
	uint texel = ( record * 2u ) + which;
	float2 uv = ( float2( float( texel % across ), float( texel / across ) ) + 0.5 ) / max( size, float2( 1.0, 1.0 ) );

	return g_textures[picture].SampleLevel( uv, 0.0 ).rgb;
}

//The frame's lights at a point, over pi, each shadowed by a ray; a shaped lamp as the point it stands on
float3 LightsAt( RaytracingAccelerationStructure scene, float3 at, float3 n )
{
	float3 sum = 0.0;
	uint count = LightCount();
	uint traced = 0u;

	for ( uint i = 0u; i < count && traced < 8u; ++i )
	{
		Light light = LightAt( i );

		//A baked light's bounce is in the bake already
		if ( LightBaked( light ) ) continue;

		traced++;

		float3 l;
		float far = 10000.0;
		float attenuation = 1.0;

		if ( light.colour.w < 0.5 )
		{
			l = -light.direction.xyz;
		}
		else
		{
			LightLamp( i, light );

			float3 toLight = light.position.xyz - at;
			float distance = length( toLight );
			if ( distance > light.position.w || distance < 1e-4 ) continue;

			l = toLight / distance;
			far = distance - 0.02;

			float reach = distance / max( light.position.w, 1e-4 );
			float window = saturate( 1.0 - ( reach * reach * reach * reach ) );
			float square = max( distance * distance, 1e-4 );
			float spread = light.shape.x == 1.0 ? square : pow( square, light.shape.x );

			attenuation = window * window / max( spread, 1e-4 );

			if ( light.colour.w > 1.5 )
			{
				float aligned = dot( -l, light.direction.xyz );
				attenuation *= saturate( ( aligned - light.cone.x ) / max( light.direction.w - light.cone.x, 1e-4 ) );
			}
		}

		float facing = dot( n, l );
		if ( facing <= 0.0 || attenuation <= 0.0 ) continue;
		if ( Blocked( scene, at, l, far ) ) continue;

		sum += light.colour.rgb * ( facing * attenuation * 0.31830989 );
	}

	return sum;
}

//The light around a point the frame's lights leave out, over pi: the bake's volume and sky, else the sky
float3 AroundAt( float3 at, float3 n )
{
	if ( HasBakedVolume() )
	{
		float3 spot = BakedVolumeSpot( at, n );
		float3 around = BakedVolume( spot, n ) + BakedGlowVolumes( spot );

		if ( HasBakedBounce() ) around += BakedBounce( spot, n );
		if ( HasBakedSky() ) around += SkyLit( n ) * BakedSkyVolume( spot, n );

		return around;
	}

	return SceneAmbient( HasSky() ? SkyAmbient( n ) * SkyAmbientScale() : float3( 0.0, 0.0, 0.0 ) );
}

//What leaves a hit facing n towards the ray: the lit world where the screen shows that very point - last frame's picture
//by its place in the table, this frame's where last is nought, none below - else lit here by the instance's record
float3 HitLight( RaytracingAccelerationStructure scene, float3 at, float3 n, uint record, float last )
{
	float2 now;

	if ( last >= 0.0 && ScreenOf( at, EYE_FRAME( worldToClip ), now ) )
	{
		float3 seen = PlaceOf( now, SceneDepthRaw( now ) );
		float slack = 0.05 + ( 0.01 * length( at - EYE_FRAME( camera ).xyz ) );

		float2 was;

		if ( length( seen - at ) < slack )
		{
			if ( last < 0.5 ) return SceneColour( now );
			if ( ScreenOf( at, EYE_FRAME( previousWorldToClip ), was ) ) return FramePicture( last, was ).rgb;
		}
	}

	float3 lifted = at + ( n * 0.01 );

	return ( RecordOf( record, 0u ) * ( LightsAt( scene, lifted, n ) + AroundAt( lifted, n ) ) ) + RecordOf( record, 1u );
}

#endif
