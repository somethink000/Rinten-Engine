//A scene's light with no bake, by hardware rays (CoreProbeGrid): probes about the camera lit by what their rays hit,
//kept as a running mean with the way to the walls along each axis, so a surface reads only probes that see it - DDGI

//Header, lamps, sky, cascades and every instance's colours - see CoreProbeGrid.Parameters
[[vk::binding(0,0)]] StructuredBuffer<float4> g_params;

//Each probe as it is kept: red, green, blue constant and lean, the mean way to walls along +x -x +y -y, then +z -z,
//the share of rays that found the inside of something, and the cell the slot holds
[[vk::binding(1,0)]] RWStructuredBuffer<float4> g_state;

//Each probe as the frame's four pictures want it, in halves: colour, lean, walls along xy, walls along z
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

//Float4s a probe keeps - see g_state
static const uint StateWords = 6u;

//Where the parts of the parameters are, in float4s - CoreProbeGrid writes the same
static const uint HeaderAt = 0u;
static const uint LampsAt = 6u;
static const uint MostLamps = 32u;
static const uint SkyAt = LampsAt + ( MostLamps * 3u );
static const uint CascadesAt = SkyAt + 6u;
static const uint MostCascades = 2u;
static const uint RecordsAt = CascadesAt + ( MostCascades * 2u );

uint2 Address() { return asuint( g_params[HeaderAt].xy ); }
uint ProbeCount() { return asuint( g_params[HeaderAt].z ); }
uint LampCount() { return asuint( g_params[HeaderAt + 1u].x ); }
uint CascadeCount() { return asuint( g_params[HeaderAt + 1u].y ); }
float3 SunTowards() { return g_params[HeaderAt + 3u].xyz; }
float3 SunColour() { return g_params[HeaderAt + 4u].rgb; }
float Keep() { return g_params[HeaderAt + 4u].w; }

//A cascade's first lattice cell xyz and the step between probes w, then its probes along each axis
float4 CascadePlace( uint c ) { return g_params[CascadesAt + ( c * 2u )]; }
uint3 CascadeCounts( uint c ) { return asuint( g_params[CascadesAt + ( c * 2u ) + 1u].xyz ); }
uint CascadeFirst( uint c ) { return asuint( g_params[CascadesAt + ( c * 2u ) + 1u].w ); }

#include "engine/ray_common.slang"

//The sky's light from a way, as six colours along the axes blended by the way's squares - see CoreProbeGrid.SkyOf
float3 Sky( float3 way )
{
	float3 w = way * way;

	return ( w.x * g_params[SkyAt + ( way.x >= 0.0 ? 0u : 1u )].rgb )
		+ ( w.y * g_params[SkyAt + ( way.y >= 0.0 ? 2u : 3u )].rgb )
		+ ( w.z * g_params[SkyAt + ( way.z >= 0.0 ? 4u : 5u )].rgb );
}

//Where a lattice cell is kept, wrapping each axis, so a cascade follows the camera without moving its probes
uint SlotOf( uint c, int3 cell )
{
	uint3 counts = CascadeCounts( c );
	int3 n = int3( counts );
	uint3 wrapped = uint3( ( ( cell % n ) + n ) % n );

	return CascadeFirst( c ) + wrapped.x + ( counts.x * ( wrapped.y + ( counts.y * wrapped.z ) ) );
}

//The last bounce at a point on a surface facing n, over pi: the finest cascade holding it, its eight nearest probes
//lerped; the sky past them all
float3 Bounced( float3 at, float3 n )
{
	for ( uint c = 0u; c < CascadeCount(); ++c )
	{
		float4 place = CascadePlace( c );
		uint3 counts = CascadeCounts( c );
		float3 local = ( at / place.w ) - place.xyz;

		if ( any( local < 0.0 ) || any( local > float3( counts ) - 1.0 ) ) continue;

		int3 low = int3( min( floor( local ), float3( counts ) - 2.0 ) );
		float3 f = local - float3( low );
		int3 first = int3( place.xyz );

		float3 sum = 0.0;

		[unroll]
		for ( uint k = 0u; k < 8u; ++k )
		{
			int3 corner = low + int3( k & 1u, ( k >> 1u ) & 1u, ( k >> 2u ) & 1u );
			float3 w3 = lerp( 1.0 - f, f, float3( corner - low ) );
			uint probe = SlotOf( c, first + corner ) * StateWords;
			float4 facing = float4( 1.0, n );

			sum += max( float3( dot( g_state[probe], facing ), dot( g_state[probe + 1u], facing ), dot( g_state[probe + 2u], facing ) ), 0.0 )
				* ( w3.x * w3.y * w3.z );
		}

		return sum;
	}

	//Past every cascade: the sky as a surface facing n has it
	return ( Sky( n ) * 0.5 ) + ( ( Sky( float3( 1.0, 0.0, 0.0 ) ) + Sky( float3( -1.0, 0.0, 0.0 ) ) + Sky( float3( 0.0, 1.0, 0.0 ) )
		+ Sky( float3( 0.0, -1.0, 0.0 ) ) + Sky( float3( 0.0, 0.0, 1.0 ) ) + Sky( float3( 0.0, 0.0, -1.0 ) ) ) / 12.0 );
}

//What a ray from a probe brings back, and how far it went; inside where it met the back of something
float3 Arriving( RaytracingAccelerationStructure scene, float3 from, float3 way, float far, out float reached, out bool inside )
{
	inside = false;
	reached = far;

	RayDesc ray;
	ray.Origin = from;
	ray.Direction = way;
	ray.TMin = 0.0;
	ray.TMax = far;

	RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
	query.TraceRayInline( scene, RAY_FLAG_NONE, 0xFF, ray );
	query.Proceed();

	if ( query.CommittedStatus() != COMMITTED_TRIANGLE_HIT ) return Sky( way );

	reached = query.CommittedRayT();

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

	float3 lifted = from + ( way * reached ) + ( n * 0.01 );

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

//Rays a probe casts an update: a group a probe, a thread a ray - CoreProbeGrid.RaysPerProbe says the same
static const uint GroupRays = 64u;

//Each ray's share of the sums, then halved down to the group's in place: light and insides, the light's lean in each
//colour, how far it went along the four ways of x and y and how much it leant along them, then the same along z
static const uint Sums = 7u;
groupshared float4 s_sums[Sums][GroupRays];

[numthreads(64, 1, 1)]
void main( uint3 group : SV_GroupID, uint3 lane : SV_GroupThreadID )
{
	if ( group.x >= g_count ) return;

	uint count = ProbeCount();
	uint slot = ( g_first + group.x ) % count;
	uint i = lane.x;

	//Which cascade the slot is in, and the cell of the cascade's lattice it holds now
	uint c = 0u;
	while ( c + 1u < CascadeCount() && slot >= CascadeFirst( c + 1u ) ) c++;

	float4 place = CascadePlace( c );
	uint3 counts = CascadeCounts( c );
	uint within = slot - CascadeFirst( c );
	int3 wrapped = int3( within % counts.x, ( within / counts.x ) % counts.y, within / ( counts.x * counts.y ) );
	int3 first = int3( place.xyz );
	int3 n = int3( counts );
	int3 cell = first + ( ( ( wrapped - ( first % n ) ) % n ) + n ) % n;

	float3 at = float3( cell ) * place.w;
	uint state = slot * StateWords;

	//A slot handed to another cell as the camera moved starts over
	float4 held = g_state[state + 5u];
	bool fresh = any( int3( held.xyz ) != cell ) || held.w < 0.5;

	//A probe most of whose rays found the inside of something lights nothing a surface reads, so it is looked at again
	//only one turn in eight, as RTXGI turns such probes off - the group leaves together, before any barrier
	if ( !fresh && g_state[state + 4u].z > 0.5 && ( ( g_turn + slot ) & 7u ) != 0u ) return;

	//The same turn for every ray of the probe, so they spread over the sphere together
	uint seed = ( slot * 9781u ) ^ ( g_turn * 6271u );
	float turn = Random( seed ) * 2.0 * Pi;
	float far = place.w * float( max( counts.x, max( counts.y, counts.z ) ) );

	float3 way = Spread( i, GroupRays, turn );
	float reached;
	bool inside;
	float3 light = Arriving( Scene(), at, way, far, reached, inside );

	//How far it went, towards the axes it leans along
	float3 w = way * way;
	float4 xy = float4( way.x > 0.0 ? w.x : 0.0, way.x < 0.0 ? w.x : 0.0, way.y > 0.0 ? w.y : 0.0, way.y < 0.0 ? w.y : 0.0 );
	float2 z = float2( way.z > 0.0 ? w.z : 0.0, way.z < 0.0 ? w.z : 0.0 );

	s_sums[0][i] = float4( light, inside ? 1.0 : 0.0 );
	s_sums[1][i] = float4( way * light.r, 0.0 );
	s_sums[2][i] = float4( way * light.g, 0.0 );
	s_sums[3][i] = float4( way * light.b, 0.0 );
	s_sums[4][i] = xy * reached;
	s_sums[5][i] = xy;
	s_sums[6][i] = float4( z * reached, z );

	GroupMemoryBarrierWithGroupSync();

	[unroll]
	for ( uint apart = GroupRays / 2u; apart > 0u; apart /= 2u )
	{
		if ( i < apart )
		{
			[unroll]
			for ( uint k = 0u; k < Sums; ++k ) s_sums[k][i] += s_sums[k][i + apart];
		}

		GroupMemoryBarrierWithGroupSync();
	}

	if ( i != 0u ) return;

	float share = 1.0 / float( GroupRays );

	float3 mean = s_sums[0][0].rgb;

	//As BakeVolume's: the mean, and twice the mean along each way
	float4 r = float4( mean.r * share, s_sums[1][0].xyz * ( 2.0 * share ) );
	float4 g = float4( mean.g * share, s_sums[2][0].xyz * ( 2.0 * share ) );
	float4 b = float4( mean.b * share, s_sums[3][0].xyz * ( 2.0 * share ) );
	float4 walls = s_sums[4][0] / max( s_sums[5][0], 1e-4 );
	float4 wallsMore = float4( s_sums[6][0].xy / max( s_sums[6][0].zw, 1e-4 ), s_sums[0][0].w * share, 0.0 );

	float keep = fresh ? 0.0 : Keep();

	g_state[state] = lerp( r, g_state[state], keep );
	g_state[state + 1u] = lerp( g, g_state[state + 1u], keep );
	g_state[state + 2u] = lerp( b, g_state[state + 2u], keep );
	g_state[state + 3u] = lerp( walls, g_state[state + 3u], keep );
	g_state[state + 4u] = lerp( wallsMore, g_state[state + 4u], keep );
	g_state[state + 5u] = float4( float3( cell ), 1.0 );

	r = g_state[state];
	g = g_state[state + 1u];
	b = g_state[state + 2u];

	//One lean for all three colours, the brightness's, over the constant - as CoreLiveBounce writes it
	float3 colour = max( float3( r.x, g.x, b.x ), 0.0 );
	float bright = Luma( colour );
	float3 along = ( r.yzw * 0.2126 ) + ( g.yzw * 0.7152 ) + ( b.yzw * 0.0722 );
	float3 tilt = bright > 1e-6 ? along / bright : 0.0;
	float tilted = length( tilt );
	if ( tilted > 1.0 ) tilt /= tilted;

	g_out[slot] = Halves( float4( colour, 1.0 ) );
	g_out[count + slot] = Halves( float4( tilt, 0.0 ) );
	g_out[( count * 2u ) + slot] = Halves( g_state[state + 3u] );
	g_out[( count * 3u ) + slot] = Halves( float4( g_state[state + 4u].xyz, 0.0 ) );
}
