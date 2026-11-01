using NativeEngine;
using System;
using System.IO;

namespace EngineTests.Core;

//Unfilled material slots get neutral defaults (white, flat normal, etc.), unlike broken materials
[TestClass]
[DoNotParallelize]
public class MaterialDefaultsTests
{
	[TestInitialize]
	public void OnMainThread() => ThreadSafe.MarkMainThread();

	private const int Size = 64;

	//The missing texture is the one games have used for thirty years.
	[TestMethod]
	public void TheMissingTextureIsMagentaAndBlack()
	{
		var pixels = CoreResources.MissingPixels( CoreResources.MissingSize, CoreResources.MissingCell );

		Assert.AreEqual( CoreResources.MissingSize * CoreResources.MissingSize * 4, pixels.Length );

		var magenta = 0;
		var black = 0;

		for ( var i = 0; i < pixels.Length; i += 4 )
		{
			var r = pixels[i];
			var g = pixels[i + 1];
			var b = pixels[i + 2];

			Assert.AreEqual( 0, g, $"green has no business in it - pixel {i / 4}" );
			Assert.AreEqual( 255, pixels[i + 3], $"and it is opaque - pixel {i / 4}" );

			if ( r == 255 && b == 255 ) magenta++;
			else if ( r == 0 && b == 0 ) black++;
			else Assert.Fail( $"pixel {i / 4} is neither magenta nor black: {r},{g},{b}" );
		}

		//Squares, not a wash: the two faults it stands for - no picture at all, and a picture whose UVs are wrong - look different only because of them.
		Assert.AreEqual( magenta, black, "the two colours should cover the same area" );

		var corner = 0;
		var acrossOneCell = CoreResources.MissingCell * 4;

		Assert.AreNotEqual( pixels[corner], pixels[corner + acrossOneCell],
			"a square's width along, the colour should have changed" );
	}

	//A material with no colour map draws white, not the missing-texture checkerboard
	[TestMethod]
	public void AMaterialWithNoColourMapIsWhiteRatherThanTheCheckerboard()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		var view = 0ul;
		var wasAntialias = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			Rinten.Rendering.DrawSettings.Msaa = 0;

			var scene = Made( out var box, out var camera, out var eye );
			if ( scene is null ) return;

			var plain = Material.Create( "test_no_colour.mat", "shaders/standard.shader" );
			plain.Set( "Roughness", 1.0f );
			plain.Set( "Metalness", 0.0f );

			box.Components.Get<ModelRenderer>().MaterialOverride = plain;

			if ( !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( $"the drawer has no pipelines here: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer { Watched = false };
			var seen = Middle( drawer, scene, camera, view );

			Console.WriteLine( $"the box came out {seen}" );

			//Magenta is red and blue with nothing between them. A grey surface is all three together, whatever the light does to the brightness.
			Assert.IsTrue( seen.r > 8 || seen.g > 8 || seen.b > 8,
				$"the box should have been drawn at all: {seen}" );

			var lowest = Math.Min( seen.g, Math.Min( seen.r, seen.b ) );
			var highest = Math.Max( seen.g, Math.Max( seen.r, seen.b ) );

			Assert.IsTrue( highest - lowest < highest / 2,
				$"a material with no colour map should be white, not magenta: {seen}" );

			Assert.IsTrue( seen.g * 2 > seen.r + seen.b - 20,
				$"and the green channel is what magenta has none of: {seen}" );
		}
		finally
		{
			Rinten.Rendering.DrawSettings.Msaa = wasAntialias;
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//The engine's plain material resolves, while a missing material file still does not
	[TestMethod]
	public void ThePlainMaterialResolvesAndAMissingOneStillDoesNot()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreRenderDevice.Initialize( validation: false ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		try
		{
			var materials = new System.Collections.Generic.Dictionary<string, ulong>();
			var textures = new System.Collections.Generic.Dictionary<string, ulong>();
			var fallback = CoreResources.Fallback;

			var plain = CoreSceneRenderer.ResolveMaterial(
				ModelDefinition.PlainMaterial, materials, textures, fallback );

			Console.WriteLine( $"fallback = {fallback}, plain = {plain}" );

			Assert.AreNotEqual( 0ul, plain, $"{ModelDefinition.PlainMaterial} should resolve at all" );

			Assert.AreNotEqual( fallback, plain,
				$"{ModelDefinition.PlainMaterial} names no maps, which is not the same as being broken" );

			//And a name nothing answers to is still the checkerboard, which is what stops the line above from being "everything is fine now".
			var missing = CoreSceneRenderer.ResolveMaterial(
				"materials/there_is_no_such_material.mat", materials, textures, fallback );

			Console.WriteLine( $"missing = {missing}" );

			Assert.AreEqual( fallback, missing,
				"a material the project does not have is still missing" );
		}
		finally
		{
			CoreRenderDevice.Shutdown();
		}
	}

	//A tint alpha below one blends the model with what's behind it
	[TestMethod]
	public void ATintAlphaBelowOneFadesTheModelTowardsWhatIsBehindIt()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreRenderDevice.Initialize( validation: true ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		var view = 0ul;
		var wasAntialias = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			Rinten.Rendering.DrawSettings.Msaa = 0;

			var scene = Made( out var box, out var camera, out var eye );
			if ( scene is null ) return;

			//A background nothing in the scene is, so what shows through is unmistakably the background and not the box in another light.
			camera.BackgroundColor = new Color( 0.0f, 0.0f, 1.0f );

			var renderer = box.Components.Get<ModelRenderer>();

			if ( !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( $"the drawer has no pipelines here: {CoreRenderDevice.Description}" );
				return;
			}

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer { Watched = false };

			renderer.Tint = Color.White;
			var solid = Middle( drawer, scene, camera, view );

			renderer.Tint = new Color( 1.0f, 1.0f, 1.0f, 0.5f );
			var faded = Middle( drawer, scene, camera, view );

			renderer.Enabled = false;
			var behind = Middle( drawer, scene, camera, view );

			Console.WriteLine( $"solid {solid}, faded {faded}, behind {behind}" );

			Assert.IsTrue( behind.b > behind.r + 40,
				$"the background should be the blue it was asked for: {behind}" );

			Assert.IsTrue( Math.Abs( faded.b - solid.b ) > 12,
				$"half a tint should not draw the same picture as none: solid {solid}, faded {faded}" );

			//Between the two, which is the whole claim: not the box, not the background, and nearer the middle than either end.
			var low = Math.Min( solid.b, behind.b );
			var high = Math.Max( solid.b, behind.b );

			Assert.IsTrue( faded.b > low + 8 && faded.b < high - 8,
				$"the faded box should sit between the two: solid {solid}, faded {faded}, behind {behind}" );

			//And half way, not a quarter of the way. The shader multiplied the tint into the colour and
			//then multiplied its alpha in again on the way out, so a tint of a half came out as a quarter.
			var middle = ( low + high ) * 0.5f;
			var quarter = low + ( ( high - low ) * 0.25f );

			Assert.IsTrue( Math.Abs( faded.b - middle ) < Math.Abs( faded.b - quarter ),
				$"half should blend half, not a quarter: faded {faded.b}, half {middle}, quarter {quarter}" );
		}
		finally
		{
			Rinten.Rendering.DrawSettings.Msaa = wasAntialias;
			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//A feature's bit in the standard surface combo; in-memory materials pick features from their maps
	static int At( string feature )
	{
		var features = MaterialDefinition.FeaturesFor( "shaders/standard.shader" );

		for ( var i = 0; i < features.Count; i++ )
		{
			if ( features[i].Name == feature ) return i;
		}

		return -1;
	}

	[TestMethod]
	public void AMaterialMadeInCodeAsksForTheBuildThatSamplesItsMaps()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "rintencore is not loaded" ); return; }

		if ( !CoreRenderDevice.Initialize( validation: false ) )
		{
			Assert.Inconclusive( $"no Vulkan device here: {CoreRenderDevice.Description}" );
			return;
		}

		try
		{
			var bare = Material.Create( "test_bare.mat", "shaders/standard.shader" );
			CoreResources.Live( bare );

			Assert.IsNull( CoreResources.LiveCombo( bare ),
				"a material with no maps wants the shipped build, which is what null means" );

			var mapped = Material.Create( "test_mapped.mat", "shaders/standard.shader" );
			mapped.Set( "Normal", Flat( 128, 128, 255 ) );
			CoreResources.Live( mapped );

			var combo = CoreResources.LiveCombo( mapped );

			Console.WriteLine( $"bare = null, mapped = {combo ?? "null"}" );

			Assert.IsNotNull( combo,
				"a material with a normal map wants a build that samples one" );

			//The combo is the features' values in the order the shader declared them, with dots between - so
			//the one to read is wherever the normal map sits in that order, looked up rather than counted.
			Assert.AreEqual( "1", combo.Split( '.' )[At( "F_NORMAL_MAP" )],
				$"and F_NORMAL_MAP is on in it: {combo}" );

			//What the material says itself wins over what its maps imply, the same way it does for one on disk.
			var told = Material.Create( "test_told.mat", "shaders/standard.shader" );
			told.SetFeature( "F_ALPHA", 1 );
			CoreResources.Live( told );

			var asked = CoreResources.LiveCombo( told );

			Console.WriteLine( $"told = {asked ?? "null"}" );

			Assert.IsNotNull( asked, "a feature set by hand is still a feature" );

			//By where it sits rather than by being at the end: the surface grows features, and an assertion
			//that reads the last one moves onto whatever was added most recently and stops testing this.
			Assert.AreEqual( "1", asked.Split( '.' )[At( "F_ALPHA" )],
				$"and F_ALPHA is on in it: {asked}" );
		}
		finally
		{
			CoreRenderDevice.Shutdown();
		}
	}

	//A compiled shader older than any .slang it includes is ignored and rebuilt
	[TestMethod]
	public void ABlobOlderThanWhatItIncludesIsIgnored()
	{
		var room = Path.Combine( Path.GetTempPath(), "rinten-stale-" + Guid.NewGuid().ToString( "N" ) );

		Directory.CreateDirectory( room );

		try
		{
			var included = Path.Combine( room, "surface.slang" );
			var source = Path.Combine( room, "thing.shader" );
			var blob = Path.Combine( room, "thing.shader_c" );

			File.WriteAllText( included, "// the surface\n" );
			File.WriteAllText( source, "FEATURES { }\n#include \"surface.slang\"\n" );
			File.WriteAllText( blob, "compiled" );

			var then = new DateTime( 2020, 1, 1, 0, 0, 0, DateTimeKind.Utc );

			File.SetLastWriteTimeUtc( included, then );
			File.SetLastWriteTimeUtc( source, then );
			File.SetLastWriteTimeUtc( blob, then.AddHours( 1 ) );

			Assert.IsFalse( CoreShaderFile.StaleAgainst( blob, source ),
				"a blob written after everything it came from is current" );

			//The .shader untouched, which is the whole point: only the included file moved, and that is the file somebody actually edits.
			File.SetLastWriteTimeUtc( included, then.AddHours( 2 ) );

			Assert.IsTrue( CoreShaderFile.StaleAgainst( blob, source ),
				"an include newer than the blob makes the blob stale" );

			File.SetLastWriteTimeUtc( included, then );
			File.SetLastWriteTimeUtc( source, then.AddHours( 2 ) );

			Assert.IsTrue( CoreShaderFile.StaleAgainst( blob, source ),
				"and so does the source itself" );

			//Nothing to compare against is not evidence of staleness: a shipped game has blobs and no sources.
			Assert.IsFalse( CoreShaderFile.StaleAgainst( blob, null ),
				"with no source to compare, the blob stands" );
		}
		finally
		{
			try { Directory.Delete( room, true ); } catch ( IOException ) { }
		}
	}

	//A one-pixel texture of one colour, for filling a slot by hand.
	private static Texture Flat( byte r, byte g, byte b )
	{
		var made = CoreTexture.Create( new CTextureDesc
		{
			m_nWidth = 1,
			m_nHeight = 1,
			m_nDepth = 1,
			m_nNumMipLevels = 1,
			m_nImageFormat = ImageFormat.RGBA8888,
		}, "flat" );

		made.Write( new byte[] { r, g, b, 255 } );

		return new Texture( made );
	}

	//A box in front of a camera, lit, or null where the box will not load.
	private static Scene Made( out GameObject box, out CameraComponent camera, out GameObject eye )
	{
		box = null;
		camera = null;
		eye = null;

		var model = Model.FromCore( CoreModel.Load( "models/dev/box.vmdl" ) );

		if ( model is null )
		{
			Assert.Inconclusive( "the built-in box did not load" );
			return null;
		}

		var scene = Scene.CreateEditorScene();
		scene.Push();

		var sun = scene.CreateObject().Components.Create<DirectionalLight>();
		sun.Shadows = false;
		sun.WorldRotation = Rotation.LookAt( new Vector3( 1, 1, -1 ).Normal );

		box = scene.CreateObject();
		box.WorldPosition = Vector3.Zero;
		box.Components.Create<ModelRenderer>().Model = model;

		eye = scene.CreateObject();
		eye.WorldPosition = new Vector3( -3.0f, 0, 0 );
		eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

		camera = eye.Components.Create<CameraComponent>();
		camera.BackgroundColor = Color.Black;
		camera.ZNear = 0.1f;
		camera.ZFar = 200.0f;
		camera.FieldOfView = 70.0f;

		return scene;
	}

	//The middle pixel of a settled frame.
	private static (byte r, byte g, byte b) Middle( CoreSceneDrawer drawer, Scene scene,
		CameraComponent camera, ulong view )
	{
		var pixels = new byte[Size * Size * 4];

		//Twice: a drawer keeps what it worked out about the last frame.
		for ( var frame = 0; frame < 2; frame++ )
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

		var at = ( ( ( Size / 2 ) * Size ) + ( Size / 2 ) ) * 4;

		return (pixels[at], pixels[at + 1], pixels[at + 2]);
	}
}
