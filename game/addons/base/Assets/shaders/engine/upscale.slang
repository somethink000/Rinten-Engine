//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Upscale passes: TemporalCs (TAA/upscaler), EasuCs (FSR1; its RCAS is in compose.slang), MotionCs (full motion),
//ReactiveCs (where glass and sprites changed the world, which a history should not be trusted with)

#include "postprocess.slang"

//What is stored into: the motion pass's picture is two half floats, every other pass's four - and a stored picture
//has to be declared in its own format, so the motion pass is its own build. See CoreUpscaler.MotionDefines.
#if defined( UPSCALE_MOTION )
[[vk::binding(0,2)]] [format("rg16f")] RWTexture2D<float4> g_out;
#elif defined( UPSCALE_SINGLE )
[[vk::binding(0,2)]] [format("r16f")] RWTexture2D<float4> g_out;
#else
[[vk::binding(0,2)]] [format("rgba16f")] RWTexture2D<float4> g_out;
#endif

//The size of the picture being read.
float2 InSize() { return g_vFrameParams.xy; }

//Where the world was drawn from this frame, in its own pixels: the geometry sits this far right and down of where it would with no jitter at all.
float2 Jitter() { return g_vFrameRange.zw; }

uint Flags() { return uint( g_vFrameParams.z ); }

static const uint FlagReset = 1u;
static const uint FlagHasMotion = 2u;

//Slot is uniform across the pass, so don't mark it non-uniform (that turned reads into loops)
float4 Texel( float slot, int2 at, float2 size )
{
	int index = int( slot );
	if ( index <= 0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	return g_textures[uint( index )].Load( int3( at, 0 ) );
}

float4 At( float slot, float2 uv )
{
	int index = int( slot );
	if ( index <= 0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	return g_textures[uint( index )].SampleLevel( uv, 0.0 );
}

float Max3( float3 c ) { return max( c.x, max( c.y, c.z ) ); }

//Squeezed into [0, 1) for everything that compares or averages colours - a lamp is a hundred times a wall in linear light, and a filter
//fed that finds the lamp everywhere. Undone exactly by Unsqueeze. Exposed first, so the squeeze bends where the eye will see the bend.
float3 Squeeze( float3 c )
{
	float3 exposed = max( c, 0.0 ) * Exposure();
	return exposed / ( 1.0 + Max3( exposed ) );
}

float3 Unsqueeze( float3 s )
{
	float3 exposed = s / max( 1.0 - Max3( s ), 1.0 / 65504.0 );
	return exposed / max( Exposure(), 1e-8 );
}

float3 ToYCoCg( float3 c )
{
	return float3(
		dot( c, float3( 0.25, 0.5, 0.25 ) ),
		dot( c, float3( 0.5, 0.0, -0.5 ) ),
		dot( c, float3( -0.25, 0.5, -0.25 ) ) );
}

float3 FromYCoCg( float3 c )
{
	return float3( c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z );
}

//---- motion ------------------------------------------------------------------

//UV offset to last frame: written motion if any, else reprojected via depth; jitter isn't motion
float2 BackwardsAt( int2 texel, float2 size )
{
	float4 written = ( Flags() & FlagHasMotion ) != 0u ? Texel( g_vFramePictures.w, texel, size ) : float4( 0.0, 0.0, 0.0, 0.0 );
	return BackwardsFrom( texel, size, Texel( g_vFramePictures.z, texel, size ).r, written );
}

//The same, from the texel's depth and written motion already in hand.
float2 BackwardsFrom( int2 texel, float2 size, float depth, float4 written )
{
	float2 uv = ( float2( texel ) + 0.5 ) / size;

	if ( written.a > 0.5 ) return -written.xy;

	float raw = min( depth, 1.0 );

	float4 clip = float4( ( uv * 2.0 ) - 1.0, raw, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	if ( abs( world.w ) < 1e-8 ) return float2( 0.0, 0.0 );

	float4 before = mul( float4( world.xyz / world.w, 1.0 ), EYE_FRAME( previousWorldToClip ) );

	if ( before.w <= 1e-6 ) return float2( 0.0, 0.0 );

	float2 was = ( ( before.xy / before.w ) * 0.5 ) + 0.5;

	return was - uv;
}

//BackwardsFrom for the temporal pass: one matrix from this frame's clip space to the last's, three of its columns in the push block
float2 Reprojected( int2 texel, float2 size, float depth, float4 written )
{
	float2 uv = ( float2( texel ) + 0.5 ) / size;

	if ( written.a > 0.5 ) return -written.xy;

	float4 clip = float4( ( uv * 2.0 ) - 1.0, min( depth, 1.0 ), 1.0 );
	float3 before = float3( dot( clip, g_vPostSlots[1] ), dot( clip, g_vPostSlots[2] ), dot( clip, g_vPostSlots[3] ) );

	if ( before.z <= 1e-6 ) return float2( 0.0, 0.0 );

	return ( ( ( before.xy / before.z ) * 0.5 ) + 0.5 ) - uv;
}

[numthreads(8, 8, 1)]
void MotionCs( uint3 id : SV_DispatchThreadID )
{
	float2 size = g_vFrameSize.xy;
	if ( any( float2( id.xy ) >= size ) ) return;

	g_out[id.xy] = float4( BackwardsAt( int2( id.xy ), size ), 0.0, 0.0 );
}

//---- a copy ------------------------------------------------------------------

//The world's picture into half floats, texel for texel - for DLSS, which takes no smaller format; see CoreDlss.
[numthreads(8, 8, 1)]
void CopyCs( uint3 id : SV_DispatchThreadID )
{
	float2 size = g_vFrameSize.xy;
	if ( any( float2( id.xy ) >= size ) ) return;

	g_out[id.xy] = float4( Texel( g_vFramePictures.x, int2( id.xy ), size ).rgb, 1.0 );
}

//---- reactivity --------------------------------------------------------------

//How much of a texel what was laid over the opaque world changed, as AMD's generated reactive mask: the largest channel's
//step as the eye sees it, nothing under a small one, at most 0.9 so a texel never forgets its history whole
[numthreads(8, 8, 1)]
void ReactiveCs( uint3 id : SV_DispatchThreadID )
{
	float2 size = g_vFrameSize.xy;
	if ( any( float2( id.xy ) >= size ) ) return;

	float3 final = Squeeze( Texel( g_vFramePictures.x, int2( id.xy ), size ).rgb );
	float3 opaque = Squeeze( Texel( g_vFramePictures.y, int2( id.xy ), size ).rgb );

	float step = Max3( abs( final - opaque ) );

	g_out[id.xy] = float4( saturate( ( step - 0.02 ) * 4.0 ) * 0.9, 0.0, 0.0, 0.0 );
}

//---- the temporal pass -------------------------------------------------------

//The history at a point, by Catmull-Rom - nine taps' worth in five bilinear ones. Bilinear alone blurs a
//little every frame it is carried, and a history is carried every frame; this keeps it sharp while it moves.
float4 HistoryAt( float2 uv, float2 size )
{
	float2 place = uv * size;
	float2 centre = floor( place - 0.5 ) + 0.5;
	float2 f = place - centre;

	float2 w0 = f * ( -0.5 + f * ( 1.0 - 0.5 * f ) );
	float2 w1 = 1.0 + f * f * ( -2.5 + 1.5 * f );
	float2 w2 = f * ( 0.5 + f * ( 2.0 - 1.5 * f ) );
	float2 w3 = f * f * ( -0.5 + 0.5 * f );

	float2 w12 = w1 + w2;
	float2 middle = centre + w2 / w12;

	float2 texel = 1.0 / size;
	float2 t0 = ( centre - 1.0 ) * texel;
	float2 t3 = ( centre + 2.0 ) * texel;
	float2 t12 = middle * texel;

	float slot = g_vFramePictures.y;

	float4 sum = 0.0;
	float weight = 0.0;

	float w;

	w = w12.x * w0.y; sum += At( slot, float2( t12.x, t0.y ) ) * w; weight += w;
	w = w0.x * w12.y; sum += At( slot, float2( t0.x, t12.y ) ) * w; weight += w;
	w = w12.x * w12.y; sum += At( slot, float2( t12.x, t12.y ) ) * w; weight += w;
	w = w3.x * w12.y; sum += At( slot, float2( t3.x, t12.y ) ) * w; weight += w;
	w = w12.x * w3.y; sum += At( slot, float2( t12.x, t3.y ) ) * w; weight += w;

	float4 result = sum / max( weight, 1e-5 );

	//The negative lobes may ring below nothing around a bright edge.
	return float4( max( result.rgb, 0.0 ), result.a );
}

//Pulls the history towards the middle of the box this frame's neighbourhood spans until it is inside it - what the history
//may still say, given what is there now. Along the line to the middle rather than per channel, which would change its hue.
float3 ClipTowards( float3 history, float3 low, float3 high )
{
	float3 middle = 0.5 * ( high + low );
	float3 extent = 0.5 * ( high - low ) + 1e-5;

	float3 away = history - middle;
	float3 units = abs( away / extent );
	float most = Max3( units );

	return most > 1.0 ? middle + away / most : history;
}

//Group-shared 10x10 tile of world texels, loaded once per 8x8 group instead of per pixel
static const int TileSide = 10;
groupshared float4 g_tileColour[TileSide * TileSide];   //squeezed colour, and depth

[numthreads(8, 8, 1)]
void TemporalCs( uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint thread : SV_GroupIndex )
{
	float2 outSize = g_vFrameSize.xy;
	float2 inSize = InSize();
	int2 last = int2( inSize ) - 1;

	//The tile's corner: one texel before the nearest the group's first pixel reaches. Loaded by every thread, the group's edge ones included,
	//before any of them returns. Its colour squeezed and turned into YCoCg once here, not nine times over by every pixel that reads it.
	float2 firstPlace = ( ( float2( group.xy * 8u ) + 0.5 ) / outSize * inSize ) + Jitter();
	int2 corner = int2( floor( firstPlace ) ) - 1;

	for ( uint t = thread; t < uint( TileSide * TileSide ); t += 64u )
	{
		int2 at = clamp( corner + int2( int( t ) % TileSide, int( t ) / TileSide ), int2( 0, 0 ), last );

		g_tileColour[t] = float4( ToYCoCg( Squeeze( Texel( g_vFramePictures.x, at, inSize ).rgb ) ), Texel( g_vFramePictures.z, at, inSize ).r );
	}

	GroupMemoryBarrierWithGroupSync();

	if ( any( float2( id.xy ) >= outSize ) ) return;

	float2 uv = ( float2( id.xy ) + 0.5 ) / outSize;

	//Where the thing this output pixel shows was drawn in the world's picture: a texel's centre
	//is where its sample landed, and the whole picture sits the jitter away from where it would.
	float2 place = ( uv * inSize ) + Jitter();
	int2 nearest = int2( floor( place ) );

	float4 numbers = g_vPostSlots[0];
	float most = numbers.x;       //how much a pixel may have gathered
	float spread = numbers.y;     //how wide the box the history must stay in is
	float sharp = numbers.z;      //how narrow a sample's reach is

	//A sample's weight falls off with its distance squared, which splits into one along x times one along
	//y: six exponentials rather than nine. The picture's edge repeats its last texel, as the tile does.
	float2 off = place - ( float2( nearest ) + 0.5 );
	float k = -2.29 * sharp * 1.442695;
	float3 wx = exp2( k * float3( ( off.x + 1.0 ) * ( off.x + 1.0 ), off.x * off.x, ( off.x - 1.0 ) * ( off.x - 1.0 ) ) );
	float3 wy = exp2( k * float3( ( off.y + 1.0 ) * ( off.y + 1.0 ), off.y * off.y, ( off.y - 1.0 ) * ( off.y - 1.0 ) ) );

	int2 base = clamp( nearest - corner, int2( 1, 1 ), int2( TileSide - 2, TileSide - 2 ) );
	int middle = ( base.y * TileSide ) + base.x;

	float3 sum = 0.0;
	float total = 0.0;
	float closest = 0.0;
	float3 m1 = 0.0;
	float3 m2 = 0.0;

	//And which of the nine drew the nearest surface, whose motion is the one followed: an edge moves with the thing in front, not with what it hides.
	float nearestDepth = 2.0;
	int2 nearestStep = int2( 0, 0 );

	[unroll]
	for ( int y = -1; y <= 1; y++ )
	{
		[unroll]
		for ( int x = -1; x <= 1; x++ )
		{
			int slot = middle + ( y * TileSide ) + x;
			float4 held = g_tileColour[slot];
			float w = wx[x + 1] * wy[y + 1];

			sum += held.rgb * w;
			total += w;
			closest = max( closest, w );
			m1 += held.rgb;
			m2 += held.rgb * held.rgb;

			if ( held.a < nearestDepth )
			{
				nearestDepth = held.a;
				nearestStep = int2( x, y );
			}
		}
	}

	float3 current = sum / max( total, 1e-5 );

	//Its written motion read for it alone, not for the whole tile: one texel a pixel instead of the tile's half again as many
	int2 nearestAt = clamp( nearest + nearestStep, int2( 0, 0 ), last );
	float4 written = ( Flags() & FlagHasMotion ) != 0u ? Texel( g_vFramePictures.w, nearestAt, inSize ) : float4( 0.0, 0.0, 0.0, 0.0 );
	float2 backwards = Reprojected( nearestAt, inSize, nearestDepth, written );
	float2 was = uv + backwards;

	bool reset = ( Flags() & FlagReset ) != 0u;
	bool onScreen = all( was > 0.0 ) && all( was < 1.0 );

	float gathered = 0.0;
	float3 result = current;

	if ( !reset && onScreen )
	{
		//Catmull-Rom only where the history is read between its texels; a pixel that stood still reads its own one, and one tap is exact there
		float2 between = abs( frac( was * outSize ) - 0.5 );
		float4 history = all( between < 0.01 ) ? At( g_vFramePictures.y, was ) : HistoryAt( was, outSize );

		gathered = min( history.a, most );

		//The box the neighbourhood spans, as a mean and a spread rather than a minimum and
		//a maximum - one bright texel of noise widens a min/max box until it holds anything.
		float3 mean = m1 / 9.0;
		float3 sigma = sqrt( max( ( m2 / 9.0 ) - ( mean * mean ), 0.0 ) );

		float3 wanted = ToYCoCg( Squeeze( history.rgb ) );
		float3 clipped = ClipTowards( wanted, mean - spread * sigma, mean + spread * sigma );

		//How far it had to be pulled is how much of what it said was about something no longer there -
		//a thing that moved away, a light that went out. That much of what it had gathered is forgotten.
		float moved = length( clipped - wanted ) / max( clipped.x + 0.02, 0.02 );
		gathered *= saturate( 1.0 - moved * 2.0 );

		//And a history carried a long way has been resampled every frame of the way: it keeps less of itself the faster the pixel goes.
		float speed = length( backwards * outSize );
		gathered *= 1.0 / ( 1.0 + speed * 0.05 );

		//Glass, particles and sprites move over what the motion follows: where they changed the picture, the history counts less
		if ( g_vFrameParams.w > 0.5 ) gathered *= 1.0 - Texel( g_vFrameParams.w, clamp( nearest, int2( 0, 0 ), last ), inSize ).r;

		//This frame's samples count by how close they fell to this pixel - a sample right
		//on it is a whole frame's worth, one between output pixels a fraction of one.
		result = lerp( clipped, current, closest / ( gathered + closest ) );
	}

	g_out[id.xy] = float4( Unsqueeze( saturate( FromYCoCg( result ) ) ), min( gathered + closest, most ) );
}

//---- FSR1 --------------------------------------------------------------------

//One tap of the edge-adaptive upscale - see compose.slang's UpscaleTap, of which this is a copy for the pass.
void EasuTap( inout float3 sum, inout float weight, float2 offset, float2 direction,
	float2 stretched, float reach, float clip, float3 colour )
{
	float2 turned;

	turned.x = ( offset.x * direction.x ) + ( offset.y * direction.y );
	turned.y = ( offset.x * -direction.y ) + ( offset.y * direction.x );

	turned *= stretched;

	float distance = min( ( turned.x * turned.x ) + ( turned.y * turned.y ), clip );

	float wide = ( ( 2.0 / 5.0 ) * distance ) - 1.0;
	wide = ( wide * wide * ( 25.0 / 16.0 ) ) - ( ( 25.0 / 16.0 ) - 1.0 );

	float narrow = ( reach * distance ) - 1.0;
	narrow = narrow * narrow;

	float w = wide * narrow;

	sum += colour * w;
	weight += w;
}

void EasuEdge( float2 frac, float a, float b, float c, float d, float e,
	bool s, bool t, bool u, bool v, inout float2 direction, inout float length )
{
	float w = 0.0;

	if ( s ) w = ( 1.0 - frac.x ) * ( 1.0 - frac.y );
	if ( t ) w = frac.x * ( 1.0 - frac.y );
	if ( u ) w = ( 1.0 - frac.x ) * frac.y;
	if ( v ) w = frac.x * frac.y;

	float rightward = d - c;
	float leftward = c - b;

	float acrossLength = max( abs( rightward ), abs( leftward ) );
	acrossLength = acrossLength > 0.0 ? 1.0 / acrossLength : 0.0;

	float across = d - b;
	direction.x += across * w;

	float measured = saturate( abs( across ) * acrossLength );
	length += measured * measured * w;

	float downward = e - c;
	float upward = c - a;

	float downLength = max( abs( downward ), abs( upward ) );
	downLength = downLength > 0.0 ? 1.0 / downLength : 0.0;

	float down = e - a;
	direction.y += down * w;

	measured = saturate( abs( down ) * downLength );
	length += measured * measured * w;
}

//One texel of the world, exposed - see compose.slang's Tap.
float3 EasuTexel( float2 uv )
{
	return At( g_vFramePictures.x, uv ).rgb * Exposure();
}

[numthreads(8, 8, 1)]
void EasuCs( uint3 id : SV_DispatchThreadID )
{
	float2 outSize = g_vFrameSize.xy;
	if ( any( float2( id.xy ) >= outSize ) ) return;

	float2 source = InSize();
	float2 uv = ( float2( id.xy ) + 0.5 ) / outSize;
	float2 texel = 1.0 / source;

	float2 place = ( uv * source ) - 0.5;
	float2 corner = floor( place );
	float2 part = place - corner;

	float2 middle = ( corner + 0.5 ) * texel;

	float3 b = EasuTexel( middle + ( float2(  0.0, -1.0 ) * texel ) );
	float3 c = EasuTexel( middle + ( float2(  1.0, -1.0 ) * texel ) );
	float3 e = EasuTexel( middle + ( float2( -1.0,  0.0 ) * texel ) );
	float3 f = EasuTexel( middle );
	float3 g = EasuTexel( middle + ( float2(  1.0,  0.0 ) * texel ) );
	float3 h = EasuTexel( middle + ( float2(  2.0,  0.0 ) * texel ) );
	float3 i = EasuTexel( middle + ( float2( -1.0,  1.0 ) * texel ) );
	float3 j = EasuTexel( middle + ( float2(  0.0,  1.0 ) * texel ) );
	float3 k = EasuTexel( middle + ( float2(  1.0,  1.0 ) * texel ) );
	float3 l = EasuTexel( middle + ( float2(  2.0,  1.0 ) * texel ) );
	float3 n = EasuTexel( middle + ( float2(  0.0,  2.0 ) * texel ) );
	float3 o = EasuTexel( middle + ( float2(  1.0,  2.0 ) * texel ) );

	float2 direction = float2( 0.0, 0.0 );
	float length = 0.0;

	EasuEdge( part, b.g, e.g, f.g, g.g, j.g, true,  false, false, false, direction, length );
	EasuEdge( part, c.g, f.g, g.g, h.g, k.g, false, true,  false, false, direction, length );
	EasuEdge( part, f.g, i.g, j.g, k.g, n.g, false, false, true,  false, direction, length );
	EasuEdge( part, g.g, j.g, k.g, l.g, o.g, false, false, false, true,  direction, length );

	float2 axis = direction * direction;
	float strength = axis.x + axis.y;

	if ( strength < ( 1.0 / 32768.0 ) )
	{
		direction = float2( 1.0, 0.0 );
		strength = 1.0;
	}
	else
	{
		strength = sqrt( strength );
		direction /= strength;
	}

	length = length * 0.5;
	length = length * length;

	float longest = max( abs( direction.x ), abs( direction.y ) );
	float stretch = ( ( direction.x * direction.x ) + ( direction.y * direction.y ) ) / max( longest, 0.0001 );

	float2 stretched = float2( 1.0 + ( ( stretch - 1.0 ) * length ), 1.0 - ( 0.5 * length ) );

	float reach = 0.5 + ( ( ( 1.0 / 4.0 ) - 0.04 ) - 0.5 ) * length;
	float clip = 1.0 / max( reach, 0.0001 );

	float3 sum = float3( 0.0, 0.0, 0.0 );
	float weight = 0.0;

	EasuTap( sum, weight, float2(  0.0, -1.0 ) - part, direction, stretched, reach, clip, b );
	EasuTap( sum, weight, float2(  1.0, -1.0 ) - part, direction, stretched, reach, clip, c );
	EasuTap( sum, weight, float2( -1.0,  0.0 ) - part, direction, stretched, reach, clip, e );
	EasuTap( sum, weight, float2(  0.0,  0.0 ) - part, direction, stretched, reach, clip, f );
	EasuTap( sum, weight, float2(  1.0,  0.0 ) - part, direction, stretched, reach, clip, g );
	EasuTap( sum, weight, float2(  2.0,  0.0 ) - part, direction, stretched, reach, clip, h );
	EasuTap( sum, weight, float2( -1.0,  1.0 ) - part, direction, stretched, reach, clip, i );
	EasuTap( sum, weight, float2(  0.0,  1.0 ) - part, direction, stretched, reach, clip, j );
	EasuTap( sum, weight, float2(  1.0,  1.0 ) - part, direction, stretched, reach, clip, k );
	EasuTap( sum, weight, float2(  2.0,  1.0 ) - part, direction, stretched, reach, clip, l );
	EasuTap( sum, weight, float2(  0.0,  2.0 ) - part, direction, stretched, reach, clip, n );
	EasuTap( sum, weight, float2(  1.0,  2.0 ) - part, direction, stretched, reach, clip, o );

	float3 result = sum / max( weight, 0.0001 );

	float3 low = min( min( f, g ), min( j, k ) );
	float3 high = max( max( f, g ), max( j, k ) );

	g_out[id.xy] = float4( clamp( result, low, high ) / max( Exposure(), 1e-8 ), 1.0 );
}
