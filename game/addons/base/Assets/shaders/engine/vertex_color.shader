//The same as unlit, times the colour the vertices carry: what a mesh built in code paints with, and what a debug view or a
//gizmo-like prop wants. The bindings are the engine's, the same as every shader here - see standard.shader for what is in which set.

#include "depth_id.slang"

struct V2F
{
	DEPTH_ID_FIELD
	float4 position : SV_Position;
	float4 colour : COLOR0;
	float2 uv : TEXCOORD0;
};

//The push block every world draw carries is the engine's, not this shader's - see frame.slang.
#define ENGINE_WORLD_PUSH 1

//The material, the frame and the lights, and where they are bound. One copy for the whole engine.
#include "frame.slang"

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

//The vertex body, which the classic entry below and the engine's cluster entry both run - see cluster_surface.slang.
V2F Vertex( VIn i )
{
	float3 position = i.position;

#ifdef SKINNED
	float4x4 skin = SkinAt( i.bones, i.weights );
	position = mul( float4( position, 1.0 ), skin ).xyz;
#endif

	row_major float4x4 modelToWorld = g_modelToWorld;

	float3 world = mul( float4( position, 1.0 ), modelToWorld ).xyz;

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.colour = i.colour;
	o.uv = i.texCoord0.xy;
	DEPTH_ID_SET( o, i.texCoord0.xy );

	return o;
}

[shader("vertex")]
V2F MainVs( VIn i )
{
	return Vertex( i );
}

//The pixel body, which the classic entry at the end and the engine's cluster entry both run.
Out Shade( V2F i, bool front )
{
	float4 albedo = g_color.Sample( i.uv ) * g_tint * i.colour;

	float cutoff = g_material.surface.w;
	if ( cutoff > 0.0 && albedo.a < cutoff && CutsOwnDepth() ) discard;
#ifdef DEPTH_ONLY
	//Depth pre-pass writes no colour; the guard keeps the lit stage out of every depth build
	Out nothing = (Out)0;
	return nothing;
#endif


	Out o;
	o.colour = albedo;

#ifdef TARGET_SCENE
	//Facing the eye and not a mirror: nothing here has a normal worth reflecting off, and
	//saying so is better than leaving the buffer at whatever the last frame left in it.
	o.surface = float4( 0.5, 0.5, 1.0, 0.0 );
#endif

	//Tonemap when drawing straight to screen; alpha must be opaque or the Linux window shows through
#ifdef TARGET_SCREEN
	//Its alpha is the surface's own where this draw blends - see ScreenAlpha in frame.slang, and the comment above for why everything else leaves one.
#if F_TRANSLUCENT
	bool blends = true;
#else
	bool blends = g_tint.a < 1.0;
#endif

	o.colour = float4( FinishToScreen( o.colour.rgb ), ScreenAlpha( blends, albedo.a ) );
#endif

	return o;
}

[shader("fragment")]
Out MainPs( V2F i )
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

	return Shade( i, true );
}
