//Colour grading: white balance in scene light, then curves or a LUT on the clamped colour

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

FEATURES
{
}

PARAMETERS
{
	//How much of this shows at all.
	float g_flBlendFactor < Default( 1.0 ); Range( 0, 1 ); UiGroup( "Grading" ); >;

	//What the white balance multiplies the picture by: the colour of a black body at the temperature asked for,
	//against daylight. Worked out by the component once rather than by every pixel - see ColorGrading.Kelvin.
	float3 g_vWhiteBalance < Default3( 1.0, 1.0, 1.0 ); UiGroup( "Grading" ); >;

	//Which of the three this does: 0 nothing, 1 the temperature, 2 the table. ColorGrading.GradingType.
	int g_nGradingMethod < Default( 0 ); UiGroup( "Grading" ); >;

	//And whether the curves run, and in what space: 0 none, 1 RGB, 2 HSV. ColorGrading.ColorSpaceEnum.
	int g_nColorSpace < Default( 0 ); UiGroup( "Grading" ); >;

	//A 2D strip of a 3D table: as many squares across as it is tall, blue choosing the square, red and
	//green the place in it. White where the component has none, which reads as the colour unchanged.
	Texture2D g_tLookupTexture < Default3( 1.0, 1.0, 1.0 ); UiGroup( "Grading" ); >;

	//Up to four frames a curve, each time, value, in and out tangent, and the reciprocal of the gap to the next - see ColorGrading.ProcessCurve.
	int g_nCurveFramesR < Default( 0 ); >;
	float4 g_vCurveFrameR0 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameR1 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameR2 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameR3 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveDivisorsR < Default4( 0, 0, 0, 0 ); >;

	int g_nCurveFramesG < Default( 0 ); >;
	float4 g_vCurveFrameG0 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameG1 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameG2 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameG3 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveDivisorsG < Default4( 0, 0, 0, 0 ); >;

	int g_nCurveFramesB < Default( 0 ); >;
	float4 g_vCurveFrameB0 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameB1 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameB2 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveFrameB3 < Default4( 0, 0, 0, 0 ); >;
	float4 g_vCurveDivisorsB < Default4( 0, 0, 0, 0 ); >;
}

#include "postprocess.slang"

//One curve, evaluated the way Curve.Evaluate does it: held at the ends, and a Hermite between two frames whose tangents are slopes.
float Curved( float x, float count, float4 f0, float4 f1, float4 f2, float4 f3, float4 divisors )
{
	if ( count < 1.5 ) return count < 0.5 ? x : f0.y;

	if ( x <= f0.x ) return f0.y;

	float4 a = f0;
	float4 b = f1;
	float gap = divisors.x;

	if ( count > 2.5 && x > f2.x ) { a = f2; b = f3; gap = divisors.z; }
	else if ( count > 1.5 && x > f1.x ) { a = f1; b = f2; gap = divisors.y; }

	if ( x >= b.x ) return b.y;

	//The same arithmetic as Curve.GetInterpolatedValue, with the in tangent of the frame ahead turned around the way it is there.
	float t = ( x - a.x ) * gap;
	float dx = b.x - a.x;
	float dy = b.y - a.y;

	float it = -b.z;
	float ot = a.w;

	return a.y + t * ( t * ( t * ( ( it + ot ) * dx - 2.0 * dy ) + ( -it - 2.0 * ot ) * dx + 3.0 * dy ) + ot * dx );
}

float3 ToHsv( float3 c )
{
	float4 k = float4( 0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0 );
	float4 p = c.g < c.b ? float4( c.bg, k.wz ) : float4( c.gb, k.xy );
	float4 q = c.r < p.x ? float4( p.xyw, c.r ) : float4( c.r, p.yzx );

	float d = q.x - min( q.w, q.y );
	float e = 1.0e-10;

	return float3( abs( q.z + ( q.w - q.y ) / ( 6.0 * d + e ) ), d / ( q.x + e ), q.x );
}

float3 FromHsv( float3 c )
{
	float4 k = float4( 1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0 );
	float3 p = abs( frac( c.xxx + k.xyz ) * 6.0 - k.www );

	return c.z * lerp( k.xxx, saturate( p - k.xxx ), c.y );
}

//The picture through the table: a strip of squares, blue picking the square and red and
//green the place in it, read twice and blended between the two squares blue falls between.
float3 Tabled( float3 colour )
{
	if ( !g_tLookupTexture.Exists() ) return colour;

#ifdef PORTABLE
	return colour;
#else
	uint wide, tall;
	g_textures[NonUniformResourceIndex( uint( g_tLookupTexture.where.x ) )].GetDimensions( wide, tall );

	//One square is as many texels across as the strip is tall, and there are as many squares as that too. A
	//picture that is not that shape is not a table, and reads back as the colour unchanged rather than as noise.
	float size = float( tall );

	if ( tall < 2 || wide != tall * tall ) return colour;

	float3 c = saturate( colour );

	float square = c.b * ( size - 1.0 );
	float low = floor( square );
	float high = min( low + 1.0, size - 1.0 );

	//Half a texel in from either edge of the square, so the table's ends are its own first and last texels and not a blend with the neighbour's.
	float2 inside = ( c.rg * ( size - 1.0 ) + 0.5 ) / size;

	float2 uvLow = float2( ( low + inside.x ) / size, inside.y );
	float2 uvHigh = float2( ( high + inside.x ) / size, inside.y );

	float3 a = g_tLookupTexture.SampleLevel( uvLow, 0.0 ).rgb;
	float3 b = g_tLookupTexture.SampleLevel( uvHigh, 0.0 ).rgb;

	return lerp( a, b, square - low );
#endif
}

//The grade, as a point effect: the white balance, the curves and the table, on the colour it is handed - see CorePointChain.
float3 Apply( float3 was, float2 uv )
{
	float3 colour = was;

	//The white balance, in the light the world was shaded in.
	if ( g_nGradingMethod == 1 )
		colour *= g_vWhiteBalance;

	//The curves, per channel, on the colour as a screen would show it.
	if ( g_nColorSpace == 1 )
	{
		float3 c = saturate( colour );

		colour = float3(
			Curved( c.r, g_nCurveFramesR, g_vCurveFrameR0, g_vCurveFrameR1, g_vCurveFrameR2, g_vCurveFrameR3, g_vCurveDivisorsR ),
			Curved( c.g, g_nCurveFramesG, g_vCurveFrameG0, g_vCurveFrameG1, g_vCurveFrameG2, g_vCurveFrameG3, g_vCurveDivisorsG ),
			Curved( c.b, g_nCurveFramesB, g_vCurveFrameB0, g_vCurveFrameB1, g_vCurveFrameB2, g_vCurveFrameB3, g_vCurveDivisorsB ) );
	}
	else if ( g_nColorSpace == 2 )
	{
		//The same three curves, but over hue, saturation and value - the component sends them under the
		//red, green and blue names in that order, so the letters here are the slots and not the channels.
		float3 hsv = ToHsv( saturate( colour ) );

		hsv = float3(
			Curved( hsv.x, g_nCurveFramesR, g_vCurveFrameR0, g_vCurveFrameR1, g_vCurveFrameR2, g_vCurveFrameR3, g_vCurveDivisorsR ),
			Curved( hsv.y, g_nCurveFramesG, g_vCurveFrameG0, g_vCurveFrameG1, g_vCurveFrameG2, g_vCurveFrameG3, g_vCurveDivisorsG ),
			Curved( hsv.z, g_nCurveFramesB, g_vCurveFrameB0, g_vCurveFrameB1, g_vCurveFrameB2, g_vCurveFrameB3, g_vCurveDivisorsB ) );

		colour = FromHsv( saturate( hsv ) );
	}

	//And the table, last, over what the curves made.
	if ( g_nGradingMethod == 2 )
		colour = Tabled( colour );

	return max( lerp( was, colour, saturate( g_flBlendFactor ) ), 0.0 );
}
