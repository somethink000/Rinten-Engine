using System;

namespace EngineTests.Core;

//Edge upscaler must beat bilinear when both are scored against a full-size render
[TestClass]
[DoNotParallelize]
public class UpscalingTests
{
	private const int Size = 256;

	[TestInitialize]
	public void OnMainThread() => ThreadSafe.MarkMainThread();

	[TestMethod]
	public void TheEdgeFilterBeatsStretching()
	{
		if ( !CoreNative.Available ) { Assert.Inconclusive( "no core" ); return; }
		if ( !CoreRenderDevice.Initialize( validation: false ) ) { Assert.Inconclusive( "no device" ); return; }

		var view = 0ul;
		var wasUpscaler = Rinten.Rendering.DrawSettings.Upscaler;
		var wasAa = Rinten.Rendering.DrawSettings.Msaa;

		try
		{
			var model = Model.FromCore( CoreModel.Load( "models/dev/box.vmdl" ) );

			if ( model is null || !CoreSceneDrawer.EnsureResources() )
			{
				Assert.Inconclusive( "no box or no pipelines" );
				return;
			}

			//Nothing that wanders between draws: the three pictures have to differ by the upscaler and by nothing else.
			Rinten.Rendering.DrawSettings.Msaa = 0;

			var scene = new Scene();
			using var scope = scene.Push();

			var sun = scene.CreateObject().Components.Create<DirectionalLight>();
			sun.WorldRotation = Rotation.LookAt( new Vector3( 1, 0.3f, -0.5f ).Normal );

			//Turned off the axes and standing at several depths: what an upscaler is judged on is edges
			//that cross the pixel grid at an angle, and a scene of straight ones would tell nothing apart.
			var random = 5678u;

			for ( var i = 0; i < 12; i++ )
			{
				random = ( random * 1664525u ) + 1013904223u;

				var box = scene.CreateObject();

				box.WorldPosition = new Vector3(
					4 + ( ( random >> 8 ) % 9 ),
					-3.0f + ( ( random >> 12 ) % 7 ),
					-2.0f + ( ( random >> 16 ) % 5 ) );

				box.WorldRotation = Rotation.From(
					( random >> 4 ) % 90, ( random >> 9 ) % 90, ( random >> 14 ) % 90 );

				box.WorldScale = 0.6f;
				box.Components.Create<ModelRenderer>().Model = model;
			}

			var eye = scene.CreateObject();
			eye.WorldRotation = Rotation.LookAt( Vector3.Forward );

			var camera = eye.Components.Create<CameraComponent>();
			camera.BackgroundColor = Color.Black;
			camera.ZNear = 0.1f;
			camera.ZFar = 100.0f;
			camera.FieldOfView = 80.0f;

			view = CoreRenderDevice.CreateView( Size, Size );
			Assert.AreNotEqual( 0ul, view );

			var drawer = new CoreSceneDrawer();

			var full = new byte[Size * Size * 4];
			var stretched = new byte[Size * Size * 4];
			var filtered = new byte[Size * Size * 4];

			Render( drawer, scene, camera, view, full );

			if ( !CoreRenderDevice.SetViewScale( view, 0.5f ) )
			{
				Assert.Inconclusive( $"this device will not draw a view smaller: {CoreRenderDevice.Description}" );
				return;
			}

			Rinten.Rendering.DrawSettings.Upscaler = 1;
			Render( drawer, scene, camera, view, stretched );

			Rinten.Rendering.DrawSettings.Upscaler = 2;
			Render( drawer, scene, camera, view, filtered );

			var byStretching = Difference( full, stretched );
			var byFilter = Difference( full, filtered );

			//How much detail each picture has, measured the same way for all three. Carrying a picture up cannot invent
			//what was not drawn, so both come out softer than the reference - the question is which comes out less soft.
			var wanted = Sharpness( full );
			var afterStretching = Sharpness( stretched );
			var afterFilter = Sharpness( filtered );

			Console.WriteLine( $"UPSCALE error {byStretching:F3} against {byFilter:F3}, " +
				$"sharpness {wanted:F3} / stretched {afterStretching:F3} / filtered {afterFilter:F3}" );

			//Both are wrong - three quarters of the pixels were never drawn - and neither is expected to be right.
			Assert.IsTrue( byStretching > 0.5, "the half-size picture is the same as the full one" );

			Assert.IsTrue( afterStretching < wanted,
				"stretching came out sharper than the picture it is standing in for" );

			//What an edge-adaptive filter is for: an edge that stays an edge.
			Assert.IsTrue( afterFilter > afterStretching * 1.02,
				$"the filter left the picture at {afterFilter:F3} against {afterStretching:F3} for stretching" );

			//And it has to be closer to the reference's detail, not merely sharper - a filter that oversharpens is also wrong.
			Assert.IsTrue( Math.Abs( wanted - afterFilter ) < Math.Abs( wanted - afterStretching ),
				$"the filter is further from the reference sharpness {wanted:F3} than stretching is" );

			//Being sharper must not have cost accuracy: a filter that guesses wildly would score better on detail and worse on everything else.
			Assert.IsTrue( byFilter < byStretching * 1.05,
				$"the filter is {byFilter:F3} away from the reference against {byStretching:F3}" );
		}
		finally
		{
			Rinten.Rendering.DrawSettings.Upscaler = wasUpscaler;
			Rinten.Rendering.DrawSettings.Msaa = wasAa;

			CoreRenderDevice.DestroyView( view );
			CoreRenderDevice.Shutdown();
		}
	}

	//Detail metric: mean green step between neighbours; blur lowers it, sharpening raises it
	private static double Sharpness( byte[] picture )
	{
		long total = 0;
		var counted = 0;

		for ( var y = 1; y < Size - 1; y++ )
		for ( var x = 1; x < Size - 1; x++ )
		{
			var at = ( ( ( y * Size ) + x ) * 4 ) + 1;

			total += Math.Abs( picture[at] - picture[at + 4] );
			total += Math.Abs( picture[at] - picture[at + ( Size * 4 )] );

			counted += 2;
		}

		return total / (double)counted;
	}

	//How far one picture is from another, per channel.
	private static double Difference( byte[] one, byte[] other )
	{
		long total = 0;

		for ( var i = 0; i < one.Length; i++ )
		{
			if ( ( i % 4 ) == 3 ) continue;

			total += Math.Abs( one[i] - other[i] );
		}

		return total / (double)( one.Length * 3 / 4 );
	}

	private static void Render( CoreSceneDrawer drawer, Scene scene, CameraComponent camera,
		ulong view, byte[] into )
	{
		//Twice: the first frame after a change is the one that rebuilds whatever was worked out about the last.
		for ( var frame = 0; frame < 2; frame++ )
		{
			if ( !CoreRenderDevice.BeginViewFrame( view, camera.BackgroundColor ) ) continue;

			try { drawer.Draw( scene, camera, view, Size, Size ); }
			finally { CoreRenderDevice.EndViewFrame( view ); }
		}

		Assert.IsTrue( CoreRenderDevice.ReadView( view, into ), "the picture should come back" );
	}
}
