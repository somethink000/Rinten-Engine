namespace Rinten;

//Where one eye is drawn from and how wide it sees - the pose in the world, and the four angles
//of its field, which a headset's are not the same either side. See CoreSceneDrawer.Eye.
internal readonly record struct XrEyeView( Vector3 Position, Rotation Rotation,
	float AngleLeft, float AngleRight, float AngleUp, float AngleDown )
{
	/// <summary>The projection for it: off centre, the way the angles say, with depth in Vulkan's nought to one and Y turned over the way every other projection in the drawer is - see CoreSceneDrawer.ProjectionMatrix.</summary>
	public System.Numerics.Matrix4x4 Projection( float near, float far )
	{
		var left = MathF.Tan( AngleLeft ) * near;
		var right = MathF.Tan( AngleRight ) * near;
		var down = MathF.Tan( AngleDown ) * near;
		var up = MathF.Tan( AngleUp ) * near;

		var projection = System.Numerics.Matrix4x4.CreatePerspectiveOffCenter( left, right, down, up, near, far );

		//Y turned over: the column Y comes out of, all of it, since an off-centre field puts a term in the third row as well as the second.
		projection.M22 = -projection.M22;
		projection.M32 = -projection.M32;

		return CoreSceneDrawer.Reversed( projection );
	}

	/// <summary>One field enclosing both eyes, for a stereo pass to cull, sort and plan the sun with</summary>
	public static XrEyeView Around( in XrEyeView first, in XrEyeView second )
	{
		var turn = MathF.Acos( Math.Clamp( Vector3.Dot( first.Rotation.Forward, second.Rotation.Forward ), -1.0f, 1.0f ) );

		//Short of straight out sideways, where a field stops being one.
		const float widest = 1.48f;

		var left = MathF.Max( MathF.Min( first.AngleLeft, second.AngleLeft ) - turn, -widest );
		var right = MathF.Min( MathF.Max( first.AngleRight, second.AngleRight ) + turn, widest );
		var up = MathF.Min( MathF.Max( first.AngleUp, second.AngleUp ) + turn, widest );
		var down = MathF.Max( MathF.Min( first.AngleDown, second.AngleDown ) - turn, -widest );

		//An eye half the distance between them from the middle is inside a side that far back from it, the side at its narrowest.
		var narrowest = MathF.Min( MathF.Min( -left, right ), MathF.Min( up, -down ) );
		var half = ( first.Position - second.Position ).Length * 0.5f;
		var back = narrowest > 0.01f ? half / MathF.Tan( narrowest ) : half;

		var middle = ( first.Position + second.Position ) * 0.5f - first.Rotation.Forward * back;

		return new XrEyeView( middle, first.Rotation, left, right, up, down );
	}
}

//Per-eye view and drawer (each keeps its own history), independent of which headset asks
internal static class CoreEyes
{
	//An eye where the headset says it is, put in the world: the pose it gave, sized by the world's scale, stood where the play area
	//stands. The two headsets the engine knows differ in how they are asked and in nothing that happens after, which starts here.
	internal static XrEyeView Field( in Transform local, in Transform anchor, float scale,
		float angleLeft, float angleRight, float angleUp, float angleDown )
	{
		var world = anchor.ToWorld( local.WithPosition( local.Position * scale ) );

		return new XrEyeView( world.Position, world.Rotation, angleLeft, angleRight, angleUp, angleDown );
	}

	//One eye's view and the drawer that fills it.
	internal sealed class Eye
	{
		public ulong View;
		public (int Width, int Height) ViewSize;

		//Not a game view: a screen's panels are a screen's, and would be stuck to the eye - a headset sees the world's panels, which the world draws.
		public readonly CoreSceneDrawer Drawer = new() { GameViewSource = () => false };
	}

	//The view an eye is drawn into, made at the size asked for and remade when that changes. Two layers where both eyes are drawn in one pass.
	internal static bool EnsureView( Eye eye, int width, int height, int layers = 1 )
	{
		if ( eye.View != 0 && eye.ViewSize == (width, height) ) return true;

		if ( eye.View != 0 ) CoreRenderDevice.DestroyView( eye.View );

		eye.View = layers > 1
			? CoreRenderDevice.CreateEyesView( width, height )
			: CoreRenderDevice.CreateView( width, height );
		eye.ViewSize = (width, height);

		return eye.View != 0;
	}

	//The view and its drawer let go. Nothing else ever lets go of an eye's, and they outlive the device if this
	//is not called - which is every one of them named in the validation layer's count as the device is destroyed.
	internal static void Let( Eye eye )
	{
		eye.Drawer.Release();

		if ( eye.View != 0 ) CoreRenderDevice.DestroyView( eye.View );

		eye.View = 0;
		eye.ViewSize = default;
	}

	//One view's frame: drawn, each layer copied into the target given for it, and sent. Settled rather than sent
	//on where the caller is about to read the pixels back, so they are there to be read - see CoreXr.Shoot.
	internal static bool Draw( Eye eye, Scene scene, CameraComponent camera, int width, int height, bool settle,
		ReadOnlySpan<ulong> targets )
	{
		//Named because on a page this is where the encoder is made and the view's scratch is checked,
		//once an eye - and a third of a browser's VR frame was sitting outside every zone there was.
		CoreRenderDevice.ZoneBegin( "eye begin" );
		var began = CoreRenderDevice.BeginViewFrame( eye.View, camera.BackgroundColor );
		CoreRenderDevice.ZoneEnd();

		if ( !began ) return false;

		bool drawn;

		//Where the device shades finest this frame - see CoreFovea.
		CoreRenderDevice.ZoneBegin( "eye fovea" );
		CoreFovea.Place( eye.View, eye.Drawer.Fovea, eye.Drawer.Eye, eye.Drawer.SecondEye );
		CoreRenderDevice.ZoneEnd();

		try
		{
			//The drawer's own frame under a name, so that what is left outside it is the headset's own work -
			//reading the poses, sizing the eyes, asking the runtime for a picture - and not a lump called "vr frame".
			CoreRenderDevice.ZoneBegin( "view draw" );
			eye.Drawer.Draw( scene, camera, eye.View, width, height );
			CoreRenderDevice.ZoneEnd();

			//Named apart from the draw: on a page it is a blit of the whole eye and it is worth knowing whether it is the blit or the draw.
			CoreRenderDevice.ZoneBegin( "eye copy" );

			for ( var layer = 0; layer < targets.Length; layer++ )
				CoreRenderDevice.CopyViewTo( eye.View, targets[layer], width, height, layer );

			CoreRenderDevice.ZoneEnd();
		}
		finally
		{
			CoreRenderDevice.ZoneBegin( "eye end" );

			//The frame goes up whatever the draw did, or the view is left open.
			drawn = settle
				? CoreRenderDevice.EndViewFrame( eye.View )
				: CoreRenderDevice.EndViewFrameAsync( eye.View );

			CoreRenderDevice.ZoneEnd();
		}

		return drawn;
	}
}
