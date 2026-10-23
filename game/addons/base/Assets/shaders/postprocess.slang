//Base for full-screen post passes: MainVs, frame pictures by table index, push params, Apply

#ifndef ENGINE_POSTPROCESS_INCLUDED
#define ENGINE_POSTPROCESS_INCLUDED

//What the pass is told about the frame it is drawing over. Written by CorePostBlit.Push. The pictures are
//indices into the table, not handles - see Scene() and the rest below, which is how they are meant to be read.
[[vk::push_constant]]
cbuffer Push
{
	//Width, height, and one over each.
	float4 g_vFrameSize;

	//Near and far planes, the stage, and in the portable frame whether the colour is kept - see FramePicture
	float4 g_vFrameRange;

	//Where the frame's pictures are in the indexed table: the colour, the world normals, the depth, and the motion. Zero for one this frame does not have.
	float4 g_vFramePictures;

	//Whatever the effect set on its attributes: blend, amount, strength, and the time. Everything else a pass wants belongs in its own parameters.
	float4 g_vFrameParams;

	//Post pass params packed in the push block so per-frame changes don't rebuild the material
	float4 g_vPostSlots[4];
};

#include "frame.slang"

//The exposure the frame's curve applies: the light the world was shaded in, times this, is what the curve
//takes. For an effect that wants to work in the light the screen will show - see coloradjust.shader.
float Exposure() { return g_frame[0].finish.x; }

//The screen, in texels, of the pixel being written.
float2 ScreenUv( float4 position )
{
	return position.xy * g_vFrameSize.zw;
}

#ifdef PORTABLE

//No table here: the frame as it stands is in the colour slot, named by one, and the rest are absent - see CorePostBlit.
//Where g_vFrameRange.w says so it holds the world as drawn aside, x / (1 + x) after exposure, given back as light
float4 FramePicture( float slot, float2 uv )
{
	if ( int( slot ) != 1 ) return float4( 0.0, 0.0, 0.0, 0.0 );

#ifdef WEBGL
	//GL keeps a drawn picture's rows bottom first - see ShadowCompare in frame.slang
	uv.y = 1.0 - uv.y;
#endif

	//Held half a texel inside: the material's sampler repeats, and an edge read would wrap to the far side
	float2 inside = g_vFrameSize.zw * 0.5;
	float4 colour = g_color.SampleLevel( clamp( uv, inside, 1.0 - inside ), 0.0 );

	if ( g_vFrameRange.w < 0.5 ) return colour;

	float3 kept = min( colour.rgb, 254.0 / 255.0 );
	return float4( kept / ( 1.0 - kept ) / max( Exposure(), 1e-4 ), colour.a );
}

//No levels either: the top one
float4 FrameMip( float slot, float2 uv, float level ) { return FramePicture( slot, uv ); }

#else

//A frame picture's top mip, or black if absent; use FrameMip for blurred levels
float4 FramePicture( float slot, float2 uv )
{
	int index = int( slot );

	if ( index <= 0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	//Not marked non-uniform: every caller's slot is the pass's or the material's, the same in every
	//lane, and marked, each read was a loop over the lanes - see Texel in engine/upscale.slang.
	return g_textures[uint( index )].SampleLevel( uv, 0.0 );
}

//A frame picture at a chosen mip level; pictures without a chain clamp to the top level
float4 FrameMip( float slot, float2 uv, float level )
{
	int index = int( slot );

	if ( index <= 0 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	return g_textures[uint( index )].SampleLevel( uv, level );
}

#endif

//What has been drawn so far, before this pass.
float3 SceneColour( float2 uv ) { return FramePicture( g_vFramePictures.x, uv ).rgb; }

//The world-space normal the surface at this pixel had, and how mirror-like it was. Written by every surface that draws into the
//world's own picture - see surface_standard.slang - so a pass can tell a wall from a floor without working it out from the depth.
float4 SceneSurface( float2 uv ) { return FramePicture( g_vFramePictures.y, uv ); }

float3 SceneNormal( float2 uv ) { return normalize( SceneSurface( uv ).xyz * 2.0 - 1.0 ); }

//How rough the surface is, up to where it stops being a mirror - see SurfaceWord in surface_shading.slang
float SceneRoughness( float2 uv ) { return saturate( ( 1.0 - length( SceneSurface( uv ).xyz * 2.0 - 1.0 ) ) / 0.8 ); }

//The depth as it was written: nought at the near plane, one at the far - and one where nothing was drawn, which is the sky.
float SceneDepthRaw( float2 uv ) { return FramePicture( g_vFramePictures.z, uv ).r; }

//And that as a distance in metres. A pixel with nothing drawn on it comes back at the far plane,
//which is what a pass comparing neighbours wants: the sky is far, not touching the camera.
float SceneDepth( float2 uv )
{
	float raw = SceneDepthRaw( uv );

	if ( raw >= 1.0 ) return g_vFrameRange.y;

	float near = g_vFrameRange.x;
	float far = g_vFrameRange.y;

	return near * far / max( far - raw * ( far - near ), 1e-6 );
}

//Screen motion since last frame, in uv, pointing where it went; multiply by g_vFrameSize for px
float2 SceneMotion( float2 uv ) { return FramePicture( g_vFramePictures.w, uv ).xy; }

//The same picture whole: the movement, then how far away the surface that wrote it stood, then whether anything wrote at all.
float4 SceneMotionFull( float2 uv ) { return FramePicture( g_vFramePictures.w, uv ); }

//Camera-only motion from depth and last frame's matrix; wrong for self-moving objects
float2 SceneMotionCamera( float2 uv )
{
	float raw = SceneDepthRaw( uv );

	//The sky has no place in the world to have moved from.
	if ( raw >= 1.0 ) return float2( 0.0, 0.0 );

	float4 clip = float4( ( uv * 2.0 ) - 1.0, raw, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	if ( abs( world.w ) < 1e-6 ) return float2( 0.0, 0.0 );

	float4 before = mul( float4( world.xyz / world.w, 1.0 ), EYE_FRAME( previousWorldToClip ) );

	if ( before.w <= 0.0001 ) return float2( 0.0, 0.0 );

	float2 was = ( ( before.xy / before.w ) * 0.5 ) + 0.5;

	return uv - was;
}

struct PostV2F
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

//One triangle covering the screen, out of no vertex buffer at all.
[shader("vertex")]
PostV2F MainVs( uint id : SV_VertexID )
{
	PostV2F o;

	o.uv = float2( ( id << 1 ) & 2, id & 2 );
	o.position = float4( ( o.uv * 2.0 ) - 1.0, 0.5, 1.0 );

	return o;
}

#endif
