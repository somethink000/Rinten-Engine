//Self-contained procedural sky driven by sun height: draws the sky and provides SkyLight for ambient

PARAMETERS
{
	//Cloud cover, 0 clear to 1 overcast; tints ambient too. Fold( 15 ) bakes kCloudOn when it's zero
	float g_flCloudCover < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Clouds" ); Fold( 15 ); >;

	//How thick the cloud is: nought a veil the sun shows through as a bright patch, one a heavy sheet with dark bases and no sun.
	float g_flCloudThickness < Default( 0.6 ); Range( 0, 1 ); UiGroup( "Clouds" ); >;

	//The size of the cloud's pattern, in metres across the sheet: bigger is broader, calmer cloud.
	float g_flCloudScale < Default( 2200.0 ); Range( 100, 20000 ); UiGroup( "Clouds" ); >;

	//How high the sheet is, in metres. What decides how much it foreshortens towards the horizon.
	float g_flCloudHeight < Default( 1500.0 ); Range( 100, 10000 ); UiGroup( "Clouds" ); >;

	//Which way the cloud drifts and how fast, in metres a second along the ground - x and z of the world.
	float2 g_vCloudWind < Default2( 6.0, 2.0 ); UiGroup( "Clouds" ); >;
}

//What says this file is a sky: the engine builds the table's entries round SkyLight below only
//for a shader that says so, and SkyBox2D takes a material on one. See CoreResources.IsSky.
#define ENGINE_SKY 1

#include "frame.slang"

//Baked from g_flCloudCover > 0 (Fold 15); zero compiles the cloud sheet out of a clear sky
#ifdef SPECIALIZED
[vk::constant_id( 15 )] const int kCloudOn = 1;
#else
static const int kCloudOn = 1;
#endif

//Underside of a cloud sheet: grey-white, brighter overhead and towards the sun, faintly sun-tinted
float3 OvercastSky( float up, float mu, float strength, float3 hue, float thickness )
{
	float cosTheta = saturate( ( up - 0.5 ) * 2.0 );

	float luminance = 0.6 + ( 0.4 * cosTheta );

	//A little of the sun's side, and a flat sheet has no glare to speak of.
	luminance *= 0.9 + ( 0.2 * mu * mu );

	//The sheet's own colour: nearly white, a touch cool, at the brightness the clear sky's horizon
	//has so exposure lands in the same place. Thickness takes it down towards the grey of a heavy day.
	float3 sheet = lerp( float3( 0.92, 0.94, 0.98 ), float3( 0.66, 0.68, 0.72 ), thickness );

	float3 grey = lerp( float3( 1.0, 1.0, 1.0 ), hue, 0.3 );

	return sheet * grey * ( strength * 1.15 * luminance );
}

//Sky colour by view height and sun cosine (all ambient needs); cover/thickness passed in, not read
float3 SkyColourOf( float height, float mu, bool circumsolar, float cover, float thickness )
{
	if ( !HasSun() ) return SkyGradientOf( height );

	const float3 luma = float3( 0.2126, 0.7152, 0.0722 );

	//Unit length already: the renderer normalises it on the way into the block, and normalising it again in every pixel bought nothing.

	float3 light = SunLight();
	float night = NightFactor();

	//The light's brightness and its colour, kept apart. The first scales the whole sky; the second is what the sky borrows near the
	//horizon, and it has to be borrowed without the dimming or a sunset would be dark orange everywhere rather than orange in one place.
	float strength = max( dot( light, luma ), 1e-4 );
	float3 hue = light / strength;

	//How far towards orange the light has gone: nothing at noon, all of the way when the sun is in the horizon.
	float warmth = saturate( ( hue.r - hue.b ) * 0.6 );

	//One while the sun is low enough for any of that to show. How high the sun stands, which is the frame's and not the pixel's.
	float3 sun = g_frame[0].sunDirection.xyz;

	float low = saturate( 1.0 - sun.y * 2.2 );

	//Straight down is nought, the horizon a half, straight up one.
	float up = saturate( height * 0.5 + 0.5 );

	//And how near this direction is to the sun, which is what tells the two

	float3 sky = float3( 0.0, 0.0, 0.0 );

	//Twilight is the only hour when both halves of this are worth working out, and which half runs follows the
	//sun's height rather than anything about this pixel - so the whole frame takes the same side of both branches.
	if ( night < 1.0 )
	{
		//One logarithm for both powers of the vertical ramp. A pow is a logarithm and an exponential,
		//and these two are powers of the same number - so the logarithm is worth taking once.
		float logUp = log2( max( up, 1e-4 ) );

		//Air scatters most towards where the light was already going and back the way it came, and least across it.
		float phase = 0.5 + ( 0.5 * mu * mu );

		float3 zenith = g_frame[0].zenith.rgb;
		float3 horizon = g_frame[0].horizon.rgb;

		//The horizon takes the colour of the light itself as the sun sinks into it - that colour at the
		//brightness the horizon already had, so what changes is the hue and not how pale the band is.
		float3 warmed = hue * dot( horizon, luma );
		horizon = lerp( horizon, warmed, low * ( 0.45 + warmth * 0.55 ) );

		//Between the two, nearer the zenith's colour but not as deep.
		float3 middle = lerp( horizon, zenith, 0.55 );

		//Deep blue overhead through pale blue to near-white at the horizon, which is the ordinary daytime sky and nine
		//tenths of what this is for. The two exponents are what keep the pale band low and thin instead of half the picture.
		float3 day = lerp( horizon, lerp( middle, zenith, exp2( logUp * 0.75 ) ),
			exp2( logUp * 0.30 ) );

		day *= strength * phase * ( 0.40 + up * 0.60 );

		//The sun's half of the sky warms with it as it sets, while the half behind stays blue. Which is what makes a sunset
		//a sunset rather than a sky somebody has turned orange. mu to the three halves, as a square root and a multiply.
		day = lerp( day, day * hue, saturate( mu * sqrt( mu ) * low * warmth ) );

		//Horizon haze band and wide sun glow, both growing as the sun drops
		float band = 1.0 - up;
		float haze = band * band * sqrt( band ) * ( 0.12 + low * 0.30 );

		if ( circumsolar )
		{
			float mu2 = mu * mu;
			haze += mu2 * mu2 * mu2 * ( 0.20 + low * 0.50 );
		}

		sky = day + ( light * haze );

		//And under cloud, the cloud: what is seen of the sky is the sheet where the sheet is, and the average of the two is what lights
		//the world. The cover goes in squared - a half-clouded sky is still mostly lit by its clear half, which is the brighter of the two.
		if ( cover > 0.0 )
			sky = lerp( sky, OvercastSky( up, mu, strength, hue, thickness ), cover * cover );
	}

	if ( night > 0.0 )
	{
		//Night is the same colours a long way down, rather than a colour of its own: a sky somebody tinted green is still green after dark.
		float3 evening = g_frame[0].zenith.rgb * 0.05 * ( 0.35 + sqrt( up ) * 0.65 );

		sky = night >= 1.0 ? evening : lerp( sky, evening, night );
	}

	//Ground below the horizon, lit by the sun, blended softly to avoid a seam in ambient
	float3 earth = g_frame[0].ground.rgb * lerp( 0.40 + strength * 0.60, 0.10, night );

	return lerp( sky, earth, smoothstep( 0.0, 0.35, -height ) );
}

//The clear sky: the same function with no cloud in it, which is what the drawn sky lays its own sheet of cloud over.
float3 SkyColourOf( float height, float mu, bool circumsolar )
{
	return SkyColourOf( height, mu, circumsolar, 0.0, 0.0 );
}

float3 SkyColour( float3 direction, bool circumsolar )
{
	return SkyColourOf( direction.y, saturate( dot( direction, g_frame[0].sunDirection.xyz ) ), circumsolar );
}

float3 SkyColour( float3 direction )
{
	return SkyColour( direction, true );
}

//The ambient sky the engine asks for (fills the sky table): includes cloud and sun glow, not the disc
float3 SkyLight( float height, float mu )
{
	return SkyColourOf( height, mu, true, saturate( g_flCloudCover ), saturate( g_flCloudThickness ) );
}

//Drawn sky from the sky table; falls back to the full SkyColourOf when there's no table
float3 SkyFromTable( float3 direction )
{
	float3 sky;

	//The table already includes the sun glow, so this is just the read
	if ( HasSun() && SkyTableAt( direction, sky ) ) return sky;

	return SkyColour( direction );
}

struct V2F
{
	float4 position : SV_Position;

	//Where this corner of the screen points, in the world. Interpolated across the triangle, which is exactly the ray for each pixel.
	float3 direction : TEXCOORD0;

#ifdef STEREO
	//Which of a headset's eyes, where both are drawn in one pass. The sky has no DEPTH_ID_FIELD to carry
	//it - it is not one of the world's surfaces - so it carries its own. See Eye() in frame_data.slang.
	nointerpolation uint eyeId : TEXCOORD1;
#endif
};

//View direction from a camera-position-free transform so the sky doesn't drift far from the origin
float3 SkyRay( float2 clip )
{
	float3 ray = mul( float4( clip, 1.0, 1.0 ), EYE_FRAME( rayToWorld ) ).xyz;

	//A frame that filled it in cannot answer nothing here - the three corners of a screen point
	//somewhere - so a zero is the matrix having been left at nothing, and that is the whole of the test.
	if ( dot( ray, ray ) > 0.0 ) return ray;

	float4 world = mul( float4( clip, 1.0, 1.0 ), EYE_FRAME( clipToWorld ) );

	return ( world.xyz / world.w ) - EYE_FRAME( camera ).xyz;
}

[shader("vertex")]
V2F MainVs( uint id : SV_VertexID )
{
	//One triangle covering the screen: (-1,-1), (3,-1), (-1,3) in clip space.
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );
	float2 clip = ( corner * 2.0 ) - 1.0;

	V2F o;

	//At the far plane, so the depth test keeps it behind everything solid.
	o.position = float4( clip, FarDepth, 1.0 );

	o.direction = SkyRay( clip );

#ifdef STEREO
	o.eyeId = ViewOfThisVertex();
#endif

	return o;
}

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

//Half a degree of sky is what the sun and the moon each actually cover, and it is the same half degree for both - which is why an eclipse works
//at all. Three times that here: at any sensible window the real one is four pixels across and reads as a bright dot rather than as a disc.
static const float DiscCos = 0.99966;  //the cosine of a degree and a half
static const float DiscSin = 0.02618;  //and its sine, for measuring across one

//Where the sun's glare - `exp( ( towards - 1 ) * 180 )` - has fallen to a millionth: one minus ln( 1e6 ) / 180.
//Past it the disc and the glare add nothing a picture could hold, and the sky pass skips them. See MainPs.
static const float GlareFloorCos = 0.92325;

//Three numbers out of a cell, and the only random this file has.
float3 Hash33( float3 p )
{
	p = frac( p * float3( 0.1031, 0.1030, 0.0973 ) );
	p += dot( p, p.yxz + 33.33 );
	return frac( ( p.xxy + p.yxx ) * p.zyx );
}

//Procedural stars from a hashed cell grid; darkness reveals them by magnitude at dusk
float3 Stars( float3 direction, float darkness, float time )
{
	//How many cells to a radian, which is the one number that decides how many stars there are: about a degree to a cell puts six
	//hundred of them across an ordinary view, which is a rich night sky and roughly twice what an eye picks out on a good one.
	const float grid = 60.0;

	float3 at = direction * grid;

	//How much of that grid one pixel covers. Worked out before anything below decides whether this pixel
	//holds a star at all: a derivative asked for where only some of the pixels went is not a derivative.
	float across = max( length( fwidth( at ) ), 1e-4 );

	float3 cell = floor( at );
	float3 rnd = Hash33( cell );

	//About one cell in eight has anything in it, and what it has is spread by the cube of its own number - so the
	//faint outnumber the bright the way they do overhead, rather than the sky being an even scattering of equals.
	float magnitude = saturate( ( rnd.z - 0.88 ) * 8.3 );
	magnitude = magnitude * magnitude * magnitude;

	if ( magnitude <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//When this one comes out.
	float shows = saturate( ( darkness - ( 1.0 - magnitude ) ) * 3.0 );

	if ( shows <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//Project the star onto the view sphere so star count doesn't depend on resolution
	float3 point = normalize( cell + 0.5 + ( ( rnd - 0.5 ) * 0.9 ) ) * grid;
	float3 offset = at - point;

	//About 1.5 pixels across at any resolution; sub-pixel stars crawl and shimmer
	float radius = across * 0.85;
	float shape = saturate( 1.0 - ( dot( offset, offset ) / ( radius * radius ) ) );

	shape *= shape;

	if ( shape <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//Air moving between here and there, which is the thing a star does that a planet does not. Its own rate and its
	//own phase per star, or the whole sky pulses at once; stronger low down, where there is more air in the way.
	float low = 1.0 - saturate( direction.y * 1.6 );
	float beat = sin( ( time * ( 2.0 + ( rnd.x * 7.0 ) ) ) + ( rnd.y * 40.0 ) );
	float flicker = 1.0 - ( low * 0.35 * ( 0.5 + ( 0.5 * beat ) ) );

	//Hot ones are blue-white and cool ones orange, and there are more of the cool. Nothing else up
	//there is a colour, so this is most of what keeps a night sky from reading as grey dust on glass.
	float3 tint = lerp( float3( 0.72, 0.82, 1.00 ), float3( 1.00, 0.83, 0.66 ), rnd.y * rnd.y );

	//The brightest go well over white, which is what the bloom is looking for.
	return tint * ( shape * shows * flicker * ( 0.15 + ( magnitude * 2.4 ) ) );
}

//One number out of a cell on the sheet, and the whole of the cloud's random.
float Hash21( float2 p )
{
	p = frac( p * float2( 0.1031, 0.1030 ) );
	p += dot( p, p.yx + 33.33 );
	return frac( ( p.x + p.y ) * p.x );
}

//Smooth noise over the sheet: the cell's four corners blended by a curve, which is what keeps it from reading as a grid.
float Noise2( float2 p )
{
	float2 cell = floor( p );
	float2 f = p - cell;

	f = f * f * ( 3.0 - ( 2.0 * f ) );

	float a = Hash21( cell );
	float b = Hash21( cell + float2( 1.0, 0.0 ) );
	float c = Hash21( cell + float2( 0.0, 1.0 ) );
	float d = Hash21( cell + float2( 1.0, 1.0 ) );

	return lerp( lerp( a, b, f.x ), lerp( c, d, f.x ), f.y );
}

//Five octaves of that, each half the size and half the weight of the last and turned a little, so no octave's grid lines up with another's.
//Nought to one, weighted towards the middle, which is the shape of a cloud sheet: broad slow swells with fine detail torn across them.
float CloudNoise( float2 p )
{
	const float2x2 turn = float2x2( 0.80, 0.60, -0.60, 0.80 );

	float sum = 0.0;
	float weight = 0.5;
	float total = 0.0;

	for ( int i = 0; i < 5; i++ )
	{
		sum += Noise2( p ) * weight;
		total += weight;
		weight *= 0.5;
		p = mul( turn, p ) * 2.03 + 17.0;
	}

	return sum / total;
}

//Flat noise cloud sheet: coverage, colour and haze; a second tap towards the sun lights the rims
float3 Clouds( float3 direction, float3 sun, float3 light, float night, out float alpha, out float haze, out float3 hazeColour )
{
	alpha = 0.0;
	haze = 0.0;
	hazeColour = float3( 0.0, 0.0, 0.0 );

	float up = direction.y;

	//Fade cloud into haze near the horizon where the noise would alias
	float horizon = smoothstep( 0.015, 0.16, up );

	//The sun as the sky's colour splits it: brightness and hue.
	const float3 luma = float3( 0.2126, 0.7152, 0.0722 );
	float strength = max( dot( light, luma ), 1e-4 );
	float3 hue = light / strength;

	float mu = saturate( dot( direction, sun ) );

	//Haze under the sheet takes the overcast colour, full along the horizon and half overhead
	hazeColour = SkyColourOf( up, mu, false, 1.0, g_flCloudThickness );
	haze = g_flCloudCover * g_flCloudCover * lerp( 1.0, 0.5, horizon );

	if ( horizon <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//The wind's drift is the wind by the scene's own clock, so the cloud stands still when the scene is paused.
	float2 along = direction.xz * ( g_flCloudHeight / max( up, 0.015 ) );
	float2 at = ( along + ( g_vCloudWind * g_frame[0].time.z ) ) / g_flCloudScale;

	//Which way across the sheet the sun is: what the second read is offset by.
	float2 towardsSun = sun.xz / max( sun.y, 0.15 ) * ( g_flCloudHeight / g_flCloudScale ) * 0.035;

	float here = CloudNoise( at );
	float lit = CloudNoise( at + towardsSun );

	//Where the sheet stands above the line there is cloud: the line sits at one minus the cover, and it is crossed
	//over a band - wider for thin cloud, whose edges are soft, tighter for heavy cloud that ends where it ends.
	float edge = lerp( 0.20, 0.10, g_flCloudThickness );
	float line = 1.0 - g_flCloudCover;

	float density = smoothstep( line - edge, line + edge, here );

	if ( density <= 0.0 ) return float3( 0.0, 0.0, 0.0 );

	//Depth into the sheet from the noise itself, so overcast isn't one flat grey
	float depth = saturate( ( here - line ) / max( 1.0 - line, 0.05 ) );
	float depthLit = saturate( ( lit - line ) / max( 1.0 - line, 0.05 ) );

	//How much of the light the cloud stops here: its thickness setting, deeper into the sheet.
	float thick = density * lerp( 0.35, 1.0, g_flCloudThickness ) * ( 0.4 + ( 0.6 * depth ) );

	//The sheet's own grey, darker at its heavy bases and paler where it thins - about the grey the table
	//holds, taken over the sheet, so what lights a wall and what is drawn behind it are the same sky.
	float3 colour = OvercastSky( up * 0.5 + 0.5, mu, strength, hue, g_flCloudThickness ) * lerp( 1.25, 0.7, thick );

	//The sun through it: a tight patch and a wider glow, both gone where the cloud is heavy.
	float mu2 = mu * mu;
	float mu8 = mu2 * mu2 * mu2 * mu2;
	float through = ( mu8 * mu8 * mu8 * 2.2 ) + ( mu8 * 0.18 );

	colour += light * through * ( 1.0 - thick ) * ( 1.0 - thick );

	//The lit rim: where the sheet is thinner towards the sun than it is here, the light comes round its edge. Stronger the nearer the sun.
	float rim = saturate( ( depth - depthLit ) * 3.0 ) * ( 0.25 + ( 0.75 * mu2 ) );

	colour += light * rim * 0.45 * ( 1.0 - ( g_flCloudThickness * 0.5 ) );

	//Dark after dark: a cloud at night is where the stars are not.
	colour *= lerp( 1.0, 0.04, night );

	alpha = density * horizon;

	return colour;
}

//Moon opposite the sun, turned 60 degrees around up so a lit terminator shows its shape
float3 MoonDirection( float3 sun )
{
	const float turned = 0.5;        //the cosine of sixty degrees
	const float sideon = 0.8660254;  //and its sine

	//Lean sideways and level so the moon still rises as the sun sets; fallback for a zenith sun
	float3 flat = float3( sun.z, 0.0, -sun.x );
	float spread = dot( flat, flat );

	float3 side = spread > 1e-8 ? flat * rsqrt( spread ) : float3( 1.0, 0.0, 0.0 );

	return -( ( sun * turned ) + ( side * sideon ) );
}

[shader("fragment")]
Out MainPs( V2F i )
{
#ifdef STEREO
	g_eyeId = i.eyeId;
#endif

	//A frame with no sky in it leaves the picture as it found it, rather than painting the whole of it
	//black. The renderer skips this pass in that case; this is what makes the pass safe to draw anyway.
	if ( !HasSky() ) discard;

	float3 direction = normalize( i.direction );

	//Clear sky under per-pixel cloud; kCloudOn folds the cloud path out entirely when there's no cover
	bool clouded = kCloudOn != 0 && HasSun() && g_flCloudCover > 0.0;

	float3 colour = clouded ? SkyColour( direction ) : SkyFromTable( direction );

	float3 overcast = float3( 0.0, 0.0, 0.0 );
	float3 hazeColour = float3( 0.0, 0.0, 0.0 );
	float cloudAlpha = 0.0;
	float haze = 0.0;

	if ( HasSun() )
	{
		//Unit length already - the renderer normalises it into the block.
		float3 sun = g_frame[0].sunDirection.xyz;

		float night = NightFactor();

		//One-pixel horizon edge (via fwidth) that cuts the sun and moon off instead of fading them
		float horizon = saturate( ( direction.y / max( fwidth( direction.y ), 1e-7 ) ) + 0.5 );

		//The moon and the stars, where the frame has any night in it at all - see kNightOn, which is NightFactor's own answer taken at bake time.
		if ( kNightOn != 0 && night > 0.0 )
		{
			float3 moon = MoonDirection( sun );

			//The disc's own measurements: how far along the moon this ray is, and how far out from the middle
			//of its face - nought at the centre of it, one at the limb, more than one for the rest of the sky.
			float towards = dot( direction, moon );
			float3 sideways = direction - ( moon * towards );

			float outward = length( sideways );
			float r = outward / DiscSin;

			//A pixel of softness at the rim, the same way the horizon gets one.
			float edge = saturate( ( 1.0 - r ) / max( fwidth( r ), 1e-4 ) );

			//Which way the ground under this part of the moon faces. A ball far enough away is a disc whose surface
			//leans as it goes out: straight back at the viewer in the middle of the face, along the surface at the limb.
			float3 lean = sideways / max( outward, 1e-6 );
			float3 under = ( lean * saturate( r ) ) - ( moon * sqrt( saturate( 1.0 - ( r * r ) ) ) );

			//And so which part of it the sun is on, which is the phase - worked out rather than drawn. The terminator is where the sun
			//goes round the curve, and it sits where the geometry puts it: turn the light and the moon fills and empties on its own.
			float lit = smoothstep( -0.07, 0.12, dot( under, sun ) );

			//A little darker at the rim, so the face reads as curved. The real moon does the opposite - dust scatters
			//light straight back and a full one is a flat bright coin - but a coin is what this is trying not to look like.
			float curve = 0.72 + ( 0.28 * sqrt( saturate( 1.0 - ( r * r ) ) ) );

			float disc = edge * lit * curve;

			//The air immediately round it. Tight: a moon has a small hard halo, not the wide wash a sun has.
			float halo = exp( ( towards - 1.0 ) * 900.0 ) * 0.5;

			const float3 moonlight = float3( 0.94, 0.92, 0.88 );

			//The stars, with the moon standing in front of whichever of them it covers. The disc and not
			//the halo: the halo is air, and air does not hide a star, it only makes it harder to pick out.
#ifdef SKY_CAPTURE
			//Points far under a capture texel, which would only flicker in it - and twinkle it into redrawing
			float3 stars = float3( 0.0, 0.0, 0.0 );
#else
			float3 stars = Stars( direction, night, g_frame[0].time.x ) * ( 1.0 - edge );
#endif

			colour += ( stars + ( moonlight * ( ( disc * 1.5 ) + halo ) * night ) ) * horizon;
		}

#ifndef SKY_CAPTURE
		//And the sun, where there is any day in it - kSunDiscOn, the other half of the same answer. Both are one on the frames that are in between.
		if ( kSunDiscOn != 0 && night < 1.0 )
		{
			//The sun. Its light is what is left after the air it came through - the same figure the sky was coloured by, so the disc
			//reddens and dims into the horizon along with everything round it, and a sun low enough to look at is dim enough to look at.
			float3 light = SunLight();

			float towards = dot( direction, sun );

			//A degree and a half of sky, with a pixel of softness at the rim. The derivative first, where every pixel of the quad still takes it.
			float soft = max( fwidth( towards ), 1e-7 );

			//Skip disc and glare past ~22 degrees from the sun, where they add under a millionth
			if ( towards > GlareFloorCos )
			{
				float disc = smoothstep( DiscCos - soft, DiscCos + soft, towards );

				//Limb darkening: the middle of the disc is brighter than its rim, because at the rim you are looking through the sun's atmosphere at
				//a slant and seeing a shallower, cooler part of it. It is a small thing and it is most of what tells a drawn sun from a white circle.
				float limb = saturate( ( towards - DiscCos ) / ( 1.0 - DiscCos ) );

				disc *= 0.62 + ( 0.38 * sqrt( limb ) );

				//And the tight glare just off the disc. An exponential rather than a hundred and eightieth power: this close to one it is the same
				//curve, and it is one transcendental instead of two. The wide glow further out is the sky's own, worked out with the rest of the haze.
				float glare = exp( ( towards - 1.0 ) * 180.0 ) * 0.45;

				//Well over white at noon, which is what the bloom is looking for, and down to a
				//tenth of that at the horizon without a second number to tune - the air took it.
				colour += light * ( ( disc * 8.0 ) + glare ) * horizon;
			}
		}
#endif

		if ( clouded )
			overcast = Clouds( direction, sun, SunLight(), night, cloudAlpha, haze, hazeColour );
	}

	//The haze under the sheet, then the cloud over all of it - the sun, the moon and the stars go behind both.
	colour = lerp( colour, hazeColour, haze );
	colour = lerp( colour, overcast, cloudAlpha );

	//A fog volume standing between the eye and the sky fogs the sky too. The gradient fog does
	//not - what is far away takes its colour from the sky, and the sky is that colour already.
#ifndef SKY_CAPTURE
	if ( kFogOn != 0 )
	{
		float3 eye = EYE_FRAME( camera ).xyz;
		colour = VolumeFog( colour, eye + direction * 4000.0, eye, i.position.xy );
	}
#endif

	Out o;
	o.colour = float4( colour, 1.0 );

#ifdef TARGET_SCENE
	//No normal and no reflectivity: the sky is not a surface, and everything that reads that attachment
	//afterwards should leave it alone. A zero normal is what those passes take as "there is nothing here".
	o.surface = float4( 0.0, 0.0, 0.0, 0.0 );
#endif

	//Tonemap when drawing straight to screen; alpha must be opaque or the Linux window shows through
#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), 1.0 );
#endif

	return o;
}
