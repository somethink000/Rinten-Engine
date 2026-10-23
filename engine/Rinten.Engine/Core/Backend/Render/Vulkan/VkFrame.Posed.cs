namespace Rinten;

//Morphed models posed by compute once a frame for the portable frame, into a room of the frame's own that every pass then
//draws them out of as still meshes - see engine/classic_pose.slang and CoreSceneDrawer.PoseAhead
internal static unsafe partial class VkFrame
{
	//A posed range starts on a whole vertex and a whole bone: 52 and 64 both divide it
	private const ulong PosedAlign = VkDevice.EngineVertexStride * 16;

	//Grown only while nothing of the frame is in it: what was handed out already names the buffer it is in.
	private static bool PosedFits( Frame frame, ulong wanted )
	{
		if ( wanted > frame.PosedPeak ) frame.PosedPeak = wanted;
		if ( wanted <= frame.PosedCapacity ) return true;
		if ( frame.PosedUsed > 0 ) return false;

		GrowPosed( frame, wanted );
		return frame.PosedCapacity >= wanted;
	}

	private static void GrowPosed( Frame frame, ulong wanted )
	{
		var capacity = Math.Max( frame.PosedCapacity * 2, 1ul << 20 );
		while ( capacity < wanted ) capacity *= 2;

		//Retired, not destroyed: an earlier frame of this slot may be the last to have read it, and its fence is what says so
		if ( frame.Posed != 0 ) VkBuffers.Destroy( frame.Posed );

		frame.Posed = VkBuffers.Reserve( (long)capacity );
		frame.PosedCapacity = frame.Posed != 0 ? capacity : 0;
	}

	//Made ready at the top of a frame: emptied, and grown to the most the last frame of the slot wanted
	private static void ReadyPosed( Frame frame )
	{
		frame.PosedUsed = 0;

		if ( frame.PosedPeak > frame.PosedCapacity ) GrowPosed( frame, frame.PosedPeak );

		frame.PosedPeak = 0;
	}

	private static void DestroyPosed( Frame frame )
	{
		if ( frame.Posed != 0 ) VkBuffers.Destroy( frame.Posed );

		frame.Posed = 0;
		frame.PosedCapacity = frame.PosedUsed = frame.PosedPeak = 0;
	}

	//Poses vertexCount vertices into the frame's room: the room's buffer and where in it the posed vertices start, in vertices;
	//nought where there was no room this frame, and the draw then poses in its own vertex stage as it would have
	internal static ulong PoseVertices( ulong pipeline, ulong vertices, ulong skin, ulong runs, ulong deltas, int vertexCount,
		int deltaCount, float* bones, int boneCount, float* weights, int weightCount, out int firstVertex )
	{
		firstVertex = 0;

		lock ( VkDevice.renderLock )
		{
			if ( !Current.Recording || vertexCount <= 0 || weights is null || weightCount <= 0 ) return 0;
			if ( boneCount < 0 || (uint)boneCount > VkDevice.MaxBones || ( boneCount > 0 && bones is null ) ) return 0;

			var frame = CurrentFrame;

			var at = ( frame.PosedUsed + PosedAlign - 1 ) / PosedAlign * PosedAlign;
			var posedBytes = ( (ulong)vertexCount * VkDevice.EngineVertexStride + 63 ) & ~63ul;
			var boneBytes = (ulong)boneCount * 64;
			var weightBytes = (ulong)weightCount * 4;
			var end = at + posedBytes + boneBytes + weightBytes;

			if ( !PosedFits( frame, end ) ) return 0;

			var room = frame.Posed;
			var bonesAt = at + posedBytes;
			var weightsAt = bonesAt + boneBytes;

			if ( boneCount > 0 && FrameWrite( room, (long)bonesAt, (byte*)bones, (long)boneBytes ) == 0 ) return 0;
			if ( FrameWrite( room, (long)weightsAt, (byte*)weights, (long)weightBytes ) == 0 ) return 0;

			frame.PosedUsed = at + posedBytes + boneBytes + weightBytes;

			var buffers = stackalloc ulong[7];
			buffers[0] = vertices;
			buffers[1] = skin != 0 ? skin : vertices;
			buffers[2] = runs;
			buffers[3] = deltas;
			buffers[4] = room;
			buffers[5] = room;
			buffers[6] = room;

			//PosePush in classic_pose.slang
			var push = stackalloc uint[8];
			push[0] = (uint)vertexCount;
			push[1] = (uint)at;
			push[2] = (uint)( bonesAt / 64 );
			push[3] = (uint)( weightsAt / 4 );
			push[4] = (uint)boneCount;
			push[5] = (uint)weightCount;
			push[6] = boneCount > 0 && skin != 0 ? 1u : 0u;
			push[7] = (uint)deltaCount;

			if ( RecordDispatch( pipeline, buffers, 7, 0, (byte*)push, 8 * sizeof( uint ),
				( vertexCount + 63 ) / 64, 1, 1, 0, 0 ) == 0 ) return 0;

			firstVertex = (int)( at / VkDevice.EngineVertexStride );
			return room;
		}
	}
}
