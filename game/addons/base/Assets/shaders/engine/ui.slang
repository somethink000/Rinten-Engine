#include "colour.slang"
#include "half.slang"
#include "picture_size.slang"

//The frame's block, for the world build only: a head-locked sheet and a panel standing in the world take their view from the eye being drawn -
//see g_mode below and EyeClip in frame_data.slang. The interface's own build includes nothing of it, so an editor window needs no frame set bound.
#ifdef UI_WORLD
#include "frame_data.slang"
#endif

//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//The box shader: one signed-distance rounded rectangle per quad, with the border drawn as the ring
//between the outer edge and the same edge pushed inward. Everything it needs rides on the vertex.

struct VIn
{
	[[vk::location(0)]] float2 position;
	[[vk::location(1)]] float2 uv;
	[[vk::location(2)]] float4 rect;
	[[vk::location(3)]] float4 radii;
	[[vk::location(4)]] float4 fill;

	//A colour a side - left, top, right, bottom - as packed RGBA8, sRGB. Packed because the portable frame's
	//WebGL2 promises sixteen attributes and no more; four colours of their own would have taken four of them.
	[[vk::location(5)]] uint4 borders;
	[[vk::location(6)]] float4 misc;
	[[vk::location(7)]] float4 clip;
	[[vk::location(8)]] float4 filter;
	[[vk::location(9)]] float2 filter2;
	[[vk::location(10)]] float2 local;

	//Which picture in the indexed table, or nought for the bound one.
	[[vk::location(11)]] uint texture;

	//The corners the clipping ancestor was rounded by.
	[[vk::location(12)]] float4 clipRadii;

	//The vertical radii, the box's corners and then the clip's, as half floats two to a word - top-left and
	//top-right, bottom-right and bottom-left. Nought is a circle: the vertical radius is the horizontal.
	[[vk::location(13)]] uint4 vertical;
};

struct V2F
{
	float4 position : SV_Position;
	float2 screen : TEXCOORD0;
	float2 uv : TEXCOORD1;
	float4 rect : TEXCOORD2;
	float4 radii : TEXCOORD3;
	float4 fill : COLOR0;

	//Unpacked where they are used: four colours unpacked here would be four interpolants, and WebGL2 promises fifteen.
	nointerpolation uint4 borders : COLOR1;
	float4 misc : TEXCOORD4;
	float4 clip : TEXCOORD5;
	float4 filter : TEXCOORD6;
	float2 filter2 : TEXCOORD7;

	//Flat, because an index is not a quantity: interpolating between two slot numbers names a third picture that has nothing to do with either.
	nointerpolation uint texture : TEXCOORD8;

	float4 clipRadii : TEXCOORD9;
	nointerpolation uint4 vertical : TEXCOORD10;
};

[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_worldToClip;

	//The target, in pixels: the backdrop copy is the whole of it, so a panel reading it has to know where on it the panel sits. zw is
	//where the target's corner is on the window - nothing for the window's own frame, the layer's place for a layer the world is drawn on.
	float4 g_screen;

	//x: world panel (own pixel space for clips); y: use the frame's eye view. Only UI_WORLD reads y
	float4 g_mode;
};

[[vk::binding(0,0)]] Sampler2D g_color;

//Bindless texture table (set 3) so one draw can use any texture; portable tier binds slot 0 instead
#ifndef PORTABLE
[[vk::binding(0,3)]] Sampler2D g_textures[];
#endif

//The picture this box reads, wherever it lives.
float4 SampleUi( uint slot, float2 uv )
{
#ifdef PORTABLE
	return g_color.Sample( uv );
#else
	if ( slot == 0 ) return g_color.Sample( uv );

	return g_textures[NonUniformResourceIndex( slot )].Sample( uv );
#endif
}

//The same, with the gradients given rather than taken from the coordinate - for a coordinate that has been moved, as a pixelated read's is.
float4 SampleUiGrad( uint slot, float2 uv, float2 dx, float2 dy )
{
#ifdef PORTABLE
	return g_color.SampleGrad( uv, dx, dy );
#else
	if ( slot == 0 ) return g_color.SampleGrad( uv, dx, dy );

	return g_textures[NonUniformResourceIndex( slot )].SampleGrad( uv, dx, dy );
#endif
}

//How many texels the picture this box reads is across and down.
float2 UiPictureSize( uint slot )
{
	float2 size;
#ifdef PORTABLE
	size = float2( PictureSize( g_color ) );
#else
	if ( slot == 0 ) size = float2( PictureSize( g_color ) );
	else g_textures[NonUniformResourceIndex( slot )].GetDimensions( size.x, size.y );
#endif
	return max( size, 1.0 );
}

//UV for image-rendering: pixelated: texel centre, crossing only in the last pixel before an edge
float2 PixelatedUv( uint slot, float2 uv )
{
	float2 size = UiPictureSize( slot );
	float2 texel = uv * size;
	float2 across = max( fwidth( texel ), 1e-5 );
	float2 fromMiddle = texel - 0.5;

	return ( floor( fromMiddle ) + 0.5 + saturate( ( frac( fromMiddle ) - 0.5 ) / across + 0.5 ) ) / size;
}

//The interface's colours arrive as sRGB and are blended in linear - see ToLinear in colour.slang,
//which the overlay shaders share, and which says why it is called from a vertex stage.

[shader("vertex")]
V2F MainVs( VIn i )
{
	V2F o;

	float4 placed = mul( float4( i.position, 0.0, 1.0 ), g_worldToClip );

#ifdef UI_WORLD
	o.position = g_mode.y != 0.0 ? EyeClip( placed ) : placed;
#else
	o.position = placed;
#endif

	//Where the corner was before a transform moved it. The rounding, the border and the shadows are all
	//measured in that space, so a turned panel is the same picture turned rather than a different one.
	o.screen = i.local;
	o.uv = i.uv;
	o.rect = i.rect;
	o.radii = i.radii;
	o.fill = float4( ToLinear( i.fill.rgb ), i.fill.a );
	o.borders = i.borders;
	o.misc = i.misc;
	o.clip = i.clip;
	o.filter = i.filter;
	o.filter2 = i.filter2;
	o.texture = i.texture;
	o.clipRadii = i.clipRadii;
	o.vertical = i.vertical;
	return o;
}

//Four vertical radii out of two words of half floats, where nought means the corner's horizontal radius - a circle. Selected by
//multiplying rather than by lerp, so a circle's two radii come out equal to the last bit and RoundedBox takes it for the circle it is.
float4 VerticalRadii( float4 horizontal, uint2 packed )
{
	float4 v = float4(
		HalfToFloat( packed.x & 0xffffu ), HalfToFloat( packed.x >> 16 ),
		HalfToFloat( packed.y & 0xffffu ), HalfToFloat( packed.y >> 16 ) );

	float4 given = step( 0.0001, v );
	return v * given + horizontal * ( 1.0 - given );
}

//A packed RGBA8 colour, as it was written.
float4 Unpacked( uint colour )
{
	return float4(
		float( colour & 0xffu ), float( ( colour >> 8 ) & 0xffu ),
		float( ( colour >> 16 ) & 0xffu ), float( colour >> 24 ) ) / 255.0;
}

//The two radii of the corner this point is nearest, by quadrant.
float2 CornerOf( float2 p, float4 h, float4 v )
{
	return p.x < 0.0
		? ( p.y < 0.0 ? float2( h.x, v.x ) : float2( h.w, v.w ) )
		: ( p.y < 0.0 ? float2( h.y, v.y ) : float2( h.z, v.z ) );
}

//Clamp corner radii to what the box can hold so shapes don't bulge out of their quad
float2 HeldCorner( float2 r, float2 halfSize )
{
	return r.x == r.y
		? min( r.x, min( halfSize.x, halfSize.y ) ).xx
		: min( r, halfSize );
}

float RoundedBox( float2 p, float2 halfSize, float4 radii, float4 radiiV )
{
	float2 r = HeldCorner( CornerOf( p, radii, radiiV ), halfSize );

	if ( r.x == r.y )
	{
		float2 q = abs( p ) - halfSize + r.x;
		return length( max( q, 0.0 ) ) + min( max( q.x, q.y ), 0.0 ) - r.x;
	}

	//A quarter of an ellipse. Its distance has no closed form, so it is the usual first-order one - exact
	//on the curve, which is where the edge's pixel of antialiasing is measured, and near enough off it.
	float2 d = abs( p ) - halfSize;
	float2 q = d + r;

	if ( q.x > 0.0 && q.y > 0.0 && min( r.x, r.y ) > 0.0001 )
	{
		float k0 = length( q / r );
		float k1 = length( q / ( r * r ) );
		return k0 * ( k0 - 1.0 ) / max( k1, 0.000001 );
	}

	return length( max( d, 0.0 ) ) + min( max( d.x, d.y ), 0.0 );
}

//Inner ring corner radii: subtract border widths (circle by the thicker side, ellipse per axis)
float2 InnerCorner( float h, float v, float sideX, float sideY )
{
	if ( h == v ) return max( h - max( sideX, sideY ), 0.0 ).xx;

	float2 inner = float2( h - sideX, v - sideY );
	return inner.x > 0.0 && inner.y > 0.0 ? inner : float2( 0.0, 0.0 );
}

//Border styles, as Rinten.UI.BorderStyle numbers them. None and hidden never reach here
//- the renderer draws no border for them - and the styles past dashed are drawn solid.
static const int BorderDotted = 3;
static const int BorderDashed = 4;

//Where a pixel is along a border's midline: how far round from the top edge's start, how
//long the whole loop is, how far off the line it is and which way the line faces there.
struct BorderContour
{
	float along;
	float length;
	float across;
	float2 normal;
};

//The arc length of ( a cos t, b sin t ) from nought to the angle, by eight-point Gauss-Legendre - exact for a circle, close for an ellipse.
float EllipseLength( float2 radii, float angle )
{
	if ( radii.x == radii.y ) return radii.x * angle;

	const float4 nodes = float4( 0.1834346425, 0.5255324099, 0.7966664774, 0.9602898565 );
	const float4 weights = float4( 0.3626837834, 0.3137066459, 0.2223810345, 0.1012285363 );

	float result = 0.0;

	for ( int i = 0; i < 4; i++ )
	{
		float2 t = angle * 0.5 * float2( 1.0 - nodes[i], 1.0 + nodes[i] );
		float2 s = sin( t );
		float2 c = cos( t );

		result += weights[i] * ( length( radii * float2( s.x, c.x ) ) + length( radii * float2( s.y, c.y ) ) );
	}

	return result * angle * 0.5;
}

float2 Normalised( float2 v )
{
	float size = length( v );
	return size > 0.000001 ? v / size : float2( 1.0, 0.0 );
}

//Nearest point on the border midline (4 sides, 4 elliptic corners), for dotted/dashed borders
BorderContour Contour( float2 p, float2 size, float4 widths, float4 h, float4 v )
{
	float4 inset = widths * 0.5;
	float2 inner = max( size - inset.xy - inset.zw, 0.0 );
	p -= inset.xy;

	//Each corner less half of each side that meets it, by axis.
	h = max( h - inset.xzzx, 0.0 );
	v = max( v - inset.yyww, 0.0 );

	float4 keep = step( 0.0001, h ) * step( 0.0001, v );
	h *= keep;
	v *= keep;

	float2 starts[4] = { float2( h.x, 0.0 ), float2( inner.x, v.y ), float2( inner.x - h.z, inner.y ), float2( 0.0, inner.y - v.w ) };
	float2 ends[4] = { float2( inner.x - h.y, 0.0 ), float2( inner.x, inner.y - v.z ), float2( h.w, inner.y ), float2( 0.0, v.x ) };
	float2 centres[4] = { float2( inner.x - h.y, v.y ), float2( inner.x - h.z, inner.y - v.z ), float2( h.w, inner.y - v.w ), float2( h.x, v.x ) };
	float2 axes[4] = { float2( 0.0, -1.0 ), float2( 1.0, 0.0 ), float2( 0.0, 1.0 ), float2( -1.0, 0.0 ) };
	float2 radii[4] = { float2( v.y, h.y ), float2( h.z, v.z ), float2( v.w, h.w ), float2( h.x, v.x ) };

	BorderContour result;
	result.along = 0.0;
	result.length = 0.0;
	result.across = 0.0;
	result.normal = float2( 0.0, -1.0 );

	float nearest = 1e30;

	for ( int i = 0; i < 4; i++ )
	{
		float2 delta = ends[i] - starts[i];
		float extent = length( delta );
		float t = saturate( dot( p - starts[i], delta ) / max( dot( delta, delta ), 0.000001 ) );
		float2 relative = p - lerp( starts[i], ends[i], t );
		float distance = dot( relative, relative );

		if ( distance < nearest )
		{
			nearest = distance;
			result.along = result.length + t * extent;
			result.normal = axes[i];
			result.across = dot( relative, result.normal );
		}

		result.length += extent;

		float2 r = radii[i];

		if ( r.x > 0.0 && r.y > 0.0 )
		{
			float2 nextAxis = axes[( i + 1 ) % 4];
			float2 q = p - centres[i];
			float2 local = float2( dot( q, axes[i] ), dot( q, nextAxis ) );
			float angle = clamp( atan2( local.y / r.y, local.x / r.x ), 0.0, 1.57079632679 );

			//The radial angle is not the nearest point on an ellipse; a few Newton steps along
			//its normal keep dots round on a wide corner rather than drawn out into wedges.
			if ( r.x != r.y )
			{
				for ( int iteration = 0; iteration < 5; iteration++ )
				{
					float s = sin( angle );
					float c = cos( angle );
					float slope = ( r.y * r.y - r.x * r.x ) * s * c + r.x * local.x * s - r.y * local.y * c;
					float bend = ( r.y * r.y - r.x * r.x ) * ( c * c - s * s ) + r.x * local.x * c + r.y * local.y * s;

					if ( bend > 0.000001 ) angle = clamp( angle - clamp( slope / bend, -0.25, 0.25 ), 0.0, 1.57079632679 );
				}
			}

			float2 radial = float2( cos( angle ), sin( angle ) );
			float2 onCurve = centres[i] + axes[i] * radial.x * r.x + nextAxis * radial.y * r.y;

			relative = p - onCurve;
			distance = dot( relative, relative );

			if ( distance < nearest )
			{
				nearest = distance;
				result.along = result.length + EllipseLength( r, angle );
				result.normal = Normalised( axes[i] * radial.x / r.x + nextAxis * radial.y / r.y );
				result.across = dot( relative, result.normal );
			}

			result.length += EllipseLength( r, 1.57079632679 );
		}
	}

	return result;
}

//The integral of a square wave that is up for <paramref name="duty"/> of each
//period - so a dash's ends can be covered by how much of a pixel they cover.
float Pulse( float x, float duty )
{
	return floor( x ) * duty + min( frac( x ), duty );
}

//Dotted/dashed coverage, with a whole number of periods so the pattern closes; browser-like sizes
float BorderPattern( int style, float width, BorderContour contour )
{
	float target = width * ( style == BorderDotted ? 2.0 : 6.0 );
	float count = max( 1.0, round( contour.length / max( target, 0.0001 ) ) );
	float period = max( contour.length / count, 0.0001 );
	float phase = frac( contour.along / period );

	if ( style == BorderDashed || width < 2.5 )
	{
		//A pixel's worth of the pattern, averaged.
		float footprint = 1.0 / period;
		float centre = phase + 0.25;

		return saturate( ( Pulse( centre + footprint * 0.5, 0.5 ) - Pulse( centre - footprint * 0.5, 0.5 ) ) / footprint );
	}

	float along = ( frac( phase + 0.5 ) - 0.5 ) * period;
	float2 tangent = float2( -contour.normal.y, contour.normal.x );
	float2 offset = tangent * along + contour.normal * contour.across;

	return 1.0 - smoothstep( -0.5, 0.5, length( offset ) - width * 0.5 );
}

//Border side colour per pixel, split at each corner's mitre like the web; zero-width sides never win
float4 BorderColour( float2 pos, float2 size, float4 widths, uint4 colours )
{
	float4 has = step( 0.0001, widths );
	float4 depth = float4( pos.x, pos.y, size.x - pos.x, size.y - pos.y ) / max( widths, 0.0001 );
	depth = depth * has + ( 1.0 - has ) * 1e9;

	//The nearest side, and the one after it.
	uint first = colours.x;
	uint second = colours.y;
	float d1 = depth.x;
	float d2 = depth.y;
	float w1 = widths.x;
	float w2 = widths.y;

	if ( d2 < d1 ) { first = colours.y; second = colours.x; d1 = depth.y; d2 = depth.x; w1 = widths.y; w2 = widths.x; }

	if ( depth.z < d1 ) { second = first; d2 = d1; w2 = w1; first = colours.z; d1 = depth.z; w1 = widths.z; }
	else if ( depth.z < d2 ) { second = colours.z; d2 = depth.z; w2 = widths.z; }

	if ( depth.w < d1 ) { second = first; d2 = d1; w2 = w1; first = colours.w; d1 = depth.w; w1 = widths.w; }
	else if ( depth.w < d2 ) { second = colours.w; d2 = depth.w; w2 = widths.w; }

	float4 near = Unpacked( first );
	near.rgb = ToLinear( near.rgb );

	//How far from the cut this pixel is, in pixels: the difference of two depths each measured in its own side's widths.
	float fromCut = ( d2 - d1 ) * w1 * w2 / max( length( float2( w1, w2 ) ), 0.0001 );
	float share = d2 > 1e8 ? 0.0 : saturate( 0.5 - fromCut );

	if ( share <= 0.0 || first == second ) return near;

	float4 far = Unpacked( second );
	far.rgb = ToLinear( far.rgb );

	return lerp( near, far, share );
}

//Stylesheet colours are sRGB and the target encodes on write, so vertex colours are decoded once

//The texture turned about its middle - which is how a gradient points somewhere other
//than straight down, the gradient itself being a strip of colour baked into a picture.
float2 Turn( float2 uv, float angle )
{
	if ( abs( angle ) < 0.0001 ) return uv;

	float s = sin( angle );
	float c = cos( angle );

	float2 centred = uv - 0.5;
	return float2( centred.x * c - centred.y * s, centred.x * s + centred.y * c ) + 0.5;
}

float3 ToHsv( float3 colour )
{
	const float4 k = float4( 0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0 );

	float4 p = lerp( float4( colour.bg, k.wz ), float4( colour.gb, k.xy ), step( colour.b, colour.g ) );
	float4 q = lerp( float4( p.xyw, colour.r ), float4( colour.r, p.yzx ), step( p.x, colour.r ) );

	float chroma = q.x - min( q.w, q.y );
	const float tiny = 1.0e-10;

	return float3( abs( q.z + ( q.w - q.y ) / ( 6.0 * chroma + tiny ) ), chroma / ( q.x + tiny ), q.x );
}

float3 FromHsv( float3 hsv )
{
	const float4 k = float4( 1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0 );

	float3 p = abs( frac( hsv.xxx + k.xyz ) * 6.0 - k.www );

	return hsv.z * lerp( k.xxx, saturate( p - k.xxx ), hsv.y );
}

//CSS backdrop filters, in the same order as the engine's backdrop shader; the order matters
float3 Filtered( float3 colour, float4 filter, float2 filter2 )
{
	colour = ToGamma( colour );

	float sepia = saturate( filter2.y );

	if ( sepia > 0.0001 )
	{
		float3 aged = float3(
			dot( colour, float3( 0.393, 0.769, 0.189 ) ),
			dot( colour, float3( 0.349, 0.686, 0.168 ) ),
			dot( colour, float3( 0.272, 0.534, 0.131 ) ) );

		colour = lerp( colour, aged, sepia );
	}

	colour = lerp( colour, 1.0 - colour, saturate( filter.w ) );     //invert
	colour = saturate( lerp( float3( 0.5, 0.5, 0.5 ), colour, filter.z ) ); //contrast

	colour = ToLinear( colour );

	float3 hsv = ToHsv( colour );

	hsv.x = frac( hsv.x + filter2.x );      //hue, in turns
	hsv.y = lerp( 0.0, hsv.y, filter.y );   //saturation
	hsv.z *= filter.x;                      //brightness

	return max( FromHsv( hsv ), 0.0 );
}

//Blurred backdrop from a mip of the copy, golden-angle spiral rotated per pixel to avoid ghosting
float3 Backdrop( float2 screen, float radius )
{
	float2 texel = 1.0 / max( g_screen.xy, 1.0 );

	if ( radius <= 0.5 ) return g_color.SampleLevel( screen * texel, 0.0 ).rgb;

	//Six pixels of the radius to a texel of the level read: level two at the sheets' twenty-four.
	float level = max( log2( radius / 6.0 ), 0.0 );

	float3 sum = g_color.SampleLevel( screen * texel, level ).rgb;

	const float golden = 2.39996323;
	const int taps = 12;

	//Interleaved gradient noise: a turn per pixel that its neighbours share nothing of, and no texture to read it from.
	float turn = 6.2831853 * frac( 52.9829189 * frac( dot( floor( screen ), float2( 0.06711056, 0.00583715 ) ) ) );

	for ( int tap = 1; tap <= taps; ++tap )
	{
		//Square-rooted so the taps spread evenly over the disk rather than crowding its middle.
		float distance = radius * sqrt( float( tap ) / float( taps ) );
		float angle = golden * float( tap ) + turn;

		float2 at = screen + float2( cos( angle ), sin( angle ) ) * distance;

		sum += g_color.SampleLevel( clamp( at, 0.5, g_screen.xy - 0.5 ) * texel, level ).rgb;
	}

	return sum / float( taps + 1 );
}

[shader("fragment")]
float4 MainPs( V2F i ) : SV_Target
{
	//The pixel this actually is, as against where the box thinks it is: a transformed panel is clipped by where it ended up, and reads the backdrop from
	//there too. A panel in the world has no screen pixels of its own to speak of; its laid-out ones are the only ones that mean anything to its clips.
	bool inWorld = g_mode.x > 0.5;
	float2 pixel = inWorld ? i.screen : i.position.xy;

	//How fast a vector shape's distances change across the screen, taken before anything can discard or branch: a derivative wants every pixel of its quad
	//still running, and WGSL and GLSL ES only promise one in uniform control flow. Worthless and harmless for a box, which carries no such distances.
	float2 acrossRate = float2( ddx( i.uv.x ), ddy( i.uv.x ) );
	float2 endRate = float2( ddx( i.filter.x ), ddy( i.filter.x ) );

	//Rounded overflow clip from ancestors (zero rect = none), in window pixels offset to the target
	float clipped = 1.0;

	if ( i.clip.z > i.clip.x || i.clip.w > i.clip.y )
	{
		float2 clipHalf = ( i.clip.zw - i.clip.xy ) * 0.5;
		float2 onWindow = inWorld ? pixel : pixel + g_screen.zw;

		clipped = 1.0 - smoothstep( -0.5, 0.5,
			RoundedBox( onWindow - ( i.clip.xy + clipHalf ), clipHalf, i.clipRadii,
				VerticalRadii( i.clipRadii, i.vertical.zw ) ) );

		if ( clipped <= 0.0 ) discard;
	}

	//Vector shape triangle: antialias edge and cap distances to one screen pixel via derivatives
	if ( ( int( i.misc.y + 0.5 ) & 15 ) == 10 )
	{
		float across = max( length( acrossRate ), 0.00001 );
		float along = max( length( endRate ), 0.00001 );

		float cover = saturate( ( i.uv.y - abs( i.uv.x ) ) / across + 0.5 )
			* saturate( i.filter.x / along + 0.5 );

		if ( cover <= 0.0 ) discard;

		float4 ink = i.fill;
		ink.a *= cover * clipped;
		return ink;
	}

	float2 halfSize = i.rect.zw * 0.5;
	float2 p = i.screen - ( i.rect.xy + halfSize );

	float4 radiiV = VerticalRadii( i.radii, i.vertical.xy );
	float distance = RoundedBox( p, halfSize, i.radii, radiiV );

	//The low four bits: above them is what only the processor reads - see OpaquePicture in AddBox - and above that the border's style.
	int marks = int( i.misc.y + 0.5 );
	int flags = marks & 15;
	int borderStyle = ( marks >> 5 ) & 15;

	//A shadow is not bounded by the panel's own box - an outer one lies entirely outside it
	//- so it is its own shape and its own edge, and the rest of this does not apply to it.
	if ( flags == 4 || flags == 5 )
	{
		bool inset = flags == 5;

		float2 offset = i.filter.xy;
		float spread = inset ? -i.filter.z : i.filter.z;

		//Never quite zero: a hard edge still wants its pixel of antialiasing, and dividing by the softness is how it gets it.
		float softness = max( i.misc.w, 0.5 );

		float2 shadowHalf = max( halfSize + spread, 0.0 );
		float4 shadowRadii = max( i.radii + spread, 0.0 );
		float4 shadowRadiiV = max( radiiV + spread, 0.0 );

		float shadowDistance = RoundedBox( p - offset, shadowHalf, shadowRadii, shadowRadiiV );

		//Outer shadow fades outside and is masked from its own panel; inset is the reverse
		float alpha = inset
			? smoothstep( -softness, softness, shadowDistance ) * ( 1.0 - smoothstep( -0.5, 0.5, distance ) )
			: ( 1.0 - smoothstep( -softness, softness, shadowDistance ) )
				* smoothstep( -0.5, 0.5, distance );

		if ( alpha <= 0.0 ) discard;

		float4 shadow = i.fill;
		shadow.a *= alpha * clipped;
		return shadow;
	}

	//A pixel of antialiasing across the edge.
	float coverage = 1.0 - smoothstep( -0.5, 0.5, distance );
	if ( coverage <= 0.0 ) discard;

	float4 colour = i.fill;

	if ( flags == 2 )
	{
		//What is behind the panel, filtered, standing in for a background of its own - so
		//whatever draws next blends over the filtered picture rather than over the raw one.
		colour.rgb = Filtered( Backdrop( pixel, i.misc.w ), i.filter, i.filter2 );
		colour.a = i.fill.a;
	}
	else if ( flags == 1 || flags == 3 || flags == 6 )
	{
		//Turned about the middle, then either held to its edges, let to tile, or left to stop where it stops.
		float2 uv = Turn( i.uv, i.misc.z );

		//A picture given a size smaller than the box covers only what it covers; past that the background
		//colour shows through, which is what CSS means and what clamping to the edge pixel does not.
		if ( flags == 6 && ( any( uv < 0.0 ) || any( uv > 1.0 ) ) ) discard;

		if ( flags == 1 ) uv = saturate( uv );

		//Hard texels where the style asked for them - AddBox's Pixelated bit.
		if ( ( marks & 512 ) != 0 )
			colour *= SampleUiGrad( i.texture, PixelatedUv( i.texture, uv ), ddx( uv ), ddy( uv ) );
		else
			colour *= SampleUi( i.texture, uv );
	}
	else if ( flags == 7 )
	{
		//Glyph atlas is premultiplied in sRGB: unpremultiply before decoding to linear to avoid dark fringes
		float4 ink = SampleUi( i.texture, saturate( i.uv ) );

		if ( ink.a <= 0.0 ) discard;

		colour.rgb *= ToLinear( saturate( ink.rgb / ink.a ) );

		//Text coverage was cut for sRGB blending; a power curve picked by ink lightness corrects for linear
		float lightness = ToSrgb( saturate( dot( colour.rgb, float3( 0.2126, 0.7152, 0.0722 ) ) ) );

		//Held at both ends: past a fifth of the way up, the ink is light enough that it is certainly on something darker, and the correction is the full one.
		float cut = saturate( ( lightness - 0.15 ) / 0.5 );

		colour.a *= pow( ink.a, lerp( 0.45, 1.8, cut ) );
	}
	else if ( flags == 8 )
	{
		colour.rgb = FromHsv( float3( i.filter.x, i.uv.x, 1.0 - i.uv.y ) );
	}
	else if ( flags == 9 )
	{
		//A gradient. The picture is its colours along the gradient line, 0 to 1, and where this pixel falls on
		//that line is worked out here from the box, the way the web measures it - see CoreUiRenderer.GradientBox.
		float2 local = i.screen - i.rect.xy;
		float2 size = max( i.rect.zw, 1.0 );
		int type = int( i.filter2.x + 0.5 );

		float t;

		if ( type == 0 )
		{
			//Through the middle of the box, long enough that its ends touch the corners. 0 runs down the box, a quarter turn runs to the right.
			float2 dir = float2( sin( i.misc.z ), cos( i.misc.z ) );
			float len = abs( size.x * dir.x ) + abs( size.y * dir.y );
			t = dot( local - size * 0.5, dir ) / max( len, 0.0001 ) + 0.5;
		}
		else if ( type == 1 )
		{
			//Out from the centre to the ending shape, whose radii were worked out for this box.
			t = length( ( local - i.filter.xy ) / max( i.filter.zw, 0.0001 ) );
		}
		else
		{
			//Round from straight up, clockwise, starting where it was told to.
			float2 d = local - i.filter.xy;
			t = frac( ( atan2( d.x, -d.y ) - i.misc.z ) / 6.28318530718 );
		}

		//Texel centres, so the ends are the end stops and never a blend with the far end of a picture the sampler wraps.
		float texels = max( i.filter2.y, 1.0 );
		float u = ( saturate( t ) * ( texels - 1.0 ) + 0.5 ) / texels;

		colour *= SampleUi( i.texture, float2( u, 0.5 ) );
	}

	//Border ring composited over the fill, using each side's own width; transparent borders add nothing.
	//Left, top, right, bottom. Nothing said means the same all round, which is what the one width in misc always meant.
	float4 w = i.filter;

	if ( all( w <= 0.0 ) ) w = i.misc.x;

	//The ring lies within its widest side of the edge; deeper in, the inner shape covers the pixel whole
	if ( i.misc.x > 0.0 && distance > -( max( max( w.x, w.y ), max( w.z, w.w ) ) + 1.0 ) )
	{
		float2 innerCentre = float2( w.x - w.z, w.y - w.w ) * 0.5;
		float2 innerHalf = max( halfSize - float2( w.x + w.z, w.y + w.w ) * 0.5, 0.0 );

		//Each corner less the sides that meet there - see InnerCorner.
		float2 innerTL = InnerCorner( i.radii.x, radiiV.x, w.x, w.y );
		float2 innerTR = InnerCorner( i.radii.y, radiiV.y, w.z, w.y );
		float2 innerBR = InnerCorner( i.radii.z, radiiV.z, w.z, w.w );
		float2 innerBL = InnerCorner( i.radii.w, radiiV.w, w.x, w.w );

		float innerDistance = RoundedBox( p - innerCentre, innerHalf,
			float4( innerTL.x, innerTR.x, innerBR.x, innerBL.x ),
			float4( innerTL.y, innerTR.y, innerBR.y, innerBL.y ) );

		//Ring as outer minus inner coverage, so zero-width sides leave no smear
		float insideInner = 1.0 - smoothstep( -0.5, 0.5, innerDistance );
		float ring = saturate( coverage - insideInner );

		if ( ring > 0.0 )
		{
			float2 corner = p + halfSize;
			float4 side = BorderColour( corner, i.rect.zw, w, i.borders );

			//Dots and dashes are the same ring with pieces of it left out, measured along the loop through the middle of it.
			float kept = 1.0;

			if ( borderStyle == BorderDotted || borderStyle == BorderDashed )
			{
				float2 tl = HeldCorner( float2( i.radii.x, radiiV.x ), halfSize );
				float2 tr = HeldCorner( float2( i.radii.y, radiiV.y ), halfSize );
				float2 br = HeldCorner( float2( i.radii.z, radiiV.z ), halfSize );
				float2 bl = HeldCorner( float2( i.radii.w, radiiV.w ), halfSize );

				BorderContour contour = Contour( corner, i.rect.zw, w,
					float4( tl.x, tr.x, br.x, bl.x ), float4( tl.y, tr.y, br.y, bl.y ) );

				//One period all the way round, whatever each side's width.
				kept = BorderPattern( borderStyle, max( max( w.x, w.y ), max( w.z, w.w ) ), contour );
			}

			//Said as a fraction of what the shape covers here, because the coverage is put back at the
			//end - and a border which is all there is in a pixel should be all of that pixel's colour.
			float border = side.a * kept * ( coverage > 0.0001 ? saturate( ring / coverage ) : 0.0 );

			float outside = border + colour.a * ( 1.0 - border );

			colour.rgb = outside <= 0.0001
				? colour.rgb
				: ( side.rgb * border + colour.rgb * colour.a * ( 1.0 - border ) ) / outside;

			colour.a = outside;
		}
	}

	colour.a *= coverage * clipped;
	return colour;
}
