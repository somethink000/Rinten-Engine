using System;

namespace EngineTests.Core;

//A camera that moved leaves a smear; a camera that did not does not. Both halves again. The blur is driven by where each pixel was on the screen last
//frame, so a still camera must produce no smear at all - and a pass that blurs a still picture is not motion blur, it is a blur that happens to be on.
[TestClass]
[DoNotParallelize]
public class MotionBlurTests
{
	private const int Size = 192;

	[TestInitialize]
	public void OnMainThread() => ThreadSafe.MarkMainThread();

	[TestMethod]
	public void ItSmearsWhenTheCameraTurnsAndNotWhenItDoesNot()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "no core" ); return; }
		if ( !CoreRenderDevice.Initialize( validation: true ) ) { Assert.Inconclusive( "no device" ); return; }

		var view = 0ul;
		var wasQuality = MotionBlur.Quality;
		var wasFocus = DepthOfField.Quality;
		var wasAa = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			var model = Model.FromCore( CoreModel.Load( "models/dev/box.vmdl" ) );

			if ( model is null || !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( "no box or no pipelines" );
				return;
			}

			//Nothing else that softens anything: what differs between the pictures has to be the one thing being measured.
			Rinten.Rendering.DrawSettings.Msaa = 0;
			DepthOfField.Quality = 0;

			var scene = new Scene();
			using var scope = scene.Push();

			var sun = scene.CreateObject().Components.Create<DirectionalLight>();
			sun.WorldRotation = Rotation.LookAt( new Vector3( 1, 0.3f, -0.5f ).Normal );

			//A row of boxes across the view, turned off the axes: plenty of edges for a smear to eat.
			for ( var i = 0; i < 9; i++ )
			{
				var box = scene.CreateObject();

				box.WorldPosition = new Vector3( 6 + ( i % 3 ), -3.0f + i, -1.0f + ( i % 2 ) );
				box.WorldRotation = Rotation.From( 15 + ( i * 7 ), 30 + ( i * 11 ), 5 * i );
				box.Components.Create<ModelRenderer>().Model = model;
			}

			var eye = scene.CreateObject();
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

			var camera = eye.Components.Create<CameraComponent>();

			//The effect is a component and it goes on the camera: without one there is no smear, however the console setting is set.
			eye.Components.Create<MotionBlur>();
			camera.BackgroundColor = Color.Black;
			camera.ZNear = 0.1f;
			camera.ZFar = 100.0f;
			camera.FieldOfView = 80.0f;

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer();
			var errorsBefore = CoreErrors.Count;

			var pixels = new byte[Size * Size * 4];

			//Still, with the blur off: what the scene actually looks like.
			MotionBlur.Quality = 0;
			Draw( drawer, scene, camera, view );
			Draw( drawer, scene, camera, view );
			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			var still = Sharpness( pixels );

			//Still, with the blur on: it has nothing to smear along, and must leave the picture where it found it.
			MotionBlur.Quality = 3;
			Draw( drawer, scene, camera, view );
			Draw( drawer, scene, camera, view );
			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			var standing = Sharpness( pixels );

			//And turning: the frame before was a different view, so every pixel has travelled.
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );
			Draw( drawer, scene, camera, view );

			eye.WorldRotation = Rotation.From( 0, 6, 0 );
			Draw( drawer, scene, camera, view );

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			var turning = Sharpness( pixels );

			//And one thing moving while the camera stands still, which the depth cannot tell from anything
			//else - it is the picture of velocities that says so, and only the things that moved draw into it.
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

			var mover = scene.CreateObject();

			mover.WorldPosition = new Vector3( 3.0f, 0, 0 );
			mover.WorldRotation = Rotation.From( 20, 35, 15 );
			mover.Components.Create<ModelRenderer>().Model = model;

			Draw( drawer, scene, camera, view );
			Draw( drawer, scene, camera, view );

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			//Measured around the middle, where the thing that moves is: one box of nine changes the whole
			//picture by a per cent or two whatever it does, and that is not a measurement of anything.
			var parked = Sharpness( pixels, ( Size / 2 ) - 30, ( Size / 2 ) + 30 );

			mover.WorldPosition = new Vector3( 3.0f, 0.35f, 0 );

			Draw( drawer, scene, camera, view );

			Assert.IsTrue( CoreRenderDevice.ReadView( view, pixels ) );

			var moving = Sharpness( pixels, ( Size / 2 ) - 30, ( Size / 2 ) + 30 );

			Console.WriteLine( $"SMEAR still {still:F3}, blur on and still {standing:F3}, " +
				$"turning {turning:F3}, thing parked {parked:F3} and moving {moving:F3}" );

			Assert.IsTrue( still > 0.5, "the scene has nothing in it to smear" );

			Assert.IsTrue( standing > still * 0.97,
				$"a still camera lost detail: {still:F3} became {standing:F3}" );

			Assert.IsTrue( turning < still * 0.9,
				$"turning the camera left the picture at {turning:F3} against {still:F3} standing still" );

			//The camera never moved for those last two, so anything that softened came out of the picture of velocities.
			Assert.IsTrue( moving < parked * 0.95,
				$"a thing that moved by itself left the picture at {moving:F3} against {parked:F3} parked" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			MotionBlur.Quality = wasQuality;
			DepthOfField.Quality = wasFocus;
			Rinten.Rendering.DrawSettings.Msaa = wasAa;

			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//The average step between neighbours: how much detail is left.
	private static double Sharpness( byte[] picture ) => Sharpness( picture, 1, Size - 1 );

	//The same, over one band of the picture.
	private static double Sharpness( byte[] picture, int fromX, int toX )
	{
		long total = 0;
		var counted = 0;

		for ( var y = 1; y < Size - 1; y++ )
		for ( var x = Math.Max( 1, fromX ); x < Math.Min( toX, Size - 1 ); x++ )
		{
			var at = ( ( ( y * Size ) + x ) * 4 );

			var here = Luma( picture, at );

			total += Math.Abs( here - Luma( picture, at + 4 ) );
			total += Math.Abs( here - Luma( picture, at + ( Size * 4 ) ) );

			counted += 2;
		}

		return total / (double)counted;
	}

	//Pixel luma from all three channels; green alone misses the magenta missing-texture boxes
	private static int Luma( byte[] picture, int at )
		=> ( ( picture[at] * 54 ) + ( picture[at + 1] * 183 ) + ( picture[at + 2] * 19 ) ) >> 8;

	private static void Draw( CoreSceneDrawer drawer, Scene scene, CameraComponent camera, ulong view )
	{
		if ( !CoreRenderDevice.BeginViewFrame( view, camera.BackgroundColor ) ) return;

		try { drawer.Draw( scene, camera, view, Size, Size ); }
		finally { CoreRenderDevice.EndViewFrame( view ); }
	}
}
