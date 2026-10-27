//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Cluster cull passes: 0 instances, 1 clusters, 2 depth retest, 3 bin camera clusters by material

#include "mesh_tables.slang"


[[vk::binding(0,0)]] StructuredBuffer<Params> g_params;
[[vk::binding(1,0)]] StructuredBuffer<Instance> g_instances;
[[vk::binding(2,0)]] StructuredBuffer<MeshInfo> g_meshes;
[[vk::binding(3,0)]] StructuredBuffer<ClusterInfo> g_clusters;
[[vk::binding(4,0)]] StructuredBuffer<uint> g_materialData;
[[vk::binding(5,0)]] RWStructuredBuffer<uint4> g_work;
[[vk::binding(6,0)]] RWStructuredBuffer<uint> g_visible;

//The instances by the world's grid: two uint4 a cell - its sphere (a radius below nought is the moving cell, taken whatever
//looks), then the first of its run and how many - and after the cells the instances, four to a uint4. See CoreInstanceCells.
[[vk::binding(7,0)]] StructuredBuffer<uint4> g_cells;
[[vk::binding(8,0)]] StructuredBuffer<uint> g_clusterTriangles;
[[vk::binding(9,0)]] RWStructuredBuffer<uint> g_indices;
[[vk::binding(0,1)]] Sampler2D g_pyramid;

[[vk::push_constant]]
cbuffer Push
{
	uint g_pass;
	uint g_debugOn;

	//The depth pyramid's two dispatches, groups across in the low half and down in the high - written
	//by the finish pass as the second pyramid's arguments, or as nothing where nothing appeared.
	uint g_pyramidA;
	uint g_pyramidB;
};

void Dbg( uint at, uint value ) { g_visible[g_params[0].debugAt + at] = value; }

bool InsideTheFrustum( View view, float3 centre, float radius )
{
	for ( uint i = 0; i < 6; ++i )
	{
		float4 plane = view.planes[i];
		if ( dot( plane.xyz, centre ) + plane.w < -radius ) return false;
	}
	return true;
}

//Backface cone test; cutoff is the SINE of the cone half-angle (1 = no cone), never culls front faces
bool FacesAway( float3 centre, float radius, float3 axis, float cutoff, float3 eye )
{
	if ( cutoff >= 1.0 ) return false;
	float coneCos = sqrt( max( 0.0, 1.0 - ( cutoff * cutoff ) ) );
	float3 away = centre - eye;
	float distance = length( away );
	if ( distance <= radius ) return false;
	float cosPhi = dot( axis, away / distance );
	float sinPhi = sqrt( max( 0.0, 1.0 - ( cosPhi * cosPhi ) ) );
	float sinTheta = sqrt( max( 0.0, 1.0 - ( coneCos * coneCos ) ) );
	return ( ( cosPhi * coneCos ) - ( sinPhi * sinTheta ) ) * distance > radius;
}

//Error multiplier for distance from a headset's gaze (1 off-eye), measured from the sphere's edge
float FoveaScale( View view, float3 centre, float radius, bool farthest )
{
	if ( !IsCamera( view ) || view.fovea.w <= 0.0 ) return 1.0;

	float3 toward = centre - view.eye.xyz;
	float away = length( toward );
	if ( away <= radius ) return 1.0;

	float off = acos( clamp( dot( toward / away, view.fovea.xyz ), -1.0, 1.0 ) );
	float spread = asin( saturate( radius / away ) );
	float angle = farthest ? off + spread : max( off - spread, 0.0 );

	//Sharp to fifteen degrees off the gaze, the coarsest past forty: about where an eye stops reading detail, and where it stops seeing it at all.
	return 1.0 + view.fovea.w * 3.0 * smoothstep( 0.26, 0.70, angle );
}

//Distance slice (half an octave each), so material bins draw near clusters first for early-Z
uint ClusterSlice( View camera, float3 middle, uint slices )
{
	if ( slices <= 1u ) return 0u;

	float steps = log2( max( distance( camera.eye.xyz, middle ), 1.0 ) ) * 2.0;

	return uint( clamp( steps, 0.0, float( slices - 1u ) ) );
}

float PixelsAcross( View camera, float3 centre, float radius )
{
	//A flat view: as many pixels at every depth - see IsFlat.
	if ( IsFlat( camera ) ) return abs( camera.screen.z ) * radius * camera.screen.x;

	float deep = distance( centre, camera.eye.xyz );
	if ( deep <= radius ) return 1e30;
	return abs( camera.screen.z ) * radius / deep * camera.screen.x;
}

//Occlusion test vs the depth pyramid; pick the level where the rect is at most one texel across
bool BehindTheDepth( View view, float3 middle, float3 reach )
{
	if ( view.hzb.w < 0.5 ) return false;

	float2 low = float2( 1e9, 1e9 );
	float2 high = float2( -1e9, -1e9 );
	//Depth is reversed - near 1, far 0 - so the box's nearest corner is its greatest
	float nearest = -1e9;

	for ( uint corner = 0; corner < 8; ++corner )
	{
		float3 at = middle + reach * float3(
			( corner & 1 ) != 0 ? 1.0 : -1.0,
			( corner & 2 ) != 0 ? 1.0 : -1.0,
			( corner & 4 ) != 0 ? 1.0 : -1.0 );
		float4 clip = mul( float4( at, 1.0 ), view.worldToClip );
		if ( clip.w <= 0.0001 ) return false;
		float3 ndc = clip.xyz / clip.w;
		float2 on = ( ndc.xy * 0.5 ) + 0.5;
		low = min( low, on );
		high = max( high, on );
		nearest = max( nearest, ndc.z );
	}

	if ( high.x < 0.0 || high.y < 0.0 || low.x > 1.0 || low.y > 1.0 ) return false;
	if ( nearest >= 1.0 ) return false;

	//The first level's texels the depth fills, and the power of two they are kept in - the rest far, see CoreDepthPyramid
	float2 image = view.hzb.xy;
	float2 kept = exp2( ceil( log2( max( image, 1.0 ) ) ) );
	float last = view.hzb.z - 1.0;

	float across = max( ( high.x - low.x ) * image.x, ( high.y - low.y ) * image.y );
	float level = clamp( ceil( log2( max( across, 1.0 ) ) ), 0.0, last );
	float shrink = exp2( level );
	float2 levelSize = max( floor( kept / shrink ), 1.0 );
	float2 edge = levelSize - 1.0;

	//Through the first level's texels, where a texel of every level covers exactly its share
	int2 first = int2( clamp( floor( low * image / shrink ), 0.0, edge ) );
	int2 past = int2( clamp( floor( high * image / shrink ), 0.0, edge ) );
	past = min( past, first + 1 );

	int lod = int( level );
	float stored = 1.0;
	for ( int y = first.y; y <= past.y; y++ )
		for ( int x = first.x; x <= past.x; x++ )
			stored = min( stored, g_pyramid.Load( int3( x, y, lod ) ).x );

	return nearest < stored;
}

//A cluster's box in the world, as an axis-aligned one round the turned box.
void WorldBox( float4x4 world, float3 boxMin, float3 boxMax, float allowance, out float3 middle, out float3 reach )
{
	float3 half = ( boxMax - boxMin ) * 0.5;
	middle = mul( float4( ( boxMin + boxMax ) * 0.5, 1.0 ), world ).xyz;
	reach = float3(
		( abs( world[0][0] ) * half.x ) + ( abs( world[1][0] ) * half.y ) + ( abs( world[2][0] ) * half.z ),
		( abs( world[0][1] ) * half.x ) + ( abs( world[1][1] ) * half.y ) + ( abs( world[2][1] ) * half.z ),
		( abs( world[0][2] ) * half.x ) + ( abs( world[1][2] ) * half.y ) + ( abs( world[2][2] ) * half.z ) ) * allowance;
}

//Index-buffer run this thread claimed but hasn't written yet; WriteClaimed writes it wave-wide
static uint g_claimCount = 0;
static uint g_claimFirst = 0;
static uint g_claimSource = 0;
static uint g_claimCorner = 0;

//Writes every lane's claimed triangles together, one per lane; call with the whole wave present
void WriteClaimed()
{
	uint lanes = WaveGetLaneCount();
	uint me = WaveGetLaneIndex();
	uint4 claimed = WaveActiveBallot( g_claimCount > 0 );

	for ( uint word = 0; word < 4; ++word )
	{
		uint bits = claimed[word];

		while ( bits != 0 )
		{
			uint lane = ( word * 32 ) + firstbitlow( bits );
			bits &= bits - 1;

			uint count = WaveReadLaneAt( g_claimCount, lane );
			uint first = WaveReadLaneAt( g_claimFirst, lane );
			uint source = WaveReadLaneAt( g_claimSource, lane );
			uint corner = WaveReadLaneAt( g_claimCorner, lane );

			for ( uint t = me; t < count; t += lanes )
			{
				uint from = ( source + t ) * 3;
				uint put = first + ( t * 3 );
				g_indices[put] = corner | TriangleByte( g_clusterTriangles, from );
				g_indices[put + 1] = corner | TriangleByte( g_clusterTriangles, from + 1 );
				g_indices[put + 2] = corner | TriangleByte( g_clusterTriangles, from + 2 );
			}
		}
	}

	g_claimCount = 0;
}

void Append( uint list, uint instance, uint cluster, uint material, uint4 words, uint slice )
{
	Params params = g_params[0];
	ListInfo info = params.lists[list];
	uint at = 0;
	InterlockedAdd( g_visible[( list * 4 ) + 1], 1, at );
	if ( at >= info.capacity ) return;
	g_visible[info.base + ( at * 2 )] = instance;

	//The cluster, and how far away the cull found it - see ClusterSlice. A list that runs out of room
	//marks the entry as well, above both; every reader takes the cluster back out through ClusterOf.
	uint entry = cluster | ( slice << ClusterSliceShift );
	uint triangles = words.z >> 16;

	//Light lists only count here; the finish lays them end to end in one shared run, bin pass fills them
	if ( list >= params.cameraLists )
	{
		InterlockedAdd( g_visible[( list * 4 ) + 2], triangles );
		g_visible[info.base + ( at * 2 ) + 1] = entry;
		return;
	}

	//Write indices only for drawn lists; vertex-moving surfaces skip the pre-pass but are still binned
	bool moves = ( g_materialData[material * 4] & MaterialMoves ) != 0;

	//Nor one past the frame's pre-pass cut: its material's far pass writes its depth itself - see prepassSlices,
	//and CoreSceneDrawer.PrepassSlices for why the far clusters are the ones not worth drawing twice.
	bool prepassed = !moves && slice < params.prepassSlices;

	//Mesh-stage depth lists store packed entries instead of indices, one workgroup each (see MainMs)
	bool meshed = list < params.retestList && ( ( params.meshDepth >> ( list % 3u ) ) & 1u ) != 0;

	if ( meshed && prepassed )
	{
		uint packedAt = 0;
		InterlockedAdd( g_visible[( list * 4 ) + 2], 1, packedAt );
		if ( packedAt < info.indexCapacity ) g_visible[info.indexBase + packedAt] = at;
	}

	//Its triangles into the list's run - where the list is drawn from indices.
	if ( list < params.retestList && prepassed && !meshed )
	{
		uint start = list >= 3 ? min( Counted( ( ( list - 3 ) * 4 ) + 2 ), info.indexCapacity ) : 0;
		uint tri = 0;
		InterlockedAdd( g_visible[( list * 4 ) + 2], triangles, tri );
		tri += start;

		//Which entry of this list, counted from the list's own start: a list's draw reads its pairs from there
		//- see mesh_raster.slang - so an index names as many entries as one list holds, not every list's.
		uint corner = at << EntryShift;

		//Out of room: fill the claimed run with degenerate triangles and mark the entry unshaded
		if ( tri + triangles > info.indexCapacity )
		{
			g_visible[info.base + ( at * 2 ) + 1] = entry | ClusterDropped;

			for ( uint t = tri; t < info.indexCapacity; t++ )
			{
				uint put = ( info.indexBase + t ) * 3;
				g_indices[put] = corner;
				g_indices[put + 1] = corner;
				g_indices[put + 2] = corner;
			}

			return;
		}

		//Claimed here, written by the whole wave in WriteClaimed - see there.
		g_claimCount = triangles;
		g_claimFirst = ( info.indexBase + tri ) * 3;
		g_claimSource = words.y;
		g_claimCorner = corner;
	}

	g_visible[info.base + ( at * 2 ) + 1] = entry;

	//And counted for its material where the camera will shade it - the camera's lists are the ones
	//below the retest's - with its triangles, which is what the bin's run of indices is sized by.
	if ( list < params.retestList )
	{
		//Its bin: its material's run of slices, and its own slice of that - which is where the binned
		//list puts it, so a material's run comes out sorted from near to far without anything sorting it.
		uint bin = ( material * max( params.binSlices, 1u ) ) + slice;

		InterlockedAdd( g_visible[params.binCountsAt + bin], 1 );
		InterlockedAdd( g_visible[params.binTrisAt + bin], triangles );
	}
}

//Read atomically-counted words with an atomic too; a plain read may return a stale cached value
uint Counted( uint at )
{
	uint value = 0;
	InterlockedOr( g_visible[at], 0u, value );
	return value;
}

//Camera takes visible non-shadow-only instances; lights take their casters, even camera-hidden ones
bool Takes( View view, Instance instance, uint index )
{
	if ( IsCamera( view ) )
	{
		if ( ( instance.flags & InstanceShadowOnly ) != 0 ) return false;

		uint hiddenAt = g_params[0].hiddenAt;

		return hiddenAt == 0 || ( ( g_visible[hiddenAt + ( index >> 5 )] >> ( index & 31u ) ) & 1u ) == 0;
	}

	if ( ( instance.flags & InstanceNoShadow ) != 0 ) return false;

	uint casters = ViewCasters( view );
	if ( casters == CastersStill && Moves( instance ) ) return false;
	if ( casters == CastersMoving && !Moves( instance ) ) return false;

	return true;
}

//Per-instance error scale: bias times its LOD group curve at the camera's distance (one per instance)
float LodScaleOf( Instance instance, float3 eye )
{
	//Thinned with distance from the eye, which a flat view does not have.
	if ( instance.lodGroup == 0 || IsFlat( g_params[0].views[0] ) ) return instance.lodScale;

	uint group = min( instance.lodGroup - 1, MaxLodGroups - 1 );
	float4 span = g_params[0].lodSpans[group];

	float away = max( distance( eye, instance.sphere.xyz ) - instance.sphere.w, 0.0 );
	float at = saturate( ( away - span.x ) * span.y ) * float( LodCurveSamples - 1 );
	uint k = min( uint( at ), LodCurveSamples - 2 );

	uint a = ( group * LodCurveSamples ) + k;
	uint b = a + 1;
	float near = g_params[0].lodCurves[a >> 2][a & 3];
	float far = g_params[0].lodCurves[b >> 2][b & 3];

	return instance.lodScale * lerp( near, far, at - float( k ) );
}

//Coarsest chain level whose error fits at the box's nearest point; picked once per instance
uint ChainLevel( View view, MeshInfo mesh, float4x4 world, float grew, uint levels, float lodScale, bool shrinks )
{
	float allowed = view.lod.x * lodScale;

	float3 middle;
	float3 reach;
	WorldBox( world, mesh.boxMin.xyz, mesh.boxMax.xyz, 1.0, middle, reach );

	float deep = length( max( abs( view.eye.xyz - middle ) - reach, 0.0 ) );
	if ( shrinks && deep <= view.lod.y ) return 0u;

	float perMetre = shrinks ? view.eye.w / ( deep * FoveaScale( view, middle, length( reach ), false ) ) : view.eye.w;
	uint pick = 0;

	for ( uint l = 1; l < levels; ++l )
	{
		if ( mesh.level[l].y == 0 ) continue;
		if ( asfloat( mesh.level[l].z ) * grew * perMetre <= allowed ) pick = l;
	}

	return pick;
}

void CullInstance( uint index )
{
	Params params = g_params[0];

	//The last group's spare threads stand for no instance.
	if ( index >= params.instanceCount ) return;

	if ( g_debugOn != 0 && index == 0 )
	{
		Dbg( 0, params.instanceCount );
		Dbg( 1, params.viewCount );
		Dbg( 2, params.workCapacity );
		Dbg( 3, asuint( params.views[0].planes[0].x ) );
		Dbg( 4, asuint( params.views[0].planes[0].z ) );
		Dbg( 5, asuint( params.views[0].eye.y ) );
		Dbg( 6, asuint( params.views[0].screen.w ) );
		Dbg( 7, asuint( params.views[0].lod.w ) );
	}

	Instance instance = g_instances[index];

	uint code = 0;
	if ( g_debugOn != 0 && index == 1 )
	{
		Dbg( 8, asuint( instance.sphere.x ) );
		Dbg( 9, asuint( instance.sphere.y ) );
		Dbg( 10, asuint( instance.sphere.z ) );
		Dbg( 11, asuint( instance.sphere.w ) );
		Dbg( 12, instance.mesh );
		Dbg( 13, instance.flags );
		Dbg( 14, g_meshes[instance.mesh].clusterCount );
		Dbg( 15, g_meshes[instance.mesh].clusterFirst );
	}

	if ( ( instance.flags & InstanceDrawn ) == 0 ) { if ( g_debugOn != 0 ) Dbg( 64 + index, 0 ); return; }
	code |= 1;

	MeshInfo mesh = g_meshes[instance.mesh];
	if ( mesh.clusterCount == 0 ) { if ( g_debugOn != 0 ) Dbg( 64 + index, code ); return; }
	code |= 2;

	float4 sphere = instance.sphere;
	float radius = sphere.w * ( 1.0 + max( instance.cullGrow, 0.0 ) );

	//Cheap sphere-only frustum test first, so off-screen instances never read the world matrix
	bool wantedAnywhere = false;

	for ( uint pv = 0; pv < params.viewCount; ++pv )
	{
		View look = params.views[pv];

		if ( !Takes( look, instance, index ) ) continue;
		if ( !InsideTheFrustum( look, sphere.xyz, radius ) ) continue;

		wantedAnywhere = true;
		break;
	}

	if ( !wantedAnywhere ) { if ( g_debugOn != 0 ) Dbg( 64 + index, code ); return; }

	float4x4 world = WorldOf( instance );

	//One level of the model's chain for the whole instance: the camera's here, each light's below - see ChainLevel.
	//A model whose file carries no chain - one written before chains, cut on load - has level nought only.
	float grew = GrewBy( world );
	uint levels = min( mesh.levels, 8u );
	float lodScale = LodScaleOf( instance, params.views[0].eye.xyz );
	bool chained = ( mesh.flags & MeshChain ) != 0;
	uint chainShadow = ( mesh.flags >> MeshShadowShift ) & 3u;
	uint chainLevel = chained ? ChainLevel( params.views[0], mesh, world, grew, levels, lodScale, !IsFlat( params.views[0] ) ) : 0u;

	for ( uint v = 0; v < params.viewCount; ++v )
	{
		View view = params.views[v];
		bool camera = IsCamera( view );

		if ( !Takes( view, instance, index ) ) continue;
		if ( !InsideTheFrustum( view, sphere.xyz, radius ) ) continue;
		if ( v == 0 ) code |= 4;

		//A light's level: its own - a lamp's face from its own eye, the sun's by its texels - or the camera's for a posed thing,
		//whose shadow has to be cast by the shape that is drawn; made as many levels coarser as the model says its shadows are.
		bool posed = ( instance.flags & InstancePosed ) != 0;
		uint lit = chainLevel;

		if ( chained && !camera )
		{
			if ( !posed ) lit = ChainLevel( view, mesh, world, grew, levels, lodScale, PerspectiveLight( view ) );
			lit = min( lit + chainShadow, levels - 1u );
			while ( lit > 0u && mesh.level[lit].y == 0 ) --lit;
		}

		uint viewWant = 1u << lit;

		if ( viewWant == 0 ) continue;

		if ( camera )
		{
			if ( view.lod.w > 0.0 && PixelsAcross( view, sphere.xyz, radius ) < view.lod.w ) continue;
			if ( v == 0 ) code |= 8;

			//No depth test here: the pyramid is last frame's, and an instance dropped by it has no retest to come back by
			//- with the eye moving, what came into view blinked. Its clusters are tested, and those found hidden retested
			if ( v == 0 ) code |= 16;
		}
		if ( v == 0 ) code |= 32;

		for ( uint l = 0; l < levels; ++l )
		{
			if ( ( viewWant & ( 1u << l ) ) == 0 ) continue;

			uint first = mesh.level[l].x;
			uint held = mesh.level[l].y;

			//Whole 64-cluster runs become work items; leftovers go to a packed list so small meshes fill groups
			uint c = 0;

			for ( ; c + 64u <= held; c += 64u )
			{
				uint at = 0;
				InterlockedAdd( g_work[6].x, 1, at );
				if ( at >= params.workCapacity ) { InterlockedAdd( g_work[0].w, 1 ); return; }
				g_work[7 + at] = uint4( index, v, first + c, 64u );
				InterlockedAdd( g_work[0].x, 1 );
			}

			uint rest = held - c;

			if ( rest > 0 )
			{
				uint at = 0;
				InterlockedAdd( g_work[6].y, rest, at );

				//Fill the packed list as far as it fits, the rest become items; never leave counted slots unwritten
				uint fits = at < params.singlesCapacity ? min( rest, params.singlesCapacity - at ) : 0u;

				//A group of the same dispatch covers sixty-four of these, so one is claimed by whoever
				//writes its first slot. A run is shorter than a group, so that is at most one claim.
				for ( uint k = 0; k < fits; k++ )
				{
					g_work[params.singlesAt + at + k] = uint4( index, v, first + c + k, 1u );
					if ( ( ( at + k ) & 63u ) == 0u ) InterlockedAdd( g_work[0].x, 1 );
				}

				if ( fits < rest )
				{
					//No room in the packed list, which is a scene shaped unlike any measured. An item still draws it right, only with the lanes it used to waste.
					uint item = 0;
					InterlockedAdd( g_work[6].x, 1, item );
					if ( item >= params.workCapacity ) { InterlockedAdd( g_work[0].w, 1 ); return; }
					g_work[7 + item] = uint4( index, v, first + c + fits, rest - fits );
					InterlockedAdd( g_work[0].x, 1 );
				}
			}
		}
	}

	if ( g_debugOn != 0 ) Dbg( 64 + index, code );
}

//Records the camera's texel density demand per material for texture streaming
void Want( Params params, uint material, float grew, float3 eye, float3 middle, float reach )
{
	if ( params.wantAt == 0 ) return;

	uint word = asuint( grew / max( distance( eye, middle ) - reach, 0.25 ) );
	uint at = params.wantAt + material;

	if ( g_visible[at] < word ) InterlockedMax( g_visible[at], word );
}

void CullCluster( uint index, uint v, uint cluster, bool retest )
{
	Params params = g_params[0];
	Instance instance = g_instances[index];
	View view = params.views[v];
	View camera = params.views[0];
	bool isCamera = view.screen.w > 0.5;

	ClusterInfo held = g_clusters[cluster];
	uint part = held.words.w & 0xFFFFu;
	uint material = g_materialData[params.materialsAt + instance.materialFirst + part];

	//Skip materials the frame lacks; their bin would overwrite other bins' draw arguments
	if ( material >= params.materials ) return;

	uint flags = g_materialData[material * 4];

	if ( g_debugOn != 0 && index == 0 ) { Dbg( 16, flags ); Dbg( 17, material ); Dbg( 18, part ); Dbg( 19, asuint( held.boxMinError.w ) ); Dbg( 20, asuint( held.boxMaxParentError.w ) ); Dbg( 21, held.words.w ); }
	if ( ( flags & ( MaterialTranslucent | MaterialLoose ) ) != 0 ) return;

	//Posed clusters store the unposed shape, so skip per-cluster sphere, cone and depth tests
	if ( ( instance.flags & InstancePosed ) != 0 )
	{
		if ( retest ) return;

		float4x4 posedWorld = WorldOf( instance );

		uint posedKind = ( flags & MaterialAlphaTest ) != 0 ? 2 : ( flags & MaterialTwoSided ) != 0 ? 1 : 0;
		uint posedBase = v == 0 ? 0 : params.cameraLists + ( ( v - 1 ) * 3 );
		float3 posedMiddle = mul( float4( held.sphere.xyz, 1.0 ), posedWorld ).xyz;
		float posedGrew = GrewBy( posedWorld );

		if ( v == 0 ) Want( params, material, posedGrew, camera.eye.xyz, posedMiddle, held.sphere.w * posedGrew );

		Append( posedBase + posedKind, index, cluster, material, held.words,
			ClusterSlice( camera, posedMiddle, params.binSlices ) );

		return;
	}

	float4x4 world = WorldOf( instance );
	float grew = GrewBy( world );
	float allowance = 1.0 + max( instance.cullGrow, 0.0 );

	float3 middle = mul( float4( held.sphere.xyz, 1.0 ), world ).xyz;
	float reach = held.sphere.w * grew * allowance;

	if ( !InsideTheFrustum( view, middle, reach ) ) return;

	bool twoSided = ( flags & MaterialTwoSided ) != 0;
	bool alpha = ( flags & MaterialAlphaTest ) != 0;

	if ( isCamera && !twoSided && ( instance.flags & InstanceSquare ) != 0 &&
		FacesAway( middle, reach, normalize( mul( float4( held.cone.xyz, 0.0 ), world ).xyz ), held.cone.w, view.eye.xyz ) )
		return;

	uint kind = alpha ? 2 : twoSided ? 1 : 0;
	uint listBase = v == 0 ? ( retest ? 3 : 0 ) : params.cameraLists + ( ( v - 1 ) * 3 );

	if ( isCamera && view.hzb.w > 0.5 )
	{
		float3 boxMiddle;
		float3 boxReach;
		WorldBox( world, held.boxMinError.xyz, held.boxMaxParentError.xyz, allowance, boxMiddle, boxReach );

		if ( BehindTheDepth( view, boxMiddle, boxReach ) )
		{
			if ( !retest )
			{
				ListInfo info = g_params[0].lists[params.retestList];
				uint at = 0;
				InterlockedAdd( g_visible[( params.retestList * 4 ) + 1], 1, at );
				if ( at < info.capacity )
				{
					g_visible[info.base + ( at * 2 )] = index;
					g_visible[info.base + ( at * 2 ) + 1] = cluster;
					InterlockedMax( g_work[1].x, ( at + 64 ) / 64 );
				}
			}
			return;
		}
	}

	if ( v == 0 ) Want( params, material, grew, camera.eye.xyz, middle, reach );

	Append( listBase + kind, index, cluster, material, held.words, ClusterSlice( camera, middle, params.binSlices ) );
}

//Whether this group is the last of its dispatch to get here - see main, where it is worked out in place: one count
//a dispatch, and the group that brings it to the dispatch's size is the one every other group has finished ahead of.
groupshared uint s_last;

//The materials' counts folded across the group - see the finish - and their triangles beside them.
groupshared uint s_scan[64];
groupshared uint s_scanTris[64];

//How many entries the lights' lists hold between them - the bin pass fills those after the camera's, so its dispatch is sized by both.
groupshared uint s_lightEntries;

//How many threads of the bin pass write one entry's triangles - see there.
static const uint BinLanes = 8;

//Where the entry each eight threads of the bin share sends its triangles, and from where: what the one that found it tells the rest.
groupshared uint s_binTriangles[64 / BinLanes];
groupshared uint s_binFirst[64 / BinLanes];
groupshared uint s_binCorner[64 / BinLanes];
groupshared uint s_binSource[64 / BinLanes];

//The bin pass's groups for this many entries: one a BinLanes-th of sixty-four of them, folded along y past what one axis may name.
uint4 BinGroups( uint entries )
{
	uint groups = ( entries + ( 64u / BinLanes ) - 1u ) / ( 64u / BinLanes );
	return uint4( min( groups, 65535u ), ( groups + 65534u ) / 65535u, 1, 0 );
}
//The instance at <c>at</c> of the cells' run of instances.
uint CellInstance( uint at )
{
	uint index = ( g_work[5].w * 2 ) + ( at >> 2 );
	return g_cells[index][at & 3u];
}

//Culls grid cells per view and hands visible ones to the instance pass; small cells are packed
void CullCells( uint cell )
{
	Params params = g_params[0];
	if ( cell >= g_work[5].w ) return;

	uint4 sphereBits = g_cells[cell * 2];
	uint4 run = g_cells[( cell * 2 ) + 1];
	float3 middle = asfloat( sphereBits.xyz );
	float radius = asfloat( sphereBits.w );

	bool seen = radius < 0.0;

	for ( uint v = 0; v < params.viewCount && !seen; ++v )
	{
		View view = params.views[v];
		//The frustum only: last frame's depth would drop a cell with no retest to bring it back - see CullInstance
		if ( !InsideTheFrustum( view, middle, radius ) ) continue;
		seen = true;
	}

	if ( !seen || run.y == 0 ) return;

	uint at = 0;

	if ( run.y >= 64u )
	{
		InterlockedAdd( g_work[params.cellsAt].x, 1, at );
		g_work[params.cellsAt + 1 + at] = uint4( run.x, run.y, cell, 0 );
		InterlockedAdd( g_work[5].x, 1 );
		return;
	}

	InterlockedAdd( g_work[params.cellsAt].y, run.y, at );

	uint packedAt = params.cellsAt + 1 + g_work[5].w;

	for ( uint i = 0; i < run.y; ++i )
	{
		uint slot = at + i;
		g_work[packedAt + ( slot >> 2 )][slot & 3u] = CellInstance( run.x + i );
		if ( ( slot & 63u ) == 0u ) InterlockedAdd( g_work[5].x, 1 );
	}
}

[shader("compute")]
[numthreads(64, 1, 1)]
void main( uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint thread : SV_GroupIndex )
{
	if ( g_pass == 4 )
	{
		CullCells( id.x );
		return;
	}

	if ( g_pass == 0 )
	{
		//A cell a group, its instances sixty-four at a time - or, with no cells (g_pyramidA nought), an instance a thread across the table.
		if ( g_pyramidA == 0 )
		{
			CullInstance( id.x );
			return;
		}

		uint cellsAt = g_params[0].cellsAt;
		uint4 counts = g_work[cellsAt];

		if ( group.x < counts.x )
		{
			uint4 run = g_work[cellsAt + 1 + group.x];

			for ( uint i = thread; i < run.y; i += 64u )
				CullInstance( CellInstance( run.x + i ) );

			return;
		}

		uint slot = ( ( group.x - counts.x ) * 64u ) + thread;
		if ( slot < counts.y ) CullInstance( g_work[cellsAt + 1 + g_work[5].w + ( slot >> 2 )][slot & 3u] );

		return;
	}

	if ( g_pass == 3 )
	{
		//Bins camera and light list entries into material runs; eight threads per entry write triangles
		Params params = g_params[0];

		uint mine = thread / BinLanes;
		uint lane = thread % BinLanes;
		uint which = ( ( ( group.y * 65535u ) + group.x ) * ( 64u / BinLanes ) ) + mine;

		if ( lane == 0 )
		{
			uint writing = 0;
			uint firstIndex = 0;
			uint corner = 0;
			uint source = 0;
			bool found = false;

			for ( uint list = 0; list < params.retestList && !found; ++list )
			{
				ListInfo info = params.lists[list];
				uint count = min( g_visible[( list * 4 ) + 1], info.capacity );

				if ( which >= count )
				{
					which -= count;
					continue;
				}

				found = true;

				uint index = g_visible[info.base + ( which * 2 )];
				uint packed = g_visible[info.base + ( which * 2 ) + 1];

				uint cluster = ClusterOf( packed );

				//Its triangles found no room in its list's run - see Append - so the depth has nothing of it and neither does any bin.
				if ( ( packed & ClusterDropped ) != 0 ) break;

				Instance instance = g_instances[index];
				ClusterInfo held = g_clusters[cluster];
				uint material = g_materialData[params.materialsAt + instance.materialFirst + ( held.words.w & 0xFFFFu )];

				//Never counted - see CullCluster - so never filled.
				if ( material >= params.materials ) break;

				//The slice the cull put it in - read back rather than worked out again, because the two answers have to be the same one.
				uint bin = ( material * max( params.binSlices, 1u ) ) + SliceOf( packed );

				uint at = 0;
				InterlockedAdd( g_visible[params.binFillAt + bin], 1, at );

				//Its place in the binned list, and no further than the list has room for. Every count that is right fits: a bin only counts entries a list kept.
				uint slot = g_visible[params.binOffsetsAt + bin] + at;
				if ( slot >= params.binnedCapacity ) break;

				uint to = params.binnedAt + ( slot * 2 );
				g_visible[to] = index;
				g_visible[to + 1] = cluster;

				//Write triangles into the bin's run, bounded by this bin's room so it can't spill into the next bin
				uint triangles = held.words.z >> 16;
				uint room = Counted( params.binTrisAt + bin );
				uint tri = 0;
				InterlockedAdd( g_visible[params.binTriFillAt + bin], triangles, tri );

				//Past the end of the bin's room: nothing of this cluster is drawn. Its pair is written and no index names it, which is what makes that safe.
				if ( tri >= room ) break;

				uint first = ( params.binIndexAt + g_visible[params.binTriOffsetsAt + bin] + tri ) * 3;

				//Pad the straddling cluster with degenerate triangles so the draw never reads stale indices
				if ( tri + triangles > room )
				{
					uint blank = slot << EntryShift;

					for ( uint t = tri; t < room; t++ )
					{
						uint put = first + ( ( t - tri ) * 3 );
						g_indices[put] = blank;
						g_indices[put + 1] = blank;
						g_indices[put + 2] = blank;
					}

					break;
				}

				writing = triangles;
				corner = slot << EntryShift;
				firstIndex = first;
				source = held.words.y;
			}

			//Then the lights' lists: its triangles into its list's share of the lights' run - where the finish laid the list, and how far into it the
			//list has got - each corner naming the entry by its place in its own list, which is what the list's draw reads its pairs back by. See Append.
			for ( uint light = params.cameraLists; light < params.listTotal && !found; ++light )
			{
				ListInfo info = params.lists[light];
				uint count = min( g_visible[( light * 4 ) + 1], info.capacity );

				if ( which >= count )
				{
					which -= count;
					continue;
				}

				found = true;

				uint cluster = ClusterOf( g_visible[info.base + ( which * 2 ) + 1] );
				ClusterInfo held = g_clusters[cluster];
				uint triangles = held.words.z >> 16;

				uint tri = 0;
				InterlockedAdd( g_visible[( light * 4 ) + 3], triangles, tri );
				tri += g_visible[light * 4];
				if ( tri + triangles > params.shadowIndexCapacity ) break;

				writing = triangles;
				corner = which << EntryShift;
				firstIndex = ( params.shadowIndexAt + tri ) * 3;
				source = held.words.y;
			}

			s_binTriangles[mine] = writing;
			s_binFirst[mine] = firstIndex;
			s_binCorner[mine] = corner;
			s_binSource[mine] = source;
		}

		GroupMemoryBarrierWithGroupSync();

		uint triangles = s_binTriangles[mine];
		uint firstIndex = s_binFirst[mine];
		uint corner = s_binCorner[mine];
		uint source = s_binSource[mine];

		for ( uint t = lane; t < triangles; t += BinLanes )
		{
			uint from = ( source + t ) * 3;
			uint put = firstIndex + ( t * 3 );
			g_indices[put] = corner | TriangleByte( g_clusterTriangles, from );
			g_indices[put + 1] = corner | TriangleByte( g_clusterTriangles, from + 1 );
			g_indices[put + 2] = corner | TriangleByte( g_clusterTriangles, from + 2 );
		}

		return;
	}

	if ( g_pass == 1 )
	{
		//Low groups take work items, high groups take packed clusters; counts clamped to capacity
		uint items = min( g_work[6].x, g_params[0].workCapacity );

		if ( group.x < items )
		{
			uint4 item = g_work[7 + group.x];
			if ( thread < item.w ) CullCluster( item.x, item.y, item.z + thread, false );
		}
		else
		{
			uint at = ( ( group.x - items ) * 64u ) + thread;

			if ( at < min( g_work[6].y, g_params[0].singlesCapacity ) )
			{
				uint4 one = g_work[g_params[0].singlesAt + at];
				CullCluster( one.x, one.y, one.z, false );
			}
		}

		//Every lane is back from its cluster: what they claimed, the wave writes together - see WriteClaimed.
		WriteClaimed();

		//No early return above this: the whole group meets here, and everything it wrote is out before it says it is done.
		DeviceMemoryBarrierWithGroupSync();

		if ( thread == 0 )
		{
			uint done = 0;
			InterlockedAdd( g_work[1].w, 1, done );
			s_last = done + 1 == g_work[0].x ? 1u : 0u;
		}

		GroupMemoryBarrierWithGroupSync();

		//And the last group sees everything the others wrote. Every group waits here, not only the last. What it sees
		//is ordered, not fresh: the words the groups counted into are read through Counted, never plain - see there.
		DeviceMemoryBarrierWithGroupSync();

		if ( s_last != 0 )
		{
			//List counts to draw workgroups, plus the second pyramid dispatch; inline, Slang hangs on helpers
			Params finished = g_params[0];

			if ( thread == 0 )
			{
				uint appeared = Counted( ( 3 * 4 ) + 1 ) + Counted( ( 4 * 4 ) + 1 ) + Counted( ( 5 * 4 ) + 1 );
				bool any = appeared != 0;
				g_work[2] = any ? uint4( g_pyramidA & 0xFFFFu, g_pyramidA >> 16, 1, 0 ) : uint4( 0, 0, 0, 0 );
				g_work[3] = any ? uint4( g_pyramidB & 0xFFFFu, g_pyramidB >> 16, 1, 0 ) : uint4( 0, 0, 0, 0 );

				//The lights' lists end to end in the run they share: where each begins in it, its draw, and how many entries they hold
				//between them for the bin pass to fill. One thread, because each list starts where the one before it ended - see Append.
				uint running = 0;
				uint lightEntries = 0;

				for ( uint light = finished.cameraLists; light < finished.listTotal; ++light )
				{
					uint tris = Counted( ( light * 4 ) + 2 );
					tris = running < finished.shadowIndexCapacity ? min( tris, finished.shadowIndexCapacity - running ) : 0u;

					g_visible[light * 4] = running;

					uint draw = finished.drawArgsAt + ( light * 5 );
					g_visible[draw] = tris * 3;
					g_visible[draw + 1] = 1;
					g_visible[draw + 2] = ( finished.shadowIndexAt + running ) * 3;
					g_visible[draw + 3] = 0;
					g_visible[draw + 4] = 0;

					running += tris;

					uint entries = min( Counted( ( light * 4 ) + 1 ), finished.lists[light].capacity );

					//And as mesh-stage workgroups, one an entry - see MainMs. Where those are what draws it, the bin pass has no indices to fill.
					uint mesh = finished.meshArgsAt + ( light * 4 );
					g_visible[mesh] = min( entries, 65535u );
					g_visible[mesh + 1] = ( entries + 65534u ) / 65535u;
					g_visible[mesh + 2] = 1;

					if ( ( finished.meshDepth & 8u ) == 0 ) lightEntries += entries;
				}

				s_lightEntries = lightEntries;
			}

			//The camera's lists' indexed draws: each list's run of the index buffer as far as the cull
			//filled it. What appeared starts where the first half of its kind ended - see Append.
			for ( uint list = thread; list < finished.retestList; list += 64 )
			{
				ListInfo info = finished.lists[list];
				uint start = list >= 3 ? min( Counted( ( ( list - 3 ) * 4 ) + 2 ), info.indexCapacity ) : 0;
				uint tris = min( Counted( ( list * 4 ) + 2 ), info.indexCapacity - start );
				uint draw = finished.drawArgsAt + ( list * 5 );
				g_visible[draw] = tris * 3;
				g_visible[draw + 1] = 1;
				g_visible[draw + 2] = ( info.indexBase + start ) * 3;
				g_visible[draw + 3] = 0;
				g_visible[draw + 4] = 0;

				//And the same list as mesh-stage workgroups, one a packed entry, folded along
				//y past what one axis may name - see Append and MainMs in mesh_raster.slang.
				uint groups = min( Counted( ( list * 4 ) + 2 ), info.indexCapacity );
				uint mesh = finished.meshArgsAt + ( list * 4 );
				g_visible[mesh] = min( groups, 65535u );
				g_visible[mesh + 1] = ( groups + 65534u ) / 65535u;
				g_visible[mesh + 2] = 1;
			}
		}

		//Material offsets into the binned list via a group-wide prefix sum; all groups hit each barrier
		{
			Params finished = g_params[0];
			bool last = s_last != 0;
			uint per = ( finished.bins + 63 ) / 64;
			uint slices = max( finished.binSlices, 1u );
			uint from = thread * per;
			uint to = min( from + per, finished.bins );
			uint sum = 0;
			uint sumTris = 0;
			if ( last )
			{
				for ( uint m = from; m < to; ++m )
				{
					sum += Counted( finished.binCountsAt + m );
					sumTris += Counted( finished.binTrisAt + m );
				}
			}
			s_scan[thread] = sum;
			s_scanTris[thread] = sumTris;
			GroupMemoryBarrierWithGroupSync();
			for ( uint step = 1; step < 64; step <<= 1 )
			{
				uint add = thread >= step ? s_scan[thread - step] : 0;
				uint addTris = thread >= step ? s_scanTris[thread - step] : 0;
				GroupMemoryBarrierWithGroupSync();
				s_scan[thread] += add;
				s_scanTris[thread] += addTris;
				GroupMemoryBarrierWithGroupSync();
			}
			if ( last )
			{
				uint running = thread == 0 ? 0 : s_scan[thread - 1];
				uint runningTris = thread == 0 ? 0 : s_scanTris[thread - 1];
				for ( uint m = from; m < to; ++m )
				{
					//The bin's pairs, and its run of the index buffer - as many triangles as its clusters were counted with, which is what the bin
					//pass fills, so the draw is the whole run. Cut to the room the binned runs have, which every count that is right fits inside.
					uint count = Counted( finished.binCountsAt + m );
					uint tris = Counted( finished.binTrisAt + m );
					tris = runningTris < finished.binIndexCapacity ? min( tris, finished.binIndexCapacity - runningTris ) : 0u;
					g_visible[finished.binOffsetsAt + m] = running;
					g_visible[finished.binTriOffsetsAt + m] = runningTris;
					
					//What this slice was cut down to, written back over what the cull asked for: a material's draw below is the whole of
					//its slices, and the number it adds up has to be the one the offsets were laid out by, not the one that did not fit.
					g_visible[finished.binTrisAt + m] = tris;
					
					running += count;
					runningTris += tris;
				}

				//And each material's draws, now that every slice of it has its place: spans of one run, from the first triangle of the nearest slice of a span to the
				//end of its farthest - see binDraws. Behind a barrier, because a material's slices are spread over several of the sixty-four threads that laid them out.
				DeviceMemoryBarrierWithGroupSync();
				
				for ( uint mat = thread; mat < finished.materials; mat += 64 )
				{
					uint firstSlice = mat * slices;
					uint draws = max( finished.binDraws, 1u );

					//A call a span of the run, the slices cut into as many spans as the frame asked for - see binDraws. A span is the
					//triangles of its own slices and nothing else, so the spans lie end to end over exactly the run one call would have drawn.
					for ( uint span = 0; span < draws; ++span )
					{
						uint from = ClusterSpanFrom( span, draws, slices, ClusterFarSlice );
						uint upto = span + 1u == draws ? slices
							: ClusterSpanFrom( span + 1u, draws, slices, ClusterFarSlice );

						uint draw = finished.binArgsAt + ( ( ( mat * draws ) + span ) * 5 );

						uint firstTri = 0;
						uint triangles = 0;

						if ( from < upto )
						{
							firstTri = Counted( finished.binTriOffsetsAt + firstSlice + from );
							uint last = firstSlice + upto - 1u;
							triangles = ( Counted( finished.binTriOffsetsAt + last ) + Counted( finished.binTrisAt + last ) ) - firstTri;
						}

						g_visible[draw] = triangles * 3;
						g_visible[draw + 1] = 1;
						g_visible[draw + 2] = ( finished.binIndexAt + firstTri ) * 3;
						g_visible[draw + 3] = 0;
						g_visible[draw + 4] = 0;
					}
				}

				//A thread an entry: the camera's, binned, and then the lights', filled - see the bin pass.
				if ( thread == 63 ) g_work[4] = BinGroups( s_scan[63] + s_lightEntries );
			}
		}

		return;
	}

	Params params = g_params[0];
	ListInfo retest = params.lists[params.retestList];
	uint count = min( g_visible[( params.retestList * 4 ) + 1], retest.capacity );

	if ( id.x < count )
	{
		uint instance = g_visible[retest.base + ( id.x * 2 )];
		uint cluster = g_visible[retest.base + ( id.x * 2 ) + 1];
		CullCluster( instance, 0, cluster, true );
	}

	//And what the retest claimed, the same way.
	WriteClaimed();

	DeviceMemoryBarrierWithGroupSync();

	if ( thread == 0 )
	{
		uint done = 0;
		InterlockedAdd( g_work[3].w, 1, done );
		s_last = done + 1 == g_work[1].x ? 1u : 0u;
	}

	GroupMemoryBarrierWithGroupSync();
	DeviceMemoryBarrierWithGroupSync();

	if ( s_last != 0 )
	{
		//List counts to draw workgroups, plus the second pyramid dispatch; inline, Slang hangs on helpers
		Params finished = g_params[0];

		if ( thread == 0 )
		{
			uint appeared = Counted( ( 3 * 4 ) + 1 ) + Counted( ( 4 * 4 ) + 1 ) + Counted( ( 5 * 4 ) + 1 );
			bool any = appeared != 0;
			g_work[2] = any ? uint4( g_pyramidA & 0xFFFFu, g_pyramidA >> 16, 1, 0 ) : uint4( 0, 0, 0, 0 );
			g_work[3] = any ? uint4( g_pyramidB & 0xFFFFu, g_pyramidB >> 16, 1, 0 ) : uint4( 0, 0, 0, 0 );

			//The same as the first pass's finish - see there. The lights' lists do not change in the retest, which
			//is the camera's alone, and are laid out again only because this is where the draws are written last.
			uint running = 0;
			uint lightEntries = 0;

			for ( uint light = finished.cameraLists; light < finished.listTotal; ++light )
			{
				uint tris = Counted( ( light * 4 ) + 2 );
				tris = running < finished.shadowIndexCapacity ? min( tris, finished.shadowIndexCapacity - running ) : 0u;

				g_visible[light * 4] = running;

				uint draw = finished.drawArgsAt + ( light * 5 );
				g_visible[draw] = tris * 3;
				g_visible[draw + 1] = 1;
				g_visible[draw + 2] = ( finished.shadowIndexAt + running ) * 3;
				g_visible[draw + 3] = 0;
				g_visible[draw + 4] = 0;

				running += tris;

				uint entries = min( Counted( ( light * 4 ) + 1 ), finished.lists[light].capacity );

				//And as mesh-stage workgroups - see the first finish.
				uint mesh = finished.meshArgsAt + ( light * 4 );
				g_visible[mesh] = min( entries, 65535u );
				g_visible[mesh + 1] = ( entries + 65534u ) / 65535u;
				g_visible[mesh + 2] = 1;

				if ( ( finished.meshDepth & 8u ) == 0 ) lightEntries += entries;
			}

			s_lightEntries = lightEntries;
		}

		//The camera's lists' indexed draws - the same as the first pass's finish, see there.
		for ( uint list = thread; list < finished.retestList; list += 64 )
		{
			ListInfo info = finished.lists[list];
			uint start = list >= 3 ? min( Counted( ( ( list - 3 ) * 4 ) + 2 ), info.indexCapacity ) : 0;
			uint tris = min( Counted( ( list * 4 ) + 2 ), info.indexCapacity - start );
			uint draw = finished.drawArgsAt + ( list * 5 );
			g_visible[draw] = tris * 3;
			g_visible[draw + 1] = 1;
			g_visible[draw + 2] = ( info.indexBase + start ) * 3;
			g_visible[draw + 3] = 0;
			g_visible[draw + 4] = 0;

			//And the same list as mesh-stage workgroups - see the first finish.
			uint groups = min( Counted( ( list * 4 ) + 2 ), info.indexCapacity );
			uint mesh = finished.meshArgsAt + ( list * 4 );
			g_visible[mesh] = min( groups, 65535u );
			g_visible[mesh + 1] = ( groups + 65534u ) / 65535u;
			g_visible[mesh + 2] = 1;
		}
	}

	//Material offsets into the binned list via a group-wide prefix sum; all groups hit each barrier
	{
		Params finished = g_params[0];
		bool last = s_last != 0;
		uint per = ( finished.bins + 63 ) / 64;
		uint slices = max( finished.binSlices, 1u );
		uint from = thread * per;
		uint to = min( from + per, finished.bins );
		uint sum = 0;
		uint sumTris = 0;
		if ( last )
		{
			for ( uint m = from; m < to; ++m )
			{
				sum += Counted( finished.binCountsAt + m );
				sumTris += Counted( finished.binTrisAt + m );
			}
		}
		s_scan[thread] = sum;
		s_scanTris[thread] = sumTris;
		GroupMemoryBarrierWithGroupSync();
		for ( uint step = 1; step < 64; step <<= 1 )
		{
			uint add = thread >= step ? s_scan[thread - step] : 0;
			uint addTris = thread >= step ? s_scanTris[thread - step] : 0;
			GroupMemoryBarrierWithGroupSync();
			s_scan[thread] += add;
			s_scanTris[thread] += addTris;
			GroupMemoryBarrierWithGroupSync();
		}
		if ( last )
		{
			uint running = thread == 0 ? 0 : s_scan[thread - 1];
			uint runningTris = thread == 0 ? 0 : s_scanTris[thread - 1];
			for ( uint m = from; m < to; ++m )
			{
				//The same as the first pass's finish - see there.
				uint count = Counted( finished.binCountsAt + m );
				uint tris = Counted( finished.binTrisAt + m );
				tris = runningTris < finished.binIndexCapacity ? min( tris, finished.binIndexCapacity - runningTris ) : 0u;
				g_visible[finished.binOffsetsAt + m] = running;
				g_visible[finished.binTriOffsetsAt + m] = runningTris;
				
				//What this slice was cut down to, written back over what the cull asked for: a material's draw below is the whole of
				//its slices, and the number it adds up has to be the one the offsets were laid out by, not the one that did not fit.
				g_visible[finished.binTrisAt + m] = tris;
				
				running += count;
				runningTris += tris;
			}

			//And each material's draws, now that every slice of it has its place: spans of one run, from the first triangle of the nearest slice of a span to the
			//end of its farthest - see binDraws. Behind a barrier, because a material's slices are spread over several of the sixty-four threads that laid them out.
			DeviceMemoryBarrierWithGroupSync();
			
			for ( uint mat = thread; mat < finished.materials; mat += 64 )
			{
				uint firstSlice = mat * slices;
				uint draws = max( finished.binDraws, 1u );

				//A call a span of the run, the slices cut into as many spans as the frame asked for - see binDraws. A span is the
				//triangles of its own slices and nothing else, so the spans lie end to end over exactly the run one call would have drawn.
				for ( uint span = 0; span < draws; ++span )
				{
					uint from = ClusterSpanFrom( span, draws, slices, ClusterFarSlice );
					uint upto = span + 1u == draws ? slices
						: ClusterSpanFrom( span + 1u, draws, slices, ClusterFarSlice );

					uint draw = finished.binArgsAt + ( ( ( mat * draws ) + span ) * 5 );

					uint firstTri = 0;
					uint triangles = 0;

					if ( from < upto )
					{
						firstTri = Counted( finished.binTriOffsetsAt + firstSlice + from );
						uint last = firstSlice + upto - 1u;
						triangles = ( Counted( finished.binTriOffsetsAt + last ) + Counted( finished.binTrisAt + last ) ) - firstTri;
					}

					g_visible[draw] = triangles * 3;
					g_visible[draw + 1] = 1;
					g_visible[draw + 2] = ( finished.binIndexAt + firstTri ) * 3;
					g_visible[draw + 3] = 0;
					g_visible[draw + 4] = 0;
				}
			}

			if ( thread == 63 ) g_work[4] = BinGroups( s_scan[63] + s_lightEntries );
		}
	}
}
