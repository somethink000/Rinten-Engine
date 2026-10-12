//The bake's denoiser (BakeDenoise): joint non-local means - each texel the mean of those around it whose patch of
//light, albedo and normal is alike, so edges stay.
//
//Based on YoctoImageDenoiser's JNLM implementation with corrections from "Nonlinearly Weighted First-order Regression for
//Denoising Monte Carlo Renderings" (<https://benedikt-bitterli.me/nfor/nfor.pdf>).
//Copyright (c) 2014-present Godot Engine contributors (MIT).
//
//MIT License
//
//Copyright (c) 2020 ManuelPrandini
//
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
//files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
//modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software
//is furnished to do so, subject to the following conditions:
//
//The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
//OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
//LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
//IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

//The light rgb and the variance of its mean in a, a texel of the square a float4
[[vk::binding(0,0)]] StructuredBuffer<float4> g_light;

//Albedo rgb, and one in a where the texel was pushed out of something (the unocclude mask)
[[vk::binding(1,0)]] StructuredBuffer<float4> g_albedo;

//Normal xyz, nought where no surface owns the texel
[[vk::binding(2,0)]] StructuredBuffer<float4> g_normal;

[[vk::binding(3,0)]] RWStructuredBuffer<float4> g_out;

//Three more of each texel - its light's first band a colour - smoothed by the same weights
[[vk::binding(4,0)]] StructuredBuffer<float4> g_bands;
[[vk::binding(5,0)]] RWStructuredBuffer<float4> g_bandsOut;

[[vk::push_constant]]
cbuffer Push
{
	uint g_width;
	uint g_height;
	uint g_regionX;
	uint g_regionY;

	int g_halfSearch;
	float g_spatial;
	float g_lightBand;
	float g_albedoBand;

	float g_normalBand;
	uint g_pad0, g_pad1, g_pad2;
};

//Half the patch compared around each texel: 3 is a 7x7
static const int HalfPatch = 3;

float4 Light( int2 at )
{
	if ( any( at < 0 ) || at.x >= int( g_width ) || at.y >= int( g_height ) ) return float4( 0.0, 0.0, 0.0, 0.0 );
	return g_light[( at.y * int( g_width ) ) + at.x];
}

float4 Albedo( int2 at )
{
	if ( any( at < 0 ) || at.x >= int( g_width ) || at.y >= int( g_height ) ) return float4( 0.0, 0.0, 0.0, 0.0 );
	return g_albedo[( at.y * int( g_width ) ) + at.x];
}

float3 Normal( int2 at )
{
	if ( any( at < 0 ) || at.x >= int( g_width ) || at.y >= int( g_height ) ) return float3( 0.0, 0.0, 0.0 );
	return g_normal[( at.y * int( g_width ) ) + at.x].xyz;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void main( uint3 id : SV_DispatchThreadID )
{
	int2 at = int2( id.xy ) + int2( g_regionX, g_regionY );
	if ( at.x >= int( g_width ) || at.y >= int( g_height ) ) return;

	const float Epsilon = 1e-6;
	const int PatchSide = ( HalfPatch * 2 ) + 1;
	const int PatchArea = PatchSide * PatchSide;

	float twoSpatial = 2.0 * g_spatial * g_spatial;
	float bandSquared = g_lightBand * g_lightBand;
	float twoAlbedo = 2.0 * g_albedoBand * g_albedoBand;
	float twoNormal = 2.0 * g_normalBand * g_normalBand;

	float4 input = Light( at );
	float3 inputAlbedo = Albedo( at ).rgb;
	float3 inputNormal = Normal( at );

	uint here = ( uint( at.y ) * g_width ) + uint( at.x );
	float3 denoised = input.rgb;
	float3 bands[3] = { g_bands[here * 3u].xyz, g_bands[( here * 3u ) + 1u].xyz, g_bands[( here * 3u ) + 2u].xyz };

	if ( length( inputNormal ) > Epsilon )
	{
		float3 sum = 0.0;
		float3 bandSums[3] = { float3( 0.0, 0.0, 0.0 ), float3( 0.0, 0.0, 0.0 ), float3( 0.0, 0.0, 0.0 ) };
		float total = 0.0;

		for ( int sy = -g_halfSearch; sy <= g_halfSearch; sy++ )
		{
			for ( int sx = -g_halfSearch; sx <= g_halfSearch; sx++ )
			{
				int2 search = at + int2( sx, sy );
				float3 searchNormal = Normal( search );

				//Out of the square, or no surface there
				if ( length( searchNormal ) <= Epsilon ) continue;

				float4 searchAlbedo = Albedo( search );
				float patch = 0.0;

				//Rousselle et al.'s distance: the difference less what both texels' noise explains, over that noise - a
				//blob the samples cannot tell from noise is smoothed, an edge the variance cannot explain stays
				for ( int py = -HalfPatch; py <= HalfPatch; py++ )
				{
					for ( int px = -HalfPatch; px <= HalfPatch; px++ )
					{
						float4 mine = Light( at + int2( px, py ) );
						float4 theirs = Light( search + int2( px, py ) );
						float3 delta = mine.rgb - theirs.rgb;

						patch += ( ( dot( delta, delta ) / 3.0 ) - ( mine.a + min( mine.a, theirs.a ) ) )
							/ ( 1e-4 + ( bandSquared * ( mine.a + theirs.a ) ) );
					}
				}

				patch = max( 0.0, patch / float( PatchArea ) );

				float weight = exp( -float( ( sx * sx ) + ( sy * sy ) ) / twoSpatial );
				weight *= exp( -patch );

				float3 albedoDelta = inputAlbedo - searchAlbedo.rgb;
				weight *= exp( -dot( albedoDelta, albedoDelta ) / twoAlbedo );

				float3 normalDelta = inputNormal - searchNormal;
				weight *= exp( -dot( normalDelta, normalDelta ) / twoNormal );

				//Not from a texel pushed out of something
				weight *= 1.0 - searchAlbedo.a;

				sum += weight * Light( search ).rgb;
				total += weight;

				uint there = ( uint( search.y ) * g_width ) + uint( search.x );

				[unroll]
				for ( uint c = 0u; c < 3u; ++c ) bandSums[c] += weight * g_bands[( there * 3u ) + c].xyz;
			}
		}

		if ( total > Epsilon )
		{
			denoised = sum / total;

			[unroll]
			for ( uint c = 0u; c < 3u; ++c ) bands[c] = bandSums[c] / total;
		}
	}

	g_out[here] = float4( denoised, input.a );

	[unroll]
	for ( uint c = 0u; c < 3u; ++c ) g_bandsOut[( here * 3u ) + c] = float4( bands[c], 0.0 );
}
