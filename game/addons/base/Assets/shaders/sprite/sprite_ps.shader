//Engine sprite pixel stage: texture, tint, cut-out, optional lighting and fog

//Only the engine makes materials on this, so the material editor does not offer it
#define ENGINE_INTERNAL 1

#include "sprite/sprite.slang"
#include "picture_size.slang"
#include "half.slang"

[shader("fragment")]
Out MainPs( V2F i )
{
	//Which eye, where both are drawn at once - see depth_id.slang.
	STEREO_EYE_IN( i );

	SpriteData sprite = SpriteAt( i.sprite );

	//Read with the gradients of where the pixel really is: a pixelated read's coordinate stands
	//still inside a texel, and its own would pick the finest mip however small the sprite is drawn.
	float2 dx = ddx( i.uv ), dy = ddy( i.uv );
	float2 size;

#ifdef PORTABLE
	//One picture a run, bound as the run's colour map - see SpriteAt.
	size = float2( PictureSize( g_color ) );
	float4 texel = g_color.SampleGrad( SpriteReadAt( sprite, i.uv, size ), dx, dy );
#else
	Sampler2D picture = g_textures[NonUniformResourceIndex( uint( max( sprite.TextureHandle, 0 ) ) )];
	picture.GetDimensions( size.x, size.y );
	float4 texel = picture.SampleGrad( SpriteReadAt( sprite, i.uv, size ), dx, dy );
#endif

	//The tint is stored as RGBE - three mantissa bytes with the exponent put away in the top half
	//of Lighting, so a sprite may be brighter than white without a wider buffer to hold it in.
	float4 tint = UnpackRgba8( sprite.TintColor );
	uint exponent = ( sprite.Lighting >> 16 ) & 0xFF;

	tint.rgb = ( exponent == 0 )
		? float3( 0, 0, 0 )
		: ( ( tint.rgb * 255.0 ) - 128.0 ) * exp2( float( exponent ) - 128.0 - 7.0 );

	float4 overlay = UnpackRgba8( sprite.OverlayColor );

	if ( overlay.a > 0.0 )
	{
		float3 signedOverlay = ( ( overlay.rgb * 255.0 ) - 128.0 ) * exp2( float( exponent ) - 128.0 - 7.0 );
		texel.rgb = lerp( texel.rgb, signedOverlay, overlay.a );
	}

	//The cutoff is measured before the tint, so how opaque a sprite was told to be does not move where its edge is.
	float cutoff = float( ( sprite.FogStrengthCutout >> 16 ) & 0xFFFF ) / 65535.0;

	if ( texel.a < cutoff ) discard;

#ifdef DEPTH_ONLY
	//Depth pass only needs the cut-out above; without discard it would write a solid card
	Out nothing = (Out)0;
	return nothing;
#endif

	Out o;
	o.colour = texel * tint;

	if ( ( sprite.Lighting & 0xFF ) != 0 )
	{
		o.colour.rgb *= LightOn( i.world, i.normal );
	}

	if ( ( sprite.Lighting & Lit2DFlag ) != 0 )
	{
		o.colour.rgb *= Light2DAt( i.world );
	}

	//As much of the fog as the sprite asked for: nought for one that is meant to read
	//through it - a marker, a glow - one for anything that is part of the scene.
	float fogStrength = float( sprite.FogStrengthCutout & 0xFFFF ) / 65535.0;

	if ( fogStrength > 0.0 )
	{
		float3 fogged = ApplyFog( o.colour.rgb, i.world, EYE_FRAME( camera ).xyz, i.position.xy );
		o.colour.rgb = lerp( o.colour.rgb, fogged, fogStrength );
	}

	//Faded out as it nears the camera, so a puff the camera flies through thins away instead of filling the screen: nothing nearer than
	//the near distance, all of it past the far one. Two halves in one word, see SpriteData.PackCameraFade; a far of nought is off.
	float cameraFadeNear = HalfToFloat( sprite.CameraFade & 0xFFFFu );
	float cameraFadeFar = HalfToFloat( sprite.CameraFade >> 16 );

	if ( cameraFadeFar > cameraFadeNear )
	{
		float dist = distance( i.world, EYE_FRAME( camera ).xyz );
		o.colour.a *= saturate( ( dist - cameraFadeNear ) / ( cameraFadeFar - cameraFadeNear ) );
	}

#ifdef TARGET_SCENE
	//Facing the eye and not a mirror, the same thing every unlit surface says.
	o.surface = float4( 0.5, 0.5, 1.0, 0.0 );
#endif

#ifdef TARGET_SCREEN
	o.colour = float4( FinishToScreen( o.colour.rgb ), o.colour.a );
#endif

	//The pipeline that adds reads no alpha - one plus one, see VkPipelines - so a sprite that fades
	//has to fade itself: the alpha goes into the colour here, and what is added is what is left.
	if ( ( sprite.RenderFlags & FlagAdditive ) != 0 )
		o.colour.rgb *= o.colour.a;

	return o;
}
