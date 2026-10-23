//Engine half of every sprite shader: instance data layout, quad corners, billboarding, vertex stage

//What says a shader is one of these. Read off the file, includes and all, so a project's own sprite shader says it by including this.
#define ENGINE_SPRITE 1

#include "frame.slang"

//Publishes the eye at a pixel entry, as a surface's does - see depth_id.slang, which a sprite does not include because its V2F is not a surface's.
#ifndef STEREO_EYE_IN
#ifdef STEREO
#define STEREO_EYE_IN( i ) g_eyeId = i.eyeId
#else
#define STEREO_EYE_IN( i )
#endif
#endif

//Matches managed SpriteData; all scalars because float3 in a storage buffer aligns to 16 bytes
struct SpriteData
{
	float PositionX, PositionY, PositionZ;
	float Pitch, Yaw, Roll;
	float ScaleX, ScaleY;
	uint TintColor;
	uint OverlayColor;
	int TextureHandle;
	int RenderFlags;
	uint BillboardMode;
	uint FogStrengthCutout;
	uint Lighting;
	float DepthFeather;
	int Splots;
	int Sequence;
	float SequenceTime;
	float RotationOffset;
	float BlurLeading, BlurAmount, BlurSpacing, BlurOpacity;
	float VelocityX, VelocityY, VelocityZ;
	float SheetX, SheetY, SheetZ, SheetW;
	float OffsetX, OffsetY;
	uint CameraFade;
	float RegionX, RegionY, RegionW, RegionH;
};

#ifndef PORTABLE
//The one buffer this draw binds. Set 2 is where a draw's own buffers go, and binding nought because the attributes
//are bound in the order their names sort and "Sprites" is the only one - see Graphics.DrawIndexedInstanced.
[[vk::binding(0,2)]] StructuredBuffer<SpriteData> Sprites;

SpriteData SpriteAt( uint index ) { return Sprites[index]; }
#else
//Portable frame (no storage buffers) reads sprite runs from the bone ring block instead
static const uint SpriteWords = 38;

#ifdef WEBGL
struct SpriteBlock
{
	float4 words[512];
};

[[vk::binding(0,1)]] ConstantBuffer<SpriteBlock> g_spriteBlock;

float SpriteWord( uint at ) { float4 four = g_spriteBlock.words[at >> 2]; return four[at & 3u]; }
#else
[[vk::binding(0,1)]] StructuredBuffer<float4> g_spriteWords;

float SpriteWord( uint at ) { float4 four = g_spriteWords[at >> 2]; return four[at & 3u]; }
#endif

SpriteData SpriteAt( uint index )
{
	uint at = index * SpriteWords;

	SpriteData s;
	s.PositionX = SpriteWord( at + 0 ); s.PositionY = SpriteWord( at + 1 ); s.PositionZ = SpriteWord( at + 2 );
	s.Pitch = SpriteWord( at + 3 ); s.Yaw = SpriteWord( at + 4 ); s.Roll = SpriteWord( at + 5 );
	s.ScaleX = SpriteWord( at + 6 ); s.ScaleY = SpriteWord( at + 7 );
	s.TintColor = asuint( SpriteWord( at + 8 ) );
	s.OverlayColor = asuint( SpriteWord( at + 9 ) );
	s.TextureHandle = asint( SpriteWord( at + 10 ) );
	s.RenderFlags = asint( SpriteWord( at + 11 ) );
	s.BillboardMode = asuint( SpriteWord( at + 12 ) );
	s.FogStrengthCutout = asuint( SpriteWord( at + 13 ) );
	s.Lighting = asuint( SpriteWord( at + 14 ) );
	s.DepthFeather = SpriteWord( at + 15 );
	s.Splots = asint( SpriteWord( at + 16 ) );
	s.Sequence = asint( SpriteWord( at + 17 ) );
	s.SequenceTime = SpriteWord( at + 18 );
	s.RotationOffset = SpriteWord( at + 19 );
	s.BlurLeading = SpriteWord( at + 20 ); s.BlurAmount = SpriteWord( at + 21 );
	s.BlurSpacing = SpriteWord( at + 22 ); s.BlurOpacity = SpriteWord( at + 23 );
	s.VelocityX = SpriteWord( at + 24 ); s.VelocityY = SpriteWord( at + 25 ); s.VelocityZ = SpriteWord( at + 26 );
	s.SheetX = SpriteWord( at + 27 ); s.SheetY = SpriteWord( at + 28 ); s.SheetZ = SpriteWord( at + 29 ); s.SheetW = SpriteWord( at + 30 );
	s.OffsetX = SpriteWord( at + 31 ); s.OffsetY = SpriteWord( at + 32 );
	s.CameraFade = asuint( SpriteWord( at + 33 ) );
	s.RegionX = SpriteWord( at + 34 ); s.RegionY = SpriteWord( at + 35 ); s.RegionW = SpriteWord( at + 36 ); s.RegionH = SpriteWord( at + 37 );
	return s;
}
#endif

//How the sprite is turned to face: the values of SpriteRenderer.BillboardMode.
static const uint BillboardAlways = 0;
static const uint BillboardYOnly = 1;
static const uint BillboardParticle = 2;
static const uint BillboardNone = 3;

//The bits of SpriteData.RenderFlags - SpriteBatchSceneObject.SpriteFlags.
static const int FlagFlipX = 0x2;
static const int FlagFlipY = 0x4;
static const int FlagAdditive = 0x10;
static const int FlagPixelated = 0x20;

struct V2F
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	nointerpolation uint sprite : TEXCOORD1;
	float3 world : TEXCOORD2;
	nointerpolation float3 normal : TEXCOORD3;

#ifdef STEREO
	//Which eye, where both are drawn at once: a sprite's V2F is its own, not a
	//surface's, so it carries the eye itself - see DEPTH_ID_FIELD in depth_id.slang.
	nointerpolation uint eyeId : TEXCOORD16;
#endif
};

struct Out
{
	float4 colour : SV_Target0;

#ifdef TARGET_SCENE
	float4 surface : SV_Target1;
#endif
};

//The corner this vertex is, out of the four the index buffer names. No vertex buffer: the pipeline is built with no vertex input at all, so the
//quad is the vertex id rather than something bound. The order matches the indices the batcher writes - 0,1,2, 0,2,3 around (0,0) (1,0) (1,1) (0,1).
float2 Corner( uint vertexId )
{
	return float2(
		( vertexId == 1 || vertexId == 2 ) ? 1.0 : 0.0,
		( vertexId >= 2 ) ? 1.0 : 0.0 );
}

//A colour out of the four bytes it was packed into.
float4 UnpackRgba8( uint packed )
{
	return float4(
		float( ( packed       ) & 0xFF ) / 255.0,
		float( ( packed >>  8 ) & 0xFF ) / 255.0,
		float( ( packed >> 16 ) & 0xFF ) / 255.0,
		float( ( packed >> 24 ) & 0xFF ) / 255.0 );
}

//Turn two axes about a third, for a sprite that carries a roll.
void Roll( float angleDegrees, float3 axis, inout float3 right, inout float3 up )
{
	if ( angleDegrees == 0.0 ) return;

	float a = radians( angleDegrees );
	float s = sin( a );
	float c = cos( a );

	float3 turnedRight = ( right * c ) + ( cross( axis, right ) * s );
	float3 turnedUp = ( up * c ) + ( cross( axis, up ) * s );

	right = turnedRight;
	up = turnedUp;
}

//The rotation the managed side means by three angles - the same product Rotation.From makes, so a
//sprite told to hold a particle's rotation turns exactly as a model given the same angles would.
float4 QuaternionFrom( float pitchDegrees, float yawDegrees, float rollDegrees )
{
	float pitch = radians( pitchDegrees ) * 0.5;
	float yaw = radians( yawDegrees ) * 0.5;
	float roll = radians( rollDegrees ) * 0.5;

	float sp = sin( pitch ), cp = cos( pitch );
	float sy = sin( yaw ), cy = cos( yaw );
	float sr = sin( roll ), cr = cos( roll );

	float srXcp = sr * cp, crXsp = cr * sp;
	float crXcp = cr * cp, srXsp = sr * sp;

	return float4(
		-( crXsp * cy + srXcp * sy ),
		crXcp * sy - srXsp * cy,
		crXsp * sy - srXcp * cy,
		crXcp * cy + srXsp * sy );
}

float3 Rotate( float4 q, float3 v )
{
	float3 t = 2.0 * cross( q.xyz, v );
	return v + ( q.w * t ) + cross( q.xyz, t );
}

//Sprite UV kept half a texel inside its region; pixelated sprites snap to texel centres
float2 SpriteReadAt( SpriteData sprite, float2 uv, float2 size )
{
	float2 texel = uv * size;

	if ( ( sprite.RenderFlags & FlagPixelated ) != 0 )
	{
		//Measured from texel middles: the nearest middle below, and how far on towards the next one. Held
		//at the middle until the last screen pixel before the edge between the two, then carried across it.
		float2 across = max( fwidth( texel ), 1e-5 );
		float2 fromMiddle = texel - 0.5;
		texel = floor( fromMiddle ) + 0.5 + saturate( ( frac( fromMiddle ) - 0.5 ) / across + 0.5 );
	}

	float2 low = float2( sprite.RegionX, sprite.RegionY ) * size + 0.5;
	float2 high = ( float2( sprite.RegionX, sprite.RegionY ) + float2( sprite.RegionW, sprite.RegionH ) ) * size - 0.5;

	return clamp( texel, low, max( low, high ) ) / size;
}

[shader("vertex")]
V2F MainVs( uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID )
{
	SpriteData sprite = SpriteAt( instanceId );

	float3 position = float3( sprite.PositionX, sprite.PositionY, sprite.PositionZ );

	//Facing the eye rather than facing along the camera's own axis: a sprite near the edge of a wide view leans
	//towards the lens this way, which is what a puff of smoke does and a camera-plane-aligned quad does not.
	float3 toEye = EYE_FRAME( camera ).xyz - position;
	float3 forward = ( dot( toEye, toEye ) > 1e-12 ) ? normalize( toEye ) : float3( 0, 0, 1 );

	//Up is Y here - see Vector3.Up - and looking straight down or straight up leaves nothing to cross against, so a sprite seen from above stands on Z.
	float3 worldUp = ( abs( forward.y ) > 0.9999 ) ? float3( 0, 0, 1 ) : float3( 0, 1, 0 );

	float3 right = normalize( cross( worldUp, forward ) );
	float3 up = cross( forward, right );

	if ( sprite.BillboardMode == BillboardYOnly )
	{
		//Standing upright and turning only about the world's up: what a tree or a flame wants, which should not lie down when looked at from above.
		float3 level = float3( forward.x, 0.0, forward.z );

		forward = ( dot( level, level ) > 1e-12 ) ? normalize( level ) : float3( 0, 0, 1 );
		up = float3( 0, 1, 0 );
		right = normalize( cross( up, forward ) );
	}

	if ( sprite.BillboardMode == BillboardParticle || sprite.BillboardMode == BillboardNone )
	{
		//The rotation is the sprite's own, all three of it: a particle that tumbles, or a sprite that faces the way its object does.
		//A quad here faces along -Z the way everything else in the engine does, so it is the rotation's right and up that span it.
		float4 q = QuaternionFrom( sprite.Pitch, sprite.Yaw, sprite.Roll );

		right = Rotate( q, float3( 1, 0, 0 ) );
		up = Rotate( q, float3( 0, 1, 0 ) );
		forward = Rotate( q, float3( 0, 0, -1 ) );
	}
	else if ( sprite.RotationOffset >= 0.0 )
	{
		//Turned to face the way it is going, as the picture lies on the screen: the velocity's shadow on the
		//quad's own plane says how far round, and the offset is the picture's own idea of which way up is.
		float3 velocity = float3( sprite.VelocityX, sprite.VelocityY, sprite.VelocityZ );
		float2 across = float2( dot( velocity, right ), dot( velocity, up ) );

		if ( dot( across, across ) > 1e-8 )
		{
			float heading = degrees( atan2( across.y, across.x ) ) - 90.0;
			Roll( heading + sprite.RotationOffset, forward, right, up );
		}
		else
		{
			Roll( sprite.Roll, forward, right, up );
		}
	}
	else
	{
		Roll( sprite.Roll, forward, right, up );
	}

	float2 corner = Corner( vertexId ) - float2( sprite.OffsetX, sprite.OffsetY );

	//Scale is the half extent, so a corner a whole width away from its opposite spans twice it. The
	//v axis runs down the picture and up runs up the world, which is why one of them is negated.
	float3 world = position
		+ ( right * ( corner.x * sprite.ScaleX * 2.0 ) )
		+ ( up * ( -corner.y * sprite.ScaleY * 2.0 ) );

	V2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
	o.world = world;
	o.normal = forward;

	float2 uv = Corner( vertexId );
	if ( ( sprite.RenderFlags & FlagFlipX ) != 0 ) uv.x = 1.0 - uv.x;
	if ( ( sprite.RenderFlags & FlagFlipY ) != 0 ) uv.y = 1.0 - uv.y;

	//Into the part of the picture this frame is - one cell of a sheet, or all of it.
	o.uv = float2( sprite.RegionX, sprite.RegionY ) + uv * float2( sprite.RegionW, sprite.RegionH );
	o.sprite = instanceId;
#ifdef STEREO
	o.eyeId = ViewOfThisVertex();
#endif

#ifdef LIGHT2D_SCENE
	//Into the 2D light's pictures, which are the screen grown by a margin: the screen shrunk inside them, and moved onto
	//their texels - see CoreLight2D.SnapOf
	o.position.xy = ( o.position.xy * g_frame[0].light2dScene.xz ) + ( g_frame[0].light2dSnap.xy * o.position.w );
#endif

	return o;
}

//The bit of a sprite's Lighting that asks for the scene's 2D lighting, above the 3D lit byte and below the
//tint's exponent - SpriteData.Lit2D
static const uint Lit2DFlag = 1u << 8;

//What the scene's 2D lighting makes of a sprite or tile here, as a multiplier on its colour: one where the
//scene has no 2D lighting, so a lit sprite in a scene nobody has lit is drawn unlit. The radiance cascades' light
//where this view worked it out (CoreLight2D), read through the view and shrunk into its margin; the global light
//alone where it did not - a perspective view, the portable frame
float3 Light2DAt( float3 world )
{
	float4 light2d = g_frame[0].light2d;

	if ( light2d.w < 0.5 ) return float3( 1.0, 1.0, 1.0 );

	float4 pictures = g_frame[0].light2dScene;

#ifndef PORTABLE
	if ( pictures.w > 0.0 )
	{
		float4 clip = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );
		float2 uv = ( ( ( ( clip.xy / clip.w ) * pictures.xz ) + g_frame[0].light2dSnap.xy ) * 0.5 ) + 0.5;

		return g_textures[uint( pictures.w )].SampleLevel( uv, 0.0 ).rgb;
	}
#endif

	return light2d.rgb;
}

//Wrapped lighting for sprites (back lights still count), no shadows
float3 LightOn( float3 world, float3 normal )
{
	float3 lit = SceneAmbient( SkyAmbient( normal ) );

	LightWalk walk = LightsHere( world );
	uint which;

	while ( NextLight( walk, which ) )
	{
		Light light = LightAt( which );

		float3 l;
		float attenuation = 1.0;

		if ( light.colour.w < 0.5 )
		{
			l = -light.direction.xyz;
		}
		else if ( kLampsOn == 0 )
		{
			continue;
		}
		else
		{
			LightLamp( which, light );

			float3 toLight = light.position.xyz - world;
			float distance = length( toLight );

			if ( distance > light.position.w ) continue;

			l = toLight / max( distance, 0.0001 );

			//The same window and falloff the surfaces use, so a lamp reaches a sprite exactly as far as it reaches the wall behind it.
			float reach = distance / max( light.position.w, 0.0001 );
			float falloff = saturate( 1.0 - reach * reach * reach * reach );
			float square = max( distance * distance, 0.0001 );
			float spread = light.shape.x == 1.0 ? square : pow( square, light.shape.x );

			attenuation = falloff * falloff / max( spread, 0.0001 );

			if ( light.colour.w > 1.5 )
			{
				float aligned = dot( -l, light.direction.xyz );
				attenuation *= saturate( ( aligned - light.cone.x ) / max( light.direction.w - light.cone.x, 0.0001 ) );
			}
		}

		//A sprite is lit only diffusely, so a light kept out of that gives it nothing.
		if ( attenuation <= 0.0 || ( LightWithholds( light ) & 1u ) != 0u ) continue;

		float wrapped = saturate( ( dot( normal, l ) * 0.5 ) + 0.5 );

		lit += light.colour.rgb * ( attenuation * wrapped );
	}

	return lit;
}
