//Instance layout comes from cluster_main.slang (VisInstance), aliased as Instance; must match C#

//The tables the engine's cluster passes read - the cull, the depth raster and the skin pass - declared once for all three.

#ifndef ENGINE_MESH_TABLES
#define ENGINE_MESH_TABLES

static const uint MaxViews = 32;
static const uint MaxLists = 100;

//The project's level-of-detail groups: how many, and samples a curve - as Rendering.LodGroups says
static const uint MaxLodGroups = 16;
static const uint LodCurveSamples = 32;
static const uint ClusterVertices = 372;

static const uint InstanceDrawn = 1;
static const uint InstanceNoShadow = 2;
static const uint InstanceShadowOnly = 4;
static const uint InstanceSquare = 8;
//The same numbers as CoreMeshShaders' - kept whole so the bits cannot drift apart
static const uint InstancePosed = 16;
static const uint InstanceNeverStill = 32;

//The bit an entry carries above its cluster when its triangles found no room in the list's index run:
//nothing draws it and the bin pass must not shade it - see Append; here as every reader needs it
static const uint ClusterDropped = 0x40000000u;

//Distance slice stored in the entry so the count and fill passes can't disagree about it
static const uint ClusterSliceShift = 26;
static const uint ClusterSlices = 16;

//First slice shaded with kFarShading (~45 m); must match CoreMeshShaders.FarSlice
static const uint ClusterFarSlice = 11;

//Where a span of a material's run begins, in slices: the first is all nearer than the cut, the rest divide
//what is past it; a material drawn in one call has one span and no cut
uint ClusterSpanFrom( uint span, uint draws, uint slices, uint cut )
{
	if ( draws <= 1u || span == 0u ) return 0u;

	cut = min( cut, slices );

	return cut + ( ( ( span - 1u ) * ( slices - cut ) ) / ( draws - 1u ) );
}

//Cluster index of an entry (low 26 bits); always use this, a raw read indexes far past the table
uint ClusterOf( uint entry ) { return entry & ( ( 1u << ClusterSliceShift ) - 1u ); }

//And which slice of the distance it stood in.
uint SliceOf( uint entry ) { return ( entry >> ClusterSliceShift ) & ( ClusterSlices - 1u ); }

//Index = entry << 7 | vertex within cluster (max 128), so shared vertices transform once
static const uint EntryShift = 7;
static const uint CornerMask = 127;

static const uint MaterialTwoSided = 1;
static const uint MaterialAlphaTest = 2;
static const uint MaterialTranslucent = 4;
static const uint MaterialLoose = 8;

//The surface moves its own vertices: its clusters are binned and shaded, and left out of the pre-pass,
//because the corner the pre-pass would put in the depth is not the corner the surface draws - see Append.
static const uint MaterialMoves = 16;

//Free: it was MaterialThins, a surface the cull drew fewer clusters of with distance. A canopy's
//levels are thinned where they are built now - see LevelChain - and nothing is dropped at draw time.

//MeshInfo.flags: levels are a chain judged by the mesh's sphere, and two bits from MeshShadowShift say how
//much coarser its shadows are - see ChainLevel in mesh_cull.slang and CoreClusters.MeshChain
static const uint MeshChain = 4u;
static const uint MeshShadowShift = 8u;

struct MeshInfo
{
	uint clusterFirst;
	uint clusterCount;
	uint flags;
	uint levels;
	float4 sphere;
	float4 boxMin;
	float4 boxMax;

	//A level a row: where its clusters are, how many, their least error and their parents' greatest
	uint4 level[8];
};

#include "vis_instance.slang"
typealias Instance = VisInstance;

//A posed thing's vertex is read out of its posed range rather than the mesh's own.
uint PosedVertex( Instance i, uint vertex )
{
	return ( i.flags & InstancePosed ) != 0 ? ( vertex - i.posedSource ) + i.posedFirst : vertex;
}

struct ClusterInfo
{
	float4 sphere;
	float4 cone;
	float4 boxMinError;
	float4 boxMaxParentError;
	uint4 words;
};

struct MaterialInfo
{
	uint flags;
	uint colourSlot;
	float cutoff;
	uint pad;
};

//A cull view (camera or light); eye.w is error scale per metre, screen holds scales or caster set
struct View
{
	float4 planes[6];
	row_major float4x4 worldToClip;
	float4 eye;
	float4 lod;
	float4 hzb;
	float4 screen;

	//Headset gaze direction and foveation strength (zero for non-eye views); keep last
	float4 fovea;
};

//Which instances a light's view takes: every one, only what stands still - the sun's pages hold the still world, and a thing in
//motion would leave its shadow standing where it was - or only what moves, which is what the map drawn again every frame is for.
static const uint CastersAll = 0;
static const uint CastersStill = 1;
static const uint CastersMoving = 2;

bool IsCamera( View view ) { return view.screen.w > 0.5; }

//Orthographic view: size by eye.w directly, not by distance from its far-back eye
bool IsFlat( View view ) { return view.screen.w > 1.5; }

//Whether a light's view shrinks with distance - a lamp's face, cut from its own eye like the camera's -
//or looks down parallel rays like the sun's, cut by texels a metre; see NearLevels and LightLevels
bool PerspectiveLight( View view ) { return !IsCamera( view ) && view.screen.y > 0.5; }
uint ViewCasters( View view ) { return IsCamera( view ) ? CastersAll : uint( view.screen.x ); }

//Whether an instance is one of what moves: posed, or put somewhere else since the last time it held still.
bool Moves( Instance instance ) { return ( instance.flags & ( InstancePosed | InstanceNeverStill ) ) != 0 || instance.moving != 0; }

struct ListInfo
{
	uint base;
	uint capacity;

	//The list's run of the index buffer, in triangles - see Append. A drawn list's only: the first
	//half's three and what appeared share a run a kind, the second starting where the first ended.
	uint indexBase;
	uint indexCapacity;
};

struct Params
{
	uint instanceCount;
	uint viewCount;
	uint listCount;
	uint workCapacity;
	uint retestList;
	uint cameraLists;
	uint materialsAt;
	uint entriesAt;
	//The run of the index buffer the lights' lists share, in triangles: where it begins and how many it has room for.
	//Every light list is laid in it after the one before by the finish, and filled by the bin pass - see Append.
	uint shadowIndexAt;
	uint shadowIndexCapacity;
	uint listTotal;

	//Materials and their distance-sorted bins: counts/fill reset each frame, one draw per material
	uint materials;
	uint binCountsAt;
	uint binOffsetsAt;
	uint binFillAt;
	uint binnedAt;
	uint binArgsAt;

	//Where the camera's hidden instances are in the visible buffer, a bit each by instance - nought
	//where the camera hides nothing. See CoreMeshRender.HiddenFromCamera and Takes in mesh_cull.slang.
	uint hiddenAt;

	//Distance slices per material bin (1..16); only orders clusters near-first within one run
	uint binSlices;
	//The lists' mesh-stage draws in the visible buffer, four words a list: group counts giving each entry a
	//workgroup; written by the cull's finish, read only where mesh stages draw - see CoreMeshRender
	uint meshArgsAt;

	//Where the debug words are in the visible buffer, under RINTEN_MESH_DEBUG.
	uint debugAt;

	//Camera pixels per metre at 1 m, so vertex stages size clusters exactly as the cull did
	uint errorScale;

	//How many triangles landed in each bin - a word a bin, beside the counts and
	//the fills. What the bin's run of the index buffer is sized by; see the finish.
	uint binTrisAt;

	//Bin count (materials * binSlices). Struct is written by index: never insert words, free in place
	uint bins;

	//Where the packed clusters live as a uint4 index, and room for how many - see the emit in CullInstance
	uint singlesAt;
	uint singlesCapacity;

	//The bins' index runs: how far the bin pass has got into each, and where each begins
	uint binTriFillAt;
	uint binTriOffsetsAt;

	//Where the lists' indexed draws are, five words a list - see the finish.
	uint drawArgsAt;

	//The binned list's index run in triangles, its room, and how many pairs the list has
	uint binIndexAt;
	uint binIndexCapacity;
	uint binnedCapacity;

	//Draw calls per material run, split by distance so far spans use the kFarShading pipeline
	uint binDraws;

	//Which depth lists a mesh stage draws this frame: a bit each for the camera's three kinds, 8 for the lights';
	//nothing writes indices for those, and their runs are absent - see CoreMeshRender.MeshKinds
	uint meshDepth;
	//Offset of the visible-cell list in the work buffer (see CullCells)
	uint cellsAt;

	//Which still clusters the pre-pass draws, by distance slice; nought none, all ones all - see PrepassSlices;
	//past it a material writes its own depth in its far pass
	uint prepassSlices;

	//Per-material texture demand words (float bits, max per frame), read back for texture streaming
	uint wantAt;
	uint wantPad0;
	uint wantPad1;
	uint wantPad2;

	//Header must be whole float4s (40 words); keep in sync with CoreMeshRender.HeaderWords and VisParams

	View views[MaxViews];
	ListInfo lists[MaxLists];

	//The LOD groups' baked curves: LodCurveSamples error multiples a group, then each group's distance start
	//and one over its length in xy; last, so nothing before it moved - see LodScaleOf in mesh_cull.slang
	float4 lodCurves[MaxLodGroups * LodCurveSamples / 4];
	float4 lodSpans[MaxLodGroups];
};

float4x4 WorldOf( Instance i ) { return float4x4( i.world0, i.world1, i.world2, i.world3 ); }

float GrewBy( float4x4 world )
{
	float3 x = float3( world[0][0], world[0][1], world[0][2] );
	float3 y = float3( world[1][0], world[1][1], world[1][2] );
	float3 z = float3( world[2][0], world[2][1], world[2][2] );
	return sqrt( max( dot( x, x ), max( dot( y, y ), dot( z, z ) ) ) );
}

uint TriangleByte( StructuredBuffer<uint> bytes, uint at )
{
	return ( bytes[at >> 2] >> ( ( at & 3 ) * 8 ) ) & 255u;
}

#endif
