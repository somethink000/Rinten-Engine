//The light near a surface by hardware rays (CoreRayLight, CoreRays): what each ray hits within reach, lit as last frame's
//picture saw it where the screen holds it, else by its colour, the frame's lights and the bake - and the share that got out

FEATURES
{
}

PARAMETERS
{
	//Last frame's lit world, by its place in the table - see CoreRayLight
	float g_flLast < Attribute( "Last" ); >;

	//How far a ray looks, in metres: past it, the sky and the bake are what light the surface
	float g_flLength < Attribute( "Length" ); Default( 4.0 ); >;

	//Rays a pixel a frame, and this frame's number, which turns them
	float g_flCount < Attribute( "Count" ); Default( 2.0 ); >;
	float g_flTurn < Attribute( "Turn" ); >;

	//What the pass before SoftenPs, TemporalPs or KeepPs wrote, by its place in the table
	float g_flSource < Attribute( "Source" ); >;

	//How far apart SoftenPs reads, in texels
	float g_flReach < Attribute( "Reach" ); Default( 1.0 ); >;

	//The light as kept last frame, by its place in the table, for TemporalPs
	float g_flBefore < Attribute( "Before" ); >;
}

#include "postprocess.slang"

static const float Pi = 3.14159265;

//Where a pixel of the depth is, in the world
float3 PlaceOf( float2 uv, float raw )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, raw, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

//A small hash, a float in [0, 1)
float Random( inout uint seed )
{
	seed = ( seed * 747796405u ) + 2891336453u;
	uint word = ( ( seed >> ( ( seed >> 28u ) + 4u ) ) ^ seed ) * 277803737u;
	return float( ( word >> 22u ) ^ word ) * ( 1.0 / 4294967296.0 );
}

//A direction around n, as likely as its cosine
float3 CosineAround( float3 n, float u, float v )
{
	float3 up = abs( n.y ) < 0.999 ? float3( 0.0, 1.0, 0.0 ) : float3( 1.0, 0.0, 0.0 );
	float3 right = normalize( cross( up, n ) );
	float3 ahead = cross( n, right );

	float r = sqrt( u );
	float a = 2.0 * Pi * v;

	return normalize( ( right * ( r * cos( a ) ) ) + ( ahead * ( r * sin( a ) ) ) + ( n * sqrt( max( 1.0 - u, 0.0 ) ) ) );
}

#include "ray_hit.slang"

//Cosine-spread rays from the surface: rgb what those that hit brought back, irradiance over pi as the bake keeps it,
//and a the share that got out - see screenLight in surface_shading.slang
[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
#ifdef PORTABLE
	return float4( 0.0, 0.0, 0.0, 1.0 );
#else
	float raw = SceneDepthRaw( i.uv );
	if ( raw >= 1.0 || g_frame[0].rays.z < 0.5 ) return float4( 0.0, 0.0, 0.0, 1.0 );

	float3 place = PlaceOf( i.uv, raw );
	float3 eye = EYE_FRAME( camera ).xyz;
	float3 view = normalize( eye - place );

	//The surface's plane from the full depth's neighbours, each side the one nearer, so an edge does not bend it
	float2 texel = 1.0 / max( g_frame[0].settings.yz, float2( 1.0, 1.0 ) );
	float3 right = PlaceOf( i.uv + float2( texel.x, 0.0 ), SceneDepthRaw( i.uv + float2( texel.x, 0.0 ) ) ) - place;
	float3 left = place - PlaceOf( i.uv - float2( texel.x, 0.0 ), SceneDepthRaw( i.uv - float2( texel.x, 0.0 ) ) );
	float3 down = PlaceOf( i.uv + float2( 0.0, texel.y ), SceneDepthRaw( i.uv + float2( 0.0, texel.y ) ) ) - place;
	float3 up = place - PlaceOf( i.uv - float2( 0.0, texel.y ), SceneDepthRaw( i.uv - float2( 0.0, texel.y ) ) );

	float3 across = dot( right, right ) < dot( left, left ) ? right : left;
	float3 along = dot( down, down ) < dot( up, up ) ? down : up;

	float3 normal = normalize( cross( across, along ) );
	if ( dot( normal, view ) < 0.0 ) normal = -normal;

	RaytracingAccelerationStructure scene = RayScene();

	uint2 pixel = uint2( i.position.xy );
	uint seed = ( pixel.x * 1973u ) ^ ( pixel.y * 9277u );
	float2 offset = float2( Random( seed ), Random( seed ) );

	float3 from = place + ( normal * ( 0.01 + ( 0.002 * length( place - eye ) ) ) );
	uint count = clamp( uint( g_flCount ), 1u, 8u );

	float3 light = 0.0;
	float outside = 0.0;

	for ( uint r = 0u; r < count; ++r )
	{
		//The R2 sequence over the frames from where this pixel starts on it, so each frame fills the gaps the last left
		float step = float( ( ( uint( g_flTurn ) % 4096u ) * count ) + r );
		float2 spot = frac( offset + ( step * float2( 0.7548776662, 0.5698402910 ) ) );
		float3 way = CosineAround( normal, spot.x, spot.y );

		RayDesc ray;
		ray.Origin = from;
		ray.Direction = way;
		ray.TMin = 0.0;
		ray.TMax = g_flLength;

		RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
		query.TraceRayInline( scene, RAY_FLAG_NONE, 0xFF, ray );
		query.Proceed();

		if ( query.CommittedStatus() != COMMITTED_TRIANGLE_HIT )
		{
			outside += 1.0;
			continue;
		}

		//The back of something: inside it, nothing comes this way
		if ( !query.CommittedTriangleFrontFace() ) continue;

		float3 corners[3] = query.CommittedGetIntersectionTriangleVertexPositions();
		float3x4 toWorld = query.CommittedObjectToWorld3x4();

		float3 a = mul( toWorld, float4( corners[0], 1.0 ) );
		float3 b = mul( toWorld, float4( corners[1], 1.0 ) );
		float3 c = mul( toWorld, float4( corners[2], 1.0 ) );

		float3 n = normalize( cross( b - a, c - a ) );
		if ( dot( n, way ) > 0.0 ) n = -n;

		light += HitLight( scene, from + ( way * query.CommittedRayT() ), n, query.CommittedInstanceID(),
			g_flLast > 0.5 ? g_flLast : -1.0 );
	}

	return float4( light / float( count ), outside / float( count ) );
#endif
}

//A 3x3 of the traced light Reach texels apart, each weighed by how far its depth is from what the slope here says it
//should be, so an edge stays one and a wall seen edge-on still softens
[shader("fragment")]
float4 SoftenPs( PostV2F i ) : SV_Target0
{
	static const float Kernel[3] = { 0.25, 0.5, 0.25 };

	float centre = SceneDepth( i.uv );
	float reach = max( g_flReach, 1.0 );
	float2 step = g_vFrameSize.zw * reach;
	float slope = max( abs( ddx( centre ) ), abs( ddy( centre ) ) ) * reach;

	float4 sum = 0.0;
	float total = 0.0;

	for ( int y = -1; y <= 1; ++y )
	{
		for ( int x = -1; x <= 1; ++x )
		{
			float2 uv = i.uv + ( float2( x, y ) * step );
			if ( any( uv < 0.0 ) || any( uv > 1.0 ) ) continue;

			float expected = ( slope * float( abs( x ) + abs( y ) ) * 1.5 ) + ( centre * 0.01 ) + 1e-3;
			float apart = abs( SceneDepth( uv ) - centre ) / expected;
			float w = Kernel[x + 1] * Kernel[y + 1] * exp( -apart );

			sum += FramePicture( g_flSource, uv ) * w;
			total += w;
		}
	}

	return total > 1e-6 ? sum / total : FramePicture( g_flSource, i.uv );
}

//This frame's light over the kept where that point was last frame, a tenth new, in the kept's own texels
[shader("fragment")]
float4 TemporalPs( PostV2F i ) : SV_Target0
{
	float4 now = FramePicture( g_flSource, i.uv );
	float raw = SceneDepthRaw( i.uv );

	float2 was;
	if ( g_flBefore < 0.5 || raw >= 1.0 || !ScreenOf( PlaceOf( i.uv, raw ), EYE_FRAME( previousWorldToClip ), was ) ) return now;

	return lerp( FramePicture( g_flBefore, was ), now, 0.1 );
}

//A picture as it is, into the target that keeps it to the next frame
[shader("fragment")]
float4 KeepPs( PostV2F i ) : SV_Target0
{
	return FramePicture( g_flSource, i.uv );
}

//The lit world after the opaque, into the picture next frame's hits read
[shader("fragment")]
float4 CopyPs( PostV2F i ) : SV_Target0
{
	return float4( SceneColour( i.uv ), 1.0 );
}
