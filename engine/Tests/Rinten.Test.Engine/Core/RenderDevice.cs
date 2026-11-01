using System;
using CoreAssets = Rinten.CoreAssets;

namespace EngineTests;

//Opening a Vulkan device. No window and no swapchain: a device on its own is headless, which is what makes this testable on a build machine at all. What
//it proves is that the core finds a driver, picks a device that can draw, and opens it with the features the renderer is going to be written against.
[TestClass]
[DoNotParallelize] //There is one device in the process and these tests close it.
public class RenderDeviceTest
{
	//Reporting an error still bumps CoreErrors.Count, which other tests use as their oracle
	[TestMethod]
	public void ReportedErrorsAreCounted()
	{
		var before = CoreErrors.Count;

		VkDevice.SayWarning( "a warning from the renderer, on purpose, from a test" );
		VkDevice.SayError( "an error from the renderer, on purpose, from a test" );

		Assert.AreEqual( before + 1, CoreErrors.Count,
			"the error should have been counted, and the warning should not" );
	}

	//A device opens, names itself, and reports a version. Inconclusive rather than failed when there is no driver. A machine without
	//a GPU - a container, a build agent - is not a broken engine, and making this fail there would train everyone to ignore it.
	[TestMethod]
	public void OpensADevice()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( $"rintencore is not loaded: {CoreNative.BuildInfo}" );
			return;
		}

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			Assert.IsTrue( CoreRenderDevice.IsReady );

			Assert.IsFalse( string.IsNullOrWhiteSpace( CoreRenderDevice.DeviceName ),
				"a device that opened should say what it is" );

			//1.3 is what the core asks for, so anything less means the version is being read wrong rather than that an old driver slipped through.
			var version = CoreRenderDevice.ApiVersion;

			Assert.IsTrue( version.Major > 1 || ( version.Major == 1 && version.Minor >= 3 ),
				$"asked for Vulkan 1.3, device reports {version}" );
		}
		finally
		{
			CoreRenderDevice.Shutdown();
		}
	}

	//Initialising twice is not an error, and shutting down twice is not a crash. Both happen for real: the editor and the game live in one process,
	//and a test host runs whichever tests it likes in whichever order. The second shutdown is the dangerous one - it is where a double free would be.
	[TestMethod]
	public void SurvivesBeingStartedAndStoppedTwice()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		var name = CoreRenderDevice.DeviceName;

		Assert.IsTrue( CoreRenderDevice.Initialize( validation: true ), "a second init should find the device already open" );
		Assert.AreEqual( name, CoreRenderDevice.DeviceName, "and should not have swapped it for another one" );

		CoreRenderDevice.Shutdown();

		Assert.IsFalse( CoreRenderDevice.IsReady );

		CoreRenderDevice.Shutdown();

		//And the device comes back afterwards, rather than the core being left in a state where it can never open one again.
		if ( CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.AreEqual( name, CoreRenderDevice.DeviceName );
			CoreRenderDevice.Shutdown();
		}
	}

	//Destroyed textures give their descriptor sets back, so a reload does not run the pool out.
	[TestMethod]
	public void RecyclesTextureDescriptorsAfterDestroy()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			const int count = 1200;
			var made = new ulong[count];
			Span<byte> pixel = stackalloc byte[4] { 255, 255, 255, 255 };

			for ( var i = 0; i < count; i++ )
			{
				made[i] = CoreRenderDevice.CreateTextureRgba( pixel, 1, 1, srgb: false );
				Assert.AreNotEqual( 0ul, made[i], $"texture {i} of the first batch should land: {CoreRenderDevice.Description}" );
			}

			for ( var i = 0; i < count; i++ )
				CoreRenderDevice.DestroyTexture( made[i] );

			for ( var i = 0; i < count; i++ )
			{
				made[i] = CoreRenderDevice.CreateTextureRgba( pixel, 1, 1, srgb: false );
				Assert.AreNotEqual( 0ul, made[i], $"texture {i} after destroy should reuse a set: {CoreRenderDevice.Description}" );
			}

			for ( var i = 0; i < count; i++ )
				CoreRenderDevice.DestroyTexture( made[i] );
		}
		finally
		{
			CoreRenderDevice.Shutdown();
		}
	}

	//A hidden window still gets a real surface, swapchain and presented frames
	[TestMethod]
	public void DrawsFramesIntoAWindow()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten render test", 320, 180, hidden: true );

		if ( window is null )
		{
			//No display: a container, or a machine nobody is logged into.
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			Assert.AreNotEqual( 0ul, swapchain,
				$"the window should have become a swapchain: {CoreRenderDevice.Description}" );

			var (width, height, images) = CoreRenderDevice.SwapchainInfo( swapchain );

			Assert.AreEqual( 320, width );
			Assert.AreEqual( 180, height );
			Assert.IsTrue( images >= 2, $"a swapchain needs at least two images, got {images}" );

			//Enough frames to go round the two in flight more than once, so the second time through
			//waits on a fence that a real submit signalled rather than on the one they were created with.
			const int frames = 8;
			var presented = 0;

			for ( var i = 0; i < frames; i++ )
			{
				CoreWindow.Pump();

				//A different colour each frame. Nothing reads it back, but a clear value that changes is one the driver cannot have optimised away.
				var shade = i / (float)frames;

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( shade, 0.2f, 1.0f - shade ) ) ) continue;
				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( frames, presented, "every frame should have reached the screen" );
			Assert.AreEqual( (ulong)frames, CoreRenderDevice.FrameCount( swapchain ) );
		}
		finally
		{
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//A triangle through Slang, SPIR-V, pipeline and present; validation layers referee
	[TestMethod]
	public void DrawsATriangleThroughTheWholeStack()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( $"no shader compiler: {CoreShaderCompiler.Info}" );
			return;
		}

		using var window = CoreWindow.Create( "rinten triangle test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var shaderPath = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-triangle-test-{System.Guid.NewGuid():N}.slang" );

		System.IO.File.WriteAllText( shaderPath, """
			struct V2F
			{
				float4 position : SV_Position;
				float3 colour : COLOR0;
			};

			static const float2 positions[3] = { float2( 0.0, -0.5 ), float2( 0.5, 0.5 ), float2( -0.5, 0.5 ) };
			static const float3 colours[3] = { float3( 1, 0, 0 ), float3( 0, 1, 0 ), float3( 0, 0, 1 ) };

			[shader("vertex")]
			V2F MainVs( uint id : SV_VertexID )
			{
				V2F o;
				o.position = float4( positions[id], 0.0, 1.0 );
				o.colour = colours[id];
				return o;
			}

			[shader("fragment")]
			float4 MainPs( V2F i ) : SV_Target
			{
				return float4( i.colour, 1.0 );
			}
			""" );

		var pipeline = 0ul;

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			var vertex = CoreRenderDevice.CompileShader( shaderPath, "MainVs", CoreShaderCompiler.Stage.Vertex );
			var pixel = CoreRenderDevice.CompileShader( shaderPath, "MainPs", CoreShaderCompiler.Stage.Pixel );

			Assert.IsNotNull( vertex, $"the vertex shader should compile: {CoreRenderDevice.ShaderError}" );
			Assert.IsNotNull( pixel, $"the pixel shader should compile: {CoreRenderDevice.ShaderError}" );

			pipeline = CoreRenderDevice.CreatePipeline( vertex, pixel );

			Assert.AreNotEqual( 0ul, pipeline, $"the pipeline should bake: {CoreRenderDevice.Description}" );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var i = 0; i < 8; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.15f ) ) ) continue;

				Assert.IsTrue( CoreRenderDevice.Draw( pipeline, 3 ), "the draw should record" );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 8, presented, "every triangle frame should have reached the screen" );

			//The whole point of installing the layers: any wrong call above has already been named in the log, and this makes it fail the test too.
			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
			System.IO.File.Delete( shaderPath );
		}
	}

	//A real mesh: vertices in the engine layout in a device buffer, 32-bit indices, a camera in push
	//constants, the depth buffer doing its work - the whole spinning-cube path, with validation refereeing.
	[TestMethod]
	public void DrawsAnIndexedMeshWithDepth()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten mesh test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var cube = default( CoreRenderDemo.CubeParts );

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			cube = CoreRenderDemo.MakeCube();
			cube.Texture = CoreRenderDemo.MakeCheckerTexture();

			Assert.AreNotEqual( 0ul, cube.Pipeline,
				$"the cube pipeline should bake: {CoreRenderDevice.Description}" );
			Assert.AreNotEqual( 0ul, cube.Vertices, "the vertex buffer should exist" );
			Assert.AreNotEqual( 0ul, cube.Indices, "the index buffer should exist" );
			Assert.AreNotEqual( 0ul, cube.Texture, $"the texture should upload: {CoreRenderDevice.Description}" );
			Assert.AreEqual( 36, cube.IndexCount );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var i = 0; i < 8; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.15f ) ) ) continue;

				//A different rotation each frame, like the real thing.
				var push = CoreRenderDemo.CubePush( i * 0.2f, 320.0f / 180.0f );

				Assert.IsTrue(
					CoreRenderDevice.DrawIndexed( cube.Pipeline, cube.Vertices, cube.Indices, cube.IndexCount, push,
						texture: cube.Texture ),
					"the indexed draw should record" );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 8, presented, "every cube frame should have reached the screen" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( cube.Pipeline );
			CoreRenderDevice.DestroyBuffer( cube.Vertices );
			CoreRenderDevice.DestroyBuffer( cube.Indices );
			CoreRenderDevice.DestroyTexture( cube.Texture );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//A compiled model, from bytes on disk to presented frames: built with the model builder, saved
	//as .mdl_c, loaded back, uploaded, drawn. This is the whole point of the renderer, in one test.
	[TestMethod]
	public unsafe void DrawsAModelFromDisk()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten model test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var modelPath = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-render-model-{System.Guid.NewGuid():N}.mdl_c" );

		//The demo's cube, saved as a model file - so what is loaded back and drawn went through the same format every ported model goes through.
		var builder = new CoreAssets.ModelBuilder();

		CoreRenderDemo.BuildCubeMesh( out var vertices, out var indices );

		fixed ( Vertex* v = vertices )
		{
			Assert.AreEqual( 0, builder.AddVertices( v, vertices.Length ) );
		}

		fixed ( uint* i = indices )
		{
			Assert.AreEqual( 0, builder.AddIndices( i, indices.Length ) );
		}

		builder.AddMesh( "cube", "dev/default.mat", 0, indices.Length, 0, vertices.Length );

		var bounds = stackalloc float[6] { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };
		builder.SetRenderBounds( bounds );

		Assert.AreEqual( 1, builder.Save( modelPath ),
			"the model should have saved" );

		builder.Dispose();

		//A texture the same way the porter makes them: an image file compiled to .tex_c, then loaded
		//back and put on the device. The model and its texture both travel through their real formats.
		var imagePath = WriteTestImage( 16 );
		var texturePath = System.IO.Path.ChangeExtension( imagePath, ".tex_c" );

		Assert.IsTrue( CoreTextureFile.Compile( imagePath, texturePath, true, true, false ),
			"the image should compile to a texture" );

		var mesh = default( CoreRenderDemo.CubeParts );

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			mesh = CoreRenderDemo.MakeModelMesh( modelPath );
			mesh.Texture = CoreRenderDevice.CreateTexture( texturePath );

			Assert.AreNotEqual( 0ul, mesh.Texture,
				$"the compiled texture should reach the device: {CoreRenderDevice.Description}" );

			Assert.AreNotEqual( 0ul, mesh.Pipeline, "the model should have become a drawable mesh" );
			Assert.AreEqual( 36, mesh.IndexCount, "with all its indices" );
			Assert.AreEqual( 0.87f, mesh.Radius, 0.01f, "and the bounds it was saved with" );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var i = 0; i < 4; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.15f ) ) ) continue;

				var push = CoreRenderDemo.CubePush( i * 0.3f, 320.0f / 180.0f, mesh.Center, mesh.Radius );

				Assert.IsTrue(
					CoreRenderDevice.DrawIndexed( mesh.Pipeline, mesh.Vertices, mesh.Indices, mesh.IndexCount, push,
						texture: mesh.Texture ) );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented, "every model frame should have reached the screen" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( mesh.Pipeline );
			CoreRenderDevice.DestroyBuffer( mesh.Vertices );
			CoreRenderDevice.DestroyBuffer( mesh.Indices );
			CoreRenderDevice.DestroyTexture( mesh.Texture );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
			System.IO.File.Delete( modelPath );
			System.IO.File.Delete( imagePath );
			System.IO.File.Delete( texturePath );
		}
	}

	//Scene mesh path: base-vertex-relative indices, .mat materials, and fallback material
	[TestMethod]
	public unsafe void SceneMeshPathLoadsMaterialsAndMeshRanges()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten scene test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var stem = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-scene-{System.Guid.NewGuid():N}" );

		var modelPath = stem + ".mdl_c";
		var imagePath = WriteTestImage( 16 );
		var materialPath = stem + ".mat";

		//A material naming the image, as the porter writes them.
		System.IO.File.WriteAllText( materialPath, $$"""
			{ "ColorMap": {{System.Text.Json.JsonSerializer.Serialize( imagePath )}} }
			""" );

		//Two cubes in one model. The second mesh's vertices sit after the first's and its indices start at zero
		//again - the relative form that makes first_vertex matter: drawn without it, mesh two shows mesh one.
		CoreRenderDemo.BuildCubeMesh( out var vertices, out var indices );

		var builder = new CoreAssets.ModelBuilder();

		fixed ( Vertex* v = vertices )
		{
			Assert.AreEqual( 0, builder.AddVertices( v, vertices.Length ) );
			Assert.AreEqual( vertices.Length, builder.AddVertices( v, vertices.Length ) );
		}

		fixed ( uint* i = indices )
		{
			builder.AddIndices( i, indices.Length );
			builder.AddIndices( i, indices.Length );
		}

		builder.AddMesh( "textured", materialPath, 0, indices.Length, 0, vertices.Length );
		builder.AddMesh( "bare", "materials/no-such.mat", indices.Length, indices.Length, vertices.Length, vertices.Length );

		var bounds = stackalloc float[6] { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };
		builder.SetRenderBounds( bounds );

		Assert.AreEqual( 1, builder.Save( modelPath ) );
		builder.Dispose();

		var pipeline = 0ul;
		var fallback = 0ul;
		var meshes = new System.Collections.Generic.Dictionary<string, CoreSceneRenderer.SceneMesh>();
		var textures = new System.Collections.Generic.Dictionary<string, ulong>();

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			pipeline = CoreSceneRenderer.MakeScenePipeline();
			fallback = CoreRenderDemo.MakeCheckerTexture();

			Assert.AreNotEqual( 0ul, pipeline, $"the scene pipeline should bake: {CoreRenderDevice.Description}" );

			var mesh = CoreSceneRenderer.LoadMesh( modelPath, meshes, textures, fallback );

			Assert.IsNotNull( mesh, "the model should load through the scene path" );
			Assert.AreEqual( 2, mesh.Meshes.Count, "with both its meshes" );

			Assert.AreEqual( vertices.Length, mesh.Meshes[1].FirstVertex,
				"the second mesh's indices are relative to its own base" );

			Assert.AreNotEqual( fallback, mesh.Meshes[0].Texture,
				"the real material should have compiled its image on demand" );
			Assert.AreEqual( fallback, mesh.Meshes[1].Texture,
				"the missing material should land on the fallback" );

			//Either name: a compiled texture that was packed into blocks is kept under its
			//own so that turning the setting off is not answered with yesterday's cache.
			Assert.IsTrue( System.IO.File.Exists( imagePath + ".tex_c" ) ||
				System.IO.File.Exists( imagePath + ".tex_c.bc" ),
				"the compiled texture should sit beside its image" );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var frame = 0; frame < 4; frame++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ) ) continue;

				var world = System.Numerics.Matrix4x4.CreateFromYawPitchRoll( frame * 0.3f, 0.4f, 0 );

				var view = System.Numerics.Matrix4x4.CreateLookAt(
					new System.Numerics.Vector3( 0, 0, 2.4f ),
					System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY );

				var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( 1.0f, 320.0f / 180.0f, 0.05f, 20.0f );
				projection.M22 = -projection.M22;

				//The camera and the lights are the frame's now, not the draw's.
				CoreRenderDevice.SetupFrame( CoreSceneRenderer.BuildFrame( null, view * projection, Vector3.Zero ) );

				var push = CoreSceneRenderer.ScenePush( world, Color.White );

				foreach ( var part in mesh.Meshes )
				{
					Assert.IsTrue( CoreRenderDevice.DrawIndexed( pipeline, mesh.Vertices, mesh.Indices,
						part.IndexCount, push, part.FirstIndex, part.FirstVertex, part.Texture ) );
				}

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			foreach ( var loaded in meshes.Values )
			{
				if ( loaded is null ) continue;

				CoreRenderDevice.DestroyBuffer( loaded.Vertices );
				CoreRenderDevice.DestroyBuffer( loaded.Indices );
			}

			foreach ( var texture in textures.Values )
			{
				if ( texture != fallback ) CoreRenderDevice.DestroyTexture( texture );
			}

			CoreRenderDevice.DestroyTexture( fallback );
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();

			System.IO.File.Delete( modelPath );
			System.IO.File.Delete( materialPath );
			System.IO.File.Delete( imagePath );
			System.IO.File.Delete( imagePath + ".tex_c" );
		}
	}

	//Dynamic lines via per-frame scratch, growing it mid-frame to test buffer retirement
	[TestMethod]
	public unsafe void DrawsDynamicLinesAndGrowsScratchMidFrame()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten lines test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var shaderPath = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-lines-test-{System.Guid.NewGuid():N}.slang" );

		System.IO.File.WriteAllText( shaderPath, """
			struct VIn
			{
				[[vk::location(0)]] float3 position : POSITION;
				[[vk::location(1)]] float4 colour : COLOR0;
			};

			struct V2F
			{
				float4 position : SV_Position;
				float4 colour : COLOR0;
			};

			[[vk::push_constant]]
			cbuffer Push
			{
				row_major float4x4 g_worldToClip;
			};

			[shader("vertex")]
			V2F MainVs( VIn i )
			{
				V2F o;
				o.position = mul( float4( i.position, 1.0 ), g_worldToClip );
				o.colour = i.colour;
				return o;
			}

			[shader("fragment")]
			float4 MainPs( V2F i ) : SV_Target
			{
				return i.colour;
			}
			""" );

		var pipeline = 0ul;

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			var vs = CoreRenderDevice.CompileShader( shaderPath, "MainVs", CoreShaderCompiler.Stage.Vertex );
			var ps = CoreRenderDevice.CompileShader( shaderPath, "MainPs", CoreShaderCompiler.Stage.Pixel );

			Assert.IsNotNull( vs, $"the line shader should compile: {CoreRenderDevice.ShaderError}" );
			Assert.IsNotNull( ps, CoreRenderDevice.ShaderError );

			pipeline = CoreRenderDevice.CreatePipeline( vs, ps,
				CoreRender.VertexLayoutKind.Line, depthTest: true, CoreRender.Topology.Lines, blend: true );

			Assert.AreNotEqual( 0ul, pipeline, $"the line pipeline should bake: {CoreRenderDevice.Description}" );

			//16 bytes per vertex: position, then RGBA.
			static byte[] MakeLines( int lineCount )
			{
				var data = new byte[lineCount * 2 * 16];
				var span = data.AsSpan();

				for ( var i = 0; i < lineCount * 2; i++ )
				{
					var at = span[( i * 16 ).. ];
					System.Runtime.InteropServices.MemoryMarshal.Write( at, (float)i / lineCount );
					System.Runtime.InteropServices.MemoryMarshal.Write( at[4..], ( i & 1 ) == 0 ? -0.5f : 0.5f );
					System.Runtime.InteropServices.MemoryMarshal.Write( at[8..], 0.5f );
					at[12] = 255; at[13] = 128; at[14] = 64; at[15] = 200;
				}

				return data;
			}

			var identity = new byte[64];
			System.Runtime.InteropServices.MemoryMarshal.Write( identity.AsSpan(),
				System.Numerics.Matrix4x4.Identity );

			var small = MakeLines( 100 );

			//64 KB is the scratch floor; five thousand lines is 160 KB, so this draw cannot fit without growing while the small one is recorded.
			var large = MakeLines( 5000 );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var i = 0; i < 4; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ) ) continue;

				Assert.IsTrue( CoreRenderDevice.DrawDynamic( pipeline, small, 200, 16, identity ),
					"the small dynamic draw should record" );
				Assert.IsTrue( CoreRenderDevice.DrawDynamic( pipeline, large, 10000, 16, identity ),
					"the large dynamic draw should record after growing" );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
			System.IO.File.Delete( shaderPath );
		}
	}

	//The UI box path: the SDF shader compiles, its 64-byte vertex layout bakes, and rounded bordered textured boxes draw clean under validation.
	[TestMethod]
	public unsafe void DrawsUiBoxes()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten ui test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		//The engine's own file, compiled as it is - never written or deleted here.
		var shaderPath = CoreShaderFile.Source( EngineShaders.Ui );

		var pipeline = 0ul;
		var white = 0ul;

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			var vs = CoreRenderDevice.CompileShader( shaderPath, "MainVs", CoreShaderCompiler.Stage.Vertex );
			var ps = CoreRenderDevice.CompileShader( shaderPath, "MainPs", CoreShaderCompiler.Stage.Pixel );

			Assert.IsNotNull( vs, $"the UI shader's vertex stage should compile: {CoreRenderDevice.ShaderError}" );
			Assert.IsNotNull( ps, $"the UI shader's pixel stage should compile: {CoreRenderDevice.ShaderError}" );

			pipeline = CoreRenderDevice.CreatePipeline( vs, ps,
				CoreRender.VertexLayoutKind.Ui, depthTest: false, CoreRender.Topology.Triangles, blend: true );

			Assert.AreNotEqual( 0ul, pipeline, $"the UI pipeline should bake: {CoreRenderDevice.Description}" );

			Span<byte> pixel = stackalloc byte[4] { 255, 255, 255, 255 };
			white = CoreRenderDevice.CreateTextureRgba( pixel, 1, 1, srgb: false );

			Assert.AreNotEqual( 0ul, white );

			//A rounded bordered box and a textured one, built by the same code the game UI pass uses.
			var batches = new System.Collections.Generic.List<CoreUiRenderer.UiBatch>();

			var vertices = new System.Collections.Generic.List<CoreUiRenderer.UiVertex>();
			batches.Add( new CoreUiRenderer.UiBatch( vertices, 0 ) );

			void Quad( float x, float y, float w, float h, float radius, float border,
				CoreUiRenderer.UiFill kind = CoreUiRenderer.UiFill.None, float blur = 0.0f,
				System.Collections.Generic.List<CoreUiRenderer.UiVertex> target = null )
			{
				target ??= vertices;

				var rect = new Vector4( x, y, w, h );
				var radii = new Vector4( radius );
				var misc = new Vector4( border, (float)kind, 0.4f, blur );

				CoreUiRenderer.UiVertex Corner( float px, float py, float u, float v ) => new()
				{
					//Turned a little about the middle of the window, so the path a transformed panel takes -
					//shape measured where it was drawn, clipping done where it landed - is the one under test.
					Position = new Vector2(
						160.0f + ( ( px - 160.0f ) * 0.966f ) - ( ( py - 90.0f ) * 0.259f ),
						90.0f + ( ( px - 160.0f ) * 0.259f ) + ( ( py - 90.0f ) * 0.966f ) ),

					Local = new Vector2( px, py ),
					Uv = new Vector2( u, v ),
					Rect = rect,
					Radii = radii,
					Fill = new Color32( 40, 90, 200, 220 ),
					Border = new Color32( 255, 255, 255, 255 ),
					Misc = misc,

					//Half the width, so the clipping path is exercised rather than merely present.
					Clip = new Vector4( 0, 0, 160, 180 ),

					//Something other than the identity for every filter, so a mistake in the maths cannot hide behind a no-op.
					Filter = new Vector4( 1.2f, 0.6f, 1.1f, 0.15f ),
					Filter2 = new Vector2( 0.5f, 0.25f ),
				};

				target.Add( Corner( x, y, 0, 0 ) );
				target.Add( Corner( x + w, y, 1, 0 ) );
				target.Add( Corner( x, y + h, 0, 1 ) );
				target.Add( Corner( x, y + h, 0, 1 ) );
				target.Add( Corner( x + w, y, 1, 0 ) );
				target.Add( Corner( x + w, y + h, 1, 1 ) );
			}

			Quad( 10, 10, 120, 60, 8, 2 );
			Quad( 150, 30, 80, 40, 0, 0 );

			//A turned texture - what a gradient is - and a panel reading the frame behind it, each in a batch of its own because each wants a different picture.
			var turned = new System.Collections.Generic.List<CoreUiRenderer.UiVertex>();
			var behind = new System.Collections.Generic.List<CoreUiRenderer.UiVertex>();

			//A shadow outside its box and one inside it, both blurred - the two branches the shader takes before it ever looks at a texture.
			Quad( 40, 120, 60, 30, 6, 0, CoreUiRenderer.UiFill.Shadow, blur: 5.0f );
			Quad( 40, 120, 60, 30, 6, 0, CoreUiRenderer.UiFill.ShadowInset, blur: 3.0f );

			Quad( 10, 90, 120, 60, 4, 0, CoreUiRenderer.UiFill.Texture, target: turned );
			Quad( 230, 20, 70, 50, 0, 0, CoreUiRenderer.UiFill.Sized, target: turned );
			Quad( 180, 90, 120, 60, 10, 0, CoreUiRenderer.UiFill.Backdrop, blur: 6.0f, target: behind );

			batches.Add( new CoreUiRenderer.UiBatch( turned, white ) );

			var ortho = new System.Numerics.Matrix4x4
			{
				M11 = 2.0f / 320, M22 = 2.0f / 180, M33 = 1, M41 = -1, M42 = -1, M44 = 1,
			};

			var push = new byte[80];
			System.Runtime.InteropServices.MemoryMarshal.Write( push.AsSpan(), in ortho );
			System.Runtime.InteropServices.MemoryMarshal.Write( push.AsSpan()[64..],
				new System.Numerics.Vector4( 320, 180, 0, 0 ) );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;
			var captured = 0;

			for ( var i = 0; i < 4; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ) ) continue;

				foreach ( var batch in batches )
				{
					Assert.IsTrue( CoreRenderDevice.DrawDynamic( pipeline,
						System.Runtime.InteropServices.MemoryMarshal.AsBytes(
							System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices ) ),
						batch.Vertices.Count, 120, push, white ) );
				}

				//The copy is taken with boxes already drawn and the panel that reads it not yet - which is the only order that means anything.
				Assert.IsTrue( CoreRenderDevice.CaptureBackdrop(), "the frame should copy aside" );

				var backdrop = CoreRenderDevice.BackdropTexture();
				Assert.AreNotEqual( 0ul, backdrop, "and the copy should have a texture" );

				captured++;

				Assert.IsTrue( CoreRenderDevice.DrawDynamic( pipeline,
					System.Runtime.InteropServices.MemoryMarshal.AsBytes(
						System.Runtime.InteropServices.CollectionsMarshal.AsSpan( behind ) ),
					behind.Count, 120, push, backdrop ) );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented );
			Assert.AreEqual( 4, captured, "every frame should have got its copy" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DestroyTexture( white );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//Skinning, end to end: a two-bone model built, saved, loaded back with its skin and skeleton intact, and drawn through the skinning
	//pipeline with a posed skeleton - under validation, which is what checks that the bone buffer's offsets and ranges are legal.
	[TestMethod]
	public unsafe void DrawsASkinnedModel()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten skin test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var modelPath = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-skin-{System.Guid.NewGuid():N}.mdl_c" );

		//A cube whose lower half belongs to bone 0 and upper half to bone 1 - the smallest thing that bends.
		CoreRenderDemo.BuildCubeMesh( out var vertices, out var indices );

		var boneIndices = new byte[vertices.Length * 4];
		var boneWeights = new byte[vertices.Length * 4];

		for ( var i = 0; i < vertices.Length; i++ )
		{
			boneIndices[i * 4] = vertices[i].Position.z > 0 ? (byte)1 : (byte)0;
			boneWeights[i * 4] = 255;
		}

		var builder = new CoreAssets.ModelBuilder();

		fixed ( Vertex* v = vertices ) builder.AddVertices( v, vertices.Length );
		fixed ( uint* i = indices ) builder.AddIndices( i, indices.Length );

		fixed ( byte* b = boneIndices )
		fixed ( byte* w = boneWeights )
		{
			Assert.AreEqual( 0, builder.AddSkin( b, w, vertices.Length ) );
		}

		//Identity bind poses: the bones start where the mesh already is.
		var identity = stackalloc float[16];
		for ( var i = 0; i < 16; i++ ) identity[i] = ( i % 5 ) == 0 ? 1.0f : 0.0f;

		Assert.AreEqual( 0, builder.AddBone( "root", -1, identity ) );
		Assert.AreEqual( 1, builder.AddBone( "upper", 0, identity ) );

		builder.AddMesh( "cube", "", 0, indices.Length, 0, vertices.Length );

		var bounds = stackalloc float[6] { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };
		builder.SetRenderBounds( bounds );

		Assert.AreEqual( 1, builder.Save( modelPath ), "the skinned model should save" );
		builder.Dispose();

		//The format round trip, before any GPU is involved: what came back has to be what went in, or the drawing below would be testing nothing.
		var loaded = CoreAssets.ModelFile.Load( modelPath );
		Assert.IsNotNull( loaded, "the skinned model should load" );

		try
		{
			Assert.AreEqual( vertices.Length, loaded.SkinCount );
			Assert.AreEqual( 2, loaded.BoneCount );
			Assert.AreEqual( "root", loaded.BoneName( 0 ) );
			Assert.AreEqual( "upper", loaded.BoneName( 1 ) );
			Assert.AreEqual( -1, loaded.BoneParent( 0 ) );
			Assert.AreEqual( 0, loaded.BoneParent( 1 ) );

			var readBack = new byte[vertices.Length * 8];
			fixed ( byte* s = readBack ) loaded.CopySkin( s, vertices.Length );

			for ( var i = 0; i < vertices.Length; i++ )
			{
				Assert.AreEqual( boneIndices[i * 4], readBack[i * 8], $"vertex {i}'s bone" );
				Assert.AreEqual( 255, readBack[( i * 8 ) + 4], $"vertex {i}'s weight" );
			}
		}
		finally
		{
			loaded.Dispose();
		}

		var mesh = default( CoreSceneRenderer.SceneMesh );
		var pipeline = 0ul;
		var meshes = new System.Collections.Generic.Dictionary<string, CoreSceneRenderer.SceneMesh>();
		var textures = new System.Collections.Generic.Dictionary<string, ulong>();
		var fallback = 0ul;

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			pipeline = CoreSceneRenderer.MakeSkinnedPipeline();
			Assert.AreNotEqual( 0ul, pipeline, $"the skinning pipeline should bake: {CoreRenderDevice.Description}" );

			fallback = CoreRenderDemo.MakeCheckerTexture();
			mesh = CoreSceneRenderer.LoadMesh( modelPath, meshes, textures, fallback );

			Assert.IsNotNull( mesh );
			Assert.IsTrue( mesh.IsSkinned, "the loaded mesh should have brought its skin to the device" );
			Assert.AreEqual( 2, mesh.Bones.Length );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var frame = 0; frame < 4; frame++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ) ) continue;

				//The upper bone bends a little more each frame; the lower one stays put. Nothing
				//reads the pixels, but a pose that changes is one the driver cannot have folded away.
				var pose = new System.Numerics.Matrix4x4[2];
				pose[0] = System.Numerics.Matrix4x4.Identity;
				pose[1] = System.Numerics.Matrix4x4.CreateRotationX( frame * 0.2f );

				var world = System.Numerics.Matrix4x4.Identity;

				var view = System.Numerics.Matrix4x4.CreateLookAt(
					new System.Numerics.Vector3( 0, 0, 2.5f ),
					System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY );

				var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( 1.0f, 320.0f / 180.0f, 0.05f, 20.0f );
				projection.M22 = -projection.M22;

				//The camera and the lights are the frame's now, not the draw's.
				CoreRenderDevice.SetupFrame( CoreSceneRenderer.BuildFrame( null, view * projection, Vector3.Zero ) );

				var push = CoreSceneRenderer.ScenePush( world, Color.White );

				foreach ( var part in mesh.Meshes )
				{
					Assert.IsTrue( CoreRenderDevice.DrawSkinned( pipeline, mesh.Vertices, mesh.Skin, mesh.Indices,
						part.IndexCount, pose, push, part.FirstIndex, part.FirstVertex, part.Texture ),
						"the skinned draw should record" );
				}

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented, "every skinned frame should have reached the screen" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( pipeline );

			if ( mesh is not null )
			{
				CoreRenderDevice.DestroyBuffer( mesh.Vertices );
				CoreRenderDevice.DestroyBuffer( mesh.Indices );
				CoreRenderDevice.DestroyBuffer( mesh.Skin );
			}

			CoreRenderDevice.DestroyTexture( fallback );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
			System.IO.File.Delete( modelPath );
		}
	}

	//That the cube built in memory faces outward, which is the same mistake in the same shape as the one
	//the dev models had: a left-handed face frame, every triangle wound inward, the whole thing inside out.
	[TestMethod]
	public void GeneratedCubesFaceOutward()
	{
		void Check( string what, Vertex[] vertices, uint[] indices )
		{
			var outward = 0;
			var inward = 0;

			for ( var i = 0; i + 2 < indices.Length; i += 3 )
			{
				var a = vertices[indices[i + 0]].Position;
				var b = vertices[indices[i + 1]].Position;
				var c = vertices[indices[i + 2]].Position;

				//Both cubes are centred on the origin, so out of the middle is just out of the origin, and the winding either agrees or not.
				var face = Vector3.Cross( b - a, c - a ).Normal;
				var middle = ( ( a + b + c ) / 3.0f ).Normal;

				if ( Vector3.Dot( face, middle ) > 0.0f ) outward++;
				else inward++;
			}

			Assert.AreEqual( 12, outward, $"every triangle of the {what} should face out; {inward} face in" );
		}

		CoreRenderDemo.BuildCubeMesh( out var vertices, out var indices );
		Check( "demo cube", vertices, indices );

		var (boxVertices, boxIndices) = CoreDevModels.BoxMesh( 50.0f );
		Check( "dev box", boxVertices, boxIndices );
	}

	//Every model on disk is wound to match its vertex normals, judged model by model
	[TestMethod]
	public unsafe void ModelsOnDiskAreWoundTheRightWay()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		var root = System.Environment.GetEnvironmentVariable( EngineEnvironment.Folder );

		if ( string.IsNullOrWhiteSpace( root ) )
		{
			Assert.Inconclusive( "no engine directory to look in" );
			return;
		}

		var content = System.IO.Path.GetFullPath( System.IO.Path.Combine( root, "..", "game" ) );

		if ( !System.IO.Directory.Exists( content ) )
		{
			Assert.Inconclusive( $"no content at {content}" );
			return;
		}

		var files = System.IO.Directory.GetFiles( content, "*.mdl_c", System.IO.SearchOption.AllDirectories );

		if ( files.Length == 0 )
		{
			Assert.Inconclusive( "no models to look at" );
			return;
		}

		var looked = 0;
		var wrong = new System.Collections.Generic.List<string>();

		foreach ( var file in files )
		{
			var model = CoreAssets.ModelFile.Load( file );
			if ( model is null ) continue;

			try
			{
				var vertexCount = model.VertexCount;
				var indexCount = model.IndexCount;

				if ( vertexCount < 3 || indexCount < 3 ) continue;

				var vertices = new Vertex[vertexCount];
				var indices = new uint[indexCount];

				fixed ( Vertex* v = vertices ) model.CopyVertices( v, vertexCount );
				fixed ( uint* i = indices ) model.CopyIndices( i, indexCount );

				var against = 0;
				var counted = 0;

				for ( var i = 0; i + 2 < indexCount; i += 3 )
				{
					var a = vertices[indices[i + 0]];
					var b = vertices[indices[i + 1]];
					var c = vertices[indices[i + 2]];

					var geometric = Vector3.Cross( b.Position - a.Position, c.Position - a.Position );
					if ( geometric.Length < 0.0000001f ) continue;

					//The three normals together, so one bad vertex in a smooth mesh does not decide anything.
					var carried = a.Normal + b.Normal + c.Normal;
					if ( carried.Length < 0.0001f ) continue;

					counted++;
					if ( Vector3.Dot( geometric.Normal, carried.Normal ) < 0.0f ) against++;
				}

				if ( counted == 0 ) continue;

				looked++;

				//Half is the line: a model with a few bad triangles has bad triangles, and one with most of them backwards is inside out.
				if ( against > counted / 2 )
					wrong.Add( $"{System.IO.Path.GetFileName( file )} ({against} of {counted})" );
			}
			finally
			{
				model.Dispose();
			}
		}

		if ( looked == 0 )
		{
			Assert.Inconclusive( "the models here carry no normals to check against" );
			return;
		}

		Assert.AreEqual( 0, wrong.Count,
			$"these models are wound against their own normals and will draw inside out: {string.Join( ", ", wrong )}" );
	}

	//The whole way round: the world drawn into a picture of its own, the depth read back for occlusion, the reflections marched off it, and a
	//tonemapped composite onto the screen - under validation, which is what says the layouts and the attachment formats line up at every step.
	[TestMethod]
	public unsafe void DrawsThroughThePostPasses()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		using var window = CoreWindow.Create( "rinten post test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var pipelines = new System.Collections.Generic.List<ulong>();
		var materials = new System.Collections.Generic.List<ulong>();

		//Something to actually draw into the world's picture: the two-attachment path is the one worth proving, and an empty pass proves nothing.
		var modelPath = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-post-{System.Guid.NewGuid():N}.mdl_c" );

		CoreRenderDemo.BuildCubeMesh( out var vertices, out var indices );

		var builder = new CoreAssets.ModelBuilder();

		fixed ( Vertex* v = vertices ) builder.AddVertices( v, vertices.Length );
		fixed ( uint* i = indices ) builder.AddIndices( i, indices.Length );

		builder.AddMesh( "cube", "", 0, indices.Length, 0, vertices.Length );

		var bounds = stackalloc float[6] { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };
		builder.SetRenderBounds( bounds );

		Assert.AreEqual( 1, builder.Save( modelPath ) );
		builder.Dispose();

		var scenePipelines = new System.Collections.Generic.Dictionary<CoreSceneRenderer.PipelineKind, ulong>();
		var meshes = new System.Collections.Generic.Dictionary<string, CoreSceneRenderer.SceneMesh>();
		var textures = new System.Collections.Generic.Dictionary<string, ulong>();
		var fallback = 0ul;

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			fallback = CoreRenderDemo.MakeCheckerTexture();

			var mesh = CoreSceneRenderer.LoadMesh( modelPath, meshes, textures, fallback );
			Assert.IsNotNull( mesh );

			//The world's own pipeline: two attachments and half floats, which is not the same pipeline as the one that draws at the screen.
			var world = CoreSceneRenderer.PipelineFor(
				new CoreSceneRenderer.PipelineKind( false, false, false )
				{
					Target = CoreRender.TargetKind.Scene,
				},
				scenePipelines );

			var prepass = CoreSceneRenderer.PipelineFor(
				new CoreSceneRenderer.PipelineKind( false, false, false )
				{
					DepthOnly = true,
					Target = CoreRender.TargetKind.Depth,
				},
				scenePipelines );

			Assert.AreNotEqual( 0ul, world, $"the world's pipeline should bake: {CoreRenderDevice.Description}" );
			Assert.AreNotEqual( 0ul, prepass, $"and the depth pre-pass's: {CoreRenderDevice.Description}" );

			ulong Bake( string file, CoreRender.TargetKind target )
			{
				{
					var vs = CoreShaderCache.Pass( file, "MainVs", CoreShaderCompiler.Stage.Vertex );
					var ps = CoreShaderCache.Pass( file, "MainPs", CoreShaderCompiler.Stage.Pixel );

					Assert.IsNotNull( vs, $"a post pass should compile: {CoreRenderDevice.ShaderError}" );
					Assert.IsNotNull( ps, $"a post pass should compile: {CoreRenderDevice.ShaderError}" );

					var baked = CoreRenderDevice.CreatePipeline( vs, ps,
						CoreRender.VertexLayoutKind.None, depthTest: false,
						CoreRender.Topology.Triangles, blend: false, depthWrite: false, target: target );

					Assert.AreNotEqual( 0ul, baked, $"and bake: {CoreRenderDevice.Description}" );
					pipelines.Add( baked );

					return baked;
				}
			}

			var compose = Bake( EngineShaders.Compose, CoreRender.TargetKind.Screen );

			var colour = CoreRenderDevice.TargetTexture( swapchain, CoreRender.TargetKind.Scene );
			var normals = CoreRenderDevice.TargetTexture( swapchain, CoreRender.TargetKind.Scene, 1 );
			var depth = CoreRenderDevice.TargetTexture( swapchain, CoreRender.TargetKind.Scene, 2 );

			Assert.AreNotEqual( 0ul, colour, "the world should have a picture of its own" );
			Assert.AreNotEqual( 0ul, normals, "and somewhere to put its normals" );
			Assert.AreNotEqual( 0ul, depth, "and its depth should be readable" );

			var composeInputs = CoreRenderDevice.CreateMaterial( colour, colour, colour, colour, 1, 0, 0, 0 );

			materials.Add( composeInputs );

			var view = System.Numerics.Matrix4x4.CreateLookAt(
				new System.Numerics.Vector3( 0, 0, 3 ), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ );

			var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( 1.0f, 320.0f / 180.0f, 0.05f, 50.0f );
			projection.M22 = -projection.M22;

			var viewProjection = view * projection;
			System.Numerics.Matrix4x4.Invert( viewProjection, out var clipToWorld );

			var frame = CoreSceneRenderer.BuildFrame( null, viewProjection, Vector3.Zero );
			CoreSceneRenderer.SetOcclusion( frame, 320, 180 );

			var push = new byte[128];
			CorePostBlit.ComposePush( push, new Vector2( 320, 180 ), default, 0,
				0.05f, 50.0f, 0, 0, 0, 0 );

			var scenePush = CoreSceneRenderer.ScenePush( System.Numerics.Matrix4x4.Identity, Color.White );

			//The pre-pass draws from where the camera stands, the same block the world's draws carry.
			var depthPush = CoreSceneRenderer.ScenePush( System.Numerics.Matrix4x4.Identity, Color.White );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var i = 0; i < 4; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ) ) continue;

				CoreRenderDevice.SetupFrame( frame );

				//Shape first, with nothing in it - an empty depth buffer is still a depth buffer, and every layout it goes through is the same.
				Assert.IsTrue( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Depth ),
					"the depth pass should open" );

				foreach ( var part in mesh.Meshes )
				{
					Assert.IsTrue( CoreRenderDevice.DrawIndexed( prepass, mesh.Vertices, mesh.Indices,
						part.IndexCount, depthPush, part.FirstIndex, part.FirstVertex, part.Texture ) );
				}

				CoreRenderDevice.EndTarget();

				Assert.IsTrue( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene, Color.Black ),
					"the world should get a picture of its own" );

				foreach ( var part in mesh.Meshes )
				{
					Assert.IsTrue( CoreRenderDevice.DrawIndexed( world, mesh.Vertices, mesh.Indices,
						part.IndexCount, scenePush, part.FirstIndex, part.FirstVertex, part.Texture ) );
				}

				CoreRenderDevice.EndTarget();

				//And onto the screen, which opens itself at this draw.
				Assert.IsTrue( CoreRenderDevice.Draw( compose, 3, push, composeInputs ) );

				//Once the screen has begun, the passes before it are too late.
				Assert.IsFalse( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Post ),
					"a pass before the screen, after the screen, should be refused" );

				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 4, presented, "every frame should have reached the screen" );

			//A resize throws away every one of those pictures and makes new ones, which means new handles - and a material still naming
			//the old ones is a black screen at best. Whoever holds them has to notice, and what they notice by is the handle changing.
			CoreRenderDevice.Resize( swapchain, 400, 240 );
			CoreWindow.Pump();

			Assert.IsTrue( CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.1f, 0.13f ) ),
				"the frame after a resize should still begin" );

			var after = CoreRenderDevice.TargetTexture( swapchain, CoreRender.TargetKind.Scene );

			Assert.AreNotEqual( colour, after,
				"the world's picture should be a new one after a resize, by a handle that says so" );

			CoreRenderDevice.EndFrame();

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			foreach ( var material in materials ) CoreRenderDevice.DestroyTexture( material );
			foreach ( var baked in pipelines ) CoreRenderDevice.DestroyPipeline( baked );
			foreach ( var baked in scenePipelines.Values ) CoreRenderDevice.DestroyPipeline( baked );

			foreach ( var loaded in meshes.Values )
			{
				if ( loaded is null ) continue;

				CoreRenderDevice.DestroyBuffer( loaded.Vertices );
				CoreRenderDevice.DestroyBuffer( loaded.Indices );
			}

			foreach ( var texture in textures.Values ) CoreRenderDevice.DestroyTexture( texture );

			CoreRenderDevice.DestroyTexture( fallback );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
			System.IO.File.Delete( modelPath );
		}
	}

	//Two windows each with their own swapchain, sharing the device and pipelines
	[TestMethod]
	public void DrawsIntoTwoWindowsAtOnce()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var first = CoreWindow.Create( "rinten one", 320, 180, hidden: true );
		using var second = CoreWindow.Create( "rinten two", 240, 240, hidden: true );

		if ( first is null || second is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var one = 0ul;
		var two = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			one = CoreRenderDevice.AttachWindow( first );
			two = CoreRenderDevice.AttachWindow( second );

			if ( one == 0 || two == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			Assert.AreNotEqual( one, two, "two windows, two swapchains" );

			//Each is its own size, which is the whole point of them being two.
			var (firstWidth, firstHeight, firstImages) = CoreRenderDevice.SwapchainInfo( one );
			var (secondWidth, secondHeight, secondImages) = CoreRenderDevice.SwapchainInfo( two );

			Assert.AreEqual( 320, firstWidth );
			Assert.AreEqual( 180, firstHeight );
			Assert.AreEqual( 240, secondWidth );
			Assert.AreEqual( 240, secondHeight );
			Assert.IsTrue( firstImages >= 2 && secondImages >= 2 );

			//And its own pictures - the world, the normals, the depth. Sharing those would mean one window drawing over another's.
			Assert.AreNotEqual( 0ul, CoreRenderDevice.TargetTexture( one, CoreRender.TargetKind.Scene ) );
			Assert.AreNotEqual(
				CoreRenderDevice.TargetTexture( one, CoreRender.TargetKind.Scene ),
				CoreRenderDevice.TargetTexture( two, CoreRender.TargetKind.Scene ),
				"each window works on a picture of its own" );

			var errorsBefore = CoreErrors.Count;

			for ( var i = 0; i < 3; i++ )
			{
				CoreWindow.Pump();

				//Alternating, and each frame ends before the next begins - one frame at a time is the model, whichever window it is for.
				if ( CoreRenderDevice.BeginFrame( one, new Color( 0.2f, 0.1f, 0.1f ) ) )
				{
					Assert.IsFalse( CoreRenderDevice.BeginFrame( two, Color.Black ),
						"a second frame while one is open should be refused" );

					CoreRenderDevice.EndFrame();
				}

				if ( CoreRenderDevice.BeginFrame( two, new Color( 0.1f, 0.2f, 0.1f ) ) )
					CoreRenderDevice.EndFrame();
			}

			Assert.IsTrue( CoreRenderDevice.FrameCount( one ) > 0, "the first window should have drawn" );
			Assert.IsTrue( CoreRenderDevice.FrameCount( two ) > 0, "and so should the second" );

			//Letting one go leaves the other alone.
			CoreRenderDevice.DetachWindow( one );
			one = 0;

			CoreWindow.Pump();

			Assert.IsTrue( CoreRenderDevice.BeginFrame( two, Color.Black ),
				"the window that is left should still draw" );

			CoreRenderDevice.EndFrame();

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DetachWindow( one );
			CoreRenderDevice.DetachWindow( two );
			CoreRenderDevice.Shutdown();
		}
	}

	//A windowless view drawn into its own textures inside the window's frame, for previews
	[TestMethod]
	public void DrawsAViewInsideTheWindowsFrame()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten view test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;
		var view = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( 128, 128 );

			Assert.AreNotEqual( 0ul, view, "a view should not need a window to exist" );
			Assert.AreNotEqual( swapchain, view, "and it is not the window" );

			//Its own pictures, at its own size.
			var colour = CoreRenderDevice.TargetTexture( view, CoreRender.TargetKind.Scene );

			Assert.AreNotEqual( 0ul, colour, "a view draws into a picture of its own" );
			Assert.AreNotEqual( CoreRenderDevice.TargetTexture( swapchain, CoreRender.TargetKind.Scene ), colour );

			var errorsBefore = CoreErrors.Count;

			//Its own background, not the window's.
			var background = new Color( 40 / 255.0f, 80 / 255.0f, 120 / 255.0f );

			//Outside a frame there is nothing to draw into.
			Assert.IsFalse( CoreRenderDevice.BeginView( view, background ),
				"a view is drawn inside a frame, not instead of one" );

			for ( var i = 0; i < 3; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, Color.Black ) ) continue;

				Assert.IsTrue( CoreRenderDevice.BeginView( view, background ),
					"the view should open inside the frame" );

				Assert.IsFalse( CoreRenderDevice.BeginView( view, background ),
					"and only one view at a time" );

				//The world's pass, at the view's size rather than the window's.
				Assert.IsTrue( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene, Color.Black ) );
				CoreRenderDevice.EndTarget();

				//Nothing draws its screen, so what comes out is the colour it was opened with - which is exactly what makes it checkable.
				CoreRenderDevice.EndView();

				//And the window's own frame carries on afterwards.
				CoreRenderDevice.EndFrame();
			}

			Assert.IsTrue( CoreRenderDevice.FrameCount( swapchain ) > 0 );

			//What it drew is a texture from then on, and pixels for whoever wants them rather than a texture.
			Assert.AreNotEqual( 0ul, CoreRenderDevice.ViewTexture( view ),
				"a view should hand back what it drew" );

			var pixels = new byte[128 * 128 * 4];

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ),
				"and be readable as an image" );

			//It was cleared to a colour and nothing was drawn over it, so every pixel is that colour
			//- which is how we know these are its pixels and not somebody else's, or nothing at all.
			Assert.AreEqual( ScreenColour.Byte( 40 ), pixels[0], 2, "red" );
			Assert.AreEqual( ScreenColour.Byte( 80 ), pixels[1], 2, "green" );
			Assert.AreEqual( ScreenColour.Byte( 120 ), pixels[2], 2, "blue" );

			Assert.AreEqual( pixels[0], pixels[( pixels.Length / 2 ) + 0 ], "and the middle is the same" );
			Assert.AreEqual( pixels[1], pixels[( pixels.Length / 2 ) + 1 ] );

			//A buffer of the wrong size is refused rather than half filled.
			Assert.IsFalse( CoreRenderDevice.ReadView( view, new byte[16] ) );

			//Resizing keeps it a view, and gives it new pictures.
			Assert.IsTrue( CoreRenderDevice.ResizeView( view, 64, 64 ) );

			Assert.AreNotEqual( 0ul, CoreRenderDevice.TargetTexture( view, CoreRender.TargetKind.Scene ) );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//The sky: a triangle at the far plane, coloured by which way each pixel looks. What it draws is checked against where it
	//was looking, which is the part that can be silently wrong - an inverse matrix that is upside down still fills the screen.
	[TestMethod]
	public unsafe void DrawsTheSkyWhereNothingElseIs()
	{
		if ( !CoreNative.Available || !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		if ( !CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		var view = 0ul;
		var pipeline = 0ul;

		try
		{
			var stages = CoreShaderFile.Stages( CoreSceneRenderer.SkyShader );

			if ( stages is null )
			{
				Assert.Inconclusive( "the sky shader is not reachable from here" );
				return;
			}

			pipeline = CoreRenderDevice.CreatePipeline(
				stages[(int)CoreShaderFile.Stage.Vertex], stages[(int)CoreShaderFile.Stage.Pixel],
				CoreRender.VertexLayoutKind.None, depthTest: true, CoreRender.Topology.Triangles,
				blend: false, depthWrite: false );

			Assert.AreNotEqual( 0ul, pipeline, $"the sky should bake: {CoreRenderDevice.ShaderError}" );

			view = CoreRenderDevice.CreateView( 64, 64 );
			Assert.AreNotEqual( 0ul, view );

			//Looking along +X, level with the horizon and with up where up is, so the top of the picture is sky and the bottom is ground.
			var eye = new System.Numerics.Vector3( 0, 0, 0 );

			var look = System.Numerics.Matrix4x4.CreateLookAt( eye,
				new System.Numerics.Vector3( 1, 0, 0 ), new System.Numerics.Vector3( 0, 1, 0 ) );

			var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
				60.0f * ( System.MathF.PI / 180.0f ), 1.0f, 0.1f, 1000.0f );

			projection.M22 = -projection.M22;

			var frame = CoreSceneRenderer.BuildFrame( null, look * projection, Vector3.Zero );

			//Colours far enough apart that no two can be confused, and no sun - a disc in the middle would only make the reading harder.
			CoreSceneRenderer.SetSky( frame,
				new Color( 0.0f, 0.0f, 1.0f ), new Color( 0.0f, 1.0f, 0.0f ), new Color( 1.0f, 0.0f, 0.0f ),
				Vector3.Zero, Color.White );

			var errorsBefore = CoreErrors.Count;

			Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, Color.Black ) );

			fixed ( float* data = frame )
			{
				CoreRender.FrameSetup( data, frame.Length );
			}

			Assert.IsTrue( CoreRenderDevice.Draw( pipeline, 3, System.Array.Empty<byte>() ) );
			Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );

			var pixels = new byte[64 * 64 * 4];
			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			byte Channel( int x, int y, int channel ) => pixels[( ( ( y * 64 ) + x ) * 4 ) + channel];

			//The top row looks up: mostly zenith. The bottom looks down: ground. The middle is the horizon between them.
			Assert.IsTrue( Channel( 32, 1, 2 ) > 128, "the top of the sky is the zenith's blue" );
			Assert.IsTrue( Channel( 32, 1, 0 ) < 100, "and not the ground's red" );

			Assert.IsTrue( Channel( 32, 62, 0 ) > 128, "the bottom is the ground's red" );
			Assert.IsTrue( Channel( 32, 62, 2 ) < 100, "and not the zenith's blue" );

			Assert.IsTrue( Channel( 32, 32, 1 ) > 128, "and the middle is the horizon's green" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//A view drawn outside anybody's frame: a recording of its own, submitted and waited for. What
	//a thumbnail is - asked for between frames, by code that wants the pixels before it returns.
	[TestMethod]
	public void DrawsAViewWithNoFrameAroundIt()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten thumbnail test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;
		var view = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( 64, 64 );
			Assert.AreNotEqual( 0ul, view );

			var errorsBefore = CoreErrors.Count;
			var background = new Color( 200 / 255.0f, 30 / 255.0f, 90 / 255.0f );

			//No window's frame is open, and it does not need one.
			Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, background ),
				"a view should be drawable on its own" );

			//One frame at a time still holds, in both directions.
			Assert.IsFalse( CoreRenderDevice.BeginViewFrame( view, background ) );

			Assert.IsFalse( CoreRenderDevice.BeginFrame( swapchain, Color.Black ),
				"and the window may not begin one meanwhile" );

			//The passes work the same as they do inside a window: the world's pass, at the view's size.
			Assert.IsTrue( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene, Color.Black ) );
			CoreRenderDevice.EndTarget();

			Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );

			//Finished by the time that returned - so this reads pixels that were drawn, with nothing waited for and no frame in between.
			var pixels = new byte[64 * 64 * 4];

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			Assert.AreEqual( ScreenColour.Byte( 200 ), pixels[0], 2, "red" );
			Assert.AreEqual( ScreenColour.Byte( 30 ), pixels[1], 2, "green" );
			Assert.AreEqual( ScreenColour.Byte( 90 ), pixels[2], 2, "blue" );

			Assert.AreEqual( pixels[0], pixels[( pixels.Length / 2 ) + 0], "and the middle is the same" );

			//Ending one that is not open changes nothing.
			Assert.IsFalse( CoreRenderDevice.EndViewFrame( view ) );

			//Twice over, because a thumbnail renderer draws one after another and the second must not inherit anything from the first.
			Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, Color.Black ) );
			Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );
			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			Assert.AreEqual( 0, pixels[0], 2, "the second drawing is its own" );
			Assert.AreEqual( 0, pixels[1], 2 );

			//And the window's frames still work afterwards.
			CoreWindow.Pump();

			Assert.IsTrue( CoreRenderDevice.BeginFrame( swapchain, Color.Black ),
				"the window's own frames carry on" );

			CoreRenderDevice.EndFrame();

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//Resizing rebuilds the swapchain, and frames keep coming afterwards. The dangerous part of a resize is that the old images are
	//destroyed while the last frames drawn into them may still be in flight. Drawing before and after is what catches getting that wrong.
	[TestMethod]
	public void SurvivesAResize()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten resize test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;
		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			for ( var i = 0; i < 3; i++ )
			{
				if ( CoreRenderDevice.BeginFrame( swapchain, Color.Black ) ) CoreRenderDevice.EndFrame();
			}

			//The window first, then the swapchain. On most platforms the surface reports the window's size
			//and the swapchain has no say in the matter, so resizing only the swapchain would change nothing.
			Assert.IsTrue( window.SetSize( 640, 360 ) );
			CoreWindow.Pump();

			var (windowWidth, windowHeight) = window.SizeInPixels;

			Assert.IsTrue( CoreRenderDevice.Resize( swapchain, windowWidth, windowHeight ),
				"the swapchain should have been rebuilt" );

			var (width, height, _) = CoreRenderDevice.SwapchainInfo( swapchain );

			Assert.AreEqual( windowWidth, width );
			Assert.AreEqual( windowHeight, height );
			Assert.AreNotEqual( 320, width, "the window should actually have changed size" );

			var before = CoreRenderDevice.FrameCount( swapchain );

			for ( var i = 0; i < 3; i++ )
			{
				if ( CoreRenderDevice.BeginFrame( swapchain, Color.White ) ) CoreRenderDevice.EndFrame();
			}

			Assert.IsTrue( CoreRenderDevice.FrameCount( swapchain ) > before, "frames should still be reaching the screen" );
		}
		finally
		{
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//Repeated resizes with frames in flight; old swapchain resources must outlive their submissions
	[TestMethod]
	public void SurvivesResizingWhileFramesAreInFlight()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten resize storm", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			for ( var round = 0; round < 8; round++ )
			{
				//Two frames, so that something is still in flight when the rebuild below takes the images apart.
				for ( var frame = 0; frame < 2; frame++ )
				{
					if ( CoreRenderDevice.BeginFrame( swapchain, Color.Black ) &&
						CoreRenderDevice.EndFrame() )
					{
						presented++;
					}
				}

				var width = 320 + ( round * 40 );
				var height = 180 + ( round * 20 );

				Assert.IsTrue( window.SetSize( width, height ) );
				CoreWindow.Pump();

				var (windowWidth, windowHeight) = window.SizeInPixels;

				Assert.IsTrue( CoreRenderDevice.Resize( swapchain, windowWidth, windowHeight ),
					$"round {round}: the swapchain should have been rebuilt" );
			}

			for ( var frame = 0; frame < 4; frame++ )
			{
				if ( CoreRenderDevice.BeginFrame( swapchain, Color.White ) &&
					CoreRenderDevice.EndFrame() )
				{
					presented++;
				}
			}

			Assert.IsTrue( presented > 8, $"only {presented} frames reached the screen" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}
	//World at half resolution still presents cleanly under validation, including the depth blit
	[TestMethod]
	public void DrawsTheWorldSmallerThanTheWindow()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		using var window = CoreWindow.Create( "rinten render scale test", 320, 180, hidden: true );

		if ( window is null )
		{
			Assert.Inconclusive( $"no window: {CoreWindow.LastError}" );
			return;
		}

		var swapchain = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			swapchain = CoreRenderDevice.AttachWindow( window );

			if ( swapchain == 0 )
			{
				Assert.Inconclusive( $"no swapchain: {CoreRenderDevice.Description}" );
				return;
			}

			//Asking for the scale it already has is meant to be free and to succeed, which is what
			//lets a caller ask every time settings change without remembering what it asked last.
			Assert.IsTrue( CoreRenderDevice.SetRenderScale( swapchain, 1.0f ),
				"a scale of one is what it already is" );

			if ( !CoreRenderDevice.SetRenderScale( swapchain, 0.5f ) )
			{
				Assert.Inconclusive( $"this device will not scale: {CoreRenderDevice.Description}" );
				return;
			}

			//The window is unchanged by any of it: what shrank is behind it.
			var (width, height, _) = CoreRenderDevice.SwapchainInfo( swapchain );

			Assert.AreEqual( 320, width, "the window keeps its own size" );
			Assert.AreEqual( 180, height, "the window keeps its own size" );

			var errorsBefore = CoreErrors.Count;
			var presented = 0;

			//More than the two frames in flight, so the second time round reuses pictures a real submit has finished with.
			for ( var i = 0; i < 6; i++ )
			{
				CoreWindow.Pump();

				if ( !CoreRenderDevice.BeginFrame( swapchain, new Color( 0.1f, 0.2f, 0.3f ) ) ) continue;
				if ( CoreRenderDevice.EndFrame() ) presented++;
			}

			Assert.AreEqual( 6, presented, "every frame should have reached the screen at half scale" );

			//And back up again, which rebuilds everything a second time - the path where the screen's own depth buffer has to go away again.
			Assert.IsTrue( CoreRenderDevice.SetRenderScale( swapchain, 1.0f ),
				"the scale should go back up" );

			for ( var i = 0; i < 3; i++ )
			{
				CoreWindow.Pump();

				if ( CoreRenderDevice.BeginFrame( swapchain, Color.White ) ) CoreRenderDevice.EndFrame();
			}

			Assert.AreEqual( errorsBefore, CoreErrors.Count,
				"drawing the world smaller than the window should upset nothing validation can see" );
		}
		finally
		{
			CoreRenderDevice.DetachWindow( swapchain );
			CoreRenderDevice.Shutdown();
		}
	}

	//A just-made mesh draws next frame; the staged upload needs a visibility barrier. Read back
	[TestMethod]
	public unsafe void AMeshDrawsInTheFrameAfterItWasMade()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		var view = 0ul;
		var pipeline = 0ul;
		var vertices = 0ul;
		var indices = 0ul;
		var texture = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( 64, 64 );
			Assert.AreNotEqual( 0ul, view );

			pipeline = CoreSceneRenderer.MakeScenePipeline();
			Assert.AreNotEqual( 0ul, pipeline, $"standard shading should bake: {CoreRenderDevice.Description}" );

			texture = CoreRenderDemo.MakeCheckerTexture();

			var look = System.Numerics.Matrix4x4.CreateLookAt(
				new System.Numerics.Vector3( 0, 0, 2.5f ),
				System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY );

			var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( 1.0f, 1.0f, 0.05f, 20.0f );
			projection.M22 = -projection.M22;

			var frame = CoreSceneRenderer.BuildFrame( null, look * projection, Vector3.Zero );
			var push = CoreSceneRenderer.ScenePush( System.Numerics.Matrix4x4.Identity, Color.White );

			var background = new Color( 0.8f, 0.1f, 0.1f );
			var pixels = new byte[64 * 64 * 4];
			var errorsBefore = CoreErrors.Count;

			//Twice: the first time through the upload ring is a slot nobody has used, the second reuses
			//one, and a mesh that only draws on a fresh slot is a mesh whose copy is racing the frame.
			for ( var round = 0; round < 2; round++ )
			{
				CoreRenderDemo.BuildCubeMesh( out var mesh, out var indexList );

				vertices = CoreRenderDevice.CreateBuffer(
					System.Runtime.InteropServices.MemoryMarshal.AsBytes( mesh.AsSpan() ) );
				indices = CoreRenderDevice.CreateBuffer(
					System.Runtime.InteropServices.MemoryMarshal.AsBytes( indexList.AsSpan() ) );

				Assert.AreNotEqual( 0ul, vertices, $"the vertices should upload: {CoreRenderDevice.Description}" );
				Assert.AreNotEqual( 0ul, indices, $"the indices should upload: {CoreRenderDevice.Description}" );

				Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, background ),
					$"the view would not begin: {CoreRenderDevice.Description}" );

				CoreRenderDevice.SetupFrame( frame );

				Assert.IsTrue( CoreRenderDevice.DrawIndexed( pipeline, vertices, indices,
					indexList.Length, push, texture: texture ), "the cube should record" );

				Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );
				Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

				//The cube fills the middle of a 64x64 view from two and a half metres away; the corner is whatever the view was cleared to.
				var middle = ( ( 32 * 64 ) + 32 ) * 4;

				Assert.AreEqual( ScreenColour.Byte( 0.8f ), pixels[0], 2,
					"the corner is the background" );

				Assert.AreNotEqual( pixels[0], pixels[middle],
					$"round {round}: nothing was drawn where the mesh should be - its bytes never arrived" );

				CoreRenderDevice.DestroyBuffer( vertices );
				CoreRenderDevice.DestroyBuffer( indices );

				vertices = 0;
				indices = 0;
			}

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyBuffer( vertices );
			CoreRenderDevice.DestroyBuffer( indices );
			CoreRenderDevice.DestroyTexture( texture );
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//With no occlusion drawn, shading reads none and two rounds render identically
	[TestMethod]
	public unsafe void ShadingIsUnoccludedWhenNobodyDrewOcclusion()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( "rintencore is not loaded" );
			return;
		}

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler" );
			return;
		}

		var view = 0ul;
		var pipeline = 0ul;
		var vertices = 0ul;
		var indices = 0ul;
		var texture = 0ul;

		try
		{
			if ( !CoreRenderDevice.Initialize( validation: true ) )
			{
				Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( 64, 64 );
			Assert.AreNotEqual( 0ul, view );

			pipeline = CoreSceneRenderer.MakeScenePipeline();
			Assert.AreNotEqual( 0ul, pipeline, $"standard shading should bake: {CoreRenderDevice.Description}" );

			CoreRenderDemo.BuildCubeMesh( out var mesh, out var indexList );

			vertices = CoreRenderDevice.CreateBuffer(
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( mesh.AsSpan() ) );
			indices = CoreRenderDevice.CreateBuffer(
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( indexList.AsSpan() ) );

			texture = CoreRenderDemo.MakeCheckerTexture();

			var look = System.Numerics.Matrix4x4.CreateLookAt(
				new System.Numerics.Vector3( 0, 0, 2.5f ),
				System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY );

			var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( 1.0f, 1.0f, 0.05f, 20.0f );
			projection.M22 = -projection.M22;

			var frame = CoreSceneRenderer.BuildFrame( null, look * projection, Vector3.Zero );

			//No picture of it drawn, so the block says there is none.
			CoreSceneRenderer.SetOcclusion( frame, 64, 64 );

			var push = CoreSceneRenderer.ScenePush( System.Numerics.Matrix4x4.Identity, Color.White );
			var errorsBefore = CoreErrors.Count;

			var first = new byte[64 * 64 * 4];
			var second = new byte[64 * 64 * 4];

			for ( var round = 0; round < 2; round++ )
			{
				Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, new Color( 0.1f, 0.1f, 0.13f ) ),
					$"the view would not begin: {CoreRenderDevice.Description}" );

				CoreRenderDevice.SetupFrame( frame );

				Assert.IsTrue( CoreRenderDevice.DrawIndexed( pipeline, vertices, indices,
					indexList.Length, push, texture: texture ) );

				Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );
				Assert.IsTrue( CoreRenderDevice.ReadView( view, round == 0 ? first : second ) );
			}

			CollectionAssert.AreEqual( first, second,
				"the second frame shaded against something the first did not" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			CoreRenderDevice.DestroyBuffer( vertices );
			CoreRenderDevice.DestroyBuffer( indices );
			CoreRenderDevice.DestroyTexture( texture );
			CoreRenderDevice.DestroyPipeline( pipeline );
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}
	//A small uncompressed TGA - written by hand because it takes a dozen lines and no encoder - for the texture compiler to eat.
	private static string WriteTestImage( int size )
	{
		var header = new byte[18];
		header[2] = 2;
		header[12] = (byte)( size & 0xFF );
		header[13] = (byte)( ( size >> 8 ) & 0xFF );
		header[14] = (byte)( size & 0xFF );
		header[15] = (byte)( ( size >> 8 ) & 0xFF );
		header[16] = 32;
		header[17] = 8 | 0x20;

		var pixels = new byte[size * size * 4];

		for ( var y = 0; y < size; ++y )
		{
			for ( var x = 0; x < size; ++x )
			{
				var at = ( y * size + x ) * 4;

				//TGA is BGRA. A gradient, so any mip is distinguishable from any solid answer.
				pixels[at + 0] = (byte)( x * 255 / size );
				pixels[at + 1] = (byte)( y * 255 / size );
				pixels[at + 2] = 200;
				pixels[at + 3] = 255;
			}
		}

		var path = System.IO.Path.Combine( System.IO.Path.GetTempPath(),
			$"rinten-render-image-{System.Guid.NewGuid():N}.tga" );

		using var file = System.IO.File.Create( path );
		file.Write( header );
		file.Write( pixels );

		return path;
	}

}
