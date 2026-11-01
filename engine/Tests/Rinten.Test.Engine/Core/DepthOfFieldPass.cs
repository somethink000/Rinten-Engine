using System;

namespace EngineTests.Core;

//What is out of focus goes soft, and what is in focus does not. Both halves matter. A pass that blurs everything is not depth of field, it is a
//blur, and it would pass a test that only looked at the far half - so the near half is measured too and has to come back as sharp as it went in.
[TestClass]
[DoNotParallelize]
public class DepthOfFieldTests
{
	private const int Size = 192;

	[TestInitialize]
	public void OnMainThread() => ThreadSafe.MarkMainThread();

	[TestMethod]
	public void ItBlursTheFarHalfAndLeavesTheNearOne()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "no core" ); return; }
		if ( !CoreRenderDevice.Initialize( validation: true ) ) { Assert.Inconclusive( "no device" ); return; }

		var view = 0ul;
		var wasQuality = DepthOfField.Quality;
		var wasAa = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			var model = Model.FromCore( CoreModel.Load( "models/dev/box.vmdl" ) );

			if ( model is null || !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( "no box or no pipelines" );
				return;
			}

			//Multisampling softens edges too, and the exposure wanders. Both off, so that what differs between the two pictures is the one thing being measured.
			Rinten.Rendering.DrawSettings.Msaa = 0;

			var scene = new Scene();
			using var scope = scene.Push();

			var sun = scene.CreateObject().Components.Create<DirectionalLight>();
			sun.WorldRotation = Rotation.LookAt( new Vector3( 1, 0.4f, -0.6f ).Normal );

			//One near and one far, both turned off the axes so they have edges worth
			//measuring, and on opposite sides of the picture so the two can be scored apart.
			void Box( float distance, float across, float scale )
			{
				var made = scene.CreateObject();

				made.WorldPosition = new Vector3( distance, across, 0 );
				made.WorldRotation = Rotation.From( 20, 35, 15 );
				made.WorldScale = scale;
				made.Components.Create<ModelRenderer>().Model = model;
			}

			Box( 3.0f, -1.2f, 1.0f );
			Box( 26.0f, 10.0f, 9.0f );

			var eye = scene.CreateObject();
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

			var camera = eye.Components.Create<CameraComponent>();
			camera.BackgroundColor = Color.Black;
			camera.ZNear = 0.1f;
			camera.ZFar = 200.0f;
			camera.FieldOfView = 70.0f;

			//On the camera, because that is what an effect being a component means: it applies to the camera it is under. See CoreSceneDrawer.EffectOn.
			var focus = eye.Components.Create<DepthOfField>();

			focus.FocalDistance = 3.0f;
			focus.FocusRange = 6.0f;
			focus.BlurSize = 10.0f;

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer();
			var errorsBefore = CoreErrors.Count;

			var sharp = new byte[Size * Size * 4];
			var blurred = new byte[Size * Size * 4];

			DepthOfField.Quality = 0;
			Render( drawer, scene, camera, view, sharp );

			DepthOfField.Quality = 3;
			Render( drawer, scene, camera, view, blurred );

			//Plus Y is to the left of a camera looking along plus X, so the far box - which
			//stands at plus Y - is the left half of the picture and the near one is the right.
			var farBefore = Sharpness( sharp, 0, Size / 2 );
			var farAfter = Sharpness( blurred, 0, Size / 2 );

			var nearBefore = Sharpness( sharp, Size / 2, Size );
			var nearAfter = Sharpness( blurred, Size / 2, Size );

			Console.WriteLine( $"FOCUS near {nearBefore:F3} to {nearAfter:F3}, far {farBefore:F3} to {farAfter:F3}" );

			Assert.IsTrue( farBefore > 0.5, "the far half has nothing in it to blur" );
			Assert.IsTrue( nearBefore > 0.5, "the near half has nothing in it to keep sharp" );

			Assert.IsTrue( farAfter < farBefore * 0.9,
				$"the far half went from {farBefore:F3} to {farAfter:F3}, which is not a blur" );

			Assert.IsTrue( nearAfter > nearBefore * 0.9,
				$"the near half went from {nearBefore:F3} to {nearAfter:F3} - the focused half was blurred too" );

			if ( CoreRenderDevice.ValidationEnabled )
			{
				Assert.AreEqual( errorsBefore, CoreErrors.Count,
					"validation reported errors - they are in the test log, by name" );
			}
		}
		finally
		{
			DepthOfField.Quality = wasQuality;
			Rinten.Rendering.DrawSettings.Msaa = wasAa;

			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//The average step between neighbours, over one band of columns.
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

		return counted == 0 ? 0.0 : total / (double)counted;
	}

	//Pixel luma from all three channels; green alone misses the magenta missing-texture boxes
	private static int Luma( byte[] picture, int at )
		=> ( ( picture[at] * 54 ) + ( picture[at + 1] * 183 ) + ( picture[at + 2] * 19 ) ) >> 8;

	private static void Render( CoreSceneDrawer drawer, Scene scene, CameraComponent camera,
		ulong view, byte[] into )
	{
		for ( var frame = 0; frame < 2; frame++ )
		{
			if ( !CoreRenderDevice.BeginViewFrame( view, camera.BackgroundColor ) ) continue;

			try { drawer.Draw( scene, camera, view, Size, Size ); }
			finally { CoreRenderDevice.EndViewFrame( view ); }
		}

		Assert.IsTrue( CoreRenderDevice.ReadView( view, into ), "the picture should come back" );
	}
}
