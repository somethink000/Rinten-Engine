//The 2D light worked out from its scene (CoreLight2D.Solve), all in the scene picture's own pixels:
//  JumpCs            a jump flood over what blocks, at half size, so every texel knows how near its nearest blocker
//                    is and rays can stride
//  CascadeCs         one radiance cascade of the global light and what glows: its rays marched over their stretch,
//                    merged with the cascade above
//  ShadowsCs         each 2D light's shadow map: how far it sees past no blocker, a row of angles about it
//  IntegrateCs       the finest cascade gathered, and every 2D light added straight - faded to its range, shaped by
//                    its cone, shadowed by its map
//
//Cascade n: probes every S 2^n pixels, S the first cascade's spacing, 4^(n+1) rays each, each covering the stretch from
//t_n to t_(n+1) of its direction - t_n = Interval0 S (4^n - 1) / 3. What a ray does not hit it takes from the four
//nearest probes of the cascade above along the four rays that split its own; past the last, nothing. Kept pre-averaged:
//a probe's rays four at a time, the four the cascade below merges as one, laid out as a 2^n square block - a quarter of
//the picture and of the reads. The lights' pass reads the first cascade, one average a probe, between its four nearest

[[vk::push_constant]]
cbuffer Push
{
	//The scene's width and height xy; the jump's stride or the cascade's number z; how many cascades w
	float4 g_size;

	//Table indices: the scene x, the nearest blockers z, and what is read from the step before w; the first cascade's
	//probe spacing in pixels y
	float4 g_pictures;

	//The global light rgb, the first cascade's stretch w
	float4 g_sky;

	//How far the flood reaches x - a texel it gave no blocker has none nearer; one where anything glows and the
	//cascades ran y; the lights' shadow maps' table index z; one in the flood's first round w
	float4 g_extra;
};

#include "frame.slang"

//Four lanes written whatever the picture holds; its format keeps what it has
#if defined( LIGHT2D_SEEDS )
[[vk::binding(0,2)]] [format("rg16f")] RWTexture2D<float4> g_out;
#elif defined( LIGHT2D_SHADOWS )
[[vk::binding(0,2)]] [format("r32f")] RWTexture2D<float4> g_out;
#else
[[vk::binding(0,2)]] [format("rgba16f")] RWTexture2D<float4> g_out;
#endif

//The flood works on two by two blocks of the scene's texels, a block blocking where any of its texels does. A block's
//nearest blocking block is kept as the way to it from its middle, in blocks, in half floats: exact for the near ones a
//ray strides by, and the flood looks no further than g_extra.x pixels. Where there is none, this
static const float2 NoSeed = float2( 60000.0, 60000.0 );

bool Seeded( float2 offset ) { return offset.x < 30000.0; }

float4 Read( float index, int2 at ) { return g_textures[uint( index )].Load( int3( at, 0 ) ); }

int2 SceneSize() { return int2( g_size.xy ); }

bool Inside( int2 at ) { return all( at >= 0 ) && all( at < SceneSize() ); }

//Whether a texel of the scene blocks light
bool Solid( int2 at ) { return Read( g_pictures.x, at ).a > 0.5; }

//The flood's blocks along each side
int2 BlocksSize() { return ( SceneSize() + 1 ) / 2; }

bool InsideBlocks( int2 at ) { return all( at >= 0 ) && all( at < BlocksSize() ); }

#ifdef LIGHT2D_SEEDS

//Whether any texel of a block blocks light
bool SolidBlock( int2 block )
{
	int2 corner = block * 2;
	bool solid = false;

	for ( int i = 0; i < 4; i++ )
	{
		int2 at = corner + int2( i & 1, i >> 1 );
		solid = solid || ( Inside( at ) && Solid( at ) );
	}

	return solid;
}

//What a block knows of its nearest blocker: in the first round, whether it is one itself, every blocker its own seed
float2 Known( int2 block )
{
	if ( g_extra.w > 0.5 ) return SolidBlock( block ) ? float2( 0.0, 0.0 ) : NoSeed;

	return Read( g_pictures.w, block ).xy;
}

//One round of the flood: the nearest of the blockers its neighbours a stride away know of
[numthreads(8, 8, 1)]
void JumpCs( uint3 id : SV_DispatchThreadID )
{
	int2 at = int2( id.xy );
	if ( !InsideBlocks( at ) ) return;

	int stride = int( g_size.z );

	float2 best = NoSeed;
	float nearest = 1.0e12;

	for ( int y = -1; y <= 1; y++ )
	{
		for ( int x = -1; x <= 1; x++ )
		{
			int2 step = int2( x, y ) * stride;
			if ( !InsideBlocks( at + step ) ) continue;

			float2 offset = Known( at + step );
			if ( !Seeded( offset ) ) continue;

			float2 apart = offset + float2( step );
			float distance = dot( apart, apart );

			if ( distance < nearest )
			{
				nearest = distance;
				best = apart;
			}
		}
	}

	g_out[at] = float4( best, 0.0, 0.0 );
}

#else

static const float Pi = 3.14159265;

//How many strides a cascade's ray may take over its stretch. None strides less than its stretch over a share of this,
//so one skimming along a surface - where the nearest blocker is always a pixel off - still gets to its end, rather than
//running out of strides short of what it would have hit
static const int MostSteps = 64;
static const float LeastStrideShare = 48.0;

//How far into a blocker the light of its lit face goes at least, in pixels, however little Penetration Depth asks for
static const float LeastPenetration = 1.5;

//Whether occluders shadow themselves - see Lighting2D.SelfShadow
bool SelfShadow() { return g_frame[0].light2dInfo.y > 0.5; }

//How far from a point in the texel at the nearest blocking texel is, at least - a ray can stride this far without
//passing one. In blocks: to the nearest blocking block's middle, less its half diagonal, then in pixels
float Clearance( int2 at, float2 position )
{
	float2 offset = Read( g_pictures.z, at / 2 ).xy;
	if ( !Seeded( offset ) ) return g_extra.x;

	float2 apart = float2( at / 2 ) + 0.5 + offset - ( position * 0.5 );

	return ( length( apart ) - 0.71 ) * 2.0;
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

//How far apart a cascade's probes stand in pixels, how many it has along each side, and each probe's block of rays' width
int SpacingOf( int cascade ) { return int( g_pictures.y ) << cascade; }
int2 ProbesOf( int cascade ) { return ( SceneSize() + SpacingOf( cascade ) - 1 ) / SpacingOf( cascade ); }
int BlockOf( int cascade ) { return 1 << cascade; }

//The four rays of a cascade, averaged, at a probe of its, that split a direction of the cascade below
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

	float2 grid = ( centre / float( SpacingOf( upper ) ) ) - 0.5;
	int2 corner = int2( floor( grid ) );
	float2 f = grid - float2( corner );

	float4 sum = float4( 0.0, 0.0, 0.0, 0.0 );

	for ( int y = 0; y <= 1; y++ )
	{
		for ( int x = 0; x <= 1; x++ )
		{
			int2 probe = clamp( corner + int2( x, y ), int2( 0, 0 ), probes - 1 );
			float weight = ( x == 0 ? 1.0 - f.x : f.x ) * ( y == 0 ? 1.0 - f.y : f.y );

			sum += Above( probe, direction, upper ) * weight;
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
	int group = within.x + ( within.y * block );
	int rays = 4 * block * block;

	float2 centre = ( float2( probe ) + 0.5 ) * float( SpacingOf( cascade ) );

	float interval = g_sky.w;
	float start = interval * ( float( ( 1 << ( 2 * cascade ) ) - 1 ) / 3.0 );
	float end = interval * ( float( ( 1 << ( 2 * ( cascade + 1 ) ) ) - 1 ) / 3.0 );

	bool fromInside = cascade == 0 && Inside( int2( centre ) ) && Solid( int2( centre ) );

	float4 average = float4( 0.0, 0.0, 0.0, 0.0 );

	for ( int k = 0; k < 4; k++ )
	{
		int direction = ( group * 4 ) + k;
		float angle = ( float( direction ) + 0.5 ) * ( 2.0 * Pi / float( rays ) );

		float4 light = March( centre, float2( cos( angle ), sin( angle ) ), start, end, fromInside );

		if ( light.a > 0.0 )
		{
			float4 beyond = cascade + 1 < cascades ? MergeAbove( centre, direction, cascade ) : float4( 0.0, 0.0, 0.0, 1.0 );

			light.rgb += light.a * beyond.rgb;
			light.a *= beyond.a;
		}

		average += light * 0.25;
	}

	g_out[texel] = average;
}

//A point in the world as a pixel of the scene picture: through the view, shrunk into the margin
float2 PixelOf( float3 world )
{
	float4 clip = mul( float4( world, 1.0 ), g_frame[0].worldToClip );
	float2 ndc = ( ( clip.xy / clip.w ) * g_frame[0].light2dScene.xz ) + g_frame[0].light2dSnap.xy;

	return ( ( ndc * 0.5 ) + 0.5 ) * g_size.xy;
}

//The first cascade at a pixel: its four nearest probes' averages, weighed by how near each is
float3 Gathered( float2 here )
{
	int2 probes = ProbesOf( 0 );
	float2 grid = ( here / float( SpacingOf( 0 ) ) ) - 0.5;
	int2 corner = int2( floor( grid ) );
	float2 f = grid - float2( corner );

	float3 sum = float3( 0.0, 0.0, 0.0 );

	for ( int y = 0; y <= 1; y++ )
	{
		for ( int x = 0; x <= 1; x++ )
		{
			int2 probe = clamp( corner + int2( x, y ), int2( 0, 0 ), probes - 1 );
			float weight = ( x == 0 ? 1.0 - f.x : f.x ) * ( y == 0 ? 1.0 - f.y : f.y );

			sum += Read( g_pictures.w, probe ).rgb * weight;
		}
	}

	return sum;
}

//Angles a light's shadow map has about it - see CoreLight2D.ShadowAngles
static const int ShadowAngles = 4096;

//How many strides a shadow map's ray may take to the light's range
static const int MostShadowSteps = 256;

//Angles read each side of a point's way to a light, looking for the nearest blocked one - see Shadow
static const int ShadowSearch = 6;

//One 2D light as the scene picture sees it: where it stands, how far it reaches and how big it is, in its pixels, and
//its colour and cone - see lights2d in frame_data.slang
struct Light2D
{
	float2 position;
	float range;
	float source;
	float unit;
	float4 colour;
	float4 cone;
};

Light2D LightOf( int i )
{
	float4 where = g_frame[0].lights2d[i * 3];

	Light2D light;
	light.position = PixelOf( where.xyz );
	light.colour = g_frame[0].lights2d[( i * 3 ) + 1];
	light.cone = g_frame[0].lights2d[( i * 3 ) + 2];

	//Pixels a world unit is, along the plane's x: the view keeps them square
	light.unit = length( PixelOf( where.xyz + float3( 1.0, 0.0, 0.0 ) ) - light.position );
	light.range = where.w * light.unit;
	light.source = max( light.colour.w * light.unit, 0.5 );

	return light;
}

//How far along a ray from origin it enters a texel's square - the way into the blocker it met, not the stride that
//found it: a stride lands anywhere up to a pixel in, and as the light moved the shadow's edge jumped by that much
float EntryInto( int2 at, float2 origin, float2 toward, float found )
{
	float2 low = float2( at );
	float2 high = low + 1.0;

	float across = abs( toward.x ) > 1e-6 ? ( ( toward.x > 0.0 ? low.x : high.x ) - origin.x ) / toward.x : -1.0e9;
	float down = abs( toward.y ) > 1e-6 ? ( ( toward.y > 0.0 ? low.y : high.y ) - origin.y ) / toward.y : -1.0e9;

	return clamp( max( across, down ), 0.0, found );
}

//One light's row of its shadow map: along each of its angles, how far from it the first blocker is, or its range
[numthreads(8, 8, 1)]
void ShadowsCs( uint3 id : SV_DispatchThreadID )
{
	int angle = int( id.x );
	int i = int( id.y );

	if ( angle >= ShadowAngles || i >= int( g_frame[0].light2dInfo.x ) ) return;

	Light2D light = LightOf( i );

	float turn = ( float( angle ) + 0.5 ) * ( 2.0 * Pi / float( ShadowAngles ) );
	float2 toward = float2( cos( turn ), sin( turn ) );

	float t = 0.0;
	float seen = light.range;

	for ( int steps = 0; steps < MostShadowSteps && t < light.range; steps++ )
	{
		float2 position = light.position + ( toward * t );
		int2 at = int2( floor( position ) );

		if ( !Inside( at ) ) break;

		if ( Solid( at ) )
		{
			seen = EntryInto( at, light.position, toward, t );
			break;
		}

		t += max( Clearance( at, position ), 0.5 );
	}

	g_out[int2( angle, i )] = float4( seen, 0.0, 0.0, 0.0 );
}

//How far light i sees along an angle, between the map's two nearest
float Seen( int i, float turn )
{
	float place = ( turn / ( 2.0 * Pi ) * float( ShadowAngles ) ) - 0.5;
	float corner = floor( place );
	int a = int( corner );

	float near = Read( g_extra.z, int2( ( a + ShadowAngles ) % ShadowAngles, i ) ).x;
	float far = Read( g_extra.z, int2( ( a + 1 + ShadowAngles ) % ShadowAngles, i ) ).x;

	return lerp( near, far, place - corner );
}

//Whether light i reaches this far this way, between the map's two nearest angles: each one's answer, blended. Their
//distances blended were neither at a shadow's edge - a near blocker and the light's range made a distance between
//that met no blocker - and as the light moved and one ray turned from missing a corner to hitting it, the edge jumped
//by a ray's width. Blended answers slide an edge from one ray to the next as the light moves
float Visible( int i, float turn, float distance )
{
	float place = ( turn / ( 2.0 * Pi ) * float( ShadowAngles ) ) - 0.5;
	float corner = floor( place );
	int a = int( corner );

	float near = Read( g_extra.z, int2( ( a + ShadowAngles ) % ShadowAngles, i ) ).x;
	float far = Read( g_extra.z, int2( ( a + 1 + ShadowAngles ) % ShadowAngles, i ) ).x;

	return lerp( near + 0.5 >= distance ? 1.0 : 0.0, far + 0.5 >= distance ? 1.0 : 0.0, place - corner );
}

//How much of light i reaches a point this far from it this way, from its map. None where the way to the light's middle
//is blocked; beside a shadow, faded by how far round the nearest blocked angle is against the width of the penumbra
//that blocker throws here - the light's size times how far past it the point lies - so a shadow is sharp where it
//starts and softens as it falls, outward from its edge as a cone traced to the light softens it. A blocker's own
//texels, where occluders shadow themselves, take its light as far in as the penetration, fading on the way
float Shadow( int i, float2 apart, float distance, float source, bool solid, float penetration )
{
	float turn = atan2( apart.y, apart.x );
	if ( turn < 0.0 ) turn += 2.0 * Pi;

	float past = distance - Seen( i, turn );

	if ( solid ) return 1.0 - smoothstep( penetration * 0.5, penetration, past );

	float open = Visible( i, turn, distance );
	if ( open <= 0.0 ) return 0.0;

	//Out as far round as a penumbra here could reach: a blocker a pixel from the light throws the widest
	float texel = 2.0 * Pi / float( ShadowAngles );
	float reach = clamp( source / max( distance, 1.0 ), texel, 0.25 );
	float lit = 1.0;

	for ( int k = 1; k <= ShadowSearch; k++ )
	{
		float round = reach * ( float( k ) / float( ShadowSearch ) );

		for ( int side = -1; side <= 1; side += 2 )
		{
			float seen = Seen( i, turn + ( round * float( side ) ) );
			if ( seen + 0.5 >= distance ) continue;

			float width = source * ( distance - seen ) / max( seen * distance, 1e-3 );
			lit = min( lit, round / max( width, texel ) );
		}
	}

	return smoothstep( 0.0, 1.0, saturate( lit ) ) * open;
}

//Every 2D light's share here - see lights2d in frame_data.slang
float3 Lights( float2 here, bool solid )
{
	float3 sum = float3( 0.0, 0.0, 0.0 );
	int count = int( g_frame[0].light2dInfo.x );

	for ( int i = 0; i < count; i++ )
	{
		Light2D light = LightOf( i );

		float2 apart = here - light.position;
		float distance = length( apart );

		if ( distance >= light.range ) continue;

		float x = distance / light.range;
		float fade = ( 1.0 - ( x * x ) ) * ( 1.0 - ( x * x ) );

		//A spot's cone, measured in the picture: its direction through the view
		if ( light.cone.z < Pi - 1e-3 && distance > 1e-3 )
		{
			float2 pointing = normalize( PixelOf( g_frame[0].lights2d[i * 3].xyz + float3( light.cone.xy, 0.0 ) ) - light.position );
			float angle = acos( clamp( dot( apart / distance, pointing ), -1.0, 1.0 ) );
			float edge = light.cone.z * ( 1.0 - saturate( light.cone.w ) );

			fade *= 1.0 - smoothstep( edge, max( light.cone.z, edge + 1e-4 ), angle );
		}

		if ( fade <= 0.0 ) continue;

		//A blocker that does not shadow itself is shadowed by no blocker: terrain does not shadow terrain - two
		//blocks of tiles are two blockers to a march, and the step below one was left in the other's shadow. Its
		//shadow falls on what is not a blocker: the background, the characters standing in it
		float penetration = max( g_frame[0].light2dInfo.z * light.unit, LeastPenetration );
		float shadow = solid && !SelfShadow() ? 1.0 : Shadow( i, apart, distance, light.source, solid, penetration );

		sum += light.colour.rgb * fade * shadow;
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

	float2 here = float2( at ) + 0.5;

	if ( g_extra.y > 0.5 && ( !solid || SelfShadow() ) ) gathered += Gathered( here );

	g_out[at] = float4( gathered + Lights( here, solid ), 1.0 );
}

#endif
