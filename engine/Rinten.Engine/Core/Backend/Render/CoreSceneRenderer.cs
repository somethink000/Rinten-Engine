using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Rinten;

//Draws the active scene's ModelRenderers through our renderer; shared scene-drawing helpers
internal static unsafe class CoreSceneRenderer
{
	//Engine shaders by asset path; compiled like any asset, never duplicated in this file
	internal const string StandardShader = MaterialDefinition.StandardShader;


	//What fills the screen where the world does not. Its own shader rather than a material's, because the sky is
	//the frame's - three colours and a sun, written into the frame block and read by everything the sky lights.
	internal const string SkyShader = "shaders/engine/sky.shader";

	//Shaders that move vertices skip the depth pre-pass, which would cull their displaced parts
	//A shader opts in via ENGINE_MOVES_VERTICES or a feature; see CoreResources.MovesVertices
	internal static bool MovesVertices( string shader, string combo ) => CoreResources.MovesVertices( shader, combo );



	//One mesh of a model: an index range, the texture its material resolved to, and the material's own tint.
	internal record struct MeshDraw( int FirstIndex, int IndexCount, int FirstVertex, ulong Texture, Color Tint )
	{
		/// <summary>Whether this mesh has to be drawn after the solid world, in back-to-front order.</summary>
		public bool Translucent;

		/// <summary>Whether the backs of its faces are drawn.</summary>
		public bool TwoSided;

		/// <summary>Only ever stands in for the rest (the impostor); a whole-model draw leaves it out.</summary>
		public bool StandsIn;

		/// <summary>The body-group choices claiming this mesh; zero, nearly always, means always drawn.</summary>
		public ulong Group;


		/// <summary>Each material group's material for this mesh, by name; or null.</summary>
		public string[] GroupMaterials;

		/// <summary>Which shader the material asked for, or null for the standard one.</summary>
		public string Shader;

		/// <summary>Shader features the material enabled, in declared order; null = defaults. A pipeline key.</summary>
		public string Combo;

		/// <summary>Vertices to run where the shader reads them itself; zero for meshes drawn by index.</summary>
		public int PulledVertices;


		//Zero on nearly every mesh, the behaviour before shaders could say anything - see CoreShaderParams.Cull
		/// <summary>What its shader said about culling it: grow, never, fade.</summary>
		public Vector3 Cull;

		/// <summary>The material's path, for the table that names it on the card - see CoreSceneTables.</summary>
		public string Material;

		/// <summary>Whether the material cuts out below an alpha, and where.</summary>
		public bool Masked;
		public float Cutoff;

		/// <summary>Metres one unit of its first UVs covers, for texture streaming; nought if unknown.</summary>
		public float MetresPerUv;
	}

	//MeshDraw.MetresPerUv for a range of a model's triangles: the square root of their area
	//over the area their first coordinates cover, from a few thousand of them at most.
	internal static float MetresPerUv( ReadOnlySpan<PackedVertex> vertices, ReadOnlySpan<uint> indices, int firstIndex, int indexCount, int firstVertex )
	{
		var triangles = indexCount / 3;
		if ( triangles <= 0 ) return 0.0f;

		var step = Math.Max( 1, triangles / 4096 );
		double surface = 0.0, covered = 0.0;

		for ( var t = 0; t < triangles; t += step )
		{
			var at = firstIndex + ( t * 3 );
			if ( at + 2 >= indices.Length ) break;

			var a = (long)indices[at] + firstVertex;
			var b = (long)indices[at + 1] + firstVertex;
			var c = (long)indices[at + 2] + firstVertex;
			if ( a >= vertices.Length || b >= vertices.Length || c >= vertices.Length ) continue;

			ref readonly var va = ref vertices[(int)a];
			ref readonly var vb = ref vertices[(int)b];
			ref readonly var vc = ref vertices[(int)c];

			surface += ( vb.Position - va.Position ).Cross( vc.Position - va.Position ).Length * 0.5;

			var u1 = vb.TexCoord0.x - va.TexCoord0.x;
			var v1 = vb.TexCoord0.y - va.TexCoord0.y;
			var u2 = vc.TexCoord0.x - va.TexCoord0.x;
			var v2 = vc.TexCoord0.y - va.TexCoord0.y;
			covered += Math.Abs( ( u1 * v2 ) - ( u2 * v1 ) ) * 0.5;
		}

		return covered > 1e-12 && surface > 0.0 ? (float)Math.Sqrt( surface / covered ) : 0.0f;
	}

	//Which of a model's material sets is named, or -1 for the one the meshes were built with.
	internal static int MaterialGroupIndex( SceneMesh mesh, string name )
	{
		if ( mesh is null || string.IsNullOrWhiteSpace( name ) ) return -1;

		for ( var i = 0; i < mesh.MaterialGroupNames.Length; i++ )
		{
			if ( string.Equals( mesh.MaterialGroupNames[i], name, StringComparison.OrdinalIgnoreCase ) )
				return i;
		}

		return -1;
	}

	//The draw with a group's material on it, resolved once and kept in the usual caches
	internal static MeshDraw WithGroupMaterial( MeshDraw part, int group,
		Dictionary<string, ulong> textures, ulong fallback, Dictionary<string, ulong> materials )
	{
		if ( part.GroupMaterials is null || group < 0 || group >= part.GroupMaterials.Length ) return part;

		var name = part.GroupMaterials[group];
		if ( string.IsNullOrWhiteSpace( name ) ) return part;

		var bound = materials is not null
			? ResolveMaterial( name, materials, textures, fallback )
			: ResolveMaterialTexture( name, textures, fallback );

		if ( bound == 0 ) return part;

		var how = ReadMaterial( name );

		return part with
		{
			Texture = bound,
			Tint = ReadMaterialTint( name ),
			Cull = ReadMaterialCull( name ),
			Material = name,
			Masked = how.Masked,
			Cutoff = how.AlphaCutoff,
			Translucent = how.Translucent,
			TwoSided = how.TwoSided,
		};
	}

	//Which pipeline a mesh needs. Three things decide it, so there are eight - baked when first asked for and kept.
	internal readonly record struct PipelineKind( bool Skinned, bool Translucent, bool TwoSided )
	{
		/// <summary>The shader, as a resource path, or null for standard; part of the pipeline key.</summary>
		public string Shader { get; init; }

		/// <summary>The entry for a frame pass in a multi-pass file (bloom's BrightPs); null is MainPs.</summary>
		public string Entry { get; init; }

		/// <summary>Whether the surface moves its own vertices - see CoreResources.MovesVertices.</summary>
		public bool Moves { get; init; }

		/// <summary>Portable instanced run (instanced_surface.slang); stages read numbers from the bone ring.</summary>
		public bool Instanced { get; init; }

		/// <summary>Whether this pass draws both of a headset's eyes at once.</summary>
		public bool Stereo { get; init; }

		/// <summary>Whether the draw ignores the depth it is drawn over.</summary>
		public bool NoDepth { get; init; }

		/// <summary>Post pass blend mode (BasePostProcess.PassBlend), e.g. adding glow unread.</summary>
		public int PostBlend { get; init; }

		/// <summary>A frame picture pass's Vulkan format, as PictureShape answers; part of the key.</summary>
		public int PictureFormat { get; init; }

		/// <summary>Whether the shader reads its own vertices; sets the vertex layout.</summary>
		public bool Pulled { get; init; }

		/// <summary>The picture drawn into: world (two half-float targets) or screen (one 8-bit).</summary>
		public CoreRender.TargetKind Target { get; init; }

		/// <summary>Whether the mesh is moved by blend shapes before anything else.</summary>
		public bool Morphed { get; init; }

		/// <summary>DepthOnly: pre-pass with the surface's shader. Wireframe: edges, baked on demand.</summary>
		public bool DepthOnly { get; init; }

		public bool Wireframe { get; init; }

		/// <summary>Draws lines rather than triangles (Graphics.Draw( GpuBuffer )); topology is baked in.</summary>
		public bool Lines { get; init; }

		/// <summary>The pass shading the world's clusters over the pre-pass depth - see cluster_main.slang.</summary>
		public bool Cluster { get; init; }


		/// <summary>frame.slang specialization constant bits (see ShadeSpecialize), or -1 for shader defaults</summary>
		public int Specialize { get; init; } = -1;

		/// <summary>Pass applies exposure and tonemap itself (no compose after it); only for screen targets</summary>
		public bool Finishes { get; init; }

		/// <summary>Point effects this finishing pass carries, by CorePointChain key; null: curve only.</summary>
		public string ScreenChain { get; init; }

		/// <summary>Draws edges through what is in front: no depth test.</summary>
		public bool Through { get; init; }

		/// <summary>Full-screen pass over a finished picture, no depth, for post-processes (CorePostBlit).</summary>
		public bool Post { get; init; }

		/// <summary>Shader feature values in declared order (null = defaults); a combo is a shader.</summary>
		public string Combo { get; init; }
	}

	//A model, on the device, ready to draw.
	internal sealed class SceneMesh
	{
		public ulong Vertices;
		public ulong Indices;

		/// <summary>Whether its skin follows its bones as dual quaternions - see PoseBones.</summary>
		public bool DualQuaternion;

		/// <summary>Whether the shader reads this mesh's vertices itself, having no device layout.</summary>
		public bool Pulled;

		/// <summary>In the geometry pool, whose buffers these are; parts count from these.</summary>
		public bool Pooled;
		public long PoolVertexAt, PoolIndexAt;
		public int PoolVertexCount, PoolIndexCount;

		/// <summary>The second buffer a pulled draw reads beside its geometry - a palette, a table - or zero.</summary>
		public ulong Data;
		public List<MeshDraw> Meshes = new();
		public BBox Bounds;

		/// <summary>The skin, where the model has one: bone indices and weights, and the skeleton they index.</summary>
		public ulong Skin;
		public BoneRest[] Bones = System.Array.Empty<BoneRest>();

		/// <summary>Blend shape deltas sorted by vertex, plus per-vertex runs; per model, weights per object.</summary>
		public ulong MorphDeltas;
		public ulong MorphRuns;

		/// <summary>How many shapes the model has, which is how many weights a draw writes.</summary>
		public int MorphCount;

		/// <summary>How many vertices the runs cover and how many deltas there are, for the portable frame's pose pass.</summary>
		public int MorphVertexCount, MorphDeltaCount;

		/// <summary>Deltas sorted by shape, for the pose pass (mesh_shapes.slang), which walks set shapes.</summary>
		public ulong ShapeDeltas;
		public int[] ShapeRuns;

		/// <summary>Drawn when no body group is chosen: each group's first choice. Zero without groups.</summary>
		public ulong DefaultGroupMask;

		/// <summary>The alternative material sets, by index.</summary>
		public string[] MaterialGroupNames = System.Array.Empty<string>();

		/// <summary>Material paths used, so CoreResources.Forget can rebuild this mesh when one changes.</summary>
		public readonly HashSet<string> Materials = new( System.StringComparer.OrdinalIgnoreCase );

		//Read once at upload for Chosen's fallback, which cannot tell from a mask which bits belong together
		/// <summary>Every bit each body group owns, one entry per group, or null without groups.</summary>
		public ulong[] PartMasks;

		/// <summary>Mask with unlisted body groups at their defaults; 0 = nothing, MaxValue = model default.</summary>
		public ulong Chosen( ulong mask )
		{
			if ( mask == 0 || PartMasks is null ) return mask;

			if ( mask == ulong.MaxValue )
				return DefaultGroupMask != 0 ? DefaultGroupMask : ulong.MaxValue;

			foreach ( var part in PartMasks )
			{
				if ( ( mask & part ) == 0 ) mask |= DefaultGroupMask & part;
			}

			return mask;
		}

		/// <summary>Its device record (CoreClusters), or -1 where it has none, as a morphed mesh.</summary>
		public int MeshSlot = -1;

		/// <summary>Its clusters in the arenas: where they start and how many, all levels.</summary>
		public int ClusterFirst, ClusterCount;
		public int ClusterVertexAt, ClusterVertexCount;
		public int ClusterTriangleAt, ClusterTriangleCount;
		public int ClusterLevels;

		/// <summary>What each chain level holds (CoreClusters.Tally), to take off the totals when it goes.</summary>
		public CoreClusters.LevelTally[] LevelTallies;
		public long UnnamedVertices;


		/// <summary>Impostor error in model metres (0 = none); whole-model draws pick their level by it</summary>
		public float ImpostorError;

		/// <summary>LOD chain data for whole-model draws; must match ChainLevel in mesh_cull.slang</summary>
		public bool Chain;
		public int ShadowLevelOffset;
		public float[] LevelErrors;
		public List<MeshDraw>[] Levels;

		/// <summary>How many of the levels are the model's own meshes - the rest, one at most, its impostor.</summary>
		public int MeshLevels;

		/// <summary>The level's parts for a whole-model draw within <paramref name="allowed"/> pixels.</summary>
		public List<MeshDraw> PartsAt( System.Numerics.Matrix4x4 world, Vector3 eye, float pixelsPerMetre, float allowed, int level = -1 )
		{
			if ( !Chain || Levels is null ) return Meshes;

			if ( level < 0 )
			{
				var scale = MathF.Max( new Vector3( world.M11, world.M12, world.M13 ).Length,
					MathF.Max( new Vector3( world.M21, world.M22, world.M23 ).Length, new Vector3( world.M31, world.M32, world.M33 ).Length ) );

				//From the nearest point of its box, as the card judges it - see ChainLevel in mesh_cull.slang.
				var half = Bounds.Size * 0.5f;
				var middle = System.Numerics.Vector3.Transform( new System.Numerics.Vector3( Bounds.Center.x, Bounds.Center.y, Bounds.Center.z ), world );
				var reach = new Vector3(
					( MathF.Abs( world.M11 ) * half.x ) + ( MathF.Abs( world.M21 ) * half.y ) + ( MathF.Abs( world.M31 ) * half.z ),
					( MathF.Abs( world.M12 ) * half.x ) + ( MathF.Abs( world.M22 ) * half.y ) + ( MathF.Abs( world.M32 ) * half.z ),
					( MathF.Abs( world.M13 ) * half.x ) + ( MathF.Abs( world.M23 ) * half.y ) + ( MathF.Abs( world.M33 ) * half.z ) );
				var off = new Vector3( MathF.Abs( eye.x - middle.X ), MathF.Abs( eye.y - middle.Y ), MathF.Abs( eye.z - middle.Z ) ) - reach;
				var deep = new Vector3( MathF.Max( off.x, 0.0f ), MathF.Max( off.y, 0.0f ), MathF.Max( off.z, 0.0f ) ).Length;

				level = 0;

				if ( deep > 0.01f && pixelsPerMetre > 0.0f )
				{
					//The impostor level is not among these; a whole-model draw tests it separately
					for ( var l = 1; l < MeshLevels; l++ )
						if ( Levels[l] is not null && pixelsPerMetre * LevelErrors[l] * scale / deep <= allowed ) level = l;
				}
			}

			level = Math.Clamp( level, 0, Levels.Length - 1 );
			return level == 0 || Levels[level] is null ? Meshes : Levels[level];
		}

		public Vector3 BoundCentre;

		public float BoundRadius;

		/// <summary>Whether a mesh shows under a body-group mask: unclaimed, or any claim bit set.</summary>
		public static bool Shows( in MeshDraw mesh, ulong mask )
			=> mesh.Group == 0 || ( mesh.Group & mask ) != 0;


		public bool IsSkinned => Skin != 0 && Bones.Length > 0;
	}

	//A bone as the model file remembers it.
	internal record struct BoneRest( string Name, int Parent, System.Numerics.Matrix4x4 InverseBind );

	//Set for the session when some drawn shader turns out to lack a two-view build
	internal static bool StereoIncomplete { get; private set; }

	//A two-view program built and then refused where it runs, as when the browser will not link it
	//Latches StereoIncomplete like a missing build, and says why once
	internal static void StereoRefused( string why )
	{
		if ( StereoIncomplete ) return;
		StereoIncomplete = true;

		Log.Warning( $"a two-view program would not build here, so both of a headset's eyes cannot be drawn " +
			$"in one pass - drawing an eye at a time instead: {why}" );
	}

	//Lights the frame block carries, as the core's MaxLights; the browser reads these and shadow tiles go
	//among them, everything else reads CoreLights.MostLights from the buffer
	internal const int MaxLights = 16;

	//Where the block's own lights sit - see CoreLights, which sends them on.
	internal const int LightsAt = 16 + 4;

	//What a frame's block is built in - one of each, never handed out to keep.
	private static readonly float[] frameScratch = new float[FrameFloats];

	//The scene's own half of the block, kept between the two eyes of a headset frame - see BuildFrame.
	private static readonly float[] sceneScratch = new float[FrameFloats];

	//Which engine frame the scene half above was built in, or -1 for none.
	private static long sharedFrame = -1;

	//Whether this is one of a headset's eyes. True for exactly the span the eyes are drawn in, so a camera drawing
	//into a texture builds its own.
	private static bool Sharing => CoreRender.SharingViews && Rinten.VR.VRSystem.IsRendering;
	private static readonly List<(Light Light, SceneLight Loose, float Reach)> lightScratch = new();
	private static readonly List<Light> lightsScratch = new();

	//Beside FrameLights, the lights of the frame put straight into the world rather than by
	//a component - null where the light is a component's. See SceneWorld.InternalLights.
	private static readonly List<SceneLight> looseScratch = new();

	//Every light of the frame, nearest reach first; CoreLights sorts them again as it sends them
	internal static readonly float[] LightBlock = new float[CoreLights.MostLights * CoreLights.LightFloats];

	//How many of them the last block was built with - which may be more than the block itself holds.
	internal static int LightsInFrame;

	//Whether the last frame had more lights than CoreLights.MostLights
	private static bool lightsOver;

	//Which light each of the last block's records is, in the same order - see CoreLampMaps.
	//Null for a light put into the world without a component, which casts no shadow.
	internal static IReadOnlyList<Light> FrameLights => lightsScratch;
	//Builds a view's frame block; the scene half can be shared between headset eyes (see Sharing)
	internal static float[] BuildFrame( Scene scene, System.Numerics.Matrix4x4 viewProjection, Vector3 camera )
	{
		if ( !Sharing || sharedFrame != EngineLoop.LoopFrames )
		{
			//Named, so the report shows how often the scene half is built: once on a headset, once per view elsewhere
			CoreRenderDevice.ZoneBegin( "frame scene" );
			BuildScene( scene, camera );
			CoreRenderDevice.ZoneEnd();

			sharedFrame = Sharing ? EngineLoop.LoopFrames : -1;
		}

		var block = frameScratch;
		sceneScratch.AsSpan().CopyTo( block );
		SetView( block, viewProjection, camera );

		return block;
	}

	//The lanes that are this view's and no other's, over a scene half that has just been laid down - see BuildFrame.
	private static void SetView( float[] block, System.Numerics.Matrix4x4 viewProjection, Vector3 camera )
	{
		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( block.AsSpan() );

		System.Runtime.InteropServices.MemoryMarshal.Write( bytes, in viewProjection );

		//Back out of the screen and into the world, for whatever starts from a pixel rather than a vertex.
		//The same inverse yields the frustum clip-to-world, which the shading reads to walk a pixel back out.
		if ( System.Numerics.Matrix4x4.Invert( viewProjection, out var clipToWorld ) )
			System.Runtime.InteropServices.MemoryMarshal.Write( bytes[( ClipToWorldAt * 4 )..], in clipToWorld );

		block[16] = camera.x;
		block[17] = camera.y;
		block[18] = camera.z;
	}

	//A block's view matrix, and the way back out through it, changed and nothing else - for a copy of the frame's
	//block that sees the same world from without the upscaler's jitter; see CoreSceneDrawer.PutPlainFrame.
	internal static void SetViewMatrix( float[] block, System.Numerics.Matrix4x4 viewProjection )
		=> SetView( block, viewProjection, new Vector3( block[16], block[17], block[18] ) );

	//Everything of the block that is the scene's rather than the view's, into sceneScratch - see BuildFrame.
	private static void BuildScene( Scene scene, Vector3 camera )
	{
		const int floatsPerLight = CoreLights.LightFloats;
		const int lightsAt = LightsAt;

		//Scratch, not fresh: the block goes straight to the device and is never kept, so a frame allocates none
		var block = sceneScratch;
		System.Array.Clear( block );

		//Seconds since engine start, written even without a scene; float precision is fine for animation
		block[TimeAt + 0] = RealTime.Now;
		block[TimeAt + 1] = RealTime.Delta;

		//Scene time while playing, so pause and time scale apply; wall clock in the editor. And a frame before, for motion
		var playing = scene.IsValid() && !scene.IsEditor;
		block[TimeAt + 2] = playing ? (float)scene.TimeNow : RealTime.Now;
		block[TimeAt + 3] = playing ? (float)( scene.TimeNow - scene.TimeDelta ) : RealTime.Now - RealTime.Delta;

		block[ShadowFilterAt] = Math.Clamp( ShadowFilterQuality, 0, 2 );

		if ( !scene.IsValid() )
		{
			LightsInFrame = 0;
			block[19] = 0;
			BakedOn = CoreBakedLighting.Write( block, null );
			AreaOn = false;
			return;
		}

		//Each under its own zone: the scene half is six walks of components, the biggest part of a page's block
		CoreRenderDevice.ZoneBegin( "block sky" );
		SetSky( block, scene );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block ambient" );
		SetAmbient( block, scene );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block light 2d" );
		SetLight2D( block, scene );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block fog" );
		SetFog( block, scene );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block probes" );
		CoreEnvmap.Write( block, scene, camera );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block bake" );
		BakedOn = CoreBakedLighting.Write( block, scene );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "block lights" );

		//Nearest first by distance from the eye less the light's radius, as the slots run out from the far end
		var wanted = lightScratch;
		wanted.Clear();

		foreach ( var light in scene.GetAllComponents<Light>() )
		{
			if ( !light.IsValid() || !light.Enabled ) continue;

			var reach = Reach( light, camera );
			if ( reach < float.MaxValue ) wanted.Add( (light, null, reach) );
		}

		//And the lights put into the world without a component: a particle's glow, a tracer's flash, an editor
		//preview's lamps. A component's own light object is skipped - the component is what was read above.
		if ( scene.SceneWorld is { } world )
		{
			lock ( world.InternalSceneObjects )
			{
				foreach ( var loose in world.InternalLights )
				{
					if ( loose.Component is Light || !loose.RenderingEnabled ) continue;

					var reach = Reach( loose, camera );
					if ( reach < float.MaxValue ) wanted.Add( (null, loose, reach) );
				}
			}
		}

		wanted.Sort( static ( a, b ) => a.Reach.CompareTo( b.Reach ) );

		var lights = lightsScratch;
		var looseLights = looseScratch;
		lights.Clear();
		looseLights.Clear();

		for ( var i = 0; i < wanted.Count && i < CoreLights.MostLights; i++ )
		{
			lights.Add( wanted[i].Light );
			looseLights.Add( wanted[i].Loose );
		}

		//Said once as the frame starts dropping lights, rather than found out from a dark corner of the level
		var over = wanted.Count > CoreLights.MostLights;
		if ( over && !lightsOver )
		{
			Log.Warning( $"the frame has {wanted.Count} lights and the card holds {CoreLights.MostLights} - " +
				$"the {wanted.Count - CoreLights.MostLights} furthest away are not in it" );
		}

		lightsOver = over;

		//The block's count is the browser's and holds sixteen; everything else reads the buffer, which holds all
		LightsInFrame = lights.Count;
		block[19] = Math.Min( lights.Count, MaxLights );

		var shaped = false;

		for ( var i = 0; i < lights.Count; i++ )
		{
			var at = i * floatsPerLight;

			var source = lights[i] is { } light ? LightSource.Of( light ) : LightSource.Of( looseLights[i] );

			//The colour already carries the brightness, so the shader does not have to know
			//the difference between a dim white light and a grey one - there is not one.
			LightBlock[at + 4] = source.Colour.r;
			LightBlock[at + 5] = source.Colour.g;
			LightBlock[at + 6] = source.Colour.b;
			//Kind; in its fraction, in twentieths, twice what it is withheld from and one where it is baked - see LightCode
			var baked = lights[i] is { } inBake && BakedOn && CoreBakedLighting.InBake( inBake );
			LightBlock[at + 7] = source.Kind + ( ( ( source.Withholds * 2 ) + ( baked ? 1 : 0 ) ) * 0.05f );

			LightBlock[at + 8] = source.Forward.x;
			LightBlock[at + 9] = source.Forward.y;
			LightBlock[at + 10] = source.Forward.z;

			//The light's own width, which its shadow's softness comes from - see LampShadow
			LightBlock[at + 13] = source.SourceRadius;

			//Where in the lamps' map its faces are, where it has any - the first of their squares and their size;
			//CoreLampMaps writes both after this, for the lamps that cast and are baked; nothing has them until then.
			LightBlock[at + 14] = -1.0f;
			LightBlock[at + 15] = 0.0f;

			//The power distance is raised to. One is the inverse square and is what a light left
			//alone does; see Light.Attenuation, which is the whole of why this float4 is here.
			LightBlock[at + 16] = source.Falloff;

			//A directional light has no position and no range: it is the sun, and the shader only reads its direction.
			if ( source.Kind > 0 )
			{
				LightBlock[at + 0] = source.Position.x;
				LightBlock[at + 1] = source.Position.y;
				LightBlock[at + 2] = source.Position.z;

				//Where it stops being worth lighting, nearer than its radius - see EffectiveRange
				LightBlock[at + 3] = EffectiveRange( source.Radius, source.Colour, source.Falloff );
			}

			LightBlock[at + 11] = 0.0f;
			LightBlock[at + 12] = 0.0f;
			LightBlock[at + 18] = 0.0f;
			LightBlock[at + 19] = 0.0f;

			if ( source.Kind == 2 )
			{
				//Cones in half-angle degrees; the shader compares cosines, the same test without trigonometry
				LightBlock[at + 11] = MathF.Cos( source.ConeInner.DegreeToRadian() );
				LightBlock[at + 12] = MathF.Cos( source.ConeOuter.DegreeToRadian() );
			}
			else if ( source.Kind == 1 && source.Shape != 0 )
			{
				//Its shape where a spot keeps its cones - see AreaLamp
				LightBlock[at + 11] = source.Shape;
				LightBlock[at + 12] = source.HalfWidth;
				LightBlock[at + 18] = source.HalfHeight;
				LightBlock[at + 19] = source.Turn;
				shaped = true;
			}

			//And into the block where it fits: the browser has no buffer and reads lights from there
			if ( i < MaxLights )
				LightBlock.AsSpan( at, floatsPerLight ).CopyTo( block.AsSpan( lightsAt + ( i * floatsPerLight ), floatsPerLight ) );
		}

		//The tables only where a lamp has a shape: without them it lights as the point it stands on
		AreaOn = shaped && CoreAreaLights.Write( block );

		CoreRenderDevice.ZoneEnd();
	}

	//Where each lamp's faces are in the lamps' maps, as CoreLampMaps.Plan wrote them into the records after the block took its copy -
	//into the block's copy too, which is where the portable frame reads its lights
	internal static void LampFacesIntoBlock( float[] block )
	{
		var count = Math.Min( LightsInFrame, MaxLights );

		for ( var i = 0; i < count; i++ )
		{
			var from = i * CoreLights.LightFloats;
			var into = LightsAt + from;

			block[into + 14] = LightBlock[from + 14];
			block[into + 15] = LightBlock[from + 15];
			block[into + 17] = LightBlock[from + 17];
		}
	}

	//The flat ambient light the scene's components ask for - see AmbientAt.
	internal static void SetAmbient( float[] block, Scene scene )
	{
		var ambient = scene.GatherAmbientLight();

		block[AmbientAt + 0] = ambient.r;
		block[AmbientAt + 1] = ambient.g;
		block[AmbientAt + 2] = ambient.b;
		block[AmbientAt + 3] = Math.Clamp( ambient.a, 0.0f, 1.0f );
	}

	//The scene's 2D lighting - see Light2DAt here and in sprite.slang, and Lights2DAt. Off in a scene with no 2D light,
	//so a sprite lit in 2D is drawn unlit rather than black in a scene nobody has lit. Gathered here, once a frame,
	//rather than in the scene's update, which an editor scene that is not playing does not run
	internal static void SetLight2D( float[] block, Scene scene )
	{
		var system = scene.GetSystem<SceneLight2DSystem>();
		if ( system is null ) return;

		system.Gather();

		if ( !system.Active ) return;

		var global = system.GlobalLight;

		block[Light2DAt + 0] = global.r;
		block[Light2DAt + 1] = global.g;
		block[Light2DAt + 2] = global.b;
		block[Light2DAt + 3] = 1.0f;

		var lights = system.Lights;

		block[Light2DInfoAt + 0] = lights.Count;
		block[Light2DInfoAt + 1] = system.Settings.SelfShadow ? 1.0f : 0.0f;
		block[Light2DInfoAt + 2] = system.Settings.PenetrationDepth;

		for ( var i = 0; i < lights.Count; i++ )
		{
			var light = lights[i];
			var at = Lights2DAt + ( i * Light2DFloats );

			block[at + 0] = light.Position.x;
			block[at + 1] = light.Position.y;
			block[at + 2] = light.Position.z;
			block[at + 3] = light.Range;

			block[at + 4] = light.Radiance.r;
			block[at + 5] = light.Radiance.g;
			block[at + 6] = light.Radiance.b;
			block[at + 7] = light.SourceSize;

			block[at + 8] = light.Direction.x;
			block[at + 9] = light.Direction.y;
			block[at + 10] = light.HalfAngle;
			block[at + 11] = light.Softness;
		}
	}

	//Writes the scene's gradient fog into the frame block, read off the scene world
	private static void SetFog( float[] block, Scene scene )
	{
		var world = scene.SceneWorld.IsValid() ? scene.SceneWorld : null;
		var fog = world?.GradientFog ?? default;

		var volumes = SetFogVolumes( block, world );

		FogOn = fog.Enabled || volumes > 0;
		FogVolumes = volumes > 0;
		FogSheetLaid = false;

		if ( !fog.Enabled )
		{
			block[FogAt + 10] = 0.0f;
			return;
		}

		block[FogAt + 0] = fog.Color.r;
		block[FogAt + 1] = fog.Color.g;
		block[FogAt + 2] = fog.Color.b;
		block[FogAt + 3] = Math.Clamp( fog.MaximumOpacity, 0.0f, 1.0f );

		block[FogAt + 4] = fog.StartDistance;
		block[FogAt + 5] = MathF.Max( fog.EndDistance, fog.StartDistance + 0.01f );
		block[FogAt + 6] = fog.StartHeight;
		block[FogAt + 7] = MathF.Max( fog.EndHeight, fog.StartHeight + 0.01f );

		block[FogAt + 8] = MathF.Max( fog.DistanceFalloffExponent, 0.001f );
		block[FogAt + 9] = MathF.Max( fog.VerticalFalloffExponent, 0.001f );
		block[FogAt + 10] = 1.0f;
		block[FogAt + 11] = fog.ColorFromSky ? 1.0f : 0.0f;
	}

	//Writes the nearest visible fog volumes as ray-marchable boxes; returns how many
	private static int SetFogVolumes( float[] block, SceneWorld world )
	{
		var count = 0;

		//How far the air reaches from the eye and where it starts, which the slices span - see CoreVolumeFog;
		//they start at the nearest volume's near face, as fog-free slices are worth nothing
		var reach = 0.0f;
		var nearest = float.MaxValue;

		if ( world is not null && world.FogVolumes.Count > 0 )
		{
			var eye = new Vector3( block[0], block[1], block[2] );

			var near = world.FogVolumes
				.Where( v => v.IsValid && v.FogStrength > 0.0f && v.BoundingBox.Size.x > 0.0f && v.BoundingBox.Size.y > 0.0f && v.BoundingBox.Size.z > 0.0f )
				.OrderBy( v => Vector3.DistanceBetweenSquared( v.Transform.PointToWorld( v.BoundingBox.Center ), eye ) )
				.Take( MostFogVolumes );

			foreach ( var volume in near )
			{
				var at = FogVolumesAt + ( count * FogVolumeFloats );
				var transform = volume.Transform;
				var centre = transform.PointToWorld( volume.BoundingBox.Center );
				var extents = volume.BoundingBox.Extents * transform.Scale;
				var turn = transform.Rotation;

				block[at + 0] = centre.x;
				block[at + 1] = centre.y;
				block[at + 2] = centre.z;
				block[at + 3] = Math.Clamp( volume.FogStrength, 0.0f, 1.0f );

				block[at + 4] = MathF.Max( MathF.Abs( extents.x ), 0.001f );
				block[at + 5] = MathF.Max( MathF.Abs( extents.y ), 0.001f );
				block[at + 6] = MathF.Max( MathF.Abs( extents.z ), 0.001f );
				block[at + 7] = MathF.Max( volume.FalloffExponent, 0.0f );

				block[at + 8] = turn.x;
				block[at + 9] = turn.y;
				block[at + 10] = turn.z;
				block[at + 11] = turn.w;

				block[at + 12] = volume.Color.r;
				block[at + 13] = volume.Color.g;
				block[at + 14] = volume.Color.b;
				block[at + 15] = 0.0f;

				//The box's farthest corner from the eye: past it there is none of this volume's air
				var away = Vector3.DistanceBetween( centre, eye );
				var corner = MathF.Max( MathF.Abs( extents.x ), MathF.Max( MathF.Abs( extents.y ), MathF.Abs( extents.z ) ) ) * 1.7321f;

				reach = MathF.Max( reach, away + corner );
				nearest = MathF.Min( nearest, away - corner );

				count++;
			}
		}

		block[FogVolumeInfoAt + 0] = count;
		block[FogVolumeInfoAt + 1] = 0.0f;
		block[FogVolumeInfoAt + 2] = 0.0f;
		block[FogVolumeInfoAt + 3] = 0.0f;

		//The sheet's numbers are written once it is drawn (LayFogVolumeSheet, before shading); nought means no
		//sheet, and the reader marches instead
		block[FogSheetAt + 0] = 0.0f;
		block[FogSheetAt + 1] = 0.0f;
		block[FogSheetAt + 2] = 0.0f;
		block[FogSheetAt + 3] = 0.0f;

		CoreVolumeFog.Volumes = count;
		CoreVolumeFog.Reach = reach;
		CoreVolumeFog.Nearest = nearest;

		return count;
	}

	//Where the frame's own four numbers begin.
	private const int SettingsAt = LightsAt + ( MaxLights * CoreLights.LightFloats );

	//And what follows them: the way back out of the screen, then the sky and the sun that lights it.
	private const int ClipToWorldAt = SettingsAt + 4;
	internal const int SkyAt = ClipToWorldAt + 16;
	internal const int SunAt = SkyAt + 12;
	//Which of the debug views the frame draws, where the occlusion picture is, and two spares.

	private const int ShadowAt = SunAt + 8;

	//Last frame's view-projection, for per-pixel motion vectors used by motion blur
	private const int PreviousAt = ShadowAt + 4;

	//Gradient fog parameters; in the frame block so every world shader sees the same fog
	internal const int FogAt = PreviousAt + 16;

	//Baked lighting slots (see bakedMaps in frame_data.slang); all zero means no bake
	internal const int BakedAt = FogAt + 12;

	//Envmap probes: a header (count, size, roughness bands, RGBM scale) then four probes
	internal const int ProbesAt = BakedAt + 36;

	//Time: wall clock, frame delta and scene clock, so all passes of a frame animate in sync
	internal const int TimeAt = ProbesAt + 4 + ( CoreEnvmap.MostProbes * 16 );

	//Sun light after the atmosphere and night amount, computed once here instead of per pixel
	internal const int SunLightAt = TimeAt + 4;

	//Clip-to-world direction matrix without camera position, so the sky doesn't jitter far from origin
	internal const int RayToWorldAt = SunLightAt + 4;

	//Sun shadow data for SunShadow in frame.slang: sun frame, page levels, map slots and movers' square
	internal const int SunToPagesAt = RayToWorldAt + 16;
	internal const int SunPagesAt = SunToPagesAt + 16;
	internal const int SunEyeAt = SunPagesAt + 4;
	internal const int SunMapsAt = SunEyeAt + 4;
	internal const int MoverZoneAt = SunMapsAt + 4;

	//The scene's flat ambient light, and in its alpha how much of the sky's and the
	//probes' it replaces - see Scene.GatherAmbientLight and SceneAmbient in frame.slang.
	internal const int AmbientAt = MoverZoneAt + 4;

	//Exposure and tonemap curve for passes that finish the picture themselves; written every frame
	internal const int FinishAt = AmbientAt + 4;

	//Where the point effects' numbers start, and their slot count - see FrameData.effects and CorePointChain.Upload
	internal const int EffectsAt = FinishAt + 4;
	internal const int EffectSlots = 128;

	//After the effects, the fog volumes: MostFogVolumes boxes of four float4s each - see fogVolumes in
	//frame_data.slang - and one float4 saying how many of them this frame holds. See SetFogVolumes.
	internal const int FogVolumesAt = EffectsAt + ( EffectSlots * 4 );
	internal const int MostFogVolumes = 8;
	internal const int FogVolumeFloats = 16;
	internal const int FogVolumeInfoAt = FogVolumesAt + ( MostFogVolumes * FogVolumeFloats );

	//The sheet of air: its picture, its slices, and the two distances they span - see CoreVolumeFog.
	internal const int FogSheetAt = FogVolumeInfoAt + 4;

	//The sun's two maps read as depth - see SetSunPages and SunSoft in frame.slang.
	internal const int SunDepthAt = FogSheetAt + 4;

	//What water and glass trace their reflections through - see LayScreenTrace.
	internal const int ScreenTraceAt = SunDepthAt + 4;

	//A panorama sky's picture, turn and levels, and the light it is drawn at - see LayPanorama.
	internal const int PanoramaAt = ScreenTraceAt + 4;
	internal const int PanoramaLightAt = PanoramaAt + 4;

	//The sky's light from its capture, and the ambient factor - see CoreSkyLight.Lay and LayPanorama.
	internal const int SkyLightAt = PanoramaLightAt + 4;

	//The area lamps' LTC tables - see CoreAreaLights.
	internal const int AreaAt = SkyLightAt + 4;

	//How shadow edges are filtered - see ShadowFilterQuality.
	internal const int ShadowFilterAt = AreaAt + 4;

	//The portable frame's bake - see CoreBakedLighting.Portable.
	internal const int PortableBakeAt = ShadowFilterAt + 4;

	//The screen's indirect light - see LayScreenLight.
	internal const int ScreenLightAt = PortableBakeAt + 4;

	//The scene's ray structure and its records' picture - see CoreRays.Lay.
	internal const int RaysAt = ScreenLightAt + 4;

	//The probe grid of a scene with no bake: its pictures, counts and cascades - see CoreProbeGrid.Lay.
	internal const int GridAt = RaysAt + 4;

	//The live bounce at the lightmap's detail: its sheet's table index - see CoreLiveBounce.Sheet.
	internal const int BakedTexelsAt = GridAt + 16;

	//The global 2D light, and whether the scene is lit in 2D at all - see SetLight2D.
	internal const int Light2DAt = BakedTexelsAt + 4;

	//How the 2D light's pictures are drawn and where they are - see CoreLight2D.Lay.
	internal const int Light2DSceneAt = Light2DAt + 4;

	//How many 2D lights the block carries and how occluders are lit - see SetLight2D.
	internal const int Light2DInfoAt = Light2DSceneAt + 4;

	//The 2D lights themselves, three float4s each - see lights2d in frame_data.slang.
	internal const int Lights2DAt = Light2DInfoAt + 4;
	internal const int Light2DFloats = 12;

	internal const int FrameFloats = Lights2DAt + ( SceneLight2DSystem.MostLights * Light2DFloats );

	[ConVar( "r_shadow_filter", ConVarFlags.Saved, Min = 0, Max = 2,
		Help = "Shadow edges: 0 one compare, 1 a fixed soft edge, 2 as soft as the light's source makes them (PCSS). Set by Shadow Quality." )]
	internal static int ShadowFilterQuality { get; set; } = 2;

	//The exposure a block was written with - see SetFinish
	internal static float ExposureOf( float[] frame )
		=> frame is null || frame.Length < FrameFloats ? 1.0f : frame[FinishAt + 0];

	internal static void SetFinish( float[] frame, float exposure, float curve, float chain = 0.0f )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		frame[FinishAt + 0] = exposure;
		frame[FinishAt + 1] = curve;
		//Where the world shading's point-effect run starts in the effects - see CorePointChain.UploadInto
		frame[FinishAt + 2] = chain;
		frame[FinishAt + 3] = 0.0f;
	}

	//Puts the sun's shadow in the block - see CoreSunPages, and SunShadow in frame.slang, which reads every one of
	//these. Whether the sun casts at all is the light's Shadows box: with it off nothing is baked and nothing read.
	internal static void SetSunPages( float[] frame, System.Numerics.Matrix4x4 toPages,
		float pageWorld, int levels, float zLow, float zScale,
		float eyeX, float eyeY, uint whole, bool casts,
		uint pagesIndex, uint moversIndex, uint pagesDepthIndex, uint moversDepthIndex,
		float moverX, float moverY, float moverSide )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( frame.AsSpan() );
		System.Runtime.InteropServices.MemoryMarshal.Write( bytes[( SunToPagesAt * 4 )..], in toPages );

		frame[SunPagesAt + 0] = pageWorld;
		frame[SunPagesAt + 1] = levels;
		frame[SunPagesAt + 2] = zLow;
		frame[SunPagesAt + 3] = zScale;

		//The levels that are whole as bits of a float - exact, there are eight.
		frame[SunEyeAt + 0] = eyeX;
		frame[SunEyeAt + 1] = eyeY;
		frame[SunEyeAt + 2] = whole;
		frame[SunEyeAt + 3] = casts ? 1.0f : 0.0f;

		//The two maps' places in the picture table; the two words after them are the lamps' - see SetLampMaps.
		frame[SunMapsAt + 0] = pagesIndex;
		frame[SunMapsAt + 1] = moversIndex;

		//And the same two read as depth, for the search that softens the edge.
		frame[SunDepthAt + 0] = pagesDepthIndex;
		frame[SunDepthAt + 1] = moversDepthIndex;

		//The square of the sun's frame the map of what moves covers: its corner and its side.
		frame[MoverZoneAt + 0] = moverX;
		frame[MoverZoneAt + 1] = moverY;
		frame[MoverZoneAt + 2] = moverSide;
		frame[MoverZoneAt + 3] = 0.0f;

		MoversOn = moverSide > 0.0f;
	}

	//Lamp faces' table indices, compared and as depth, and the movers map's the same - see CoreLampMaps,
	//LampShadow; each lamp's record holds its first layer and which faces read movers
	internal static void SetLampMaps( float[] frame, uint mapIndex, uint depthIndex, uint moversIndex, uint moversDepthIndex )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		frame[SunMapsAt + 2] = depthIndex;
		frame[SunMapsAt + 3] = mapIndex;
		frame[SunDepthAt + 2] = moversIndex;
		frame[SunDepthAt + 3] = moversDepthIndex;
	}

	//Puts the direction-only clip-to-world in the block.
	internal static void SetRayToWorld( float[] frame, System.Numerics.Matrix4x4 rayToWorld )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( frame.AsSpan() );

		System.Runtime.InteropServices.MemoryMarshal.Write( bytes[( RayToWorldAt * 4 )..], in rayToWorld );
	}

	//Puts where one eye stands and how it sees in the block, over what BuildFrame put there - the
	//fields a shader reads at g_frame[Eye()]; see frame_data.slang and CoreSceneDrawer.SecondEye.
	internal static void SetEye( float[] frame, System.Numerics.Matrix4x4 viewProjection, Vector3 camera )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( frame.AsSpan() );

		System.Runtime.InteropServices.MemoryMarshal.Write( bytes, in viewProjection );

		if ( System.Numerics.Matrix4x4.Invert( viewProjection, out var clipToWorld ) )
			System.Runtime.InteropServices.MemoryMarshal.Write( bytes[( ClipToWorldAt * 4 )..], in clipToWorld );

		frame[16] = camera.x;
		frame[17] = camera.y;
		frame[18] = camera.z;
	}

	//Puts the last frame's view-projection in the block.
	internal static void SetPrevious( float[] frame, System.Numerics.Matrix4x4 previous )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		System.Runtime.InteropServices.MemoryMarshal.Write(
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( frame.AsSpan( PreviousAt, 16 ) ),
			in previous );
	}

	//Says how big the window the frame is drawn at is and where the sky's table sits - and
	//that there is no occlusion: that is said by LayOcclusion, once something has drawn some.
	internal static void SetOcclusion( float[] frame, float width, float height, int skyTable = 0 )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		frame[SettingsAt + 0] = 0.0f;
		frame[SettingsAt + 1] = width;
		frame[SettingsAt + 2] = height;

		//A free lane, kept nought; and nothing for water to trace through until the reflections have drawn it.
		frame[SunDepthAt + 2] = 0.0f;
		frame[ScreenTraceAt + 0] = 0.0f;
		frame[ScreenTraceAt + 1] = 0.0f;

		//The sky table's bindless index, nought for none
		frame[SettingsAt + 3] = skyTable;

		frame[ShadowAt + 1] = 0.0f;

		//Laid again after the pre-pass by whichever pass drew them
		Array.Clear( frame, ScreenLightAt, 8 );

		OcclusionOn = false;
		SkyTableOn = skyTable != 0;
	}

	//The occlusion picture surfaces darken by, by table index (g_occlusionMap), laid after the pre-pass
	//stage and before shading; its strength is in the picture
	internal static void LayOcclusion( float[] frame, int picture )
	{
		if ( frame is null || frame.Length < FrameFloats || picture <= 0 ) return;

		frame[SettingsAt + 0] = 1.0f;
		frame[ShadowAt + 1] = picture;

		OcclusionOn = frame[SettingsAt + 1] > 0.0f;
	}

	//The indirect light the occlusion pass gathered off the screen, by table index - see AmbientOcclusion
	internal static void LayScreenLight( float[] frame, int picture, bool traced = false )
	{
		if ( frame is null || frame.Length < FrameFloats || picture <= 0 ) return;

		frame[ScreenLightAt] = picture;
		frame[ScreenLightAt + 1] = traced ? 1.0f : 0.0f;
	}

	//The sheet of air this view drew, by its place in the picture table, with the shape the
	//reader needs to find a point in it - see CoreVolumeFog and FogVolumeSheet in frame.slang.
	internal static void LayFogVolumeSheet( float[] frame, int picture, int slices, float near, float far, int columns )
	{
		if ( frame is null || frame.Length < FrameFloats || picture <= 0 ) return;

		FogSheetLaid = true;

		frame[FogSheetAt + 0] = picture;
		frame[FogSheetAt + 1] = slices;
		frame[FogSheetAt + 2] = near;
		frame[FogSheetAt + 3] = far;

		//How the slices are tiled across the picture, beside the count of the volumes themselves - see
		//CoreVolumeFog.Columns - and how many froxels a tile is, which the reader keeps its filter inside.
		frame[FogVolumeInfoAt + 1] = columns;
		frame[FogVolumeInfoAt + 2] = CoreVolumeFog.Grid.Wide;
		frame[FogVolumeInfoAt + 3] = CoreVolumeFog.Grid.Tall;
	}

	//Lays out the depth chain and solid-scene picture for screen-space reflections on water and glass
	internal static void LayScreenTrace( float[] frame, int chain, int scene )
	{
		if ( frame is null || frame.Length < FrameFloats || chain <= 0 || scene <= 0 ) return;

		frame[ScreenTraceAt + 0] = chain;
		frame[ScreenTraceAt + 1] = scene;
	}

	internal static void LayDepthPicture( float[] frame, int picture )
	{
		if ( frame is null || frame.Length < FrameFloats || picture <= 0 ) return;

		frame[ShadowAt + 2] = picture;
	}

	//Writes the sky's three colours and sun; shader-specific sky settings come from its material
	internal static void SetSky( float[] frame, Scene scene )
	{
		PanoramaOn = false;

		var sky = FirstEnabled<SkyBox2D>( scene );
		if ( sky is null ) return;

		var tint = sky.Tint;

		//Before the colours below, whose table it stands in for - see FillSkyTable
		var panorama = CorePanoramaSky.Of( sky.SkyMaterial, tint );
		if ( panorama is not null ) LayPanorama( frame, panorama );

		var sun = FirstEnabled<DirectionalLight>( scene );

		//Noon palette for zenith, horizon and ground, above one; the shader shifts it with the sun
		SetSky( frame,
			new Color( 0.22f, 0.42f, 0.86f ) * tint * 1.6f,
			new Color( 0.62f, 0.72f, 0.86f ) * tint * 1.4f,
			new Color( 0.16f, 0.15f, 0.14f ) * tint,
			//Where the sun is, rather than where it points: a sky draws the disc.
			sun is null ? default : -sun.WorldRotation.Forward.Normal,
			//The light's own colour at world strength; the sky derives its brightness from the light the world gets
			sun is null ? default : sun.EmittedColor );
	}

	//Whether this frame's sky is a panorama, whose table is laid out by direction and made from the picture
	internal static bool HasPanorama( float[] frame )
		=> frame is not null && frame.Length >= FrameFloats && frame[PanoramaAt + 3] > 0.0f;

	//A panorama sky into the block - see frame_data.slang's panorama
	private static void LayPanorama( float[] frame, CorePanoramaSky.Sky sky )
	{
		frame[PanoramaAt + 1] = sky.Turn;
		frame[PanoramaAt + 3] = 1.0f;
		PanoramaOn = true;

		//A panorama's light is already irradiance over pi, so all of it - see SkyAmbientScale
		frame[SkyLightAt + 3] = 1.0f;

		frame[PanoramaLightAt + 0] = sky.Light.X;
		frame[PanoramaLightAt + 1] = sky.Light.Y;
		frame[PanoramaLightAt + 2] = sky.Light.Z;
	}

	//What the scene's sky is drawn with - the SkyBox2D's material - or null where the scene has no sky. Bound
	//for the pass that draws the sky and for the one that fills its table, so both read the same numbers.
	internal static Material SkyMaterialOf( Scene scene )
		=> scene is null ? null : FirstEnabled<SkyBox2D>( scene )?.SkyMaterial;

	//The first of these in the scene that is valid and switched on, or null. A walk rather than FirstOrDefault,
	//which boxes the scene's walk onto the heap - and the sky and the sun are asked for by every view, every frame.
	internal static T FirstEnabled<T>( Scene scene ) where T : Component
	{
		foreach ( var one in scene.GetAllComponents<T>() )
		{
			if ( one.IsValid() && one.Enabled ) return one;
		}

		return null;
	}

	//Whether a built frame has a sky in it.
	internal static bool HasSky( float[] frame )
		=> frame is not null && frame.Length >= FrameFloats && frame[SkyAt + 3] > 0.0f;

	//The same, said outright rather than read off a scene. The frame block does not need a scene to describe a sky,
	//and something that draws one without a scene - a preview of a material, a test - should not have to invent one.
	internal static void SetSky( float[] frame, Color zenith, Color horizon, Color ground,
		Vector3 sun, Color sunColour )
	{
		if ( frame is null || frame.Length < FrameFloats ) return;

		WriteColour( frame, SkyAt + 0, zenith );
		WriteColour( frame, SkyAt + 4, horizon );
		WriteColour( frame, SkyAt + 8, ground );

		//The alpha of the zenith is the flag: there is a sky here.
		frame[SkyAt + 3] = 1.0f;

		if ( sun.IsNearlyZero() )
		{
			//Nothing in the sky but the sky: a shader's HasSun is false, so its sun, its moon and its stars
			//are all paths no pixel takes - and a pipeline told so does not carry them. See SkySpecialize.
			NightOn = false;
			SunDiscOn = false;
			return;
		}

		var towards = sun.Normal;

		frame[SunAt + 0] = towards.x;
		frame[SunAt + 1] = towards.y;
		frame[SunAt + 2] = towards.z;
		frame[SunAt + 3] = 1.0f;

		WriteColour( frame, SunAt + 4, sunColour );

		//The disc's width as a cosine: wider than the real half degree, which reads better
		frame[SunAt + 7] = MathF.Cos( 1.5f.DegreeToRadian() );

		//Air mass the sunlight crosses; the 0.12 offset keeps it finite at and below the horizon
		var height = Math.Clamp( towards.y, 0.0f, 1.0f );
		var air = ( 1.0f / ( height + 0.12f ) ) - ( 1.0f / 1.12f );

		//Blue scatters out of a beam about four times as readily as red, which is why a low sun is orange;
		//divided by the overhead crossing so a noon sun is its given colour
		frame[SunLightAt + 0] = sunColour.r * MathF.Exp( -0.15f * air );
		frame[SunLightAt + 1] = sunColour.g * MathF.Exp( -0.29f * air );
		frame[SunLightAt + 2] = sunColour.b * MathF.Exp( -0.66f * air );

		//How far into night: a band about the horizon, as dusk lights the sky from below; gone nine degrees under
		var dusk = Math.Clamp( ( towards.y + 0.16f ) / 0.22f, 0.0f, 1.0f );

		frame[SunLightAt + 3] = 1.0f - ( dusk * dusk * ( 3.0f - ( 2.0f * dusk ) ) );

		//Which halves of the sky's pixel stage this frame reaches, from NightFactor's word: moon and stars above
		//nought, sun below one, both through dusk
		NightOn = frame[SunLightAt + 3] > 0.0f;
		SunDiscOn = frame[SunLightAt + 3] < 1.0f;
	}

	private static void WriteColour( float[] frame, int at, Color colour )
	{
		frame[at + 0] = colour.r;
		frame[at + 1] = colour.g;
		frame[at + 2] = colour.b;
	}

	//The view's eight clip corners in the world; Vulkan depth is 0 to 1, near z = 0, far z = 1
	internal static bool FillFrustumCorners( System.Numerics.Matrix4x4 clipToWorld, Span<Vector3> corners )
	{
		if ( corners.Length < 8 ) return false;

		ReadOnlySpan<float> ndc =
		[
			-1, -1, 0,
			1, -1, 0,
			1, 1, 0,
			-1, 1, 0,
			-1, -1, 1,
			1, -1, 1,
			1, 1, 1,
			-1, 1, 1
		];

		for ( var i = 0; i < 8; i++ )
		{
			var clip = System.Numerics.Vector4.Transform(
				new System.Numerics.Vector4( ndc[i * 3], ndc[i * 3 + 1], ndc[i * 3 + 2], 1.0f ),
				clipToWorld );

			if ( MathF.Abs( clip.W ) < 1e-8f ) return false;

			var inv = 1.0f / clip.W;
			corners[i] = new Vector3( clip.X * inv, clip.Y * inv, clip.Z * inv );
		}

		return true;
	}

	//Which cube face a direction from a point light lands on. Matches PointFace in the shader: +X, -X, +Y, -Y, +Z, -Z.
	internal static int PointFaceIndex( Vector3 fromLight )
	{
		var ax = MathF.Abs( fromLight.x );
		var ay = MathF.Abs( fromLight.y );
		var az = MathF.Abs( fromLight.z );

		if ( ax >= ay && ax >= az ) return fromLight.x >= 0.0f ? 0 : 1;
		if ( ay >= az ) return fromLight.y >= 0.0f ? 2 : 3;
		return fromLight.z >= 0.0f ? 4 : 5;
	}

	//Cube faces that can shadow what the camera sees: the face the eye sits in, plus each frustum
	//corner's. A caster on another face cannot fall between the light and a receiver on these.
	internal static int PointFaceMask( Vector3 light, Vector3 camera, ReadOnlySpan<Vector3> corners )
	{
		var mask = 1 << PointFaceIndex( camera - light );

		for ( var i = 0; i < corners.Length; i++ )
			mask |= 1 << PointFaceIndex( corners[i] - light );

		return mask & 0x3F;
	}

	//The shadow atlas tiles each light held last build; one atlas and owner per device, so one map
	private static readonly Dictionary<Light, (int At, int Count)> lampHeld = new();

	internal static int KeepBestFaces( int mask, int remaining, Vector3 towardCamera )
	{
		mask &= 0x3F;
		remaining = Math.Clamp( remaining, 0, 6 );
		if ( remaining <= 0 ) return 0;

		if ( mask == 0 ) mask = 1 << PointFaceIndex( towardCamera );

		var have = System.Numerics.BitOperations.PopCount( (uint)mask );
		if ( have <= remaining ) return mask;

		Span<float> scores = stackalloc float[6];
		scores[0] = towardCamera.x;
		scores[1] = -towardCamera.x;
		scores[2] = towardCamera.y;
		scores[3] = -towardCamera.y;
		scores[4] = towardCamera.z;
		scores[5] = -towardCamera.z;

		var kept = 0;

		for ( var n = 0; n < remaining; n++ )
		{
			var best = -1;
			var bestScore = float.NegativeInfinity;

			for ( var face = 0; face < 6; face++ )
			{
				if ( ( mask & ( 1 << face ) ) == 0 ) continue;
				if ( ( kept & ( 1 << face ) ) != 0 ) continue;
				if ( scores[face] <= bestScore ) continue;

				best = face;
				bestScore = scores[face];
			}

			if ( best < 0 ) break;
			kept |= 1 << best;
		}

		return kept;
	}

	//Push block size: 96 bytes of matrix, tint and chart, plus per-draw numbers; 128 is Vulkan's minimum
	internal const int PushSize = 128;

	//A transform's world matrix (scale, rotate, translate), built directly rather than by multiplying
	internal static System.Numerics.Matrix4x4 WorldOf( Transform transform )
	{
		var world = System.Numerics.Matrix4x4.CreateFromQuaternion( new System.Numerics.Quaternion(
			transform.Rotation.x, transform.Rotation.y, transform.Rotation.z, transform.Rotation.w ) );

		world.M11 *= transform.Scale.x; world.M12 *= transform.Scale.x; world.M13 *= transform.Scale.x;
		world.M21 *= transform.Scale.y; world.M22 *= transform.Scale.y; world.M23 *= transform.Scale.y;
		world.M31 *= transform.Scale.z; world.M32 *= transform.Scale.z; world.M33 *= transform.Scale.z;

		world.M41 = transform.Position.x;
		world.M42 = transform.Position.y;
		world.M43 = transform.Position.z;

		return world;
	}

	internal static byte[] ScenePush( System.Numerics.Matrix4x4 world, Color tint,
		System.Numerics.Vector4 numbers = default )
	{
		var push = new byte[PushSize];

		ScenePushInto( push, world, tint, numbers );

		return push;
	}

	//Writes the push block into a caller's span, avoiding an array per queued part
	internal static void ScenePushInto( Span<byte> push, System.Numerics.Matrix4x4 world, Color tint,
		System.Numerics.Vector4 numbers = default, ReadOnlySpan<float> drawn = default )
	{
		System.Runtime.InteropServices.MemoryMarshal.Write( push, in world );
		System.Runtime.InteropServices.MemoryMarshal.Write( push[64..],
			new System.Numerics.Vector4( tint.r, tint.g, tint.b, tint.a ) );

		//Four per-object numbers for the shader (e.g. voxel size, effect progress); usually zero
		System.Runtime.InteropServices.MemoryMarshal.Write( push[80..], in numbers );

		//The draw's own Attribute( ... ) numbers, cleared since the arena is reused across frames
		var tail = push[96..];

		tail.Clear();

		if ( drawn.IsEmpty ) return;

		var room = System.Math.Min( drawn.Length, tail.Length / 4 );

		System.Runtime.InteropServices.MemoryMarshal.AsBytes( drawn[..room] ).CopyTo( tail );
	}

	//The six planes of a view-projection frustum, for keeping what cannot be seen off the queue
	//entirely. Planes face inward; a sphere is visible when it is not fully behind any of them.
	internal static void FrustumPlanes( in System.Numerics.Matrix4x4 vp, Span<System.Numerics.Vector4> planes )
	{
		//Row vectors: clip.x = dot(v, column 0), so the planes are sums and differences of columns 0..2 with column 3
		var c0 = new System.Numerics.Vector4( vp.M11, vp.M21, vp.M31, vp.M41 );
		var c1 = new System.Numerics.Vector4( vp.M12, vp.M22, vp.M32, vp.M42 );
		var c2 = new System.Numerics.Vector4( vp.M13, vp.M23, vp.M33, vp.M43 );
		var c3 = new System.Numerics.Vector4( vp.M14, vp.M24, vp.M34, vp.M44 );

		planes[0] = Unit( c3 + c0 ); //Left
		planes[1] = Unit( c3 - c0 ); //Right
		planes[2] = Unit( c3 + c1 ); //Bottom
		planes[3] = Unit( c3 - c1 ); //Top
		planes[4] = Unit( c2 );      //Near, with depth in [0..1]
		planes[5] = Unit( c3 - c2 ); //Far
	}

	//Normalises a plane once so sphere tests skip square roots; zero normals are left alone
	private static System.Numerics.Vector4 Unit( System.Numerics.Vector4 plane )
	{
		var length = MathF.Sqrt( ( plane.X * plane.X ) + ( plane.Y * plane.Y ) + ( plane.Z * plane.Z ) );

		return length > 0.0f ? plane / length : plane;
	}

	//The same six planes as the engine's own frustum, for the game code that asks a view what it can see.
	internal static Frustum FrustumOf( in System.Numerics.Matrix4x4 viewProjection )
	{
		Span<System.Numerics.Vector4> planes = stackalloc System.Numerics.Vector4[6];
		FrustumPlanes( viewProjection, planes );

		//Theirs measure "inside is positive"; a plane here keeps its distance the other way round.
		static Plane Of( System.Numerics.Vector4 plane )
			=> new( new Vector3( plane.X, plane.Y, plane.Z ), -plane.W );

		return new Frustum( Of( planes[1] ), Of( planes[0] ), Of( planes[3] ), Of( planes[2] ),
			Of( planes[4] ), Of( planes[5] ) );
	}

	//Whether any part of this sphere is inside the frustum. The planes arrive with normals of length one -
	//see Unit - so what the dot product gives is a distance and the radius is compared with it as it stands.
	[System.Runtime.CompilerServices.MethodImpl(
		System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	internal static bool SphereVisible( ReadOnlySpan<System.Numerics.Vector4> planes, Vector3 center, float radius )
	{
		var at = new System.Numerics.Vector4( center.x, center.y, center.z, 1.0f );

		foreach ( var plane in planes )
		{
			if ( System.Numerics.Vector4.Dot( plane, at ) < -radius ) return false;
		}

		return true;
	}

	//Distance where a lamp drops below the cutoff: d = R / (1 + R*sqrt(c/b)); never above the radius
	internal static float EffectiveRange( float radius, Color colour, float falloff = 1.0f )
	{
		var cutoff = Rinten.Rendering.DrawSettings.LightCutoff;

		if ( cutoff <= 0.0f || radius <= 0.0f ) return radius;

		//The solve is for inverse square; a slower falloff carries further, so the radius itself
		if ( falloff < 1.0f ) return radius;

		//The colour carries the brightness (see BuildFrame), so the brightest channel sets how far it is seen
		var brightness = MathF.Max( MathF.Max( colour.r, colour.g ), colour.b );

		if ( brightness <= 0.0f ) return 0.0f;

		return radius / ( 1.0f + ( radius * MathF.Sqrt( cutoff / brightness ) ) );
	}

	//How far a light is from mattering: zero for the sun, else camera distance less its reach; beyond, dropped
	private static float Reach( Light light, Vector3 camera )
	{
		if ( light is DirectionalLight ) return 0.0f;

		var radius = light switch
		{
			PointLight point => EffectiveRange( point.Radius, point.EmittedColor, point.Falloff ),
			SpotLight spot => EffectiveRange( spot.Radius, spot.EmittedColor, spot.Falloff ),
			_ => 0.0f,
		};

		var distance = camera.Distance( light.WorldPosition ) - radius;

		//Well out of reach of anything on screen. Not a hard frustum test - a light behind the camera still
		//lights what is in front of it - only a way of not spending a slot on a lamp three rooms away.
		return distance > 200.0f ? float.MaxValue : MathF.Max( distance, 0.0f );
	}

	//The same, for a light put into the world without a component.
	private static float Reach( SceneLight light, Vector3 camera )
	{
		if ( light is SceneDirectionalLight ) return 0.0f;

		var distance = camera.Distance( light.Position ) - EffectiveRange( light.Radius, light.LightColor, light.Falloff );

		return distance > 200.0f ? float.MaxValue : MathF.Max( distance, 0.0f );
	}

	//A light as the frame block holds it, from a Light component or a scene light
	private readonly record struct LightSource( int Kind, Vector3 Position, Vector3 Forward, Color Colour,
		float Radius, float Falloff, float SourceRadius, float ConeInner, float ConeOuter, int Withholds = 0,
		int Shape = 0, float HalfWidth = 0.0f, float HalfHeight = 0.0f, float Turn = 0.0f )
	{
		//What a light component is kept out of: one for the diffuse, two for the highlight - see Light.Contribution.
		private static int WithheldBy( Light light )
			=> ( ( light.Contribution & Light.LightContribution.Diffuse ) != 0 ? 0 : 1 )
				| ( ( light.Contribution & Light.LightContribution.Specular ) != 0 ? 0 : 2 );

		/// <summary>Kind is nought for the sun, one for a point, two for a spot - the shader's own numbering.</summary>
		public static LightSource Of( Light light ) => light switch
		{
			SpotLight spot => new( 2, spot.WorldPosition, spot.WorldRotation.Forward.Normal, spot.EmittedColor,
				spot.Radius, spot.Falloff, MathF.Max( spot.SourceRadius, 0.0f ), spot.ConeInner, spot.ConeOuter, WithheldBy( spot ) ),
			PointLight { AreaShape: not PointLight.AreaShapes.Point } point => Shaped( point ),
			PointLight point => new( 1, point.WorldPosition, point.WorldRotation.Forward.Normal, point.EmittedColor,
				point.Radius, point.Falloff, MathF.Max( point.SourceRadius, 0.0f ), 0, 0, WithheldBy( point ) ),
			_ => new( 0, light.WorldPosition, light.WorldRotation.Forward.Normal, light.EmittedColor,
				0, light.Falloff, MathF.Max( light.SourceRadius, 0.0f ), 0, 0, WithheldBy( light ) ),
		};

		//A point with a shape: its half extents, and its width's turn about forward from LampAxes' right - see AreaLamp
		private static LightSource Shaped( PointLight point )
		{
			var rotation = point.WorldRotation;
			var forward = rotation.Forward.Normal;
			var size = point.AreaSize;

			var ( right0, up0, _ ) = CoreSunPages.LightAxes( forward );
			var right = rotation.Right.Normal;
			var turn = MathF.Atan2( right.Dot( up0 ), right.Dot( right0 ) );

			var tube = point.AreaShape == PointLight.AreaShapes.Tube;
			var halfWidth = MathF.Max( size.x * 0.5f, 0.001f );
			var halfHeight = tube ? MathF.Max( point.SourceRadius, 0.005f )
				: point.AreaShape == PointLight.AreaShapes.Disc ? halfWidth : MathF.Max( size.y * 0.5f, 0.001f );

			//Its shadow as soft as the shape is wide
			var width = MathF.Max( point.SourceRadius, tube ? halfHeight : MathF.Max( halfWidth, halfHeight ) );

			return new( 1, point.WorldPosition, forward, point.EmittedColor, point.Radius, point.Falloff, width, 0, 0, WithheldBy( point ),
				(int)point.AreaShape, halfWidth, halfHeight, turn );
		}

		/// <summary>A scene light's colour is its brightness; it casts no shadow, so it has no width.</summary>
		public static LightSource Of( SceneLight light ) => light switch
		{
			SceneSpotLight spot => new( 2, spot.Position, spot.Rotation.Forward.Normal, spot.LightColor,
				spot.Radius, Math.Clamp( spot.Falloff, 0.0f, 2.0f ), 0, spot.ConeInner, spot.ConeOuter ),
			SceneDirectionalLight sun => new( 0, sun.Position, sun.Rotation.Forward.Normal, sun.LightColor,
				0, 1, 0, 0, 0 ),
			_ => new( 1, light.Position, light.Rotation.Forward.Normal, light.LightColor,
				light.Radius, Math.Clamp( light.Falloff, 0.0f, 2.0f ), 0, 0, 0 ),
		};
	}

	//Texture mip bias for this frame (see CoreUpscale.MipBias), in viewInfo's spare fourth lane
	//One plus the frame's turn of hashed cut-outs, nought where no temporal history gathers them - see foliage.shader
	internal static void SetHashedCut( float[] block, int turn )
	{
		if ( block is null || block.Length < ShadowFilterAt + 4 ) return;

		block[ShadowFilterAt + 1] = turn;
	}

	internal static void SetMipBias( float[] block, float bias )
	{
		if ( block is null || block.Length < ShadowAt + 4 ) return;

		block[ShadowAt + 3] = bias;
	}

	internal static void SetDebugView( float[] block, SceneCameraDebugMode view )
	{
		if ( block is null || block.Length < ShadowAt + 4 ) return;

		block[ShadowAt + 0] = (int)view;

		DebugViewOn = view != SceneCameraDebugMode.Normal;
	}

	//Whether the last written frame block asks for a debug view; gates the kDebugOn pipeline variant
	internal static bool DebugViewOn { get; private set; }

	//Whether the frame's sky is a panorama - see kPanoramaOn, constant 18, past the shader folds
	internal static bool PanoramaOn { get; private set; }

	internal const int PanoramaFold = CoreResources.ShaderFoldsEnd;

	//Whether a lamp in the block has a shape - see kAreaOn, constant 19
	internal static bool AreaOn { get; private set; }

	internal const int AreaFold = PanoramaFold + 1;

	//Whether the frame can carry the probe grid: rays on and no bake to read instead - see kProbeGridOn, constant 20
	internal static bool ProbeGridOn => CoreRays.On && !BakedOn;

	internal const int ProbeGridFold = AreaFold + 1;

	//Whether the frame drew itself a sky table - see kSkyTableOn.
	internal static bool SkyTableOn { get; private set; }

	//Whether it has fog in it - see kFogOn.
	internal static bool FogOn { get; private set; }

	//Whether it has fog volumes in it - what a pixel would march through.
	internal static bool FogVolumes { get; private set; }

	//Whether the fog sheet is in this frame's block (see kFogMarch); false until LayFogVolumeSheet, and a
	//pipeline baked before marches, correctly but slower
	internal static bool FogSheetLaid { get; private set; }

	//Whether the sun drew a map of what moves - see kMoversOn.
	internal static bool MoversOn { get; private set; }

	//And whether it drew an occlusion picture - see kOcclusionOn.
	internal static bool OcclusionOn { get; private set; }

	//Whether the scene's baked lighting was written into the block - see kBakedOn.
	internal static bool BakedOn { get; private set; }

	//Whether the sky written into the block has any night in it - see kNightOn.
	internal static bool NightOn { get; private set; }

	//And whether it has any day - see kSunDiscOn.
	internal static bool SunDiscOn { get; private set; }

	//Bakes a pass for a material's far calls (kFarShading, constant 13); not ShadeSpecialize's, as it is the call's
	internal const int FarShading = 1 << 13;

	//World pixel stage specialization bits, taken from what the frame block actually holds
	//1 probes, 2 debug, 4 light grid, 8 lamp, 16 sky table, 32 fog, 64 occlusion, 128 lights, 256 movers...
	internal static int ShadeSpecialize()
		=> ( CoreEnvmap.Written > 0 ? 1 : 0 )
			| ( DebugViewOn ? 2 : 0 )
			| ( CoreLights.Tiles > 0 ? 4 : 0 )
			| ( CoreLights.Lamps > 0 ? 8 : 0 )
			| ( SkyTableOn ? 16 : 0 )
			| ( FogOn ? 32 : 0 )
			| ( OcclusionOn ? 64 : 0 )
			| ( CoreLights.Count > 1 ? 128 : 0 )
			| ( MoversOn ? 256 : 0 )
			| ( CoreLights.Decals > 0 ? 512 : 0 )
			| ( BakedOn ? 1024 : 0 )
			| ( FogVolumes && !FogSheetLaid ? 16384 : 0 )
			| ( PanoramaOn ? 1 << PanoramaFold : 0 )
			| ( AreaOn ? 1 << AreaFold : 0 )
			| ( ProbeGridOn ? 1 << ProbeGridFold : 0 );

	//Sky pass specialization: the world bits, the frame's sky bits, and the sky material's Fold() ids
	internal static int SkySpecialize( CoreMaterial sky = null )
		=> ShadeSpecialize()
			| ( NightOn ? 2048 : 0 )
			| ( SunDiscOn ? 4096 : 0 )
			| CoreResources.FoldBits( sky );

	//The same picture, with a skeleton in front of it.
	internal static ulong MakeSkinnedPipeline() => MakePipeline( new PipelineKind( true, false, false ) );

	internal static ulong MakeScenePipeline() => MakePipeline( new PipelineKind( false, false, false ) );

	//A mesh kind's pipeline, baked once; translucency blends and tests depth without writing, so panes do
	//not hide each other; solid writes depth and draws first
	internal static ulong PipelineFor( in PipelineKind kind, Dictionary<PipelineKind, ulong> cache )
	{
		//Cache failures too, so a broken shader isn't recompiled every frame; cleared when the device resets
		if ( cache.TryGetValue( kind, out var made ) ) return made;

		made = MakePipeline( kind );

		//A pass carrying the frame's point effects that is still compiling beside the
		//frame is not remembered as broken: asked again next frame, it may be there.
		if ( made == 0 && chainWaiting )
		{
			chainWaiting = false;
			return 0;
		}

		cache[kind] = made;

		if ( made != 0 ) Baked( kind, cache.Count );

		return made;
	}

	//Whether the pipeline MakePipeline just could not make is only still compiling - see PipelineFor.
	private static bool chainWaiting;

	//Says once a session when a shader passes MostBuilds, each a compile mid-frame; a material's features times the
	//frame's targets reach a few dozen as a matter of course, and a reload of the shaders counts them all again
	private static void Baked( PipelineKind kind, int live )
	{
		var name = string.IsNullOrWhiteSpace( kind.Shader ) ? StandardShader : kind.Shader;

		builds.TryGetValue( name, out var was );

		var now = was + 1;

		builds[name] = now;

		if ( now < MostBuilds || !toldBuilds.Add( name ) ) return;

		Log.Info( $"{name} is on {now} builds ({live} pipelines live) - many combos compile mid-frame; " +
			$"the latest is combo {kind.Combo ?? "none"}" );
	}

	//Builds of one shader past which a build-up is worth a line, and the shaders already told of
	private const int MostBuilds = 64;
	private static readonly HashSet<string> toldBuilds = new( StringComparer.Ordinal );

	//How many builds of each shader have been baked. Cleared with the caches.
	private static readonly Dictionary<string, int> builds = new( StringComparer.Ordinal );

	//What has been baked, for a report and for the tests.
	internal static IReadOnlyDictionary<string, int> Builds => builds;

	//Forgotten with the pipelines they counted.
	internal static void ForgetBuilds() => builds.Clear();

	//Picks the vertex and pixel stages a pipeline kind needs (skinned, world picture, etc.)

	private static (CoreShaderFile.Stage Vertex, CoreShaderFile.Stage Pixel) StagesFor( PipelineKind kind )
	{
		var vertex = kind.Morphed
			? kind.Skinned ? CoreShaderFile.Stage.MorphedSkinnedVertex : CoreShaderFile.Stage.MorphedVertex
			: kind.Skinned ? CoreShaderFile.Stage.SkinnedVertex : CoreShaderFile.Stage.Vertex;

		//The screen takes one attachment and the world's picture takes two. A shadow pass takes
		//none at all - it writes depth - so it wants the plain stage whatever it is drawing into.
		var scene = kind.Target == CoreRender.TargetKind.Scene && !kind.DepthOnly;

		//Whether this pass is the last over the picture, so it applies the curve - see PipelineKind.Finishes
		var finishes = kind.Finishes && !kind.DepthOnly;

		var pixel = ( kind.Skinned, scene, finishes ) switch
		{
			(true, true, _) => CoreShaderFile.Stage.SkinnedScenePixel,
			(true, false, true) => CoreShaderFile.Stage.SkinnedScreenPixel,
			(true, false, false) => CoreShaderFile.Stage.SkinnedPixel,
			(false, true, _) => CoreShaderFile.Stage.ScenePixel,
			(false, false, true) => CoreShaderFile.Stage.ScreenPixel,
			(false, false, false) => CoreShaderFile.Stage.Pixel,
		};

		//Nothing to write: the surface's DEPTH_ONLY stage, so the pre-pass does not shade; without one, the above
		if ( kind.DepthOnly )
			pixel = CoreShaderFile.Stage.DepthPixel;

		if ( kind.Cluster && kind.Target == CoreRender.TargetKind.Velocity )
			return (CoreShaderFile.Stage.ClusterVelocityVertex, CoreShaderFile.Stage.ClusterVelocityPixel);

		if ( kind.Cluster )
			return (CoreShaderFile.Stage.ClusterVertex,
				kind.DepthOnly ? CoreShaderFile.Stage.ClusterDepthPixel
				: finishes ? CoreShaderFile.Stage.ClusterScreenPixel
				: CoreShaderFile.Stage.ClusterPixel);

		//Instanced runs (portable frame): world or sun-map pixel stage only; never skinned or morphed
		if ( kind.Instanced )
			return (CoreShaderFile.Stage.InstancedVertex,
				kind.DepthOnly ? CoreShaderFile.Stage.InstancedDepthPixel : CoreShaderFile.Stage.InstancedScreenPixel);

		return (vertex, pixel);
	}

	//The shaders already complained about, and whether the line that did it had a reason on the end of it.
	private static readonly Dictionary<string, bool> saidBroken = new();

	//Log a broken shader once, plus once more if the reason only arrives on a later attempt
	private static bool SaysBroken( string shader, string reason )
	{
		var name = shader ?? "";
		var withReason = !string.IsNullOrWhiteSpace( reason );

		if ( saidBroken.TryGetValue( name, out var saidWithReason ) && ( saidWithReason || !withReason ) )
			return false;

		saidBroken[name] = withReason;
		return true;
	}

	//Why a shader did not load. A file that is not there never reached the compiler, so
	//the compiler has no reason to give - its last error is empty, or somebody else's.
	private static string WhyNot( string shader )
	{
		var real = CoreShaderFile.Real( shader );

		return CoreShaderFile.Source( real ) is null && CoreShaderFile.Load( real ) is null
			? "there is no file by that name"
			: CoreRenderDevice.ShaderError;
	}

	private static ulong MakePipeline( PipelineKind kind )
	{
		//The material's shader, else the standard surface; light passes use the engine's depth shader
		var named = !string.IsNullOrWhiteSpace( kind.Shader );
		var name = named ? kind.Shader : StandardShader;

		//The depth pre-pass keeps the surface's shader, since its vertex stage may place the geometry

		//A shadow is depth only, by the engine's shader, so material features do not apply
		var combo = kind.Combo;

		//Whether this is that pass, and so whether the states below are the depth-only ones.
		var depthOnly = kind.DepthOnly;

		var stages = CoreResources.Shader( name, combo, kind.Stereo );

		//No stereo build: return 0 and flag StereoIncomplete so the next frame renders one eye at a time
		if ( stages is null && kind.Stereo )
		{
			if ( !StereoIncomplete )
			{
				StereoIncomplete = true;

				Log.Warning( $"{name} has no two-view build, so both of a headset's eyes cannot be drawn " +
					"in one pass - drawing an eye at a time instead. Export again to bake it." );
			}

			return 0;
		}

		if ( stages is null && named )
		{
			//A post pass that fails to compile draws nothing; the standard surface as fallback blacked the screen
			if ( kind.Post )
			{
				if ( SaysBroken( kind.Shader, CoreRenderDevice.ShaderError ) )
					Log.Warning( $"{kind.Shader} did not load - the pass is skipped: {WhyNot( kind.Shader )}" );

				return 0;
			}

			//With the reason. Without it this line says a shader is broken and leaves whoever reads
			//it to guess between a syntax error, a missing include and a file that is not there.
			if ( SaysBroken( kind.Shader, CoreRenderDevice.ShaderError ) )
				Log.Warning( $"{kind.Shader} did not load - drawing with {StandardShader} instead: {WhyNot( kind.Shader )}" );
			stages = CoreResources.Shader( StandardShader );

			//Drawn by the standard surface, so marked for the cluster path (CoreResources.StandsIn)
			CoreResources.StandsIn( kind.Shader );
		}

		if ( stages is null )
		{
			Log.Warning( $"{name} did not load: {CoreRenderDevice.ShaderError}" );
			return 0;
		}

		var (vertexStage, pixelStage) = StagesFor( kind );

		//What the driver is about to compile, for its own statistics - see VkPipelines.Label
		VkPipelines.Label = VkPipelines.Labelling ? $"{name} {vertexStage}/{pixelStage} spec {kind.Specialize}" : null;

		//The finishing pass's point effects, compiled with the stage (CorePointChain.ScreenText) beside the frame;
		//nought and not remembered until then, the frame drawing through its compose
		byte[] chained = null;

		if ( kind.ScreenChain is not null && kind.Finishes && !kind.DepthOnly )
		{
			chained = CoreResources.ScreenChainWords( name, combo, pixelStage, kind.ScreenChain, out var waiting );

			if ( chained is null )
			{
				if ( waiting ) chainWaiting = true;
				else CorePointChain.Refuse( kind.ScreenChain );

				return 0;
			}
		}

		//The cluster pass has no fallback: a surface without the stages is drawn the ordinary
		//way instead, and the caller asks first - see CoreSceneDrawer.ShadesByPicture.
		if ( kind.Cluster )
		{
			var clusterVertex = stages[(int)vertexStage];
			var clusterPixel = chained ?? stages[(int)pixelStage];

			//Only a surface that moves its corners has the pair; the rest have no motion of their own to draw
			if ( kind.Target == CoreRender.TargetKind.Velocity )
			{
				return clusterVertex is null || clusterPixel is null ? 0 : CoreRenderDevice.CreatePipeline( clusterVertex, clusterPixel,
					CoreRender.VertexLayoutKind.None, depthTest: true, CoreRender.Topology.Triangles, blend: false,
					depthWrite: false, cullBack: !kind.TwoSided, target: kind.Target,
					compare: CoreRenderDevice.DepthCompare.Equal );
			}

			if ( clusterVertex is null || clusterPixel is null )
			{
				//Said, since a cluster pass that will not bake drops the surface off the cluster path silently
				if ( SaysBroken( $"{name}|{vertexStage}|{pixelStage}", null ) )
					Log.Warning( $"{name}: no {( clusterVertex is null ? vertexStage : pixelStage )} stage, so no cluster pass" );

				return 0;
			}

			//Cluster shading tests the pre-pass depth and writes none; movers' depth comes from their own pass
			return CoreRenderDevice.CreatePipeline( clusterVertex, clusterPixel, CoreRender.VertexLayoutKind.None,
				depthTest: true, CoreRender.Topology.Triangles, blend: false,
				depthWrite: kind.DepthOnly || kind.Moves,
				cullBack: !kind.TwoSided,

				//Passed with the target: the mover pre-pass draws depth alone, and a colour attachment would not match
				depthOnly: kind.DepthOnly,
				target: kind.Target, pictureFormat: kind.Target == CoreRender.TargetKind.Picture ? kind.PictureFormat : -1,
				compare: CoreRenderDevice.DepthCompare.LessOrEqual,

				//The screen-covering pass, so worth baking several ways - see ShadeSpecialize
				specialize: kind.Specialize );
		}

		//No skinned stage draws the bind pose; no world-picture stage writes colour only, losing reflections
		var vertex = stages[(int)vertexStage] ?? stages[(int)CoreShaderFile.Stage.Vertex];
		var pixel = chained ?? stages[(int)pixelStage]
			?? stages[(int)( kind.Skinned ? CoreShaderFile.Stage.SkinnedPixel : CoreShaderFile.Stage.Pixel )]
			?? stages[(int)CoreShaderFile.Stage.Pixel];

		//A pass drawn by one of its file's own entries rather than by MainPs.
		if ( kind.Post && kind.Entry is not null )
			pixel = CoreResources.Entry( name, combo, kind.Entry, CoreShaderCompiler.Stage.Pixel );

		if ( vertex is null || pixel is null )
		{
			Log.Warning( $"{name} has no {vertexStage}/{( kind.Entry ?? pixelStage.ToString() )} to draw with: {CoreRenderDevice.ShaderError}" );
			return 0;
		}

		//None at all where the shader reads its own vertices: there is no vertex buffer bound for a
		//layout to describe, and declaring one would have the device read a buffer nobody gave it.
		var layout = kind.Pulled
			? CoreRender.VertexLayoutKind.None
			: kind.Morphed
				? kind.Skinned ? CoreRender.VertexLayoutKind.MorphedSkinned : CoreRender.VertexLayoutKind.Morphed
				: kind.Skinned ? CoreRender.VertexLayoutKind.Skinned : CoreRender.VertexLayoutKind.Engine;

		//Depth and nothing else, and never blending.
		if ( depthOnly )
		{
			return CoreRenderDevice.CreatePipeline( vertex, pixel, layout, depthTest: true,
				cullBack: !kind.TwoSided, depthOnly: true, target: kind.Target, pictureFormat: kind.Target == CoreRender.TargetKind.Picture ? kind.PictureFormat : -1 );
		}

		//A full-screen pass over a finished picture: no vertices, no depth, no culling
		if ( kind.Post )
		{
			return CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
				depthTest: false, CoreRender.Topology.Triangles, blend: kind.Translucent || kind.PostBlend == 4,
				depthWrite: false, cullBack: false, target: kind.Target, pictureFormat: kind.Target == CoreRender.TargetKind.Picture ? kind.PictureFormat : -1,
				additive: kind.PostBlend == 2, screen: kind.PostBlend == 3, addByAlpha: kind.PostBlend == 5,
				multiply: kind.PostBlend == 6 );
		}

		//A wireframe drawn through the world tests no depth and writes none: what it is for is seeing
		//the edges of things that are behind other things, and depth is exactly what would stop that.
		if ( kind.Wireframe )
		{
			//Writes depth too, so edges arbitrate among themselves where nothing drew first - see Wireframe.NearWins
			return CoreRenderDevice.CreatePipeline( vertex, pixel, layout, depthTest: !kind.Through,
				blend: kind.Translucent, depthWrite: !kind.Through, cullBack: false,
				target: kind.Target, pictureFormat: kind.Target == CoreRender.TargetKind.Picture ? kind.PictureFormat : -1, lines: true );
		}

		return CoreRenderDevice.CreatePipeline( vertex, pixel, layout, depthTest: !kind.NoDepth,
			kind.Lines ? CoreRender.Topology.Lines : CoreRender.Topology.Triangles,
			blend: kind.Translucent, depthWrite: !kind.Translucent && !kind.NoDepth, cullBack: !kind.TwoSided && !kind.Lines,
			target: kind.Target, pictureFormat: kind.Target == CoreRender.TargetKind.Picture ? kind.PictureFormat : -1,
			specialize: kind.Specialize );
	}


	//Re-sorts blend shape deltas from vertex-major to shape-major with a stable counting sort
	private static void ShapeMajor( SceneMesh mesh, byte[] deltas, int deltaCount, byte[] vertexRuns, int vertexCount, int morphCount )
	{
		const int stride = CoreModel.MorphDeltaStride;
		const int words = stride / 4;

		var from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( deltas.AsSpan( 0, deltaCount * stride ) );
		var runsOf = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( vertexRuns.AsSpan( 0, vertexCount * CoreModel.MorphRunStride ) );

		//Each shape's count, then where each starts.
		var runs = new int[morphCount * 2];

		for ( var d = 0; d < deltaCount; d++ )
		{
			var shape = from[d * words];
			if ( shape < (uint)morphCount ) runs[( (int)shape * 2 ) + 1]++;
		}

		for ( int shape = 0, at = 0; shape < morphCount; shape++ )
		{
			runs[shape * 2] = at;
			at += runs[( shape * 2 ) + 1];
		}

		var laid = new uint[deltaCount * words];
		var next = new int[morphCount];
		for ( var shape = 0; shape < morphCount; shape++ ) next[shape] = runs[shape * 2];

		//By vertex, as in the file, so a shape's vertices come out in order, as the sums are written
		for ( var v = 0; v < vertexCount; v++ )
		{
			var first = runsOf[v * 2];
			var count = runsOf[( v * 2 ) + 1];

			for ( var d = first; d < first + count && d < (uint)deltaCount; d++ )
			{
				var shape = from[(int)d * words];
				if ( shape >= (uint)morphCount ) continue;

				var to = next[shape]++ * words;
				laid[to] = (uint)v;
				from.Slice( ( (int)d * words ) + 1, words - 1 ).CopyTo( laid.AsSpan( to + 1, words - 1 ) );
			}
		}

		mesh.ShapeDeltas = CoreRenderDevice.CreateBuffer( System.Runtime.InteropServices.MemoryMarshal.AsBytes( laid.AsSpan() ) );
		mesh.ShapeRuns = mesh.ShapeDeltas != 0 ? runs : null;
	}

	//Appends index ranges for LOD chain levels above 0, for whole-model draws; returns the indices
	private static unsafe uint[] ChainLevels( SceneMesh mesh, CoreAssets.ModelFile model, uint[] indices )
	{
		var count = model.ClusterCount;
		if ( count == 0 || CoreAssets.ModelFile.ClusterStride != CoreClusters.RecordBytes ) return indices;

		var records = new CoreClusters.Record[count];
		var references = new uint[model.ClusterVertexCount];
		var triangles = new byte[model.ClusterTriangleCount * 3];

		fixed ( CoreClusters.Record* r = records ) model.CopyClusters( r, count );
		fixed ( uint* cv = references ) model.CopyClusterVertices( cv, references.Length );
		fixed ( byte* ct = triangles ) model.CopyClusterTriangles( ct, triangles.Length / 3 );

		var levels = 0;
		foreach ( ref readonly var one in records.AsSpan() ) levels = Math.Max( levels, (int)( one.PartLevel >> 16 ) + 1 );
		if ( levels < 2 ) return indices;

		mesh.Chain = true;
		mesh.ShadowLevelOffset = model.ShadowLevelOffset;
		mesh.LevelErrors = new float[levels];
		mesh.Levels = new List<MeshDraw>[levels];

		var parts = mesh.Meshes.Count;
		var lists = new List<uint>[levels * parts];
		var added = new List<uint>( indices );

		foreach ( ref readonly var one in records.AsSpan() )
		{
			var level = (int)( one.PartLevel >> 16 );
			var part = (int)( one.PartLevel & 0xFFFFu );

			mesh.LevelErrors[level] = MathF.Max( mesh.LevelErrors[level], one.Error );

			if ( part >= parts || mesh.Meshes[part].StandsIn ) continue;

			mesh.MeshLevels = Math.Max( mesh.MeshLevels, level + 1 );

			if ( level == 0 ) continue;

			var list = lists[( level * parts ) + part] ??= new List<uint>();
			var first = (uint)mesh.Meshes[part].FirstVertex;
			var tris = (int)( one.Counts >> 16 );

			for ( var t = 0; t < tris * 3; t++ )
			{
				var at = (int)one.TriangleOffset * 3 + t;
				if ( at >= triangles.Length ) break;

				var reference = (int)one.VertexOffset + triangles[at];
				if ( reference >= references.Length ) break;

				list.Add( references[reference] - first );
			}
		}

		for ( var level = 1; level < levels; level++ )
		{
			var draws = new List<MeshDraw>( parts );

			for ( var part = 0; part < parts; part++ )
			{
				var list = lists[( level * parts ) + part];
				var draw = mesh.Meshes[part];

				//The impostor's quads keep their own range; a part gone from this level draws nothing.
				if ( !draw.StandsIn )
				{
					draw = draw with { FirstIndex = added.Count, IndexCount = list?.Count ?? 0 };
					if ( list is not null ) added.AddRange( list );
				}

				draws.Add( draw );
			}

			mesh.Levels[level] = draws;
		}

		return added.ToArray();
	}

	private static unsafe bool Place( SceneMesh mesh, CoreAssets.ModelFile model, PackedVertex[] vertices, uint[] indices, byte[] skin )
	{
		var vertexBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( vertices.AsSpan() );
		var indexBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes( indices.AsSpan() );

		mesh.BoundCentre = mesh.Bounds.Center;
		mesh.BoundRadius = mesh.Bounds.Size.Length * 0.5f;

		//The pool, where everything the clusters read lives; morphed meshes go in too, binding past the offset
		{
			var positions = new float[vertices.Length * 3];

			fixed ( float* p = positions )
			{
				if ( model.CopyPositions( p, vertices.Length ) != vertices.Length )
				{
					for ( var v = 0; v < vertices.Length; v++ )
					{
						positions[( v * 3 ) + 0] = vertices[v].Position.x;
						positions[( v * 3 ) + 1] = vertices[v].Position.y;
						positions[( v * 3 ) + 2] = vertices[v].Position.z;
					}
				}
			}

			//And each vertex's frame on its own - see CoreGeometryPool.FrameSide.
			var frames = new uint[vertices.Length];
			for ( var v = 0; v < vertices.Length; v++ ) frames[v] = vertices[v].Frame;

			if ( CoreGeometryPool.Place( mesh, vertexBytes, indexBytes, skin,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( positions.AsSpan() ),
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( frames.AsSpan() ) ) )
			{
				var vertexAt = (int)mesh.PoolVertexAt;
				var indexAt = (int)mesh.PoolIndexAt;

				for ( var l = 1; mesh.Levels is not null && l < mesh.Levels.Length; l++ )
				{
					var level = mesh.Levels[l];

					for ( var i = 0; level is not null && i < level.Count; i++ )
						level[i] = level[i] with { FirstIndex = level[i].FirstIndex + indexAt, FirstVertex = level[i].FirstVertex + vertexAt };
				}

				for ( var i = 0; i < mesh.Meshes.Count; i++ )
				{
					var part = mesh.Meshes[i];

					mesh.Meshes[i] = part with
					{
						FirstIndex = part.FirstIndex + indexAt,
						FirstVertex = part.FirstVertex + vertexAt,
					};
				}

				var clusterCount = model.ClusterCount;
				var records = System.Array.Empty<CoreClusters.Record>();
				var clusterVertices = System.Array.Empty<uint>();
				var clusterTriangles = System.Array.Empty<byte>();

				if ( clusterCount > 0 && CoreAssets.ModelFile.ClusterStride == CoreClusters.RecordBytes )
				{
					records = new CoreClusters.Record[clusterCount];
					clusterVertices = new uint[model.ClusterVertexCount];
					clusterTriangles = new byte[model.ClusterTriangleCount * 3];

					fixed ( CoreClusters.Record* r = records ) model.CopyClusters( r, clusterCount );
					fixed ( uint* cv = clusterVertices ) model.CopyClusterVertices( cv, clusterVertices.Length );
					fixed ( byte* ct = clusterTriangles ) model.CopyClusterTriangles( ct, clusterTriangles.Length / 3 );
				}

				if ( !CoreClusters.Keep( mesh, records, clusterVertices, clusterTriangles, model.ClusterLevels ) )
					Log.Warning( $"no room for a mesh's clusters: {CoreRenderDevice.Description}" );

				return true;
			}
		}

		//No pool on a backend without storage buffers (the browser): its own buffers, no clusters
		mesh.Vertices = CoreRenderDevice.CreateBuffer( vertexBytes );
		mesh.Indices = CoreRenderDevice.CreateBuffer( indexBytes );
		if ( skin is not null ) mesh.Skin = CoreRenderDevice.CreateBuffer( skin );

		if ( mesh.Vertices == 0 || mesh.Indices == 0 )
		{
			ReleaseMesh( mesh );
			return false;
		}

		return true;
	}

	//Lets go of everything a mesh holds on the device.
	internal static void ReleaseMesh( SceneMesh mesh )
	{
		if ( mesh is null ) return;

		CoreClusters.Give( mesh );

		if ( mesh.Pooled ) CoreGeometryPool.Give( mesh );
		else
		{
			CoreRenderDevice.DestroyBuffer( mesh.Vertices );
			CoreRenderDevice.DestroyBuffer( mesh.Indices );
			CoreRenderDevice.DestroyBuffer( mesh.Skin );
		}

		CoreRenderDevice.DestroyBuffer( mesh.MorphDeltas );
		CoreRenderDevice.DestroyBuffer( mesh.MorphRuns );
		CoreRenderDevice.DestroyBuffer( mesh.ShapeDeltas );
		mesh.ShapeDeltas = 0;
		mesh.ShapeRuns = null;
		mesh.Vertices = 0;
		mesh.Indices = 0;
		mesh.Skin = 0;
		mesh.MorphDeltas = 0;
		mesh.MorphRuns = 0;
	}


	//Forgets a cached failed build of a real model so the next frame retries (its file may be mid-write)
	internal static void ForgetFailedMesh( Model model, Dictionary<string, SceneMesh> meshes )
	{
		if ( model is null || meshes is null ) return;

		var path = model.ResourcePath;

		if ( model.pulled is { Length: > 0 } )
		{
			meshes.Remove( $"#pulled:{model.GetHashCode():x8}:{path}" );
			return;
		}

		if ( model.core is { IsValid: true } && model.core.Loaded is { } ready )
		{
			meshes.Remove( $"#handle:{ready.Id}:{path}" );
			return;
		}

		if ( !string.IsNullOrWhiteSpace( path ) ) meshes.Remove( path );
	}

	internal static SceneMesh LoadMesh( Model model,
		Dictionary<string, SceneMesh> meshes, Dictionary<string, ulong> textures, ulong fallback,
		Dictionary<string, ulong> materials = null )
	{
		if ( model is null || !model.IsValid ) return null;

		//A mesh the format could not hold is drawn from what the builder kept; the model holds a placeholder
		if ( model.pulled is { Length: > 0 } )
			return PulledMesh( model, meshes );

		if ( model.core is { IsValid: true } )
			return LoadMesh( model.ResourcePath, meshes, textures, fallback, materials, model.core.Loaded );

		return LoadMesh( model.ResourcePath, meshes, textures, fallback, materials );
	}

	//The device buffers for a model whose vertices are its own, made once and kept for as long as the model is.
	private static SceneMesh PulledMesh( Model model, Dictionary<string, SceneMesh> meshes )
	{
		//Keyed by the model rather than by a path, because there is no path.
		var key = $"#pulled:{model.GetHashCode():x8}:{model.ResourcePath}";

		if ( meshes.TryGetValue( key, out var cached ) ) return cached;

		meshes[key] = null;

		var first = model.pulled[0];

		if ( first.Vertices is null || first.VertexCount <= 0 ) return null;

		var built = new SceneMesh
		{
			Pulled = true,
			Vertices = CoreRenderDevice.CreateBuffer(
				first.Vertices.AsSpan( 0, first.VertexCount * first.VertexStride ) ),
			Bounds = first.Bounds,
		};

		if ( built.Vertices == 0 ) return null;

		built.Meshes.Add( new MeshDraw( 0, 0, 0, 0, Color.White )
		{
			PulledVertices = first.VertexCount,
			Shader = first.Material?.ShaderName,
		} );

		meshes[key] = built;

		return built;
	}

	internal static SceneMesh LoadMesh( string resourcePath,
		Dictionary<string, SceneMesh> meshes, Dictionary<string, ulong> textures, ulong fallback,
		Dictionary<string, ulong> materials = null, CoreAssets.ModelFile ready = null )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) && ready is null ) return null;
		var key = ready is not null
			? $"#handle:{ready.Id}:{resourcePath}"
			: ( string.IsNullOrWhiteSpace( resourcePath ) ? "#missing" : resourcePath );
		if ( meshes.TryGetValue( key, out var cached ) ) return cached;

		//Cache the failure too, or a scene with a hundred of something missing asks a hundred times.
		meshes[key] = null;

		//The handful the engine builds rather than reads - a box, a plane, a sphere, the error marker. A
		//scene names them the way it always did, and there is no file behind any of them. See CoreDevModels.
		var builtIn = ready is null && CoreDevModels.IsBuiltIn( resourcePath ) ? CoreDevModels.Get( resourcePath ) : null;

		var model = ready ?? builtIn?.Loaded;
		var owned = false;

		//The compiled model on disk, which its levels sit beside
		var full = builtIn is null && !string.IsNullOrWhiteSpace( resourcePath )
			? CoreModel.ResolveOnDisk( CompiledPaths.Model( resourcePath ).Replace( '\\', '/' ) )
			: null;

		if ( model is null )
		{

			//Through the same door as everything else that reads one: what is on disk may be our model,
			//our model inside a resource container, or a definition whose geometry has not been built yet.
			model = full is not null ? CoreModel.Read( full ) : null;

			owned = true;
		}

		if ( model is null ) return null;

		try
		{
			var vertexCount = model.VertexCount;
			var indexCount = model.IndexCount;

			if ( vertexCount <= 0 || indexCount <= 0 ) return null;

			//Packed, as the file holds them and the card reads them - a copy, not an unpacking. See PackedVertex.
			var vertices = new PackedVertex[vertexCount];
			var indices = new uint[indexCount];

			fixed ( PackedVertex* v = vertices ) model.CopyPackedVertices( v, vertexCount );
			fixed ( uint* i = indices ) model.CopyIndices( i, indexCount );

			var mesh = new SceneMesh();
			byte[] skin = null;


			var bounds = stackalloc float[6];
			model.GetRenderBounds( bounds );
			mesh.Bounds = new BBox(
				new Vector3( bounds[0], bounds[1], bounds[2] ),
				new Vector3( bounds[3], bounds[4], bounds[5] ) );

			//The skin: both halves or neither, as either alone poses nothing
			var boneCount = model.BoneCount;
			var skinCount = model.SkinCount;

			if ( boneCount > 0 && skinCount == vertexCount )
			{
				skin = new byte[skinCount * 8];

				fixed ( byte* s = skin ) model.CopySkin( s, skinCount );

				var bones = new BoneRest[boneCount];

				for ( var i = 0; i < boneCount; i++ )
				{
					var matrix = stackalloc float[16];
					model.BoneInverseBind( i, matrix );

					bones[i] = new BoneRest(
						model.BoneName( i ),
						model.BoneParent( i ),
						RowMajor( matrix ) );
				}

				mesh.Bones = bones;
			}

			//The blend shapes, if the model has any. Both buffers or neither: deltas nothing points
			//at are never read, and runs pointing into nothing are worse than no runs at all.
			var morphCount = model.MorphCount;
			var deltaCount = model.MorphDeltaCount;

			if ( morphCount > 0 && deltaCount > 0 )
			{
				var deltas = new byte[deltaCount * CoreModel.MorphDeltaStride];
				var runs = new byte[vertexCount * CoreModel.MorphRunStride];

				fixed ( byte* d = deltas ) model.CopyMorphDeltas( d, deltaCount );
				fixed ( byte* r = runs ) model.CopyMorphRuns( r, vertexCount );

				mesh.MorphDeltas = CoreRenderDevice.CreateBuffer( deltas );
				mesh.MorphRuns = CoreRenderDevice.CreateBuffer( runs );
				mesh.MorphCount = morphCount;
				mesh.MorphVertexCount = vertexCount;
				mesh.MorphDeltaCount = deltaCount;

				if ( !CoreRenderDevice.PortableFrame ) ShapeMajor( mesh, deltas, deltaCount, runs, vertexCount, morphCount );

				//One without the other draws nothing sensible, so a failed buffer takes the other with it
				if ( mesh.MorphDeltas == 0 || mesh.MorphRuns == 0 )
				{
					CoreRenderDevice.DestroyBuffer( mesh.MorphDeltas );
					CoreRenderDevice.DestroyBuffer( mesh.MorphRuns );

					mesh.MorphDeltas = 0;
					mesh.MorphRuns = 0;
					mesh.MorphCount = 0;
					mesh.MorphVertexCount = mesh.MorphDeltaCount = 0;

					CoreRenderDevice.DestroyBuffer( mesh.ShapeDeltas );
					mesh.ShapeDeltas = 0;
					mesh.ShapeRuns = null;
				}
			}

			var meshCount = model.MeshCount;
			var materialGroups = model.MaterialGroupCount;

			mesh.DefaultGroupMask = model.DefaultMask;
			mesh.DualQuaternion = model.DualQuaternion;
			mesh.MaterialGroupNames = new string[materialGroups];

			//Which bits belong to which body group, for the fallback above.
			var partCount = model.PartCount;

			if ( partCount > 0 )
			{
				mesh.PartMasks = new ulong[partCount];

				for ( var p = 0; p < partCount; p++ )
					mesh.PartMasks[p] = model.PartMask( p );
			}

			for ( var g = 0; g < materialGroups; g++ )
			{
				mesh.MaterialGroupNames[g] = model.MaterialGroupName( g );
			}

			//The impostor sheet onto a shared page before the vertices go up (CoreImpostor); a failed one leaves
			//the model without its impostor
			var impostorMesh = -1;
			var impostorMaterial = (string)null;

			if ( CoreImpostor.Place( model, out var placed ) )
			{
				impostorMesh = (int)model.Impostor.mesh;
				impostorMaterial = placed.Material;
				mesh.ImpostorError = model.Impostor.radius * 2.0f / model.Impostor.texels;

				var span = stackalloc int[4];
				model.MeshRange( impostorMesh, span );

				for ( var v = span[2]; v < span[2] + span[3] && v < vertices.Length; v++ )
				{
					ref var corner = ref vertices[v];
					corner.TexCoord0.x = placed.U + ( corner.TexCoord0.x * placed.Wide );
					corner.TexCoord0.y = placed.V + ( corner.TexCoord0.y * placed.Tall );
				}
			}

			for ( var i = 0; i < meshCount; i++ )
			{
				var range = stackalloc int[4];
				range[0] = range[1] = range[2] = range[3] = 0;
				model.MeshRange( i, range );

				var material = i == impostorMesh ? impostorMaterial : model.MeshMaterial( i );

				if ( !string.IsNullOrWhiteSpace( material ) )
					mesh.Materials.Add( material.Replace( '\\', '/' ).TrimStart( '/' ) );

				//The art's surface numbers, where the model's build says the mesh wears them - see ModelFormat.Surface
				var surface = model.MeshSurface( i );
				if ( !surface.Worn ) surface = default;

				//Code-made materials (e.g. from Model.Builder) are drawn from memory; they have no file to read
				var made = materials is not null ? Rinten.Material.Edited( material ) : null;
				var live = made is not null ? CoreResources.Live( made ) : 0;

				var bound = live != 0 ? live
					: materials is not null
						? ResolveMaterial( material, in surface, materials, textures, fallback )
						: ResolveMaterialTexture( material, textures, fallback );

				//Its colour is another tint multiplier, with the material's, its slot's and the instance's, so forty
				//colours are forty tints over one material
				var tint = ( live != 0 ? CoreResources.LiveTint( made ) : ReadMaterialTint( material ) ) * surface.Colour;
				var how = Wearing( ReadMaterial( material ), in surface );

				if ( live != 0 )
				{
					var flags = CoreResources.LiveFlags( made );
					var shader = made.ShaderName;

					how = how with
					{
						Missing = false,
						Shader = string.IsNullOrWhiteSpace( shader ) || shader == "invalid" ? null : shader,
						Combo = CoreResources.LiveCombo( made ),
						Masked = flags.Masked,
						Translucent = flags.Translucent,
						TwoSided = flags.TwoSided,
						AlphaCutoff = CoreResources.LiveCutoff( made ),
					};
				}

				string[] groupMaterials = null;

				if ( materialGroups > 0 )
				{
					groupMaterials = new string[materialGroups];

					for ( var g = 0; g < materialGroups; g++ )
					{
						groupMaterials[g] = model.GroupMaterial( g, i );

						//A group's materials are resolved later, when somebody picks that group - but the
						//mesh has named them, and a name is what a file changing has to be matched against.
						if ( !string.IsNullOrWhiteSpace( groupMaterials[g] ) )
							mesh.Materials.Add( groupMaterials[g].Replace( '\\', '/' ).TrimStart( '/' ) );
					}
				}

				mesh.Meshes.Add( new MeshDraw( range[0], range[1], range[2], bound, tint )
				{
					Translucent = how.Translucent,
					TwoSided = how.TwoSided,
					Group = model.MeshGroup( i ),
					GroupMaterials = groupMaterials,
					Shader = string.IsNullOrWhiteSpace( how.Shader ) ? null : how.Shader,
					Combo = how.Combo,
					Cull = how.Cull,
					Material = material,
					Masked = how.Masked,
					Cutoff = how.AlphaCutoff,
					StandsIn = model.StandsIn( i ),
					MetresPerUv = MetresPerUv( vertices, indices, range[0], range[1], range[2] ),
				} );
			}

			//A model whose meshes carry no ranges still draws, as one piece.
			if ( mesh.Meshes.Count == 0 )
				mesh.Meshes.Add( new MeshDraw( 0, indexCount, 0, fallback, Color.White ) );

			//A chain's levels as ranges of the index buffer, after the model's own.
			if ( model.IsChain ) indices = ChainLevels( mesh, model, indices );

			//The indices went up before the parts were known; reordered by the cut, they go up again. Vertices stay
			//as they are, the clusters naming them by number - see Place
			if ( !Place( mesh, model, vertices, indices, skin ) ) return null;

			meshes[key] = mesh;
			return mesh;
		}
		catch ( Exception e )
		{
			Console.WriteLine( $"LoadMesh {resourcePath}: {e.GetType().Name}: {e.Message}" );
			return null;
		}
		finally
		{
			//A built-in is CoreDevModels' to keep; only a model read for this mesh is freed here
			if ( owned ) model.Dispose();
		}
	}

	//Resolves a .mat to a device material with all its maps compiled on demand; checker on failure
	internal static ulong ResolveMaterial( string materialPath,
		Dictionary<string, ulong> materials, Dictionary<string, ulong> textures, ulong fallback )
		=> ResolveMaterial( materialPath, default, materials, textures, fallback );

	//Same, with the mesh art's values overriding the material's; keyed by path plus the differences
	internal static ulong ResolveMaterial( string materialPath, in ModelFormat.Surface surface,
		Dictionary<string, ulong> materials, Dictionary<string, ulong> textures, ulong fallback )
	{
		//No material named: use the plain material, as the model compiler does, not the checkerboard
		if ( string.IsNullOrWhiteSpace( materialPath ) )
			materialPath = ModelDefinition.PlainMaterial;

		var file = ReadMaterial( materialPath );
		var definition = Wearing( file, in surface );

		var held = materialPath + WornKey( file, definition );

		if ( materials.TryGetValue( held, out var cached ) ) return cached;

		materials[held] = fallback;

		var colour = ResolveMaterialTexture( materialPath, textures, fallback );

		//Flat colour goes into the tint (see Folded), so bind white or the colour gets applied twice
		if ( definition.FlatColour && colour != fallback ) colour = CoreResources.DefaultColour;

		//A shader we can't build shows the checkerboard so it looks unported; naming the standard one is fine
		if ( NamesItsOwnShader( definition.Shader )
			&& CoreResources.Shader( definition.Shader, definition.Combo ) is null )
		{
			//Not marked missing: the combo below is the standard surface's, and this material's own surface exists
			//but will not build
			return CoreResources.MissingMaterial;
		}

		//Nothing in the colour slot; three different faults are told apart here
		if ( colour == fallback )
		{
			//There is no such material. That is a model naming a file the project does not have, and it should be seen.
			if ( !MaterialExists( materialPath ) ) return Missing( materialPath );

			//No colour map (or a flat colour) is a plain white material, not a missing one
			if ( !CoreDevTextures.IsPicture( ReadColorMap( materialPath ) ) )
			{
				colour = CoreResources.DefaultColour;
			}
			else if ( string.IsNullOrWhiteSpace( definition.Shader ) )
			{
				//Names an unreadable picture with no shader that could do without it: broken, and it says so
				return Missing( materialPath );
			}

			//Named a picture we could not read, but has a shader: the checkerboard stands in the slot
			//so the shader has something to sample, and everything else about the material is honoured.
		}

		//Missing maps get neutral values (e.g. DefaultNormal), not the colour; read as PicturesOf says
		var normal = Slot( ResolveMap( definition.Normal, textures, srgb: false ),
			CoreResources.DefaultNormal );

		var emission = Slot( ResolveMap( definition.Emission, textures, srgb: true ),
			CoreResources.DefaultEmission );

		Span<float> numbers = stackalloc float[CoreRenderDevice.MaterialParamFloats];

		numbers.Clear();

		numbers[0] = definition.Roughness;
		numbers[1] = definition.Metalness;
		numbers[2] = definition.EmissionStrength;
		numbers[3] = definition.Masked ? definition.AlphaCutoff : 0.0f;

		//And the shader's declared numbers, a slot each in declared order - see CoreShaderParams
		var written = definition.Numbers;
		var pictures = definition.Pictures;


		var third = 0ul;

		CoreShaderParams.Into( numbers, CoreResources.Declared( definition.Shader ),
			name => written is not null && written.TryGetValue( name, out var value ) ? value : null,
			name => CoreResources.PictureNumber( Picture( name, pictures, textures ), ref third ) );

		//Nothing in the third slot but in the portable frame, where it holds the one picture
		//the shader declared that this frame can bind - see CoreResources.PictureNumber.
		var material = CoreRenderDevice.CreateMaterial( colour, normal, third, emission, numbers );

		if ( material == 0 ) return fallback;

		materials[held] = material;
		return material;
	}

	//Applies mesh art values (roughness, metalness, glow) over material fields, adjusting features
	internal static MaterialFields Wearing( MaterialFields how, in ModelFormat.Surface surface )
	{
		if ( how.Missing || !surface.Worn ) return how;

		static float Step( float value ) => MathF.Round( Math.Clamp( value, 0.0f, 1.0f ) * 255.0f ) / 255.0f;

		var worn = how;

		//In 1/255 steps, so two exports of one grey are one material
		if ( surface.Says( ModelFormat.SurfaceSays.Roughness ) ) worn = worn with { Roughness = Step( surface.roughness ) };
		if ( surface.Says( ModelFormat.SurfaceSays.Metalness ) ) worn = worn with { Metalness = Step( surface.metalness ) };

		if ( surface.Says( ModelFormat.SurfaceSays.Emission ) && !CoreDevTextures.IsPicture( how.Emission ) )
		{
			var most = MathF.Max( surface.emissionR, MathF.Max( surface.emissionG, surface.emissionB ) );

			if ( most > 0.0f )
			{
				//Colour at full brightness in the picture, brightness in the number, as pictures stop at one
				var glow = new Color( surface.emissionR / most, surface.emissionG / most, surface.emissionB / most, 1.0f );

				worn = worn with
				{
					Emission = CoreDevTextures.PathFor( glow.ToSrgb(), srgb: true ),
					EmissionStrength = MathF.Round( most * surface.emissionStrength * 64.0f ) / 64.0f,
				};
			}
		}

		if ( worn.Roughness == how.Roughness && worn.Metalness == how.Metalness
			&& worn.Emission == how.Emission && worn.EmissionStrength == how.EmissionStrength )
		{
			return how;
		}

		var (masked, translucent, twoSided) = DrawFlags(
			FeatureValues( worn.Shader, how.Told, ( what, field ) => Implies( worn, what, field ) ) );

		return worn with
		{
			Combo = ComboFor( worn.Shader, how.Told, ( what, field ) => Implies( worn, what, field ) ),
			Masked = masked,
			Translucent = translucent,
			TwoSided = twoSided,
		};
	}

	//The builds a compiled model's meshes ask for beyond what their materials ask for by themselves - a metal, a glow,
	//said by the art over a material that is neither. For a bake ahead of the draw: see StandaloneExporter.BakeShaders.
	internal static IEnumerable<(string Shader, string Combo)> WornBuilds( string compiledModel )
	{
		using var model = CoreAssets.ModelFile.Load( compiledModel );

		if ( model is null ) yield break;

		for ( var i = 0; i < model.MeshCount; i++ )
		{
			var surface = model.MeshSurface( i );
			if ( !surface.Worn ) continue;

			var material = model.MeshMaterial( i );
			if ( string.IsNullOrWhiteSpace( material ) ) material = ModelDefinition.PlainMaterial;

			var file = ReadMaterial( material );
			var worn = Wearing( file, in surface );

			if ( worn.Combo == file.Combo ) continue;

			yield return (string.IsNullOrWhiteSpace( worn.Shader ) ? StandardShader : worn.Shader, worn.Combo);
		}
	}

	//Key suffix for art overrides; no dot or slash, so Forget can still match the material's file
	private static string WornKey( MaterialFields file, MaterialFields worn )
	{
		if ( worn == file ) return "";

		static int Steps( float value, float per ) => (int)MathF.Round( value * per );

		var glow = worn.Emission == file.Emission ? "" : System.IO.Path.GetFileNameWithoutExtension( worn.Emission );

		return $"#r{Steps( worn.Roughness, 255.0f ):x2}m{Steps( worn.Metalness, 255.0f ):x2}e{Steps( worn.EmissionStrength, 64.0f ):x}{glow}";
	}

	//Whether a material named one of its shader's pictures, by declared name or without its prefix
	private static bool Names( IReadOnlyDictionary<string, string> pictures, string field )
	{
		if ( pictures is null || string.IsNullOrWhiteSpace( field ) ) return false;

		return ( pictures.TryGetValue( field, out var path ) ||
			pictures.TryGetValue( CoreShaderParams.Bare( field ), out path ) ) &&
			CoreDevTextures.IsChosen( path );
	}

	//The pictures a material set on its shader, by name. Written as an object of paths under Textures - see
	//MaterialDefinition.Textures. Null where it set none, which is every material drawn by a shader that declared none.
	private static Dictionary<string, string> Named( JsonElement root )
	{
		if ( !root.TryGetProperty( nameof( MaterialDefinition.Textures ), out var listed ) ||
			listed.ValueKind != JsonValueKind.Object )
		{
			return null;
		}

		Dictionary<string, string> named = null;

		foreach ( var field in listed.EnumerateObject() )
		{
			if ( field.Value.ValueKind != JsonValueKind.String ) continue;

			var path = field.Value.GetString();

			if ( string.IsNullOrWhiteSpace( path ) ) continue;

			named ??= new Dictionary<string, string>( StringComparer.Ordinal );
			named[field.Name] = path;
		}

		return named;
	}

	//A shader-declared picture by table index (0 = use the default); uploaded linear, not sRGB
	private static ulong Picture( string name, IReadOnlyDictionary<string, string> pictures,
		Dictionary<string, ulong> textures )
	{
		if ( pictures is null ) return 0;

		//Either the name the shader gave it or that name without its prefix, so a material
		//may write Height for g_tHeight the way it writes Roughness for g_flRoughness.
		if ( !pictures.TryGetValue( name, out var path ) &&
			!pictures.TryGetValue( CoreShaderParams.Bare( name ), out path ) )
		{
			return 0;
		}

		return ResolveMap( path, textures, srgb: false );
	}

	//A map, or the stand-in for that slot where there is no map.
	private static ulong Slot( ulong found, ulong instead ) => found != 0 ? found : instead;

	//One of a material's maps, compiled and uploaded, or zero when it names nothing. Data maps
	//- normals, packed roughness - are not colours and are compiled without the gamma curve.
	internal static ulong ResolveMap( string imagePath, Dictionary<string, ulong> textures, bool srgb )
	{
		if ( string.IsNullOrWhiteSpace( imagePath ) ) return 0;

		//One of the engine's own flat pictures, which is what a slot nobody filled in names. Built rather than
		//found - there is no such file, and looking for one would land in the miss below. See CoreDevTextures.
		if ( CoreDevTextures.Get( imagePath ) is var built && built != 0 ) return built;

		var key = $"{imagePath}|{srgb}";
		if ( textures.TryGetValue( key, out var cached ) ) return cached;

		textures[key] = 0;

		var full = CoreModel.ResolveOnDisk( imagePath.Replace( '\\', '/' ) );

		//Not here as a picture: a build that carries what it compiles to instead.
		if ( full is null )
		{
			var shipped = CorePictures.Shipped( imagePath, srgb );
			if ( shipped != 0 ) textures[key] = shipped;
			return shipped;
		}

		//Their block-compressed textures upload as-is; anything else goes through our compiler
		if ( CoreImportedTexture.IsTheirs( full ) )
		{
			var kept = CoreImportedTexture.BlockTexture( full, srgb );

			if ( kept != 0 )
			{
				textures[key] = kept;
				return kept;
			}

			full = CoreImportedTexture.AsImageFile( full );
			if ( full is null ) return 0;
		}

		var texture = Compiled( full, CompiledPaths.TextureBlocks( full, srgb ), srgb );
		if ( texture == 0 ) return 0;

		textures[key] = texture;
		return texture;
	}

	//The compiled size of a picture a material names by path - see ResolveMap; false if it does not compile
	internal static bool MapSize( string imagePath, bool srgb, out int width, out int height )
	{
		width = height = 0;

		return MapBlocks( imagePath, srgb ) is { } blocks && CoreRenderDevice.TextureSize( blocks, out width, out height );
	}

	//The compiled file of a picture a material names by path - see ResolveMap; null where it is not a picture
	internal static string MapBlocks( string imagePath, bool srgb )
	{
		if ( !CoreDevTextures.IsPicture( imagePath ) ) return null;

		var full = CoreModel.ResolveOnDisk( imagePath.Replace( '\\', '/' ) );

		if ( full is null ) return CorePictures.ShippedBlocks( imagePath, srgb );

		var blocks = CompiledPaths.TextureBlocks( full, srgb );

		//Compiled names the file it made by whether the device takes blocks.
		if ( Rinten.Rendering.TextureSettings.Compress && CoreNative.Available && CoreRender.SupportsBc() != 0 )
			blocks = CompiledPaths.Compressed( blocks );

		return blocks;
	}

	//A colour cut by a mask on the device, with its size; nought where either fails. For live materials,
	//which hold textures rather than paths - see Material.Fill
	internal static ulong CutColour( string colour, string cut, out int width, out int height )
	{
		width = height = 0;
		if ( !CoreDevTextures.IsPicture( colour ) || !CoreDevTextures.IsPicture( cut ) ) return 0;

		var colourFull = CoreModel.ResolveOnDisk( colour.Replace( '\\', '/' ) );
		var cutFull = CoreModel.ResolveOnDisk( cut.Replace( '\\', '/' ) );

		string blocks;
		ulong handle;

		if ( colourFull is null || cutFull is null )
		{
			//A build: the blocks it carries, cut where they were made.
			blocks = CorePictures.ShippedBlocks( colour, srgb: true, cut );
			handle = blocks is null ? 0 : CoreRenderDevice.CreateTexture( blocks );
		}
		else
		{
			blocks = CompiledPaths.TextureBlocks( colourFull, srgb: true, cut );
			handle = Compiled( colourFull, blocks, srgb: true, cutFull );

			//Compiled names the file it made by whether the device takes blocks.
			if ( Rinten.Rendering.TextureSettings.Compress && CoreNative.Available && CoreRender.SupportsBc() != 0 )
				blocks = CompiledPaths.Compressed( blocks );
		}

		if ( handle != 0 ) CoreRenderDevice.TextureSize( blocks, out width, out height );

		//A live material's picture may show anywhere, so whole - see CoreTextureStreaming.KeepWhole
		CoreTextureStreaming.KeepWhole( handle );

		return handle;
	}

	//What a .mat says about itself, as far as the renderer cares.
	internal record struct MaterialFields( string Normal, string Emission,
		float Roughness, float Metalness, float EmissionStrength, float AlphaCutoff, bool Masked,
		bool Translucent, bool TwoSided )
	{
		/// <summary>Which shader draws it. Empty is the engine's standard one.</summary>
		public string Shader { get; init; }

		/// <summary>Which build of that shader - see CoreShaderFeatures.</summary>
		public string Combo { get; init; }

		/// <summary>Features set by hand, kept to rework the build where a mesh's art changes numbers.</summary>
		public IReadOnlyDictionary<string, int> Told { get; init; }


		/// <summary>No material at all, so the glowing checkerboard is drawn and the build needs F_EMISSION</summary>
		public bool Missing { get; init; }

		//Null where it set none; for a shader declaring none, every number is one of the four below
		/// <summary>The numbers its shader declared, by declared name.</summary>
		public Dictionary<string, Vector4> Numbers { get; init; }

		/// <summary>Pictures it declared beyond the standard four, by name - see MaterialDefinition.Textures.</summary>
		public Dictionary<string, string> Pictures { get; init; }

		/// <summary>And what those of them the cull pass reads come to.</summary>
		public Vector3 Cull { get; init; }

		/// <summary>Colour slot path, needed here because F_COLOR_MAP is implied from it</summary>
		public string Colour { get; init; }

		/// <summary>The mask its colour is cut by, folded into the colour's alpha at compile; or null.</summary>
		public string Cut { get; init; }

		/// <summary>Built without the colour sample, so the flat colour must reach the draw via the tint</summary>
		public bool FlatColour { get; init; }
	}

	//Parsed material files (fields, tint) so instances don't re-parse; cleared by CoreResources.Forget
	private static readonly Dictionary<string, (MaterialFields How, Color Tint)> materialFields = new();

	//Forgets what every material file said - see materialFields.
	internal static void ForgetMaterialFields()
	{
		lock ( materialFields ) materialFields.Clear();
	}

	private static (MaterialFields How, Color Tint) MaterialOf( string materialPath )
	{
		var key = materialPath ?? "";

		lock ( materialFields )
		{
			if ( materialFields.TryGetValue( key, out var found ) ) return found;
		}

		//Read outside the lock, as it touches the disk and one slow file would stall every material
		var read = ReadMaterialFile( key, out var onDisk );
		var made = ( read, Folded( read, ReadMaterialTintFile( key ) ) );

		//Don't cache a material not found on disk; the filesystem may still be mounting
		if ( onDisk ) lock ( materialFields ) materialFields[key] = made;

		return made;
	}

	internal static MaterialFields ReadMaterial( string materialPath ) => MaterialOf( materialPath ).How;

	//Pictures a material reads and whether each is sRGB; must match ResolveMaterial and the exporter
	internal static IEnumerable<(string Picture, bool Srgb, string Cut)> PicturesOf( MaterialFields how )
	{
		//The colour cut by its mask where it has one, as drawn - see ResolveMaterialTexture
		if ( CoreDevTextures.IsPicture( how.Colour ) ) yield return (how.Colour, true, how.Cut);
		if ( CoreDevTextures.IsPicture( how.Normal ) ) yield return (how.Normal, false, null);
		if ( CoreDevTextures.IsPicture( how.Emission ) ) yield return (how.Emission, true, null);

		if ( how.Pictures is null ) yield break;

		foreach ( var picture in how.Pictures.Values )
		{
			if ( CoreDevTextures.IsPicture( picture ) ) yield return (picture, false, null);
		}
	}

	//Returns the missing material and records it, so the draw also picks the F_EMISSION build
	private static ulong Missing( string materialPath )
	{
		var key = materialPath ?? "";

		lock ( materialFields )
		{
			if ( materialFields.TryGetValue( key, out var found ) && !found.How.Missing )
			{
				materialFields[key] = ( found.How with
				{
					Missing = true,
					Shader = "",
					Combo = MissingCombo,
				}, found.Tint );
			}
		}

		return CoreResources.MissingMaterial;
	}

	//Standard surface combo with only F_EMISSION on, for the missing material
	private static string MissingCombo => missingCombo ??=
		ComboFor( null, new Dictionary<string, int>( StringComparer.Ordinal ) { ["F_EMISSION"] = 1 },
			( _, _ ) => false ) ?? "";

	private static string missingCombo;

	//Draw tint: material tint times a flat colour slot, so the shader can skip the colour sample
	private static Color Folded( MaterialFields how, Color tint )
	{
		if ( !how.FlatColour ) return tint;

		return CoreDevTextures.AsRead( how.Colour ) is { } flat ? tint * flat : tint;
	}

	//Built without the colour sample when the shader has F_COLOR_MAP and the slot holds no picture
	private static bool Flat( MaterialFields how )
		=> Declares( how.Shader, "F_COLOR_MAP" ) && !CoreDevTextures.IsPicture( how.Colour );

	//Whether a shader declares a feature by that name at all. Asked once per material, off the back of the same
	//file ComboFor reads, and the answer is kept with everything else the material came to - see materialFields.
	private static bool Declares( string shaderPath, string feature )
	{
		var written = System.IO.Path.ChangeExtension(
			string.IsNullOrWhiteSpace( shaderPath ) ? StandardShader : shaderPath, ".shader" )
			.Replace( '\\', '/' );

		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( written ) );

		if ( onDisk is null ) return false;

		foreach ( var declared in CoreShaderFeatures.Of( onDisk ) )
		{
			if ( string.Equals( declared.Name, feature, StringComparison.Ordinal ) ) return true;
		}

		return false;
	}

	private static MaterialFields ReadMaterialFile( string materialPath, out bool found )
	{
		found = true;

		var fields = new MaterialFields( "", "", 0.5f, 0.0f, 0.0f, 0.5f, false, false, false );

		//Imported .vmat materials first, like the other readers, or imported models lose normals and shader
		if ( CoreImportedMaterial.Read( materialPath ) is { } theirs )
		{
			var read = new MaterialFields(
				theirs.Normal, theirs.Emission,
				theirs.Roughness, theirs.Metalness, theirs.EmissionStrength, theirs.AlphaCutoff,
				theirs.Masked, theirs.Translucent, theirs.TwoSided )
			{
				//Their shader names (shaders/skin.shader...) do not compile here; empty is the standard shader, which
				//draws with the maps they brought
				Shader = "",
				Colour = theirs.ColorMap,

				//Their roughness, metalness and occlusion, named as the standard shader reads them
				Pictures = theirs.Pictures,
			};

			//Their switches as the features that say the same thing, so a cutout of theirs is
			//built with the cut in and drawn with the state that goes with it - see DrawFlags.
			var theirsTold = new Dictionary<string, int>( StringComparer.Ordinal );
			if ( theirs.Masked ) theirsTold["F_ALPHA"] = 1;
			if ( theirs.Translucent ) theirsTold["F_TRANSLUCENT"] = 1;
			if ( theirs.TwoSided ) theirsTold["F_BACKFACES"] = 1;

			var (theirsMasked, theirsTranslucent, theirsTwoSided) = DrawFlags(
				FeatureValues( read.Shader, theirsTold, ( what, field ) => Implies( read, what, field ) ) );

			return read with
			{
				Combo = ComboFor( read.Shader, theirsTold, ( what, field ) => Implies( read, what, field ) ),
				Told = theirsTold,
				Masked = theirsMasked,
				Translucent = theirsTranslucent,
				TwoSided = theirsTwoSided,
				Cull = CullOf( read.Shader, null ),
				FlatColour = Flat( read ),
			};
		}

		if ( materialPath.EndsWith( ".essmat", System.StringComparison.OrdinalIgnoreCase ) )
			materialPath = materialPath[..^7] + ".mat";

		var full = CoreModel.ResolveTextOnDisk( materialPath.Replace( '\\', '/' ) );

		if ( full is null )
		{
			found = false;

			//Nothing to read: the draw wears the checkerboard, and asks for the build that lets it glow - see Missing.
			return fields with { Missing = true, Shader = "", Combo = MissingCombo };
		}

		try
		{
			using var document = JsonDocument.Parse( CompiledFile.TextOf( full ) );
			var root = document.RootElement;

			string Text( string name )
				=> root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String
					? value.GetString()
					: "";

			float Number( string name, float fallback )
				=> root.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number
					? value.GetSingle()
					: fallback;

			var built = new MaterialFields(
				Text( nameof( MaterialDefinition.Normal ) ),
				Text( nameof( MaterialDefinition.Emission ) ),
				Number( nameof( MaterialDefinition.Roughness ), 0.5f ),
				Number( nameof( MaterialDefinition.Metalness ), 0.0f ),
				Number( nameof( MaterialDefinition.EmissionStrength ), 0.0f ),
				Number( nameof( MaterialDefinition.AlphaCutoff ), 0.5f ),
				false, false, false )
			{
				Shader = Text( nameof( MaterialDefinition.Shader ) ),
				Colour = Text( nameof( MaterialDefinition.ColorMap ) ),
				Cut = CoreDevTextures.IsPicture( Text( nameof( MaterialDefinition.OpacityMask ) ) )
					? Text( nameof( MaterialDefinition.OpacityMask ) )
					: null,

				//Before the combo: a feature over a shader picture (Map( g_tAO )) is on where the material names it
				Pictures = Named( root ),
			};

			var told = Told( root );
			var (masked, translucent, twoSided) = DrawFlags(
				FeatureValues( built.Shader, told, ( what, field ) => Implies( built, what, field ) ) );

			return built with
			{
				Combo = ComboFor( built.Shader, told,
					( what, field ) => Implies( built, what, field ) ),
				Told = told,

				//How it draws comes from its shader's features (F_ALPHA, F_TRANSLUCENT, F_BACKFACES), not the material
				Masked = masked,
				Translucent = translucent,
				TwoSided = twoSided,
				FlatColour = Flat( built ),
				Numbers = Written( root ),
				Cull = CullOf( built.Shader, Written( root ) ),
			};
		}
		catch ( JsonException )
		{
			return fields;
		}
	}

	//A material's shader numbers by name under Numbers, each one number or four:
	//"Numbers": { "g_flSway": 2.5, "g_vTint": [1, 0, 0, 1] }; unmentioned ones take the shader's default
	private static Dictionary<string, Vector4> Written( JsonElement root )
	{
		if ( !root.TryGetProperty( nameof( MaterialDefinition.Numbers ), out var listed ) ||
			listed.ValueKind != JsonValueKind.Object )
		{
			return null;
		}

		Dictionary<string, Vector4> written = null;

		foreach ( var field in listed.EnumerateObject() )
		{
			Vector4 value;

			if ( field.Value.ValueKind == JsonValueKind.Number )
			{
				var one = field.Value.GetSingle();
				value = new Vector4( one, one, one, one );
			}
			else if ( field.Value.ValueKind == JsonValueKind.Array )
			{
				Span<float> parts = stackalloc float[4];
				var at = 0;

				foreach ( var part in field.Value.EnumerateArray() )
				{
					if ( at >= 4 ) break;
					if ( part.ValueKind != JsonValueKind.Number ) continue;

					parts[at++] = part.GetSingle();
				}

				value = new Vector4( parts[0], parts[1], parts[2], parts[3] );
			}
			else if ( field.Value.ValueKind == JsonValueKind.String && Vector4.TryParse( field.Value.GetString(), out var parsed ) )
			{
				//"x,y,z,w", as the engine's serializer writes a Vector4
				value = parsed;
			}
			else
			{
				continue;
			}

			written ??= new Dictionary<string, Vector4>();
			written[field.Name] = value;
		}

		return written;
	}

	//A material's shader features under Features by define, e.g. "Features": { "F_PARALLAX": 1 }; these
	//win over what its maps imply - see Implies
	private static Dictionary<string, int> Told( JsonElement root )
	{
		var told = new Dictionary<string, int>( StringComparer.Ordinal );

		//An older file's Blend and TwoSided read as the features they became - see MaterialDefinition.LegacyFeatures
		if ( !root.TryGetProperty( nameof( MaterialDefinition.Features ), out var listed ) ||
			listed.ValueKind != JsonValueKind.Object )
		{
			MaterialDefinition.LegacyFeatures( root, told );
			return told;
		}

		foreach ( var field in listed.EnumerateObject() )
		{
			if ( field.Value.ValueKind != JsonValueKind.Number ) continue;
			if ( !field.Value.TryGetInt32( out var value ) ) continue;

			told[field.Name] = value;
		}

		MaterialDefinition.LegacyFeatures( root, told );

		return told;
	}

	//Whether a material has the field a shader's feature declaration points at (Map(...), Flag(...))
	private static bool Implies( MaterialFields fields, CoreShaderFeatures.Implied what, string field )
		=> what switch
		{
			//Every slot holds a stand-in, so ask if a map was chosen, not merely present (see IsStandIn)
			CoreShaderFeatures.Implied.Map => field switch
			{
				//The colour slot needs a real picture, since a flat colour folds into the tint for free
				nameof( MaterialDefinition.ColorMap ) => CoreDevTextures.IsPicture( fields.Colour ),

				nameof( MaterialDefinition.Normal ) => CoreDevTextures.IsChosen( fields.Normal ),
				nameof( MaterialDefinition.Emission ) => CoreDevTextures.IsChosen( fields.Emission ),

				//Anything else is one of the shader's own, so it is asked of the pictures the material
				//set on it rather than of the four every material has - see MaterialDefinition.Textures.
				_ => Names( fields.Pictures, field ),
			},

			CoreShaderFeatures.Implied.Flag => field switch
			{
				//Any metal at all is metal: 0.7 builds with metalness, nothing said does not
				nameof( MaterialDefinition.Metalness ) => fields.Metalness > 0.0f,
				_ => false,
			},

			_ => false,
		};

	//The shader build a material asks for; null, the cheap common case, is the shipped shader with nothing to compile
	internal static string ComboFor( string shaderPath, IReadOnlyDictionary<string, int> told,
		Func<CoreShaderFeatures.Implied, string, bool> has )
	{
		var written = System.IO.Path.ChangeExtension(
			string.IsNullOrWhiteSpace( shaderPath ) ? StandardShader : shaderPath, ".shader" )
			.Replace( '\\', '/' );

		//Find the shader file the same way the stages are found, including legacy name mapping
		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( written ) );

		if ( onDisk is null ) return null;

		var features = CoreShaderFeatures.Of( onDisk );

		if ( features.Length == 0 ) return null;

		var combo = CoreShaderFeatures.Combo( features, CoreShaderFeatures.For( features, has, told ) );

		//The shader's own defaults are what a blob was built with, so saying so explicitly would only cost a compile.
		return combo == CoreShaderFeatures.Combo( features, null ) ? null : combo;
	}

	//Value of each declared feature for a material (told, implied, else default); null if none declared
	internal static Dictionary<string, int> FeatureValues( string shaderPath, IReadOnlyDictionary<string, int> told,
		Func<CoreShaderFeatures.Implied, string, bool> has )
	{
		var written = System.IO.Path.ChangeExtension(
			string.IsNullOrWhiteSpace( shaderPath ) ? StandardShader : shaderPath, ".shader" )
			.Replace( '\\', '/' );

		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( written ) );
		if ( onDisk is null ) return null;

		var features = CoreShaderFeatures.Of( onDisk );
		if ( features.Length == 0 ) return null;

		var chosen = CoreShaderFeatures.For( features, has, told );
		var values = new Dictionary<string, int>( StringComparer.Ordinal );

		foreach ( var feature in features )
			values[feature.Name] = chosen.TryGetValue( feature.Name, out var said ) ? said : feature.Default;

		return values;
	}

	//Pipeline state from features: F_ALPHA cut-out, F_TRANSLUCENT blended, F_BACKFACES two-sided
	internal static (bool Masked, bool Translucent, bool TwoSided) DrawFlags( IReadOnlyDictionary<string, int> values )
	{
		if ( values is null ) return (false, false, false);

		bool On( string name ) => values.TryGetValue( name, out var value ) && value > 0;

		return (On( "F_ALPHA" ), On( "F_TRANSLUCENT" ), On( "F_BACKFACES" ));
	}

	internal static ulong ResolveMaterialTexture( string materialPath,
		Dictionary<string, ulong> textures, ulong fallback )
	{
		if ( string.IsNullOrWhiteSpace( materialPath ) ) return fallback;

		//Model binaries compiled before the extensions lost their ess- prefix carry the old material names in
		//their string tables, where a migration cannot reach. The files on disk have been renamed; follow them.
		if ( materialPath.EndsWith( ".essmat", System.StringComparison.OrdinalIgnoreCase ) )
			materialPath = materialPath[..^7] + ".mat";
		if ( textures.TryGetValue( materialPath, out var cached ) ) return cached;

		textures[materialPath] = fallback;

		var imagePath = ReadColorMap( materialPath );
		if ( string.IsNullOrWhiteSpace( imagePath ) ) return fallback;

		//What the colour is cut by where the material names a mask of its own: folded into the
		//colour's alpha as it is compiled, into blocks of their own - see CompiledPaths.TextureBlocks.
		var cut = ReadMaterial( materialPath ).Cut;

		//The engine's own flat picture where named - see CoreDevTextures; nothing on disk to compile
		if ( CoreDevTextures.Get( imagePath ) is var built && built != 0 )
		{
			textures[materialPath] = built;
			return built;
		}

		var imageFull = CoreModel.ResolveOnDisk( imagePath.Replace( '\\', '/' ) );

		//Not here as a picture: a build that carries what it compiles to instead.
		if ( imageFull is null )
		{
			var shipped = CorePictures.Shipped( imagePath, srgb: true, cut );
			if ( shipped == 0 ) return fallback;

			textures[materialPath] = shipped;
			return shipped;
		}

		if ( CoreImportedTexture.IsTheirs( imageFull ) )
		{
			//Blocks through unchanged where format and device allow, as the map path; else the image round trip
			var kept = CoreImportedTexture.BlockTexture( imageFull, srgb: true );

			if ( kept != 0 )
			{
				textures[materialPath] = kept;
				return kept;
			}

			imageFull = CoreImportedTexture.AsImageFile( imageFull );
			if ( imageFull is null ) return fallback;
		}

		//The compiled form sits beside the image, extension appended rather than replaced so wood.png and wood.jpg
		//cannot fight over one cache file. It ends in _c, so the ported projects' gitignore already covers it.
		var cutFull = cut is null ? null : CoreModel.ResolveOnDisk( cut.Replace( '\\', '/' ) );

		if ( cut is not null && cutFull is null )
		{
			Log.Warning( $"{materialPath}: its opacity mask {cut} is not there - drawn uncut" );
			cut = null;
		}

		var texture = Compiled( imageFull, CompiledPaths.TextureBlocks( imageFull, srgb: true, cut ), srgb: true, cutFull );

		if ( texture == 0 )
		{
			Log.Warning( $"could not compile {imagePath}" );
			return fallback;
		}

		textures[materialPath] = texture;
		return texture;
	}

	//Loads a compiled texture, recompiling if missing, older than its image, or unreadable (once)
	private static ulong Compiled( string image, string compiled, bool srgb, string cutBy = null )
	{
		//Compression is encoded in the cache file name; only used where the device supports BC blocks
		var compress = Rinten.Rendering.TextureSettings.Compress &&
			CoreNative.Available && CoreRender.SupportsBc() != 0;

		if ( compress ) compiled = CompiledPaths.Compressed( compiled );

		var stale = CorePictures.IsStale( image, compiled, cutBy );

		if ( stale && !CoreTextureFile.Compile( image, compiled, srgb, generateMips: true, compress, cutBy ) )
			return 0;

		var texture = CoreRenderDevice.CreateTexture( compiled );

		if ( texture != 0 || stale ) return texture;

		if ( !CoreTextureFile.Compile( image, compiled, srgb, generateMips: true, compress, cutBy ) ) return 0;

		return CoreRenderDevice.CreateTexture( compiled );
	}

	//Whether every skin matrix is near identity, i.e. the rig is not moving anything
	internal static bool AtBindPose( System.Numerics.Matrix4x4[] pose )
	{
		const float tolerance = 1e-4f;

		foreach ( var m in pose )
		{
			//A dual quaternion - see DualQuaternions: at rest it is no turn, no move and a scale of one.
			if ( m.M44 > 1.5f )
			{
				if ( MathF.Abs( m.M11 ) > tolerance || MathF.Abs( m.M12 ) > tolerance || MathF.Abs( m.M13 ) > tolerance ||
					MathF.Abs( MathF.Abs( m.M14 ) - 1.0f ) > tolerance ||
					MathF.Abs( m.M21 ) > tolerance || MathF.Abs( m.M22 ) > tolerance || MathF.Abs( m.M23 ) > tolerance || MathF.Abs( m.M24 ) > tolerance ||
					MathF.Abs( m.M31 - 1.0f ) > tolerance || MathF.Abs( m.M32 - 1.0f ) > tolerance || MathF.Abs( m.M33 - 1.0f ) > tolerance )
					return false;

				continue;
			}

			if ( MathF.Abs( m.M11 - 1.0f ) > tolerance || MathF.Abs( m.M22 - 1.0f ) > tolerance || MathF.Abs( m.M33 - 1.0f ) > tolerance ||
				MathF.Abs( m.M12 ) > tolerance || MathF.Abs( m.M13 ) > tolerance || MathF.Abs( m.M21 ) > tolerance ||
				MathF.Abs( m.M23 ) > tolerance || MathF.Abs( m.M31 ) > tolerance || MathF.Abs( m.M32 ) > tolerance ||
				MathF.Abs( m.M41 ) > tolerance || MathF.Abs( m.M42 ) > tolerance || MathF.Abs( m.M43 ) > tolerance )
				return false;
		}

		return true;
	}

	//Mesh-to-animated bone index map by name, built once per pair instead of per bone per frame
	private sealed class BoneMap
	{
		public Model Model;
		public int[] At = System.Array.Empty<int>();
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneMesh, BoneMap> boneMaps = new();

	//Bone i of the mesh is bone At[i] of the model, or -1.
	private static int[] BoneIndices( SceneMesh mesh, Model model )
	{
		var map = boneMaps.GetOrCreateValue( mesh );

		//The model can change under a renderer; then names are looked up again against its current skeleton
		if ( ReferenceEquals( map.Model, model ) && map.At.Length == mesh.Bones.Length ) return map.At;

		var at = new int[mesh.Bones.Length];

		for ( var i = 0; i < at.Length; i++ )
			at[i] = model?.Bones?.GetBone( mesh.Bones[i].Name )?.Index ?? -1;

		map.Model = model;
		map.At = at;

		return at;
	}

	//Every bone of one skeleton, read in one go rather than one at a time.
	private static Transform[] worldBoneScratch = System.Array.Empty<Transform>();

	//A scene model's skin palette for a mesh drawn at toModel; bone-merged ones draw at their own object.
	//Kept in an array the scene model holds; at rest without one
	internal static System.Numerics.Matrix4x4[] PoseBones( SceneMesh mesh, SceneModel posed, Transform toModel )
	{
		var count = mesh.Bones.Length;
		var valid = posed.IsValid();

		if ( valid && ( posed.PoseBuffer is null || posed.PoseBuffer.Length != count ) )
			posed.PoseBuffer = new System.Numerics.Matrix4x4[count];

		var pose = valid ? posed.PoseBuffer : new System.Numerics.Matrix4x4[count];

		//The whole skeleton in one read, with bone mapping settled once
		var indices = valid && posed.Model is not null ? BoneIndices( mesh, posed.Model ) : null;
		var world = System.ReadOnlySpan<Transform>.Empty;

		if ( indices is not null && posed.Model is { BoneCount: > 0 } model )
		{
			if ( worldBoneScratch.Length < model.BoneCount )
				worldBoneScratch = new Transform[model.BoneCount];

			var into = worldBoneScratch.AsSpan( 0, model.BoneCount );

			//Cleared first: a skeleton with fewer bones posed than the model has leaves the tail
			//alone, and Transform.Zero is what "this one is not posed" has always looked like.
			into.Clear();
			posed.GetBoneWorldTransforms( into );
			world = into;
		}

		for ( var i = 0; i < mesh.Bones.Length; i++ )
		{
			var bone = mesh.Bones[i];
			var current = System.Numerics.Matrix4x4.Identity;
			var at = indices is null ? -1 : indices[i];

			if ( at >= 0 && at < world.Length && world[at] != Transform.Zero )
			{
				//The shader works in the model's space, so the bone's world transform comes back through the object's.
				var local = toModel.ToLocal( world[at] );

				current = WorldOf( local );
			}
			else
			{
				//At rest: the inverse bind's inverse, so they cancel and the vertex stays put
				if ( !System.Numerics.Matrix4x4.Invert( bone.InverseBind, out current ) )
					current = System.Numerics.Matrix4x4.Identity;
			}

			//Row vectors: a vertex goes through the inverse bind first.
			pose[i] = bone.InverseBind * current;
		}

		if ( mesh.DualQuaternion ) DualQuaternions( pose );

		return pose;
	}

	//Converts a bone palette to dual quaternions in place; any sheared bone keeps it all as matrices
	private static void DualQuaternions( System.Numerics.Matrix4x4[] pose )
	{
		Span<System.Numerics.Vector3> scales = pose.Length <= 256 ? stackalloc System.Numerics.Vector3[pose.Length] : new System.Numerics.Vector3[pose.Length];
		Span<System.Numerics.Quaternion> turns = pose.Length <= 256 ? stackalloc System.Numerics.Quaternion[pose.Length] : new System.Numerics.Quaternion[pose.Length];
		Span<System.Numerics.Vector3> moves = pose.Length <= 256 ? stackalloc System.Numerics.Vector3[pose.Length] : new System.Numerics.Vector3[pose.Length];

		for ( var i = 0; i < pose.Length; i++ )
		{
			if ( !System.Numerics.Matrix4x4.Decompose( pose[i], out scales[i], out turns[i], out moves[i] ) ) return;
		}

		for ( var i = 0; i < pose.Length; i++ )
		{
			var r = System.Numerics.Quaternion.Normalize( turns[i] );
			var t = moves[i];

			//Half the translation, as a quaternion with no real part, times the rotation.
			var d = new System.Numerics.Vector4(
				0.5f * ( ( t.X * r.W ) + ( t.Y * r.Z ) - ( t.Z * r.Y ) ),
				0.5f * ( ( -t.X * r.Z ) + ( t.Y * r.W ) + ( t.Z * r.X ) ),
				0.5f * ( ( t.X * r.Y ) - ( t.Y * r.X ) + ( t.Z * r.W ) ),
				0.5f * ( ( -t.X * r.X ) - ( t.Y * r.Y ) - ( t.Z * r.Z ) ) );

			var s = scales[i];

			pose[i] = new System.Numerics.Matrix4x4(
				r.X, r.Y, r.Z, r.W,
				d.X, d.Y, d.Z, d.W,
				s.X, s.Y, s.Z, 0.0f,
				0.0f, 0.0f, 0.0f, 2.0f );
		}
	}

	//Sixteen row-major floats into the matrix, whose fields are named in that order
	private static System.Numerics.Matrix4x4 RowMajor( float* m )
		=> new(
			m[0], m[1], m[2], m[3],
			m[4], m[5], m[6], m[7],
			m[8], m[9], m[10], m[11],
			m[12], m[13], m[14], m[15] );

	//What a shader and its material say about culling: grow, never, fade
	private static Vector3 CullOf( string shader, Dictionary<string, Vector4> written )
		=> CoreShaderParams.Cull( CoreResources.Declared( shader ),
			name => written is not null && written.TryGetValue( name, out var value ) ? value : null );

	//The same for a material named by a material group, which swaps the lot.
	internal static Vector3 ReadMaterialCull( string materialPath )
	{
		var how = ReadMaterial( materialPath );

		return how.Cull;
	}

	internal static Color ReadMaterialTint( string materialPath ) => MaterialOf( materialPath ).Tint;

	private static Color ReadMaterialTintFile( string materialPath )
	{
		if ( CoreImportedMaterial.Read( materialPath ) is { } tinted )
			return tinted.Tint;

		//The same legacy-name shim as the texture path: old binaries say .essmat.
		if ( materialPath.EndsWith( ".essmat", System.StringComparison.OrdinalIgnoreCase ) )
			materialPath = materialPath[..^7] + ".mat";

		var full = CoreModel.ResolveTextOnDisk( materialPath.Replace( '\\', '/' ) );
		if ( full is null ) return Color.White;

		try
		{
			using var document = JsonDocument.Parse( CompiledFile.TextOf( full ) );

			if ( !document.RootElement.TryGetProperty( nameof( MaterialDefinition.Tint ), out var value ) )
				return Color.White;

			return value.ValueKind == JsonValueKind.String && Color.Parse( value.GetString() ) is Color parsed
				? parsed
				: Color.White;
		}
		catch ( JsonException )
		{
			return Color.White;
		}
	}

	//Whether a material picks its own shader; legacy names for the standard surface don't count
	private static bool NamesItsOwnShader( string shaderPath )
	{
		if ( string.IsNullOrWhiteSpace( shaderPath ) ) return false;

		return !CoreShaderFile.Real( shaderPath.Replace( '\\', '/' ) )
			.Equals( CoreShaderFile.Real( StandardShader ), System.StringComparison.OrdinalIgnoreCase );
	}

	internal static bool MaterialExists( string materialPath )
	{
		if ( string.IsNullOrWhiteSpace( materialPath ) ) return false;

		if ( CoreImportedMaterial.Read( materialPath ) is not null ) return true;

		if ( materialPath.EndsWith( ".essmat", System.StringComparison.OrdinalIgnoreCase ) )
			materialPath = materialPath[..^7] + ".mat";

		return CoreModel.ResolveTextOnDisk( materialPath.Replace( '\\', '/' ) ) is not null;
	}

	internal static string ReadColorMap( string materialPath )
	{
		if ( CoreImportedMaterial.Read( materialPath ) is { } theirs )
			return theirs.ColorMap;

		var full = CoreModel.ResolveTextOnDisk( materialPath.Replace( '\\', '/' ) );
		if ( full is null ) return null;

		try
		{
			using var document = JsonDocument.Parse( CompiledFile.TextOf( full ) );

			return document.RootElement.TryGetProperty( nameof( MaterialDefinition.ColorMap ), out var value )
				? value.GetString()
				: null;
		}
		catch ( JsonException )
		{
			return null;
		}
	}
}
