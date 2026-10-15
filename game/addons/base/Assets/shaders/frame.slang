//Shared world-shader bindings: set 0 material, set 1 frame (bones, camera, lights, shadow atlases)

//The world draw push block, declared once; define ENGINE_WORLD_PUSH before including this
#ifdef ENGINE_WORLD_PUSH

//The engine vertex every world draw reads; bound by location only, normal/tangent unpacked lazily
#include "vertex_frame.slang"

struct VIn
{
	[[vk::location(0)]] float3 position;

	//Per-corner vertex tint (location 1), always bound, so faces can differ in colour within a material
	[[vk::location(1)]] float4 colour;

	//The normal, the tangent and the bitangent's handedness in one word.
	[[vk::location(2)]] uint frame;

	[[vk::location(3)]] float4 texCoord0;

	//The second set, which is where a mesh keeps the unwrap its lightmap was baked into. Meshes without one
	//leave it at nothing and fall back to the first set, exactly as the bake does - see lightmap_bake.shader.
	[[vk::location(4)]] float4 texCoord1;

	//The unit normal out of the frame word.
	property float3 normal
	{
		get { return VertexFrameNormal( frame ); }
		set { frame = VertexFrameEncode( newValue, VertexFrameTangent( frame ) ); }
	}

	//The unit tangent, the bitangent's handedness in w.
	property float4 tangent
	{
		get { return VertexFrameTangent( frame ); }
		set { frame = VertexFrameEncode( VertexFrameNormal( frame ), newValue ); }
	}

#ifdef SKINNED
	[[vk::location(6)]] uint4 bones;
	[[vk::location(7)]] float4 weights;
#endif

#ifdef MORPHED
	//Where this vertex's blend shape deltas start, and how many it has.
	[[vk::location(8)]] uint2 morphRun;
#endif
};

#ifdef CLUSTER

//For a cluster, a draw's numbers come from its instance, filled by cluster_main.slang before either stage;
//the push carries the pass's numbers
[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_visNamed;
	float4 g_visSpare0;
	float4 g_visSpare1;
	float4 g_visSpare2;
	float4 g_visSpare3;
};

static row_major float4x4 g_modelToWorld;
static float4 g_tint;
static float4 g_object;
static float4 g_draw0;
static float4 g_draw1;

#elif defined( INSTANCED )

//Drawn as one of a run of copies - see instanced_surface.slang, which fills these from the bone ring before
//either of the surface's stages runs. The push carries the pass's own numbers, as it does for a cluster.
[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_passNamed;
	float4 g_passSpare0;
	float4 g_passSpare1;
	float4 g_passSpare2;
	float4 g_passSpare3;
};

static row_major float4x4 g_modelToWorld;
static float4 g_tint;
static float4 g_object;
static float4 g_draw0;
static float4 g_draw1;

#else

[[vk::push_constant]]
cbuffer Push
{
	row_major float4x4 g_modelToWorld;

	//The draw's colour multiplier, with the material's Tint folded in so there is one tint
	float4 g_tint;

	//The draw's place in the baked atlas: scale xy, offset zw; a zero scale means no chart, nothing baked
	float4 g_object;

	//Numbers the draw set itself, read by Attribute( ... ) parameters: eight floats in declared order
	//- see CoreShaderParams.DrawSlots
	float4 g_draw0;
	float4 g_draw1;
};

#endif
#endif

#include "frame_data.slang"
#include "picture_size.slang"

//A material's numbers, for a shader without its own; a PARAMETERS block writes its own and sets this define
//- see CoreShaderParams
#ifndef ENGINE_MATERIAL_BLOCK

struct MaterialParams
{
	float4 surface; //Roughness, metalness, emission, alpha cutoff
};

[[vk::binding(4,0)]] ConstantBuffer<MaterialParams> g_material;

#endif

[[vk::binding(0,0)]] Sampler2D g_color;
[[vk::binding(1,0)]] Sampler2D g_normal;
//Binding 2 belongs to passes; the portable frame uses it for the material's first extra texture
[[vk::binding(3,0)]] Sampler2D g_emission;

#ifdef PORTABLE
[[vk::binding(2,0)]] Sampler2D g_third;
#endif

#ifdef WEBGL
//A uniform block where WebGL has no storage buffers - the 128 matrices a draw's
//bones are, eight kilobytes of the sixteen WebGL promises - and read as ever.
struct BoneBlock
{
	float4x4 bones[128];
};

[[vk::binding(0,1)]] ConstantBuffer<BoneBlock> g_boneBlock;

#define g_bones g_boneBlock.bones
#else
[[vk::binding(0,1)]] StructuredBuffer<float4x4> g_bones;
#endif

#ifdef INSTANCED

//Matrices per copy in the ring: the world matrix, and one holding the push block's four float4s
static const uint InstanceMatrices = 2u;

//Per-copy instance numbers from the ring skinned draws use for bones; the two never overlap
void InstanceNumbers( uint copy )
{
	float4x4 world = g_bones[( copy * InstanceMatrices )];
	float4x4 rest = g_bones[( copy * InstanceMatrices ) + 1u];

	g_modelToWorld = world;
	g_tint = rest[0];
	g_object = rest[1];
	g_draw0 = rest[2];
	g_draw1 = rest[3];
}

#endif

#ifdef MORPHED

//What one blend shape does to one vertex. Matches the on-disk MorphDelta.
struct MorphDelta
{
	uint morph;
	float3 position;
	float3 normal;
};

//Loop bounds for morphs so a bad vertex attribute can't hang the GPU
static const uint MorphsPerVertex = 64;
static const uint MorphsPerObject = 256;

//How far each shape of this object is pushed, from the frame scratch at its own offset, like the bones
[[vk::binding(6,1)]] StructuredBuffer<float> g_morphWeights;

//The deltas belong to the model, so they bind at set 2 with the draw; read as bytes since the 28-byte
//on-disk struct is not packed as a structured buffer's float3
[[vk::binding(0,2)]] ByteAddressBuffer g_morphDeltas;

//MorphDelta byte layout; must match sbc::assets::MorphDelta (checked by ModelMorphLayoutTests)
static const uint MorphDeltaStride = 28;
static const uint MorphDeltaPosition = 4;
static const uint MorphDeltaNormal = 16;

//One delta, out of the bytes the model was compiled with.
MorphDelta LoadMorphDelta( uint index )
{
	uint at = index * MorphDeltaStride;

	MorphDelta delta;
	delta.morph = g_morphDeltas.Load( at );
	delta.position = asfloat( g_morphDeltas.Load3( at + MorphDeltaPosition ) );
	delta.normal = asfloat( g_morphDeltas.Load3( at + MorphDeltaNormal ) );

	return delta;
}

#endif

//The debug view, nought for none, numbered as SceneCameraDebugMode; frame-uniform, so a predicted branch
int DebugView() { return int( g_frame[0].viewInfo.x ); }

//Debug view colours; a distinct colour per culled instance, magenta for no instance
float3 CullingColour( uint id )
{
	if ( id == 0 ) return float3( 1.0, 0.0, 1.0 );

	//By copy, not by triangle - the low seven bits are the triangle.
	uint h = ( id >> 7 ) * 2654435761u;
	h ^= h >> 13;
	h *= 0x5bd1e995u;
	h ^= h >> 15;

	return float3( float( h & 255u ), float( ( h >> 8 ) & 255u ), float( ( h >> 16 ) & 255u ) ) / 255.0 * 0.85 + 0.15;
}

float3 DebugSurface( int view, float3 albedo, float3 normal, float roughness,
	float metalness, float occlusion, float2 uv, uint id,
	float3 diffuseLit, float3 specularLit, float3 shaded )
{
	switch ( view )
	{
		case 60: return CullingColour( id );
		//Full bright: the colour map as it is, with no light in it at all.
		case 1: return albedo;

		//The two halves of the lighting, apart. What a light does to a surface is these
		//added together, and which of them is wrong is not something the sum can be asked.
		case 2: return diffuseLit;
		case 3: return specularLit;

		//Where the texture is, as colour: red across, green down, and the tiling visible as the ramp starting over.
		case 6: return float3( frac( uv ), 0.0 );

		case 10: return albedo;
		case 12: return float3( roughness, roughness, roughness );
		case 13: return float3( metalness, metalness, metalness );
		case 14: return float3( occlusion, occlusion, occlusion );

		//The mapped world normal in the half-and-half encoding: flat ground is pale blue-green
		case 21: return normal * 0.5 + 0.5;

		default: return shaded;
	}
}

#include "skin_blend.slang"

//Four weighted bones, clamped to the descriptor's 128, as matrices or dual quaternions (skin_blend.slang);
//zero-weight slots are not fetched, as index 255 loses the device on this driver even times zero
float4x4 SkinAt( uint4 bones, float4 weights )
{
	float4x4 first = g_bones[min( bones.x, 127u )];

	return SkinBlend( first,
		weights.y > 0.0 ? g_bones[min( bones.y, 127u )] : first,
		weights.z > 0.0 ? g_bones[min( bones.z, 127u )] : first,
		weights.w > 0.0 ? g_bones[min( bones.w, 127u )] : first,
		weights );
}

//The tangent re-orthogonalised against the normal - or, for a mesh that carries none (an OBJ, a
//generated grid), one made up perpendicular to it, so the frame never normalises nothing into NaN.
float3 TangentAgainst( float3 n, float3 tangent )
{
	float3 t = tangent - ( n * dot( n, tangent ) );
	if ( dot( t, t ) > 1e-12 ) return normalize( t );
	return normalize( cross( n, abs( n.y ) < 0.999 ? float3( 0.0, 1.0, 0.0 ) : float3( 1.0, 0.0, 0.0 ) ) );
}

//Portable frame draws straight to screen: no texture table, no occlusion, lights in the block
#ifndef PORTABLE
//Every picture the device has, indexed rather than bound. Lightmaps and voxel
//palettes both pick a slot per sample, so the table lives here with the frame.
[[vk::binding(0,3)]] Sampler2D g_textures[];

//The "Occlusion" picture drawn after the pre-pass (AmbientOcclusion), by table index in viewInfo.y; nought,
//with settings.x, where none was drawn - see CoreSceneRenderer.LayOcclusion
#define g_occlusionMap g_textures[uint( g_frame[0].viewInfo.y )]



//Frame lights buffer: 16-float header (count first), then 20 floats per light
[[vk::binding(7,1)]] ByteAddressBuffer g_lightData;
#else
//Portable stand-in for the occlusion picture that reads white, so full-frame surfaces still build
struct NoOcclusion
{
	float4 SampleLevel( float2 uv, float level ) { return 1.0; }
	float4 Sample( float2 uv ) { return 1.0; }
};

static NoOcclusion g_occlusionMap;
#endif

//How many lights this frame has.
uint LightCount()
{
#ifdef PORTABLE
	return uint( max( EYE_FRAME( camera ).w, 0.0 ) );
#else
	return g_lightData.Load( 0 );
#endif
}

//Whether the frame kept the world's depth after the pre-pass (a camera DepthPicture), for reading what is behind
bool HasDepthBehind()
{
#ifdef PORTABLE
	//No table of pictures in the portable frame to have kept one in.
	return false;
#else
	return g_frame[0].viewInfo.z > 0.0;
#endif
}

//The depth of the opaque world at a point on the screen, as the pre-pass wrote it: nought at the near plane,
//one at the far and where nothing was drawn. `screen` is SV_Position.xy. One where the frame kept no picture.
float DepthBehind( float2 screen )
{
#ifdef PORTABLE
	return 1.0;
#else
	if ( g_frame[0].viewInfo.z <= 0.0 ) return 1.0;

	float2 uv = screen / g_frame[0].settings.yz;

	//Frame-uniform, like the sky's table below - see SkyTableAt.
	return g_textures[uint( g_frame[0].viewInfo.z )].SampleLevel( uv, 0.0 ).r;
#endif
}

//Where that depth is, in the world.
float3 WorldBehind( float2 screen, float depth )
{
	float2 uv = screen / g_frame[0].settings.yz;
	float4 clip = float4( ( uv * 2.0 ) - 1.0, depth, 1.0 );
	float4 world = mul( clip, EYE_FRAME( clipToWorld ) );

	return world.xyz / world.w;
}

//The kind's fraction in twentieths: twice the withheld terms (1 no diffuse, 2 no specular, 3 neither) plus one
//where the light is in the bake - see CoreSceneRenderer.BuildFrame
uint LightCode( Light light )
{
	return uint( round( frac( light.colour.w ) * 20.0 ) );
}

uint LightWithholds( Light light )
{
	return LightCode( light ) >> 1;
}

//Whether the light is in the bake: a lightmapped surface has it already and takes only what movers shade of it
bool LightBaked( Light light )
{
	return ( LightCode( light ) & 1u ) != 0u;
}

Light LightAt( uint index )
{
#ifdef PORTABLE
	return g_frame[0].lights[min( index, 15u )];
#else
	uint at = 64 + ( index * 80 );

	//Load only colour and direction here; lamp-only fields are loaded in LightLamp inside its branch
	Light one;
	one.colour = asfloat( g_lightData.Load4( at + 16 ) );
	one.direction = asfloat( g_lightData.Load4( at + 32 ) );

	one.position = float4( 0.0, 0.0, 0.0, 0.0 );
	one.cone = float4( 0.0, 0.0, 0.0, 0.0 );
	one.shape = float4( 1.0, 0.0, 0.0, 0.0 );

	return one;
#endif
}

//The three a lamp needs on top of what every light carries - see LightAt.
void LightLamp( uint index, inout Light one )
{
#ifndef PORTABLE
	uint at = 64 + ( index * 80 );

	one.position = asfloat( g_lightData.Load4( at ) );
	one.cone = asfloat( g_lightData.Load4( at + 48 ) );
	one.shape = asfloat( g_lightData.Load4( at + 64 ) );
#endif
}

//The lights a pixel walks: its grid cell, or all of them without a grid - see CoreLights
struct LightWalk
{
	//How many to walk.
	uint count;

	//Where its cell's list starts, in words; nothing where there is no cell.
	uint first;

	bool celled;
};

LightWalk LightsHere( float2 screen, float viewDepth )
{
	LightWalk walk;
	walk.count = LightCount();
	walk.first = 0;
	walk.celled = false;

#ifndef PORTABLE
	if ( kGridOn == 0 ) return walk;

	uint cellsX = g_lightData.Load( 4 );
	if ( cellsX == 0 ) return walk;

	uint cellsY = g_lightData.Load( 8 );
	uint cellsZ = g_lightData.Load( 12 );
	uint perCell = g_lightData.Load( 16 );
	uint cellsAt = g_lightData.Load( 20 );
	float near = asfloat( g_lightData.Load( 24 ) );
	float scale = asfloat( g_lightData.Load( 28 ) );

	if ( cellsY == 0 || cellsZ == 0 || near <= 0.0 ) return walk;

	//How wide a cell is, out of the header rather than written down here: the grid is cut by CoreLights.TileSize
	//and this is the one place that has to agree with it. Sixteen where a block was written before that word was.
	uint tile = max( g_lightData.Load( 40 ), 1u );

	//Along the depth in the logarithm, which is how the cull cut it.
	uint cx = min( uint( max( screen.x, 0.0 ) ) / tile, cellsX - 1 );
	uint cy = min( uint( max( screen.y, 0.0 ) ) / tile, cellsY - 1 );
	uint cz = min( uint( max( log( max( viewDepth, near ) / near ) * scale, 0.0 ) ), cellsZ - 1 );

	uint at = cellsAt + ( ( cx + ( cy * cellsX ) + ( cz * cellsX * cellsY ) ) * ( perCell + 1 ) );

	walk.count = min( g_lightData.Load( at * 4 ), perCell );
	walk.first = at + 1;
	walk.celled = true;
#endif

	return walk;
}

//The number of the light at one step of that walk.
uint LightHere( LightWalk walk, uint step )
{
#ifdef PORTABLE
	return step;
#else
	return walk.celled ? g_lightData.Load( ( walk.first + step ) * 4 ) : step;
#endif
}

//Decals follow the lights in the same buffer; each is an oriented box projected along minus z

//One decal, as CoreDecals.Pack laid it out.
struct Decal
{
	float3 axisX; float overWidth;
	float3 axisY; float overHeight;
	float3 axisZ; float overDepth;
	float3 centre; float attenuation;

	float4 tint;

	float colourMix;
	float emission;
	float parallax;
	float coverage;

	//Where its pictures sit in g_textures, and nought for none.
	uint colourMap;
	uint normalMap;
	uint rmoMap;
	uint heightMap;
	uint emissionMap;

	//Which of them come back from a sample decoded from sRGB though they hold data - one bit each in
	//the order above - and so have to be encoded again to get the stored number back. See DecalData.
	uint srgb;

	float coverageRange;

	//How far from the centre the box reaches at most - its half diagonal.
	float radius;
};

//How many decals this frame has.
uint DecalCount()
{
#ifdef PORTABLE
	return 0u;
#else
	return g_lightData.Load( 32 );
#endif
}

//A decal's centre and reach, all a pixel outside it reads - see DecalAt for the rest
float4 DecalReach( uint index )
{
#ifdef PORTABLE
	return float4( 0.0, 0.0, 0.0, 0.0 );
#else
	uint at = ( g_lightData.Load( 36 ) + ( index * 32u ) ) * 4u;

	return float4( asfloat( g_lightData.Load3( at + 48 ) ), asfloat( g_lightData.Load( at + 124 ) ) );
#endif
}

//One of them.
Decal DecalAt( uint index )
{
	Decal one;

#ifdef PORTABLE
	one.axisX = float3( 1.0, 0.0, 0.0 ); one.overWidth = 0.0;
	one.axisY = float3( 0.0, 1.0, 0.0 ); one.overHeight = 0.0;
	one.axisZ = float3( 0.0, 0.0, 1.0 ); one.overDepth = 0.0;
	one.centre = float3( 0.0, 0.0, 0.0 ); one.attenuation = 0.0;
	one.tint = float4( 1.0, 1.0, 1.0, 1.0 );
	one.colourMix = 1.0; one.emission = 0.0; one.parallax = 0.0; one.coverage = 1.0;
	one.colourMap = 0u; one.normalMap = 0u; one.rmoMap = 0u; one.heightMap = 0u; one.emissionMap = 0u;
	one.srgb = 0u;
	one.coverageRange = 0.0;
	one.radius = 0.0;
#else
	uint at = ( g_lightData.Load( 36 ) + ( index * 32u ) ) * 4u;

	float4 x = asfloat( g_lightData.Load4( at ) );
	float4 y = asfloat( g_lightData.Load4( at + 16 ) );
	float4 z = asfloat( g_lightData.Load4( at + 32 ) );
	float4 c = asfloat( g_lightData.Load4( at + 48 ) );
	float4 mix = asfloat( g_lightData.Load4( at + 80 ) );
	uint4 maps = g_lightData.Load4( at + 96 );
	uint4 more = g_lightData.Load4( at + 112 );

	one.axisX = x.xyz; one.overWidth = x.w;
	one.axisY = y.xyz; one.overHeight = y.w;
	one.axisZ = z.xyz; one.overDepth = z.w;
	one.centre = c.xyz; one.attenuation = c.w;

	one.tint = asfloat( g_lightData.Load4( at + 64 ) );

	one.colourMix = mix.x;
	one.emission = mix.y;
	one.parallax = mix.z;
	one.coverage = mix.w;

	one.colourMap = maps.x;
	one.normalMap = maps.y;
	one.rmoMap = maps.z;
	one.heightMap = maps.w;

	one.emissionMap = more.x;
	one.srgb = more.y;
	one.coverageRange = asfloat( more.z );
	one.radius = asfloat( more.w );
#endif

	return one;
}

//A sample of one of a decal's pictures, or the stand-in where it has none.
float4 DecalSample( uint map, float2 uv, float2 dx, float2 dy, float4 instead )
{
#ifdef PORTABLE
	return instead;
#else
	if ( map == 0u ) return instead;
	return g_textures[NonUniformResourceIndex( map )].SampleGrad( uv, dx, dy );
#endif
}

//Re-encodes a data map the device sRGB-decoded (runtime-generated maps only)
float3 DecalData( float3 decoded, bool encode )
{
	if ( !encode ) return decoded;

	float3 low = decoded * 12.92;
	float3 high = 1.055 * pow( max( decoded, 0.0 ), 1.0 / 2.4 ) - 0.055;

	//One for a channel low enough to be on the straight part of the curve.
	float3 straight = step( decoded, 0.0031308 );

	return lerp( high, low, straight );
}

//A shader's extra texture, read bindlessly by index from the material; index 0 means use Default
struct EngineTexture
{
	float4 where;   //Picture in the table x, nought for none
	float4 instead; //What to read where there is none

	bool Exists() { return where.x >= 0.5; }

#ifdef PORTABLE
	//No table here: the one picture of these a material may bind is in its third slot, and
	//named by one - see CoreResources.PictureNumber. Any other is nought, and reads its default.
	float4 Sample( float2 uv ) { return Exists() ? g_third.Sample( uv ) : instead; }
	float4 SampleBias( float2 uv, float bias ) { return Exists() ? g_third.SampleBias( uv, bias ) : instead; }
	float4 SampleLevel( float2 uv, float level ) { return Exists() ? g_third.SampleLevel( uv, level ) : instead; }
	float4 SampleGrad( float2 uv, float2 dx, float2 dy ) { return Exists() ? g_third.SampleGrad( uv, dx, dy ) : instead; }
#else
	float4 Sample( float2 uv )
	{
		if ( !Exists() ) return instead;

		return g_textures[NonUniformResourceIndex( uint( where.x ) )].Sample( uv );
	}

	float4 SampleBias( float2 uv, float bias )
	{
		if ( !Exists() ) return instead;

		return g_textures[NonUniformResourceIndex( uint( where.x ) )].SampleBias( uv, bias );
	}

	float4 SampleLevel( float2 uv, float level )
	{
		if ( !Exists() ) return instead;

		return g_textures[NonUniformResourceIndex( uint( where.x ) )].SampleLevel( uv, level );
	}

	float4 SampleGrad( float2 uv, float2 dx, float2 dy )
	{
		if ( !Exists() ) return instead;

		return g_textures[NonUniformResourceIndex( uint( where.x ) )].SampleGrad( uv, dx, dy );
	}
#endif
};

//Shared sampling entry with the upscaler's mip bias (viewInfo.w), so textures stay sharp
#ifdef PORTABLE
float MipBias() { return 0.0; }
float4 SampleUv( Sampler2D t, float2 uv ) { return t.Sample( uv ); }
float4 SampleUv( EngineTexture t, float2 uv ) { return t.Sample( uv ); }
float2 UvDx( float2 uv ) { return ddx( uv ); }
float2 UvDy( float2 uv ) { return ddy( uv ); }
#else
float MipBias() { return g_frame[0].viewInfo.w; }
float4 SampleUv( Sampler2D t, float2 uv ) { return t.SampleBias( uv, MipBias() ); }
float4 SampleUv( EngineTexture t, float2 uv ) { return t.SampleBias( uv, MipBias() ); }
float2 UvDx( float2 uv ) { return ddx( uv ) * exp2( MipBias() ); }
float2 UvDy( float2 uv ) { return ddy( uv ) * exp2( MipBias() ); }
#endif
//Mip level computed manually from UV derivatives, since WGSL and GLSL ES 3.0 lack a LOD query
float LevelOf( Sampler2D t, float2 uv )
{
#ifdef PORTABLE
	float2 texels = float2( PictureSize( t ) );
	float2 across = ddx( uv ) * texels;
	float2 down = ddy( uv ) * texels;

	//Nothing takes the log of nought: a step of no length is the finest level.
	return 0.5 * log2( max( max( dot( across, across ), dot( down, down ) ), 1.0e-12 ) );
#else
	return t.CalculateLevelOfDetail( uv );
#endif
}

bool HasSky()
{
	return g_frame[0].zenith.a > 0.5;
}

//Ambient light: sky or probe light blended toward the scene's flat ambient by its alpha
float3 SceneAmbient( float3 around )
{
	return lerp( around, g_frame[0].ambient.rgb, g_frame[0].ambient.a );
}

//Whether that sky has a sun in it, which is what nearly all of its colour comes from.
bool HasSun()
{
	return g_frame[0].sunDirection.w > 0.0;
}

//Whether the sun casts shadows (its Shadows box); off, it lights all it faces and no map is read - see SunShadow
bool SunCasts()
{
	return g_frame[0].sunEye.w > 0.5;
}

//Zenith, horizon and ground colours only: the sky of a frame without a sun, a preview or an unlit bake
float3 SkyGradientOf( float height )
{
	float3 upper = lerp( g_frame[0].horizon.rgb, g_frame[0].zenith.rgb, saturate( height ) );
	float3 lower = lerp( g_frame[0].horizon.rgb, g_frame[0].ground.rgb, saturate( -height ) );
	return lerp( lower, upper, saturate( height * 0.5 + 0.5 ) );
}

float3 SkyGradient( float3 direction ) { return SkyGradientOf( direction.y ); }

//Sun colour after atmospheric extinction by sun height; computed once a frame on the CPU
float3 SunLight()
{
	return g_frame[0].sunLight.rgb;
}

//Night amount from sun height (0 day, 1 night); uniform, so branching on it is cheap
float NightFactor()
{
	return g_frame[0].sunLight.a;
}

//Samples across each of the table's two axes - see VkDevice.SkyTableSide
static const float SkyTableSide = 64.0;

#ifdef PORTABLE
//Portable frame's sky table as a sampled picture, so filtering is one hardware sample
[[vk::binding(5,1)]] Sampler2D g_skyTable;
#endif

//Where a direction lands on a panorama turned about up by turn radians: u round from -X the way Blender lays one out,
//v straight down from the zenith. CorePanorama.Uv is the same on the CPU
float2 PanoramaUv( float3 direction, float turn )
{
	float s = sin( turn );
	float c = cos( turn );

	float x = ( direction.x * c ) - ( direction.z * s );
	float z = ( direction.x * s ) + ( direction.z * c );

	return float2( ( atan2( -z, -x ) * 0.15915494 ) + 0.5, 0.5 - ( asin( clamp( direction.y, -1.0, 1.0 ) ) * 0.31830989 ) );
}

//The sky light's octahedral layout (CoreSkyLight): the upper half in the middle diamond, the lower folded into the
//corners, so the only seams lie below the horizon. Unnormalised: linear across each of the eight faces
float3 SkyLightRay( float2 uv )
{
	float2 p = ( uv * 2.0 ) - 1.0;
	float y = 1.0 - abs( p.x ) - abs( p.y );

	if ( y < 0.0 ) p = ( 1.0 - abs( p.yx ) ) * float2( p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0 );

	return float3( p.x, y, p.y );
}

//Where a direction lands in the sky light's pictures: SkyLightRay's inverse
float2 SkyLightUv( float3 direction )
{
	float2 p = direction.xz / ( abs( direction.x ) + abs( direction.y ) + abs( direction.z ) );

	if ( direction.y < 0.0 ) p = ( 1.0 - abs( p.yx ) ) * float2( p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0 );

	return ( p * 0.5 ) + 0.5;
}

//Sky table UV: x sun cosine, y sqrt of height, as sky_surface.slang; a portable panorama's is laid out as a panorama
float2 SkyTableUv( float3 direction )
{
#ifdef PORTABLE
	if ( kPanoramaOn != 0 && g_frame[0].panorama.w > 0.5 )
	{
		float2 uv = PanoramaUv( direction, 0.0 );

		//Held to the first and last rows' centres: the picture's sampler may wrap, and the zenith is not the ground
		return float2( uv.x, clamp( uv.y, 0.5 / SkyTableSide, 1.0 - ( 0.5 / SkyTableSide ) ) );
	}
#endif

	float up = saturate( direction.y * 0.5 + 0.5 );

	float2 at = float2( saturate( dot( direction, g_frame[0].sunDirection.xyz ) ), sqrt( up ) );

	//Onto the texel centres: nought is the first, one the last.
	return ( at * ( SkyTableSide - 1.0 ) + 0.5 ) / SkyTableSide;
}

//Reads the sky table for a direction, false if the frame has none; shared by ambient and reflections
bool SkyTableAt( float3 direction, out float3 colour )
{
	colour = float3( 0.0, 0.0, 0.0 );

#ifndef PORTABLE
	uint table = uint( g_frame[0].settings.w );

	if ( kSkyTableOn == 0 || table == 0 ) return false;

	//Not NonUniformResourceIndex: the table index is frame-uniform, so it's a scalar load
	colour = g_textures[table].SampleLevel( SkyTableUv( direction ), 0.0 ).rgb;

	return true;
#else
	float4 read = g_skyTable.SampleLevel( SkyTableUv( direction ), 0.0 );

	//Alpha one is a drawn table - see sky_surface.slang; a device that drew none reads nought
	//from a cleared picture, and the caller falls back on the gradient
	if ( read.a < 0.5 ) return false;

	colour = read.rgb;

	return true;
#endif
}

//The ambient factor the frame laid: a quarter of the procedural sky, tuned to be seen, all of a panorama's
float SkyAmbientScale()
{
	return g_frame[0].skyLight.w > 0.0 ? g_frame[0].skyLight.w : 0.25;
}

//Sky ambient for a normal: the capture's irradiance where the frame made one (CoreSkyLight), else the sky table
float3 SkyAmbient( float3 normal )
{
#ifndef PORTABLE
	uint irradiance = uint( g_frame[0].skyLight.z );
	if ( irradiance != 0u ) return g_textures[irradiance].SampleLevel( SkyLightUv( normal ), 0.0 ).rgb;
#endif

	float3 held;

	if ( SkyTableAt( normal, held ) ) return held;

	//The scene's sky colours alone: a frame without a table is a preview, a bake, or a sky that never drew
	return SkyGradientOf( normal.y );
}

//Sky and flat ambient as a surface facing way gets them now - what a baked point's sky visibility scales
float3 SkyLit( float3 way )
{
	return SceneAmbient( HasSky() ? SkyAmbient( way ) * SkyAmbientScale() : float3( 0.0, 0.0, 0.0 ) );
}

//Sky colour for distant fog and reflections: the capture's middle level where the frame made one, else the table
float3 SkyDrawn( float3 direction )
{
#ifndef PORTABLE
	uint levels = uint( g_frame[0].skyLight.x );
	if ( levels != 0u ) return g_textures[levels].SampleLevel( SkyLightUv( direction ), g_frame[0].skyLight.y * 0.5 ).rgb;
#endif

	float3 sky;

	if ( SkyTableAt( direction, sky ) ) return sky;

	return SkyGradientOf( direction.y );
}

//The sky's GGX reflection levels, roughness level / ( levels - 1 ) - see sky_light.slang; else SkyDrawn
float3 SkyReflected( float3 direction, float roughness )
{
#ifndef PORTABLE
	uint levels = uint( g_frame[0].skyLight.x );

	if ( levels != 0u )
	{
		float level = saturate( roughness ) * ( g_frame[0].skyLight.y - 1.0 );
		return g_textures[levels].SampleLevel( SkyLightUv( direction ), level ).rgb;
	}
#endif

	return SkyDrawn( direction );
}

#ifndef PORTABLE
//Every picture as depth to compare against, through the shadow sampler (VkTextures.ShadowMapCreate);
//a drawer's maps are its own, so the frame block says where they are
[[vk::binding(0,3)]] Sampler2DArrayShadow g_shadowMaps[];

//And the same maps read as plain depth - no compare - through their second place in the table, for
//how far in front of a point what shades it stands; see VkTextures.ShadowMapCreate and ShadowBlocker.
[[vk::binding(0,3)]] Sampler2DArray g_depthMaps[];
#endif

//CoreSunPages' layout: texels a page, pages a level across, and how far a level reads (ReachPages there),
//which must agree or an edge tap lands on the torus's far side
static const float SunPageTexels = 32.0;
static const float SunPageGrid = 64.0;
static const float SunReachPages = 30.875;

//And the map of what moves: texels a zone across.
static const float MoverTexels = 1024.0;

//Shadow receiver step back along the light, in map texels; slopes are handled per tap instead
static const float ShadowBiasTexels = 1.0;

//How far sun reads step off the surface, in the read level's texels: past a compared tap's four
static const float SunLiftTexels = 2.0;

//One hard shadow compare (1 lit), against the receiver's plane via slope to avoid self-shadow streaks
float ShadowSharp( uint map, float3 at, float compare, float2 slope )
{
#ifdef PORTABLE
	return 1.0;
#else
	return g_shadowMaps[map].SampleCmpLevelZero( at, compare - ( 0.5 * ( abs( slope.x ) + abs( slope.y ) ) ) );
#endif
}

//The widest a shadow edge spreads each side, in the read map's texels - a lamp in a shade a metre from what it shades
//over a blocker half way throws ten or twenty; ShadowEdge looks this far out, ShadowFilter spreads its taps as far
static const float ShadowSoftestTexels = 16.0;

//The near edge ShadowEdge checks first, and past which ShadowFilter turns from its grid to its spiral
static const float ShadowNearTexels = 2.0;

//A pixel's turn of the soft filter's spiral as its cosine and sine, from where it is on the screen (interleaved gradient
//noise, Jimenez 2014); set by the shading before it reads shadows, unturned where nothing set it
static float2 g_shadowTurn = float2( 1.0, 0.0 );

void ShadowTurnAt( float2 screen )
{
	float turn = 6.2831853 * frac( 52.9829189 * frac( dot( screen, float2( 0.06711056, 0.00583715 ) ) ) );
	g_shadowTurn = float2( cos( turn ), sin( turn ) );
}

//Five taps near and four out as far as the filter can spread (widest, in texels) detect a shadow edge; when they
//agree, that's the final lit value and filtering is skipped. A filter no wider than the near ring needs no far one
bool ShadowEdge( uint map, float3 at, float compare, float texel, float2 slope, float widest, out float lit )
{
#ifdef PORTABLE
	lit = 1.0;
	return false;
#else
	compare -= 0.5 * ( abs( slope.x ) + abs( slope.y ) );

	float least = 1.0;
	float most = 0.0;

	uint rings = widest > ShadowNearTexels ? 2u : 1u;

	[unroll]
	for ( uint ring = 0u; ring < 2u; ++ring )
	{
		if ( ring >= rings ) break;

		float reach = ring == 0u ? ShadowNearTexels : min( widest, ShadowSoftestTexels ) * 0.75;

		[unroll]
		for ( uint k = 0u; k < 4u; ++k )
		{
			float2 o = float2( ( k & 1u ) != 0u ? reach : -reach, ( k & 2u ) != 0u ? reach : -reach );
			float tap = g_shadowMaps[map].SampleCmpLevelZero( at + float3( o * texel, 0.0 ), compare + dot( o, slope ) );

			least = min( least, tap );
			most = max( most, tap );
		}
	}

	float e = g_shadowMaps[map].SampleCmpLevelZero( at, compare );
	least = min( least, e );
	most = max( most, e );

	lit = least;
	return most - least >= 0.001;
#endif
}

//PCSS blocker search: average occluder depth over nine gathers, a 3x3 spread half the softest edge wide, so a blocker
//that throws a wide penumbra is found; negative when nothing blocks
float ShadowBlocker( uint depthMap, float3 at, float compare, float texel, float2 slope )
{
#ifdef PORTABLE
	return -1.0;
#else
	float margin = 0.5 * ( abs( slope.x ) + abs( slope.y ) );
	float sum = 0.0;
	float found = 0.0;
	float apart = ShadowSoftestTexels * 0.25;

	[unroll]
	for ( uint i = 0u; i < 9u; i++ )
	{
		float2 o = ( float2( float( i % 3u ), float( i / 3u ) ) - 1.0 ) * apart;
		float4 d = g_depthMaps[depthMap].Gather( at + float3( o * texel, 0.0 ) );
		float4 hit = step( d, ( compare + dot( o, slope ) - margin ).xxxx );

		sum += dot( hit, d );
		found += dot( hit, 1.0.xxxx );
	}

	return found > 0.5 ? sum / found : -1.0;
#endif
}

//PCSS filter `soft` texels each side: 4x4 compared taps on a grid up to ShadowNearTexels, past it 24 on a golden-angle
//spiral turned by g_shadowTurn, as a stretched grid would band; under half a texel ShadowSharp
float ShadowFilter( uint map, float3 at, float compare, float texel, float2 slope, float soft )
{
#ifdef PORTABLE
	return 1.0;
#else
	if ( soft < 0.5 ) return ShadowSharp( map, at, compare, slope );

	compare -= 0.5 * ( abs( slope.x ) + abs( slope.y ) );

	if ( soft > ShadowNearTexels )
	{
		static const uint Taps = 24u;
		float spiral = 0.0;

		[unroll]
		for ( uint k = 0u; k < Taps; k++ )
		{
			//Evenly over the disc by area, each tap a golden angle round from the last: constants once unrolled,
			//turned by the pixel's turn
			float r = sqrt( ( float( k ) + 0.5 ) / float( Taps ) );
			float a = float( k ) * 2.39996323;
			float2 spot = float2( cos( a ), sin( a ) ) * r;
			float2 turn = g_shadowTurn;
			float2 o = float2( ( spot.x * turn.x ) - ( spot.y * turn.y ), ( spot.x * turn.y ) + ( spot.y * turn.x ) ) * soft;

			spiral += g_shadowMaps[map].SampleCmpLevelZero( at + float3( o * texel, 0.0 ), compare + dot( o, slope ) );
		}

		return spiral / float( Taps );
	}

	//1-3-3-1 weights, so the outer taps fade into ShadowEdge's early answer; equal weights left a faint stair
	static const float weights[4] = { 1.0, 3.0, 3.0, 1.0 };

	float apart = soft / 1.5;
	float lit = 0.0;

	[unroll]
	for ( uint j = 0u; j < 4u; j++ )
	{
		[unroll]
		for ( uint i = 0u; i < 4u; i++ )
		{
			float2 o = ( float2( float( i ), float( j ) ) - 1.5 ) * apart;
			lit += weights[i] * weights[j] * g_shadowMaps[map].SampleCmpLevelZero( at + float3( o * texel, 0.0 ), compare + dot( o, slope ) );
		}
	}

	return lit / 64.0;
#endif
}

//One tap for a point in the air, with no plane to lie along or step off; only VolumeFog asks
float ShadowTap( uint map, float3 at, float compare )
{
#ifdef PORTABLE
	return 1.0;
#else
	return g_shadowMaps[map].SampleCmpLevelZero( at, compare );
#endif
}

//Sun visibility (1 lit) from still-world pages and movers map; step off along the geometric normal
#ifdef PORTABLE
//The portable frame's sun: one map, the device's own, bound where the full frame binds nothing
//- see CorePortableSun, which draws it and writes what this reads into the words the pages use.
[[vk::binding(2,1)]] Sampler2DShadow g_sunShadow;

//The portable frame's SunShadow: the same step off the surface and one tap held off the receiver's plane,
//over one square instead of levels - see ShadowSharp
float PortableSunShadow( float3 world, float3 normal, float3 step )
{
	float side = g_frame[0].sunPages.x;
	float texels = g_frame[0].sunPages.y;
	float zScale = g_frame[0].sunPages.w;

	if ( side <= 0.0 || texels <= 0.0 ) return 1.0;

	float texelWorld = side / texels;

	float4 at = mul( float4( world + ( step * ( texelWorld * 1.5 ) ), 1.0 ), g_frame[0].sunToPages );
	float2 uv = ( at.xy * 0.5 ) + 0.5;

	//GL stores pictures bottom row first, so the top of the light's view is the bottom of the map
#ifdef WEBGL
	uv.y = 1.0 - uv.y;
#endif

	float edge = 2.0 / texels;
	if ( any( uv < edge ) || any( uv > 1.0 - edge ) ) return 1.0;

	//The receiver's plane as the pages have it: across and up span two over the side, along by depth scale
	float3 facing = mul( float4( normal, 0.0 ), g_frame[0].sunToPages ).xyz;
	float3 metres = float3( facing.xy * ( side * 0.5 ), facing.z / max( zScale, 1e-8 ) );
	float2 tilt = metres.xy / max( -metres.z, 0.1 );

	float perTexel = texelWorld * zScale;
	float2 slope = tilt * perTexel;

#ifdef WEBGL
	slope.y = -slope.y;
#endif

	float compare = at.z - ( ShadowBiasTexels * perTexel ) - ( 0.5 * ( abs( slope.x ) + abs( slope.y ) ) );

	if ( g_frame[0].shadow.x < 0.5 ) return g_sunShadow.SampleCmpLevelZero( uv, compare );

	//Four filtered compares half a texel out each way: a soft edge two texels wide for four reads
	float2 nudge = float2( 0.5, 0.5 ) / texels;
	float lit = g_sunShadow.SampleCmpLevelZero( uv + float2( -nudge.x, -nudge.y ), compare - ( 0.5 * ( slope.x + slope.y ) ) );
	lit += g_sunShadow.SampleCmpLevelZero( uv + float2( nudge.x, -nudge.y ), compare + ( 0.5 * ( slope.x - slope.y ) ) );
	lit += g_sunShadow.SampleCmpLevelZero( uv + float2( -nudge.x, nudge.y ), compare + ( 0.5 * ( slope.y - slope.x ) ) );
	lit += g_sunShadow.SampleCmpLevelZero( uv + float2( nudge.x, nudge.y ), compare + ( 0.5 * ( slope.x + slope.y ) ) );

	return lit * 0.25;
}
#endif

//The sun's angular radius as a tangent: an edge spreads this much per metre between receiver and caster
static const float SunSourceTan = 0.004651;

//The sun's edge as wide as the sun makes it: sharp where a receiver stands on its caster, five millimetres wider
//each side for every metre it stands behind it - see SunSourceTan, ShadowEdge, ShadowBlocker and ShadowFilter.
float SunSoft( uint map, uint depthMap, float3 tap, float compare, float texel, float2 slope, float texelWorld, float zScale )
{
	float quality = g_frame[0].shadow.x;
	if ( depthMap == 0u || quality < 0.5 ) return ShadowSharp( map, tap, compare, slope );

	float lit;
	if ( !ShadowEdge( map, tap, compare, texel, slope, quality < 1.5 ? 1.0 : ShadowSoftestTexels, lit ) ) return lit;

	//A fixed edge a texel each side, without the blocker search
	if ( quality < 1.5 ) return ShadowFilter( map, tap, compare, texel, slope, 1.0 );

	float blocker = ShadowBlocker( depthMap, tap, compare, texel, slope );
	float behind = blocker < 0.0 ? 0.0 : ( compare - blocker ) / max( zScale, 1e-8 );
	float soft = clamp( behind * SunSourceTan / texelWorld, 0.0, ShadowSoftestTexels );

	return ShadowFilter( map, tap, compare, texel, slope, soft );
}

//One level of the sun's pages about a point - SunShadow's read.
float SunLevel( uint level, float3 world, float3 step, float2 tilt, bool wide, float page0, float zFrom, float zScale )
{
	float page = page0 * exp2( float( level ) );
	float texelWorld = page / SunPageTexels;

	float3 lifted = mul( float4( world + ( step * ( texelWorld * SunLiftTexels ) ), 1.0 ), g_frame[0].sunToPages ).xyz;

	//The level's span divided out; the sampler repeats, as a level is a torus with tiles in slots modulo the grid
	float2 uv = lifted.xy / ( page * SunPageGrid );

	//A texel of this level, into its depth - the bias and the plane's step both.
	float perTexel = texelWorld * zScale;

	float compare = ( ( lifted.z - zFrom ) * zScale ) - ( ShadowBiasTexels * perTexel );
	float3 tap = float3( uv, float( level ) );

	return wide
		? SunSoft( uint( g_frame[0].sunMaps.x ), uint( g_frame[0].sunDepth.x ), tap, compare,
			1.0 / ( SunPageTexels * SunPageGrid ), tilt * perTexel, texelWorld, zScale )
		: ShadowTap( uint( g_frame[0].sunMaps.x ), tap, compare );
}

//The movers map's part of the sun's shadow (1 lit), where it was drawn this frame and the point is in its square
float SunMovers( float3 world, float3 step, float2 tilt, bool wide, float zFrom, float zScale )
{
	float side = g_frame[0].moverZone.z;
	if ( kMoversOn == 0 || side <= 0.0 ) return 1.0;

	float texelWorld = side / MoverTexels;
	float3 lifted = mul( float4( world + ( step * ( texelWorld * SunLiftTexels ) ), 1.0 ), g_frame[0].sunToPages ).xyz;
	float2 uv = ( lifted.xy - g_frame[0].moverZone.xy ) / side;
	float edge = 4.0 / MoverTexels;

	if ( any( uv <= edge ) || any( uv >= 1.0 - edge ) ) return 1.0;

	float perTexel = texelWorld * zScale;
	float compare = ( ( lifted.z - zFrom ) * zScale ) - ( ShadowBiasTexels * perTexel );
	float3 tap = float3( uv, 0.0 );

	return wide
		? SunSoft( uint( g_frame[0].sunMaps.y ), uint( g_frame[0].sunDepth.y ), tap, compare, 1.0 / MoverTexels, tilt * perTexel, texelWorld, zScale )
		: ShadowTap( uint( g_frame[0].sunMaps.y ), tap, compare );
}

//The still world's part of the sun's shadow (1 lit), from its pages
float SunStill( float3 world, float3 step, float2 tilt, bool wide )
{
	float3 at = mul( float4( world, 1.0 ), g_frame[0].sunToPages ).xyz;
	float2 fromEye = abs( at.xy - g_frame[0].sunEye.xy );
	float away = max( fromEye.x, fromEye.y );

	float page0 = g_frame[0].sunPages.x;
	uint levels = uint( g_frame[0].sunPages.y );
	uint whole = uint( g_frame[0].sunEye.z );
	float reach0 = page0 * SunReachPages;

	//The finest level that reaches this far from the eye, and of those the first every page of which has been baked.
	uint level = away <= reach0 ? 0u : uint( ceil( log2( away / reach0 ) ) );
	while ( level < levels && ( whole & ( 1u << level ) ) == 0u ) level++;

	return level < levels ? SunLevel( level, world, step, tilt, wide, page0, g_frame[0].sunPages.z, g_frame[0].sunPages.w ) : 1.0;
}

//The receiver's plane down the light: how much deeper it lies a metre across the maps and a metre up them -
//see ShadowSharp. Held off the grazing edge, where it runs to infinity and the light is all but gone anyway.
float2 SunTilt( float3 normal )
{
	float3 facing = mul( float4( normal, 0.0 ), g_frame[0].sunToPages ).xyz;
	return facing.xy / max( -facing.z, 0.1 );
}

float SunShadow( float3 world, float3 normal, float3 step, bool wide = true )
{
	if ( !SunCasts() ) return 1.0;

#if defined( PORTABLE ) && defined( DEPTH_ONLY )
	//A stage drawing into the map may not read it: the frame's set holds the map it is drawing into.
	return 1.0;
#elif defined( PORTABLE )
	return PortableSunShadow( world, normal, step );
#endif

	float2 tilt = SunTilt( normal );

	return min( SunStill( world, step, tilt, wide ), SunMovers( world, step, tilt, wide, g_frame[0].sunPages.z, g_frame[0].sunPages.w ) );
}

//The same, stepped off along the normal - what every surface wants.
float SunShadow( float3 world, float3 normal )
{
	return SunShadow( world, normal, normal );
}

//How much of a baked sun movers take from a surface that has it in its lightmap, in `moved`: where the still world
//lets it through and a mover does not. Returns what reaches the point past both, from one tap of the still pages -
//the highlight's shadow, which a lightmap has no direction for
float SunBakedShade( float3 world, float3 normal, out float moved )
{
	moved = 0.0;

#ifdef PORTABLE
	return 0.0;
#else
	if ( !SunCasts() ) return 1.0;

	float2 tilt = SunTilt( normal );
	float movers = SunMovers( world, normal, tilt, true, g_frame[0].sunPages.z, g_frame[0].sunPages.w );
	if ( movers < 1.0 ) moved = ( 1.0 - movers ) * SunStill( world, normal, tilt, true );

	return SunStill( world, normal, tilt, false ) * movers;
#endif
}

#ifndef PORTABLE
#include "screen_trace.slang"
#endif

//Screen-space reflection for water and glass, traced here since they draw after the SSR pass
float4 ScreenReflected( float3 world, float3 normal, float3 away )
{
#ifdef PORTABLE
	return 0.0.xxxx;
#else
	uint chain = uint( g_frame[0].screenTrace.x );
	uint scene = uint( g_frame[0].screenTrace.y );
	if ( chain == 0u || scene == 0u ) return 0.0.xxxx;

	float3 camera = EYE_FRAME( camera ).xyz;
	float lift = 0.02 + ( 0.002 * length( world - camera ) );

	//The opaque world's depth where kept, for where the ray really hit; no normals mid-pass
	uint fine = g_frame[0].viewInfo.z > 0.0 ? uint( g_frame[0].viewInfo.z ) : 0u;

	ScreenHit hit = ScreenTrace( chain, fine, 0u, world + ( normal * lift ), away,
		EYE_FRAME( worldToClip ), EYE_FRAME( clipToWorld ), camera, 64 );

	if ( !hit.found ) return 0.0.xxxx;

	return float4( g_textures[NonUniformResourceIndex( scene )].SampleLevel( hit.uv, 0.0 ).rgb, hit.fade );
#endif
}

//Sun shadow level debug view: colour per level, texel grid lines, darker in shadow
float3 SunLevelView( float3 world, float3 normal )
{
#ifdef PORTABLE
	return float3( 0.5, 0.5, 0.5 );
#else
	if ( !SunCasts() ) return float3( 0.5, 0.5, 0.5 );

	float3 at = mul( float4( world, 1.0 ), g_frame[0].sunToPages ).xyz;
	float2 fromEye = abs( at.xy - g_frame[0].sunEye.xy );
	float away = max( fromEye.x, fromEye.y );

	float page0 = g_frame[0].sunPages.x;
	uint levels = uint( g_frame[0].sunPages.y );
	uint whole = uint( g_frame[0].sunEye.z );
	float reach0 = page0 * SunReachPages;

	uint level = away <= reach0 ? 0u : uint( ceil( log2( away / reach0 ) ) );
	while ( level < levels && ( whole & ( 1u << level ) ) == 0u ) level++;

	if ( level >= levels ) return float3( 0.5, 0.5, 0.5 );

	static const float3 colours[8] =
	{
		float3( 1.0, 0.25, 0.2 ), float3( 1.0, 0.6, 0.15 ), float3( 0.95, 0.9, 0.2 ), float3( 0.3, 0.85, 0.3 ),
		float3( 0.2, 0.85, 0.9 ), float3( 0.25, 0.45, 1.0 ), float3( 0.7, 0.35, 1.0 ), float3( 0.95, 0.95, 0.95 ),
	};

	//The level's texel grid where the point lies: a line a texel, a pixel wide whatever the texel's
	//size, faded out where the lines would crowd closer than three pixels and only fill the surface in.
	float texelWorld = page0 * exp2( float( level ) ) / SunPageTexels;
	float2 grid = at.xy / texelWorld;
	float2 perPixel = fwidth( grid );
	float2 stripe = saturate( 1.0 - ( abs( frac( grid + 0.5 ) - 0.5 ) / max( perPixel, 1e-5 ) ) );
	//Each axis on its own: a floor seen low has texels wide across and thin deep, and the cross lines still count
	float2 fade = saturate( 1.0 - ( perPixel * 3.0 ) );
	float lines = max( stripe.x * fade.x, stripe.y * fade.y );

	float lit = SunShadow( world, normal );

	return colours[min( level, 7u )] * ( 0.35 + ( 0.65 * lit ) ) * ( 1.0 - ( 0.6 * lines ) );
#endif
}

//Lamp shadow map layout constants; must match CoreLampMaps
static const float LampLayerTexels = 2048.0;
static const float LampGuardTexels = 4.0;

//A lamp read's step and bias in texels of a 512 face whatever its size, so in metres; per-texel halving
//striped a grazed floor
static const float LampSteadyTexels = 512.0;
static const float LampLiftTexels = 1.5;
static const float LampBiasTexels = 1.0;
static const float LampNear = 0.05;

//A lamp face's across and up for `forward`, as CoreSunPages.LightAxes builds them: world up, or world forward
//near it; the face was projected with these, so they must agree
void LampAxes( float3 forward, out float3 right, out float3 up )
{
	float3 along = abs( forward.y ) < 0.9 ? float3( 0.0, 1.0, 0.0 ) : float3( 0.0, 0.0, -1.0 );
	right = normalize( cross( along, forward ) );
	up = normalize( cross( forward, right ) );
}

#ifndef PORTABLE
//Lamp shadow from one map, softened by the lamp's source width (light.cone.y), PCSS-style
float LampLit( uint map, uint depthMap, float3 at, float compare, float texel, float2 slope, Light light,
	float along, float far, float b, float texelWorld )
{
	float quality = g_frame[0].shadow.x;
	if ( depthMap == 0u || quality < 0.5 ) return ShadowSharp( map, at, compare, slope );

	float lit;
	if ( !ShadowEdge( map, at, compare, texel, slope, quality < 1.5 ? 1.0 : ShadowSoftestTexels, lit ) ) return lit;

	if ( quality < 1.5 ) return ShadowFilter( map, at, compare, texel, slope, 1.0 );

	//What shades it, back out of the depth the face holds into metres down it.
	float blocker = ShadowBlocker( depthMap, at, compare, texel, slope );
	float blockerAlong = blocker < 0.0 ? along : b / max( ( far / ( far - LampNear ) ) - blocker, 1e-6 );
	float soft = max( light.cone.y, 0.0 ) * ( along - blockerAlong ) / max( blockerAlong, LampNear ) / texelWorld;

	return ShadowFilter( map, at, compare, texel, slope, clamp( soft, 0.0, ShadowSoftestTexels ) );
}
#endif

//Where a point falls in a lamp's maps: the face, the tap, its depth to compare and how the receiver slopes
struct LampTap
{
	uint face;
	float3 at;
	float compare;
	float2 slope;
	float along;
	float far;
	float b;
	float texelWorld;
};

#ifndef PORTABLE
//The face chosen for the stepped lookup point; false where the lamp has no faces or the point is outside them
bool LampTapOf( float3 world, float3 normal, float3 step, Light light, float distance, out LampTap t )
{
	t = (LampTap)0;

	float first = light.cone.z;
	if ( first < 0.0 || uint( g_frame[0].sunMaps.w ) == 0u ) return false;

	bool spot = light.colour.w > 1.5;

	//A face's width a metre out: one for ninety degrees, a spot's half-angle tangent, clamped as CoreLampMaps does
	float outer = clamp( light.cone.x, 0.17364818, 0.9998 );
	float halfTan = spot ? sqrt( 1.0 - ( outer * outer ) ) / outer : 1.0;
	uint sizeClass = uint( light.cone.w );
	float size = LampLayerTexels / float( 1u << sizeClass );
	float fit = ( size - ( 2.0 * LampGuardTexels ) ) / size;

	t.texelWorld = 2.0 * max( distance, LampNear ) * halfTan / ( size * fit );
	float steadyWorld = t.texelWorld * size / LampSteadyTexels;
	float3 d = world + ( step * ( steadyWorld * LampLiftTexels ) ) - light.position.xyz;

	float3 forward = light.direction.xyz;
	t.face = 0u;

	if ( !spot )
	{
		//Down the largest of the three axes, in the order the faces are laid.
		float3 extent = abs( d );

		if ( extent.x >= extent.y && extent.x >= extent.z ) { forward = float3( sign( d.x ), 0.0, 0.0 ); t.face = d.x > 0.0 ? 0u : 1u; }
		else if ( extent.y >= extent.z ) { forward = float3( 0.0, sign( d.y ), 0.0 ); t.face = d.y > 0.0 ? 2u : 3u; }
		else { forward = float3( 0.0, 0.0, sign( d.z ) ); t.face = d.z > 0.0 ? 4u : 5u; }
	}

	t.along = dot( d, forward );
	if ( t.along <= LampNear ) return false;

	float3 right;
	float3 up;
	LampAxes( forward, right, up );

	float2 uv = ( float2( dot( d, right ), dot( d, up ) ) * ( fit / ( halfTan * t.along ) ) * 0.5 ) + 0.5;

	//The face's place in the map: the lamp's first square plus the face, counted layer by layer in squares of its size
	uint perRow = 1u << sizeClass;
	uint square = uint( first ) + t.face;
	uint within = square % ( perRow * perRow );
	float2 corner = float2( float( within % perRow ), float( within / perRow ) );
	t.at = float3( ( corner + uv ) / float( perRow ), float( square / ( perRow * perRow ) ) );

	//Depth from LampNear to the lamp's reach as projected, and the bias, LampBiasTexels in metres, through
	//that projection's derivative, as a depth step differs at every range
	t.far = max( light.position.w, LampNear + 0.01 );
	t.b = LampNear * t.far / ( t.far - LampNear );
	float depth = ( t.far / ( t.far - LampNear ) ) - ( t.b / t.along );
	if ( depth >= 1.0 ) return false;

	float perMetre = t.b / ( t.along * t.along );
	float bias = LampBiasTexels * steadyWorld * perMetre;

	//Receiver plane slope on the face, constant across it in perspective depth; clamped at grazing
	float facing = max( -dot( normal, d ), 0.1 * length( d ) );
	t.slope = float2( dot( normal, right ), dot( normal, up ) ) * ( t.b / facing ) * ( 2.0 * halfTan / ( fit * size ) );

	t.compare = depth - bias;
	return true;
}

//What the still world lets through of a lamp at a tap
float LampStill( LampTap t, Light light )
{
	return LampLit( uint( g_frame[0].sunMaps.w ), uint( g_frame[0].sunMaps.z ), t.at, t.compare, 1.0 / LampLayerTexels, t.slope,
		light, t.along, t.far, t.b, t.texelWorld );
}

//What the movers let through: one where the tap's face has no movers map this frame - see CoreLampMaps
float LampMovers( LampTap t, Light light )
{
	uint moversMap = uint( g_frame[0].sunDepth.z );
	if ( ( uint( light.shape.y ) & ( 1u << t.face ) ) == 0u || moversMap == 0u ) return 1.0;

	return LampLit( moversMap, uint( g_frame[0].sunDepth.w ), t.at, t.compare, 1.0 / LampLayerTexels, t.slope,
		light, t.along, t.far, t.b, t.texelWorld );
}
#endif

//Lamp visibility (1 lit) from its cube faces; face chosen for the stepped lookup point
float LampShadow( float3 world, float3 normal, float3 step, Light light, float distance )
{
#ifdef PORTABLE
	return 1.0;
#else
	LampTap t;
	if ( !LampTapOf( world, normal, step, light, distance, t ) ) return 1.0;

	float lit = LampStill( t, light );
	return lit > 0.0 ? lit * LampMovers( t, light ) : lit;
#endif
}

//How much of a baked lamp movers take from a surface that has it in its lightmap, in `moved`; returns what reaches
//the point past the still world and the movers, from one tap - the highlight's shadow. See SunBakedShade
float LampBakedShade( float3 world, float3 normal, Light light, float distance, out float moved )
{
	moved = 0.0;

#ifdef PORTABLE
	return 0.0;
#else
	LampTap t;
	if ( !LampTapOf( world, normal, normal, light, distance, t ) ) return 1.0;

	float movers = LampMovers( t, light );
	if ( movers < 1.0 ) moved = ( 1.0 - movers ) * LampStill( t, light );

	return ShadowSharp( uint( g_frame[0].sunMaps.w ), t.at, t.compare, t.slope ) * movers;
#endif
}

//The same, stepped off along the normal - what every surface wants.
float LampShadow( float3 world, float3 normal, Light light, float distance )
{
	return LampShadow( world, normal, normal, light, distance );
}

//One number for how much light a colour is, for weighing lights against each other.
float LightStrength( float3 colour )
{
	return dot( colour, float3( 0.2126, 0.7152, 0.0722 ) );
}

//Spec constant: shade reflection probes; folding it out saves registers. Always 0 on portable
#ifdef PORTABLE
static const int kProbesOn = 0;
#elif defined( SPECIALIZED )
[vk::constant_id( 0 )] const int kProbesOn = 1;
#else
static const int kProbesOn = 1;
#endif

//Spec constant: debug views; off merges diffuse and specular accumulators to save registers
#ifdef SPECIALIZED
[vk::constant_id( 1 )] const int kDebugOn = 1;
#else
static const int kDebugOn = 1;
#endif

//Spec constant: frame has a light grid; off walks the whole list (always safe, only slower)
#ifdef SPECIALIZED
[vk::constant_id( 2 )] const int kGridOn = 1;
#else
static const int kGridOn = 1;
#endif

//Spec constant: frame has non-directional lights; must be correct or lamps go missing
#ifdef SPECIALIZED
[vk::constant_id( 3 )] const int kLampsOn = 1;
#else
static const int kLampsOn = 1;
#endif

//Spec constant: frame has a sky table (settings.w), so the read/fallback branch folds away
#ifdef SPECIALIZED
[vk::constant_id( 4 )] const int kSkyTableOn = 1;
#else
static const int kSkyTableOn = 1;
#endif

//Whether the frame has fog, as ApplyFog would find - see CoreSceneRenderer.SetFog
#ifdef SPECIALIZED
[vk::constant_id( 5 )] const int kFogOn = 1;
#else
static const int kFogOn = 1;
#endif

//Whether an occlusion picture was drawn (settings.x non-zero); a project setting, off by default, so folded out
#ifdef SPECIALIZED
[vk::constant_id( 6 )] const int kOcclusionOn = 1;
#else
static const int kOcclusionOn = 1;
#endif

//Spec constant: far material calls (past ClusterFarSlice) skip normal and roughness maps; 0 is full
#ifdef SPECIALIZED
[vk::constant_id( 13 )] const int kFarShading = 0;
#else
static const int kFarShading = 0;
#endif

//Spec constant: march fog volumes per pixel; 0 when the froxel sheet exists. Default 1 marches
#ifdef SPECIALIZED
[vk::constant_id( 14 )] const int kFogMarch = 1;
#else
static const int kFogMarch = 1;
#endif

//Spec constant: frame has more than one light; 0 turns the light loop into a single test
#ifdef SPECIALIZED
[vk::constant_id( 7 )] const int kManyLights = 1;
#else
static const int kManyLights = 1;
#endif

//Spec constant: sun drew a movers map this frame; off drops the second read and its registers
#ifdef SPECIALIZED
[vk::constant_id( 8 )] const int kMoversOn = 1;
#else
static const int kMoversOn = 1;
#endif

//Spec constant: frame has decals; must be correct. Always 0 on portable, which has no decals
#ifdef PORTABLE
static const int kDecalsOn = 0;
#elif defined( SPECIALIZED )
[vk::constant_id( 9 )] const int kDecalsOn = 1;
#else
static const int kDecalsOn = 1;
#endif

//Whether there is baked lighting (HasBakedSheet); an unbaked scene folds out the sheet and volume reads
#ifdef SPECIALIZED
[vk::constant_id( 10 )] const int kBakedOn = 1;
#else
static const int kBakedOn = 1;
#endif

//Sky spec constants: fold out moon/stars (and the sky's own cloud) when unused, to cut registers

//Whether the sky is a panorama (panorama.w); nought folds the portable frame's panorama table layout out
#ifdef SPECIALIZED
[vk::constant_id( 18 )] const int kPanoramaOn = 1;
#else
static const int kPanoramaOn = 1;
#endif

//Whether a lamp has a shape (AreaShape); nought folds the LTC polygon and its registers out of the light loop
#ifdef PORTABLE
static const int kAreaOn = 0;
#elif defined( SPECIALIZED )
[vk::constant_id( 19 )] const int kAreaOn = 1;
#else
static const int kAreaOn = 1;
#endif

//Whether the frame can have the probe grid of a scene with no bake (rays on); nought folds GridLight and its registers
//out of every surface - a third of them, where it was compiled in and never read
#ifdef PORTABLE
static const int kProbeGridOn = 0;
#elif defined( SPECIALIZED )
[vk::constant_id( 20 )] const int kProbeGridOn = 1;
#else
static const int kProbeGridOn = 1;
#endif

//Whether there is any night (NightFactor above nought); nought folds the moon and stars out of the sky
#ifdef SPECIALIZED
[vk::constant_id( 11 )] const int kNightOn = 1;
#else
static const int kNightOn = 1;
#endif

//Whether there is any day (NightFactor below one); nought folds the sun's disc and glare out of the sky
#ifdef SPECIALIZED
[vk::constant_id( 12 )] const int kSunDiscOn = 1;
#else
static const int kSunDiscOn = 1;
#endif

//Constant ids 15+ belong to shaders' own Fold( N ) params (e.g. sky cloud); keep frame ids below

//The first two probe boxes a point is in, with edge fades, layered first over second over sky
struct ProbeMix
{
	int first;
	float firstWeight;
	int second;
	float secondWeight;
};

ProbeMix ProbesAt( float3 world )
{
	ProbeMix mix;
	mix.first = -1;
	mix.firstWeight = 0.0;
	mix.second = -1;
	mix.secondWeight = 0.0;

	int count = int( g_frame[0].probeInfo.x );

	for ( int i = 0; i < count && i < 4; ++i )
	{
		Probe probe = g_frame[0].probes[i];

		float3 mins = probe.mins.xyz;
		float3 maxs = probe.maxs.xyz;

		//Whole inside the box and fading over the feather past it, as the gizmo draws it (Bounds.Grow): a box that fades
		//inwards gives a floor lying on its face the sky's reflection, which a room does not see
		float fade = max( probe.maxs.w, 0.0001 );
		float3 outside = max( max( mins - world, world - maxs ), 0.0 );
		float weight = saturate( 1.0 - ( max( outside.x, max( outside.y, outside.z ) ) / fade ) );

		//A probe without a picture (not captured, or failed) is no probe, not a dark room
		if ( weight <= 0.0 || probe.where.w < 0.5 ) continue;

		if ( mix.first < 0 )
		{
			mix.first = i;
			mix.firstWeight = weight;

			//Wholly inside the first: nothing under it shows.
			if ( weight >= 1.0 ) break;
		}
		else
		{
			mix.second = i;
			mix.secondWeight = weight;
			break;
		}
	}

	return mix;
}

float4 EncodeRgbm( float3 hdr, float range )
{
	float3 c = max( hdr, 0.0 ) / max( range, 0.001 );
	float m = clamp( max( max( c.r, c.g ), c.b ), 1.0 / 255.0, 1.0 );
	m = ceil( m * 255.0 ) / 255.0;
	return float4( saturate( c / m ), m );
}

float3 DecodeRgbm( float4 rgbm, float range )
{
	return rgbm.rgb * rgbm.a * max( range, 0.001 );
}

//Gradient fog amount: distance falloff times height falloff (dense below the start height)
float FogAmount( float3 world, float3 eye )
{
	if ( g_frame[0].fogShape.z < 0.5 ) return 0.0;

	float2 span = g_frame[0].fogRange.xy;
	float away = saturate( ( length( world - eye ) - span.x ) / max( span.y - span.x, 0.0001 ) );

	float2 rise = g_frame[0].fogRange.zw;
	float up = saturate( ( world.y - rise.x ) / max( rise.y - rise.x, 0.0001 ) );

	float byDistance = pow( away, g_frame[0].fogShape.x );
	float byHeight = pow( 1.0 - up, g_frame[0].fogShape.y );

	return saturate( byDistance * byHeight * g_frame[0].fogColour.w );
}

//A vector turned by a quaternion's inverse - into the frame the quaternion turns out of.
float3 TurnInto( float4 q, float3 v )
{
	float3 axis = -q.xyz;
	return v + 2.0 * cross( axis, cross( axis, v ) + q.w * v );
}

//Ray march steps per fog volume; 3 on portable, which marches every volume per pixel
#ifdef PORTABLE
static const int kFogVolumeSteps = 3;
#else
static const int kFogVolumeSteps = 8;
#endif

//Extinction per metre for a volume of strength one
static const float kFogVolumeDensity = 0.25;

//Henyey-Greenstein phase, forward-leaning; averages to 1/(4 pi) over the sphere
float FogPhase( float cosine )
{
	float g = 0.45;
	return ( 1.0 - g * g ) / ( 4.0 * 3.14159265 * pow( 1.0 + g * g - 2.0 * g * cosine, 1.5 ) );
}

//Lamp light in fog via the light grid, with falloff and cone but no shadow
float3 FogLampLight( float3 here, float3 view, float2 screen )
{
	float viewDepth = mul( float4( here, 1.0 ), EYE_FRAME( worldToClip ) ).w;

	LightWalk walk = LightsHere( screen, viewDepth );
	int count = int( walk.count );
	if ( kManyLights == 0 ) count = min( count, 1 );

	float3 arriving = 0.0;

	for ( int index = 0; index < count; ++index )
	{
		uint which = LightHere( walk, uint( index ) );
		Light light = LightAt( which );

		//The sun is taken by VolumeFog itself, with its shadow.
		if ( light.colour.w < 0.5 ) continue;

		LightLamp( which, light );

		float3 toLight = light.position.xyz - here;
		float distance = length( toLight );
		if ( distance > light.position.w ) continue;

		float3 l = toLight / max( distance, 0.0001 );

		//As the lamp loop in surface_shading.slang: inverse square, windowed to nothing at the range
		float reach = distance / max( light.position.w, 0.0001 );
		float falloff = saturate( 1.0 - reach * reach * reach * reach );
		float square = max( distance * distance, 0.0001 );
		float spread = light.shape.x == 1.0 ? square : pow( square, light.shape.x );
		float attenuation = falloff * falloff / max( spread, 0.0001 );

		if ( light.colour.w > 1.5 )
		{
			float aligned = dot( -l, light.direction.xyz );
			attenuation *= saturate( ( aligned - light.cone.x ) / max( light.direction.w - light.cone.x, 0.0001 ) );
		}

		if ( attenuation <= 0.0 ) continue;

		arriving += light.colour.rgb * attenuation * FogPhase( dot( view, l ) );
	}

	return arriving;
}

//Fog volume air from the froxel sheet (two slice taps, lerped); false if no sheet, so caller marches
bool FogVolumeSheet( float3 world, float2 screen, out float3 scattered, out float through )
{
	scattered = float3( 0.0, 0.0, 0.0 );
	through = 1.0;

#ifdef PORTABLE
	//The portable frame draws no sheet: CoreVolumeFog is compute on a storage picture, with no bindless table
	//here to read it; the caller marches instead
	return false;
#else
	uint sheet = uint( g_frame[0].fogVolumeSheet.x );
	float slices = g_frame[0].fogVolumeSheet.y;
	float near = g_frame[0].fogVolumeSheet.z;
	float far = g_frame[0].fogVolumeSheet.w;
	float columns = max( g_frame[0].fogVolumeInfo.y, 1.0 );

	if ( sheet == 0 || slices < 1.0 || Eye() != 0 ) return false;

	//View depth, the clip position's w, which the slices are laid along
	float depth = mul( float4( world, 1.0 ), g_frame[0].worldToClip ).w;

	if ( depth <= near )
	{
		//Nearer than the first slice: nothing has been scattered yet.
		return true;
	}

	//Which slice, as the pass laid them out: geometric from near to far.
	float at = slices * ( log( min( depth, far ) / near ) / log( far / near ) );

	//A texel holds the integral to the END of its slice, so the value at `at` sits between texels at - 1 and at.
	float index = clamp( at - 1.0, 0.0, slices - 1.0 );
	float low = floor( index );
	float mix = index - low;
	float high = min( low + 1.0, slices - 1.0 );

	float2 uv = saturate( screen / float2( max( g_frame[0].settings.y, 1.0 ), max( g_frame[0].settings.z, 1.0 ) ) );

	//The slice's tile: laid across then down, as one column would be taller than a device allows - see
	//CoreVolumeFog.Columns and the writer, which lay them out the same way
	float rows = ceil( slices / columns );

	//Inside its own tile, so the filter cannot reach into the slice beside it: a sheet of
	//tiles is not a volume texture, and the texels along a tile's edge belong to somebody else.
	float2 froxels = max( g_frame[0].fogVolumeInfo.zw, float2( 1.0, 1.0 ) );
	float2 inset = clamp( uv, 0.5 / max( froxels, 1.0 ), 1.0 - ( 0.5 / max( froxels, 1.0 ) ) );

	float2 tileA = float2( fmod( low, columns ), floor( low / columns ) );
	float2 tileB = float2( fmod( high, columns ), floor( high / columns ) );

	float2 across = float2( columns, rows );

	//One sheet for the frame - see SkyTableAt for why that means no NonUniformResourceIndex.
	float4 a = g_textures[sheet].SampleLevel( ( tileA + inset ) / across, 0.0 );
	float4 b = g_textures[sheet].SampleLevel( ( tileB + inset ) / across, 0.0 );

	float4 held = lerp( a, b, mix );

	scattered = held.rgb;
	through = held.a;

	return true;
#endif
}

float3 VolumeFog( float3 lit, float3 world, float3 eye, float2 screen )
{
	int count = (int)g_frame[0].fogVolumeInfo.x;
	if ( count <= 0 ) return lit;

	//The sheet where drawn: one read instead of eight steps and eight sun taps, then the same arithmetic
	{
		float3 scattered;
		float through;

		if ( FogVolumeSheet( world, screen, scattered, through ) ) return lit * through + scattered;
	}

#ifndef STEREO
	//The frame drew the sheet, so the only way here is a pixel the sheet had no answer for - which the pipeline was
	//baked knowing never happens. Not in a two-eye build: the second eye reads no sheet and marches. See kFogMarch.
	if ( kFogMarch == 0 ) return lit;
#endif

	float3 ray = world - eye;
	float rayLength = length( ray );
	if ( rayLength < 0.001 ) return lit;

	float3 view = ray / rayLength;

	bool sun = HasSun();
	float3 sunDirection = g_frame[0].sunDirection.xyz;
	float3 sunLight = g_frame[0].sunLight.rgb;

	float phase = sun ? FogPhase( dot( view, sunDirection ) ) : 0.0;

	float3 ambient = g_frame[0].ambient.rgb;

	for ( int v = 0; v < count && v < 8; ++v )
	{
		FogVolume volume = g_frame[0].fogVolumes[v];

		float3 extents = volume.extents.xyz;
		float3 origin = TurnInto( volume.rotation, eye - volume.centre.xyz ) / extents;
		float3 direction = TurnInto( volume.rotation, ray ) / extents;

		//The slab test: where the ray is inside the unit box, as fractions of the way from the eye to the pixel.
		float3 safe = max( abs( direction ), 1e-6 ) * sign( direction + 1e-7 );
		float3 near = ( -1.0 - origin ) / safe;
		float3 far = ( 1.0 - origin ) / safe;
		float3 enter = min( near, far );
		float3 leave = max( near, far );
		float t0 = max( max( enter.x, enter.y ), max( enter.z, 0.0 ) );
		float t1 = min( min( leave.x, leave.y ), min( leave.z, 1.0 ) );
		if ( t1 <= t0 ) continue;

		float strength = volume.centre.w;
		float fade = volume.extents.w;
		float3 scatterColour = volume.colour.rgb;

		float step = ( t1 - t0 ) / kFogVolumeSteps;
		float metres = step * rayLength;

		float through = 1.0;
		float3 scattered = 0.0;

		for ( int i = 0; i < kFogVolumeSteps; ++i )
		{
			float t = t0 + ( i + 0.5 ) * step;
			float3 local = origin + direction * t;

			//One at the centre, nought at the faces, to the fade's power: nought a hard box, one a straight fade
			float edge = saturate( 1.0 - max( abs( local.x ), max( abs( local.y ), abs( local.z ) ) ) );
			float density = strength * kFogVolumeDensity * pow( edge, fade );

			float3 here = eye + ray * t;
			float3 light = ambient + FogLampLight( here, view, screen );

			//One sun shadow tap per march step; a point in air needs no plane or soft edge
			if ( sun )
				light += sunLight * phase * SunShadow( here, sunDirection, sunDirection, false );

			float taken = 1.0 - exp( -density * metres );
			scattered += through * taken * light * scatterColour;
			through *= 1.0 - taken;
		}

		lit = lit * through + scattered;
	}

	return lit;
}

//Apply fog volumes, then gradient fog (optionally tinted by the sky behind the pixel)
float3 ApplyFog( float3 lit, float3 world, float3 eye, float2 screen )
{
	if ( kFogOn == 0 ) return lit;

	lit = VolumeFog( lit, world, eye, screen );

	float amount = FogAmount( world, eye );
	if ( amount <= 0.0 ) return lit;

	float3 colour = g_frame[0].fogColour.rgb;

	if ( g_frame[0].fogShape.w > 0.5 ) colour *= SkyDrawn( normalize( world - eye ) );

	return lerp( lit, colour, amount );
}

//Whether this frame has a sheet of baked light for the surfaces that stand still - see bakedMaps in frame_data.slang.
bool HasBakedSheet()
{
#ifdef PORTABLE
	//Its own picture, bound as the frame's - see PortableSheet.
	return g_frame[0].portableBake.x > 0.5;
#else
	return kBakedOn != 0 && g_frame[0].bakedMaps.w > 0.5;
#endif
}

//And whether it has the volume, for everything else.
bool HasBakedVolume()
{
#ifdef PORTABLE
	return false;
#else
	return kBakedOn != 0 && g_frame[0].bakedVolume.w > 0.5;
#endif
}

#ifndef PORTABLE

//The sixteen sheet texels about a spot, each one's B-spline weight kept only where it lies on the same face as the
//spot's own texel by the light sheet's alpha (BakeSheet.FaceOf) - smooth within a face, nothing from the one beside -
//and the light sheet read with them. Worked out once a pixel: every other sheet there is read with the same weights
struct SheetSpot
{
	float2 uv;
	float2 cell;
	float2 size;

	//The weights a row of four at a time, the row nearest nought first
	float4 weights[4];
	float total;

	float4 light;
};

SheetSpot SheetSpotAt( uint sheet, float2 uv )
{
	SheetSpot spot;
	spot.uv = uv;
	g_textures[sheet].GetDimensions( spot.size.x, spot.size.y );

	float2 at = ( uv * spot.size ) - 0.5;
	spot.cell = floor( at );
	float2 f = at - spot.cell;

	float2 f2 = f * f;
	float2 f3 = f2 * f;
	float2 w[4] = { ( -f3 + ( 3.0 * f2 ) - ( 3.0 * f ) + 1.0 ) / 6.0, ( ( 3.0 * f3 ) - ( 6.0 * f2 ) + 4.0 ) / 6.0,
		( ( -3.0 * f3 ) + ( 3.0 * f2 ) + ( 3.0 * f ) + 1.0 ) / 6.0, f3 / 6.0 };

	float own = g_textures[sheet].SampleLevel( ( floor( uv * spot.size ) + 0.5 ) / spot.size, 0.0 ).a;

	float4 sum = 0.0;
	spot.total = 0.0;

	[unroll]
	for ( int j = 0; j < 4; ++j )
	{
		[unroll]
		for ( int i = 0; i < 4; ++i )
		{
			float4 texel = g_textures[sheet].SampleLevel( ( spot.cell + float2( i - 1, j - 1 ) + 0.5 ) / spot.size, 0.0 );
			float weight = abs( texel.a - own ) < ( 0.5 / 64.0 ) ? w[i].x * w[j].y : 0.0;

			spot.weights[j][i] = weight;
			sum += texel * weight;
			spot.total += weight;
		}
	}

	spot.light = spot.total > 1e-5 ? sum / spot.total : g_textures[sheet].SampleLevel( uv, 0.0 );
	return spot;
}

//Another sheet of the same size at a spot, read with its weights
float4 SheetRead( SheetSpot spot, uint picture )
{
	if ( spot.total <= 1e-5 ) return g_textures[picture].SampleLevel( spot.uv, 0.0 );

	float4 sum = 0.0;

	[unroll]
	for ( int j = 0; j < 4; ++j )
	{
		[unroll]
		for ( int i = 0; i < 4; ++i )
			sum += g_textures[picture].SampleLevel( ( spot.cell + float2( i - 1, j - 1 ) + 0.5 ) / spot.size, 0.0 ) * spot.weights[j][i];
	}

	return sum / spot.total;
}

//A sheet picture read bilinear over the four lightmap texels round a spot, each kept only where it lies on the pixel's
//own face (as SheetSpotAt, at a quarter of its taps) - for what is smooth already, as the live bounce's sheet
float3 SheetFacesLinear( uint picture, uint faces, float2 uv )
{
	float2 size;
	g_textures[faces].GetDimensions( size.x, size.y );

	float2 at = ( uv * size ) - 0.5;
	float2 cell = floor( at );
	float2 f = at - cell;

	float own = g_textures[faces].SampleLevel( ( floor( uv * size ) + 0.5 ) / size, 0.0 ).a;

	float3 sum = 0.0;
	float total = 0.0;

	[unroll]
	for ( uint k = 0u; k < 4u; ++k )
	{
		float2 corner = float2( float( k & 1u ), float( k >> 1u ) );
		float2 texel = ( cell + corner + 0.5 ) / size;
		float2 w2 = lerp( 1.0 - f, f, corner );
		float weight = w2.x * w2.y * ( abs( g_textures[faces].SampleLevel( texel, 0.0 ).a - own ) < ( 0.5 / 64.0 ) ? 1.0 : 0.0 );

		sum += g_textures[picture].SampleLevel( texel, 0.0 ).rgb * weight;
		total += weight;
	}

	return total > 1e-5 ? sum / total : g_textures[picture].SampleLevel( uv, 0.0 ).rgb;
}

//Baked lightmap diffuse at a normal: the light as the face took it, leaning by colour into where the normal turns
//from the face by the first band the bake kept (L1 lightmaps)
float3 BakedSheet( SheetSpot spot, float3 normal, float3 face )
{
	float2 uv = spot.uv;
	float3 light = spot.light.rgb;

	uint band = uint( g_frame[0].bakedMore.w );
	if ( band == 0u ) return light;

	//Three runs, red, green, blue, each the band over its colour's light as a byte - see BakeSheet.EncodeBand
	float3 red = ( g_textures[band].SampleLevel( float2( uv.x / 3.0, uv.y ), 0.0 ).xyz - 0.5 ) * 4.0;
	float3 green = ( g_textures[band].SampleLevel( float2( ( uv.x + 1.0 ) / 3.0, uv.y ), 0.0 ).xyz - 0.5 ) * 4.0;
	float3 blue = ( g_textures[band].SampleLevel( float2( ( uv.x + 2.0 ) / 3.0, uv.y ), 0.0 ).xyz - 0.5 ) * 4.0;

	float3 turned = normal - face;
	return max( light * ( 1.0 + float3( dot( red, turned ), dot( green, turned ), dot( blue, turned ) ) ), 0.0 );
}

//One layer of the volume's three pictures at a lattice floor point: each colour's constant and lean
void BakedLayer( float2 uv, out float4 red, out float4 green, out float4 blue )
{
	//Three frame-uniform pictures - see SkyTableAt.
	red = g_textures[uint( g_frame[0].bakedVolume.x )].SampleLevel( uv, 0.0 );
	green = g_textures[uint( g_frame[0].bakedVolume.y )].SampleLevel( uv, 0.0 );
	blue = g_textures[uint( g_frame[0].bakedVolume.z )].SampleLevel( uv, 0.0 );
}

//Four corners' weights at one x end of a cell: trilinear, times how far past their walls the point is not, times
//how far in front of the surface they stand as DDGI weighs probes; d is the point's distance from each per axis
float4 BakedCorners( float dx, float sx, float4 dy, float4 dz, float4 reachX, float4 reachY, float4 reachZ, float3 normal )
{
	float4 sy = float4( -1.0, 1.0, -1.0, 1.0 );
	float4 sz = float4( -1.0, -1.0, 1.0, 1.0 );

	float4 trilinear = ( 1.0 - dx ) * ( 1.0 - dy ) * ( 1.0 - dz );

	//An eighth of a cell of give past a wall, more than a nibble's rounding
	float4 seen = saturate( ( ( reachX - dx ) * 8.0 ) + 1.0 ) * saturate( ( ( reachY - dy ) * 8.0 ) + 1.0 ) * saturate( ( ( reachZ - dz ) * 8.0 ) + 1.0 );

	float4 along = ( sx * dx * normal.x ) + ( sy * dy * normal.y ) + ( sz * dz * normal.z );
	float4 apart = sqrt( ( dx * dx ) + ( dy * dy ) + ( dz * dz ) );
	float4 facing = saturate( ( along / max( apart, 1e-4 ) * 0.5 ) + 0.5 );

	return trilinear * seen * ( ( facing * facing ) + 0.2 );
}

//A point's place in the lattice, clamped inside it
float3 BakedLatticeAt( float3 world )
{
	float3 at = ( world - g_frame[0].volumeOrigin.xyz ) * g_frame[0].volumeOrigin.w;
	return clamp( at, float3( 0.0, 0.0, 0.0 ), g_frame[0].volumeCount.xyz - 1.0 );
}

//How much each of a lattice point's cell's eight corners counts for it (BakeVolume.Walls, BakedCorners): x's near
//corners in nearSide, far in farSide, each along y then z. False where the bake has no walls
bool BakedCornerWeights( float3 at, float3 normal, out float3 cell, out float4 nearSide, out float4 farSide )
{
	float3 count = g_frame[0].volumeCount.xyz;
	cell = min( floor( at ), count - 2.0 );
	nearSide = 0.0;
	farSide = 0.0;

	//Frame-uniform - see SkyTableAt.
	uint walls = uint( g_frame[0].bakedMore.x );
	if ( walls == 0u ) return false;

	float3 f = at - cell;

	//A row of cells an axis; each texel that axis's reach of the cell's eight corners, a nibble each
	float u = ( ( cell.y * count.x ) + cell.x + 0.5 ) / ( count.x * count.y );
	float rows = count.z * 3.0;

	float4 x = round( g_textures[walls].SampleLevel( float2( u, ( cell.z + 0.5 ) / rows ), 0.0 ) * 255.0 );
	float4 y = round( g_textures[walls].SampleLevel( float2( u, ( cell.z + count.z + 0.5 ) / rows ), 0.0 ) * 255.0 );
	float4 z = round( g_textures[walls].SampleLevel( float2( u, ( cell.z + ( count.z * 2.0 ) + 0.5 ) / rows ), 0.0 ) * 255.0 );

	//High nibbles are the far x corners, low the near; the channels go along y, then z
	float4 xFar = floor( x / 16.0 );
	float4 yFar = floor( y / 16.0 );
	float4 zFar = floor( z / 16.0 );

	float4 dy = lerp( f.yyyy, 1.0 - f.yyyy, float4( 0.0, 1.0, 0.0, 1.0 ) );
	float4 dz = lerp( f.zzzz, 1.0 - f.zzzz, float4( 0.0, 0.0, 1.0, 1.0 ) );

	nearSide = BakedCorners( f.x, -1.0, dy, dz, ( x - ( xFar * 16.0 ) ) / 15.0, ( y - ( yFar * 16.0 ) ) / 15.0, ( z - ( zFar * 16.0 ) ) / 15.0, normal );
	farSide = BakedCorners( 1.0 - f.x, 1.0, dy, dz, xFar / 15.0, yFar / 15.0, zFar / 15.0, normal );

	return true;
}

//Where in the lattice a point reads: moved within its cell towards the corners it sees and that face it (BakeVolume.Walls),
//so one filtered read takes their light and not the other side of a wall's - as Unity's probe volumes warp theirs
float3 BakedVolumeSpot( float3 world, float3 normal )
{
	float3 at = BakedLatticeAt( world );

	float3 cell;
	float4 nearSide, farSide;
	if ( !BakedCornerWeights( at, normal, cell, nearSide, farSide ) ) return at;

	float4 both = nearSide + farSide;
	float total = dot( both, float4( 1.0, 1.0, 1.0, 1.0 ) );

	//No corner sees it: inside a wall, where the plain read is as good as any
	if ( total < 1e-5 ) return at;

	float3 moved = float3( dot( farSide, float4( 1.0, 1.0, 1.0, 1.0 ) ), both.y + both.w, both.z + both.w ) / total;

	return cell + moved;
}

//One of the volume's per-point pictures at a spot (BakedVolumeSpot), its layers lerped, half a texel inside each
float4 BakedVolumeAt( uint picture, float3 at )
{
	float3 count = g_frame[0].volumeCount.xyz;

	float below = floor( at.y );
	float above = min( below + 1.0, count.y - 1.0 );
	float between = at.y - below;

	float across = count.x * count.y;
	float v = ( at.z + 0.5 ) / count.z;

	float4 low = g_textures[picture].SampleLevel( float2( ( ( below * count.x ) + at.x + 0.5 ) / across, v ), 0.0 );
	float4 high = g_textures[picture].SampleLevel( float2( ( ( above * count.x ) + at.x + 0.5 ) / across, v ), 0.0 );

	return lerp( low, high, between );
}

//Whether the bake keeps the sky as visibility for the frame to light (SkyLit) - a bake before that has it in its light
bool HasBakedSky()
{
	return g_frame[0].volumeCount.w > 0.5;
}

//A sheet texel's sky: which way it is open xyz, how much of it is seen w - see BakeSheet.Sky
float4 BakedSkySheet( SheetSpot spot )
{
	//Frame-uniform - see SkyTableAt.
	return SheetRead( spot, uint( g_frame[0].bakedMaps.z ) );
}

//How much sky a surface facing normal sees at a point of the volume: its visibility's two bands - see BakeVolume.Sky
float BakedSkyVolume( float3 at, float3 normal )
{
	return saturate( dot( BakedVolumeAt( uint( g_frame[0].volumeCount.w ), at ), float4( 1.0, normal ) ) );
}

//The glow layers' light times their groups' brightness now, from their sheets at a spot. Looped rather than unrolled:
//most scenes have none, and four copies of a sixteen-tap read were registers every lightmapped pixel paid for
float3 BakedGlowSheets( SheetSpot spot )
{
	float3 sum = float3( 0.0, 0.0, 0.0 );

	[loop]
	for ( uint i = 0u; i < 4u; ++i )
	{
		//Frame-uniform - see SkyTableAt.
		float4 glow = g_frame[0].bakedGlow[i];
		if ( glow.z > 0.0 && glow.x > 0.5 ) sum += SheetRead( spot, uint( glow.x ) ).rgb * glow.z;
	}

	return sum;
}

//The same from the glow layers' volumes, at a spot of the volume
float3 BakedGlowVolumes( float3 at )
{
	float3 sum = float3( 0.0, 0.0, 0.0 );

	[loop]
	for ( uint i = 0u; i < 4u; ++i )
	{
		//Frame-uniform - see SkyTableAt.
		float4 glow = g_frame[0].bakedGlow[i];
		if ( glow.z > 0.0 && glow.y > 0.5 ) sum += BakedVolumeAt( uint( glow.y ), at ).rgb * glow.z;
	}

	return sum;
}

//Whether the frame lights the bounce of the lights left out of the bake (CoreLiveBounce)
bool HasBakedBounce()
{
	return g_frame[0].bakedMore.y > 0.5;
}

//Whether the frame has the probe grid of a scene with no bake - see CoreProbeGrid
bool HasGrid()
{
	return kProbeGridOn != 0 && g_frame[0].grid[0].x > 0.5;
}

//One probe's four texels: colour, lean, the way to walls along +x -x +y -y, and along +z -z with the inside share
void GridProbe( int2 texel, out float3 colour, out float3 lean, out float4 walls, out float3 more )
{
	//All frame-uniform - see SkyTableAt.
	colour = g_textures[uint( g_frame[0].grid[0].x )].Load( int3( texel, 0 ) ).rgb;
	lean = g_textures[uint( g_frame[0].grid[0].y )].Load( int3( texel, 0 ) ).xyz;
	walls = g_textures[uint( g_frame[0].grid[0].z )].Load( int3( texel, 0 ) );
	more = g_textures[uint( g_frame[0].grid[0].w )].Load( int3( texel, 0 ) ).xyz;
}

//The grid's light on a surface facing normal, over pi: the finest cascade's eight nearest probes, each as much as it is in
//front of the surface and sees the point past the walls it found - DDGI's weights. False past every cascade
bool GridLight( float3 world, float3 normal, float3 geometric, out float3 light )
{
	light = 0.0;

	float cascades = g_frame[0].grid[1].x;
	int3 counts = int3( g_frame[0].grid[1].yzw );

	for ( uint c = 0u; c < uint( cascades ); ++c )
	{
		float4 place = g_frame[0].grid[2u + c];
		float step = place.w;

		//Off the surface a little, so it is not shaded by the probes just behind it
		float3 at = world + ( geometric * ( step * 0.2 ) );
		float3 local = ( at / step ) - place.xyz;

		if ( any( local < 0.5 ) || any( local > float3( counts ) - 1.5 ) ) continue;

		int3 low = int3( floor( local ) );
		float3 f = local - float3( low );
		int3 first = int3( place.xyz );

		float3 sum = 0.0;
		float total = 0.0;
		float3 plain = 0.0;

		[unroll]
		for ( uint k = 0u; k < 8u; ++k )
		{
			int3 corner = low + int3( k & 1u, ( k >> 1u ) & 1u, ( k >> 2u ) & 1u );
			float3 w3 = lerp( 1.0 - f, f, float3( corner - low ) );
			float trilinear = w3.x * w3.y * w3.z;

			int3 cell = first + corner;
			int3 slot = ( ( cell % counts ) + counts ) % counts;
			int2 texel = int2( slot.x + ( counts.x * slot.y ), slot.z + ( counts.z * int( c ) ) );

			float3 colour, lean, more;
			float4 walls;
			GridProbe( texel, colour, lean, walls, more );

			float3 lit = colour * max( 1.0 + dot( lean, normal ), 0.0 );
			plain += lit * trilinear;

			//Inside something: it saw the back of a wall more than a quarter of the way round
			if ( more.z > 0.25 ) continue;

			float3 toProbe = ( float3( cell ) * step ) - at;
			float apart = max( length( toProbe ), 1e-4 );
			float3 way = toProbe / apart;

			//In front of the surface counts, behind it a little - DDGI's smooth backface weight
			float facing = ( dot( way, normal ) + 1.0 ) * 0.5;
			float weight = ( facing * facing ) + 0.2;

			//How far the probe saw towards this point, from the walls it found along the axes leaning that way
			float3 from = -way;
			float3 w = from * from;
			float reach = ( w.x * ( from.x >= 0.0 ? walls.x : walls.y ) ) + ( w.y * ( from.y >= 0.0 ? walls.z : walls.w ) )
				+ ( w.z * ( from.z >= 0.0 ? more.x : more.y ) );

			if ( apart > reach ) weight *= pow( saturate( reach / apart ), 8.0 );

			weight = max( weight, 1e-4 ) * trilinear;
			sum += lit * weight;
			total += weight;
		}

		light = total > 1e-3 ? sum / total : plain;
		return true;
	}

	return false;
}

//That bounce at a spot on a surface facing normal: its colour, times one plus its lean onto the normal
float3 BakedBounce( float3 at, float3 normal )
{
	//Both frame-uniform - see SkyTableAt.
	float3 light = BakedVolumeAt( uint( g_frame[0].bakedMore.y ), at ).rgb;
	float3 lean = BakedVolumeAt( uint( g_frame[0].bakedMore.z ), at ).xyz;

	return light * max( 1.0 + dot( lean, normal ), 0.0 );
}

//That bounce on a lightmapped surface: its cell's eight corners, trilinear, each as much as it stands in front of the
//surface - weights of the corners and the surface alone, so smooth across cells, where the per-cell walls step at their faces
float3 BakedBounceSmooth( float3 world, float3 geometric, float3 normal )
{
	float3 at = BakedLatticeAt( world );
	float3 cell = min( floor( at ), g_frame[0].volumeCount.xyz - 2.0 );
	float3 f = at - cell;

	float3 sum = 0.0;
	float total = 0.0;

	[unroll]
	for ( uint k = 0u; k < 8u; ++k )
	{
		float3 offset = float3( float( k & 1u ), float( ( k >> 1u ) & 1u ), float( k >> 2u ) );
		float3 w3 = lerp( 1.0 - f, f, offset );

		//A corner behind the surface sees the far side of what the surface is on - DDGI's smooth backface weight
		float3 toCorner = offset - f;
		float apart = length( toCorner );
		float facing = apart > 1e-4 ? ( dot( toCorner / apart, geometric ) * 0.5 ) + 0.5 : 1.0;
		float weight = w3.x * w3.y * w3.z * ( ( facing * facing ) + 0.05 );

		sum += BakedBounce( cell + offset, normal ) * weight;
		total += weight;
	}

	return total > 1e-5 ? sum / total : BakedBounce( at, normal );
}

//The volume's light at a spot (BakedVolumeSpot) on a surface facing normal
float3 BakedVolume( float3 at, float3 normal )
{
	float3 count = g_frame[0].volumeCount.xyz;

	float below = floor( at.y );
	float above = min( below + 1.0, count.y - 1.0 );
	float between = at.y - below;

	float across = count.x * count.y;
	float v = ( at.z + 0.5 ) / count.z;

	float4 red0, green0, blue0, red1, green1, blue1;

	BakedLayer( float2( ( ( below * count.x ) + at.x + 0.5 ) / across, v ), red0, green0, blue0 );
	BakedLayer( float2( ( ( above * count.x ) + at.x + 0.5 ) / across, v ), red1, green1, blue1 );

	float4 red = lerp( red0, red1, between );
	float4 green = lerp( green0, green1, between );
	float4 blue = lerp( blue0, blue1, between );

	float4 facing = float4( 1.0, normal );

	return max( float3( dot( red, facing ), dot( green, facing ), dot( blue, facing ) ), 0.0 );
}

#endif

#ifdef PORTABLE
//The portable frame's bake, bound as its own since it has no table: sheet light and sky left, mask right;
//the volume's layers as tiles, the bounce below - see CoreBakedLighting.Portable
[[vk::binding(8,1)]] Sampler2D g_bakedSheet;
[[vk::binding(9,1)]] Sampler2D g_bakedVolume;

//A portable sheet texel's face mark, and in sky how much sky it sees - packed as CoreBakedLighting.MakePortableSheet does
float PortableMark( float packed, out float sky )
{
	float mark = floor( packed + 0.05 );
	sky = saturate( ( packed - mark ) / 0.9 );
	return mark;
}

//The sheet's light rgb and how much sky it sees a: bilinear over the four texels about the spot, each kept only where
//it lies on the spot's own face, as the full frame's SheetFacesLinear - so a box's sides do not bleed into each other
float4 PortableSheet( float2 uv )
{
	float2 size = max( g_frame[0].portableBake.xy, float2( 1.0, 1.0 ) );

	float2 at = ( uv * size ) - 0.5;
	float2 cell = floor( at );
	float2 f = at - cell;

	float ownSky;
	float4 centre = g_bakedSheet.SampleLevel( ( floor( uv * size ) + 0.5 ) / size, 0.0 );
	float own = PortableMark( centre.a, ownSky );

	float4 sum = 0.0;
	float total = 0.0;

	[unroll]
	for ( uint k = 0u; k < 4u; ++k )
	{
		float2 corner = float2( float( k & 1u ), float( k >> 1u ) );
		float4 texel = g_bakedSheet.SampleLevel( ( cell + corner + 0.5 ) / size, 0.0 );
		float2 w2 = lerp( 1.0 - f, f, corner );

		float sky;
		float weight = PortableMark( texel.a, sky ) == own ? w2.x * w2.y : 0.0;

		sum += float4( texel.rgb, sky ) * weight;
		total += weight;
	}

	return total > 1e-5 ? sum / total : float4( centre.rgb, ownSky );
}

//Whether there is a volume to read
bool HasPortableVolume()
{
	return g_frame[0].volumeCount.x > 0.5 && g_frame[0].portableBake.z > 0.5;
}

//The volume at a point: light rgb and sky seen a, or with bounce the live bounce rgb - each layer a tile, lerped
float4 PortableVolume( float3 world, bool bounce )
{
	float3 count = g_frame[0].volumeCount.xyz;
	float columns = g_frame[0].portableBake.z;
	float rows = ceil( count.y / columns );

	float3 at = clamp( ( world - g_frame[0].volumeOrigin.xyz ) * g_frame[0].volumeOrigin.w, float3( 0.0, 0.0, 0.0 ), count - 1.0 );

	float below = floor( at.y );
	float above = min( below + 1.0, count.y - 1.0 );

	float2 size = float2( columns * count.x, rows * count.z * 2.0 );
	float down = bounce ? rows * count.z : 0.0;

	float2 low = float2( ( fmod( below, columns ) * count.x ) + at.x, ( floor( below / columns ) * count.z ) + at.z + down ) + 0.5;
	float2 high = float2( ( fmod( above, columns ) * count.x ) + at.x, ( floor( above / columns ) * count.z ) + at.z + down ) + 0.5;

	return lerp( g_bakedVolume.SampleLevel( low / size, 0.0 ), g_bakedVolume.SampleLevel( high / size, 0.0 ), at.y - below );
}
#endif

//Octahedral mapping of a direction; must match CoreEnvmap.Fold exactly
float2 OctahedralUv( float3 direction )
{
	float3 d = normalize( direction );
	float sum = abs( d.x ) + abs( d.y ) + abs( d.z );

	if ( sum < 0.00001 ) return float2( 0.5, 0.5 );

	float2 p = d.xy / sum;

	if ( d.z < 0.0 )
	{
		p = float2(
			( 1.0 - abs( p.y ) ) * ( p.x >= 0.0 ? 1.0 : -1.0 ),
			( 1.0 - abs( p.x ) ) * ( p.y >= 0.0 ? 1.0 : -1.0 ) );
	}

	return ( p * 0.5 ) + 0.5;
}

//One band of one probe: bands stack down the texture, kept half a texel in from each end so the filter
//does not reach a sharper band
float3 SampleProbeBand( int slot, float2 uv, int band )
{
#ifdef PORTABLE
	return 0.0.xxx;
#else
	float bands = max( g_frame[0].probeInfo.z, 1.0 );
	float side = max( g_frame[0].probeInfo.y, 1.0 );

	float inset = 0.5 / ( side * bands );
	float v = ( ( float( band ) + clamp( uv.y, 0.0, 1.0 ) ) / bands );

	v = clamp( v, ( float( band ) / bands ) + inset, ( float( band + 1 ) / bands ) - inset );

	//Top level only: lower bands are the blur; neighbour-chosen mips would cross band edges and the seam
	float4 rgbm = g_textures[NonUniformResourceIndex( uint( slot ) )].SampleLevel( float2( uv.x, v ), 0.0 );

	return DecodeRgbm( rgbm, g_frame[0].probeInfo.w );
#endif
}

//One probe's reflection one way at one roughness, between two bands, since five blur steps stair-step visibly
float3 SampleProbe( int which, float3 direction, float roughness )
{
	if ( which < 0 || which >= 4 ) return 0.0.xxx;

	//Clamp the index: GLSL ES rejects a constant probes[-1] even in dead code
	Probe probe = g_frame[0].probes[clamp( which, 0, 3 )];

	int slot = int( probe.where.w );
	if ( slot <= 0 ) return 0.0.xxx;

	float2 uv = OctahedralUv( direction );

	float bands = max( g_frame[0].probeInfo.z, 1.0 );

	//Band index linear in roughness, matching how CoreEnvmap.Fold builds the bands
	float along = saturate( roughness ) * ( bands - 1.0 );

	int lower = int( floor( along ) );
	int upper = min( lower + 1, int( bands ) - 1 );

	float3 mixed = lerp(
		SampleProbeBand( slot, uv, lower ),
		SampleProbeBand( slot, uv, upper ),
		along - float( lower ) );

	return mixed * probe.tint.rgb * probe.tint.a;
}

//The probes covering a point - see ProbesAt; none on a pipeline baked without kProbesOn
ProbeMix ProbesHere( float3 world )
{
	if ( kProbesOn == 0 )
	{
		ProbeMix none;
		none.first = -1;
		none.firstWeight = 0.0;
		none.second = -1;
		none.secondWeight = 0.0;
		return none;
	}

	return ProbesAt( world );
}

//What the probes say lies one way, over `under`: the second where it reaches, the first over that; roughness
//picks the band, boxed reads along each box's projection of `away`
float3 ProbesOver( ProbeMix mix, float3 under, float3 world, float3 away, float roughness, bool boxed )
{
	float3 seen = under;

	if ( mix.second >= 0 )
		seen = lerp( seen, SampleProbe( mix.second, boxed ? ProbeDirection( mix.second, world, away ) : away, roughness ), mix.secondWeight );

	if ( mix.first >= 0 )
		seen = lerp( seen, SampleProbe( mix.first, boxed ? ProbeDirection( mix.first, world, away ) : away, roughness ), mix.firstWeight );

	return seen;
}

//Box projection: a probe is right only at its centre, so read where the reflected ray meets its box's wall
float3 ProbeDirection( int probe, float3 world, float3 away )
{
	//Clamped for the same reason as SampleProbe's, and here there is not even a guard above it to hide behind.
	Probe held = g_frame[0].probes[clamp( probe, 0, 3 )];

	if ( held.mins.w <= 0.5 ) return away;

	float3 first = ( held.maxs.xyz - world ) / away;
	float3 second = ( held.mins.xyz - world ) / away;

	float3 furthest = max( first, second );
	float distance = min( furthest.x, min( furthest.y, furthest.z ) );

	if ( distance <= 0.0 ) return away;

	return normalize( ( world + ( away * distance ) ) - held.where.xyz );
}

//The probes' part of the reflection along `away`, and in skyLeft how much sky still shows under them - apart, so
//each is scaled by what it stands for: the probes by the bake's light against theirs, the sky by how much sky is seen
float3 ProbesPart( ProbeMix mix, float3 world, float3 away, float roughness, out float skyLeft )
{
	float3 seen = float3( 0.0, 0.0, 0.0 );
	skyLeft = 1.0;

	if ( mix.second >= 0 )
	{
		seen = lerp( seen, SampleProbe( mix.second, ProbeDirection( mix.second, world, away ), roughness ), mix.secondWeight );
		skyLeft *= 1.0 - mix.secondWeight;
	}

	if ( mix.first >= 0 )
	{
		seen = lerp( seen, SampleProbe( mix.first, ProbeDirection( mix.first, world, away ), roughness ), mix.firstWeight );
		skyLeft *= 1.0 - mix.firstWeight;
	}

	return seen;
}

//Environment reflection (probe or sky); shared by surfaces and SSR so both agree exactly
float3 EnvironmentAt( ProbeMix probes, float3 world, float3 normal, float3 away, float roughness )
{
	float3 lookAt = normalize( lerp( away, normal, roughness * roughness ) );

	//Reflect the drawn sky, not the flat ambient; flat colour only when there's no sky
	float3 sky = HasSky() ? SkyReflected( lookAt, roughness ) : SceneAmbient( float3( 0.0, 0.0, 0.0 ) );

	return ProbesOver( probes, sky, world, away, roughness, true );
}

//Exposure and tonemap for frames without a compose pass, using the same EngineTonemap
#ifdef TARGET_SCREEN

#include "tonemap.slang"

#ifdef ENGINE_SCREEN_CHAIN

//Point-effect chain to screen (written by CorePointChain.ScreenText) when there's no compose
float3 EngineFinishToScreen( float3 colour, float4 position )
{
	return EngineScreenChain( colour, position.xy / max( g_frame[0].settings.yz, float2( 1.0, 1.0 ) ) );
}

#define FinishToScreen( colour ) EngineFinishToScreen( ( colour ), i.position )

#else

float3 FinishToScreen( float3 colour )
{
	return EngineTonemap( colour * g_frame[0].finish.x, g_frame[0].finish.y );
}

#endif

//Alpha written to screen: 1 unless the draw blends, since the Linux window is see-through
#ifndef F_TRANSLUCENT
#define F_TRANSLUCENT 0
#endif

float ScreenAlpha( bool blends, float alpha )
{
#ifdef CLUSTER
	//Cluster pipelines never blend, so return 1 here and keep tint/alpha from living across lighting
	return 1.0;
#else
	return blends ? alpha : 1.0;
#endif
}

#endif

#ifdef CLUSTER
#include "cluster_main.slang"
#endif

#ifdef ENGINE_WORLD_PUSH

//Unposed model-space corner position, so surface detail sticks to the mesh; clusters reread the pool
#ifdef CLUSTER
static uint g_clusterRest;

float3 RestPosition( VIn i )
{
	return LoadVisVertex( g_clusterRest ).position;
}
#else
float3 RestPosition( VIn i )
{
	return i.position;
}
#endif

#endif
