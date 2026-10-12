//Frame block (set 1, binding 1): camera, lights, sky, shadows; CoreSceneRenderer fills it

#ifndef ENGINE_FRAME_DATA
#define ENGINE_FRAME_DATA

struct Light
{
	float4 position;  //Position xyz, range w (unused on the sun)
	float4 colour;    //Colour rgb times brightness, kind w (0 sun, 1 point, 2 spot)
	float4 direction; //Direction xyz, inner cone cosine w - a point's shape (AreaShape)
	float4 cone;      //Outer cosine x (a point's half width), source width y, spare zw
	float4 shape;     //Falloff power x, faces reading movers y (a bit each), half height z, turn w
};

//Envmap probe: an octahedral 2D picture of all directions with blurred mips stacked below
struct Probe
{
	float4 where; //Position xyz, picture in the table w
	float4 mins;  //Near box corner xyz, w one if the box is projected
	float4 maxs;  //Far box corner xyz, w how far from the edge it fades
	float4 tint;  //Colour rgb, strength a
};

struct FogVolume
{
	float4 centre;   //Position xyz, strength w
	float4 extents;  //Half size along its own axes xyz, edge fade w
	float4 rotation; //Quaternion turning its own axes into the world's
	float4 colour;   //Colour rgb it scatters, spare a
};

struct FrameData
{
	row_major float4x4 worldToClip;
	float4 camera; //Eye position xyz, light count w
	Light lights[16];
	float4 settings; //Occlusion strength, window width, window height, sky table's picture index
	row_major float4x4 clipToWorld;
	float4 zenith;
	float4 horizon;
	float4 ground;
	float4 sunDirection; //Direction xyz, enabled w
	float4 sunColour;    //Colour rgb, disc cosine a
	float4 viewInfo;     //Debug view x, occlusion picture y, depth picture z, texture LOD bias w

	//Last frame's view, which a pixel is reprojected through
	row_major float4x4 previousWorldToClip;

	//Gradient fog: rgb and the most of the view it may take, the distances and heights it runs between,
	//and both falloff exponents. fogShape.z below one is off
	float4 fogColour;
	float4 fogRange;  //Start distance, end distance, start height, end height
	float4 fogShape;  //Distance exponent, height exponent, enabled, colour from the sky

	//Baked lighting: lightmap sheet for static surfaces, probe volume for moving ones; zero = no bake
	float4 bakedMaps;    //Sheet's table index x, its directions' y, its sky's z, one where there is a sheet w
	float4 bakedVolume;  //The three pictures' places xyz, one where there is a volume w
	float4 volumeOrigin; //First lattice point xyz, one over the step between points w
	float4 volumeCount;  //Points along x, y and z, the sky's visibility picture w (nought for none)
	float4 bakedMore;    //Walls' picture x, live bounce colour y and lean z, the sheet's first band w (nought for none)
	float4 bakedGlow[4]; //Glow layers: sheet's index x, volume's y, group brightness z (BakedGlow); nought for none

	//How many of the four probes are filled is x; nothing after it matters when zero
	float4 probeInfo; //Count, one picture's width, bands, RGBM range
	Probe probes[4];

	//Two clocks: wall time always runs; scene time pauses and scales with the scene
	float4 time; //Seconds since start, frame delta, scene seconds, spare

	//The sun's light after the air it crosses, and how far into night; both depend only on the sun's height,
	//so CoreSceneRenderer.SetSky works them out once a frame
	float4 sunLight; //What is left of the sun's light rgb, how far into night a

	//Clip to world ray without the camera position, used by SkyRay; all zero means use clipToWorld
	row_major float4x4 rayToWorld;

	//Sun shadow pages: world to sun frame, page levels and depth range, eye position, map slots
	row_major float4x4 sunToPages;
	float4 sunPages;
	float4 sunEye;
	float4 sunMaps;
	float4 moverZone;

	//The scene's flat ambient light - its AmbientLight components and its directional lights' sky colours,
	//added - and in a how much of what the sky and the probes give it replaces; see SceneAmbient in frame.slang.
	float4 ambient;

	//Exposure, for every pass taking the frame's light to the screen's, and the tonemap curve for a pass
	//finishing the picture itself - see TARGET_SCREEN in frame.slang
	float4 finish; //Exposure x, curve y, first point effect of the world shading in effects z, spare w

	//The numbers of the point effects laid over this frame - see CorePointChain. Each chain
	//of them reads its own run, from the slot its push block names; nothing else reads these.
	float4 effects[128];

	//The nearest eight fog boxes around the camera; fogVolumeInfo.x is how many are set
	FogVolume fogVolumes[8];
	float4 fogVolumeInfo;

	//The fog sheet: its table index, slice count, and the two distances the slices span - see CoreVolumeFog
	//x of nought is no sheet, and a surface marches the air itself
	float4 fogVolumeSheet;

	//The sun's maps as depth for the penumbra search: pages' table index x, movers' y, nought where not read (SunSoft);
	//z and w the lamps' movers map, compared and as depth - see LampShadow
	float4 sunDepth;

	//What water and glass trace reflections through: the nearest-depth chain and the shaded world's picture
	//by table index, nought where no reflections were drawn - see ScreenReflected
	float4 screenTrace;

	//Panorama sky: turn y radians, one w if any (which lays a portable sky table out by direction); panoramaLight
	//is brightness by tint - see CorePanoramaSky
	float4 panorama;
	float4 panoramaLight;

	//The sky's light from its capture (CoreSkyLight): reflection levels' table index x and their count y, the
	//irradiance picture's index z, nought where none; the ambient factor w, nought for a quarter
	float4 skyLight;

	//Area lamps' LTC tables by table index: inverse matrix x, magnitude and Fresnel y - see CoreAreaLights
	float4 area;

	//How shadow edges are filtered: nought one compare, one a fixed soft edge, two as the source makes them; spare yzw
	float4 shadow;

	//The portable frame's bake (CoreBakedLighting.Portable): the sheet's light half wide x and high y, nought for none,
	//the volume's layers to a row of tiles z, one where its bounce half is filled w
	float4 portableBake;

	//The screen's indirect light picture by table index x, nought for none - see AmbientOcclusion; one in y where rays
	//traced it, its a then the share of them that got out (ray_light.shader); spare zw
	float4 screenLight;

	//The scene's ray structure's address in 24-bit parts xy and its top 16 bits w, its instances' colours picture z;
	//nought for none
	float4 rays;

	//The probe grid of a scene with no bake (CoreProbeGrid): its four pictures' table indices, nought for none; the
	//cascades and probes along x, y and z; each cascade's first cell xyz and step w - see GridLight
	float4 grid[4];
};

#ifdef WEBGL
//WebGL has no storage buffers: a uniform block array of one, every member float4-wide
#ifdef STEREO
//Both of a headset's eyes in one pass: two blocks, the second the second eye's. Only what depends on the eye
//is read at g_frame[Eye()]; everything else stays at g_frame[0], which is the same for both - see Eye() below.
[[vk::binding(1,1)]] ConstantBuffer<FrameData> g_frame[2];
#else
[[vk::binding(1,1)]] ConstantBuffer<FrameData> g_frame[1];
#endif
#else
[[vk::binding(1,1)]] StructuredBuffer<FrameData> g_frame;
#endif

//Reads a field from this eye's block; WebGL only allows constant indices into block arrays
#if defined( STEREO )
#define EYE_FRAME( field ) ( Eye() == 0 ? g_frame[0].field : g_frame[1].field )
#else
#define EYE_FRAME( field ) g_frame[Eye()].field
#endif

//Headset eye being drawn, 0 otherwise; per-eye data reads g_frame[Eye()], the rest g_frame[0]
#ifdef STEREO
//The view a pixel is drawn for, set at the entry by STEREO_EYE_IN( i ) as InstanceNumbers sets the instance
//The shading reads it deep inside, where the interpolants are out of scope
static uint g_eyeId;

//The view a vertex is drawn for: SPIR-V's ViewIndex, which SPIRV-Cross writes as gl_ViewID_OVR for the vertex stage
//only ("OVR_multiview2 can only be used with Vertex shaders"), so the pixel stage is handed it
uint ViewOfThisVertex()
{
	__stage_switch
	{
	case vertex:
		return spirv_asm
		{
			OpCapability MultiView;
			OpExtension "SPV_KHR_multiview";
			result:$$uint = OpLoad builtin(ViewIndex:uint);
		};
	default:
		return 0;
	}
}
#endif

uint Eye()
{
#if defined( STEREO )
	//Read where it can be, taken from the vertex stage where not - see ViewOfThisVertex and g_eyeId
	__stage_switch
	{
	case vertex:
		return ViewOfThisVertex();
	case fragment:
		return g_eyeId;
	default:
		return 0;
	}
#elif defined( WEBGPU ) || defined( WEBGL )
	return 0;
#else
	__stage_switch
	{
	case vertex:
	case fragment:
		return spirv_asm
		{
			OpCapability MultiView;
			OpExtension "SPV_KHR_multiview";
			result:$$uint = OpLoad builtin(ViewIndex:uint);
		};
	default:
		return 0;
	}
#endif
}

//World position to this eye's clip space, for passes whose push block holds only a world matrix
float4 EyeClip( float4 world )
{
	return mul( world, EYE_FRAME( worldToClip ) );
}

#endif
