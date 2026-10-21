using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten;

//Draws one viewport's meshes: one cull for all views, ID/depth raster, then shade by material
internal sealed class CoreMeshRender
{
	//One view the cull decides for: the camera, or a light's - a bake of the sun's pages, a zone of the map of what moves.
	internal struct ViewSetup
	{
		public System.Numerics.Vector4 Plane0, Plane1, Plane2, Plane3, Plane4, Plane5;
		public System.Numerics.Matrix4x4 WorldToClip;
		public Vector3 Eye;
		public bool Camera;

		/// <summary>A light's view: which instances it takes - see ViewCasters in the cull. One is the still ones, two the ones that move.</summary>
		public int Casters;

		/// <summary>A view that shrinks with distance: a lamp's face, which cuts its levels as the camera does, from <see cref="Eye"/> - see PerspectiveLight in the cull - and a camera that is not a flat one. A flat camera reads a metre as the same pixels at every depth; see IsFlat in the cull.</summary>
		public bool Perspective;

		/// <summary>How much error a metre is read as: pixels at a metre away for the camera, texels for a light's view, whose projection does not shrink with distance.</summary>
		public float ErrorScale;
		public float LodPixels;
		public float Near;
		public float Far;
		public float CullPixels;
		public float HalfHeight;
		public float ScaleX;
		public float ScaleY;

		/// <summary>A headset's gaze, the camera's only: the direction in xyz, and in w how much coarser the cut may be away from it - see FoveaScale in the cull. Nought for every view that is not an eye.</summary>
		public System.Numerics.Vector4 Fovea;

		public void SetPlanes( ReadOnlySpan<System.Numerics.Vector4> planes )
		{
			Plane0 = planes.Length > 0 ? planes[0] : default;
			Plane1 = planes.Length > 1 ? planes[1] : default;
			Plane2 = planes.Length > 2 ? planes[2] : default;
			Plane3 = planes.Length > 3 ? planes[3] : default;
			Plane4 = planes.Length > 4 ? planes[4] : default;
			Plane5 = planes.Length > 5 ? planes[5] : default;
		}
	}

	//What else a light's view draws into its map beside its clusters: the draws made the classic way that cast
	//into it - see CoreSceneDrawer.DrawLooseView. Called with the shadow layer open and its area cleared.
	internal delegate void LooseView( in ViewSetup view );

	//The three lists one view draws: one-sided, two-sided, and cut out by alpha.
	internal const int ListsPerView = 3;

	//The camera's lists: three for the first half, three for what appeared, and the ones put aside for the depth.
	internal const int CameraLists = 7;
	internal const int RetestList = 6;

	private const int ViewWords = 60;
	private const int ListWords = 4;
	//Words of the cull's parameters before the views begin. The struct is positional and written by index, so this, the shader's Params
	//and VisParams in cluster_main.slang have to agree word for word - see the note on `bins` - and it has to be a whole number of float4s.
	private const int HeaderWords = 40;

	//Max triangles for the camera's lists, same again for movers; overflow leaves holes, logged in Begin
	private const long MostListTriangles = 1L << 23;
	private const int ParamsWords = LodGroupsAt + Rinten.Rendering.LodGroups.Words;

	//Where the level-of-detail groups' baked curves begin, after the lists - see Rendering.LodGroups and
	//lodCurves in mesh_tables.slang. A whole number of float4s from the start, as the card lays them.
	private const int LodGroupsAt = HeaderWords + ( CoreMeshShaders.MaxViews * ViewWords ) + ( CoreMeshShaders.MaxLists * ListWords );
	//The work buffer's header, in words: the cluster pass's dispatch, the retest's, the second depth pyramid's
	//two - see PyramidArgsOffset - the bin's, the packed clusters' dispatch, and the two counts the passes share.
	private const int WorkHeaderWords = 28;


	//Where in the work buffer the bin's dispatch arguments are, in bytes.
	private const int BinArgsOffset = 64;

	//Where the instance pass's dispatch is in the work buffer - g_work[5], counted by the cell pass.
	private const int CellArgsOffset = 80;

	//How many frames running each list buffer has been far larger than wanted - see Reach.
	private static readonly Dictionary<ulong, int> oversized = new();

	//How many cells this frame's cull walks - see CoreInstanceCells - and where its lists are in the work buffer, in uint4s.
	private int cellCount;
	private long cellsAt;

	//How many instances a table has before the cull walks it by cells - see Begin.
	private const int CellsFrom = 32768;

	//Where in the work buffer the second pyramid's dispatch arguments are, in bytes: one uint4 a launch, written by the finish
	//pass with the pyramid's own group counts where anything appeared and zeros where nothing did. See CoreSceneDrawer.KeepDepth.
	internal const int PyramidArgsOffset = 32;

	//The work buffer, for the pyramid's indirect dispatch.
	internal ulong WorkBuffer => workBuffer;

	private static ulong cullPipeline;
	private static bool pipelinesTried;
	//The pass that poses a whole model - see shaders/engine/mesh_skin.slang.
	private static ulong skinPipeline;
	private readonly ulong[] skinBound = new ulong[7];
	private readonly byte[] skinPush = new byte[32];

	//The pass that adds one blend shape into an instance's sums - see shaders/engine/mesh_shapes.slang.
	private static ulong shapesPipeline;

	//The pass that lays the next frame's shading rates off this one's picture - see shaders/engine/shading_rate.slang.
	private static ulong ratePipeline;
	private readonly ulong[] shapesBound = new ulong[2];
	private readonly byte[] shapesPush = new byte[20];
	//The camera's pre-pass: the depth alone, back faces culled; the two-sided and cut-out lists draw with a light's.
	private static ulong prepassPipeline;
	private static ulong depthPipeline, depthAlphaPipeline;
	private static ulong viewPipeline;

	private readonly uint[] paramsWords = new uint[ParamsWords];
	private ulong paramsBuffer;
	private ulong workBuffer;
	private long workCapacity;
	private ulong visibleBuffer;
	private long visibleWords;

	//The corners the clusters are drawn by, written by the cull: each of the camera's three kinds of list
	//has a run of it, and the bins a run each behind them. See Append in shaders/engine/mesh_cull.slang.
	private ulong indexBuffer;
	private long indexWords;
	private long saidIndexWanted;

	//Where the debug words are in the visible buffer, under RINTEN_MESH_DEBUG.
	private int debugAt;

	//The buffers a pass binds - ten for the cull, the first eight for a draw.
	private readonly ulong[] bound = new ulong[10];
	private readonly byte[] push = new byte[16];
	private readonly byte[] visPush = new byte[CoreSceneRenderer.PushSize];

	private CoreSceneTables tables;
	private int viewCount;
	private int listCount;

	//Every list the frame has room for - the cull's. The counters of list l are at l * 4 and its indexed draw at drawArgsAt + l * 5 .
	private int listTotal;
	private int entriesAt;
	private int cameraEntries;
	//Calls per material run, split by distance; later calls use the far-shading pass (kFarShading)
	private const int DrawsAMaterial = 2;

	private readonly int[] listBase = new int[CoreMeshShaders.MaxLists];
	private readonly int[] listCapacity = new int[CoreMeshShaders.MaxLists];
	private readonly int[] listView = new int[CoreMeshShaders.MaxLists];
	private readonly int[] listKind = new int[CoreMeshShaders.MaxLists];
	private (int Width, int Height) pixels;
	private int materials;

	internal static long Culls, ListDraws, MaterialDraws, Skins;

	//Whether the last Begin left anything to draw.
	internal bool Ready { get; private set; }

	//Compiles the passes once for the process; false where the card cannot run them.
	internal static bool EnsurePipelines()
	{
		if ( !Rinten.Rendering.DrawSettings.Clusters ) return false;
		if ( cullPipeline != 0 ) return true;
		if ( pipelinesTried ) return false;

		//The portable frame computes nothing, draws nothing this way and is not worth a warning about every pass in turn - see CoreGeometryPool.Place.
		if ( !CoreNative.Available || !CoreRenderDevice.FirstInstance || CoreRenderDevice.PortableFrame ) return false;

		pipelinesTried = true;

		var cull = CoreShaderCache.Pass( EngineShaders.MeshCull, "main", CoreShaderCompiler.Stage.Compute );
		cullPipeline = cull is null ? 0 : CoreRenderDevice.CreateCompute( cull );
		if ( cullPipeline == 0 ) Log.Warning( $"the mesh cull did not compile: {CoreRenderDevice.ShaderError}" );


		var skin = CoreShaderCache.Pass( EngineShaders.MeshSkin, "main", CoreShaderCompiler.Stage.Compute );
		skinPipeline = skin is null ? 0 : CoreRenderDevice.CreateCompute( skin );
		if ( skinPipeline == 0 ) Log.Warning( $"the skin pass did not compile: {CoreRenderDevice.ShaderError}" );

		var shapes = CoreShaderCache.Pass( EngineShaders.MeshShapes, "main", CoreShaderCompiler.Stage.Compute );
		shapesPipeline = shapes is null ? 0 : CoreRenderDevice.CreateCompute( shapes );
		if ( shapesPipeline == 0 ) Log.Warning( $"the blend shape pass did not compile: {CoreRenderDevice.ShaderError}" );

		//Only where the device takes rates without a headset - see Rendering.AdaptiveShading.
		if ( CoreRender.AdaptiveShadingPossible )
		{
			var rates = CoreShaderCache.Pass( EngineShaders.ShadingRate, "main", CoreShaderCompiler.Stage.Compute );
			ratePipeline = rates is null ? 0 : CoreRenderDevice.CreateComputeOverTable( rates );
			if ( ratePipeline == 0 ) Log.Warning( $"the shading rate pass did not compile: {CoreRenderDevice.ShaderError}" );
			CoreRender.AdaptiveShadingPipeline( ratePipeline );
		}

		prepassPipeline = Raster( "", CoreRender.TargetKind.Depth, cullBack: true );

		//And the same three by a mesh stage, where the device has them - see MeshKinds. All three or none.
		if ( CoreRenderDevice.MeshShaders )
		{
			prepassMesh = RasterMesh( "", cullBack: true );
			depthMesh = RasterMesh( "", cullBack: false );
			depthAlphaMesh = RasterMesh( EngineShaders.Alpha, cullBack: false );

			if ( prepassMesh == 0 || depthMesh == 0 || depthAlphaMesh == 0 )
			{
				Log.Warning( $"the mesh stage would not bake, and the depth is drawn from indices: {CoreRenderDevice.ShaderError}" );

				foreach ( var one in new[] { prepassMesh, depthMesh, depthAlphaMesh } )
					if ( one != 0 ) CoreRenderDevice.DestroyPipeline( one );

				prepassMesh = depthMesh = depthAlphaMesh = 0;
			}
		}
		depthPipeline = Raster( "", CoreRender.TargetKind.Depth, cullBack: false );
		depthAlphaPipeline = Raster( EngineShaders.Alpha, CoreRender.TargetKind.Depth, cullBack: false );

		if ( prepassPipeline == 0 || depthPipeline == 0 )
			Log.Warning( $"the mesh raster did not compile: {CoreRenderDevice.ShaderError}" );

		//Over the world's picture, equal to the depth the pre-pass wrote and writing none - the same rule the materials draw by.
		viewPipeline = Raster( EngineShaders.View, CoreRender.TargetKind.Scene, cullBack: false, over: true );
		if ( viewPipeline == 0 ) Log.Warning( $"the cluster view did not compile: {CoreRenderDevice.ShaderError}" );

		return cullPipeline != 0 && prepassPipeline != 0 && depthPipeline != 0;
	}

	//The depth by a mesh stage - the pre-pass, the two-sided and the cut-out lists, the camera's and every light's
	//- where the device has one. Nought where it has not; then the indexed draws do it, as they do everywhere.
	private static ulong prepassMesh, depthMesh, depthAlphaMesh;

	//Depth lists drawn by the mesh stage: bits for solid, two-sided, cut-out and 8 for lights
	internal int MeshKinds { get; private set; }

	//Mesh stage lists: camera solid and two-sided, and lights; cut-out stays indexed
	private const int MeshWhere = 1 | 2 | 8;

	private static ulong RasterMesh( string defines, bool cullBack )
	{
		var withMesh = "#define MESH 1\n" + defines;

		var ms = CoreShaderCache.Pass( EngineShaders.MeshRaster, "MainMs", CoreShaderCompiler.Stage.Mesh, withMesh );
		var ps = CoreShaderCache.Pass( EngineShaders.MeshRaster, "MainPs", CoreShaderCompiler.Stage.Pixel, withMesh );
		if ( ms is null || ps is null ) return 0;

		return CoreRenderDevice.CreatePipeline( ms, ps, CoreRender.VertexLayoutKind.MeshShader, depthTest: true,
			CoreRender.Topology.Triangles, blend: false, depthWrite: true, cullBack: cullBack, depthOnly: true,
			target: CoreRender.TargetKind.Depth, compare: CoreRenderDevice.DepthCompare.LessOrEqual );
	}

	//One raster pipeline: the depth, or with over a colour over a depth already there.
	private static ulong Raster( string defines, CoreRender.TargetKind target, bool cullBack, bool over = false )
	{
		var vs = CoreShaderCache.Pass( EngineShaders.MeshRaster, "MainVs", CoreShaderCompiler.Stage.Vertex, defines );
		var ps = CoreShaderCache.Pass( EngineShaders.MeshRaster, "MainPs", CoreShaderCompiler.Stage.Pixel, defines );
		if ( vs is null || ps is null ) return 0;

		return CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None, depthTest: true,
			CoreRender.Topology.Triangles, blend: false, depthWrite: !over, cullBack: cullBack, depthOnly: !over, target: target,
			compare: over ? CoreRenderDevice.DepthCompare.Equal : CoreRenderDevice.DepthCompare.LessOrEqual );
	}

	internal static void ReleasePipelines()
	{
		CoreRender.AdaptiveShadingPipeline( 0 );

		foreach ( var one in new[] { cullPipeline, skinPipeline, shapesPipeline, ratePipeline, viewPipeline, prepassPipeline, depthPipeline, depthAlphaPipeline } )
			if ( one != 0 ) CoreRenderDevice.DestroyPipeline( one );

		cullPipeline = skinPipeline = shapesPipeline = ratePipeline = 0;
		foreach ( var one in new[] { prepassMesh, depthMesh, depthAlphaMesh } )
			if ( one != 0 ) CoreRenderDevice.DestroyPipeline( one );

		prepassMesh = depthMesh = depthAlphaMesh = 0;
		prepassPipeline = depthPipeline = depthAlphaPipeline = 0;
		viewPipeline = 0;
		pipelinesTried = false;
	}

	internal void Release()
	{
		foreach ( var one in new[] { paramsBuffer, workBuffer, visibleBuffer, indexBuffer } )
			if ( one != 0 ) CoreRenderDevice.DestroyBuffer( one );

		paramsBuffer = workBuffer = visibleBuffer = indexBuffer = 0;
		workCapacity = visibleWords = indexWords = 0;
		Ready = false;
	}

	private static bool Reach( ref ulong buffer, ref long have, long wanted, int bytesEach )
	{
		//Given back once it has been four times what a frame wants for a second of frames running
		//- not at once, or a frame of many views and the next of one would make it again and again.
		if ( buffer != 0 && have >= wanted )
		{
			if ( have <= Math.Max( wanted * 4, 1L << 20 ) ) { oversized.Remove( buffer ); return true; }

			var frames = oversized.GetValueOrDefault( buffer ) + 1;
			oversized[buffer] = frames;
			if ( frames < 120 ) return true;

			oversized.Remove( buffer );
			CoreRenderDevice.DestroyBuffer( buffer );
			have = wanted * 2;
			buffer = CoreRenderDevice.ReserveBuffer( have * bytesEach );
			return buffer != 0;
		}

		if ( buffer != 0 ) CoreRenderDevice.DestroyBuffer( buffer );
		have = Math.Max( wanted, Math.Max( have * 2, 1024 ) );
		buffer = CoreRenderDevice.ReserveBuffer( have * bytesEach );

		return buffer != 0;
	}

	//Instances hidden from the camera by table slot bit, or empty for none; set before Begin
	internal ReadOnlyMemory<uint> HiddenFromCamera { get; set; }

	internal bool Begin( CoreSceneTables tables, ReadOnlySpan<ViewSetup> views, (int Width, int Height) pixels, bool depthKnown )
	{
		Ready = false;
		this.tables = tables;
		this.pixels = pixels;

		if ( !EnsurePipelines() || tables is null || tables.InstanceCount == 0 || views.IsEmpty || !views[0].Camera ) return false;
		if ( tables.InstanceBuffer == 0 || tables.MaterialBuffer == 0 ) return false;

		viewCount = Math.Min( views.Length, CoreMeshShaders.MaxViews );
		listCount = CameraLists + ( ( viewCount - 1 ) * ListsPerView );
		listTotal = listCount;
		if ( listTotal > CoreMeshShaders.MaxLists ) return false;

		materials = CoreMaterialTable.Count;

		var finest = Math.Max( 1, tables.FinestClusters );

		//A list's counters, four words each - where a light's list starts in the lights' run, its entries,
		//its triangles and the triangles filled - then the lists' indexed draws, five words each.
		drawArgsAt = listTotal * 4;
		meshArgsAt = drawArgsAt + ( listTotal * 5 );
		var argsEnd = meshArgsAt + ( listTotal * 4 );
		entriesAt = ( argsEnd + 3 ) & ~3;
		var at = entriesAt;

		//How many entries an index can name - see CoreMeshShaders.EntryShift. The camera's six lists and the
		//binned list both number their entries from nought, so each of those has to fit under it on its own.
		var mostEntries = CoreRenderDevice.FullIndex ? 1 << 25 : 1 << 17;

		//Each camera list kind gets a run sized to the drawn instances' finest levels, so it cannot overflow
		long wanted = 0;
		for ( var k = 0; k < 3; k++ ) { kindRun[k] = tables.KindTriangles( k ); wanted += kindRun[k]; }

		if ( wanted > MostListTriangles )
		{
			for ( var k = 0; k < 3; k++ ) kindRun[k] = kindRun[k] * MostListTriangles / wanted;

			//Log when runs are too short, at most once a second; dropped clusters leave holes
			SayTheRunsAreShort( wanted, MostListTriangles, "the camera's lists" );
		}

		//Drawn by the mesh stage, the camera's lists have no runs of their own - the bins' run is the same triangles, and it keeps its size.
		MeshKinds = prepassMesh != 0 && Rinten.Rendering.Wireframe.Clusters <= 0 ? MeshWhere : 0;

		var runAt = 0L;
		var binned = 0L;

		for ( var k = 0; k < 3; k++ )
		{
			binned += kindRun[k];
			if ( ( ( MeshKinds >> k ) & 1 ) != 0 ) kindRun[k] = 0;
			kindRunAt[k] = runAt;
			runAt += kindRun[k];
		}

		//Bin run: the same triangles sorted by material, plus moving ones, which are only paid for here
		var moving = tables.MovingTriangles;
		var movingRun = Math.Min( moving, MostListTriangles );

		if ( moving > movingRun ) SayTheRunsAreShort( moving, movingRun, "the surfaces that move" );

		binIndexAt = runAt;
		binIndexCapacity = binned + movingRun;

		//The run all light lists share, sized by what CoreSunPages bakes this frame; none without lights
		shadowIndexAt = binIndexAt + binIndexCapacity;
		shadowIndexCapacity = viewCount > 1 && ( MeshKinds & 8 ) == 0 ? Math.Clamp( ShadowTriangles, 0L, MostShadowTriangles ) : 0L;

		var indexWanted = Math.Max( 3L, ( runAt + binIndexCapacity + shadowIndexCapacity ) * 3 );

		for ( var l = 0; l < listTotal; l++ )
		{
			var camera = l < CameraLists;
			var kind = camera ? ( l == RetestList ? 0 : l % 3 ) : ( l - CameraLists ) % 3;
			var capacity = camera
				? Math.Min( finest, Math.Min( kind == 0 ? 1 << 20 : 1 << 19, mostEntries / 8 ) )
				: Math.Min( finest, Math.Min( kind == 0 ? 1 << 18 : 1 << 17, mostEntries ) );

			var drawn = camera && l < RetestList;

			listBase[l] = at;
			listCapacity[l] = capacity;
			listView[l] = camera ? 0 : 1 + ( ( l - CameraLists ) / 3 );
			listKind[l] = kind;
			listIndexBase[l] = drawn ? kindRunAt[kind] : 0;
			listIndexCapacity[l] = drawn ? kindRun[kind] : 0;
			at += capacity * 2;
		}

		cameraEntries = ( listBase[RetestList] - entriesAt ) / 2;

		//A camera's list drawn by the mesh stage is handed the entries it draws, packed, instead of indices - a word each, behind the lists. See Append.
		for ( var l = 0; l < RetestList; l++ )
		{
			if ( ( ( MeshKinds >> listKind[l] ) & 1 ) == 0 ) continue;

			listIndexBase[l] = at;
			listIndexCapacity[l] = listCapacity[l];
			at += listCapacity[l];
		}

		//Bins are material x distance slice, drawn front to back; slices capped to keep zeroing under 64KB
		binSlices = Math.Clamp( 4096 / Math.Max( materials, 1 ), 1, MostSlices );
		bins = materials * binSlices;

		//And how many draws a material's run is cut into - see DrawsAMaterial - which can never ask for more spans than there are slices to cut.
		binDraws = Math.Min( DrawsAMaterial, binSlices );

		//A bin's count, fill, triangles and triangles filled - begun at nought together - then
		//its offsets into the binned list and into its run of indices, then its indexed draw.
		binCountsAt = at;
		binFillAt = at + bins;
		binTrisAt = at + ( bins * 2 );
		binTriFillAt = at + ( bins * 3 );
		binOffsetsAt = at + ( bins * 4 );
		binTriOffsetsAt = at + ( bins * 5 );
		at += bins * 6;
		binArgsAt = ( at + 3 ) & ~3;
		at = binArgsAt + ( materials * binDraws * 5 );
		binnedAt = at;
		at += cameraEntries * 2;

		//The instances the camera leaves out, a bit each - see HiddenFromCamera. Nought where it leaves nothing out, and the cull then does not look.
		var hidden = HiddenFromCamera.Span;
		var hiddenWords = ( tables.InstanceCount + 31 ) / 32;
		var hiddenAt = hidden.Length >= hiddenWords && hiddenWords > 0 ? at : 0;
		if ( hiddenAt != 0 ) at += hiddenWords;

		//Each material's want of its pictures, a word a material - see Want in the cull and CoreTextureStreaming. Only while streaming is on.
		var wantAt = CoreTextureStreaming.On && materials > 0 ? at : 0;
		if ( wantAt != 0 ) at += materials;

		//The debug words, where asked for, behind everything else.
		debugAt = at;
		if ( debug ) at += 64 + tables.InstanceCount;

		//Work list sized at twice the chunks plus 1000, as running out silently drops geometry
		var byCells = tables.Cells.Buffer != 0 && tables.InstanceCount >= CellsFrom;
		var work = byCells
			? Math.Max( 4096L, tables.Cells.ChunksSeen( views[..viewCount] ) + 4096L )
			: Math.Max( 4096L, ( (long)tables.ChunkCount * viewCount * 2L ) + 4096L );

		//Room for packed single clusters, sized like the items; on overflow an item is emitted instead
		var singles = work;

		if ( paramsBuffer == 0 ) paramsBuffer = CoreRenderDevice.ReserveBuffer( ParamsWords * 4 );
		if ( paramsBuffer == 0 ) return false;

		//Then the cells some view sees, only when there are enough instances for cells to pay off
		cellCount = byCells ? tables.Cells.Count : 0;
		cellsAt = 7 + work + singles;
		var cellWords = cellCount > 0 ? 1L + cellCount + ( ( tables.InstanceCount + 3L ) / 4 ) : 0L;
		var workWords = WorkHeaderWords + ( ( work + singles + cellWords ) * 4 );
		if ( !Reach( ref workBuffer, ref workCapacity, workWords, 4 ) ) return false;
		if ( !Reach( ref visibleBuffer, ref visibleWords, at, 4 ) ) return false;
		if ( !Reach( ref indexBuffer, ref indexWords, indexWanted, 4 ) ) return false;

		if ( debug && indexWanted != saidIndexWanted )
		{
			saidIndexWanted = indexWanted;
			Log.Info( $"mesh buffers: indices {indexWanted * 4 / 1048576.0:F1} MB (camera {runAt * 12 / 1048576.0:F1}, bins {binIndexCapacity * 12 / 1048576.0:F1}, lights {shadowIndexCapacity * 12 / 1048576.0:F1}), entries and counters {at * 4 / 1048576.0:F1} MB, work {workWords * 4 / 1048576.0:F1} MB" );
		}

		//The header: counts and offsets, then the views, then the lists.
		Array.Clear( paramsWords );
		paramsWords[0] = (uint)tables.InstanceCount;
		paramsWords[1] = (uint)viewCount;
		paramsWords[2] = (uint)listCount;
		paramsWords[3] = (uint)work;
		paramsWords[4] = RetestList;
		paramsWords[5] = CameraLists;
		paramsWords[6] = (uint)tables.MaterialsAt;
		paramsWords[7] = (uint)entriesAt;
		paramsWords[8] = (uint)shadowIndexAt;
		paramsWords[9] = (uint)shadowIndexCapacity;
		paramsWords[10] = (uint)listTotal;
		paramsWords[11] = (uint)materials;
		paramsWords[12] = (uint)binCountsAt;
		paramsWords[13] = (uint)binOffsetsAt;
		paramsWords[14] = (uint)binFillAt;
		paramsWords[15] = (uint)binnedAt;
		paramsWords[16] = (uint)binArgsAt;
		paramsWords[18] = (uint)binSlices;
		paramsWords[32] = (uint)binDraws;

		//Which still clusters the pre-pass draws, by slice - see CoreSceneDrawer.PrepassSlices. Past it the cull writes no run of the pre-pass's indices for
		//them, and their material's far pass writes their depth itself; clamped to the slices there are, where the far pass's cut is too - see ClusterSpanFrom.
		prepassSlices = CoreSceneDrawer.PrepassSlices == uint.MaxValue
			? uint.MaxValue
			: Math.Min( CoreSceneDrawer.PrepassSlices, (uint)binSlices );
		paramsWords[35] = prepassSlices;

		//Where the materials' wants are - see wantAt in mesh_tables.slang.
		paramsWords[36] = (uint)wantAt;

		//Which lists of depth are drawn by the mesh stage - see MeshKinds.
		paramsWords[33] = (uint)MeshKinds;

		//Where the cells some view sees are listed - see cellsAt in mesh_tables.slang.
		paramsWords[34] = (uint)cellsAt;

		//Where the lists' mesh-stage draws are, and how many pixels the camera reads a metre at a metre away as.
		paramsWords[19] = (uint)meshArgsAt;
		paramsWords[21] = BitConverter.SingleToUInt32Bits( views[0].ErrorScale );
		paramsWords[22] = (uint)binTrisAt;
		paramsWords[26] = (uint)binTriFillAt;
		paramsWords[27] = (uint)binTriOffsetsAt;
		paramsWords[28] = (uint)drawArgsAt;
		paramsWords[29] = (uint)binIndexAt;
		paramsWords[30] = (uint)binIndexCapacity;
		paramsWords[31] = (uint)Math.Min( cameraEntries, mostEntries );

		//As uint4 indices, which is how the card reads that buffer: the header is seven of them and the items follow.
		paramsWords[24] = (uint)( 7 + work );
		paramsWords[25] = (uint)singles;
		paramsWords[23] = (uint)bins;

		paramsWords[20] = (uint)debugAt;
		paramsWords[17] = (uint)hiddenAt;

		var floats = MemoryMarshal.Cast<uint, float>( paramsWords.AsSpan() );

		for ( var v = 0; v < viewCount; v++ )
		{
			ref readonly var view = ref views[v];
			var w = HeaderWords + ( v * ViewWords );

			Put( floats, w, view.Plane0 ); Put( floats, w + 4, view.Plane1 ); Put( floats, w + 8, view.Plane2 );
			Put( floats, w + 12, view.Plane3 ); Put( floats, w + 16, view.Plane4 ); Put( floats, w + 20, view.Plane5 );

			var m = view.WorldToClip;
			floats[w + 24] = m.M11; floats[w + 25] = m.M12; floats[w + 26] = m.M13; floats[w + 27] = m.M14;
			floats[w + 28] = m.M21; floats[w + 29] = m.M22; floats[w + 30] = m.M23; floats[w + 31] = m.M24;
			floats[w + 32] = m.M31; floats[w + 33] = m.M32; floats[w + 34] = m.M33; floats[w + 35] = m.M34;
			floats[w + 36] = m.M41; floats[w + 37] = m.M42; floats[w + 38] = m.M43; floats[w + 39] = m.M44;

			floats[w + 40] = view.Eye.x; floats[w + 41] = view.Eye.y; floats[w + 42] = view.Eye.z; floats[w + 43] = view.ErrorScale;
			floats[w + 44] = view.LodPixels; floats[w + 45] = view.Near; floats[w + 46] = view.Far; floats[w + 47] = view.CullPixels;
			floats[w + 48] = CoreDepthPyramid.DepthWide; floats[w + 49] = CoreDepthPyramid.DepthTall; floats[w + 50] = CoreDepthPyramid.DepthLevels;
			floats[w + 51] = view.Camera && depthKnown ? 1.0f : 0.0f;
			//The camera's screen, or which casters a light's view takes - see ViewCasters in the cull.
			floats[w + 52] = view.Camera ? view.HalfHeight : view.Casters;
			floats[w + 53] = view.Camera ? view.ScaleX : view.Perspective ? 1.0f : 0.0f;
			floats[w + 54] = view.Camera ? view.ScaleY : 0.0f;
			//One for a camera, two for a flat one - see IsCamera and IsFlat.
			floats[w + 55] = view.Camera ? ( view.Perspective ? 1.0f : 2.0f ) : 0.0f;

			floats[w + 56] = view.Fovea.X; floats[w + 57] = view.Fovea.Y; floats[w + 58] = view.Fovea.Z;
			floats[w + 59] = view.Camera ? view.Fovea.W : 0.0f;
		}

		for ( var l = 0; l < listTotal; l++ )
		{
			var w = HeaderWords + ( CoreMeshShaders.MaxViews * ViewWords ) + ( l * ListWords );
			paramsWords[w] = (uint)listBase[l];
			paramsWords[w + 1] = (uint)listCapacity[l];
			paramsWords[w + 2] = (uint)listIndexBase[l];
			paramsWords[w + 3] = (uint)listIndexCapacity[l];
		}

		Rinten.Rendering.LodGroups.Table.AsSpan().CopyTo( floats[LodGroupsAt..] );

		if ( !CoreRenderDevice.FrameWrite( paramsBuffer, 0, MemoryMarshal.AsBytes( paramsWords.AsSpan() ) ) ) return false;

		//The lists' counters at nought, their indexed draws at nothing until the finish writes them, and the work list's dispatch at nought items.
		Span<uint> args = stackalloc uint[Math.Max( argsEnd, WorkHeaderWords )];
		args.Clear();

		if ( !CoreRenderDevice.FrameWrite( visibleBuffer, 0, MemoryMarshal.AsBytes( args[..argsEnd] ) ) ) return false;

		//The counts, the fills, the triangles and the triangles filled of each bin, all at nought
		//- they sit together for that reason. The offsets after them are the card's to write.
		if ( noCounts.Length < bins * 4 ) noCounts = new uint[bins * 4];
		if ( bins > 0 && !CoreRenderDevice.FrameWrite( visibleBuffer, (long)binCountsAt * 4, MemoryMarshal.AsBytes( noCounts.AsSpan( 0, bins * 4 ) ) ) ) return false;

		if ( wantAt != 0 && !CoreRenderDevice.FrameFill( visibleBuffer, (long)wantAt * 4, (long)materials * 4, 0u ) ) return false;

		if ( hiddenAt != 0 && !CoreRenderDevice.FrameWrite( visibleBuffer, (long)hiddenAt * 4, MemoryMarshal.AsBytes( hidden[..hiddenWords] ) ) ) return false;

		args.Clear();

		args[1] = 1; args[2] = 1;
		args[5] = 1; args[6] = 1;
		args[17] = 1; args[18] = 1;
		args[21] = 1; args[22] = 1;

		//The instance pass's dispatch, a group a cell some view sees, counted by the cell pass; how many cells there are beside it.
		args[23] = (uint)cellCount;
		if ( !CoreRenderDevice.FrameWrite( workBuffer, 0, MemoryMarshal.AsBytes( args[..WorkHeaderWords] ) ) ) return false;

		//And the cell pass's two counts - big cells, packed instances - at nought.
		Span<uint> noCells = stackalloc uint[4];
		if ( cellCount > 0 && !CoreRenderDevice.FrameWrite( workBuffer, cellsAt * 16, MemoryMarshal.AsBytes( noCells ) ) ) return false;

		//Now and then, the materials' wants as this frame's cull leaves them, read back a frame or two later - see CoreTextureStreaming.Wants.
		if ( wantAt != 0 && RealTime.NowDouble - wantsAskedAt >= CoreTextureStreaming.Every )
		{
			wantsAskedAt = RealTime.NowDouble;
			var scale = views[0].ErrorScale;
			CoreReadbacks.Ask<float>( visibleBuffer, (long)wantAt * 4, (long)materials * 4, wants => CoreTextureStreaming.Wants( wants, scale ) );
		}

		Ready = true;
		return true;
	}

	//When the materials' wants were last asked back for CoreTextureStreaming.
	private double wantsAskedAt = -1e9;

	private static void Put( Span<float> into, int at, System.Numerics.Vector4 v )
	{
		into[at] = v.X; into[at + 1] = v.Y; into[at + 2] = v.Z; into[at + 3] = v.W;
	}

	private ReadOnlySpan<ulong> CullBound()
	{
		bound[0] = paramsBuffer;
		bound[1] = tables.InstanceBuffer;
		bound[2] = CoreClusters.Meshes.Buffer;
		bound[3] = CoreClusters.Clusters.Buffer;
		bound[4] = tables.MaterialBuffer;
		bound[5] = workBuffer;
		bound[6] = visibleBuffer;
		bound[7] = cellCount > 0 ? tables.Cells.Buffer : paramsBuffer;
		bound[8] = CoreClusters.ClusterTriangles.Buffer;
		bound[9] = indexBuffer;
		return bound;
	}

	//When the run shortfall was last said, so a frame in that state says it once a second.
	private static long saidRuns;

	//Logs at most once a second that runs were too short and clusters were dropped
	private static void SayTheRunsAreShort( long wanted, long held, string what )
	{
		if ( Environment.TickCount64 - saidRuns < 1000 ) return;

		saidRuns = Environment.TickCount64;

		Log.Warning( $"{what} want {wanted:N0} triangles and their runs hold {held:N0} - " +
			$"the rest is left out of the picture. Either the level of detail is cutting less than it should " +
			$"(r_mesh_lod_pixels) or this scene asks for more at once than the runs are sized for." );
	}

	//How many bins there are: a slice of the distance a material.
	private int bins;

	//How many slices of the distance each material's bin is cut into - see ClusterSlice in the cull.
	private int binSlices = 1;
	private int binDraws = 1;

	//This frame's pre-pass cut, as the cull was given it - see Begin.
	private uint prepassSlices = uint.MaxValue;

	//The most an entry can carry above its cluster - ClusterSlices in mesh_tables.slang.
	private const int MostSlices = 16;

	//The bins, in the visible buffer: a count, a fill and an offset a bin, the draws' arguments, and the binned pairs.
	private int binCountsAt;
	private int binFillAt;

	//The triangles that landed in each bin, how far the bin pass has got into each bin's
	//run of the index buffer, and where each run begins. See the finish and the bin pass.
	private int binTrisAt;
	private int binTriFillAt;
	private int binTriOffsetsAt;
	private int binOffsetsAt;
	private int binArgsAt;
	private int binnedAt;

	//Where the lists' indexed draws are, five words a list.
	private int drawArgsAt;
	private int meshArgsAt;

	//The run of the index buffer the lights' lists share: where it begins and how many triangles it has room for - see Begin.
	private long shadowIndexAt;
	private long shadowIndexCapacity;

	//How many triangles this frame's lights' views can hold between them - the finest levels of
	//what stands in each view's square, added up. Set by the drawer from CoreSunPages before Begin.
	internal long ShadowTriangles { get; set; }

	//The most the lights' run may take: twelve bytes a triangle, so 24 MB.
	private const long MostShadowTriangles = 1L << 21;

	//The binned runs of the index buffer, in triangles: where they begin and how many they hold.
	private long binIndexAt;
	private long binIndexCapacity;

	//Each kind of list's run of the index buffer, in triangles: how long, and where it begins.
	private readonly long[] kindRun = new long[3];
	private readonly long[] kindRunAt = new long[3];

	//A list's run of the index buffer - its kind's, for the lists something draws.
	private readonly long[] listIndexBase = new long[CoreMeshShaders.MaxLists];
	private readonly long[] listIndexCapacity = new long[CoreMeshShaders.MaxLists];

	//Noughts for the counts and the fills as the frame begins.
	private uint[] noCounts = Array.Empty<uint>();

	//Sums an instance's blend shapes into its range of CoreSceneTables.ShapeSums: cleared, then one dispatch
	//a shape whose weight is not nought. False where none is, and the pose pass then reads no sums at all.
	private bool Shape( CoreSceneTables.Entry entry, CoreSceneRenderer.SceneMesh mesh )
	{
		var weights = entry.ShapeWeights;
		var runs = mesh.ShapeRuns;
		var sums = CoreSceneTables.ShapeSums.Buffer;

		if ( shapesPipeline == 0 || weights is null || runs is null || mesh.ShapeDeltas == 0 || sums == 0 ) return false;

		var any = false;
		var shapeCount = Math.Min( weights.Length, runs.Length / 2 );

		for ( var s = 0; s < shapeCount && !any; s++ ) any = weights[s] != 0.0f && runs[( s * 2 ) + 1] > 0;
		if ( !any ) return false;

		if ( !CoreRenderDevice.FrameFill( sums, entry.ShapesAt * CoreSceneTables.ShapeSums.Stride,
			(long)entry.ShapesCount * CoreSceneTables.ShapeSums.Stride, 0 ) ) return false;

		shapesBound[0] = mesh.ShapeDeltas;
		shapesBound[1] = sums;

		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( shapesPush.AsSpan() );

		for ( var s = 0; s < shapeCount; s++ )
		{
			var weight = weights[s];
			var first = runs[s * 2];
			var count = runs[( s * 2 ) + 1];
			if ( weight == 0.0f || count <= 0 ) continue;

			words[0] = (uint)first;
			words[1] = (uint)count;
			words[2] = (uint)entry.ShapesAt;
			words[3] = (uint)entry.ShapesCount;
			words[4] = BitConverter.SingleToUInt32Bits( weight );

			CoreRenderDevice.Dispatch( shapesPipeline, shapesBound, ( count + 127 ) / 128, 1, 1, shapesPush );
		}

		return true;
	}

	//SkinPush in mesh_skin.slang, eight words.
	private ReadOnlySpan<byte> SkinPush( uint index, uint first, uint count, uint sumsAt, uint shaped, uint skinned,
		uint wasAt, uint keepWas )
	{
		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( skinPush.AsSpan() );
		words[0] = index;
		words[1] = first;
		words[2] = count;
		words[3] = wasAt;
		words[4] = sumsAt;
		words[5] = shaped;
		words[6] = skinned;
		words[7] = keepWas;
		return skinPush;
	}

	private ReadOnlySpan<byte> Push( uint a, uint b = 0, uint c = 0, uint d = 0 )
	{
		BitConverter.TryWriteBytes( push.AsSpan( 0 ), a );
		BitConverter.TryWriteBytes( push.AsSpan( 4 ), b );
		BitConverter.TryWriteBytes( push.AsSpan( 8 ), c );
		BitConverter.TryWriteBytes( push.AsSpan( 12 ), d );
		return push;
	}

	//Re-poses only instances whose pose hash or pool range changed; after the cull, before vertex reads
	internal void Skin()
	{
		if ( !Ready || skinPipeline == 0 || !tables.AnyPosed ) return;

		//What a posed thing without a skeleton or without shapes does not read still has to be bound
		//to something - see mesh_skin.slang, which is told by g_skinned and g_shapes not to look.
		var stand = tables.InstanceBuffer;

		skinBound[0] = tables.InstanceBuffer;
		skinBound[1] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.FrameSide );
		skinBound[2] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.SkinSide );
		skinBound[3] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide );
		skinBound[4] = CoreSceneTables.Palettes.Buffer != 0 ? CoreSceneTables.Palettes.Buffer : stand;
		skinBound[5] = CoreSceneTables.ShapeSums.Buffer != 0 ? CoreSceneTables.ShapeSums.Buffer : stand;
		skinBound[6] = CoreSceneTables.Was.Buffer != 0 ? CoreSceneTables.Was.Buffer : stand;

		foreach ( var one in skinBound ) if ( one == 0 ) return;

		var generation = CoreGeometryPool.Vertices.Generation;
		var begun = false;

		foreach ( var slot in tables.PosedSlots )
		{
			var entry = tables.EntryAt( slot );

			if ( entry is null || !entry.Live || !entry.Posed || entry.PosedAt < 0 ) continue;

			var mesh = entry.Mesh;
			if ( mesh is null || !mesh.Pooled || mesh.PoolVertexCount <= 0 ) continue;

			//Posed already, as it stands.
			if ( entry.PoseHash != 0 && entry.SkinnedHash == entry.PoseHash &&
				entry.SkinnedAt == entry.PosedAt && entry.SkinnedGeneration == generation ) continue;

			if ( !begun )
			{
				CoreRenderDevice.ZoneBegin( "skin" );
				begun = true;
			}

			//The shapes somebody set, summed a vertex before the bones - see mesh_shapes.slang. The sums
			//are cleared and built again whole: what a shape added last frame is not something to subtract.
			CoreRenderDevice.ZoneBegin( "skin shapes" );
			var shaped = entry.ShapesAt >= 0 && Shape( entry, mesh );
			CoreRenderDevice.ZoneEnd();
			var skinned = entry.PaletteAt >= 0 && mesh.Skin != 0;

			//The range holds a pose of this one's only where it was posed into before
			var keepWas = entry.WasAt >= 0 && CoreSceneTables.Was.Buffer != 0 && entry.SkinnedAt == entry.PosedAt;

			CoreRenderDevice.Dispatch( skinPipeline, skinBound,
				( mesh.PoolVertexCount + 127 ) / 128, 1, 1,
				SkinPush( (uint)slot, (uint)mesh.PoolVertexAt, (uint)mesh.PoolVertexCount,
					shaped ? (uint)entry.ShapesAt : 0u, shaped ? 1u : 0u, skinned ? 1u : 0u,
					keepWas ? (uint)entry.WasAt : 0u, keepWas ? 1u : 0u ) );

			entry.PoseMovedTurn = keepWas && entry.SkinnedHash != entry.PoseHash ? EngineLoop.LoopFrames : -1;

			entry.SkinnedHash = entry.PoseHash;
			entry.SkinnedAt = entry.PosedAt;
			entry.SkinnedGeneration = generation;
			Skins++;
		}

		if ( begun ) CoreRenderDevice.ZoneEnd();
	}

	internal void Cull( ulong pyramid )
	{
		if ( !Ready || CoreClusters.Clusters.Buffer == 0 || CoreClusters.Meshes.Buffer == 0 ) { Ready = false; return; }

		var ok = true;

		//The cells first, then the instances of the ones some view sees - see CoreInstanceCells. With none, every instance of the table.
		if ( cellCount > 0 )
		{
			CoreRenderDevice.ZoneBegin( "cull cells" );
			ok &= CoreRenderDevice.Dispatch( cullPipeline, CullBound(), ( cellCount + 63 ) / 64, 1, 1, Push( 4, debug ? 1u : 0u ), pyramid );
			CoreRenderDevice.ZoneEnd();

			CoreRenderDevice.ZoneBegin( "cull instances" );
			ok &= CoreRenderDevice.DispatchIndirect( cullPipeline, CullBound(), workBuffer, CellArgsOffset, Push( 0, debug ? 1u : 0u, 1u ), pyramid );
			CoreRenderDevice.ZoneEnd();
		}
		else
		{
			CoreRenderDevice.ZoneBegin( "cull instances" );
			ok &= CoreRenderDevice.Dispatch( cullPipeline, CullBound(), ( tables.InstanceCount + 63 ) / 64, 1, 1, Push( 0, debug ? 1u : 0u ), pyramid );
			CoreRenderDevice.ZoneEnd();
		}

		//The pass finishes itself: its last group writes the draws' arguments - see Finish in the shader - so the pyramid's groups go in with it.
		var (first, second) = CoreDepthPyramid.DispatchGroups();

		CoreRenderDevice.ZoneBegin( "cull clusters" );
		ok &= CoreRenderDevice.DispatchIndirect( cullPipeline, CullBound(), workBuffer, 0, Push( 1, debug ? 1u : 0u, first, second ), pyramid );
		CoreRenderDevice.ZoneEnd();

		Culls++;
		if ( !ok ) Ready = false;
	}

	//Asks the clusters the first pass put aside against the pyramid this frame built.
	internal void Retest( ulong pyramid )
	{
		if ( !Ready ) return;

		var (first, second) = CoreDepthPyramid.DispatchGroups();

		CoreRenderDevice.ZoneBegin( "cull appeared" );
		CoreRenderDevice.DispatchIndirect( cullPipeline, CullBound(), workBuffer, 16, Push( 2, debug ? 1u : 0u, first, second ), pyramid );
		CoreRenderDevice.ZoneEnd();
	}

	//Sorts the camera's clusters by material, for DrawMaterials, and fills the lights' lists into the run of the index buffer they share, for
	//DrawView. After the last cull pass and before either is drawn; a thread an entry, the camera's then the lights', as many as the finish counted.
	internal void Bin( ulong pyramid )
	{
		if ( !Ready || ( materials <= 0 && viewCount < 2 ) ) return;

		CoreRenderDevice.ZoneBegin( "bin" );
		CoreRenderDevice.DispatchIndirect( cullPipeline, CullBound(), workBuffer, BinArgsOffset, Push( 3, debug ? 1u : 0u ), pyramid );
		CoreRenderDevice.ZoneEnd();
	}

	private ReadOnlySpan<ulong> RasterBound( bool alpha )
	{
		bound[0] = paramsBuffer;
		bound[1] = tables.InstanceBuffer;
		bound[2] = CoreClusters.Clusters.Buffer;
		bound[3] = CoreClusters.ClusterVertices.Buffer;
		bound[4] = CoreClusters.ClusterTriangles.Buffer;
		bound[5] = alpha ? CoreGeometryPool.Vertices.Buffer : CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide );
		bound[6] = visibleBuffer;
		bound[7] = tables.MaterialBuffer;

		//A cut-out's corners read where the texture goes out of the mesh's own vertex and the position out of the posed one - see mesh_raster.slang.
		if ( !alpha ) return bound.AsSpan( 0, 8 );

		bound[8] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide );
		return bound.AsSpan( 0, 9 );
	}

	//A list's indexed draw, over its run of the index buffer as the finish wrote it.
	private void DrawList( int list, ulong pipeline, uint mode = 0 )
	{
		if ( listCapacity[list] <= 0 || pipeline == 0 ) return;

		var alpha = listKind[list] == 2;

		if ( CoreRenderDevice.DrawPulledIndexedIndirect( pipeline, visibleBuffer, ( drawArgsAt + ( list * 5 ) ) * 4, indexBuffer,
			Push( (uint)list, (uint)listView[list], mode ), 0, RasterBound( alpha && mode == 0 ) ) )
			ListDraws++;
	}

	//A list of depth: by the mesh stage where this frame draws its depth so, from its indices otherwise.
	private void DrawDepth( int list, ulong pipeline, ulong mesh )
	{
		var meshed = list < CameraLists ? ( ( MeshKinds >> listKind[list] ) & 1 ) != 0 : ( MeshKinds & 8 ) != 0;

		if ( !meshed )
		{
			DrawList( list, pipeline );
			return;
		}

		if ( listCapacity[list] <= 0 ) return;

		if ( CoreRenderDevice.DrawMeshIndirect( mesh, visibleBuffer, ( meshArgsAt + ( list * 4 ) ) * 4,
			Push( (uint)list, (uint)listView[list] ), 0, RasterBound( listKind[list] == 2 ) ) )
			ListDraws++;
	}

	//The camera's three lists into its depth - the first half, or what appeared.
	internal void DrawCamera( bool appeared )
	{
		if ( !Ready ) return;

		var first = appeared ? 3 : 0;
		DrawDepth( first, prepassPipeline, prepassMesh );
		DrawDepth( first + 1, depthPipeline, depthMesh );
		DrawDepth( first + 2, depthAlphaPipeline, depthAlphaMesh );
	}

	//Draws a light's view (index 1+) into the open shadow layer with the pre-pass pipelines
	internal void DrawView( int view )
	{
		if ( !Ready || view < 1 || view >= viewCount ) return;

		var first = CameraLists + ( ( view - 1 ) * ListsPerView );
		DrawDepth( first, prepassPipeline, prepassMesh );
		DrawDepth( first + 1, depthPipeline, depthMesh );
		DrawDepth( first + 2, depthAlphaPipeline, depthAlphaMesh );
	}

	private static readonly bool debug = Environment.GetEnvironmentVariable( "RINTEN_MESH_DEBUG" ) == "1";
	private int debugFrame;
	private int lastViews;
	private static readonly bool traceLights = Environment.GetEnvironmentVariable( "RINTEN_SUN_TRACE" ) == "1";

	//Under RINTEN_MESH_DEBUG=1, reads the frame's counters back and logs them now and then. Waits for the card.
	internal void Debug()
	{
		if ( !debug || !Ready ) return;
		//One frame in a hundred and twenty - or, under RINTEN_SUN_TRACE, every frame a light's view was
		//drawn in and the one after: what a bake's lists held is only worth reading on the frames that bake.
		if ( ( ++debugFrame % 120 ) != 5 && !( traceLights && ( viewCount > 1 || lastViews > 1 ) ) ) { lastViews = viewCount; return; }
		lastViews = viewCount;

		var work = new byte[WorkHeaderWords * 4];
		var args = new byte[listTotal * 16];
		CoreRenderDevice.ReadBuffer( workBuffer, work );
		CoreRenderDevice.ReadBuffer( visibleBuffer, args );
		var drawnCount = 0;
		var inFrustum = 0;
		var floats = MemoryMarshal.Cast<uint, float>( paramsWords.AsSpan() );
		Span<System.Numerics.Vector4> planes = stackalloc System.Numerics.Vector4[6];
		for ( var i = 0; i < 6; i++ )
			planes[i] = new System.Numerics.Vector4( floats[HeaderWords + ( i * 4 )], floats[HeaderWords + ( i * 4 ) + 1], floats[HeaderWords + ( i * 4 ) + 2], floats[HeaderWords + ( i * 4 ) + 3] );

		for ( var i = 0; i < tables.InstanceCount; i++ )
		{
			ref var record = ref tables.RecordAt( i );
			if ( ( record.Flags & CoreMeshShaders.InstanceDrawn ) == 0 ) continue;
			drawnCount++;
			if ( CoreSceneRenderer.SphereVisible( planes, new Vector3( record.Sphere.X, record.Sphere.Y, record.Sphere.Z ), record.Sphere.W ) ) inFrustum++;
		}

		var sample = tables.RecordAt( Math.Min( 1, tables.InstanceCount - 1 ) );

		var gpu = new byte[( 64 + tables.InstanceCount ) * 4];
		{
			var whole = new byte[( debugAt + 64 + tables.InstanceCount ) * 4];
			if ( CoreRenderDevice.ReadBuffer( visibleBuffer, whole ) ) whole.AsSpan( debugAt * 4, gpu.Length ).CopyTo( gpu );
		}
		var codes = new int[64];
		for ( var i = 0; i < tables.InstanceCount; i++ ) codes[BitConverter.ToUInt32( gpu, ( 64 + i ) * 4 ) & 63]++;
		var gpuText = $"gpu saw: count {BitConverter.ToUInt32( gpu, 0 )}, views {BitConverter.ToUInt32( gpu, 4 )}, work {BitConverter.ToUInt32( gpu, 8 )}, plane0.x {BitConverter.ToSingle( gpu, 12 )}, plane0.z {BitConverter.ToSingle( gpu, 16 )}, eye.y {BitConverter.ToSingle( gpu, 20 )}, camera {BitConverter.ToSingle( gpu, 24 )}, cullPixels {BitConverter.ToSingle( gpu, 28 )}, sphere1 {BitConverter.ToSingle( gpu, 32 )},{BitConverter.ToSingle( gpu, 36 )},{BitConverter.ToSingle( gpu, 40 )},{BitConverter.ToSingle( gpu, 44 )} mesh {BitConverter.ToUInt32( gpu, 48 )} flags {BitConverter.ToUInt32( gpu, 52 )} clusters {BitConverter.ToUInt32( gpu, 56 )} first {BitConverter.ToUInt32( gpu, 60 )}; codes:";
		for ( var c = 0; c < 64; c++ ) if ( codes[c] > 0 ) gpuText += $" {c}x{codes[c]}";

		var text = new System.Text.StringBuilder();

		//How many levels the meshes were built with. Nothing to cut is nothing to thin: a model kept with one
		//level is drawn at its finest whatever the camera is doing, and the cull's whole pyramid is a no-op for it.
		text.Append( "meshes by levels:" );
		for ( var l = 0; l <= CoreClusters.MostLevels; l++ )
			if ( CoreClusters.KeptWithLevels[l] > 0 ) text.Append( $" {l}x{CoreClusters.KeptWithLevels[l]}" );
		text.Append( " | " );

		text.Append( gpuText ).Append( " | " );
		text.Append( $"mesh debug: instances {tables.InstanceCount} ({drawnCount} drawn, {inFrustum} in the frustum by the processor, hzb {floats[HeaderWords + 51]}, lod budget {floats[HeaderWords + 44]:F2} px, cull {floats[HeaderWords + 47]:F2} px, error a metre {floats[HeaderWords + 43]:F0} px, eye {floats[HeaderWords + 40]:F1},{floats[HeaderWords + 41]:F1},{floats[HeaderWords + 42]:F1}, plane0 {planes[0]}, sample sphere {sample.Sphere} mesh {sample.Mesh} flags {sample.Flags} materialFirst {sample.MaterialFirst}), chunks {tables.ChunkCount}, finest {tables.FinestClusters}, views {viewCount}, lists {listCount}, materials {materials}, work items {BitConverter.ToUInt32( work, 96 )} packed {BitConverter.ToUInt32( work, 100 )}, overflow {BitConverter.ToUInt32( work, 12 )}, retest groups {BitConverter.ToUInt32( work, 16 )}; lists:" );
		for ( var l = 0; l < listCount; l++ ) text.Append( ' ' ).Append( BitConverter.ToUInt32( args, ( l * 16 ) + 4 ) );
		text.Append( "; " ).Append( tables.Posed ).Append( " posed (" )
			.Append( tables.PosedAtRest ).Append( " still at rest), " ).Append( Skins ).Append( " posings so far" );
		//Which level of the chains the camera's list is made of - what the cut actually chose, rather than what it was asked for.
		if ( CoreClusters.Clusters.Buffer != 0 && CoreClusters.Clusters.Used > 0 )
		for ( var list = 0; list < 3; list++ )
		{
			var drawn = Math.Min( Math.Min( BitConverter.ToUInt32( args, ( ( list * 4 ) + 1 ) * 4 ), (uint)listCapacity[list] ), 65536u );

			if ( drawn > 0 )
			{
				var records = new byte[CoreClusters.Clusters.Used * CoreClusters.RecordBytes];
				var entries = new byte[(long)drawn * 8];

				if ( !CoreRenderDevice.ReadBuffer( CoreClusters.Clusters.Buffer, records ) ) text.Append( "; levels: no records" );
				else
				{
					var pairs = new byte[( listBase[list] * 4 ) + entries.Length];

					if ( !CoreRenderDevice.ReadBuffer( visibleBuffer, pairs ) ) text.Append( "; levels: no list" );
					else
					{
						var levels = new int[8];
						var triangles = 0L;
						var widest = 0u;
						var dropped = 0;

						for ( var e = 0; e < drawn; e++ )
						{
							//Masks off the ClusterDropped bit and counts dropped clusters separately
							var entry = BitConverter.ToUInt32( pairs, ( listBase[list] + ( e * 2 ) + 1 ) * 4 );
							if ( ( entry & 0x40000000u ) != 0 ) dropped++;

							//And without the slice of the distance the cull found it in, which rides above the cluster's number - ClusterOf in mesh_tables.slang. Left on, every
							//cluster past the first slice read as a number no cluster has and was skipped: "0 tris in N clusters" for any list not standing in the camera's lap.
							var cluster = entry & ( ( 1u << CoreMeshShaders.ClusterSliceShift ) - 1u );
							if ( cluster >= CoreClusters.Clusters.Used ) continue;

							var partLevel = BitConverter.ToUInt32( records, ( (int)cluster * CoreClusters.RecordBytes ) + CoreClusters.RecordBytes - 4 );
							levels[Math.Min( partLevel >> 16, 7 )]++;

							//And how many triangles it holds - words.z's top half, which is what the vertex stage stops at.
							var held = BitConverter.ToUInt32( records, ( (int)cluster * CoreClusters.RecordBytes ) + CoreClusters.RecordBytes - 8 ) >> 16;
							triangles += held;
							widest = Math.Max( widest, held );
						}

						text.Append( $"; list {list} levels:" );
						for ( var l = 0; l < 8; l++ ) if ( levels[l] > 0 ) text.Append( $" {l}x{levels[l]}" );

						//What the list holds, and what its indexed draw was told to draw - the two are the same number where nothing ran out.
						var indexed = BitConverter.ToUInt32( args, ( ( list * 4 ) + 2 ) * 4 );
						text.Append( $" = {triangles} tris in {drawn} clusters (widest {widest}), {indexed} indexed, {dropped} dropped" );
					}
				}
			}
		}

		//The lights' views: what each drew into the sun's pages or the map of what moves - clusters and triangles - and how long a run they share.
		if ( viewCount > 1 )
		{
			text.Append( "; lights:" );

			for ( var v = 1; v < viewCount; v++ )
			{
				var clusters = 0L;
				var tris = 0L;

				for ( var k = 0; k < ListsPerView; k++ )
				{
					var l = CameraLists + ( ( v - 1 ) * ListsPerView ) + k;
					clusters += BitConverter.ToUInt32( args, ( ( l * 4 ) + 1 ) * 4 );
					tris += BitConverter.ToUInt32( args, ( ( l * 4 ) + 2 ) * 4 );
				}

				text.Append( $" {v}:{clusters}/{tris}t" );
			}

			text.Append( $" (a run of {shadowIndexCapacity})" );
		}

		//And the bins: how many clusters each material's pass drew.
		if ( materials > 0 )
		{
			//The counters are a bin - a slice of the distance a material - and the draws are a material, five words each. As far as the last span's draw: a material
			//is drawn in binDraws of them, and read short of that the far spans' triangles went uncounted - which the check below then called a disagreement.
			var whole = new byte[( binArgsAt + ( materials * binDraws * 5 ) ) * 4];
			CoreRenderDevice.ReadBuffer( visibleBuffer, whole );

			text.Append( $"; binned:" );
			var crossed = 0L;
			var biggest = new List<string>();

			for ( var b = 0; b < materials; b++ )
			{
				//Its clusters over all its slices, and the triangles its one indexed draw was told to draw.
				var count = 0L;
				var filled = 0L;
				var trisFilled = 0L;
				var nearest = 0L;

				for ( var slice = 0; slice < binSlices; slice++ )
				{
					var one = BitConverter.ToUInt32( whole, ( binCountsAt + ( b * binSlices ) + slice ) * 4 );
					count += one;
					filled += BitConverter.ToUInt32( whole, ( binFillAt + ( b * binSlices ) + slice ) * 4 );
					trisFilled += BitConverter.ToUInt32( whole, ( binTriFillAt + ( b * binSlices ) + slice ) * 4 );

					//How much of the bin is in the four nearest slices - what the sorting has to work with. All of it in one slice is a bin nothing can be gained on.
					if ( slice < 4 ) nearest += one;
				}

				//The draw of its first span - a material is drawn in binDraws of them now, so what this reads is the near one; see DrawMaterials.
				var tris = 0L;

				for ( var span = 0; span < binDraws; span++ )
				{
					//The read is bounded: this buffer is copied back as far as the debug words and a
					//material's later spans can sit past them - a diagnostic must not be what throws.
					var at = ( binArgsAt + ( ( ( b * binDraws ) + span ) * 5 ) ) * 4;

					if ( at + 4 > whole.Length ) break;

					tris += BitConverter.ToUInt32( whole, at ) / 3;
				}

				//Named, because which surface holds a scene's triangles is the first question of every
				//cut: a canopy cannot take a coarser one and a trunk can - see r_mesh_lod_pixels.
				var named = CoreMaterialTable.At( b )?.Shader ?? "?";

				if ( count > 0 ) text.Append( $" {b}:{System.IO.Path.GetFileNameWithoutExtension( named )}x{count}/{tris}t({nearest} near)" );

				//Triangles per distance slice for the two biggest bins, to see what LOD cannot reduce
				if ( binSlices > 1 && count > 0 && tris > 0 && biggest.Count < 2 )
				{
					var spread = new System.Text.StringBuilder();

					for ( var slice = 0; slice < binSlices; slice++ )
					{
						var sliceTris = BitConverter.ToUInt32( whole, ( binTrisAt + ( b * binSlices ) + slice ) * 4 );
						if ( sliceTris > 0 ) spread.Append( $" {slice}:{sliceTris / 1000}k" );
					}

					if ( spread.Length > 0 ) biggest.Add( $"{b}{spread}" );
				}

				if ( trisFilled != tris ) crossed += Math.Abs( trisFilled - tris );

				//Checks the bin pass filled what the bin's draw was told; a mismatch shows as depth holes
				if ( filled != count ) crossed += Math.Abs( filled - count );
			}

			if ( crossed > 0 )
			{
				text.Append( $"; **the bins' draws and fills disagree by {crossed} clusters**" );

				//The raw words of the first bin's slices, because a disagreement that reads as billions is a reader
				//looking at the wrong words, and one that reads as thousands is the card running out of run.
				text.Append( $"; layout: bins {bins} slices {binSlices} countsAt {binCountsAt} argsAt {binArgsAt} binnedAt {binnedAt} entriesAt {entriesAt} cameraEntries {cameraEntries}" );
				text.Append( "; bin 0 raw counts:" );
				for ( var slice = 0; slice < Math.Min( binSlices, 6 ); slice++ )
					text.Append( $" [{slice}] n={BitConverter.ToUInt32( whole, ( binCountsAt + slice ) * 4 )}" +
						$" fill={BitConverter.ToUInt32( whole, ( binFillAt + slice ) * 4 )}" +
						$" tris={BitConverter.ToUInt32( whole, ( binTrisAt + slice ) * 4 )}" +
						$" triFill={BitConverter.ToUInt32( whole, ( binTriFillAt + slice ) * 4 )}" +
						$" at={BitConverter.ToUInt32( whole, ( binOffsetsAt + slice ) * 4 )}" +
						$" triAt={BitConverter.ToUInt32( whole, ( binTriOffsetsAt + slice ) * 4 )}" );
			}

			foreach ( var one in biggest ) text.Append( $"; by slice {one}" );

			text.Append( $", material draws {MaterialDraws}" );
		}

		Log.Info( text.ToString() );

		SayWhatNothingShades();
	}

	//Debug: clusters binned to materials no pass draws, and why each was skipped (RINTEN_MESH_DEBUG)
	private void SayWhatNothingShades()
	{
		if ( materials <= 0 ) return;

		var whole = new byte[( binCountsAt + bins ) * 4];
		CoreRenderDevice.ReadBuffer( visibleBuffer, whole );

		var lost = 0L;
		var said = new System.Text.StringBuilder();

		for ( var m = 0; m < materials; m++ )
		{
			var material = CoreMaterialTable.At( m );

			//The same four the loop takes, in the same order, plus the ceiling it never reaches past.
			var why =
				m >= ushort.MaxValue - 1 ? "past the ceiling" :
				material is null ? "slot freed" :
				material.Loose ? "loose" :
				material.Translucent ? "translucent" :
				material.ShadePipeline == 0 ? "its pass would not build" : null;

			if ( why is null ) continue;

			//Its bin's count of clusters, over every slice of it - not a word of its draw, which is an indexed one and says triangles.
			var count = 0L;
			for ( var slice = 0; slice < binSlices; slice++ )
				count += BitConverter.ToUInt32( whole, ( binCountsAt + ( m * binSlices ) + slice ) * 4 );

			if ( count == 0 ) continue;

			lost += count;
			if ( said.Length < 400 ) said.Append( $" [{m} {material?.Shader ?? "?"}: {why}, {count} clusters]" );
		}

		if ( lost > 0 )
			Log.Warning( $"{lost} clusters of the depth have a material with nothing to shade them -{said}" );
	}

	//The buffers a material's draw reads, in cluster_main.slang's order; empty where the pool has no vertices yet
	private Span<ulong> MaterialBound()
	{
		var vertices = CoreGeometryPool.Vertices.Buffer;
		if ( vertices == 0 ) return default;

		bound[0] = paramsBuffer;
		bound[1] = tables.InstanceBuffer;
		bound[2] = CoreClusters.Clusters.Buffer;
		bound[3] = CoreClusters.ClusterVertices.Buffer;
		bound[4] = CoreClusters.ClusterTriangles.Buffer;
		bound[5] = vertices;
		bound[6] = visibleBuffer;
		bound[7] = paramsBuffer;

		//A posed thing's position and frame, out of its posed range - see LoadClusterCorner in cluster_main.slang
		bound[8] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide );
		bound[9] = CoreGeometryPool.Vertices.Side( CoreGeometryPool.FrameSide );

		return bound.AsSpan( 0, 10 );
	}

	//A material's push block: its bin and number, the screen, and its tint in g_visSpare0, which ClusterInstance multiplies in
	private byte[] MaterialPush( int m, Color tint )
	{
		Array.Clear( visPush );

		var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( visPush.AsSpan() );
		words[0] = (uint)binnedAt;
		words[1] = (uint)m;
		words[2] = (uint)pixels.Width;
		words[3] = (uint)pixels.Height;
		words[4] = (uint)entriesAt;
		words[5] = (uint)binOffsetsAt;

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( visPush.AsSpan() );
		floats[16] = tint.r;
		floats[17] = tint.g;
		floats[18] = tint.b;
		floats[19] = tint.a;

		return visPush;
	}

	//Depth pre-pass for self-moving surfaces using their own vertex shader and alpha cut only
	//so the shading pass writes no depth and gets early depth rejection
	internal void DrawMoverDepth()
	{
		if ( !Ready || materials <= 0 ) return;

		var drawBound = MaterialBound();
		if ( drawBound.IsEmpty ) return;

		CoreRenderDevice.ZoneBegin( "mover depth" );

		var most = Math.Min( materials, ushort.MaxValue - 1 );

		for ( var m = 0; m < most; m++ )
		{
			var material = CoreMaterialTable.At( m );
			if ( material is null || material.Loose || material.Translucent || !material.Moves ) continue;

			if ( !material.AskedToDepth )
			{
				material.AskedToDepth = true;
				material.DepthPipeline = CoreResources.Pipeline( new CoreSceneRenderer.PipelineKind( false, false, material.TwoSided )
				{
					Shader = material.Shader,
					Combo = material.Combo,
					Target = CoreRender.TargetKind.Depth,
					Cluster = true,
					DepthOnly = true,

					//Everything folded out, as it was when nought was the default: a depth pass reads none of the frame's paths.
					Specialize = 0,
					Moves = true,
				} );
			}

			//A surface with no depth build of its own keeps the old bargain: nothing writes its depth here, and
			//its shading pass writes it - see DrawMaterials, which asks the same question of the same field.
			if ( material.DepthPipeline == 0 )
			{
				if ( !material.SaidNoDepth )
				{
					material.SaidNoDepth = true;

					Log.Warning( $"{material.Shader}: no depth build, so its own pass carries the depth again - " +
						"see CoreShaderFile.Stage.ClusterDepthPixel" );
				}

				continue;
			}

			//Whole run in one call, far spans included; letting them write depth while shading was slower
			for ( var span = 0; span < binDraws; span++ )
			{
				if ( CoreRenderDevice.DrawPulledIndexedIndirect( material.DepthPipeline, visibleBuffer,
					( binArgsAt + ( ( ( m * binDraws ) + span ) * 5 ) ) * 4, indexBuffer,
					MaterialPush( m, material.Tint ), material.Texture, drawBound ) )
					MoverDepthDraws++;
			}
		}

		CoreRenderDevice.ZoneEnd();
	}

	//How many depth calls the movers took this run - see DrawMoverDepth.
	internal static long MoverDepthDraws;

	//Whether any cluster material moves its own corners - see DrawMoverVelocity
	internal bool AnyMovers()
	{
		var most = Math.Min( materials, ushort.MaxValue - 1 );

		for ( var m = 0; m < most; m++ )
		{
			if ( CoreMaterialTable.At( m ) is { Loose: false, Translucent: false, Moves: true } ) return true;
		}

		return false;
	}

	//The movers' own motion into the velocity picture, over the depth they wrote - see MainClusterVelocityVs; whether any drew
	internal bool DrawMoverVelocity()
	{
		if ( !Ready || materials <= 0 ) return false;

		var drawBound = MaterialBound();
		if ( drawBound.IsEmpty ) return false;

		var drew = false;
		var most = Math.Min( materials, ushort.MaxValue - 1 );

		CoreRenderDevice.ZoneBegin( "mover velocity" );

		for ( var m = 0; m < most; m++ )
		{
			var material = CoreMaterialTable.At( m );
			if ( material is null || material.Loose || material.Translucent || !material.Moves ) continue;

			if ( !material.AskedToVelocity )
			{
				material.AskedToVelocity = true;
				material.VelocityPipeline = CoreResources.Pipeline( new CoreSceneRenderer.PipelineKind( false, false, material.TwoSided )
				{
					Shader = material.Shader,
					Combo = material.Combo,
					Target = CoreRender.TargetKind.Velocity,
					Cluster = true,
					Specialize = 0,
					Moves = true,
				} );
			}

			if ( material.VelocityPipeline == 0 ) continue;

			for ( var span = 0; span < binDraws; span++ )
			{
				drew |= CoreRenderDevice.DrawPulledIndexedIndirect( material.VelocityPipeline, visibleBuffer,
					( binArgsAt + ( ( ( m * binDraws ) + span ) * 5 ) ) * 4, indexBuffer,
					MaterialPush( m, material.Tint ), material.Texture, drawBound );
			}
		}

		CoreRenderDevice.ZoneEnd();

		return drew;
	}

	//A material at a time, its clusters out of the bin the cull sorted for it: a draw a material, a cluster an instance of
	//it, tested equal against the depth the pre-pass wrote and shaded by the material's own stages - see cluster_main.slang.
	internal void DrawMaterials( CoreRender.TargetKind target, bool finishes, string chain = null, string warm = null )
	{
		if ( !Ready || materials <= 0 ) return;

		//What the frame holds, as the bits the world's pixel stage is baked with: no probes, no debug view, no lamps, no fog and the rest are
		//all whole branches of that stage which the ordinary frame never takes and pays for anyway - see CoreSceneRenderer.ShadeSpecialize.
		var specialize = CoreSceneRenderer.ShadeSpecialize();

		var drawBound = MaterialBound();
		if ( drawBound.IsEmpty ) return;

		CoreRenderDevice.ZoneBegin( "materials" );

		var most = Math.Min( materials, ushort.MaxValue - 1 );

		//Twice over the table: everything the pre-pass covered first, then the surfaces that move their own vertices. Those carry their own depth
		//- nothing pre-passed them - so they have something to be rejected by only once the still world is in the buffer. See MaterialMoves.
		for ( var sweep = 0; sweep < 2; sweep++ )
		for ( var m = 0; m < most; m++ )
		{
			var material = CoreMaterialTable.At( m );
			if ( material is null || material.Loose || material.Translucent ) continue;
			if ( material.Moves != ( sweep == 1 ) ) continue;

			//Whether another pass writes this surface's depth first (DrawMoverDepth), so this one need not
			var elsewhere = material.Moves && material.DepthPipeline != 0;

			//And whether its far calls want a pass of their own: only where the run is cut at all, and only
			//for a shader that reads kFarShading - for any other it would be the near pass baked twice.
			var far = binDraws > 1 && CoreResources.ShadesFar( material.Shader );

			//And a still surface carries its own depth where the frame's pre-pass did not draw it: all of it where there was none, and its far
			//calls where the pre-pass stopped at the far cut - see CoreSceneDrawer. PrepassSlices, and the cull's Append, which made the cut.
			var carries = ( material.Moves && !elsewhere ) || ( !material.Moves && prepassSlices == 0 );
			var farCarries = carries || ( !material.Moves && prepassSlices != uint.MaxValue && binDraws > 1 );

			//A far pipeline is there when the shader bakes one for the distance, or when the far calls must
			//write the depth the pre-pass left out - then the near shader is baked again writing it, see below.
			var wantsFar = far || ( binDraws > 1 && farCarries != carries );

			var stale = finishes != material.ShadeFinishes || specialize != material.ShadeSpecialize ||
				!string.Equals( chain, material.ShadeChain, StringComparison.Ordinal ) ||
				( material.ShadeFarPipeline != 0 ) != wantsFar ||
				material.ShadePrepass != elsewhere || material.ShadeCarries != carries ||
				material.ShadeFarCarries != farCarries;

			if ( stale )
			{
				material.AskedToShade = false;
				material.ShadeFinishes = finishes;
				material.ShadeSpecialize = specialize;
				material.ShadeChain = chain;
				material.ShadePrepass = elsewhere;
				material.ShadeCarries = carries;
				material.ShadeFarCarries = farCarries;
			}

			if ( !material.AskedToShade )
			{
				material.AskedToShade = true;
				material.ShadePipeline = ShadePipelineFor( material, target, finishes, specialize, chain, carries );

				//And the same pass baked for the calls past the cut, where the shader leaves anything out there - see far above.
				material.ShadeFarPipeline = far
					? ShadePipelineFor( material, target, finishes, specialize | CoreSceneRenderer.FarShading, chain, farCarries )
					: 0;

				//The far pass writes the depth the pre-pass left out, so where the one baked for the distance will not bake, the whole surface is
				//baked writing it instead - the near pass as it stands does not, and the far clusters would be drawn into a depth nobody filled.
				if ( binDraws > 1 && material.ShadeFarPipeline == 0 && farCarries != carries )
					material.ShadeFarPipeline = ShadePipelineFor( material, target, finishes, specialize, chain, farCarries );

				//The pass carrying the frame's point effects is still compiling, or would not: drawn with the
				//curve alone meanwhile, and asked again next frame - never taken off the cluster path for it.
				if ( chain is not null && material.ShadePipeline == 0 )
				{
					ChainWaiting++;
					material.AskedToShade = false;
					material.ShadePipeline = ShadePipelineFor( material, target, finishes, specialize, null, carries );
				}
			}

			//And the pass that would carry them, asked for beside this one while the frame still draws a compose - see CoreSceneDrawer.FoldChain.
			if ( warm is not null && ShadePipelineFor( material, CoreRender.TargetKind.Screen, true, specialize, warm, carries ) == 0 )
				ChainWaiting++;

			//One pass per material; if it will not build, its meshes go back to the classic path next frame
			if ( material.ShadePipeline == 0 )
			{
				CoreResources.CannotShade( material.Shader );
				continue;
			}

			//One call per span, nearest first; far spans use the far pass, or the full one if it will not build
			for ( var span = 0; span < binDraws; span++ )
			{
				var pipeline = span == 0 || material.ShadeFarPipeline == 0
					? material.ShadePipeline
					: material.ShadeFarPipeline;

				if ( CoreRenderDevice.DrawPulledIndexedIndirect( pipeline, visibleBuffer,
					( binArgsAt + ( ( ( m * binDraws ) + span ) * 5 ) ) * 4, indexBuffer,
					MaterialPush( m, material.Tint ), material.Texture, drawBound ) )
					MaterialDraws++;
			}
		}

		CoreRenderDevice.ZoneEnd();
	}

	//How many passes carrying a run of point effects were wanted this frame and are not
	//there yet - see CoreSceneDrawer.FoldChain, which carries a run only once there are none.
	internal static int ChainWaiting;

	//A material's cluster pass, into this picture, baked this way.
	private static ulong ShadePipelineFor( CoreMaterialTable.Info material,
		CoreRender.TargetKind target, bool finishes, int specialize, string chain = null, bool carries = false )
	{
		return CoreResources.Pipeline( new CoreSceneRenderer.PipelineKind( false, false, material.TwoSided )
		{
			Shader = material.Shader,
			ScreenChain = chain,
			Combo = material.Combo,
			Target = target,
			Cluster = true,

			//Only a surface whose depth nothing else writes carries it itself - see the cluster branch of MakePipeline, which is the one reader.
			Moves = carries,
			Specialize = specialize,
			Finishes = finishes,
		} );
	}

	//Paints the camera's clusters as colours over the finished world - see mat_clusters. Drawn last of the
	//world's own passes, equal to the depth the clusters wrote, so what it covers is what the clusters drew.
	internal void DrawClusterView( int mode )
	{
		var pipeline = viewPipeline;
		if ( !Ready || mode <= 0 || pipeline == 0 ) return;

		CoreRenderDevice.ZoneBegin( "cluster view" );

		for ( var list = 0; list < RetestList; list++ ) DrawList( list, pipeline, (uint)mode );

		CoreRenderDevice.ZoneEnd();
	}

}
