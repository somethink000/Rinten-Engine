//GTAO from depth: occlusion plus bent normal (MainPs), then a 4x4 blur (SoftenPs); and the light the same horizons
//hide, out of last frame's picture (IndirectPs) - what the occlusion takes away, given back from what is really there

FEATURES
{
}

PARAMETERS
{
	//How far round a pixel to look, in metres.
	float g_flRadius < Attribute( "Radius" ); Default( 0.5 ); >;

	//How many slices of the hemisphere, and how many reads a side along each.
	float g_flSlices < Attribute( "Slices" ); Default( 2.0 ); >;
	float g_flSteps < Attribute( "Steps" ); Default( 4.0 ); >;

	//What the pass before SoftenPs wrote, by its place in the table - the same size as the picture it writes.
	float g_flSource < Attribute( "Source" ); >;

	//How far apart SoftenPs reads: one, then two, then four texels.
	float g_flReach < Attribute( "Reach" ); Default( 1.0 ); >;

	//How much of the occlusion is kept, from none to all of it - SoftenPs's last run.
	float g_flStrength < Attribute( "Strength" ); Default( 1.0 ); >;

	//Last frame's lit world, small, by its place in the table, for IndirectPs - see AmbientOcclusion.history
	float g_flLast < Attribute( "Last" ); >;

	//The indirect light as it was accumulated last frame, by its place in the table, for TemporalIndirectPs
	float g_flBefore < Attribute( "Before" ); >;
}

#include "postprocess.slang"

//Where a pixel of the depth is, in the world.
float3 PlaceOf( float2 uv, float depth )
{
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / max( world.w, 0.000001 );
}

//The eye's rays a metre ahead: to the first corner, and what a screen's width and height add - see Place
struct Rays
{
	float3 eye;
	float3 corner;
	float3 across;
	float3 down;
};

Rays RaysOf()
{
	Rays r;
	r.eye = EYE_FRAME( camera ).xyz;

	float3 ahead = normalize( PlaceOf( float2( 0.5, 0.5 ), 1.0 ) - r.eye );
	float3 c00 = PlaceOf( float2( 0.0, 0.0 ), 1.0 ) - r.eye;
	float3 c10 = PlaceOf( float2( 1.0, 0.0 ), 1.0 ) - r.eye;
	float3 c01 = PlaceOf( float2( 0.0, 1.0 ), 1.0 ) - r.eye;

	c00 /= dot( c00, ahead );
	c10 /= dot( c10, ahead );
	c01 /= dot( c01, ahead );

	r.corner = c00;
	r.across = c10 - c00;
	r.down = c01 - c00;

	return r;
}

//A point of the screen at a depth as written, in the world: its ray, as long as the depth made linear.
float3 Place( Rays r, float2 uv, float raw )
{
	float near = g_vFrameRange.x;
	float far = g_vFrameRange.y;
	float ahead = near * far / max( far - raw * ( far - near ), 1e-6 );

	return r.eye + ( ( r.corner + ( r.across * uv.x ) + ( r.down * uv.y ) ) * ahead );
}

static const float Pi = 3.14159265;

float Acos( float x )
{
	//Eberly's, good to a few ten-thousandths, a third of the instructions.
	float a = abs( x );
	float r = ( -0.156583 * a + 1.570796 ) * sqrt( 1.0 - a );
	return x >= 0.0 ? r : Pi - r;
}

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	float raw = SceneDepthRaw( i.uv );

	//Nothing was drawn here: the sky is not occluded by anything.
	if ( raw >= 1.0 ) return float4( 1.0, 0.5, 0.5, 0.5 );

	Rays rays = RaysOf();
	float3 place = Place( rays, i.uv, raw );

	//The surface's own plane, from how the reconstructed position moves across one pixel; facing the eye.
	float3 view = normalize( rays.eye - place );
	float3 normal = normalize( cross( ddx( place ), ddy( place ) ) );
	if ( dot( normal, view ) < 0.0 ) normal = -normal;

	//Texels a metre spans at this depth: the ray's width per screen at a metre, times the distance ahead
	float2 size = g_vFrameSize.xy;
	float ahead = dot( place - rays.eye, normalize( rays.corner + ( rays.across + rays.down ) * 0.5 ) );
	float perTexel = length( rays.across ) * max( ahead, 1e-3 ) / size.x;
	float radius = g_flRadius;
	float reach = clamp( radius / perTexel, 2.0, 256.0 );

	//This pixel's turn of the slices and start along them.
	uint2 cell = uint2( i.position.xy ) & 3u;
	float turn = ( float( cell.x + ( cell.y * 4u ) ) + 0.5 ) / 16.0;
	float jitter = frac( ( float( ( cell.x * 3u + cell.y * 2u ) & 3u ) + 0.5 ) * 0.25 + turn * 0.5 );

	int slices = clamp( int( g_flSlices ), 1, 4 );
	int steps = clamp( int( g_flSteps ), 1, 8 );

	float visibility = 0.0;
	float3 bent = 0.0;

	for ( int s = 0; s < slices; s++ )
	{
		float angle = ( float( s ) + turn ) * ( Pi / float( slices ) );
		float2 screen = float2( cos( angle ), sin( angle ) );

		//That way on the screen, in the world, at the pixel: how the point moves as its texel does.
		float3 along = normalize( ( rays.across * ( screen.x / size.x ) ) + ( rays.down * ( screen.y / size.y ) ) );

		//The slice through the eye's ray and that way; the normal laid into it, and its angle from the eye's ray
		float3 across = along - ( view * dot( along, view ) );
		float3 axis = normalize( cross( across, view ) );
		float3 laid = normal - ( axis * dot( normal, axis ) );
		float laidLength = length( laid );

		float n = sign( dot( across, laid ) ) * Acos( saturate( dot( laid, view ) / max( laidLength, 1e-5 ) ) );

		//The highest thing each side, as the cosine of its angle from the eye's ray, from the surface's own plane
		float horizon0 = cos( n - ( Pi * 0.5 ) );
		float horizon1 = cos( n + ( Pi * 0.5 ) );
		float floor0 = horizon0;
		float floor1 = horizon1;

		for ( int k = 0; k < steps; k++ )
		{
			//Denser near the pixel, where the detail is.
			float t = ( float( k ) + jitter ) / float( steps );
			float2 offset = screen * max( t * t * reach, 1.0 + float( k ) ) / size;

			for ( int side = 0; side < 2; side++ )
			{
				float2 uv = side == 0 ? i.uv - offset : i.uv + offset;
				if ( any( uv < 0.0 ) || any( uv > 1.0 ) ) continue;

				float3 sample = Place( rays, uv, SceneDepthRaw( uv ) ) - place;
				float distance = length( sample );
				float cosine = dot( sample / max( distance, 1e-5 ), view );

				//Past the radius counts less, past half again not at all: a far wall is not this corner's ceiling
				float falloff = saturate( ( ( radius * 1.5 ) - distance ) / ( radius * 0.5 ) );

				if ( side == 0 ) horizon0 = max( horizon0, lerp( floor0, cosine, falloff ) );
				else horizon1 = max( horizon1, lerp( floor1, cosine, falloff ) );
			}
		}

		//The two horizons as angles, kept inside the surface's own hemisphere.
		float h0 = n + max( -Acos( horizon0 ) - n, -Pi * 0.5 );
		float h1 = n + min( Acos( horizon1 ) - n, Pi * 0.5 );

		float sinN = sin( n );
		float cosN = cos( n );

		visibility += laidLength * 0.25 * (
			( cosN + ( 2.0 * h0 * sinN ) - cos( ( 2.0 * h0 ) - n ) ) +
			( cosN + ( 2.0 * h1 * sinN ) - cos( ( 2.0 * h1 ) - n ) ) );

		//The mean direction the slice sees, cosine-weighted - Jimenez's closed form
		float t0 = ( ( 6.0 * sin( h0 - n ) ) - sin( ( 3.0 * h0 ) - n ) + ( 6.0 * sin( h1 - n ) ) - sin( ( 3.0 * h1 ) - n ) +
			( 16.0 * sinN ) - ( 3.0 * ( sin( h0 + n ) + sin( h1 + n ) ) ) ) / 12.0;
		float t1 = ( -cos( ( 3.0 * h0 ) - n ) - cos( ( 3.0 * h1 ) - n ) + ( 8.0 * cosN ) - ( 3.0 * ( cos( h0 + n ) + cos( h1 + n ) ) ) ) / 12.0;

		bent += ( ( normalize( across ) * t0 ) + ( view * t1 ) ) * laidLength;
	}

	visibility = saturate( visibility / float( slices ) );

	float bentLength = length( bent );
	bent = bentLength > 1e-5 ? bent / bentLength : normal;

	//The bent normal made nought to one for the eight bits it is kept in.
	return float4( visibility, ( bent * 0.5 ) + 0.5 );
}

//4x4 average in four bilinear taps; bent normal left unnormalized, its length is confidence
[shader("fragment")]
float4 SoftenPs( PostV2F i ) : SV_Target0
{
	float reach = max( g_flReach, 1.0 );

	//Always on a corner between texels: half a texel either side of the reach.
	float2 near = ( -0.5 - reach ) * g_vFrameSize.zw;
	float2 far = ( reach - 0.5 ) * g_vFrameSize.zw;

	float4 sum =
		FramePicture( g_flSource, i.uv + float2( near.x, near.y ) ) +
		FramePicture( g_flSource, i.uv + float2( far.x, near.y ) ) +
		FramePicture( g_flSource, i.uv + float2( near.x, far.y ) ) +
		FramePicture( g_flSource, i.uv + float2( far.x, far.y ) );

	sum *= 0.25;

	float strength = saturate( g_flStrength );

	return float4( lerp( 1.0, sum.r, strength ), lerp( 0.5.xxx, sum.gba, strength ) );
}

//The cosine-weighted share of a slice's side seen above a horizon at angle h - GTAO's integral, one side of it
float SideSeen( float h, float n, float sinN, float cosN )
{
	return 0.25 * ( cosN + ( 2.0 * h * sinN ) - cos( ( 2.0 * h ) - n ) );
}

//Screen-space indirect light: along GTAO's slices, every sample that raises a horizon adds last frame's light where it
//stands, reprojected, times the share of the hemisphere it newly hides - irradiance over pi, as the bake keeps it
[shader("fragment")]
float4 IndirectPs( PostV2F i ) : SV_Target0
{
	float raw = SceneDepthRaw( i.uv );
	if ( raw >= 1.0 || g_flLast < 0.5 ) return float4( 0.0, 0.0, 0.0, 1.0 );

	Rays rays = RaysOf();
	float3 place = Place( rays, i.uv, raw );

	float3 view = normalize( rays.eye - place );
	float3 normal = normalize( cross( ddx( place ), ddy( place ) ) );
	if ( dot( normal, view ) < 0.0 ) normal = -normal;

	float2 size = g_vFrameSize.xy;
	float ahead = dot( place - rays.eye, normalize( rays.corner + ( rays.across + rays.down ) * 0.5 ) );
	float perTexel = length( rays.across ) * max( ahead, 1e-3 ) / size.x;
	float radius = g_flRadius;
	float reach = clamp( radius / perTexel, 2.0, 128.0 );

	uint2 cell = uint2( i.position.xy ) & 3u;
	float turn = ( float( cell.x + ( cell.y * 4u ) ) + 0.5 ) / 16.0;
	float jitter = frac( ( float( ( cell.x * 3u + cell.y * 2u ) & 3u ) + 0.5 ) * 0.25 + turn * 0.5 );

	int slices = clamp( int( g_flSlices ), 1, 4 );
	int steps = clamp( int( g_flSteps ), 1, 8 );

	float3 light = 0.0;

	for ( int s = 0; s < slices; s++ )
	{
		float angle = ( float( s ) + turn ) * ( Pi / float( slices ) );
		float2 screen = float2( cos( angle ), sin( angle ) );

		float3 along = normalize( ( rays.across * ( screen.x / size.x ) ) + ( rays.down * ( screen.y / size.y ) ) );
		float3 across = along - ( view * dot( along, view ) );
		float3 axis = normalize( cross( across, view ) );
		float3 laid = normal - ( axis * dot( normal, axis ) );
		float laidLength = length( laid );

		float n = sign( dot( across, laid ) ) * Acos( saturate( dot( laid, view ) / max( laidLength, 1e-5 ) ) );
		float sinN = sin( n );
		float cosN = cos( n );

		//Each side's horizon as an angle from the eye's ray, starting on the surface's own plane
		float2 horizon = float2( n - ( Pi * 0.5 ), n + ( Pi * 0.5 ) );

		for ( int k = 0; k < steps; k++ )
		{
			float t = ( float( k ) + jitter ) / float( steps );
			float2 offset = screen * max( t * t * reach, 1.0 + float( k ) ) / size;

			for ( int side = 0; side < 2; side++ )
			{
				float2 uv = side == 0 ? i.uv - offset : i.uv + offset;
				if ( any( uv < 0.0 ) || any( uv > 1.0 ) ) continue;

				float3 there = Place( rays, uv, SceneDepthRaw( uv ) );
				float3 sample = there - place;
				float distance = length( sample );
				if ( distance > radius ) continue;

				float cosine = dot( sample / max( distance, 1e-5 ), view );
				float h = side == 0 ? n + max( -Acos( cosine ) - n, -Pi * 0.5 ) : n + min( Acos( cosine ) - n, Pi * 0.5 );

				//Only what rises over the horizon so far hides more of the sky, and only that much gives light
				float was = side == 0 ? horizon.x : horizon.y;
				bool higher = side == 0 ? h > was : h < was;
				if ( !higher ) continue;

				float share = abs( SideSeen( was, n, sinN, cosN ) - SideSeen( h, n, sinN, cosN ) ) * laidLength;

				if ( side == 0 ) horizon.x = h;
				else horizon.y = h;

				//Where that point was last frame, and what light it gave then
				float4 before = mul( float4( there, 1.0 ), EYE_FRAME( previousWorldToClip ) );
				if ( before.w <= 1e-4 ) continue;

				float2 was2 = ( ( before.xy / before.w ) * 0.5 ) + 0.5;
				if ( any( was2 < 0.0 ) || any( was2 > 1.0 ) ) continue;

				light += FramePicture( g_flLast, was2 ).rgb * share;
			}
		}
	}

	return float4( light / float( slices ), 1.0 );
}

//The indirect light's 4x4 average in four bilinear taps, Reach texels apart
[shader("fragment")]
float4 SoftenIndirectPs( PostV2F i ) : SV_Target0
{
	float reach = max( g_flReach, 1.0 );
	float2 near = ( -0.5 - reach ) * g_vFrameSize.zw;
	float2 far = ( reach - 0.5 ) * g_vFrameSize.zw;

	return 0.25 * (
		FramePicture( g_flSource, i.uv + float2( near.x, near.y ) ) +
		FramePicture( g_flSource, i.uv + float2( far.x, near.y ) ) +
		FramePicture( g_flSource, i.uv + float2( near.x, far.y ) ) +
		FramePicture( g_flSource, i.uv + float2( far.x, far.y ) ) );
}

//This frame's indirect light over last frame's where that point was, a fifth new each frame; the old held inside what
//this frame's neighbours span, so what moved or was uncovered does not trail
[shader("fragment")]
float4 TemporalIndirectPs( PostV2F i ) : SV_Target0
{
	float4 now = FramePicture( g_flSource, i.uv );
	float raw = SceneDepthRaw( i.uv );

	if ( g_flBefore < 0.5 || raw >= 1.0 ) return now;

	float4 before = mul( float4( PlaceOf( i.uv, raw ), 1.0 ), EYE_FRAME( previousWorldToClip ) );
	if ( before.w <= 1e-4 ) return now;

	float2 was = ( ( before.xy / before.w ) * 0.5 ) + 0.5;
	if ( any( was < 0.0 ) || any( was > 1.0 ) ) return now;

	float2 texel = g_vFrameSize.zw;
	float4 a = FramePicture( g_flSource, i.uv + float2( -texel.x, 0.0 ) );
	float4 b = FramePicture( g_flSource, i.uv + float2( texel.x, 0.0 ) );
	float4 c = FramePicture( g_flSource, i.uv + float2( 0.0, -texel.y ) );
	float4 d = FramePicture( g_flSource, i.uv + float2( 0.0, texel.y ) );

	float4 least = min( min( min( a, b ), min( c, d ) ), now );
	float4 most = max( max( max( a, b ), max( c, d ) ), now );

	float4 old = clamp( FramePicture( g_flBefore, was ), least, most );

	return lerp( old, now, 0.2 );
}

//A picture as it is, into a target that outlives the frame - the indirect light's history
[shader("fragment")]
float4 KeepPs( PostV2F i ) : SV_Target0
{
	return FramePicture( g_flSource, i.uv );
}

//The lit world, as it is after the opaque, into the small picture next frame's IndirectPs reads
[shader("fragment")]
float4 CopyPs( PostV2F i ) : SV_Target0
{
	return float4( SceneColour( i.uv ), 1.0 );
}
