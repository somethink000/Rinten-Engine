//The frame's sky light from the sky's capture: GGX reflection levels and the irradiance picture - see CoreSkyLight.
//All three pictures are square and octahedral, laid out as SkyLightUv; a texel's solid angle is (2/side)² · L1³

[[vk::push_constant]]
cbuffer Push
{
	//The capture's table index x, its side y, the roughness this level is filtered for w
	float4 g_source;

	//The target's side x, the capture level the irradiance reads z
	float4 g_target;
};

#include "frame.slang"

//GGX samples a reflection texel takes; reading the level matching each one's share of the lobe keeps so few clean
static const uint ReflectionSamples = 32u;

//Texels a side of the capture level the irradiance sums: under 2% off beside a low sun's glow, where eight is 6%
static const uint IrradianceTexels = 16u;

[shader("vertex")]
float4 MainVs( uint id : SV_VertexID ) : SV_Position
{
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );
	return float4( ( corner * 2.0 ) - 1.0, 0.0, 1.0 );
}

float3 Capture( float3 direction, float level )
{
	return g_textures[uint( g_source.x )].SampleLevel( SkyLightUv( direction ), level ).rgb;
}

//Karis's split-sum prefilter with the view along the normal; Krivanek's filtered importance sampling picks each sample's level
[shader("fragment")]
float4 MainPrefilterPs( float4 position : SV_Position ) : SV_Target0
{
	float3 normal = normalize( SkyLightRay( position.xy / g_target.x ) );
	float alpha = g_source.w * g_source.w;

	if ( alpha <= 0.0 ) return float4( Capture( normal, 0.0 ), 1.0 );

	float3 up = abs( normal.y ) < 0.999 ? float3( 0.0, 1.0, 0.0 ) : float3( 1.0, 0.0, 0.0 );
	float3 tangent = normalize( cross( up, normal ) );
	float3 bitangent = cross( normal, tangent );

	float alpha2 = alpha * alpha;
	float texelArea = 4.0 / ( g_source.y * g_source.y );

	float3 sum = float3( 0.0, 0.0, 0.0 );
	float weight = 0.0;

	//Unrolled, so the reads go out together rather than one waiting on the last
	[unroll]
	for ( uint i = 0u; i < ReflectionSamples; i++ )
	{
		//Hammersley: an even spread with the index's radical inverse
		float e1 = float( i ) / float( ReflectionSamples );
		float e2 = float( reversebits( i ) ) * 2.3283064365386963e-10;

		float cosTheta = sqrt( ( 1.0 - e2 ) / ( 1.0 + ( ( alpha2 - 1.0 ) * e2 ) ) );
		float sinTheta = sqrt( 1.0 - ( cosTheta * cosTheta ) );
		float phi = 6.2831853 * e1;

		float3 mid = ( tangent * ( sinTheta * cos( phi ) ) ) + ( bitangent * ( sinTheta * sin( phi ) ) ) + ( normal * cosTheta );
		float3 toward = ( mid * ( 2.0 * dot( normal, mid ) ) ) - normal;

		float lit = max( dot( normal, toward ), 0.0 );

		//View along the normal makes the pdf D / 4; its solid angle against the texel's there picks the level
		float d = ( cosTheta * cosTheta * ( alpha2 - 1.0 ) ) + 1.0;
		float pdf = alpha2 / ( 3.14159265 * d * d ) * 0.25;
		float spread = abs( toward.x ) + abs( toward.y ) + abs( toward.z );
		float texelAngle = texelArea * spread * spread * spread;
		float sampleAngle = 1.0 / ( float( ReflectionSamples ) * max( pdf, 1e-6 ) );
		float level = max( 0.0, ( 0.5 * log2( sampleAngle / texelAngle ) ) + 1.0 );

		sum += Capture( toward, level ) * lit;
		weight += lit;
	}

	return float4( weight > 0.0 ? sum / weight : float3( 0.0, 0.0, 0.0 ), 1.0 );
}

//Cosine-weighted mean radiance about each direction (irradiance over pi), from one small capture level's texels
[shader("fragment")]
float4 MainIrradiancePs( float4 position : SV_Position ) : SV_Target0
{
	float3 normal = normalize( SkyLightRay( position.xy / g_target.x ) );

	float3 sum = float3( 0.0, 0.0, 0.0 );
	float weight = 0.0;

	for ( uint y = 0u; y < IrradianceTexels; y++ )
	{
		[unroll]
		for ( uint x = 0u; x < IrradianceTexels; x++ )
		{
			float2 uv = ( float2( x, y ) + 0.5 ) / float( IrradianceTexels );
			float3 ray = SkyLightRay( uv );

			//The cosine over the ray's length, and the texel's solid angle, one over its length cubed
			float along = dot( ray, ray );
			float w = max( dot( normal, ray ), 0.0 ) / ( along * along );

			sum += g_textures[uint( g_source.x )].SampleLevel( uv, g_target.z ).rgb * w;
			weight += w;
		}
	}

	return float4( weight > 0.0 ? sum / weight : float3( 0.0, 0.0, 0.0 ), 1.0 );
}
