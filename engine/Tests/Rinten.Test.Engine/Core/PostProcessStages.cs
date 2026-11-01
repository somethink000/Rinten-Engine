using Rinten.Rendering;
using System;
using System.IO;

namespace EngineTests.Core;

//Project passes run at every frame stage and stack, alternating between two colour targets
[TestClass]
[DoNotParallelize]
public class PostProcessStagesTests
{
	[TestInitialize]
	public void OnMainThread() => ThreadSafe.MarkMainThread();

	private const int Size = 64;

	//Where the test shader is written, as a resource path and on disk. Beside the engine's own, because a shader is found by
	//resource path and that is a path the engine looks in - see CoreModel.ResolveOnDisk. Removed afterwards, whatever happens.
	private const string ShaderResource = "shaders/postprocess/rinten_test_add.shader";

	private const string ShaderSource = """
		// Adds its blend to the red of whatever has been drawn so far.
		//
		// Written by a test and removed by it. What it is for is that the amount
		// it adds is visible and cumulative: one of these makes the picture
		// redder, two make it redder still, and the second can only see the
		// first if the chain really handed it what the first wrote.

		FEATURES
		{
		}

		PARAMETERS
		{
			float g_flRedAdd < Default( 0.0 ); Range( 0, 1 ); UiGroup( "Test" ); >;
		}

		#include "postprocess.slang"

		[shader("fragment")]
		float4 MainPs( PostV2F i ) : SV_Target0
		{
			return float4( SceneColour( i.uv ) + float3( g_flRedAdd, 0.0, 0.0 ), 1.0 );
		}
		""";

	/// <summary>An effect that hangs however many passes it is told to.</summary>
	private sealed class AddsRed : BasePostProcess<AddsRed>
	{
		public static int Passes;
		public static float Amount = 0.25f;
		public static Stage At = Stage.BeforePostProcess;
		public static int Drawn;

		public override void Render()
		{
			var material = Material.FromShader( ShaderResource );

			if ( material is null ) return;

			for ( var i = 0; i < Passes; i++ )
			{
				//Under the name the shader declared it by, without the prefix - which is the only way an effect's own properties reach its shader. Nothing joined
				//the two before, so every post-process in the engine drew with whatever its shader declared as a default and ignored what the component was set to.
				var attributes = new RenderAttributes();
				attributes.Set( "RedAdd", Amount );

				Blit( new BlitMode { Material = material, Attributes = attributes, RenderStage = At, Order = i },
					$"AddsRed{i}" );

				Drawn++;
			}
		}
	}

	[TestMethod]
	public void EveryPassAProjectHangsOnTheFrameHappensAndTheyStack()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler on this host" );
			return;
		}

		var written = Where();

		if ( written is null )
		{
			Assert.Inconclusive( "the engine's own shader folder is not where it should be" );
			return;
		}

		if ( !CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		var view = 0ul;
		var wasAntialias = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			File.WriteAllText( written, ShaderSource );

			Rinten.Rendering.DrawSettings.Msaa = 0;

			var model = Model.FromCore( CoreModel.Load( "models/dev/box.vmdl" ) );

			if ( model is null )
			{
				Assert.Inconclusive( "the built-in box did not load" );
				return;
			}

			var scene = Scene.CreateEditorScene();
			using var scope = scene.Push();

			var sun = scene.CreateObject().Components.Create<DirectionalLight>();
			sun.Shadows = false;
			sun.WorldRotation = Rotation.LookAt( new Vector3( 1, 1, -1 ).Normal );

			var box = scene.CreateObject();
			box.WorldPosition = Vector3.Zero;
			box.Components.Create<ModelRenderer>().Model = model;

			var eye = scene.CreateObject();
			eye.WorldPosition = new Vector3( -3.0f, 0, 0 );
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

			var camera = eye.Components.Create<CameraComponent>();
			camera.BackgroundColor = Color.Black;
			camera.ZNear = 0.1f;
			camera.ZFar = 200.0f;
			camera.FieldOfView = 70.0f;

			eye.Components.Create<AddsRed>();

			if ( !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( $"the drawer has no pipelines here: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer { Watched = false };
			var errorsBefore = CoreErrors.Count;

			AddsRed.Passes = 0;
			var none = Middle( drawer, scene, camera, view );

			Assert.IsTrue( none.r > 2 || none.g > 2 || none.b > 2,
				$"the world should have been drawn at all: {none}" );

			//Stages where colour exists to change; AfterDepthPrepass is excluded as the world pass clears it
			var stages = new[]
			{
				Stage.AfterOpaque, Stage.AfterSkybox, Stage.AfterTransparent,
				Stage.AfterViewmodel, Stage.BeforePostProcess, Stage.Tonemapping,
				Stage.AfterPostProcess, Stage.AfterUI,
			};

			var missed = new System.Collections.Generic.List<string>();

			foreach ( var stage in stages )
			{
				AddsRed.At = stage;

				AddsRed.Passes = 1;
				var one = Middle( drawer, scene, camera, view );

				AddsRed.Passes = 2;
				var two = Middle( drawer, scene, camera, view );

				Console.WriteLine( $"{stage}: none {none}, one {one}, two {two}" );

				if ( one.r <= none.r + 2 )
				{
					missed.Add( $"{stage} never reached the picture ({none} -> {one})" );
					continue;
				}

				//A second pass must stack on the first, except at the end-of-frame stages which only overlay
				var alternates = stage is not ( Stage.AfterPostProcess or Stage.AfterUI );

				if ( alternates && two.r <= one.r + 1 )
					missed.Add( $"{stage} does not stack ({one} -> {two})" );

				if ( !alternates && two.r <= none.r + 2 )
					missed.Add( $"{stage} stopped working with two on it ({none} -> {two})" );
			}

			AddsRed.Passes = 0;

			Assert.IsTrue( AddsRed.Drawn > 0,
				"the effect was never asked to build - the system did not see it" );

			Assert.AreEqual( 0, missed.Count, string.Join( "\n", missed ) );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			AddsRed.Passes = 0;
			AddsRed.Drawn = 0;

			Rinten.Rendering.DrawSettings.Msaa = wasAntialias;

			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();

			try { if ( written is not null ) File.Delete( written ); } catch ( IOException ) { }
		}
	}

	//The engine's own post-process shaders compile
	[TestMethod]
	public void TheEnginesOwnEffectsHaveShadersThatCompile()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler on this host" );
			return;
		}

		string[] ours =
		{
			"bloom.shader", "copy.shader", "tonemapping.shader",
			"vignette.shader", "filmgrain.shader", "coloradjust.shader", "pixelate.shader",
			"chromatic.shader", "sharpen.shader", "colorgrading.shader",
			"ambient_occlusion.shader",
			"reflection.shader", "depth_of_field.shader",
		};

		var game = Environment.GetEnvironmentVariable( EngineEnvironment.Folder );

		if ( string.IsNullOrWhiteSpace( game ) )
		{
			Assert.Inconclusive( "the engine folder is not named here" );
			return;
		}

		var folder = Path.Combine( game, "addons", "base", "Assets", "shaders", "postprocess" );
		var broken = new System.Collections.Generic.List<string>();

		foreach ( var name in ours )
		{
			var full = Path.Combine( folder, name );

			if ( !File.Exists( full ) ) { broken.Add( $"{name} is not there" ); continue; }

			if ( CoreShaderFile.Compile( full ) is null )
				broken.Add( $"{name}: {CoreRenderDevice.ShaderError}" );
		}

		Assert.AreEqual( 0, broken.Count, string.Join( Environment.NewLine, broken ) );
	}

	//Where a resource path under the engine's own assets lands on disk.
	private static string Where()
	{
		var game = Environment.GetEnvironmentVariable( EngineEnvironment.Folder );

		if ( string.IsNullOrWhiteSpace( game ) ) return null;

		var folder = Path.Combine( game, "addons", "base", "Assets", "shaders", "postprocess" );

		return Directory.Exists( folder )
			? Path.Combine( folder, "rinten_test_add.shader" )
			: null;
	}

	//The middle pixel of a settled frame.
	private static (byte r, byte g, byte b) Middle( CoreSceneDrawer drawer, Scene scene,
		CameraComponent camera, ulong view )
	{
		var pixels = new byte[Size * Size * 4];

		//Extra frames: effects build at tick end, and cold pipelines draw nothing at first
		for ( var frame = 0; frame < 12; frame++ )
		{
			scene.EditorTick( frame * 0.016f, 0.016f );
			scene.PreRender();

			Assert.IsTrue( CoreRenderDevice.BeginViewFrame( view, camera.BackgroundColor ),
				$"the view would not begin: {CoreRenderDevice.Description}" );

			try
			{
				drawer.Draw( scene, camera, view, Size, Size );
			}
			finally
			{
				Assert.IsTrue( CoreRenderDevice.EndViewFrame( view ) );
			}

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );
		}

		//The whole picture rather than one pixel of it: a full-screen pass changes every
		//pixel, and where the box happens to land says nothing about whether the pass ran.
		long red = 0, green = 0, blue = 0;

		for ( var i = 0; i < pixels.Length; i += 4 )
		{
			red += pixels[i];
			green += pixels[i + 1];
			blue += pixels[i + 2];
		}

		var count = pixels.Length / 4;

		return ( (byte)( red / count ), (byte)( green / count ), (byte)( blue / count ) );
	}
}
