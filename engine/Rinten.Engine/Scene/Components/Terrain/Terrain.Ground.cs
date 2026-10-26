using System.Runtime.InteropServices;

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
	private System.Threading.Tasks.Task<(byte[] Bytes, Vector2 Corner)> groundLaying;

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

	//Each texel's ground under its middle, as a float's four bytes; far below where there is none. The triangles over the
	//square are laid into it once each, on this one worker - a trace down through every texel keeps every core busy for seconds
	private static (byte[] Bytes, Vector2 Corner) LayGround( TerrainMesh mesh, Transform transform, Vector2 corner )
	{
		var bytes = new byte[GroundTexels * GroundTexels * 4];
		var heights = MemoryMarshal.Cast<byte, float>( bytes.AsSpan() );
		heights.Fill( -1e6f );

		//The square in the terrain's plane, wide by an edge so a triangle reaching in from outside is laid too
		var side = GroundTexels * GroundTexel;
		var low = new Vector2( float.MaxValue );
		var high = new Vector2( float.MinValue );

		for ( var k = 0; k < 4; k++ )
		{
			var local = transform.PointToLocal( new Vector3( corner.x + ( ( k & 1 ) * side ), 0.0f, corner.y + ( ( k >> 1 ) * side ) ) );
			low = Vector2.Min( low, new Vector2( local.x, local.z ) );
			high = Vector2.Max( high, new Vector2( local.x, local.z ) );
		}

		var reach = new Vector2( mesh.LongestEdge );
		var triangles = new List<int>();
		mesh.TrianglesInRect( low - reach, high + reach, triangles );

		foreach ( var t in triangles )
		{
			if ( !mesh.IsTriangleAlive( t ) ) continue;

			mesh.GetTriangle( t, out var ia, out var ib, out var ic );
			var a = transform.PointToWorld( mesh.PositionOf( ia ) );
			var b = transform.PointToWorld( mesh.PositionOf( ib ) );
			var c = transform.PointToWorld( mesh.PositionOf( ic ) );

			//Its area seen from above; one standing on its edge covers no texel's middle
			var area = ( ( b.x - a.x ) * ( c.z - a.z ) ) - ( ( c.x - a.x ) * ( b.z - a.z ) );
			if ( MathF.Abs( area ) < 1e-8f ) continue;

			var x0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( a.x, MathF.Min( b.x, c.x ) ) - corner.x ) / GroundTexel - 0.5f ), 0 );
			var x1 = Math.Min( (int)MathF.Floor( ( MathF.Max( a.x, MathF.Max( b.x, c.x ) ) - corner.x ) / GroundTexel - 0.5f ), GroundTexels - 1 );
			var z0 = Math.Max( (int)MathF.Ceiling( ( MathF.Min( a.z, MathF.Min( b.z, c.z ) ) - corner.y ) / GroundTexel - 0.5f ), 0 );
			var z1 = Math.Min( (int)MathF.Floor( ( MathF.Max( a.z, MathF.Max( b.z, c.z ) ) - corner.y ) / GroundTexel - 0.5f ), GroundTexels - 1 );

			for ( var z = z0; z <= z1; z++ )
			{
				var pz = corner.y + ( ( z + 0.5f ) * GroundTexel );

				for ( var x = x0; x <= x1; x++ )
				{
					var px = corner.x + ( ( x + 0.5f ) * GroundTexel );

					//Where the middle stands among the corners; a hair outside still counts, so no seam is left between two
					var wb = ( ( ( px - a.x ) * ( c.z - a.z ) ) - ( ( c.x - a.x ) * ( pz - a.z ) ) ) / area;
					var wc = ( ( ( b.x - a.x ) * ( pz - a.z ) ) - ( ( px - a.x ) * ( b.z - a.z ) ) ) / area;
					var wa = 1.0f - wb - wc;
					if ( wa < -1e-4f || wb < -1e-4f || wc < -1e-4f ) continue;

					//The highest surface over the texel, as HeightAt answers under an overhang
					var at = ( z * GroundTexels ) + x;
					heights[at] = MathF.Max( heights[at], ( a.y * wa ) + ( b.y * wb ) + ( c.y * wc ) );
				}
			}
		}

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
