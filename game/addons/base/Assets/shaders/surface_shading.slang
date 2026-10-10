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

//x to the fifth, as four multiplies rather than a log and an exp. This is Schlick's exponent
//and it is on the per-light path, where a pow is three instructions doing the work of one.
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

			//Half the height either side of the middle, so the picture as a whole stays where it was
			//put; the floor under the divide stops a grazing angle sending it off across the box.
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

	for ( int index = 0; index < count; ++index )
	{
		uint which = LightHere( walk, uint( index ) );

		Light light = LightAt( which );

		float3 l;
		float attenuation = 1.0;

		//Out here for the shadow too: a lamp map texel's world size depends on the receiver's distance
		float distance = 0.0;

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

		if ( lit.r <= 0.0 || attenuation <= 0.0 ) continue;

		float shadow;

		//Where the bake holds this light's static shadow (Light.Baked); its maps then hold only movers
		int channel = LightMaskChannel( light );

		if ( light.colour.w < 0.5 )
		{
			//The sun's static pages and movers map, for cluster and loose draws alike - see SunShadow; not the pages
			//where the mask has the static answer
			shadow = SunShadow( s.world, geometric, geometric, true, channel < 0 );
			shadow = BakedShadow( shadow, s.world, geometric, s.lightmap, channel );
		}
		else
		{
			//Out of the lamp's faces, where it casts - see LampShadow.
			shadow = LampShadow( s.world, geometric, light, distance );
			shadow = BakedShadow( shadow, s.world, geometric, s.lightmap, channel );
		}

		//Shadow blocks only this light; lights add independently, so nothing gives it back
		if ( shadow <= 0.0 ) continue;

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

		float3 reaching = light.colour.rgb * nDotL * attenuation * shadow;

		//A shadow's edge let through redder where light scatters under it.
		float3 shade = scatter > 0.0
			? pow( float3( shadow, shadow, shadow ), float3( 1.0, 1.0, 1.0 ) - ( float3( 0.6, 0.25, 0.1 ) * scatter ) )
			: float3( shadow, shadow, shadow );

		litDiffuse += diffuse * light.colour.rgb * lit * attenuation * shade;

		litSpecular += specular * reaching;
	}

	//A small bright highlight can exceed a half float; clamped so the picture and bloom get the most it can say
	litSpecular = min( litSpecular, 32768.0.xxx );

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

	float3 lit = litDiffuse + litSpecular;

	//Lit by the sky table where there is a sky, so a wall facing it takes its colour; none without one
	float3 sky = HasSky() ? SkyAmbient( skyward ) * SkyAmbientScale() : float3( 0.0, 0.0, 0.0 );

	//A probe pictures the room's walls, so inside its box it replaces the sky rather than adding to it
	ProbeMix probes = ProbesHere( s.world );

	//Along the normal at the roughest band, nearest a probe gets to the average light arriving here
	sky = ProbesOver( probes, sky, s.world, skyward, 1.0, false );

	//And the scene's own ambient light over both, as much as it asks - see SceneAmbient.
	sky = SceneAmbient( sky );

	//With a bake, its answer replaces the sky and AO guess rather than adding to it
	float3 ambientSky = sky;

	//Sky visibility from the bake ratio, so indoor floors don't mirror the full sky
	float skyReach = 1.0;

#ifndef PORTABLE
	if ( HasBakedSheet() && ( s.lightmap.x + s.lightmap.y ) > 0.0 )
	{
		ambientSky = BakedSheet( s.lightmap, normal );
		visible = 1.0;

		skyReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
	else if ( HasBakedVolume() )
	{
		//A step off the surface, so a floor reads the room above it rather than the lattice's layer below.
		ambientSky = BakedVolume( s.world + ( geometric * ( 0.35 / g_frame[0].volumeOrigin.w ) ), normal );

		skyReach = saturate( LightStrength( ambientSky ) / max( LightStrength( sky ), 1e-4 ) );
	}
#endif

	//Metal has no diffuse to light: what it does with the sky is reflect it, which is what the rest of this does.
	float3 ambient = albedo * ambientSky * ( 1.0 - metalness ) * occlusion * visible;

	//Sky/probe reflection everywhere, not only in probe boxes, so metals aren't black outdoors
	float3 away = reflect( -v, normal );

	float3 around = EnvironmentAt( probes, s.world, normal, away, roughness ) * skyReach;

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

	float3 shaded = lit + ambient + reflected + s.emission;

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
	float mirror = LightStrength( reflectance ) * skyReach;

	o.mirror = mirror * saturate( 1.0 - ( roughness * 2.0 ) );

	return o;
}

Shaded ShadeSurface( Surface s )
{
	return ShadeSurfaceWith( s, 0.0 );
}

#endif
