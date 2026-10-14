//The 2D light worked out from its scene (CoreLight2D.Solve), all in the scene picture's own pixels:
//  SeedCs, JumpCs    a jump flood over what blocks, so every texel knows its nearest blocker and rays can stride
//  CascadeCs         one radiance cascade of the global light and what glows: its rays marched over their stretch,
//                    merged with the cascade above
//  IntegrateCs       the finest cascade's four rays a probe averaged, and every 2D light added straight - faded to
//                    its range, shaped by its cone, shadowed by a march to it
//
//Cascade n: probes every 2^n pixels, 4^(n+1) rays each laid out as a 2^(n+1) square block, each ray covering the
//stretch from t_n to t_(n+1) of its direction - t_n = Interval0 (4^n - 1) / 3. What a ray does not hit it takes from
//the four nearest probes of the cascade above along the four rays that split its own; past the last, nothing

[[vk::push_constant]]
cbuffer Push
{
	//The scene's width and height xy; the jump's stride or the cascade's number z; how many cascades w
	float4 g_size;

	//Table indices: the scene x, the nearest blockers z, and what is read from the step before w
	float4 g_pictures;

	//The global light rgb, the first cascade's stretch w
	float4 g_sky;

	//How far the flood reaches x - a texel it gave no blocker has none nearer; one where anything glows and the
	//cascades ran y
	float4 g_extra;
};

#include "frame.slang"

#ifdef LIGHT2D_SEEDS
[[vk::binding(0,2)]] [format("rgba32f")] RWTexture2D<float4> g_out;
#else
[[vk::binding(0,2)]] [format("rgba16f")] RWTexture2D<float4> g_out;
#endif

//Where there is no blocker at all
static const float2 NoSeed = float2( -1.0e6, -1.0e6 );

float4 Read( float index, int2 at ) { return g_textures[uint( index )].Load( int3( at, 0 ) ); }

int2 SceneSize() { return int2( g_size.xy ); }

bool Inside( int2 at ) { return all( at >= 0 ) && all( at < SceneSize() ); }

//Whether a texel of the scene blocks light
bool Solid( int2 at ) { return Read( g_pictures.x, at ).a > 0.5; }

#ifdef LIGHT2D_SEEDS

//Every blocking texel is its own nearest blocker
[numthreads(8, 8, 1)]
void SeedCs( uint3 id : SV_DispatchThreadID )
{
	int2 at = int2( id.xy );
	if ( !Inside( at ) ) return;

	g_out[at] = float4( Solid( at ) ? float2( at ) + 0.5 : NoSeed, 0.0, 0.0 );
}

//One round of the flood: the nearest of the blockers its neighbours a stride away know of
[numthreads(8, 8, 1)]
void JumpCs( uint3 id : SV_DispatchThreadID )
{
	int2 at = int2( id.xy );
	if ( !Inside( at ) ) return;

	int stride = int( g_size.z );
	float2 here = float2( at ) + 0.5;

	float2 best = NoSeed;
	float nearest = 1.0e12;

	for ( int y = -1; y <= 1; y++ )
	{
		for ( int x = -1; x <= 1; x++ )
		{
			int2 from = at + ( int2( x, y ) * stride );
			if ( !Inside( from ) ) continue;

			float2 seed = Read( g_pictures.w, from ).xy;
			if ( seed.x < 0.0 ) continue;

			float2 apart = seed - here;
			float distance = dot( apart, apart );

			if ( distance < nearest )
			{
				nearest = distance;
				best = seed;
			}
		}
	}

	g_out[at] = float4( best, 0.0, 0.0 );
}

#else

static const float Pi = 3.14159265;

//How many strides a cascade's ray may take over its stretch, and a light's shadow ray over its whole way. Neither
//strides less than its way over a share of these, so one skimming along a surface - where the nearest blocker is
//always a pixel off - still gets to its end, rather than running out of strides short of what it would have hit
static const int MostSteps = 64;
static const int MostShadowSteps = 256;
static const float LeastStrideShare = 48.0;
static const float LeastShadowStrideShare = 192.0;

//How far into a blocker the light of its lit face goes at least, in pixels, however little Penetration Depth asks for
static const float LeastPenetration = 1.5;

//Whether occluders shadow themselves - see Lighting2D.SelfShadow
bool SelfShadow() { return g_frame[0].light2dInfo.y > 0.5; }

//How far from here the nearest blocking texel's edge is, at least - a ray can stride this far without passing one
float Clearance( int2 at, float2 position )
{
	float2 seed = Read( g_pictures.z, at ).xy;
	if ( seed.x < 0.0 ) return g_extra.x;

	return length( seed - position ) - 0.71;
}

//One ray over its stretch: what it hit in rgb, and in a whether it went through untouched. A ray of the first
//cascade that starts inside a blocker looks out of it first, so the blocker's own face takes what reaches it
float4 March( float2 origin, float2 direction, float start, float end, bool fromInside )
{
	float t = start;
	bool leaving = fromInside;
	float least = max( 0.5, ( end - start ) / LeastStrideShare );

	for ( int steps = 0; steps < MostSteps && t < end; steps++ )
	{
		float2 position = origin + ( direction * t );
		int2 at = int2( floor( position ) );

		//Off the picture: nothing more to meet this way
		if ( !Inside( at ) ) return float4( 0.0, 0.0, 0.0, 1.0 );

		float4 scene = Read( g_pictures.x, at );

		if ( scene.a > 0.5 )
		{
			if ( !leaving ) return float4( scene.rgb, 0.0 );

			t += 0.5;
			continue;
		}

		leaving = false;
		t += max( Clearance( at, position ), least );
	}

	return float4( 0.0, 0.0, 0.0, 1.0 );
}

//How many probes a cascade has along each side, and how wide each probe's block of rays is
int2 ProbesOf( int cascade ) { return ( SceneSize() + ( 1 << cascade ) - 1 ) >> cascade; }
int BlockOf( int cascade ) { return 2 << cascade; }

//The ray of the cascade above, at a probe of its, in a direction of its
float4 Above( int2 probe, int direction, int cascade )
{
	int block = BlockOf( cascade );
	int2 texel = ( probe * block ) + int2( direction % block, direction / block );

	return Read( g_pictures.w, texel );
}

//The cascade above, along the four of its rays that split this one, between its four probes nearest here
float4 MergeAbove( float2 centre, int direction, int cascade )
{
	int upper = cascade + 1;
	int2 probes = ProbesOf( upper );

	float2 grid = ( centre / float( 1 << upper ) ) - 0.5;
	int2 corner = int2( floor( grid ) );
	float2 f = grid - float2( corner );

	float4 sum = float4( 0.0, 0.0, 0.0, 0.0 );

	for ( int y = 0; y <= 1; y++ )
	{
		for ( int x = 0; x <= 1; x++ )
		{
			int2 probe = clamp( corner + int2( x, y ), int2( 0, 0 ), probes - 1 );
			float weight = ( x == 0 ? 1.0 - f.x : f.x ) * ( y == 0 ? 1.0 - f.y : f.y );

			float4 rays = float4( 0.0, 0.0, 0.0, 0.0 );

			for ( int k = 0; k < 4; k++ )
				rays += Above( probe, ( direction * 4 ) + k, upper );

			sum += rays * ( weight * 0.25 );
		}
	}

	return sum;
}

[numthreads(8, 8, 1)]
void CascadeCs( uint3 id : SV_DispatchThreadID )
{
	int cascade = int( g_size.z );
	int cascades = int( g_size.w );

	int block = BlockOf( cascade );
	int2 probes = ProbesOf( cascade );

	int2 texel = int2( id.xy );
	int2 probe = texel / block;

	if ( any( probe >= probes ) ) return;

	int2 within = texel - ( probe * block );
	int direction = within.x + ( within.y * block );
	int rays = block * block;

	float angle = ( float( direction ) + 0.5 ) * ( 2.0 * Pi / float( rays ) );
	float2 toward = float2( cos( angle ), sin( angle ) );

	float spacing = float( 1 << cascade );
	float2 centre = ( float2( probe ) + 0.5 ) * spacing;

	float interval = g_sky.w;
	float start = interval * ( float( ( 1 << ( 2 * cascade ) ) - 1 ) / 3.0 );
	float end = interval * ( float( ( 1 << ( 2 * ( cascade + 1 ) ) ) - 1 ) / 3.0 );

	bool fromInside = cascade == 0 && Inside( int2( centre ) ) && Solid( int2( centre ) );

	float4 light = March( centre, toward, start, end, fromInside );

	if ( light.a > 0.0 )
	{
		float4 beyond = cascade + 1 < cascades ? MergeAbove( centre, direction, cascade ) : float4( 0.0, 0.0, 0.0, 1.0 );

		light.rgb += light.a * beyond.rgb;
		light.a *= beyond.a;
	}

	g_out[texel] = light;
}

//A point in the world as a pixel of the scene picture: through the view, shrunk into the margin
float2 PixelOf( float3 world )
{
	float4 clip = mul( float4( world, 1.0 ), g_frame[0].worldToClip );
	float2 ndc = ( clip.xy / clip.w ) * g_frame[0].light2dScene.x;

	return ( ( ndc * 0.5 ) + 0.5 ) * g_size.xy;
}

//How far from here the nearest blocking texel's edge is, as near as the flood can say - for how close a shadow ray
//passes a blocker, where Clearance's caution would read every pass within a pixel as touching it
float Nearest( int2 at, float2 position )
{
	float2 seed = Read( g_pictures.z, at ).xy;
	if ( seed.x < 0.0 ) return g_extra.x;

	return length( seed - position ) - 0.5;
}

//How much of a light reaches here past the blockers: a march to it, softened by how close the ray passes a blocker
//against how big the light looks from there. A ray from inside a blocker - where occluders shadow themselves - walks
//out of it, the light fading the deeper in it started until none is left at the penetration depth; the softening is
//measured from where it came out, ignoring the first pixel: right beside the surface it left, that surface is not in
//its way
float Shadow( float2 from, float2 light, float distance, float source, bool solid, float penetration )
{
	float2 toward = ( light - from ) / max( distance, 1e-4 );

	bool leaving = solid;
	float lit = 1.0;
	float depth = 1.0;
	float t = 0.0;
	float outside = 0.0;
	float least = max( 1.0, distance / LeastShadowStrideShare );
	float through = max( 0.5, penetration / 32.0 );

	for ( int steps = 0; steps < MostShadowSteps && t < distance - 0.5; steps++ )
	{
		float2 position = from + ( toward * t );
		int2 at = int2( floor( position ) );

		if ( !Inside( at ) ) break;

		if ( Solid( at ) )
		{
			if ( leaving && t < penetration )
			{
				t += through;
				continue;
			}

			return 0.0;
		}

		if ( leaving )
		{
			leaving = false;
			outside = t;
			depth = 1.0 - smoothstep( penetration * 0.5, penetration, t );
		}

		float along = t - outside;

		if ( along > 1.0 )
			lit = min( lit, ( Nearest( at, position ) * ( distance - outside ) ) / ( source * along ) );

		t += max( Clearance( at, position ), least );
	}

	return smoothstep( 0.0, 1.0, saturate( lit ) ) * depth;
}

//Every 2D light's share here - see lights2d in frame_data.slang
float3 Lights( float2 here, bool solid )
{
	float3 sum = float3( 0.0, 0.0, 0.0 );
	int count = int( g_frame[0].light2dInfo.x );

	for ( int i = 0; i < count; i++ )
	{
		float4 where = g_frame[0].lights2d[i * 3];
		float4 colour = g_frame[0].lights2d[( i * 3 ) + 1];
		float4 cone = g_frame[0].lights2d[( i * 3 ) + 2];

		float2 light = PixelOf( where.xyz );

		//Pixels a world unit is, along the plane's x: the view keeps them square
		float unit = length( PixelOf( where.xyz + float3( 1.0, 0.0, 0.0 ) ) - light );

		float range = where.w * unit;
		float2 apart = here - light;
		float distance = length( apart );

		if ( distance >= range ) continue;

		float x = distance / range;
		float fade = ( 1.0 - ( x * x ) ) * ( 1.0 - ( x * x ) );

		//A spot's cone, measured in the picture: its direction through the view
		if ( cone.z < Pi - 1e-3 && distance > 1e-3 )
		{
			float2 pointing = normalize( PixelOf( where.xyz + float3( cone.xy, 0.0 ) ) - light );
			float angle = acos( clamp( dot( apart / distance, pointing ), -1.0, 1.0 ) );
			float edge = cone.z * ( 1.0 - saturate( cone.w ) );

			fade *= 1.0 - smoothstep( edge, max( cone.z, edge + 1e-4 ), angle );
		}

		if ( fade <= 0.0 ) continue;

		//A blocker that does not shadow itself is shadowed by no blocker: terrain does not shadow terrain - two
		//blocks of tiles are two blockers to a march, and the step below one was left in the other's shadow. Its
		//shadow falls on what is not a blocker: the background, the characters standing in it
		float penetration = max( g_frame[0].light2dInfo.z * unit, LeastPenetration );
		float shadow = solid && !SelfShadow() ? 1.0 : Shadow( here, light, distance, max( colour.w * unit, 0.5 ), solid, penetration );

		sum += colour.rgb * fade * shadow;
	}

	return sum;
}

//The light at a pixel: what the cascades gathered there - the global light and what glows - and the 2D lights.
//A blocker that does not shadow itself takes the global light whole, as it takes the lights unshadowed by itself
[numthreads(8, 8, 1)]
void IntegrateCs( uint3 id : SV_DispatchThreadID )
{
	int2 at = int2( id.xy );
	if ( !Inside( at ) ) return;

	bool solid = Solid( at );

	//The global light is flat ambient, the same everywhere and blocked by nothing - a level walled in all round
	//got none of it as light from beyond the walls, and its shadows were black. The cascades carry what glows,
	//and did not run where nothing does
	float3 gathered = g_sky.rgb;

	if ( g_extra.y > 0.5 && ( !solid || SelfShadow() ) )
	{
		for ( int y = 0; y < 2; y++ )
			for ( int x = 0; x < 2; x++ )
				gathered += Read( g_pictures.w, ( at * 2 ) + int2( x, y ) ).rgb * 0.25;
	}

	g_out[at] = float4( gathered + Lights( float2( at ) + 0.5, solid ), 1.0 );
}

#endif
