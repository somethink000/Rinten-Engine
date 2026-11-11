namespace Rinten;

//The full frame's multisampled colour into the world's picture - see msaa_resolve.slang
internal static class CoreWorldResolve
{
	private static ulong pipeline;
	private static int revision = -1;

	//S2x's edges and weights for each of MSAA 2x's two samples - see CoreSmaa.RunS2x
	private static readonly CoreSmaa s2xPictures = new();

	/// <summary>Whether this frame's world came through SMAA S2x - its later SMAA then only blends in last frame (4x).</summary>
	internal static bool S2xDone;

	//A resolve for a stage between the world's passes (AO's last lit): plain, as the world is resolved again at its end
	internal static bool Interim;

	internal static void Run()
	{
		CoreRenderDevice.ZoneBegin( "msaa resolve" );

		if ( pipeline == 0 || revision != CoreShaderCache.Revision )
		{
			Release();

			pipeline = CoreSceneDrawer.BakePost( EngineShaders.MsaaResolve, CoreRender.TargetKind.SceneColour );
			revision = CoreShaderCache.Revision;
		}

		//SMAA S2x smooths the two samples apart and averages them itself, where it can - see CoreSmaa.RunS2x
		var s2x = CoreSmaa.S2x && !Interim;
		var (samples, count, width, height) = VkFrame.WorldResolveBegin( open: !s2x );

		S2xDone = s2x && count == 2 && s2xPictures.RunS2x( samples, width, height, CoreSceneRenderer.LastExposure );

		if ( s2x && !S2xDone ) count = VkFrame.WorldResolveOpen() ? count : 0;

		if ( count > 0 && !S2xDone )
		{
			Span<byte> push = stackalloc byte[128];
			push.Clear();

			var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( push );
			floats[16] = (int)CoreRender.TextureIndex( samples );
			floats[17] = count;

			CoreRenderDevice.Draw( pipeline, 3, push );
			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.ZoneEnd();
	}

	internal static void Release()
	{
		if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
		pipeline = 0;
		s2xPictures.Release();
	}
}
