//What the hardware ray passes share (ray_bounce.slang, ray_grid.slang): the structure, a hash, directions over the
//sphere, a shadow ray and a lamp's light. Each declares g_params, Address(), LampsAt and LampCount() before this

RaytracingAccelerationStructure Scene()
{
	uint2 address = Address();
	return RaytracingAccelerationStructure( ( uint64_t( address.y ) << 32 ) | uint64_t( address.x ) );
}

//A small hash, a float in [0, 1)
float Random( inout uint seed )
{
	seed = ( seed * 747796405u ) + 2891336453u;
	uint word = ( ( seed >> ( ( seed >> 28u ) + 4u ) ) ^ seed ) * 277803737u;
	return float( ( word >> 22u ) ^ word ) * ( 1.0 / 4294967296.0 );
}

//The i-th of n directions spread evenly over the sphere, turned - spherical Fibonacci, as DDGI's
float3 Spread( uint i, uint n, float turn )
{
	float golden = 2.39996323;
	float z = 1.0 - ( ( 2.0 * float( i ) + 1.0 ) / float( n ) );
	float r = sqrt( max( 1.0 - ( z * z ), 0.0 ) );
	float a = ( float( i ) * golden ) + turn;
	return float3( r * cos( a ), z, r * sin( a ) );
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

//One lamp's light at a point over pi, as BakeScene.DirectOne without its shape, shadowed by a ray
float3 Lamp( RaytracingAccelerationStructure scene, uint index, float3 at, float3 n )
{
	float4 place = g_params[LampsAt + ( index * 3u )];
	float4 colour = g_params[LampsAt + ( index * 3u ) + 1u];
	float4 cone = g_params[LampsAt + ( index * 3u ) + 2u];

	float3 toLamp = place.xyz - at;
	float distance = length( toLamp );
	if ( distance > place.w || distance < 1e-4 ) return 0.0;

	float3 l = toLamp / distance;
	float facing = dot( n, l );
	if ( facing <= 0.0 ) return 0.0;

	float reach = distance / max( place.w, 1e-4 );
	float window = saturate( 1.0 - ( reach * reach * reach * reach ) );
	float square = max( distance * distance, 1e-4 );
	float spread = colour.w == 1.0 ? square : pow( square, colour.w );
	float attenuation = window * window / max( spread, 1e-4 );

	//A spot: its forward and outer cosine, faded from the outer edge to straight ahead
	if ( dot( cone.xyz, cone.xyz ) > 0.5 )
		attenuation *= saturate( ( dot( -l, cone.xyz ) - cone.w ) / max( 1.0 - cone.w, 1e-4 ) );

	if ( attenuation <= 0.0 || Blocked( scene, at + ( n * 0.01 ), l, distance - 0.02 ) ) return 0.0;

	return colour.rgb * ( facing * attenuation / Pi );
}

