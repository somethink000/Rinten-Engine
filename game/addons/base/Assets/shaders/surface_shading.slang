//Shared surface lighting (lights, shadows, sky, probes, fog) for standard and terrain shaders

#ifndef ENGINE_SURFACE_SHADING_INCLUDED
#define ENGINE_SURFACE_SHADING_INCLUDED

//What the light loop needs of roughness, worked out once since it is the same for every light
struct Rough
{
	//Trowbridge-Reitz's alpha squared.
	float a2;

	//Schlick's k, with the direct-lighting remapping.
	float k;
};

Rough RoughFor( float roughness )
{
	float a = roughness * roughness;

	Rough r;
	r.a2 = a * a;
	r.k = ( roughness + 1.0 ) * ( roughness + 1.0 ) / 8.0;

	return r;
}

//GGX distribution; the divisor guard must stay tiny or sharp highlights get clipped flat
float DistributionGGX( float nDotH, Rough r )
{
	float d = nDotH * nDotH * ( r.a2 - 1.0 ) + 1.0;

	return r.a2 / max( 3.14159265 * d * d, 1e-20 );
}

float GeometrySmith( float nDotV, float nDotL, Rough r )
{
	float v = nDotV / ( nDotV * ( 1.0 - r.k ) + r.k );
	float l = nDotL / ( nDotL * ( 1.0 - r.k ) + r.k );

	return v * l;
}

//Fifth power as four multiplies: Schlick's exponent on the per-light path, where pow costs three instructions
float Fifth( float x )
{
	float square = x * x;
	return square * square * x;
}

float3 FresnelSchlick( float cosine, float3 f0 )
{
	float f = Fifth( saturate( 1.0 - cosine ) );
	return f0 + ( 1.0 - f0 ) * f;
}

//Environment BRDF (split sum second half) via Lazarov's analytic fit instead of a LUT texture
float3 EnvBrdf( float3 f0, float roughness, float nDotV )
{
	const float4 c0 = float4( -1.0, -0.0275, -0.572, 0.022 );
	const float4 c1 = float4( 1.0, 0.0425, 1.04, -0.04 );

	float4 r = ( roughness * c0 ) + c1;
	float a004 = ( min( r.x * r.x, exp2( -9.28 * nDotV ) ) * r.x ) + r.y;

	return ( f0 * ( ( a004 * -1.04 ) + r.z ) ) + ( ( a004 * 1.04 ) + r.w );
}

#ifndef PORTABLE
//A lamp's shape, its direction.w - see PointLight.AreaShape
static const float AreaRectangle = 1.0;
static const float AreaDisc = 2.0;
static const float AreaTube = 3.0;

//A disc drawn as the octagon of its area: the corners' radius over the disc's
static const float AreaOctagon = 1.0539;

//Edge integral of a clamped cosine between unit corners, z of its vector form (Hill and Heitz 2017's fit)
float AreaEdge( float3 a, float3 b )
{
	float x = dot( a, b );
	float y = abs( x );
	float p = 0.8543985 + ( ( 0.4965155 + ( 0.0145206 * y ) ) * y );
	float q = 3.4175940 + ( ( 4.1616724 + y ) * y );
	float t = p / q;
	float thetaOverSin = x > 0.0 ? t : ( 0.5 * rsqrt( max( 1.0 - ( x * x ), 1e-7 ) ) ) - t;

	return ( ( a.x * b.y ) - ( a.y * b.x ) ) * thetaOverSin;
}

//A clamped cosine's share of a polygon's solid angle times two pi, after the 2x2 inverse ( x y ; z w ) over x and z,
//clipped at the horizon on the way round (Sutherland-Hodgman), either winding
float AreaPolygon( float3 corners[8], uint count, float4 inverse )
{
	float sum = 0.0;
	float3 first = float3( 0.0, 0.0, 1.0 );
	float3 last = first;
	bool any = false;

	float3 from = corners[count - 1u];
	from = float3( ( inverse.x * from.x ) + ( inverse.y * from.z ), from.y, ( inverse.z * from.x ) + ( inverse.w * from.z ) );

	[unroll]
	for ( uint i = 0u; i < 8u; ++i )
	{
		if ( i >= count ) break;

		float3 to = corners[i];
		to = float3( ( inverse.x * to.x ) + ( inverse.y * to.z ), to.y, ( inverse.z * to.x ) + ( inverse.w * to.z ) );

		//Where the edge crosses the horizon, then the corner itself if it is above
		if ( ( from.z >= 0.0 ) != ( to.z >= 0.0 ) )
		{
			float3 cut = normalize( from + ( ( to - from ) * ( from.z / ( from.z - to.z ) ) ) );

			if ( any ) sum += AreaEdge( last, cut );
			else first = cut;

			last = cut;
			any = true;
		}

		if ( to.z >= 0.0 )
		{
			float3 corner = normalize( to );

			if ( any ) sum += AreaEdge( last, corner );
			else first = corner;

			last = corner;
			any = true;
		}

		from = to;
	}

	if ( any ) sum += AreaEdge( last, first );

	return abs( sum );
}

//What an area lamp does to a point: diffuse irradiance over its colour x, and its highlight's radiance over its colour yzw
//LTC (Heitz et al. 2016) with CoreAreaLights' tables; the lamp's falloff kept as the ratio of its own to the inverse square
float4 AreaLamp( Light light, float3 world, float3 normal, float3 v, float nDotV, float roughness, float3 f0, float distance )
{
	float3 forward = light.direction.xyz;
	float3 centre = light.position.xyz;
	float shape = light.direction.w;
	float halfWidth = light.cone.x;
	float halfHeight = light.shape.z;

	float3 r0;
	float3 u0;
	LampAxes( forward, r0, u0 );

	float turn = light.shape.w;
	float3 right = ( r0 * cos( turn ) ) + ( u0 * sin( turn ) );
	float3 up = cross( forward, right );

	float3 corners[8];
	uint count = 4u;
	float area;

	if ( shape == AreaTube )
	{
		//A cylinder seen side on: the panel through its axis facing the point, as wide as the tube
		float3 across = cross( right, world - centre );
		float side = length( across );
		across = side > 1e-5 ? across / side : up;

		corners[0] = centre - ( right * halfWidth ) - ( across * halfHeight );
		corners[1] = centre + ( right * halfWidth ) - ( across * halfHeight );
		corners[2] = centre + ( right * halfWidth ) + ( across * halfHeight );
		corners[3] = centre - ( right * halfWidth ) + ( across * halfHeight );
		area = 4.0 * halfWidth * halfHeight;
	}
	else
	{
		//Rectangles and discs give light only forward
		if ( dot( world - centre, forward ) <= 0.0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

		if ( shape == AreaDisc )
		{
			float reach = halfWidth * AreaOctagon;

			[unroll]
			for ( uint k = 0u; k < 8u; ++k )
			{
				float angle = float( k ) * 0.78539816;
				corners[k] = centre + ( ( ( right * cos( angle ) ) + ( up * sin( angle ) ) ) * reach );
			}

			count = 8u;
			area = 3.14159265 * halfWidth * halfWidth;
		}
		else
		{
			corners[0] = centre - ( right * halfWidth ) - ( up * halfHeight );
			corners[1] = centre + ( right * halfWidth ) - ( up * halfHeight );
			corners[2] = centre + ( right * halfWidth ) + ( up * halfHeight );
			corners[3] = centre - ( right * halfWidth ) + ( up * halfHeight );
			area = 4.0 * halfWidth * halfHeight;
		}
	}

	if ( area <= 1e-8 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	//The surface's frame: along the view across it, then about the normal
	float3 t1 = v - ( normal * dot( v, normal ) );
	float t1Length = length( t1 );
	t1 = t1Length > 1e-4 ? t1 / t1Length : ( abs( normal.y ) < 0.9 ? normalize( cross( float3( 0.0, 1.0, 0.0 ), normal ) ) : float3( 1.0, 0.0, 0.0 ) );
	float3 t2 = cross( normal, t1 );

	[unroll]
	for ( uint c = 0u; c < 8u; ++c )
	{
		if ( c >= count ) break;

		float3 d = corners[c] - world;
		corners[c] = float3( dot( d, t1 ), dot( d, t2 ), dot( d, normal ) );
	}

	float diffuse = AreaPolygon( corners, count, float4( 1.0, 0.0, 0.0, 1.0 ) );
	if ( diffuse <= 0.0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	float2 uv = ( float2( roughness, sqrt( 1.0 - saturate( nDotV ) ) ) * ( 63.0 / 64.0 ) ) + ( 0.5 / 64.0 );

	//Frame-uniform - see SkyTableAt.
	float4 inverse = g_textures[uint( g_frame[0].area.x )].SampleLevel( uv, 0.0 );
	float2 scale = g_textures[uint( g_frame[0].area.y )].SampleLevel( uv, 0.0 ).xy;

	float specular = AreaPolygon( corners, count, inverse );

	//The lamp's own falloff over the inverse square, held where the shape is near enough to be seen as a shape
	float reach = distance / max( light.position.w, 0.0001 );
	float window = saturate( 1.0 - ( reach * reach * reach * reach ) );
	float square = max( distance * distance, 0.25 * area );
	float ratio = window * window * ( light.shape.x == 1.0 ? 1.0 : pow( square, 1.0 - light.shape.x ) );

	//Radiance is colour over area; a polygon's integral over two pi is its share of the clamped cosine
	float perColour = ratio / area;
	float3 fresnel = ( f0 * scale.x ) + ( ( 1.0 - f0 ) * scale.y );

	return float4( diffuse * 0.5 * perColour, fresnel * ( specular * 0.15915494 * perColour ) );
}
#endif

//Everything the shading needs to know about a pixel, whoever worked it out.
struct Surface
{
	//Where the pixel is, in the world.
	float3 world;

	//Which way it faces, in the world, unit length and after any map.
	float3 normal;

	//And which way the surface itself faces, before any map - what a ray leaving it is offset along.
	float3 geometric;

	float3 albedo;

	//Passed through untouched: a surface decides its own transparency
	float alpha;

	float roughness;
	float metalness;
	float occlusion;

	float3 emission;

	//The texture coordinate, for the debug view that shows it. Nothing else reads it.
	float2 uv;

	//The instance the cull pass named this surface by, as the ID picture holds it; nought without one
	uint id;

	//This pixel's place in its lightmap chart, and whether it has one: the scene has an atlas and this draw a chart
	float2 lightmap;

	//SV_Position.xy, for the screen-space occlusion the frame may have drawn.
	float2 screen;

#if SURFACE_TRANSMITS
	//A leaf's or thin cloth's light through it from behind, its colour times its amount; and how tightly the glow of a
	//light behind it gathers towards the eye. Only where the shader defines SURFACE_TRANSMITS
	float3 transmission;
	float transmissionSpread;
#endif
};

//The colour, and how much of a mirror the surface is for the passes that come after and never saw it drawn.
struct Shaded
{
	float3 colour;
	float mirror;
};

//Lays the frame's decals over the surface before lighting, in order, each by its own alpha
void ApplyDecals( inout Surface s )
{
	uint count = DecalCount();

	float3 geometric = normalize( s.geometric );
	float3 toEye = EYE_FRAME( camera ).xyz - s.world;

	//World derivatives taken once outside the loop, so the loop may branch freely
	float3 worldDx = ddx( s.world );
	float3 worldDy = ddy( s.world );

	for ( uint i = 0u; i < count; ++i )
	{
		//The cheap test first, where most pixels stop: further from the centre than the box reaches at all
		float4 reach = DecalReach( i );
		float3 rel = s.world - reach.xyz;

		if ( dot( rel, rel ) > reach.w * reach.w ) continue;

		Decal d = DecalAt( i );

		//The pixel in the box's own units: within a half of nought each way is inside.
		float3 box = float3( dot( rel, d.axisX ) * d.overWidth,
			dot( rel, d.axisY ) * d.overHeight,
			dot( rel, d.axisZ ) * d.overDepth );

		if ( any( abs( box ) > 0.5 ) ) continue;

		//Picture coordinate, left at the box's -x and top at +y, with gradients from the world's
		float2 uv = float2( box.x + 0.5, 0.5 - box.y );
		float2 dx = float2( dot( worldDx, d.axisX ) * d.overWidth, -dot( worldDx, d.axisY ) * d.overHeight );
		float2 dy = float2( dot( worldDy, d.axisX ) * d.overWidth, -dot( worldDy, d.axisY ) * d.overHeight );

		//Back faces get no decal; attenuation sets how fast it fades as the surface turns edge-on
		float facing = dot( geometric, d.axisZ );
		if ( facing <= 0.0 ) continue;

		float fade = lerp( 1.0, facing, d.attenuation );

		//And the last tenth of the depth either end thins out rather than stopping
		//dead, so a decal whose box ends inside a wall does not draw a line across it.
		fade *= saturate( ( 0.5 - abs( box.z ) ) * 10.0 );

		//The height map walks the picture along the view for relief and masks it: coverage keeps one (all) to
		//nought (none) of the height, the range softens the edge
		float mask = 1.0;

		if ( d.heightMap != 0u )
		{
			float height = DecalData( DecalSample( d.heightMap, uv, dx, dy, float4( 0.5, 0.5, 0.5, 1.0 ) ).rrr,
				( d.srgb & 8u ) != 0u ).r;

			//The eye's direction in the picture's units, y down the picture.
			float3 eyeBox = float3( dot( toEye, d.axisX ), -dot( toEye, d.axisY ), dot( toEye, d.axisZ ) );
			eyeBox *= rsqrt( max( dot( eyeBox, eyeBox ), 1e-8 ) );

			//Half the height either side of the middle, so the picture stays centred; the floor under the divide
			//stops a grazing angle sending it across the box
			float2 offset = eyeBox.xy * ( height - 0.5 ) * d.parallax / max( eyeBox.z, 0.35 );
			uv += offset;

			float edge = 1.0 - d.coverage;
			float low = edge - d.coverageRange;
			float high = edge + d.coverageRange;
			float t = saturate( ( height - low ) / max( high - low, 1e-4 ) );

			mask = t * t * ( 3.0 - 2.0 * t );
		}

		//The colour picture's alpha is the decal's shape; without one the whole box is the shape.
		float4 colour = DecalSample( d.colourMap, uv, dx, dy, float4( 1.0, 1.0, 1.0, 1.0 ) ) * d.tint;
		float alpha = saturate( colour.a * fade * mask );

		if ( alpha <= 0.002 ) continue;

		if ( d.colourMap != 0u )
			s.albedo = lerp( s.albedo, colour.rgb, alpha * d.colourMix );

		if ( d.normalMap != 0u )
		{
			//The map's frame: x across the box, y down it, z out of the surface; laid against the surface normal,
			//since the surface is what bends
			float3 sampled = DecalData( DecalSample( d.normalMap, uv, dx, dy, float4( 0.5, 0.5, 1.0, 1.0 ) ).rgb,
				( d.srgb & 2u ) != 0u ) * 2.0 - 1.0;

			float3 n = normalize( s.normal );
			float3 t = TangentAgainst( n, d.axisX );
			float3 b = TangentAgainst( n, -d.axisY );

			float3 bent = normalize( sampled.x * t + sampled.y * b + sampled.z * n );

			s.normal = normalize( lerp( n, bent, alpha ) );
		}

		if ( d.rmoMap != 0u )
		{
			float3 rmo = DecalData( DecalSample( d.rmoMap, uv, dx, dy, float4( 1.0, 0.0, 1.0, 1.0 ) ).rgb,
				( d.srgb & 4u ) != 0u );

			s.roughness = clamp( lerp( s.roughness, rmo.r, alpha ), 0.04, 1.0 );
			s.metalness = saturate( lerp( s.metalness, rmo.g, alpha ) );
			s.occlusion = saturate( lerp( s.occlusion, rmo.b, alpha ) );
		}

		if ( d.emissionMap != 0u )
		{
			float3 glow = DecalSample( d.emissionMap, uv, dx, dy, float4( 0.0, 0.0, 0.0, 1.0 ) ).rgb * d.tint.rgb;

			s.emission = lerp( s.emission, glow * d.emission, alpha );
		}
	}
}

//Subsurface scatter (0-1): diffuse wraps past the terminator, red furthest; 0 equals ShadeSurface
Shaded ShadeSurfaceWith( Surface s, float scatter )
{
	//This pixel's turn of the soft shadows' taps - see ShadowFilter
	ShadowTurnAt( s.screen );

	//The decals first, where the frame has any: what they paint is what the lights see - see ApplyDecals.
	if ( kDecalsOn != 0 ) ApplyDecals( s );

	float3 normal = s.normal;
	float3 albedo = s.albedo;

	float roughness = s.roughness;
	float metalness = s.metalness;
	float occlusion = s.occlusion;

	//The surface's own normal rather than the mapped one, for the shadow's step off the surface:
	//a bump map says which way the light bends, not where the surface is. See SunShadow.
	float3 geometric = normalize( s.geometric );

	float3 v = normalize( EYE_FRAME( camera ).xyz - s.world );
	float nDotV = saturate( dot( normal, v ) ) + 0.0001;

	//Diffuse and specular kept apart for debug views; the compiler merges them when unused
	float3 litDiffuse = float3( 0.0, 0.0, 0.0 );
	float3 litSpecular = float3( 0.0, 0.0, 0.0 );

	//View depth, which cuts the light grid and picks the sun's cascade, worked out once
	float viewDepth = mul( float4( s.world, 1.0 ), EYE_FRAME( worldToClip ) ).w;

	//The lights reaching it: a grid cell where the frame built one, else every light - see CoreLights
	LightWalk walk = LightsHere( s.screen, viewDepth );
	int count = int( walk.count );

	//With one light at most per pixel this is no loop: a trip count of one drops the back edge - see kManyLights
	if ( kManyLights == 0 ) count = min( count, 1 );

	//A surface with its own lightmap has the baked lights in it already - see LightBaked
	bool sheetLit = HasBakedSheet() && ( s.lightmap.x + s.lightmap.y ) > 0.0;

	for ( int index = 0; index < count; ++index )
	{
		uint which = LightHere( walk, uint( index ) );

		Light light = LightAt( which );

		float3 l;
		float attenuation = 1.0;

		//Out here for the shadow too: a lamp map texel's world size depends on the receiver's distance
		float distance = 0.0;

#ifndef PORTABLE
		//A point with a shape (AreaLamp): its own diffuse and highlight, the shadow of its centre
		if ( kAreaOn != 0 && light.colour.w > 0.5 && light.colour.w < 1.5 && light.direction.w > 0.5 )
		{
			LightLamp( which, light );

			distance = length( light.position.xyz - s.world );
			if ( distance > light.position.w ) continue;

			float3 areaF0 = lerp( float3( 0.04, 0.04, 0.04 ), albedo, metalness );
			float4 area = AreaLamp( light, s.world, normal, v, nDotV, roughness, areaF0, distance );

			if ( area.x <= 0.0 ) continue;

			//A baked lamp on a lightmapped surface: what movers take of its diffuse taken away, its highlight live
			bool areaTaken = sheetLit && LightBaked( light );
			float areaShadow;
			float areaSeen = areaTaken
				? LampBakedShade( s.world, geometric, light, distance, areaShadow )
				: LampShadow( s.world, geometric, light, distance );

			if ( !areaTaken ) areaShadow = areaSeen;
			if ( areaShadow <= 0.0 && areaSeen <= 0.0 ) continue;

			areaShadow *= areaTaken ? -1.0 : 1.0;
			uint areaWithholds = LightWithholds( light );

			if ( ( areaWithholds & 1u ) == 0u )
				litDiffuse += albedo * ( 1.0 - metalness ) / 3.14159265 * light.colour.rgb * ( area.x * areaShadow );

			if ( ( areaWithholds & 2u ) == 0u )
				litSpecular += light.colour.rgb * area.yzw * areaSeen;

			continue;
		}
#endif

		if ( light.colour.w < 0.5 )
		{
			//The sun: everywhere at once, from one direction.
			l = -light.direction.xyz;
		}
		else if ( kLampsOn == 0 )
		{
			//A frame of only directional lights never gets here, and kLampsOn bakes the falloff, window and cone out
			continue;
		}
		else
		{
			//The three fields a lamp reads and the sun does not, fetched here - see LightAt
			LightLamp( which, light );

			float3 toLight = light.position.xyz - s.world;
			distance = length( toLight );

			if ( distance > light.position.w ) continue;

			l = toLight / max( distance, 0.0001 );

			//Inverse square with a fourth-power window to zero at range, so falloff happens near the edge
			float reach = distance / max( light.position.w, 0.0001 );
			float falloff = saturate( 1.0 - reach * reach * reach * reach );

			//Per-lamp attenuation power; 1 is plain inverse square and skips the pow
			float square = max( distance * distance, 0.0001 );
			float spread = light.shape.x == 1.0 ? square : pow( square, light.shape.x );

			attenuation = falloff * falloff / max( spread, 0.0001 );

			if ( light.colour.w > 1.5 )
			{
				//A spot, narrowed between its two cones.
				float aligned = dot( -l, light.direction.xyz );
				float inner = light.direction.w;
				float outer = light.cone.x;

				attenuation *= saturate( ( aligned - outer ) / max( inner - outer, 0.0001 ) );
			}
		}

		float facing = dot( normal, l );
		float nDotL = saturate( facing );

		//How far past the terminator each colour still takes this light: wrapped diffuse,
		//kept to the energy of a plain one. At nought scatter this is nDotL in all three.
		float3 wrap = float3( 0.5, 0.2, 0.12 ) * scatter;
		float3 lit = saturate( ( facing + wrap ) / ( 1.0 + wrap ) );

		//A baked light on a lightmapped surface is in its lightmap: what movers shade of it is taken away, as
		//UE's stationary lights shadow movers over a lightmap, and its highlight is drawn live as theirs is - a
		//lightmap's one averaged way per texel blurs every lamp's into one smear
		bool taken = sheetLit && LightBaked( light );

#if SURFACE_TRANSMITS
		//Through a leaf or thin cloth: its back to the light, and the glow of a light behind it towards the eye;
		//a baked light's on a lightmapped surface is in the lightmap already - see BakeScene.Transmission
		float through = 0.0;

		if ( attenuation > 0.0 && !taken && any( s.transmission > 0.0 ) && ( LightWithholds( light ) & 1u ) == 0u )
		{
			float behind = saturate( -dot( geometric, l ) );
			float glow = pow( saturate( dot( -v, l ) ), s.transmissionSpread );
			through = ( behind * 0.5 ) + ( glow * ( 0.3 + ( 0.7 * behind ) ) );
		}

		if ( ( lit.r <= 0.0 && through <= 0.0 ) || attenuation <= 0.0 ) continue;
#else
		if ( lit.r <= 0.0 || attenuation <= 0.0 ) continue;
#endif

		float shadow;
		float seen = 0.0;

#if SURFACE_TRANSMITS
		//Stepped off the side the light is on, so a sheet lit from behind is not shadowed by itself; one tap serves
		//the light on its face and the light through it
		float3 side = dot( geometric, l ) >= 0.0 ? geometric : -geometric;
#else
		float3 side = geometric;
#endif

		if ( taken )
		{
			seen = light.colour.w < 0.5
				? SunBakedShade( s.world, geometric, shadow )
				: LampBakedShade( s.world, geometric, light, distance, shadow );
		}
		else if ( light.colour.w < 0.5 )
		{
			//The sun's static pages and movers map, for cluster and loose draws alike - see SunShadow
			shadow = SunShadow( s.world, side, side, true );
		}
		else
		{
			//Out of the lamp's faces, where it casts - see LampShadow.
			shadow = LampShadow( s.world, side, light, distance );
		}

#if SURFACE_TRANSMITS
		if ( through > 0.0 ) litDiffuse += s.transmission * light.colour.rgb * ( through * attenuation * shadow );
		if ( lit.r <= 0.0 ) continue;
#endif

		//Shadow blocks only this light; lights add independently, so nothing gives it back
		if ( !taken ) seen = shadow;
		if ( shadow <= 0.0 && seen <= 0.0 ) continue;

		float sign = taken ? -1.0 : 1.0;

		//What the light is kept out of - see Light.Contribution.
		uint withholds = LightWithholds( light );

		float3 diffuse = ( withholds & 1u ) != 0u ? float3( 0.0, 0.0, 0.0 ) : albedo * ( 1.0 - metalness ) / 3.14159265;

		//Area light highlight: Karis nearest point on the sphere/sun disc, lobe widened by its angle
		float3 mirrored = reflect( -v, normal );
		float sinAngle;
		float3 towards;

		if ( light.colour.w < 0.5 )
		{
			sinAngle = SunSourceTan;

			//Off the disc's middle towards the mirrored view, as far as its rim.
			float3 across = mirrored - ( dot( mirrored, l ) * l );
			float spread = length( across );
			towards = spread > sinAngle
				? normalize( l + ( across * ( sinAngle / spread ) ) )
				: mirrored;
		}
		else
		{
			float radius = light.cone.y;
			sinAngle = saturate( radius / max( distance, 0.0001 ) );

			//The point of the bulb nearest the mirrored view, from this point.
			float3 toLight = l * distance;
			float3 across = ( dot( toLight, mirrored ) * mirrored ) - toLight;
			towards = normalize( toLight + ( across * saturate( radius / max( length( across ), 0.0001 ) ) ) );
		}

		float3 h = normalize( towards + v );
		float nDotH = saturate( dot( normal, h ) );
		float vDotH = saturate( dot( v, h ) );

		//Computed per light, not hoisted: hoisting costs registers and occupancy on this hot stage
		float3 f0 = lerp( float3( 0.04, 0.04, 0.04 ), albedo, metalness );

		Rough rough = RoughFor( roughness );

		//And the lobe the wider for it, which takes its peak down by as much as it spreads it: the
		//light a source reflects does not grow with its size. Karis' widening.
		float widened = rough.a2 + ( 0.25 * sinAngle * ( ( 3.0 * sqrt( rough.a2 ) ) + sinAngle ) / ( vDotH + 0.001 ) );

		float3 specular = DistributionGGX( nDotH, rough ) * ( rough.a2 / widened )
			* GeometrySmith( nDotV, nDotL, rough )
			* FresnelSchlick( vDotH, f0 )
			/ max( 4.0 * nDotV * nDotL, 0.0001 );

		if ( ( withholds & 2u ) != 0u ) specular = float3( 0.0, 0.0, 0.0 );

		float3 reaching = light.colour.rgb * nDotL * attenuation * seen;

		//A shadow's edge let through redder where light scatters under it.
		float3 shade = scatter > 0.0
			? pow( float3( shadow, shadow, shadow ), float3( 1.0, 1.0, 1.0 ) - ( float3( 0.6, 0.25, 0.1 ) * scatter ) )
			: float3( shadow, shadow, shadow );

		litDiffuse += diffuse * light.colour.rgb * lit * attenuation * shade * sign;

		litSpecular += specular * reaching;
	}

	//A small bright highlight can exceed a half float; clamped so the picture and bloom get the most it can say
	litSpecular = min( litSpecular, 32768.0.xxx );

#if SURFACE_TRANSMITS
	//And the sky behind it, a quarter of it; a lightmapped one has its back's light in the lightmap
	if ( !sheetLit && HasSky() ) litDiffuse += s.transmission * SceneAmbient( SkyAmbient( -geometric ) * SkyAmbientScale() ) * 0.25;
#endif

	//What the corner of a room can see of the sky, if the frame worked it out. The picture is half the
	//size of the window, which is why this reads by position rather than by anything the mesh carries.
	float visible = 1.0;

	//The bent normal from the same picture: sky light is taken along it, so a crevice is lit from its opening;
	//its length is how far the directions agreed, and the normal leans that much
	float3 skyward = normal;

	//A picture a pass drew, which the portable frame has none of.
#ifndef PORTABLE
	if ( kOcclusionOn != 0 && g_frame[0].settings.x > 0.0 && g_frame[0].settings.y > 0.0 )
	{
		float2 screen = s.screen / g_frame[0].settings.yz;
		float4 held = g_occlusionMap.SampleLevel( screen, 0.0 );

		visible = lerp( 1.0, held.r, g_frame[0].settings.x );

		//Kept nought to one, in eight bits - see ambient_occlusion.shader.
		float3 bent = ( held.gba * 2.0 ) - 1.0;
		float lean = saturate( length( bent ) ) * g_frame[0].settings.x;
		if ( lean > 0.01 ) skyward = normalize( lerp( normal, normalize( bent ), lean ) );
	}
#endif

	//The light the occlusion's horizons hide, out of last frame's picture: where it is, the occluded share of the
	//ambient is replaced by it, the bake's included - see IndirectPs in ambient_occlusion.shader
	float3 screenLight = float3( 0.0, 0.0, 0.0 );
	float screenVisible = visible;
	bool screenLit = false;

#ifndef PORTABLE
	if ( g_frame[0].screenLight.x > 0.5 && g_frame[0].settings.y > 0.0 )
	{
		//Frame-uniform - see SkyTableAt.
		float4 gathered = g_textures[uint( g_frame[0].screenLight.x )].SampleLevel( s.screen / g_frame[0].settings.yz, 0.0 );
		screenLight = gathered.rgb;
		screenLit = true;

		//Traced: the share of the rays that got out is what sees the sky and the bake, in place of the horizons' guess
		if ( g_frame[0].screenLight.y > 0.5 )
		{
			visible = saturate( gathered.a );
			screenVisible = visible;
		}
	}
#endif

	float3 lit = litDiffuse + litSpecular;

	//Lit by the sky table where there is a sky, so a wall facing it takes its colour; none without one
	float3 sky = HasSky() ? SkyAmbient( skyward ) * SkyAmbientScale() : float3( 0.0, 0.0, 0.0 );

	//A probe pictures the room's walls, so inside its box it replaces the sky rather than adding to it
	ProbeMix probes = ProbesHere( s.world );

	//Along the normal at the roughest band, nearest a probe gets to the average light arriving here
	sky = ProbesOver( probes, sky, s.world, skyward, 1.0, false );

	//And the scene's own ambient light over both, as much as it asks - see SceneAmbient.
	sky = SceneAmbient( sky );

	//With no bake, the probe grid's light where it reaches: the sky as it gets in and every bounce, in place of the
	//sky alone - its sky has the scene's ambient in it already (BakeSky)
	float3 openSky = sky;
	bool inGrid = false;

#ifndef PORTABLE
	float3 gridded;
	inGrid = HasGrid() && GridLight( s.world, skyward, geometric, gridded );
	if ( inGrid ) sky = gridded;
#endif

	//With a bake, its answer replaces the sky and AO guess rather than adding to it
	float3 ambientSky = sky;

	//How much of the sky the reflection sees, so indoor floors don't mirror the full sky; and how much of what probes
	//saw reaches here, as UE normalises its captures by the bake - a dark corner reflects the room darkly
	float skyReach = inGrid ? saturate( LightStrength( sky ) / max( LightStrength( openSky ), 1e-4 ) ) : 1.0;
	float probeReach = skyReach;

#ifndef PORTABLE
	if ( HasBakedSheet() && ( s.lightmap.x + s.lightmap.y ) > 0.0 )
	{
		//Frame-uniform - see SkyTableAt.
		SheetSpot spot = SheetSpotAt( uint( g_frame[0].bakedMaps.x ), s.lightmap );

		ambientSky = BakedSheet( spot, normal, geometric ) + BakedGlowSheets( spot );
		visible = 1.0;

		//The bounce of the lights left out of the bake, as they are now: at the lightmap's detail where the bake linked
		//its texels (CoreLiveBounce.Sheet), else from the volume a step off the surface
		if ( HasBakedBounce() )
			ambientSky += g_frame[0].bakedTexels.x > 0.5
				? SheetFacesLinear( uint( g_frame[0].bakedTexels.x ), uint( g_frame[0].bakedMaps.x ), s.lightmap )
				: BakedBounceSmooth( s.world + ( geometric * ( 0.35 / g_frame[0].volumeOrigin.w ) ), geometric, normal );

		if ( HasBakedSky() && g_frame[0].bakedMaps.z > 0.5 )
		{
			//The sky now, from where the texel sees it, as much as it sees - right at any hour without a new bake
			float4 open = BakedSkySheet( spot );
			float3 way = dot( open.xyz, open.xyz ) > 1e-4 ? normalize( open.xyz ) : normal;

			ambientSky += SkyLit( way ) * open.w;
			skyReach = saturate( open.w );
		}
		else skyReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );

		probeReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
	else if ( HasBakedVolume() )
	{
		//A step off the surface, so a floor reads the room above it rather than the lattice's layer below.
		float3 lifted = s.world + ( geometric * ( 0.35 / g_frame[0].volumeOrigin.w ) );
		float3 spot = BakedVolumeSpot( lifted, geometric );

		ambientSky = BakedVolume( spot, normal ) + BakedGlowVolumes( spot );
		if ( HasBakedBounce() ) ambientSky += BakedBounce( spot, normal );

		if ( HasBakedSky() )
		{
			float seen = BakedSkyVolume( spot, normal );

			ambientSky += SkyLit( normal ) * seen;
			skyReach = seen;
		}
		else skyReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );

		probeReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
#else
	//The portable frame's bake: no directions and no walls, the sky by the normal, the bounce from the volume
	if ( HasBakedSheet() && ( s.lightmap.x + s.lightmap.y ) > 0.0 )
	{
		float4 baked = PortableSheet( s.lightmap );

		ambientSky = baked.rgb + ( SkyLit( normal ) * baked.a );
		visible = 1.0;

		if ( HasPortableVolume() && g_frame[0].portableBake.w > 0.5 )
			ambientSky += PortableVolume( s.world + ( geometric * ( 0.35 / g_frame[0].volumeOrigin.w ) ), true ).rgb;

		skyReach = saturate( baked.a );
		probeReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
	else if ( HasPortableVolume() )
	{
		float3 lifted = s.world + ( geometric * ( 0.35 / g_frame[0].volumeOrigin.w ) );
		float4 baked = PortableVolume( lifted, false );

		ambientSky = baked.rgb + ( SkyLit( normal ) * baked.a );

		if ( g_frame[0].portableBake.w > 0.5 ) ambientSky += PortableVolume( lifted, true ).rgb;

		skyReach = saturate( baked.a );
		probeReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
#endif

	//Metal has no diffuse to light: what it does with the sky is reflect it, which is what the rest of this does.
	if ( screenLit ) visible = screenVisible;

	float3 ambient = albedo * ( ( ambientSky * visible ) + screenLight ) * ( 1.0 - metalness ) * occlusion;

	//Sky/probe reflection everywhere, not only in probe boxes, so metals aren't black outdoors
	float3 away = reflect( -v, normal );

	//The probes' part and the sky's under them, each by its own reach - EnvironmentAt adds them as one, for SSR
	float skyLeft;
	float3 probed = ProbesPart( probes, s.world, away, roughness, skyLeft );
	float3 lookAt = normalize( lerp( away, normal, roughness * roughness ) );
	float3 mirroredSky = HasSky() ? SkyReflected( lookAt, roughness ) : SceneAmbient( float3( 0.0, 0.0, 0.0 ) );
	float3 unscaled = probed + ( mirroredSky * skyLeft );
	float3 around = ( probed * probeReach ) + ( mirroredSky * skyLeft * skyReach );

	//How much of the environment reflection is left after both reaches - what SSR takes the place of
	float reach = saturate( LightStrength( around ) / max( LightStrength( unscaled ), 1e-4 ) );

	//A normal map can point the reflected ray back into the surface it left, and a floor
	//does not reflect its own underside. Squared so it is a fade rather than an edge.
	float horizon = saturate( 1.0 + dot( away, geometric ) );
	horizon *= horizon;

	//What comes back of it: the surface's own colour where it is metal, four per cent where it is not, and at a
	//grazing angle nearly all of whatever it is - which is the whole reason a wet road shines and a dry one does not.
	float3 mirrorColour = lerp( float3( 0.04, 0.04, 0.04 ), albedo, metalness );

	//How much occlusion takes of the reflection: all of the sky's on rough surfaces, less as the surface
	//sharpens (Lagarde and de Rousiers 2014)
	float seen = saturate( pow( nDotV + visible, exp2( ( -16.0 * roughness ) - 1.0 ) ) - 1.0 + visible );

	float3 reflectance = EnvBrdf( mirrorColour, roughness, nDotV ) * occlusion * seen * horizon;
	float3 reflected = around * reflectance;

	//Movers' shadows taken from a lightmap never take more than it holds
	float3 shaded = max( lit + ambient, 0.0 ) + reflected + s.emission;

	//Fog over everything, after emission so a lamp in fog is dimmed rather than floating in front of the haze
	shaded = ApplyFog( shaded, s.world, EYE_FRAME( camera ).xyz, s.screen );

	//Debug view branch gated by kDebugOn at bake time, so normal frames don't pay its registers
	int debug = kDebugOn != 0 ? DebugView() : 0;

	if ( kDebugOn != 0 && debug != 0 )
	{
		shaded = debug == 70
			? SunLevelView( s.world, s.geometric )
			: DebugSurface( debug, albedo, normal, roughness, metalness, occlusion, s.uv, s.id,
				litDiffuse + ambient, litSpecular, shaded );
	}

	Shaded o;
	o.colour = shaded;

	//Mirror weight for the SSR pass: the exact environment weight used here, gone by half rough
	float mirror = LightStrength( reflectance ) * reach;

	o.mirror = mirror * saturate( 1.0 - ( roughness * 2.0 ) );

	return o;
}

Shaded ShadeSurface( Surface s )
{
	return ShadeSurfaceWith( s, 0.0 );
}

#endif
