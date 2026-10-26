using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Rinten;

public partial class Terrain
{
	//The ground's height in world metres over a square round the eye, for shaders that want what lies straight under a
	//point - GroundBelow in frame.slang, whose GroundTexels and GroundTexel these are
	private const int GroundTexels = 1024;
	private const float GroundTexel = 0.25f;

	//Metres the eye may go from the square's middle before it is laid again round it
	private const float GroundMoved = 64.0f;

	private Texture groundMap;

	//World x, z of the corner of the square on the card
	private Vector2 groundAt;
	private bool groundKept, groundStale;
	private Task<(byte[] Bytes, Vector2 Corner)> groundLaying;

	//Once a frame: a square laid on a worker is put on the card, the world told of it, and another begun where the eye left it
	private void PumpGround()
	{
		if ( Application.IsHeadless || !CoreNative.Available || CoreRenderDevice.PortableFrame ) return;
		if ( Scene?.SceneWorld is not { } world ) return;

		if ( groundLaying is { IsCompleted: true } done )
		{
			groundLaying = null;

			if ( done.IsCompletedSuccessfully )
			{
				groundMap ??= Texture.Create( GroundTexels, GroundTexels, ImageFormat.LINEAR_RGBA8888 ).WithName( "terrain_ground" ).Finish();
				groundMap.Update( done.Result.Bytes );
				groundAt = done.Result.Corner;
				groundKept = true;
			}
		}

		if ( groundKept ) world.Ground = (groundMap.Index, groundAt.x, groundAt.y);

		var seen = Scene.Detail.Looking ? Scene.Detail.Eye : Scene.DetailCamera?.WorldPosition;
		if ( seen is not { } eye || groundLaying is not null || Storage?.Mesh is not { } mesh ) return;

		var half = GroundTexels * GroundTexel * 0.5f;

		if ( groundKept && !groundStale && MathF.Abs( eye.x - groundAt.x - half ) < GroundMoved && MathF.Abs( eye.z - groundAt.y - half ) < GroundMoved )
			return;

		groundStale = false;

		//On whole texels, so a square laid again over the same ground reads it the same
		var corner = new Vector2( MathF.Floor( ( eye.x - half ) / GroundTexel ) * GroundTexel, MathF.Floor( ( eye.z - half ) / GroundTexel ) * GroundTexel );
		var transform = WorldTransform;

		groundLaying = System.Threading.Tasks.Task.Run( () => LayGround( mesh, transform, corner ) );
	}

	//Each texel's ground straight down through its middle, as a float's four bytes; far below where there is none
	private static (byte[] Bytes, Vector2 Corner) LayGround( TerrainMesh mesh, Transform transform, Vector2 corner )
	{
		var bytes = new byte[GroundTexels * GroundTexels * 4];
		var top = mesh.Bounds.Maxs.y + 1.0f;
		var reach = top - mesh.Bounds.Mins.y + 2.0f;

		Parallel.For( 0, GroundTexels, row =>
		{
			var heights = MemoryMarshal.Cast<byte, float>( bytes.AsSpan( row * GroundTexels * 4, GroundTexels * 4 ) );
			var z = corner.y + ( ( row + 0.5f ) * GroundTexel );

			for ( var x = 0; x < GroundTexels; x++ )
			{
				var local = transform.PointToLocal( new Vector3( corner.x + ( ( x + 0.5f ) * GroundTexel ), 0.0f, z ) );

				heights[x] = mesh.Trace( new Vector3( local.x, top, local.z ), Vector3.Down, reach, out var travelled, out _, out _ )
					? transform.PointToWorld( new Vector3( local.x, top - travelled, local.z ) ).y
					: -1e6f;
			}
		} );

		return (bytes, corner);
	}

	private void DropGround()
	{
		if ( groundMap is not null && Scene?.SceneWorld is { } world && world.Ground.Index == groundMap.Index ) world.Ground = default;

		groundMap?.Dispose();
		groundMap = null;
		groundKept = false;
	}
}
