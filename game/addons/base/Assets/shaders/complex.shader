//The engine's default lit PBR surface; other legacy intents alias here. Built again with SKINNED

FEATURES
{
	//Features are compiled out, not runtime switches; the <...> after one says what turns it on

	//Normal map turns on when a map is set (empty slot binds a flat normal); colour map is on by default
	Feature( F_COLOR_MAP, 0..1, "Maps" ) < Map( ColorMap ); Default( 1 ); >;

	Feature( F_NORMAL_MAP, 0..1, "Maps" ) < Map( Normal ); >;
	//Roughness map, on by default; off drops the map, the value and the sample (fully matte)
	Feature( F_ROUGHNESS, 0..1, "Maps" ) < Map( g_tRoughness ); Default( 1 ); >;

	//Metalness, on to begin with the same way. Off is a surface that is not metal at all, and nothing read to say so.
	Feature( F_METALNESS, 0..1, "Maps" ) < Flag( Metalness ); >;

	//Ambient occlusion: a map turns it on, and a material without one pays no sample for a texel that would have read one.
	Feature( F_AO, 0..1, "Maps" ) < Map( g_tAO ); >;

	//Tint only where a mask says: white takes the tint, black keeps the colour map's own. A tick rather than a map switching it on, so a
	//material can carry its mask and still be drawn untinted-by-mask - painted metal whose rust and dirt must not turn the paint's colour.
	Feature( F_TINT_MASK, 0..1, "Maps" );


	//Emission stays off, which is theirs too: F_EMISSIVE is a tick nobody has ticked, and a surface that emits nothing should not pay to say so.
	Feature( F_EMISSION, 0..1, "Maps" ) < Map( Emission ); >;
	//Cut out: a pixel whose alpha is under the cutoff is not drawn. A feature of the surface rather than a blend mode of
	//the material - the discard costs the whole pass wherever it is built in, so it is built in only where it is ticked.
	Feature( F_ALPHA, 0..1, "Rendering" );

	//See-through: blended with what is behind it and drawn after everything solid, in order. Nothing in the body tests it - it is the draw's state,
	//not the surface's arithmetic - and being a feature is what lets a material say so, and what keeps one that does not from paying for it.
	Feature( F_TRANSLUCENT, 0..1, "Rendering" );

	//Both sides of a sheet drawn, and each lit as though it faced out. The feature is the whole of it: it turns the
	//rasteriser's culling off for the draw as well as building the flipped normal in - see CoreSceneRenderer.DrawFlags.
	Feature( F_BACKFACES, 0..1, "Rendering" );

	//Parallax by height map; off by default since it adds a dependent read before every other sample
	Feature( F_PARALLAX, 0..1, "Maps" ) < Map( g_tHeight ); >;

	//Light through a thin sheet from behind - a curtain, a lampshade, paper; foliage.shader has the same for leaves
	Feature( F_TRANSMISSION, 0..1, "Transmission" );

}

PARAMETERS
{
	//Material numbers; the first four keep their legacy slot. Feature( F_X ) groups a value in the editor

	//Roughness and AO are separate maps so their neutral values are readable; Default is used when unset
	Texture2D g_tRoughness < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;
	Texture2D g_tAO < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_AO ); >;
	//Where the tint applies, in red: one takes all of it, nought none. White where the material
	//named no mask, which is the whole surface tinted - the same as with the tick off.
	Texture2D g_tTintMask < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_TINT_MASK ); >;

	//The roughness where there is no picture - nought a mirror, one entirely matte, the way Blender reads it - and what the picture is multiplied
	//by where there is one. The picture's Default above is white for that reason: it was a half, which made this slider reach only half rough.
	float g_flRoughness < Default( 0.5 ); Range( 0, 2 ); UiGroup( "Maps" ); Feature( F_ROUGHNESS ); >;

	//Metalness the same way: the number where there is no mask, what the mask is multiplied by where there is
	//one. F_METALNESS comes on for any metal at all, so a material that says nothing about it pays nothing.
	float g_flMetalness < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Maps" ); Feature( F_METALNESS ); >;

	Texture2D g_tMetalness < Default( 1.0 ); UiGroup( "Maps" ); Feature( F_METALNESS ); >;
	//How hard the normal map bites: nought is flat, one is the map as painted, two doubles its slopes. Read only where F_NORMAL_MAP built the sample in.
	float g_flNormalStrength < Default( 1.0 ); Range( 0, 2 ); UiGroup( "Maps" ); Feature( F_NORMAL_MAP ); >;
	float g_flEmissionStrength < Default( 0.0 ); Range( 0, 8 ); UiGroup( "Maps" ); Feature( F_EMISSION ); >;
	float g_flAlphaCutoff < Default( 0.5 ); Range( 0, 1 ); UiGroup( "Rendering" ); Feature( F_ALPHA ); >;

	//What the emission map is multiplied by, on top of its strength. Its own colour rather than the surface's, unless the tick below says
	//otherwise: a neon sign wants the picture's colours out of the albedo and a warning lamp wants one colour whatever it is painted.
	float3 g_vEmissiveColor < Default3( 1, 1, 1 ); UiType( Color ); UiGroup( "Maps" ); Feature( F_EMISSION ); >;
	bool g_bEmissiveUseAlbedo < Default( 0 ); UiGroup( "Maps" ); Feature( F_EMISSION ); >;

	//Height per texel; 0.5 is flat, so no height map means no parallax. Bindless, so free to declare
	Texture2D g_tHeight < Default( 0.5 ); UiGroup( "Maps" ); Feature( F_PARALLAX ); >;

	//How far the view ray walks the texture coordinate through that height. Read only where F_PARALLAX built it in.
	float g_flParallaxStrength < Default( 0.035 ); Range( 0, 0.25 ); UiGroup( "Maps" ); Feature( F_PARALLAX ); >;

	//Distant cutout fixes, off by default: Boost lifts alpha per mip, Narrow raises the cutoff per mip
	float g_flOpacityMipBoost < Default( 0 ); Range( 0, 1 ); UiGroup( "Rendering" ); Feature( F_ALPHA ); >;
	float g_flOpacityMipNarrow < Default( 0 ); Range( 0, 0.2 ); UiGroup( "Rendering" ); Feature( F_ALPHA ); >;

	//How much of the light behind the sheet comes through, against what it reflects: nought is opaque
	float g_flTransmission < Default( 0.5 ); Range( 0, 2 ); UiGroup( "Transmission" ); Feature( F_TRANSMISSION ); >;

	//What colour the light is once through, on top of the surface's own
	float3 g_vTransmissionColour < Default3( 1, 1, 1 ); UiType( Color ); UiGroup( "Transmission" ); Feature( F_TRANSMISSION ); >;

	//How tightly a light behind the sheet glows towards the eye; cloth scatters wide, so low
	float g_flTransmissionSpread < Default( 2.0 ); Range( 1, 32 ); UiGroup( "Transmission" ); Feature( F_TRANSMISSION ); >;

	//Where the sheet is thick, in red on the colour map's coordinates: white lets light through, black a seam
	Texture2D g_tThickness < Default( 1.0 ); UiGroup( "Transmission" ); Feature( F_TRANSMISSION ); >;
}

//Marker only: tells the editor this surface uses vertex tint (see CoreResources.TintsPerVertex)
#define ENGINE_VERTEX_TINT 1

#include "depth_id.slang"

struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;
	float3 world : TEXCOORD2;
	float3 normal : NORMAL;
	float3 tangent : TANGENT;
	float bitangentSign : TEXCOORD3;
	float2 uv : TEXCOORD0;

	//Where this pixel sits in the baked atlas. Zero where the mesh has no chart in it, which is what says there is nothing baked for this draw.
	float2 lightmap : TEXCOORD4;

	//The corner tint, interpolated across the triangle.
	float4 colour : TEXCOORD5;
};

//Use the engine's world push block (full matrix plus tint); camera and lights live in the frame
#define ENGINE_WORLD_PUSH 1

//A thin sheet's light through it, shaded with every light's - see Surface.transmission
#define SURFACE_TRANSMITS F_TRANSMISSION

//The material, the frame and the lights, and where they are bound. One copy for the whole engine.
#include "frame.slang"
#include "surface_shading.slang"

//The vertex as the interpolants: the material's own vertex body, which the classic
//entry below and the engine's cluster entry both run - see cluster_surface.slang.
V2F Vertex( VIn i )
{
	float3 position = i.position;
	float3 normal = i.normal;
	float3 tangent = i.tangent.xyz;

	//Where this copy of the mesh stands.
	row_major float4x4 modelToWorld = g_modelToWorld;

#ifdef MORPHED
	//Morphs apply in model space before skinning; clamp the count so a bad attribute can't hang the GPU
	uint count = min( i.morphRun.y, MorphsPerVertex );

	for ( uint d = 0; d < count; d++ )
	{
		MorphDelta delta = LoadMorphDelta( i.morphRun.x + d );
		float weight = g_morphWeights[min( delta.morph, MorphsPerObject - 1 )];

		position += delta.position * weight;
		normal += delta.normal * weight;
	}

	//Adding two normals does not give a normal. Left long, a morphed surface is lit as though it were brighter than
	//the one beside it, which reads as the shape being a different piece of geometry rather than the same one moved.
	float along = dot( normal, normal );
	if ( along > 1e-8 ) normal *= rsqrt( along );
#endif

#ifdef SKINNED
	//Four bones blended by weight, then one transform - cheaper and smoother than four transforms blended by weight. See SkinAt.
	float4x4 skin = SkinAt( i.bones, i.weights );

	position = mul( float4( position, 1.0 ), skin ).xyz;
	normal = mul( float4( normal, 0.0 ), skin ).xyz;
	tangent = mul( float4( tangent, 0.0 ), skin ).xyz;
#endif

	float3 world = mul( float4( position, 1.0 ), modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.world = world;
	o.normal = mul( float4( normal, 0.0 ), modelToWorld ).xyz;
	o.tangent = mul( float4( tangent, 0.0 ), modelToWorld ).xyz;

	//The fourth tangent component says which way the bitangent goes; it is a handedness, not a direction, so it rides along untouched.
	o.bitangentSign = i.tangent.w < 0.0 ? -1.0 : 1.0;

	o.uv = i.texCoord0.xy;

	//And where it sits in the baked atlas. The second set of coordinates is what a mesh keeps its unwrap in; one that has none falls
	//back to the first, which is what the bake read too - the two have to agree or the lighting lands somewhere else on the model.
	float2 chart = any( i.texCoord1.xy ) ? i.texCoord1.xy : i.texCoord0.xy;
	o.lightmap = ( saturate( chart ) * g_object.xy ) + g_object.zw;

	o.colour = i.colour;

	DEPTH_ID_SET( o, i.texCoord0.xy );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

//What the world's pass writes: the colour in half floats, and beside it the normal and
//how mirror-like the surface is, for the passes that come after and never saw it drawn.
struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif

};

//The pixel stage's body, run for a rasterised pixel by MainPs below and for a cluster's by the engine's MainClusterPs - see
//cluster_surface.slang. Which side of the sheet this pixel is comes in with it, because a triangle does not know which way it will be looked at.
Out Shade( V2F i, bool front )
{
	float2 uv = i.uv;

	float3 n = normalize( i.normal );

#if F_BACKFACES
	//The back of a two-sided sheet is lit as though it faced out. Without this a leaf or a curtain seen from
	//behind is lit by whatever is on the far side of it, which reads as a hole rather than as a surface.
	n = front ? n : -n;
#endif

#if F_NORMAL_MAP || F_PARALLAX
	//The tangent frame: the interpolated normal and tangent, re-orthogonalised because interpolation does not
	//preserve a right angle. Both the normal map and the walk below want it, and neither wants to build it twice.
	float3 t = TangentAgainst( n, i.tangent );
	float3 b = cross( n, t ) * i.bitangentSign;
#endif

#if F_PARALLAX
	{
		//Parallax: walk the UV along the view ray by height; one dependent sample
		float3 toEye = EYE_FRAME( camera ).xyz - i.world;
		float away = length( toEye );

		//Off close up, where a coordinate that moves with the eye swims faster than the eye can follow, and off far away, where
		//the offset is smaller than a texel and only stirs the mips. In metres, not inches.
		const float fadesBy = 1.0 / 11.43;
		const float nearFrom = 0.305;
		const float nearOver = 0.711;

		float fade = saturate( 1.0 - away * fadesBy ) *
			saturate( ( away - nearFrom ) / nearOver );

		float3 toEyeTs = float3( dot( t, toEye ), dot( b, toEye ), dot( n, toEye ) );
		toEyeTs *= rsqrt( dot( toEyeTs, toEyeTs ) );

		//Half the height either side of the middle, so a surface of average height stays where it was drawn rather than sinking as a whole.
		float height = SampleUv( g_tHeight, uv ).r;
		float depth = g_flParallaxStrength * fade;
		float walk = height * depth - 0.5 * depth;

		//The floor under the divide is what stops a grazing angle sending the coordinate off across the texture.
		float2 offset = toEyeTs.xy * walk / max( toEyeTs.z, 0.35 );

		//And a ceiling on the distance, because past about a hundredth of the texture the offset smears the picture rather than displacing it.
		const float most = 0.012;
		float far = dot( offset, offset );

		offset *= sqrt( min( far, most * most ) / max( far, 1e-8 ) );

		uv += offset;
	}
#endif

#if F_COLOR_MAP
	float4 texel = SampleUv( g_color, uv );
#else
	//One colour, already in the tint below. White here so the multiply leaves it alone, and opaque so a
	//masked material with no picture cuts nothing out - there is no shape in a flat colour to cut along.
	float4 texel = float4( 1.0, 1.0, 1.0, 1.0 );
#endif

	float alpha = texel.a;

#if F_ALPHA
	{
		//Alpha cutout against the untinted texture alpha; a cutoff of zero means nothing is cut
		float cutoff = g_flAlphaCutoff;

		//Plain cut by default; mip boost and taps only when the material asks, they change distant cutouts
#if F_COLOR_MAP
		bool corrected = g_flOpacityMipBoost > 0.0 || g_flOpacityMipNarrow > 0.0;
#else
		//A flat colour has one texel and no mips, so there is nothing for the corrections below to correct - and no picture for them to read.
		const bool corrected = false;
#endif

		if ( cutoff > 0.0 && !corrected )
		{
			if ( texel.a < cutoff ) discard;
		}
#if F_COLOR_MAP
		else if ( cutoff > 0.0 )
		{
			//Thin distant cutouts: boost alpha per mip and take 4 taps along the long axis. For painted lines
			const float mipMost = 7.0;
			const float mipBias = -1.0;
			const float hardwareAniso = 16.0;

			float2 dX = UvDx( uv );
			float2 dY = UvDy( uv );

			bool acrossX = dot( dX, dX ) > dot( dY, dY );
			float2 along = acrossX ? dX : dY;
			float2 across = acrossX ? dY : dX;

			float2 gradAlong = along * ( 0.25 * exp2( mipBias ) );
			float2 gradAcross = across * exp2( mipBias );

			float opacity = 0.0;
			float lowTap = 1.0;
			float highTap = 0.0;

			[unroll]
			for ( int tap = 0; tap < 4; tap++ )
			{
				float2 shift = along * ( float( tap ) * 0.25 - 0.375 );

				float one = g_color.SampleGrad( uv + shift, gradAlong, gradAcross ).a;

				opacity += one;
				lowTap = min( lowTap, one );
				highTap = max( highTap, one );
			}

			opacity *= 0.25;

			//Which mip the taps actually landed on: what the sampler would have chosen, less what the
			//taps won back, and only what they won beyond the anisotropy the hardware was already doing.
			float stretch = sqrt( max( dot( along, along ), 1e-12 ) / max( dot( across, across ), 1e-12 ) );
			float won = log2( clamp( stretch / hardwareAniso, 1.0, 4.0 ) );

			float mip = max( LevelOf( g_color, uv ) + mipBias - won, 0.0 );
			float capped = min( mip, mipMost );

			opacity = saturate( opacity * exp2( capped * g_flOpacityMipBoost ) );

			//The cutoff rises a little with the mip, which trims the halo the boost leaves around the line. Capped at four mips: the boosted ridge
			//of the line has to stay above the cutoff, and an uncapped one climbed past it and took the marking out of the distance altogether.
			float raised = saturate( cutoff + min( capped, 4.0 ) * g_flOpacityMipNarrow );

			//Coverage in pixels past the cutoff, which antialiases the cut edge
			float slope = fwidth( opacity );

			float edge = ( opacity - raised ) / max( slope, 0.001 ) + 0.5;

			if ( edge < 0.5 ) discard;

			alpha = saturate( edge );
		}
#endif
	}
#endif


#ifdef DEPTH_ONLY
	//Depth pre-pass writes no colour; the guard keeps the lit stage out of every depth build
	Out nothing = (Out)0;
	return nothing;
#endif
	//Draw tint (includes material tint and folded flat colour) times the per-corner vertex tint
	float4 tint = g_tint * i.colour;

#if F_TINT_MASK
	//The colour only: a mask that faded the alpha too would cut holes in a translucent surface wherever it is black.
	tint.rgb = lerp( float3( 1.0, 1.0, 1.0 ), tint.rgb, saturate( SampleUv( g_tTintMask, uv ).r ) );
#endif

	float4 albedo = float4( texel.rgb * tint.rgb, alpha * tint.a );

#if F_NORMAL_MAP
	//The normal map is in tangent space; the frame built above is what it needs.
	float3 sampled = SampleUv( g_normal, uv ).rgb * 2.0 - 1.0;
	sampled.xy *= g_flNormalStrength;

	float3 normal = normalize( sampled.x * t + sampled.y * b + sampled.z * n );
#else
	//No map, so no sample, and no guessing from the length of what came back whether the slot held a normal or somebody's colour.
	float3 normal = n;
#endif

	//One sample per thing it says. Where the material named no picture these read the Default from the declaration
	//- white - without touching the table at all, so the number beside the picture is the whole of the answer.
#if !F_ROUGHNESS
	//Off: entirely matte, and nothing read to say so.
	float roughness = 1.0;
#else
	//The picture times the number beside it: with the number at one, a texel of 0.5 is
	//a surface that is half rough, which is what somebody painting one means by it.
	float roughness = clamp( SampleUv( g_tRoughness, uv ).r * g_flRoughness, 0.04, 1.0 );
#endif

#if F_METALNESS
	float metalness = saturate( SampleUv( g_tMetalness, uv ).r * g_flMetalness );
#else
	float metalness = 0.0;
#endif

#if F_AO
	float occlusion = saturate( SampleUv( g_tAO, uv ).r );
#else
	float occlusion = 1.0;
#endif

#if F_EMISSION
	//Emission colour: the texture (if asked) or a flat colour; ignores draw tint so fades don't dim it
	float3 glow = g_bEmissiveUseAlbedo > 0.5 ? texel.rgb : g_vEmissiveColor;

	float3 emission = SampleUv( g_emission, uv ).rgb * glow * g_flEmissionStrength;
#else
	float3 emission = float3( 0.0, 0.0, 0.0 );
#endif

	//And the rest of it is what the engine does with any surface, whatever worked the surface out - see surface_shading.slang.
	Surface surface;
	surface.world = i.world;
	surface.normal = normal;
	surface.geometric = n;
	surface.albedo = albedo.rgb;
	surface.alpha = albedo.a;
	surface.roughness = roughness;
	surface.metalness = metalness;
	surface.occlusion = occlusion;
	surface.emission = emission;
	surface.uv = uv;
	surface.id = i.depthId;
	surface.lightmap = i.lightmap;

	surface.screen = i.position.xy;

#if F_TRANSMISSION
	surface.transmission = albedo.rgb * g_vTransmissionColour * ( g_flTransmission * SampleUv( g_tThickness, uv ).r );
	surface.transmissionSpread = g_flTransmissionSpread;
#endif

	Shaded lit = ShadeSurface( surface );

	Out o;
	//albedo.a already includes the tint's alpha; multiplying by it again would square it
	o.colour = float4( lit.colour, albedo.a );

#ifdef TARGET_SCENE
	//The normal, roughness and how much of a mirror this is, for the passes that come after the world and never saw it drawn.
	o.surface = SurfaceWord( normal, lit.roughness, lit.mirror );
#endif

	//Tonemap when drawing straight to screen; alpha must be opaque or the Linux window shows through
#ifdef TARGET_SCREEN
	//Its alpha is the surface's own where this draw blends - see ScreenAlpha in frame.slang, and the comment above for why everything else leaves one.
#if F_TRANSLUCENT
	bool blends = true;
#else
	bool blends = tint.a < 1.0;
#endif

	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( blends, albedo.a ) );
#endif

	return o;
}

[shader("fragment")]
Out MainPs( V2F i
#if F_BACKFACES
	, bool front : SV_IsFrontFace
#endif
	)
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

#if F_BACKFACES
	return Shade( i, front );
#else
	return Shade( i, true );
#endif
}
