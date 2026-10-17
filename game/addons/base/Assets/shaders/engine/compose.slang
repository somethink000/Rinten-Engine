//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader
//is - includes and all - and a project may put its own file of this name in its assets to replace it.

//Composes the frame to screen: point-effect chain (incl. tonemap) around the edge filter

#include "postprocess.slang"
#include "tonemap.slang"

#ifndef ENGINE_POINT_CHAIN

//On its own - compiled with no chain in front of it - the frame's own curve and nothing laid over it: what
//a frame is drawn with where its chain could not be compiled, and there is no compiler to compile it.
float3 ChainBefore( float3 colour, float2 uv ) { return EngineTonemap( colour * Exposure(), g_frame[0].finish.y ); }
float3 ChainAfter( float3 colour, float2 uv ) { return colour; }
float2 ChainWhere( float2 uv ) { return uv; }

#endif

//How big the picture being read is, where it is smaller than the one being
//written. Nothing means they are the same size and there is nothing to carry up.
float2 SourceSize() { return g_vPostSlots[0].xy; }

//Whether the edges are softened - off where nothing was drawn, where the world is carried up, and where a point effect moved where the frame is read.
bool Softens() { return g_vPostSlots[0].z > 0.5; }

//How far RCAS sharpens what an upscaler gathered (CoreUpscale.RcasStrength), nought none; never with the softening
float Sharpening() { return g_vPostSlots[0].w; }

float Brightest( float3 c ) { return max( c.x, max( c.y, c.z ) ); }

//Exposed and squeezed into [0, 1) for RCAS to compare, as upscale.slang's Squeeze; Unsqueeze undoes it
float3 Squeeze( float3 c )
{
	float3 exposed = max( c, 0.0 ) * Exposure();
	return exposed / ( 1.0 + Brightest( exposed ) );
}

float3 Unsqueeze( float3 s )
{
	return ( s / max( 1.0 - Brightest( s ), 1.0 / 65504.0 ) ) / max( Exposure(), 1e-8 );
}

float LumaOf( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

//FSR1's RCAS over the picture read: the lobe as far as the four neighbours allow, less where the pixel is lone noise
float3 Sharpened( float2 at )
{
	float2 texel = g_vFrameSize.zw;

	float3 b = Squeeze( g_color.SampleLevel( at + float2( 0.0, -texel.y ), 0.0 ).rgb );
	float3 d = Squeeze( g_color.SampleLevel( at + float2( -texel.x, 0.0 ), 0.0 ).rgb );
	float3 e = Squeeze( g_color.SampleLevel( at, 0.0 ).rgb );
	float3 f = Squeeze( g_color.SampleLevel( at + float2( texel.x, 0.0 ), 0.0 ).rgb );
	float3 h = Squeeze( g_color.SampleLevel( at + float2( 0.0, texel.y ), 0.0 ).rgb );

	float3 low = min( min( b, d ), min( f, h ) );
	float3 high = max( max( b, d ), max( f, h ) );

	float3 hitLow = min( low, e ) / max( 4.0 * high, 1e-5 );
	float3 hitHigh = ( 1.0 - max( high, e ) ) / min( ( 4.0 * low ) - 4.0, -1e-5 );

	//FSR_RCAS_LIMIT: a quarter less a sixteenth
	float lobe = max( -0.1875, min( Brightest( max( -hitLow, hitHigh ) ), 0.0 ) ) * Sharpening();

	float lb = LumaOf( b ), ld = LumaOf( d ), le = LumaOf( e ), lf = LumaOf( f ), lh = LumaOf( h );
	float noise = 0.25 * ( lb + ld + lf + lh ) - le;
	float range = max( max( max( lb, ld ), max( lf, lh ) ), le ) - min( min( min( lb, ld ), min( lf, lh ) ), le );
	lobe *= 1.0 - ( 0.5 * saturate( abs( noise ) / max( range, 1e-5 ) ) );

	return Unsqueeze( saturate( ( lobe * ( b + d + f + h ) + e ) / ( 4.0 * lobe + 1.0 ) ) );
}

#ifdef CARRY

//One EASU tap, weighted by an ellipse stretched along the edge so cross-edge taps count less
void UpscaleTap( inout float3 sum, inout float weight, float2 offset, float2 direction,
	float2 stretched, float reach, float clip, float3 colour )
{
	//Turned into the frame of the edge, then squashed by how long the ellipse is in each of its axes.
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

//Which way the edge under this pixel runs, and how much of an edge it is, from the brightness of the twelve taps around it.
void EdgeOf( float2 frac, float a, float b, float c, float d, float e,
	bool s, bool t, bool u, bool v, inout float2 direction, inout float length )
{
	float w = 0.0;

	if ( s ) w = ( 1.0 - frac.x ) * ( 1.0 - frac.y );
	if ( t ) w = frac.x * ( 1.0 - frac.y );
	if ( u ) w = ( 1.0 - frac.x ) * frac.y;
	if ( v ) w = frac.x * frac.y;

	//Across, then down. The length of an axis is how straight the change through the middle is: a ramp counts, a spike does not.
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

//One exposed world texel, before the tonemap (measured slightly better than after)
float3 Tap( float2 uv )
{
	return g_color.SampleLevel( uv, 0.0 ).rgb * Exposure();
}

//FSR1 EASU upscale as a fragment shader: averages along edges instead of across them
float3 Upscaled( float2 uv, float2 source )
{
	float2 texel = 1.0 / source;

	//Where this pixel lands in the picture being read, and how far it is from the middle of the texel it lands in.
	float2 place = ( uv * source ) - 0.5;
	float2 corner = floor( place );
	float2 part = place - corner;

	//The twelve around it, named the way the paper names them: b c e f g h i j k l n o
	float2 middle = ( corner + 0.5 ) * texel;

	float3 b = Tap( middle + ( float2(  0.0, -1.0 ) * texel ) );
	float3 c = Tap( middle + ( float2(  1.0, -1.0 ) * texel ) );
	float3 e = Tap( middle + ( float2( -1.0,  0.0 ) * texel ) );
	float3 f = Tap( middle );
	float3 g = Tap( middle + ( float2(  1.0,  0.0 ) * texel ) );
	float3 h = Tap( middle + ( float2(  2.0,  0.0 ) * texel ) );
	float3 i = Tap( middle + ( float2( -1.0,  1.0 ) * texel ) );
	float3 j = Tap( middle + ( float2(  0.0,  1.0 ) * texel ) );
	float3 k = Tap( middle + ( float2(  1.0,  1.0 ) * texel ) );
	float3 l = Tap( middle + ( float2(  2.0,  1.0 ) * texel ) );
	float3 n = Tap( middle + ( float2(  0.0,  2.0 ) * texel ) );
	float3 o = Tap( middle + ( float2(  1.0,  2.0 ) * texel ) );

	//Green stands in for brightness, as it does in the original: it is most of what the eye sees and it costs one channel instead of three.
	float2 direction = float2( 0.0, 0.0 );
	float length = 0.0;

	EdgeOf( part, b.g, e.g, f.g, g.g, j.g, true,  false, false, false, direction, length );
	EdgeOf( part, c.g, f.g, g.g, h.g, k.g, false, true,  false, false, direction, length );
	EdgeOf( part, f.g, i.g, j.g, k.g, n.g, false, false, true,  false, direction, length );
	EdgeOf( part, g.g, j.g, k.g, l.g, o.g, false, false, false, true,  direction, length );

	//No direction worth the name: everything here is flat, and the ellipse becomes a circle.
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

	//How far the ellipse is stretched along the edge. A diagonal edge stretches less
	//than one along an axis, which is what stops a staircase turning into a smear.
	float longest = max( abs( direction.x ), abs( direction.y ) );
	float stretch = ( ( direction.x * direction.x ) + ( direction.y * direction.y ) ) /
		max( longest, 0.0001 );

	float2 stretched = float2(
		1.0 + ( ( stretch - 1.0 ) * length ),
		1.0 - ( 0.5 * length ) );

	float reach = 0.5 + ( ( ( 1.0 / 4.0 ) - 0.04 ) - 0.5 ) * length;
	float clip = 1.0 / max( reach, 0.0001 );

	float3 sum = float3( 0.0, 0.0, 0.0 );
	float weight = 0.0;

	UpscaleTap( sum, weight, float2(  0.0, -1.0 ) - part, direction, stretched, reach, clip, b );
	UpscaleTap( sum, weight, float2(  1.0, -1.0 ) - part, direction, stretched, reach, clip, c );
	UpscaleTap( sum, weight, float2( -1.0,  0.0 ) - part, direction, stretched, reach, clip, e );
	UpscaleTap( sum, weight, float2(  0.0,  0.0 ) - part, direction, stretched, reach, clip, f );
	UpscaleTap( sum, weight, float2(  1.0,  0.0 ) - part, direction, stretched, reach, clip, g );
	UpscaleTap( sum, weight, float2(  2.0,  0.0 ) - part, direction, stretched, reach, clip, h );
	UpscaleTap( sum, weight, float2( -1.0,  1.0 ) - part, direction, stretched, reach, clip, i );
	UpscaleTap( sum, weight, float2(  0.0,  1.0 ) - part, direction, stretched, reach, clip, j );
	UpscaleTap( sum, weight, float2(  1.0,  1.0 ) - part, direction, stretched, reach, clip, k );
	UpscaleTap( sum, weight, float2(  2.0,  1.0 ) - part, direction, stretched, reach, clip, l );
	UpscaleTap( sum, weight, float2(  0.0,  2.0 ) - part, direction, stretched, reach, clip, n );
	UpscaleTap( sum, weight, float2(  1.0,  2.0 ) - part, direction, stretched, reach, clip, o );

	float3 result = sum / max( weight, 0.0001 );

	//And kept inside what the four nearest taps say the picture is. The window has negative lobes, so without
	//this an edge overshoots into a bright line beside itself - which is exactly what a person notices.
	float3 low = min( min( f, g ), min( j, k ) );
	float3 high = max( max( f, g ), max( j, k ) );

	return clamp( result, low, high );
}

#endif

//The finished pixel (world plus effects through the curve); the edge filter compares these
float3 Shaded( float2 uv )
{
	float2 at = ChainWhere( uv );

#ifdef CARRY
	float2 source = SourceSize();

	//Carried up in the light the screen will show - see Tap - and put back into the
	//light the world was shaded in for the effects, which are written against that.
	if ( source.x > 0.5 )
		return ChainBefore( Upscaled( at, source ) / max( Exposure(), 1e-8 ), uv );
#endif

	return ChainBefore( g_color.SampleLevel( at, 0.0 ).rgb, uv );
}

float Luma( float3 colour )
{
	return dot( colour, float3( 0.299, 0.587, 0.114 ) );
}

//Corners are shaded fully like the middle; cheaper variants were measured and shifted edges

[shader("fragment")]
float4 MainPs( PostV2F i ) : SV_Target0
{
	//The swapchain is an sRGB format, so the hardware does the last conversion. What leaves here is linear, and that is deliberate.
	float2 uv = i.uv;

	float3 middle = Sharpening() > 0.0 ? ChainBefore( Sharpened( ChainWhere( uv ) ), uv ) : Shaded( uv );
	float3 result = middle;

	if ( Softens() )
	{
		//And the edges softened, which is the whole of what this engine has for anti-aliasing: there are no multisampled targets, and the setting that says
		//8x has never meant anything. This is FXAA - find where the brightness turns a corner, work out which way the corner runs, and average along it.
		float2 texel = g_vFrameSize.zw;

		float nw = Luma( Shaded( uv + ( float2( -1.0, -1.0 ) * texel ) ) );
		float ne = Luma( Shaded( uv + ( float2(  1.0, -1.0 ) * texel ) ) );
		float sw = Luma( Shaded( uv + ( float2( -1.0,  1.0 ) * texel ) ) );
		float se = Luma( Shaded( uv + ( float2(  1.0,  1.0 ) * texel ) ) );
		float m  = Luma( middle );

		float low = min( m, min( min( nw, ne ), min( sw, se ) ) );
		float high = max( m, max( max( nw, ne ), max( sw, se ) ) );

		//Flat, or dark enough that the step would not have shown. The second half is what keeps this off the inside of a
		//shadow, where a small absolute difference is a large relative one and softening it only smears the noise about.
		float range = high - low;

		if ( range >= max( 1.0 / 24.0, high * 0.125 ) )
		{
			//Which way the edge runs: perpendicular to the way the brightness changes fastest, which the four corners give directly.
			float2 direction;
			direction.x = -( ( nw + ne ) - ( sw + se ) );
			direction.y =  ( ( nw + sw ) - ( ne + se ) );

			//A bright edge may step by a lot and a dim one by very little; this keeps the step from running away on either.
			float reduce = max( ( nw + ne + sw + se ) * 0.25 * 0.125, 1.0 / 128.0 );
			float scale = 1.0 / ( min( abs( direction.x ), abs( direction.y ) ) + reduce );

			direction = clamp( direction * scale, -8.0, 8.0 ) * texel;

			//Two samples across the edge, and two further out. If the wider pair leaves the range the neighbourhood
			//had, it has reached past the edge into something else and the narrow pair is used instead.
			float3 near = 0.5 * (
				Shaded( uv + ( direction * ( ( 1.0 / 3.0 ) - 0.5 ) ) ) +
				Shaded( uv + ( direction * ( ( 2.0 / 3.0 ) - 0.5 ) ) ) );

			float3 wide = ( near * 0.5 ) + ( 0.25 * (
				Shaded( uv + ( direction * -0.5 ) ) +
				Shaded( uv + ( direction *  0.5 ) ) ) );

			float found = Luma( wide );

			result = found < low || found > high ? near : wide;
		}
	}

	//And what comes after the curve, once, over the finished picture.
	return float4( ChainAfter( result, i.uv ), 1.0 );
}
