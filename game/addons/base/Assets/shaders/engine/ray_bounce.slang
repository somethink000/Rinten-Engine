//The bounce of the lights left out of the bake, through hardware rays (CoreLiveBounce, VkRays): each probe's rays light
//what they hit by those lights and the last bounce, kept as a running mean - DDGI's way, into the surfels' two pictures

//Header, lamps and every instance's colours - see CoreLiveBounce.Parameters
[[vk::binding(0,0)]] StructuredBuffer<float4> g_params;

//Each probe's light by colour: constant and lean, a float4 a colour
[[vk::binding(1,0)]] RWStructuredBuffer<float4> g_state;

//Each probe as the two pictures want it, in halves: colour then lean, each a run of the probes
[[vk::binding(2,0)]] RWStructuredBuffer<uint2> g_out;

[[vk::push_constant]]
cbuffer Push
{
	//The first probe this dispatch traces, how many, and this update's turn of the rays
	uint g_first;
	uint g_count;
	uint g_turn;
	uint g_spare;
};

static const float Pi = 3.14159265;

//Where the parts of the header are, in float4s - CoreLiveBounce writes the same
static const uint HeaderAt = 0u;
static const uint LampsAt = 6u;
static const uint MostLamps = 32u;
static const uint RecordsAt = LampsAt + ( MostLamps * 3u );

uint2 Address() { return asuint( g_params[HeaderAt].xy ); }
uint ProbeCount() { return asuint( g_params[HeaderAt].z ); }
uint Rays() { return asuint( g_params[HeaderAt].w ); }
float3 Origin() { return g_params[HeaderAt + 1u].xyz; }
float Step() { return g_params[HeaderAt + 1u].w; }
uint3 Counts() { return asuint( g_params[HeaderAt + 2u].xyz ); }
uint LampCount() { return asuint( g_params[HeaderAt + 2u].w ); }
float3 SunTowards() { return g_params[HeaderAt + 3u].xyz; }
float3 SunColour() { return g_params[HeaderAt + 4u].rgb; }
float Keep() { return g_params[HeaderAt + 4u].w; }

#include "engine/ray_common.slang"

//The last bounce at a point on a surface facing n, over pi: the lattice's eight nearest probes, lerped
float3 Bounced( float3 at, float3 n )
{
	uint3 counts = Counts();
	float3 cell = clamp( ( at - Origin() ) / Step(), float3( 0.0, 0.0, 0.0 ), float3( counts ) - 1.0 );
	uint3 low = min( uint3( cell ), counts - 2u );
	float3 f = cell - float3( low );

	float3 sum = 0.0;

	[unroll]
	for ( uint c = 0u; c < 8u; ++c )
	{
		uint3 corner = low + uint3( c & 1u, ( c >> 1u ) & 1u, ( c >> 2u ) & 1u );
		float3 w3 = lerp( 1.0 - f, f, float3( corner - low ) );
		float w = w3.x * w3.y * w3.z;
		uint probe = corner.x + ( counts.x * ( corner.y + ( counts.y * corner.z ) ) );

		float4 r = g_state[probe * 3u];
		float4 g = g_state[( probe * 3u ) + 1u];
		float4 b = g_state[( probe * 3u ) + 2u];
		float4 facing = float4( 1.0, n );

		sum += max( float3( dot( r, facing ), dot( g, facing ), dot( b, facing ) ), 0.0 ) * w;
	}

	return sum;
}

//What a ray from a probe brings back: the light leaving what it hits towards the probe, nought where it leaves
float3 Arriving( RaytracingAccelerationStructure scene, float3 from, float3 way, inout uint seed, out bool inside )
{
	inside = false;

	RayDesc ray;
	ray.Origin = from;
	ray.Direction = way;
	ray.TMin = 0.0;
	ray.TMax = 1000.0;

	RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
	query.TraceRayInline( scene, RAY_FLAG_NONE, 0xFF, ray );
	query.Proceed();

	if ( query.CommittedStatus() != COMMITTED_TRIANGLE_HIT ) return 0.0;

	//The back of something: the probe is inside it
	if ( !query.CommittedTriangleFrontFace() )
	{
		inside = true;
		return 0.0;
	}

	float3 corners[3] = query.CommittedGetIntersectionTriangleVertexPositions();
	float3x4 toWorld = query.CommittedObjectToWorld3x4();

	float3 a = mul( toWorld, float4( corners[0], 1.0 ) );
	float3 b = mul( toWorld, float4( corners[1], 1.0 ) );
	float3 c = mul( toWorld, float4( corners[2], 1.0 ) );

	float3 n = normalize( cross( b - a, c - a ) );
	if ( dot( n, way ) > 0.0 ) n = -n;

	float3 at = from + ( way * query.CommittedRayT() );
	float3 lifted = at + ( n * 0.01 );

	uint record = RecordsAt + ( query.CommittedInstanceID() * 2u );
	float3 albedo = g_params[record].rgb;
	float3 glow = g_params[record + 1u].rgb;

	//Over pi, all of it: the sun square on, each lamp, and the last bounce
	float3 lit = 0.0;

	float3 sun = SunTowards();
	float sunFacing = dot( n, sun );

	if ( sunFacing > 0.0 && any( SunColour() > 0.0 ) && !Blocked( scene, lifted, sun, 10000.0 ) )
		lit += SunColour() * ( sunFacing / Pi );

	for ( uint i = 0u; i < min( LampCount(), MostLamps ); ++i ) lit += Lamp( scene, i, lifted, n );


	lit += Bounced( lifted, n );

	return ( albedo * lit ) + glow;
}

uint2 Halves( float4 v )
{
	return uint2( f32tof16( v.x ) | ( f32tof16( v.y ) << 16u ), f32tof16( v.z ) | ( f32tof16( v.w ) << 16u ) );
}

float Luma( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

[numthreads(64, 1, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_count ) return;

	uint count = ProbeCount();
	uint probe = ( g_first + id.x ) % count;

	uint3 counts = Counts();
	uint3 at3 = uint3( probe % counts.x, ( probe / counts.x ) % counts.y, probe / ( counts.x * counts.y ) );
	float3 at = Origin() + ( float3( at3 ) * Step() );

	RaytracingAccelerationStructure scene = Scene();
	uint seed = ( probe * 9781u ) ^ ( g_turn * 6271u );
	float turn = Random( seed ) * 2.0 * Pi;

	uint rays = Rays();
	float3 mean = 0.0;
	float3 leanR = 0.0, leanG = 0.0, leanB = 0.0;
	uint insides = 0u;

	for ( uint i = 0u; i < rays; ++i )
	{
		float3 way = Spread( i, rays, turn );
		bool inside;
		float3 light = Arriving( scene, at, way, seed, inside );

		if ( inside ) insides++;

		mean += light;
		leanR += way * light.r;
		leanG += way * light.g;
		leanB += way * light.b;
	}

	float share = 1.0 / float( rays );

	//As BakeVolume's: the mean, and twice the mean along each way; a probe inside a wall keeps what it had
	float4 r = float4( mean.r * share, leanR * ( 2.0 * share ) );
	float4 g = float4( mean.g * share, leanG * ( 2.0 * share ) );
	float4 b = float4( mean.b * share, leanB * ( 2.0 * share ) );

	if ( insides * 4u < rays )
	{
		float keep = Keep();
		g_state[probe * 3u] = lerp( r, g_state[probe * 3u], keep );
		g_state[( probe * 3u ) + 1u] = lerp( g, g_state[( probe * 3u ) + 1u], keep );
		g_state[( probe * 3u ) + 2u] = lerp( b, g_state[( probe * 3u ) + 2u], keep );
	}

	r = g_state[probe * 3u];
	g = g_state[( probe * 3u ) + 1u];
	b = g_state[( probe * 3u ) + 2u];

	//One lean for all three colours, the brightness's, over the constant - as CoreLiveBounce writes it
	float3 colour = max( float3( r.x, g.x, b.x ), 0.0 );
	float bright = Luma( colour );
	float3 along = ( r.yzw * 0.2126 ) + ( g.yzw * 0.7152 ) + ( b.yzw * 0.0722 );
	float3 tilt = bright > 1e-6 ? along / bright : 0.0;
	float tilted = length( tilt );
	if ( tilted > 1.0 ) tilt /= tilted;

	g_out[probe] = Halves( float4( colour, 1.0 ) );
	g_out[count + probe] = Halves( float4( tilt, 0.0 ) );
}
