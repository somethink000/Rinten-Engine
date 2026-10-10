//Model impostor: six quads with pictures of each box side; only the one facing the eye is shown

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

//Not in the depth pre-pass: the pre-pass cuts at a half and puts a corner where the mesh says, and a quad cuts its cover
//against a pattern and is turned to the eye - the pass that shades it writes its depth, see CoreResources.MovesVertices.
#define ENGINE_MOVES_VERTICES 1

FEATURES
{
	//Cut out; each quad one-sided - the one the eye is shown faces it.
	Feature( F_ALPHA, 0..1, "Rendering" ) < Default( 1 ); >;
	Feature( F_BACKFACES, 0..1, "Rendering" );
}

PARAMETERS
{
	//The colour and cover of every model's views are the material's colour slot - a page every model's sheet is laid into, see CoreImpostor;
	//their normals, folded, how much the normals under each texel agree, and their roughness are here - see ModelBuilder.Impostor.
	Texture2D g_tImpostorShape < Default( 0.5 ); UiGroup( "Impostor" ); >;
}

#include "depth_id.slang"

struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;
	float3 world : TEXCOORD2;
	float2 uv : TEXCOORD0;

	//The model's sphere in the world: every corner carries it in its second coordinates, the model's own, so one material serves every model.
	nointerpolation float4 sphere : TEXCOORD1;
};

#define ENGINE_WORLD_PUSH 1

#include "frame.slang"
#include "surface_shading.slang"

//A normal the bake folded to two numbers - ModelBuilder.Fold, the other way.
float3 ImpostorUnfold( float2 folded )
{
	float2 e = folded * 2.0 - 1.0;
	float3 n = float3( e.x, 1.0 - abs( e.x ) - abs( e.y ), e.y );

	if ( n.y < 0.0 )
	{
		n.x = ( 1.0 - abs( e.y ) ) * ( e.x >= 0.0 ? 1.0 : -1.0 );
		n.z = ( 1.0 - abs( e.x ) ) * ( e.y >= 0.0 ? 1.0 : -1.0 );
	}

	return normalize( n );
}

V2F Vertex( VIn i )
{
	row_major float4x4 modelToWorld = g_modelToWorld;

	float3 position = i.position;
	float3 centre = i.texCoord1.xyz;

	//The way to the eye from the model's centre, in the model's space: the transpose of the matrix's axes, which for a turn and an even scale
	//points the same way as its inverse. Nothing was baked from below that a side does not show better, so below the horizon is the horizon.
	float3 toEyeWorld = EYE_FRAME( camera ).xyz - mul( float4( centre, 1.0 ), modelToWorld ).xyz;
	float3 toEye = float3( dot( modelToWorld[0].xyz, toEyeWorld ), dot( modelToWorld[1].xyz, toEyeWorld ),
		dot( modelToWorld[2].xyz, toEyeWorld ) );
	toEye.y = max( toEye.y, 0.0 );

	//The side whose view is nearest the eye's: the axis it is furthest along. Every corner of every
	//quad asks about the same centre, so all of a quad's corners agree, and exactly one quad is drawn.
	float3 along = abs( toEye );
	float3 chosen = along.y >= along.x && along.y >= along.z ? float3( 0.0, 1.0, 0.0 )
		: along.x >= along.z ? float3( toEye.x < 0.0 ? -1.0 : 1.0, 0.0, 0.0 )
		: float3( 0.0, 0.0, toEye.z < 0.0 ? -1.0 : 1.0 );

	//A side, turned about the upright through the centre until it faces the eye across: the turn that takes its way to the eye's, as a complex number
	//in x and z - the eye's way times the side's conjugate. Never more than a quarter of a right angle off the picture's own, and never edge-on.
	if ( chosen.y == 0.0 )
	{
		float2 h = normalize( toEye.xz + float2( 1e-20, 0.0 ) );
		float2 q = float2( ( h.x * chosen.x ) + ( h.y * chosen.z ), ( h.y * chosen.x ) - ( h.x * chosen.z ) );
		float2 p = position.xz - centre.xz;

		position.xz = centre.xz + float2( ( q.x * p.x ) - ( q.y * p.y ), ( q.x * p.y ) + ( q.y * p.x ) );
	}

	float3 world = mul( float4( position, 1.0 ), modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );

	//The other five: past the screen's right edge, all three corners of each triangle, so the raster drops them before a pixel.
	if ( dot( i.normal, chosen ) < 0.5 ) o.position = float4( 2.0, 2.0, 0.5, 1.0 );

	o.world = world;
	o.uv = i.texCoord0.xy;
	o.sphere = float4( mul( float4( i.texCoord1.xyz, 1.0 ), modelToWorld ).xyz, i.texCoord1.w * length( modelToWorld[0].xyz ) );

	DEPTH_ID_SET( o, i.texCoord0.xy );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif

};

//A threshold a screen pixel, the same every frame: interleaved gradient noise (Jimenez 2014). Any share of cover
//is that share of pixels, spread evenly, without the grid of dots an ordered pattern draws along every edge.
float CoverThreshold( float2 pixel )
{
	return frac( 52.9829189 * frac( dot( floor( pixel ), float2( 0.06711056, 0.00583715 ) ) ) );
}

Out Shade( V2F i, bool front )
{
	float4 colour = SampleUv( g_color, i.uv );

	float cover = colour.a;

	//Against a fixed pattern: a texel half covered is half the pixels round it.
	if ( cover <= CoverThreshold( i.position.xy ) ) discard;

#ifdef DEPTH_ONLY
	Out nothing = (Out)0;
	return nothing;
#endif

	float4 shape = SampleUv( g_tImpostorShape, i.uv );

	float3 normal = normalize( mul( float4( ImpostorUnfold( shape.xy ), 0.0 ), g_modelToWorld ).xyz );
	float agree = saturate( shape.z );

	Surface surface;

	//Sun shadow ray starts outside the quad sphere so the quads don't shadow their own crown
	surface.world = i.world;

	if ( HasSun() )
	{
		float3 l = g_frame[0].sunDirection.xyz;
		surface.world += l * max( 0.0, i.sphere.w * 1.05 - dot( i.world - i.sphere.xyz, l ) );
	}

	surface.normal = normal;
	surface.geometric = normal;
	surface.albedo = colour.rgb * g_tint.rgb;
	surface.alpha = 1.0;
	//The normals under a texel that disagree read rougher (Toksvig), and take less of the sky's light: the sky's light at the mean normal m is
	//linear in it, so (1 + |m|) / 2 of it comes along m and the rest from the other side, which from below is the ground's and next to nothing.
	float rough = shape.w;
	surface.roughness = clamp( sqrt( rough * rough + 0.25 * ( 1.0 - agree ) ), 0.04, 1.0 );
	surface.metalness = 0.0;
	surface.occlusion = 0.5 + 0.5 * agree;
	surface.emission = float3( 0.0, 0.0, 0.0 );
	surface.uv = i.uv;
	surface.id = i.depthId;
	surface.lightmap = float2( 0.0, 0.0 );
	surface.screen = i.position.xy;

	Shaded lit = ShadeSurface( surface );

	Out o;
	o.colour = float4( lit.colour, 1.0 );

#ifdef TARGET_SCENE
	o.surface = float4( normal * 0.5 + 0.5, lit.mirror );
#endif

#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( g_tint.a < 1.0, 1.0 ) );
#endif

	return o;
}

[shader("fragment")]
Out MainPs( V2F i, bool front : SV_IsFrontFace )
{
	STEREO_EYE_IN( i );
	return Shade( i, front );
}
