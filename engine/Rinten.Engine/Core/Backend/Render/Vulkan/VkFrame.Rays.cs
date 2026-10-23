namespace Rinten;

internal static unsafe partial class VkFrame
{
	/// <summary>Builds the scene's ray structure into the frame being recorded; its address, or nought.</summary>
	internal static ulong RaysBuild( ReadOnlySpan<VkRays.Instance> placed )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkRays.On || !Current.Recording ) return 0;

			var frame = CurrentFrame;
			CloseOpenPasses( frame );

			return VkRays.Build( frame.Commands, placed );
		}
	}

	/// <summary>Builds a structure over a soup of triangles into the frame being recorded; null where it is not built.</summary>
	internal static VkRays.Soup RaysBuildSoup( ReadOnlySpan<float> corners, int solid )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkRays.On || !Current.Recording ) return null;

			var frame = CurrentFrame;
			CloseOpenPasses( frame );

			return VkRays.BuildSoup( frame.Commands, corners, solid );
		}
	}

	/// <summary>A buffer's run of half texels into a half picture, in the frame's order after what wrote it.</summary>
	internal static bool RaysCopy( ulong buffer, long offset, ulong texture, int width, int height )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording ) return false;

			var frame = CurrentFrame;
			CloseOpenPasses( frame );

			return VkTextures.CopyFromBuffer( frame.Commands, VkBuffers.Get( buffer ), (ulong)offset, texture, width, height );
		}
	}
}
