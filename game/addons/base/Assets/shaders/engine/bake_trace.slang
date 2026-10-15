//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//GPU lightmap bake rays, matching BakeSheet.Trace; per-texel sample sums are resolved on the CPU

[[vk::binding(0,0)]] ByteAddressBuffer g_world;
[[vk::binding(1,0)]] StructuredBuffer<float4> g_texels;
[[vk::binding(2,0)]] RWStructuredBuffer<float4> g_sums;

//The last pass's light over pi a texel of the sheet, which a gathering pass reads where its rays land - see BakeSheet
[[vk::binding(3,0)]] StructuredBuffer<float4> g_cache;

//The cache's lit triangles as lights (BakeSheet.CacheLights): each world triangle's chance per square metre, then the
//picked ones, CacheLightFloats each - triangle, power summed so far, a corner and two edges
[[vk::binding(4,0)]] StructuredBuffer<float> g_lights;

[[vk::push_constant]]
cbuffer Push
{
	uint g_first;       //First texel of this dispatch
	uint g_count;       //How many
	uint g_samples;     //Samples each takes this dispatch
	uint g_sampleBase;  //Samples taken before, for the random numbers
	uint g_seed;
	uint g_across;      //The volume's strata along each side of the sphere - see ProbeCs
	uint g_mode;        //What a texel's samples do - the Mode flags; nought traces whole paths
	uint g_cacheLights; //Lit triangles of the cache a gather samples as lights; nought for none
	uint g_triangles;   //The world's triangles, where g_lights' list starts

};

static const float Pi = 3.14159265;
static const float InvPi = 1.0 / Pi;
static const float Lift = 0.002;

//---- the world's layout - see BakeGpu.Pack ------------------------------------

uint Word( uint at ) { return g_world.Load( at * 4 ); }
float Float( uint at ) { return asfloat( g_world.Load( at * 4 ) ); }
float3 Float3( uint at ) { return asfloat( g_world.Load3( at * 4 ) ); }
float4 Float4( uint at ) { return asfloat( g_world.Load4( at * 4 ) ); }

uint NodesAt() { return Word( 0 ); }
uint TreeAt() { return Word( 1 ); }        //Per tree triangle: corner, edge, edge (9) and the world's triangle | tested (1)
uint CornersAt() { return Word( 2 ); }     //Per world triangle: three vertex indices
uint FacingAt() { return Word( 3 ); }      //Per world triangle: its face's normal (3)
uint TriangleAt() { return Word( 4 ); }    //Per world triangle: material | through mode << 28, then tint (3)
uint NormalsAt() { return Word( 5 ); }     //Per vertex (3)
uint UvsAt() { return Word( 6 ); }         //Per vertex (2)
uint ColoursAt() { return Word( 7 ); }     //Per vertex (3)
uint MaterialsAt() { return Word( 8 ); }   //MaterialWords each
uint PicturesAt() { return Word( 9 ); }    //Four floats a texel
uint LightsAt() { return Word( 10 ); }     //LightWords each
uint LightCount() { return Word( 11 ); }
uint SkyAt() { return Word( 12 ); }        //Octahedral map of the sky, three floats a texel
uint SkySide() { return Word( 13 ); }
float SunCosine() { return Float( 14 ); }
uint Bounces() { return Word( 15 ); }
float AlbedoBoost() { return Float( 16 ); }
uint NodeCount() { return Word( 17 ); }
uint EmittersAt() { return Word( 18 ); }   //Per glow: world triangle, power so far, corner, two edges
uint EmitterCount() { return Word( 19 ); }
uint GlowAt() { return Word( 20 ); }       //Per world triangle: TowardsGlow's chance per square metre
uint Pass() { return Word( 21 ); }         //Nought the main bake, else the glow group baked alone
uint GroupsAt() { return Word( 22 ); }     //Per world triangle: its glow group; no table where none has one
uint AtlasAt() { return Word( 23 ); }      //Per vertex: its spot on the sheet in [0,1], negative off it; none at 0
uint AtlasSide() { return Word( 24 ); }
uint MirrorsAt() { return Word( 25 ); }    //Per mirror: its normal, offset, reflectance (3) and whether it is water or glass
uint MirrorCount() { return Word( 26 ); }
uint MirrorOfAt() { return Word( 27 ); }   //Per world triangle: its mirror, or all ones

//A pass's sampling: a ray reads the light where it lands from g_cache rather than tracing on (a final gather, as
//Lightmass's), the sky counts where a ray leaves, every lamp's straight light or none rather than the baked ones'
static const uint ModeGather = 1u;
static const uint ModeSky = 2u;
static const uint ModeAllLights = 4u;
static const uint ModeNoDirect = 8u;

//Whether a triangle's glow is in this pass - BakeScene.Surface
bool GlowsNow( uint t ) { return ( GroupsAt() == 0u ? 0u : Word( GroupsAt() + t ) ) == Pass(); }

static const uint MaterialWords = 28;
static const uint LightWords = 24;
static const uint EmitterWords = 12;

//---- random numbers ------------------------------------------------------------

uint Hash( uint v )
{
	uint state = v * 747796405u + 2891336453u;
	uint word = ( ( state >> ( ( state >> 28u ) + 4u ) ) ^ state ) * 277803737u;
	return ( word >> 22u ) ^ word;
}

struct Random
{
	uint state;

	[mutating] float Next()
	{
		state = Hash( state );
		return float( state >> 8 ) * ( 1.0 / 16777216.0 );
	}
};

//---- BakeMath ------------------------------------------------------------------

float Luma( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

void Basis( float3 n, out float3 right, out float3 up )
{
	float s = n.z >= 0.0 ? 1.0 : -1.0;
	float a = -1.0 / ( s + n.z );
	float b = n.x * n.y * a;

	right = float3( 1.0 + ( s * n.x * n.x * a ), s * b, -s * n.x );
	up = float3( b, s + ( n.y * n.y * a ), -n.y );
}

float3 AnyDirection( float u, float v )
{
	float z = 1.0 - ( 2.0 * u );
	float radius = sqrt( max( 0.0, 1.0 - ( z * z ) ) );
	float angle = 2.0 * Pi * v;
	return float3( radius * cos( angle ), z, radius * sin( angle ) );
}

float3 CosineAround( float3 n, float u, float v )
{
	float radius = sqrt( u );
	float angle = 2.0 * Pi * v;
	float3 right, up;
	Basis( n, right, up );
	return normalize( right * ( radius * cos( angle ) ) + up * ( radius * sin( angle ) ) + n * sqrt( max( 0.0, 1.0 - u ) ) );
}

float3 InCone( float3 axis, float cosine, float u, float v )
{
	float z = 1.0 - ( u * ( 1.0 - cosine ) );
	float radius = sqrt( max( 0.0, 1.0 - ( z * z ) ) );
	float angle = 2.0 * Pi * v;
	float3 right, up;
	Basis( axis, right, up );
	return normalize( right * ( radius * cos( angle ) ) + up * ( radius * sin( angle ) ) + axis * z );
}

float3 Offset( float3 position, float3 geometric, float3 direction )
{
	float side = dot( direction, geometric ) >= 0.0 ? 1.0 : -1.0;
	float scale = max( max( abs( position.x ), abs( position.y ) ), abs( position.z ) );
	return position + geometric * ( side * ( Lift + ( scale * 2e-6 ) ) );
}

//---- pictures ------------------------------------------------------------------

//A material's picture, bilinear and repeating - BakePicture.Sample; nought where none, read as white
float4 Picture( uint at, uint width, uint height, float2 uv )
{
	float x = ( uv.x * width ) - 0.5;
	float y = ( uv.y * height ) - 0.5;

	float fx = floor( x );
	float fy = floor( y );

	float tx = x - fx;
	float ty = y - fy;

	int w = int( width );
	int h = int( height );

	int x0 = ( ( int( fx ) % w ) + w ) % w;
	int y0 = ( ( int( fy ) % h ) + h ) % h;
	int x1 = ( x0 + 1 ) % w;
	int y1 = ( y0 + 1 ) % h;

	uint base = PicturesAt() + at;

	float4 a = Float4( base + uint( ( y0 * w ) + x0 ) * 4 );
	float4 b = Float4( base + uint( ( y0 * w ) + x1 ) * 4 );
	float4 c = Float4( base + uint( ( y1 * w ) + x0 ) * 4 );
	float4 d = Float4( base + uint( ( y1 * w ) + x1 ) * 4 );

	return lerp( lerp( a, b, tx ), lerp( c, d, tx ), ty );
}

//A material, as BakeGpu.Pack writes it: 0  tint xyz, alpha 4  metalness, emission xyz 8  cutoff, flags, colour
//picture's place, width 12  height, emission picture's place, width, height 16  roughness, its picture's place, width,
//height 20  metalness picture's place, width, height
static const uint FlagMasked = 1u;
static const uint FlagTranslucent = 2u;
static const uint FlagTwoSided = 4u;
static const uint FlagGlowsFromAlbedo = 8u;
static const uint FlagHasColour = 16u;
static const uint FlagHasEmissionMap = 32u;
static const uint FlagHasRoughnessMap = 64u;
static const uint FlagHasMetalnessMap = 128u;

float4 MaterialColour( uint m, float2 uv )
{
	uint at = MaterialsAt() + ( m * MaterialWords );
	uint flags = Word( at + 9 );
	if ( ( flags & FlagHasColour ) == 0u ) return float4( 1.0, 1.0, 1.0, 1.0 );
	return Picture( Word( at + 10 ), Word( at + 11 ), Word( at + 12 ), uv );
}

static const uint CacheLightFloats = 11u;

//A world triangle's chance per square metre of being where a gather's light sample lands; nought where it is no light
float CacheDensity( uint t ) { return g_cacheLights == 0u ? 0.0 : g_lights[t]; }

//The light over pi the last pass left where a ray landed on a triangle with a place on the sheet
bool Cached( Hit hit, out float3 light )
{
	light = float3( 0.0, 0.0, 0.0 );
	if ( AtlasAt() == 0u ) return false;

	uint c = CornersAt() + ( hit.triangle * 3 );
	uint at = AtlasAt();

	float2 a = asfloat( g_world.Load2( ( at + ( Word( c ) * 2 ) ) * 4 ) );
	float2 b = asfloat( g_world.Load2( ( at + ( Word( c + 1 ) * 2 ) ) * 4 ) );
	float2 d = asfloat( g_world.Load2( ( at + ( Word( c + 2 ) * 2 ) ) * 4 ) );
	if ( a.x < 0.0 || b.x < 0.0 || d.x < 0.0 ) return false;

	float side = float( AtlasSide() );
	float2 spot = ( ( a * ( 1.0 - hit.u - hit.v ) ) + ( b * hit.u ) + ( d * hit.v ) ) * side;
	uint2 texel = min( uint2( max( spot, 0.0 ) ), uint2( AtlasSide() - 1u, AtlasSide() - 1u ) );

	light = g_cache[( texel.y * AtlasSide() ) + texel.x].rgb;
	return true;
}

float2 Uv( uint t, float u, float v )
{
	uint c = CornersAt() + ( t * 3 );
	uint a = Word( c ), b = Word( c + 1 ), d = Word( c + 2 );
	uint at = UvsAt();

	float2 ua = asfloat( g_world.Load2( ( at + a * 2 ) * 4 ) );
	float2 ub = asfloat( g_world.Load2( ( at + b * 2 ) * 4 ) );
	float2 ud = asfloat( g_world.Load2( ( at + d * 2 ) * 4 ) );

	return ua * ( 1.0 - u - v ) + ub * u + ud * v;
}

float3 Interpolate3( uint table, uint t, float u, float v )
{
	uint c = CornersAt() + ( t * 3 );
	uint a = Word( c ), b = Word( c + 1 ), d = Word( c + 2 );

	return Float3( table + a * 3 ) * ( 1.0 - u - v ) + Float3( table + b * 3 ) * u + Float3( table + d * 3 ) * v;
}

//Whether a ray passes a triangle that is not solid all over - BakeScene.Through.
bool Through( uint t, float u, float v )
{
	uint word = Word( TriangleAt() + ( t * 4 ) );
	uint mode = word >> 28;

	if ( mode == 1u ) return true;
	if ( mode == 0u ) return false;

	uint m = word & 0x0FFFFFFFu;
	uint at = MaterialsAt() + ( m * MaterialWords );

	float alpha = Float( at + 3 ) * MaterialColour( m, Uv( t, u, v ) ).w;
	return alpha < Float( at + 8 );
}

//---- the tree - BakeBvh --------------------------------------------------------

struct Hit
{
	float distance;
	uint triangle;
	float u, v;
};

float Enter( uint node, float3 origin, float3 inverse, float furthest )
{
	uint at = NodesAt() + ( node * 8 );
	float3 low = Float3( at );
	float3 high = Float3( at + 4 );

	float3 one = ( low - origin ) * inverse;
	float3 two = ( high - origin ) * inverse;
	float3 near = min( one, two );
	float3 far = max( one, two );

	float enter = max( max( near.x, near.y ), max( near.z, 0.0 ) );
	float leave = min( min( far.x, far.y ), min( far.z, furthest ) );

	return enter <= leave ? enter : 3.0e38;
}

float3 Inverse( float3 d )
{
	const float least = 1e-20;
	return float3(
		1.0 / ( abs( d.x ) < least ? ( d.x < 0.0 ? -least : least ) : d.x ),
		1.0 / ( abs( d.y ) < least ? ( d.y < 0.0 ? -least : least ) : d.y ),
		1.0 / ( abs( d.z ) < least ? ( d.z < 0.0 ? -least : least ) : d.z ) );
}

//Whether a ray crosses a tree triangle, however clear it is; its world triangle and whether it is not solid all over
bool Crosses( uint t, float3 origin, float3 direction, float furthest, out float distance, out float u, out float v, out uint source,
	out bool tested )
{
	distance = 0.0;
	u = 0.0;
	v = 0.0;
	source = 0u;
	tested = false;

	uint at = TreeAt() + ( t * 10 );

	float3 corner = Float3( at );
	float3 e1 = Float3( at + 3 );
	float3 e2 = Float3( at + 6 );

	float3 p = cross( direction, e2 );
	float det = dot( e1, p );
	if ( det > -1e-12 && det < 1e-12 ) return false;

	float inv = 1.0 / det;
	float3 s = origin - corner;
	u = dot( s, p ) * inv;
	if ( u < 0.0 || u > 1.0 ) return false;

	float3 q = cross( s, e1 );
	v = dot( direction, q ) * inv;
	if ( v < 0.0 || u + v > 1.0 ) return false;

	distance = dot( e2, q ) * inv;
	if ( distance <= 0.0 || distance >= furthest ) return false;

	uint word = Word( at + 9 );
	source = word & 0x7FFFFFFFu;
	tested = ( word & 0x80000000u ) != 0u;

	return true;
}

//How much of a ray gets by a triangle it crosses, each colour: none of a solid one, all or none of a cut-out where
//cut, and of glass what its mirror leaves at this angle (Schlick's): the share its opacity lets by, tinted the more it
//holds back, and half what it holds back, scattered on in its own colour - so a red pane throws red light
float3 Veil( uint t, float u, float v, bool tested, float3 direction )
{
	if ( !tested ) return float3( 0.0, 0.0, 0.0 );

	uint word = Word( TriangleAt() + ( t * 4 ) );
	uint m = word & 0x0FFFFFFFu;
	uint at = MaterialsAt() + ( m * MaterialWords );

	if ( ( word >> 28 ) == 2u ) return Through( t, u, v ) ? float3( 1.0, 1.0, 1.0 ) : float3( 0.0, 0.0, 0.0 );
	if ( ( Word( at + 9 ) & FlagTranslucent ) == 0u ) return float3( 1.0, 1.0, 1.0 );

	float4 texel = MaterialColour( m, Uv( t, u, v ) );
	float opacity = saturate( Float( at + 3 ) * texel.w );
	float3 colour = saturate( texel.rgb * Float3( at ) * Float3( TriangleAt() + ( t * 4 ) + 1 ) );

	float f0 = Float( at + 23 );
	float cosine = saturate( abs( dot( direction, Float3( FacingAt() + ( t * 3 ) ) ) ) );
	float mirrored = f0 + ( ( 1.0 - f0 ) * pow( 1.0 - cosine, 5.0 ) );
	float3 lets = ( lerp( float3( 1.0, 1.0, 1.0 ), colour, opacity ) * ( 1.0 - opacity ) ) + ( colour * ( opacity * 0.5 ) );

	return lets * ( 1.0 - mirrored );
}

//What a pane lets by, ten bits a colour in one word: four of them kept as float3s spilled out of registers
uint PackLets( float3 c )
{
	uint3 q = uint3( round( saturate( c ) * 1023.0 ) );
	return q.x | ( q.y << 10 ) | ( q.z << 20 );
}

float3 UnpackLets( uint w ) { return float3( float( w & 1023u ), float( ( w >> 10 ) & 1023u ), float( ( w >> 20 ) & 1023u ) ) / 1023.0; }

//The nearest solid thing along a ray, and in `veil` what the glass in front of it let by - the panes it crossed on the
//way, kept as it walks so the bounce needs no second walk (the first four by distance, any more just multiplied)
bool Nearest( float3 origin, float3 direction, float furthest, out Hit hit, out float3 veil )
{
	hit.distance = 0.0;
	hit.triangle = 0xFFFFFFFFu;
	hit.u = 0.0;
	hit.v = 0.0;
	veil = float3( 1.0, 1.0, 1.0 );

	if ( NodeCount() == 0u ) return false;

	float paneAt[4];
	uint paneLets[4];
	uint panes = 0u;

	float3 inverse = Inverse( direction );
	uint stack[48];
	uint depth = 0;
	uint at = 0;
	bool found = false;

	if ( Enter( 0, origin, inverse, furthest ) >= 3.0e38 ) return false;

	for ( ;; )
	{
		uint node = NodesAt() + ( at * 8 );
		uint first = Word( node + 3 );
		uint count = Word( node + 7 );

		bool descend = false;

		if ( count > 0u )
		{
			for ( uint t = first; t < first + count; ++t )
			{
				float distance, u, v;
				uint source;
				bool tested;
				if ( !Crosses( t, origin, direction, furthest, distance, u, v, source, tested ) ) continue;

				//Cut away or clear, it is passed; glass is passed and kept for what it lets by
				if ( tested )
				{
					float3 lets = Veil( source, u, v, true, direction );
					if ( all( lets >= 1.0 ) ) continue;

					if ( any( lets > 0.0 ) )
					{
						if ( panes < 4u )
						{
							paneAt[panes] = distance;
							paneLets[panes] = PackLets( lets );
							panes++;
						}
						else veil *= lets;

						continue;
					}
				}

				furthest = distance;
				found = true;
				hit.distance = distance;
				hit.triangle = Word( TreeAt() + ( t * 10 ) + 9 ) & 0x7FFFFFFFu;
				hit.u = u;
				hit.v = v;
			}
		}
		else
		{
			uint left = first;
			uint right = first + 1;
			float leftAt = Enter( left, origin, inverse, furthest );
			float rightAt = Enter( right, origin, inverse, furthest );

			if ( leftAt > rightAt )
			{
				uint swapNode = left; left = right; right = swapNode;
				float swapAt = leftAt; leftAt = rightAt; rightAt = swapAt;
			}

			if ( leftAt < 3.0e38 )
			{
				if ( rightAt < 3.0e38 && depth < 48 ) stack[depth++] = right;
				at = left;
				descend = true;
			}
		}

		if ( descend ) continue;
		if ( depth == 0 ) break;
		at = stack[--depth];
	}

	for ( uint p = 0u; p < panes; ++p )
		if ( !found || paneAt[p] < hit.distance ) veil *= UnpackLets( paneLets[p] );

	return found;
}

//How much of a ray gets through to `furthest`: what every pane it crosses lets by, nought at the first solid thing -
//one walk for shadows and glass alike, which with no glass in the world is a plain test of whether anything is there
float3 Transmittance( float3 origin, float3 direction, float furthest )
{
	if ( NodeCount() == 0u ) return float3( 1.0, 1.0, 1.0 );

	float3 inverse = Inverse( direction );
	uint stack[48];
	uint depth = 0;
	uint at = 0;
	float3 through = float3( 1.0, 1.0, 1.0 );

	if ( Enter( 0, origin, inverse, furthest ) >= 3.0e38 ) return through;

	for ( ;; )
	{
		uint node = NodesAt() + ( at * 8 );
		uint first = Word( node + 3 );
		uint count = Word( node + 7 );

		if ( count > 0u )
		{
			for ( uint t = first; t < first + count; ++t )
			{
				float distance, u, v;
				uint source;
				bool tested;
				if ( !Crosses( t, origin, direction, furthest, distance, u, v, source, tested ) ) continue;

				through *= Veil( source, u, v, tested, direction );
				if ( all( through <= 1e-4 ) ) return float3( 0.0, 0.0, 0.0 );
			}
		}
		else
		{
			uint left = first;
			uint right = first + 1;
			bool leftIn = Enter( left, origin, inverse, furthest ) < 3.0e38;
			bool rightIn = Enter( right, origin, inverse, furthest ) < 3.0e38;

			if ( leftIn )
			{
				if ( rightIn && depth < 48 ) stack[depth++] = right;
				at = left;
				continue;
			}

			if ( rightIn )
			{
				at = right;
				continue;
			}
		}

		if ( depth == 0 ) break;
		at = stack[--depth];
	}

	return through;
}

//---- the world as the rays see it - BakeScene ----------------------------------

struct Surface
{
	float3 position;
	float3 normal;
	float3 geometric;
	float3 albedo;
	float3 emission;
	bool back;

	//What it mirrors at square incidence - four per cent where it is not metal, its colour where it is - and how rough
	float3 specular;
	float roughness;
};

//What a material gives off at a point - BakeScene.GlowOf
float3 GlowOf( uint at, uint flags, float2 uv, float4 texel )
{
	float3 glow = Float3( at + 5 );
	if ( glow.x <= 0.0 && glow.y <= 0.0 && glow.z <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	float3 from = ( flags & FlagGlowsFromAlbedo ) != 0u ? texel.xyz : float3( 1.0, 1.0, 1.0 );
	float3 map = ( flags & FlagHasEmissionMap ) != 0u
		? Picture( Word( at + 13 ), Word( at + 14 ), Word( at + 15 ), uv ).xyz
		: float3( 1.0, 1.0, 1.0 );

	return map * from * glow;
}

Surface SurfaceOf( Hit hit, float3 origin, float3 direction )
{
	uint t = hit.triangle;
	uint word = Word( TriangleAt() + ( t * 4 ) );
	uint m = word & 0x0FFFFFFFu;
	uint at = MaterialsAt() + ( m * MaterialWords );
	uint flags = Word( at + 9 );

	Surface s;
	s.position = origin + direction * hit.distance;
	s.geometric = Float3( FacingAt() + ( t * 3 ) );

	float3 normal = Interpolate3( NormalsAt(), t, hit.u, hit.v );
	float length = sqrt( dot( normal, normal ) );
	s.normal = length > 1e-8 ? normal / length : s.geometric;

	s.back = dot( direction, s.geometric ) > 0.0;

	if ( s.back && ( flags & FlagTwoSided ) != 0u )
	{
		s.geometric = -s.geometric;
		s.normal = -s.normal;
		s.back = false;
	}

	float2 uv = Uv( t, hit.u, hit.v );
	float4 texel = MaterialColour( m, uv );
	float3 vertex = Interpolate3( ColoursAt(), t, hit.u, hit.v );
	float3 tint = Float3( TriangleAt() + ( t * 4 ) + 1 );

	float3 albedo = texel.xyz * Float3( at ) * tint * vertex;

	//As complex.shader: each the number times its picture where there is one - see BakeMaterial.MetalnessAt
	float metal = Float( at + 4 );
	if ( ( flags & FlagHasMetalnessMap ) != 0u ) metal *= Picture( Word( at + 20 ), Word( at + 21 ), Word( at + 22 ), uv ).x;
	metal = saturate( metal );

	float rough = Float( at + 16 );
	if ( ( flags & FlagHasRoughnessMap ) != 0u ) rough *= Picture( Word( at + 17 ), Word( at + 18 ), Word( at + 19 ), uv ).x;

	//Metal keeps none of its light diffuse; what it and everything else mirrors is the specular colour - see Sheen
	s.albedo = min( albedo * ( 1.0 - metal ) * AlbedoBoost(), float3( 0.95, 0.95, 0.95 ) );
	s.specular = lerp( float3( 0.04, 0.04, 0.04 ), min( albedo, float3( 1.0, 1.0, 1.0 ) ), metal );
	s.roughness = clamp( rough, 0.04, 1.0 );

	s.emission = GlowsNow( t ) ? GlowOf( at, flags, uv, texel ) : float3( 0.0, 0.0, 0.0 );

	return s;
}

//Sky and flat ambient from one direction - BakeScene.Environment, from BakeGpu.Pack's octahedral map
float3 Environment( float3 d )
{
	float3 a = abs( d );
	float2 o = d.xz / max( a.x + a.y + a.z, 1e-8 );

	if ( d.y < 0.0 )
		o = ( 1.0 - abs( o.yx ) ) * float2( o.x >= 0.0 ? 1.0 : -1.0, o.y >= 0.0 ? 1.0 : -1.0 );

	uint side = SkySide();
	float2 place = ( ( o * 0.5 ) + 0.5 ) * float( side ) - 0.5;
	float2 corner = floor( place );
	float2 f = place - corner;

	int last = int( side ) - 1;
	int x0 = clamp( int( corner.x ), 0, last );
	int y0 = clamp( int( corner.y ), 0, last );
	int x1 = min( x0 + 1, last );
	int y1 = min( y0 + 1, last );

	uint at = SkyAt();

	float3 c00 = Float3( at + uint( y0 * int( side ) + x0 ) * 3 );
	float3 c10 = Float3( at + uint( y0 * int( side ) + x1 ) * 3 );
	float3 c01 = Float3( at + uint( y1 * int( side ) + x0 ) * 3 );
	float3 c11 = Float3( at + uint( y1 * int( side ) + x1 ) * 3 );

	return lerp( lerp( c00, c10, f.x ), lerp( c01, c11, f.x ), f.y );
}

//A lamp, as BakeGpu.Pack writes it: 0 kind, position, range, colour, forward, inner and outer cosine, falloff,
//radius, flags (1 shadows, 2 whole) 17 shape, half extents 20 right xyz
static const uint LightShadows = 1u;
static const uint LightBaked = 2u;

//A lamp's light at a squared distance: its falloff to its radius, then the inverse square on - BakeScene.Spread
float Spread( uint at, float square )
{
	float edge = max( Float( at + 4 ) * Float( at + 4 ), 1e-4 );
	float near = min( square, edge );
	float falloff = Float( at + 13 );
	float own = 1.0 / ( falloff == 1.0 ? near : pow( near, falloff ) );

	return square > edge ? own * edge / square : own;
}

//A random point on a shaped lamp and the cosine it gives off at towards a point - BakeScene.PointOn
float3 PointOn( uint at, float3 position, inout Random random, out float cosine )
{
	float u = random.Next();
	float v = random.Next();

	uint shape = Word( at + 17 );
	float halfWidth = Float( at + 18 );
	float halfHeight = Float( at + 19 );
	float3 centre = Float3( at + 1 );
	float3 forward = Float3( at + 8 );
	float3 right = Float3( at + 20 );
	float3 up = cross( forward, right );

	float3 point;
	float3 facing = forward;

	if ( shape == 3u )
	{
		float3 across = cross( right, position - centre );
		float side = length( across );
		across = side > 1e-5 ? across / side : up;

		point = centre + ( right * ( halfWidth * ( ( 2.0 * u ) - 1.0 ) ) ) + ( across * ( halfHeight * ( ( 2.0 * v ) - 1.0 ) ) );
		facing = cross( across, right );
	}
	else if ( shape == 2u )
	{
		float r = halfWidth * sqrt( u );
		float angle = 2.0 * Pi * v;

		point = centre + ( ( ( right * cos( angle ) ) + ( up * sin( angle ) ) ) * r );
	}
	else
	{
		point = centre + ( right * ( halfWidth * ( ( 2.0 * u ) - 1.0 ) ) ) + ( up * ( halfHeight * ( ( 2.0 * v ) - 1.0 ) ) );
	}

	float3 away = position - point;
	float length0 = length( away );
	cosine = length0 > 1e-6 ? dot( away / length0, facing ) : 0.0;

	if ( shape == 3u ) cosine = abs( cosine );

	return point;
}

//A lamp's light where nothing stands between, the way to the point on it sampled and how far - BakeScene.Unshadowed
float3 Unshadowed( uint index, float3 position, float3 normal, float3 geometric, inout Random random, out float3 l,
	out float3 aim, out float distance )
{
	l = float3( 0.0, 0.0, 0.0 );
	aim = float3( 0.0, 0.0, 0.0 );
	distance = 0.0;

	uint at = LightsAt() + ( index * LightWords );
	uint kind = Word( at );

	float attenuation = 1.0;

	float3 forward = Float3( at + 8 );

	if ( kind == 0u )
	{
		l = -forward;
		distance = 1e5;
		float u = random.Next();
		float v = random.Next();
		aim = InCone( l, SunCosine(), u, v );
	}
	else if ( kind == 1u && Word( at + 17 ) != 0u )
	{
		//BakeScene.DirectOne's shaped lamp: radiance colour over area, its falloff as the ratio to the inverse square
		float3 centre = Float3( at + 1 ) - position;

		uint shape = Word( at + 17 );
		float halfWidth = Float( at + 18 );
		float area = shape == 2u ? Pi * halfWidth * halfWidth : 4.0 * halfWidth * Float( at + 19 );

		float cosine;
		float3 on = PointOn( at, position, random, cosine );
		if ( cosine <= 0.0 || area <= 1e-8 ) return float3( 0.0, 0.0, 0.0 );

		float3 toPoint = on - position;
		distance = length( toPoint );
		if ( distance < 1e-5 ) return float3( 0.0, 0.0, 0.0 );

		aim = toPoint / distance;
		l = aim;

		float square = max( dot( centre, centre ), 0.25 * area );

		attenuation = cosine * square * Spread( at, square ) / max( distance * distance, 1e-4 );
	}
	else
	{
		float3 lamp = Float3( at + 1 );

		float3 toLight = lamp - position;
		distance = length( toLight );

		if ( distance < 1e-5 ) return float3( 0.0, 0.0, 0.0 );

		l = toLight / distance;
		attenuation = Spread( at, max( distance * distance, 1e-4 ) );

		if ( kind == 2u )
		{
			float aligned = dot( -l, forward );
			float inner = Float( at + 11 );
			float outer = Float( at + 12 );
			attenuation *= saturate( ( aligned - outer ) / max( inner - outer, 1e-4 ) );
		}

		float3 target = lamp;
		float radius = Float( at + 14 );

		float u = random.Next();
		float v = random.Next();

		if ( radius > 0.0 )
		{
			float3 right, up;
			Basis( l, right, up );

			float r = radius * sqrt( u );
			float angle = 2.0 * Pi * v;

			target += ( right * ( r * cos( angle ) ) ) + ( up * ( r * sin( angle ) ) );
		}

		float3 toPoint = target - position;
		distance = length( toPoint );
		aim = toPoint / max( distance, 1e-6 );
	}

	float nDotL = dot( normal, l );
	if ( nDotL <= 0.0 || attenuation <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	if ( dot( geometric, aim ) <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	return Float3( at + 5 ) * ( nDotL * attenuation / Pi );
}

//Whether a ray crosses, however clear, a triangle of one mirror - BakeBvh.Touches
bool Touches( float3 origin, float3 direction, float furthest, uint mirror )
{
	float3 inverse = Inverse( direction );
	uint stack[48];
	uint depth = 0;
	uint at = 0;

	if ( NodeCount() == 0u || Enter( 0, origin, inverse, furthest ) >= 3.0e38 ) return false;

	for ( ;; )
	{
		uint node = NodesAt() + ( at * 8 );
		uint first = Word( node + 3 );
		uint count = Word( node + 7 );

		if ( count > 0u )
		{
			for ( uint t = first; t < first + count; ++t )
			{
				float distance, u, v;
				uint source;
				bool tested;
				if ( Crosses( t, origin, direction, furthest, distance, u, v, source, tested ) && Word( MirrorOfAt() + source ) == mirror )
					return true;
			}
		}
		else
		{
			uint left = first;
			uint right = first + 1;
			bool leftIn = Enter( left, origin, inverse, furthest ) < 3.0e38;
			bool rightIn = Enter( right, origin, inverse, furthest ) < 3.0e38;

			if ( leftIn )
			{
				if ( rightIn && depth < 48 ) stack[depth++] = right;
				at = left;
				continue;
			}

			if ( rightIn )
			{
				at = right;
				continue;
			}
		}

		if ( depth == 0 ) break;
		at = stack[--depth];
	}

	return false;
}

float3 Reflect( float3 v, float3 n ) { return v - ( n * ( 2.0 * dot( v, n ) ) ); }

//Each lamp's light, and as each mirror shows it - BakeScene.Direct and Reflected: the lamp as the point's image behind the
//mirror sees it, where the way crosses the mirror and both legs are clear. One loop for both, so the lamp's sampling and
//the shadow ray are one copy each in the kernel
float3 Direct( float3 position, float3 normal, float3 geometric, inout Random random, bool bakedOnly, inout float3 towards )
{
	float3 sum = float3( 0.0, 0.0, 0.0 );
	uint count = LightCount();
	uint images = MirrorCount() + 1u;

	for ( uint i = 0; i < count; ++i )
	{
		uint flags = Word( LightsAt() + ( i * LightWords ) + 15 );
		if ( bakedOnly && ( flags & LightBaked ) == 0u ) continue;

		for ( uint k = 0u; k < images; ++k )
		{
			float3 seen = position;
			float3 n = float3( 0.0, 1.0, 0.0 );
			float side = 0.0;
			uint mirror = MirrorsAt() + ( ( k - 1u ) * 8u );

			if ( k > 0u )
			{
				n = Float3( mirror );
				side = dot( n, position ) - Float( mirror + 3u );

				if ( side < 0.0 )
				{
					if ( Word( mirror + 7u ) == 0u ) continue;

					n = -n;
					side = -side;
				}

				if ( side < 1e-3 ) continue;

				seen = position - ( n * ( 2.0 * side ) );
			}

			float3 l, aim;
			float distance;
			float3 lit = Unshadowed( i, seen, k > 0u ? Reflect( normal, n ) : normal, k > 0u ? Reflect( geometric, n ) : geometric,
				random, l, aim, distance );
			if ( !any( lit > 0.0 ) ) continue;

			//The way from the point and how far, then from the mirror on to the lamp where there is one
			float3 way = aim;
			float reach = distance;
			float3 spot = position;
			float rise = 1.0;

			if ( k > 0u )
			{
				rise = dot( n, aim );
				if ( rise <= 1e-4 ) continue;

				reach = side / rise;
				if ( reach >= distance ) continue;

				spot = seen + ( aim * reach );
				if ( !Touches( spot + ( n * 0.01 ), -n, 0.02, k - 1u ) ) continue;

				way = Reflect( aim, n );

				//Schlick's for water and glass, rising to all of it at a grazing angle; metal mirrors its colour
				float3 f0 = Float3( mirror + 4u );
				lit *= Word( mirror + 7u ) != 0u ? f0 + ( ( 1.0 - f0 ) * pow( 1.0 - rise, 5.0 ) ) : f0;
			}

			uint legs = k > 0u ? 2u : 1u;

			if ( ( flags & LightShadows ) != 0u )
			{
				for ( uint leg = 0u; leg < legs && any( lit > 0.0 ); ++leg )
				{
					float3 from = leg == 0u ? Offset( position, geometric, way ) : Offset( spot, n, aim );
					lit *= Transmittance( from, leg == 0u ? way : aim, ( leg == 0u ? reach : distance - reach ) - ( Lift * 2.0 ) );
				}
			}

			sum += lit;
			towards += ( k > 0u ? way : l ) * Luma( lit );
		}
	}

	return sum;
}

//Power heuristic - BakeScene.Weight
float Weight( float picked, float other ) { return picked * picked / max( ( picked * picked ) + ( other * other ), 1e-30 ); }

//One shadow ray to a point on a glowing triangle picked by power, MIS-weighted - BakeScene.TowardsGlow
float3 TowardsGlow( float3 position, float3 normal, float3 geometric, bool onSurface, inout Random random, out float3 aim )
{
	aim = float3( 0.0, 0.0, 0.0 );

	uint count = EmitterCount();
	if ( count == 0u ) return float3( 0.0, 0.0, 0.0 );

	float pick = random.Next();
	float root = sqrt( random.Next() );
	float turn = random.Next();

	//The first whose power summed so far passes the pick
	uint list = EmittersAt();
	float picked = Float( list + ( ( count - 1u ) * EmitterWords ) + 1u ) * pick;
	uint low = 0u;
	uint high = count - 1u;

	while ( low < high )
	{
		uint middle = ( low + high ) / 2u;
		if ( Float( list + ( middle * EmitterWords ) + 1u ) > picked ) high = middle;
		else low = middle + 1u;
	}

	uint record = list + ( low * EmitterWords );
	uint t = Word( record );
	float u = root * ( 1.0 - turn );
	float v = root * turn;

	float3 point = Float3( record + 2u ) + ( Float3( record + 5u ) * u ) + ( Float3( record + 8u ) * v );
	float3 toPoint = point - position;
	float distance = length( toPoint );
	if ( distance < 1e-5 ) return float3( 0.0, 0.0, 0.0 );

	aim = toPoint / distance;

	uint m = Word( TriangleAt() + ( t * 4 ) ) & 0x0FFFFFFFu;
	uint at = MaterialsAt() + ( m * MaterialWords );
	uint flags = Word( at + 9 );

	float facing = dot( aim, Float3( FacingAt() + ( t * 3 ) ) );
	float cosine = ( flags & FlagTwoSided ) != 0u ? abs( facing ) : -facing;
	if ( cosine <= 1e-6 ) return float3( 0.0, 0.0, 0.0 );

	float bounce = InvPi * 0.25;

	if ( onSurface )
	{
		float nDotL = dot( normal, aim );
		if ( nDotL <= 0.0 || dot( geometric, aim ) <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

		bounce = nDotL * InvPi;
	}

	if ( ( flags & FlagMasked ) != 0u && Through( t, u, v ) ) return float3( 0.0, 0.0, 0.0 );

	float3 from = onSurface ? Offset( position, geometric, aim ) : position;
	float3 through = Transmittance( from, aim, ( distance * 0.999 ) - ( Lift * 2.0 ) );
	if ( !any( through > 0.0 ) ) return float3( 0.0, 0.0, 0.0 );

	float2 uv = Uv( t, u, v );
	float4 texel = ( flags & FlagGlowsFromAlbedo ) != 0u ? MaterialColour( m, uv ) : float4( 1.0, 1.0, 1.0, 1.0 );

	float density = Float( GlowAt() + t ) * distance * distance / cosine;

	return GlowOf( at, flags, uv, texel ) * ( through * Weight( density, bounce ) / density );
}

//Glowing triangles' direct light over pi on a surface, and its lean - BakeScene.GlowDirect
float3 GlowDirect( float3 position, float3 normal, float3 geometric, inout Random random, inout float3 towards )
{
	if ( EmitterCount() == 0u ) return float3( 0.0, 0.0, 0.0 );

	float3 aim;
	float3 arriving = TowardsGlow( position, normal, geometric, true, random, aim ) * ( max( dot( normal, aim ), 0.0 ) * InvPi );

	towards += aim * Luma( arriving );
	return arriving;
}

//A point on one of the cache's lit triangles picked by power, seen from a surface: the light it leaves towards it, over
//pi, unweighed - with its way and the chance per steradian it was picked; nought where it faces away or is hidden
float3 TowardsCache( float3 position, float3 geometric, inout Random random, out float3 aim, out float chance )
{
	aim = float3( 0.0, 0.0, 0.0 );
	chance = 0.0;

	uint count = g_cacheLights;
	if ( count == 0u ) return float3( 0.0, 0.0, 0.0 );

	float pick = random.Next();
	float root = sqrt( random.Next() );
	float turn = random.Next();

	//The first whose power summed so far passes the pick
	uint list = g_triangles;
	float picked = g_lights[list + ( ( count - 1u ) * CacheLightFloats ) + 1u] * pick;
	uint low = 0u;
	uint high = count - 1u;

	while ( low < high )
	{
		uint middle = ( low + high ) / 2u;
		if ( g_lights[list + ( middle * CacheLightFloats ) + 1u] > picked ) high = middle;
		else low = middle + 1u;
	}

	uint record = list + ( low * CacheLightFloats );
	uint t = asuint( g_lights[record] );

	Hit hit;
	hit.triangle = t;
	hit.u = root * ( 1.0 - turn );
	hit.v = root * turn;

	float3 corner = float3( g_lights[record + 2u], g_lights[record + 3u], g_lights[record + 4u] );
	float3 one = float3( g_lights[record + 5u], g_lights[record + 6u], g_lights[record + 7u] );
	float3 two = float3( g_lights[record + 8u], g_lights[record + 9u], g_lights[record + 10u] );

	float3 toPoint = corner + ( one * hit.u ) + ( two * hit.v ) - position;
	hit.distance = length( toPoint );
	if ( hit.distance < 1e-5 ) return float3( 0.0, 0.0, 0.0 );

	aim = toPoint / hit.distance;
	if ( dot( geometric, aim ) <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	Surface s = SurfaceOf( hit, position, aim );
	float facing = -dot( aim, s.geometric );
	if ( s.back || facing <= 1e-6 ) return float3( 0.0, 0.0, 0.0 );

	float3 cached;
	if ( !Cached( hit, cached ) ) return float3( 0.0, 0.0, 0.0 );

	float3 through = Transmittance( Offset( position, geometric, aim ), aim, ( hit.distance * 0.999 ) - ( Lift * 2.0 ) );
	if ( !any( through > 0.0 ) ) return float3( 0.0, 0.0, 0.0 );

	chance = CacheDensity( t ) * hit.distance * hit.distance / facing;
	return s.albedo * cached * through;
}

//The light a surface mirrors towards `towards`: one way picked on its GGX lobe, weighed by Fresnel and Smith, and
//what the last pass left where that way lands - the bounce a black lacquer or a metal throws, which a lightmap that
//knew only diffuse light gave none of. Taken by its share against the diffuse light's, and weighed back by it
float3 Sheen( Surface s, float3 towards, inout Random random )
{
	float nDotV = dot( s.normal, towards );
	if ( nDotV <= 1e-4 ) return float3( 0.0, 0.0, 0.0 );

	//What it mirrors, a little more for the grazing Fresnel, against what it scatters: plaster seldom, metal always
	float mirrored = Luma( s.specular ) + 0.02;
	float taken = clamp( mirrored / ( mirrored + Luma( s.albedo ) ), 0.05, 1.0 );
	if ( random.Next() >= taken ) return float3( 0.0, 0.0, 0.0 );

	float alpha = max( s.roughness * s.roughness, 1e-3 );
	float u = random.Next();
	float v = random.Next();

	float cosine = sqrt( ( 1.0 - v ) / ( 1.0 + ( ( ( alpha * alpha ) - 1.0 ) * v ) ) );
	float sine = sqrt( max( 0.0, 1.0 - ( cosine * cosine ) ) );
	float angle = 2.0 * Pi * u;

	float3 right, up;
	Basis( s.normal, right, up );
	float3 between = normalize( ( right * ( sine * cos( angle ) ) ) + ( up * ( sine * sin( angle ) ) ) + ( s.normal * cosine ) );

	float vDotH = dot( towards, between );
	if ( vDotH <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	float3 l = ( 2.0 * vDotH * between ) - towards;
	float nDotL = dot( s.normal, l );
	if ( nDotL <= 0.0 || dot( l, s.geometric ) <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//Schlick's Fresnel and Smith's shadowing over the half vector's own chance: the lobe's share that way
	float k = alpha * 0.5;
	float smith = ( nDotV / ( ( nDotV * ( 1.0 - k ) ) + k ) ) * ( nDotL / ( ( nDotL * ( 1.0 - k ) ) + k ) );
	float3 fresnel = s.specular + ( ( 1.0 - s.specular ) * pow( 1.0 - vDotH, 5.0 ) );
	float3 share = fresnel * ( smith * vDotH / max( nDotV * saturate( dot( s.normal, between ) ), 1e-4 ) ) / taken;

	Hit hit;
	float3 from = Offset( s.position, s.geometric, l );
	float3 veil;
	bool found = Nearest( from, l, 1e6, hit, veil );
	share *= veil;
	if ( !found ) return share * Environment( l );

	Surface there = SurfaceOf( hit, from, l );
	if ( there.back ) return float3( 0.0, 0.0, 0.0 );

	float3 cached;
	float3 leaving = there.emission;

	if ( Cached( hit, cached ) ) leaving += there.albedo * cached;
	else
	{
		float3 ignored = float3( 0.0, 0.0, 0.0 );
		leaving += there.albedo * ( Direct( there.position, there.normal, there.geometric, random, false, ignored )
			+ GlowDirect( there.position, there.normal, there.geometric, random, ignored ) );
	}

	return share * leaving;
}

//Density: the caller's chance per steradian of this direction when it also sampled a glow, else nought
//Escaped: the ray left straight to the sky, which the frame lights by the kept visibility - BakeScene.Arriving
//What glass the last Arriving's first ray crossed let by - the share of an escape that counts as sky seen
static float g_firstVeil = 1.0;

float3 Arriving( float3 origin, float3 direction, float density, inout Random random, out bool inside, out bool escaped )
{
	inside = false;
	escaped = false;
	g_firstVeil = 1.0;

	float3 carried = float3( 1.0, 1.0, 1.0 );
	float3 gathered = float3( 0.0, 0.0, 0.0 );

	uint bounces = Bounces();

	for ( uint bounce = 0; ; ++bounce )
	{
		Hit hit;
		float3 veil;
		bool found = Nearest( origin, direction, 1e6, hit, veil );

		//Glass on the way lets only its share by
		carried *= veil;
		if ( bounce == 0u ) g_firstVeil = ( veil.x + veil.y + veil.z ) / 3.0;

		if ( !found )
		{
			//The sky is the frame's for a texel's own escapes, unless a pass gathers it into the light it leaves
			escaped = bounce == 0u && ( g_mode & ModeSky ) == 0u;
			return escaped ? gathered : gathered + ( carried * Environment( direction ) );
		}

		Surface s = SurfaceOf( hit, origin, direction );

		//A back face first is a point inside something; a front one however near is what shades it
		if ( s.back )
		{
			if ( bounce == 0 ) inside = true;
			return gathered;
		}

		if ( any( s.emission > 0.0 ) )
		{
			//What TowardsGlow at the last point could have found as well counts only its share
			float glow = density > 0.0 && EmitterCount() > 0u ? Float( GlowAt() + hit.triangle ) : 0.0;
			float cosine = abs( dot( direction, s.geometric ) );

			gathered += carried * s.emission * ( glow > 0.0 && cosine > 1e-6
				? Weight( density, glow * hit.distance * hit.distance / cosine )
				: 1.0 );
		}

		if ( bounce >= bounces ) return gathered;

		//Gathering: the light the last pass found here, every bounce of it, in place of the rest of the path
		float3 cached;
		if ( bounce == 0u && ( g_mode & ModeGather ) != 0u && Cached( hit, cached ) )
		{
			//What TowardsCache could have found here as well counts only its share
			float lit = CacheDensity( hit.triangle );
			float facing = abs( dot( direction, s.geometric ) );
			float share = density > 0.0 && lit > 0.0 && facing > 1e-6 ? Weight( density, lit * hit.distance * hit.distance / facing ) : 1.0;

			return gathered + ( carried * ( ( s.albedo * cached * share ) + Sheen( s, -direction, random ) ) );
		}

		float3 ignored = float3( 0.0, 0.0, 0.0 );
		gathered += carried * s.albedo * ( Direct( s.position, s.normal, s.geometric, random, false, ignored )
			+ GlowDirect( s.position, s.normal, s.geometric, random, ignored ) );

		carried *= s.albedo;

		if ( bounce >= 1 )
		{
			float keep = min( max( carried.x, max( carried.y, carried.z ) ), 0.95 );
			if ( random.Next() >= keep ) return gathered;
			carried /= keep;
		}

		float u = random.Next();
		float v = random.Next();
		float3 axis = s.normal;
		direction = CosineAround( axis, u, v );

		if ( dot( direction, s.geometric ) <= 0.0 )
		{
			u = random.Next();
			v = random.Next();
			axis = s.geometric;
			direction = CosineAround( axis, u, v );
		}

		density = EmitterCount() > 0u ? dot( axis, direction ) * InvPi : 0.0;
		origin = Offset( s.position, s.geometric, direction );
	}

	return gathered;
}

//---- a texel - BakeSheet.Trace -------------------------------------------------

[shader("compute")]
[numthreads(64, 1, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_count ) return;

	uint texel = g_first + id.x;


	float4 centre = g_texels[( texel * 5 ) + 0];
	float4 n = g_texels[( texel * 5 ) + 1];
	float3 g = g_texels[( texel * 5 ) + 2].xyz;
	uint transmitter = asuint( g_texels[( texel * 5 ) + 2].w );
	float4 across = g_texels[( texel * 5 ) + 3];
	float4 down = g_texels[( texel * 5 ) + 4];

	uint at = texel * 9u;

	float4 sumSquares = g_sums[at + 0u];
	float4 insideSpare = g_sums[at + 1u];
	float4 open = g_sums[at + 2u];

	//The light's first band a colour, bounced then straight - see BakeSheet.Encode
	float3 bouncedR = g_sums[at + 3u].xyz, bouncedG = g_sums[at + 4u].xyz, bouncedB = g_sums[at + 5u].xyz;
	float3 straightR = g_sums[at + 6u].xyz, straightG = g_sums[at + 7u].xyz, straightB = g_sums[at + 8u].xyz;

	for ( uint s = 0; s < g_samples; ++s )
	{
		Random random;
		random.state = Hash( texel ^ Hash( g_sampleBase + s + g_seed ) );

		//Somewhere in the texel's area on its own face - see BakeSheet.reach, the four w's
		float3 p = centre.xyz + ( across.xyz * lerp( centre.w, n.w, random.Next() ) ) + ( down.xyz * lerp( across.w, down.w, random.Next() ) );

		//A leaf's or thin cloth's back first, whose light comes through it, then its face - one set of calls for both,
		//as a second inlined Arriving costs every texel registers; see BakeScene.Transmission
		float3 through = float3( 0.0, 0.0, 0.0 );
		float3 direction = n.xyz;
		float3 arriving = float3( 0.0, 0.0, 0.0 );
		float3 aimed = float3( 0.0, 0.0, 0.0 );
		float3 towardsLit = float3( 0.0, 0.0, 0.0 );
		float3 lean = float3( 0.0, 0.0, 0.0 );
		float3 direct = float3( 0.0, 0.0, 0.0 );
		bool behind = false;

		for ( uint side = transmitter == 0u ? 1u : 2u; side-- > 0u; )
		{
			float3 sn = side == 0u ? n.xyz : -n.xyz;
			float3 sg = side == 0u ? g : -g;

			float3 axis = sn;
			direction = CosineAround( axis, random.Next(), random.Next() );

			if ( dot( direction, sg ) <= 0.0 )
			{
				axis = sg;
				direction = CosineAround( axis, random.Next(), random.Next() );
			}

			float density = EmitterCount() > 0u || g_cacheLights > 0u ? dot( axis, direction ) * InvPi : 0.0;

			bool inside;
			bool escaped;
			arriving = Arriving( Offset( p, sg, direction ), direction, density, random, inside, escaped );

			//A spot of the texel inside something counts for nothing: the rest of the texel is what it shows
			if ( inside )
			{
				behind = side == 0u;
				continue;
			}

			//The face's sky is the frame's, by the visibility kept; the back's is counted here
			if ( escaped && side == 0u ) open += float4( direction, 1.0 ) * g_firstVeil;
			if ( escaped && side == 1u ) arriving += Environment( direction ) * g_firstVeil;

			//And a lit spot of the cache aimed at, by its power, weighed against the cosine ray's chance of that way
			aimed = float3( 0.0, 0.0, 0.0 );
			float litChance;
			float3 litLight = TowardsCache( p, sg, random, towardsLit, litChance );

			if ( litChance > 0.0 )
			{
				float facing = dot( sn, towardsLit );
				if ( facing > 0.0 ) aimed = litLight * ( facing * InvPi * Weight( litChance, facing * InvPi ) / litChance );
			}

			lean = float3( 0.0, 0.0, 0.0 );
			direct = float3( 0.0, 0.0, 0.0 );

			if ( ( g_mode & ModeNoDirect ) == 0u )
				direct = Direct( p, sn, sg, random, ( g_mode & ModeAllLights ) == 0u, lean ) + GlowDirect( p, sn, sg, random, lean );

			//Half of what a face would take, as F_TRANSMISSION shades it: with the bounced light, and in no band
			if ( side == 1u )
				through = Float3( MaterialsAt() + ( ( transmitter - 1u ) * MaterialWords ) + 24 ) * ( arriving + aimed + direct ) * ( Pi * 0.5 );
		}

		if ( behind )
		{
			insideSpare.x += 1.0;
			continue;
		}

		float luma = Luma( arriving + aimed + through );

		sumSquares += float4( direct + arriving + aimed + through, luma * luma );
		insideSpare.yzw += direct;

		bouncedR += ( direction * arriving.r ) + ( towardsLit * aimed.r );
		bouncedG += ( direction * arriving.g ) + ( towardsLit * aimed.g );
		bouncedB += ( direction * arriving.b ) + ( towardsLit * aimed.b );

		//The lights' way, and their light as it would fall square on, so a normal facing them takes all of it
		float leaning = length( lean );

		if ( leaning > 1e-8 )
		{
			float3 l = lean / leaning;
			float3 square = direct / max( dot( n.xyz, l ), 0.1 );

			straightR += l * square.r;
			straightG += l * square.g;
			straightB += l * square.b;
		}
	}

	g_sums[at + 0u] = sumSquares;
	g_sums[at + 1u] = insideSpare;
	g_sums[at + 2u] = open;
	g_sums[at + 3u] = float4( bouncedR, 0.0 );
	g_sums[at + 4u] = float4( bouncedG, 0.0 );
	g_sums[at + 5u] = float4( bouncedB, 0.0 );
	g_sums[at + 6u] = float4( straightR, 0.0 );
	g_sums[at + 7u] = float4( straightG, 0.0 );
	g_sums[at + 8u] = float4( straightB, 0.0 );
}

//---- a point of the volume - BakeVolume.Trace ----------------------------------

//One light-volume lattice point: stratified directions, summed with their per-axis colour lean
[shader("compute")]
[numthreads(64, 1, 1)]
void ProbeCs( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_count ) return;

	uint point = g_first + id.x;
	float3 at = g_texels[point].xyz;

	float4 sum = g_sums[( point * 5 ) + 0];
	float4 red = g_sums[( point * 5 ) + 1];
	float4 green = g_sums[( point * 5 ) + 2];
	float4 blue = g_sums[( point * 5 ) + 3];
	float4 open = g_sums[( point * 5 ) + 4];

	uint across = max( g_across, 2u );

	//The sphere's chance per steradian of a direction, where glowing triangles are sent rays of their own
	float glowing = EmitterCount() > 0u ? InvPi * 0.25 : 0.0;

	for ( uint k = 0; k < g_samples; ++k )
	{
		uint s = g_sampleBase + k;

		Random random;
		random.state = Hash( ( point * 7919u ) ^ Hash( s + g_seed ) );

		float u = ( float( s % across ) + random.Next() ) / float( across );
		float v = ( float( s / across ) + random.Next() ) / float( across );

		float3 direction = AnyDirection( u, v );

		bool behind;
		bool escaped;
		float3 arriving = Arriving( at, direction, glowing, random, behind, escaped );

		if ( escaped ) open += float4( direction, 1.0 ) * g_firstVeil;

		sum.xyz += arriving;
		red.xyz += direction * arriving.x;
		green.xyz += direction * arriving.y;
		blue.xyz += direction * arriving.z;
		red.w += behind ? 1.0 : 0.0;

		//And a glowing triangle straight from the point - BakeVolume.Trace
		if ( glowing > 0.0 )
		{
			float3 aim;
			float3 glow = TowardsGlow( at, float3( 0.0, 0.0, 0.0 ), float3( 0.0, 0.0, 0.0 ), false, random, aim ) * glowing;

			sum.xyz += glow;
			red.xyz += aim * glow.x;
			green.xyz += aim * glow.y;
			blue.xyz += aim * glow.z;
		}
	}

	g_sums[( point * 5 ) + 0] = sum;
	g_sums[( point * 5 ) + 1] = red;
	g_sums[( point * 5 ) + 2] = green;
	g_sums[( point * 5 ) + 3] = blue;
	g_sums[( point * 5 ) + 4] = open;
}
