namespace Rinten;

//The full frame's multisampled colour into the world's picture - see msaa_resolve.slang
internal static class CoreWorldResolve
{
	private static ulong pipeline;
	private static int revision = -1;

	internal static void Run()
	{
		CoreRenderDevice.ZoneBegin( "msaa resolve" );

		if ( pipeline == 0 || revision != CoreShaderCache.Revision )
		{
			Release();

			pipeline = CoreSceneDrawer.BakePost( EngineShaders.MsaaResolve, CoreRender.TargetKind.SceneColour );
			revision = CoreShaderCache.Revision;
		}

		var (samples, count) = VkFrame.WorldResolveBegin();

		if ( count > 0 )
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
	}
}
