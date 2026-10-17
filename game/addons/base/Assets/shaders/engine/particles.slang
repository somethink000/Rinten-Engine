//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//An effect's particles stepped on the card: what ParticleEffect.UpdateParticle and FinishParticle do to one, then the sprite
//IBatchedParticleSpriteRenderer would make of it - for an effect whose every part has a version here. See ParticleGpuRun.

//One particle, ten float4s - see ParticleGpuRun.Packed
struct Particle
{
	float4 place;    //Position, seconds of its life gone
	float4 motion;   //Velocity, seconds it lives; nought for a slot with nothing in it
	float4 size;     //Size as it was made, how fast its clock runs against the effect's
	float4 colour;   //Colour as it was made
	float4 start;    //Starting angles, starting scale
	float4 angles;   //Angles now, its random seed's bits
	float4 spin;     //Degrees a second its spin modules add, its alpha as made
	float4 last;     //The last velocity worth facing along, where it is among its emitter's places
	float4 overlay;  //Overlay colour
	float4 follow;   //Where on the shape it starts: its stagger and its place among the shape's, by FollowShapeModule
};

[[vk::binding(0,0)]] RWStructuredBuffer<Particle> g_particles;
[[vk::binding(1,0)]] RWByteAddressBuffer g_sprites;
[[vk::binding(2,0)]] StructuredBuffer<float> g_table;
[[vk::binding(3,0)]] StructuredBuffer<Particle> g_spawns;
[[vk::binding(4,0)]] StructuredBuffer<uint> g_spawnSlots;

//What the step wrote, for an effect drawn far to near: SortCs puts it into g_sprites in that order
[[vk::binding(5,0)]] RWByteAddressBuffer g_unsorted;

[[vk::push_constant]]
cbuffer Push
{
	uint g_capacity;
	uint g_spawnCount;

	//Seconds of the effect's clock this frame, and a number that changes every frame
	float g_dt;
	uint g_frame;

	//Bytes a sprite takes - see SpriteBatchSceneObject.SpriteData
	uint g_spriteStride;

	//Where the eye is and which way it looks, for an effect drawn far to near - see SortCs. Nought where it is not sorted
	float3 g_eye;
	uint g_sorted;
	float3 g_forward;
};

//Where in the table each thing is - see ParticleGpuRun.Table
static const uint TableGravity = 0;
static const uint TableCurl = 4;
static const uint TableCurlOffset = 8;
static const uint TableSprite = 12;
static const uint TableTint = 28;
static const uint TableApply = 32;
static const uint TableBlur = 40;
static const uint TableChannels = 44;
static const uint ChannelFloats = 8;

static const uint ChannelDamping = 0;
static const uint ChannelBrightness = 1;
static const uint ChannelAlpha = 2;
static const uint ChannelScale = 3;
static const uint ChannelStretch = 4;
static const uint ChannelPitch = 5;
static const uint ChannelYaw = 6;
static const uint ChannelRoll = 7;
static const uint ChannelAlong = 8;
static const uint TableGradient = TableChannels + ( 9 * ChannelFloats );
static const uint TableOrbit = TableGradient + 12;
static const uint TableAttract = TableOrbit + 8;
static const uint TableVortex = TableAttract + 8;
static const uint TableFollow = TableVortex + 8;
static const uint ShapeSamples = 256;

static const uint LutSamples = 64;

float T( uint at ) { return g_table[at]; }

uint Hash( uint x )
{
	x ^= x >> 16;
	x *= 0x7feb352du;
	x ^= x >> 15;
	x *= 0x846ca68bu;
	x ^= x >> 16;
	return x;
}

float Random01( uint seed, uint salt ) { return float( Hash( seed ^ ( salt * 0x9E3779B9u ) ) >> 8 ) / 16777216.0; }

//A baked curve read at x, nought to one
float Lut( uint at, float x )
{
	float place = saturate( x ) * float( LutSamples - 1 );
	uint i = min( uint( place ), LutSamples - 2 );

	return lerp( T( at + i ), T( at + i + 1 ), place - float( i ) );
}

//Where on a curve a value is read: its life, a number for the frame, its own number, or its place - ParticleFloat.EvaluationType
float ReadAt( float eval, float life, float index, uint seed, uint salt, float random )
{
	if ( eval < 0.5 ) return life;
	if ( eval < 1.5 ) return Random01( seed ^ ( g_frame * 7919u ), salt );
	if ( eval < 2.5 ) return random;
	return index;
}

//A ParticleFloat as ParticleGpuRun laid it: type, evaluation, two constants, two curves' places, its salt
float Channel( uint channel, float life, float index, uint seed )
{
	uint at = TableChannels + ( channel * ChannelFloats );
	float type = T( at );

	if ( type < 0.5 ) return T( at + 2 );

	uint salt = uint( T( at + 6 ) );
	float random = Random01( seed, salt );
	float d = ReadAt( T( at + 1 ), life, index, seed, salt, random );

	if ( type < 1.5 ) return lerp( T( at + 2 ), T( at + 3 ), d );
	if ( type < 2.5 ) return Lut( uint( T( at + 4 ) ), d );

	return lerp( Lut( uint( T( at + 4 ) ), d ), Lut( uint( T( at + 5 ) ), d ), random );
}

//The effect's gradient: type, evaluation, its baked colours' place, its salt, then two constant colours
float4 Gradient( float life, float index, uint seed )
{
	uint at = TableGradient;
	float type = T( at );
	float4 a = float4( T( at + 4 ), T( at + 5 ), T( at + 6 ), T( at + 7 ) );

	if ( type < 0.5 ) return a;

	uint salt = uint( T( at + 3 ) );
	float d = ReadAt( T( at + 1 ), life, index, seed, salt, Random01( seed, salt ) );

	if ( type < 1.5 ) return lerp( a, float4( T( at + 8 ), T( at + 9 ), T( at + 10 ), T( at + 11 ) ), d );

	float place = saturate( d ) * float( LutSamples - 1 );
	uint i = min( uint( place ), LutSamples - 2 );
	uint lut = uint( T( at + 2 ) ) + ( i * 4 );
	float4 low = float4( T( lut ), T( lut + 1 ), T( lut + 2 ), T( lut + 3 ) );
	float4 high = float4( T( lut + 4 ), T( lut + 5 ), T( lut + 6 ), T( lut + 7 ) );

	return lerp( low, high, place - float( i ) );
}

//Vector3.WithFriction with ParticleEffect's stop speed of a tenth of a metre a second
float3 Friction( float3 v, float amount )
{
	float speed = length( v );
	if ( speed < 0.01 ) return v;

	float control = max( speed, 0.1 );
	float slower = max( speed - ( control * amount ), 0.0 );

	return v * ( slower / speed );
}

//CurlNoiseModule's field, exactly - see its Gradient
float3 NoiseGradient( float3 position, float time )
{
	float3 p = position + float3( time, time * 0.7, time * 0.5 );

	float a = cos( p.x + p.y * 1.3 ) * 0.5;
	float b = cos( p.y * 1.7 + p.z * 0.9 ) * 0.3;
	float c = cos( p.z * 2.1 + p.x * 1.1 ) * 0.2;
	float d = cos( p.x * 3.7 + p.y * 2.3 + p.z * 1.9 ) * 0.15;

	return float3( a + ( c * 1.1 ) + ( d * 3.7 ), ( a * 1.3 ) + ( b * 1.7 ) + ( d * 2.3 ), ( b * 0.9 ) + ( c * 2.1 ) + ( d * 1.9 ) );
}

//The shape followed at t, nought to one: its baked poses in its own frame, then the frame it is in this step
float3 OnShape( float t )
{
	float place = saturate( t ) * float( ShapeSamples - 1 );
	uint i = min( uint( place ), ShapeSamples - 2 );
	uint at = uint( T( TableFollow + 15 ) ) + ( i * 3 );

	float3 low = float3( T( at ), T( at + 1 ), T( at + 2 ) );
	float3 high = float3( T( at + 3 ), T( at + 4 ), T( at + 5 ) );
	float3 local = lerp( low, high, place - float( i ) ) * float3( T( TableFollow + 12 ), T( TableFollow + 13 ), T( TableFollow + 14 ) );

	float4 q = float4( T( TableFollow + 8 ), T( TableFollow + 9 ), T( TableFollow + 10 ), T( TableFollow + 11 ) );
	float3 turned = local + ( 2.0 * cross( q.xyz, cross( q.xyz, local ) + ( q.w * local ) ) );

	return float3( T( TableFollow + 4 ), T( TableFollow + 5 ), T( TableFollow + 6 ) ) + turned;
}

//A vector turned about a unit axis by an angle in radians, by Rodrigues' formula
float3 Turn( float3 v, float3 axis, float angle )
{
	float s = sin( angle );
	float c = cos( angle );

	return ( v * c ) + ( cross( axis, v ) * s ) + ( axis * ( dot( axis, v ) * ( 1.0 - c ) ) );
}

float3 Curl( float3 position, float time )
{
	float3 x = NoiseGradient( position, time );
	float3 y = NoiseGradient( position + float3( 31.416, -47.853, 12.793 ), time );
	float3 z = NoiseGradient( position + float3( -17.737, 86.214, -59.482 ), time );

	return float3( z.y - y.z, x.z - z.x, y.x - x.y );
}

//Color.ToRgbe: three signed bytes over a shared exponent byte
uint4 Rgbe( float3 c )
{
	float most = max( abs( c.r ), max( abs( c.g ), abs( c.b ) ) );
	if ( most < 1e-16 ) return uint4( 0, 0, 0, 0 );

	int exponent = int( ( asuint( most ) >> 23 ) & 0xFFu ) - 126;
	float scale = asfloat( uint( 134 - exponent ) << 23 );
	int3 q = clamp( int3( ( c * scale ) + 0.5 ), -128, 127 ) + 128;

	return uint4( uint3( q ), uint( clamp( exponent, -128, 127 ) + 128 ) );
}

uint Pack( uint4 bytes ) { return bytes.x | ( bytes.y << 8 ) | ( bytes.z << 16 ) | ( bytes.w << 24 ); }

//A sorted effect's step writes aside, and SortCs moves each sprite to its place in the order
void Put( uint at, uint word )
{
	if ( g_sorted != 0u ) g_unsorted.Store( at * 4, word );
	else g_sprites.Store( at * 4, word );
}

void PutF( uint at, float value ) { Put( at, asuint( value ) ); }

//A sprite drawn as nothing: no size, no alpha
void Nothing( uint sprite )
{
	PutF( sprite + 6, 0.0 );
	PutF( sprite + 7, 0.0 );
	Put( sprite + 8, 0u );
}

//The particle as IBatchedParticleSpriteRenderer.ProcessParticlesDirectly writes it - word by word, see SpriteBatchSceneObject.SpriteData
void Sprite( uint sprite, Particle p, float3 velocity, float4 colour, float alpha, float3 size )
{
	float scale = T( TableSprite );
	float aspect = T( TableSprite + 1 );

	uint4 tint = Rgbe( colour.rgb );
	uint4 overlay = Rgbe( p.overlay.rgb );

	PutF( sprite + 0, p.place.x );
	PutF( sprite + 1, p.place.y );
	PutF( sprite + 2, p.place.z );
	PutF( sprite + 3, p.angles.x );
	PutF( sprite + 4, p.angles.y );
	PutF( sprite + 5, p.angles.z );
	PutF( sprite + 6, size.x * scale * aspect );
	PutF( sprite + 7, size.y * scale );
	Put( sprite + 8, Pack( uint4( tint.xyz, uint( saturate( colour.a * alpha ) * 255.0 ) ) ) );
	Put( sprite + 9, Pack( uint4( overlay.xyz, uint( saturate( p.overlay.a ) * 255.0 ) ) ) );
	Put( sprite + 10, asuint( T( TableSprite + 3 ) ) );
	Put( sprite + 11, asuint( T( TableSprite + 4 ) ) );
	Put( sprite + 12, uint( T( TableSprite + 2 ) ) );
	Put( sprite + 13, asuint( T( TableSprite + 5 ) ) );
	Put( sprite + 14, uint( T( TableSprite + 6 ) ) | ( tint.w << 16 ) );
	PutF( sprite + 15, T( TableSprite + 7 ) );
	Put( sprite + 16, 0u );
	Put( sprite + 17, 0u );
	PutF( sprite + 18, 0.0 );
	PutF( sprite + 19, T( TableSprite + 9 ) );
	PutF( sprite + 20, T( TableBlur ) );
	PutF( sprite + 21, T( TableBlur + 1 ) );
	PutF( sprite + 22, T( TableBlur + 2 ) );
	PutF( sprite + 23, T( TableBlur + 3 ) );
	PutF( sprite + 24, velocity.x );
	PutF( sprite + 25, velocity.y );
	PutF( sprite + 26, velocity.z );
	PutF( sprite + 27, T( TableSprite + 12 ) );
	PutF( sprite + 28, T( TableSprite + 13 ) );
	PutF( sprite + 29, T( TableSprite + 14 ) );
	PutF( sprite + 30, T( TableSprite + 15 ) );
	PutF( sprite + 31, T( TableSprite + 10 ) );
	PutF( sprite + 32, T( TableSprite + 11 ) );
	Put( sprite + 33, asuint( T( TableSprite + 8 ) ) );
	PutF( sprite + 34, 0.0 );
	PutF( sprite + 35, 0.0 );
	PutF( sprite + 36, 1.0 );
	PutF( sprite + 37, 1.0 );
}

//This frame's new ones into their slots, a thread each, before the step
[shader("compute")]
[numthreads(64, 1, 1)]
void SpawnCs( uint3 id : SV_DispatchThreadID )
{
	if ( id.x >= g_spawnCount ) return;

	uint slot = g_spawnSlots[id.x];
	if ( slot < g_capacity ) g_particles[slot] = g_spawns[id.x];
}

[shader("compute")]
[numthreads(64, 1, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	uint slot = id.x;
	if ( slot >= g_capacity ) return;

	Particle p = g_particles[slot];

	uint sprite = slot * ( g_spriteStride / 4 );

	if ( p.motion.w <= 0.0 )
	{
		g_particles[slot] = p;
		Nothing( sprite );
		return;
	}

	uint seed = asuint( p.angles.w );
	float index = p.last.w;

	float ts = g_dt * p.size.w;
	float life = p.place.w / p.motion.w;

	p.place.w += ts;

	//Past its end: gone before it is drawn, as PostStep takes it out
	if ( life >= 1.0 )
	{
		p.motion.w = 0.0;
		g_particles[slot] = p;
		Nothing( sprite );
		return;
	}

	float3 velocity = Friction( p.motion.xyz, Channel( ChannelDamping, life, index, seed ) * ts );

	//The modules: gravity summed, spin, curl noise - what ParticleGpuRun let through
	velocity += float3( T( TableGravity ), T( TableGravity + 1 ), T( TableGravity + 2 ) ) * ts;
	p.angles.xyz += p.spin.xyz * ts;

	if ( T( TableCurl + 3 ) > 0.5 )
	{
		float3 at = ( p.place.xyz + float3( T( TableCurlOffset ), T( TableCurlOffset + 1 ), T( TableCurlOffset + 2 ) ) ) * T( TableCurl + 1 );
		velocity += Curl( at, p.place.w * T( TableCurl + 2 ) ) * T( TableCurl ) * ts;
	}

	//On a shape: where the shape is at its place along it, drawn there by the weight, and moving by what that took - FollowShapeModule
	if ( T( TableFollow ) > 0.5 )
	{
		float along = ( Channel( ChannelAlong, life, index, seed ) * T( TableFollow + 7 ) ) + p.follow.x + ( p.place.w * T( TableFollow + 2 ) );
		float t = p.follow.y + along;
		t = T( TableFollow + 1 ) > 0.5 ? t - floor( t ) : saturate( t );

		float3 next = lerp( p.place.xyz, OnShape( t ), saturate( T( TableFollow + 3 ) ) );

		p.motion.xyz = ts > 0.0 ? ( next - p.place.xyz ) / ts : velocity;
		p.place.xyz = next;
		velocity = p.motion.xyz;
	}
	else
	{
		//An attractor pulls with all its strength inside its size and less past it - AttractorModule
		if ( T( TableAttract + 3 ) > 0.5 )
		{
			float3 toward = float3( T( TableAttract ), T( TableAttract + 1 ), T( TableAttract + 2 ) ) - p.place.xyz;
			float distance = length( toward );

			if ( distance >= 0.01 )
			{
				float size = T( TableAttract + 5 );
				float pull = distance > size ? 1.0 / pow( 1.0 + ( ( distance - size ) / size ), T( TableAttract + 6 ) ) : 1.0;
				float side = T( TableAttract + 7 ) > 0.5 ? -1.0 : 1.0;

				velocity += ( toward / distance ) * side * T( TableAttract + 4 ) * pull * ts;
			}
		}

		//A vortex swirls about an axis, harder nearer it - VortexModule
		if ( T( TableVortex + 3 ) > 0.5 )
		{
			float3 outward = p.place.xyz - float3( T( TableVortex ), T( TableVortex + 1 ), T( TableVortex + 2 ) );
			float distance = length( outward );

			if ( distance >= 0.01 )
			{
				float3 axis = float3( T( TableVortex + 4 ), T( TableVortex + 5 ), T( TableVortex + 6 ) );
				velocity += cross( axis, outward / distance ) * ( T( TableVortex + 7 ) / distance ) * ts;
			}
		}

		//An orbit turns where it is and where it goes about an axis through a point, Rotation.FromAxis's way round
		if ( T( TableOrbit + 3 ) > 0.5 )
		{
			float3 centre = float3( T( TableOrbit ), T( TableOrbit + 1 ), T( TableOrbit + 2 ) );
			float3 axis = float3( T( TableOrbit + 4 ), T( TableOrbit + 5 ), T( TableOrbit + 6 ) );
			float angle = radians( T( TableOrbit + 7 ) * ts );

			p.place.xyz = centre + Turn( p.place.xyz - centre, axis, angle );
			velocity = Turn( velocity, axis, angle );
		}

		p.place.xyz += velocity * ts;
		p.motion.xyz = velocity;
	}

	//FinishParticle: colour, alpha, size, turn - each only where the effect applies it
	float4 colour = p.colour;
	float alpha = p.spin.w;
	float3 size = p.size.xyz;

	if ( T( TableApply ) > 0.5 )
	{
		float4 tint = float4( T( TableTint ), T( TableTint + 1 ), T( TableTint + 2 ), T( TableTint + 3 ) );
		colour = tint * Gradient( life, index, seed );
		colour.rgb *= Channel( ChannelBrightness, life, index, seed );
	}

	if ( T( TableApply + 1 ) > 0.5 ) alpha = Channel( ChannelAlpha, life, index, seed );

	if ( T( TableApply + 2 ) > 0.5 )
	{
		size = p.start.w * Channel( ChannelScale, life, index, seed );

		float aspect = Channel( ChannelStretch, life, index, seed );

		if ( aspect < 0.0 ) size.x *= 1.0 - aspect;
		else if ( aspect > 0.0 ) size.y *= 1.0 + aspect;
	}

	if ( T( TableApply + 3 ) > 0.5 )
	{
		p.angles.xyz = p.start.xyz + float3( Channel( ChannelPitch, life, index, seed ), Channel( ChannelYaw, life, index, seed ),
			Channel( ChannelRoll, life, index, seed ) );
	}

	//Kept facing the way it last went where the sprite turns to its motion - see ProcessParticlesDirectly
	float3 facing = velocity;

	if ( T( TableApply + 4 ) > 0.5 )
	{
		if ( dot( velocity, velocity ) > 1e-6 ) p.last.xyz = velocity;
		else facing = p.last.xyz;
	}

	g_particles[slot] = p;

	Sprite( sprite, p, facing, colour, alpha, size );
}

//-- far to near -------------------------------------------------------------------------------------------------------

static const uint SortMost = 1024;

//The depth along the view as a key that sorts the same way as unsigned numbers, nothing at all lowest
groupshared uint2 g_order[SortMost];

uint Key( float depth )
{
	uint bits = asuint( depth );
	return ( bits & 0x80000000u ) != 0u ? ~bits : bits | 0x80000000u;
}

//A whole effect of at most SortMost in one group: bitonic, furthest first, then each sprite copied to its place
[shader("compute")]
[numthreads(1024, 1, 1)]
void SortCs( uint i : SV_GroupIndex )
{
	uint key = 0u;

	if ( i < g_capacity )
	{
		Particle p = g_particles[i];
		if ( p.motion.w > 0.0 ) key = Key( dot( p.place.xyz - g_eye, g_forward ) );
	}

	g_order[i] = uint2( key, i );
	GroupMemoryBarrierWithGroupSync();

	for ( uint k = 2u; k <= SortMost; k <<= 1 )
	{
		for ( uint j = k >> 1; j > 0u; j >>= 1 )
		{
			uint other = i ^ j;

			if ( other > i )
			{
				uint2 a = g_order[i];
				uint2 b = g_order[other];

				//Descending where the run is going down, ascending where up: the whole ends furthest first
				bool down = ( i & k ) == 0u;

				if ( down ? a.x < b.x : a.x > b.x )
				{
					g_order[i] = b;
					g_order[other] = a;
				}
			}

			GroupMemoryBarrierWithGroupSync();
		}
	}

	if ( i >= g_capacity ) return;

	uint words = g_spriteStride / 4;
	uint source = g_order[i].y;
	uint into = i * words;

	//A slot past the effect's own, which only pads the sort out to a power of two: nothing to draw here
	if ( source >= g_capacity )
	{
		g_sprites.Store( ( into + 6 ) * 4, 0u );
		g_sprites.Store( ( into + 7 ) * 4, 0u );
		g_sprites.Store( ( into + 8 ) * 4, 0u );
		return;
	}

	uint from = source * words;

	for ( uint w = 0u; w < words; w++ ) g_sprites.Store( ( into + w ) * 4, g_unsorted.Load( ( from + w ) * 4 ) );
}
