using System.Runtime.InteropServices;

namespace Rinten;

//The relief ring: ground near the eye cut finer the nearer and lifted by layer heights (terrain_relief.shader), drawn
//as a card mesh (CoreCardMeshes) with the ground's material so it casts. Full frame only; collision never sees it
internal sealed class TerrainRelief : IDisposable
{
	/// <summary>Metres the lift reaches and fades over, and how far the ground's corners sink under it.</summary>
	public const float Reach = 16.0f;
	public const float Fade = 4.0f;
	public const float Sink = 0.02f;

	//How far the eye goes before the ring is built round it again, in metres.
	private const float Moved = 1.0f;

	//What one ring may come to - a few tens of megabytes at most; past it every triangle is cut coarser until it fits.
	private const int MostVertices = 400_000;

	//What the card reads of a corner: where it is, its normal, its chunk's eight slots' paint, and which layers those are.
	[StructLayout( LayoutKind.Sequential )]
	private struct Corner
	{
		public Vector4 Place, Normal, Paint, PaintMore, Palette;
	}

	private readonly List<Corner> corners = new();
	private readonly List<uint> cuts = new();

	private GpuBuffer<Corner> cornerBuffer;
	private GpuBuffer<uint> cutBuffer;
	private GpuBuffer vertexBuffer, indexBuffer;

	private int triangles, vertices, indices;

	//Built and waiting for the card, and what the card last made of it: the ring is drawn only once it has.
	private bool waiting, expanded;

	private Vector3 builtAt;
	private bool built;

	//What the world draws it as.
	private readonly CoreCardMeshes.Shape shape = new();

	//The ring's box in the terrain's metres, before it is lifted.
	private BBox box;

	private static readonly List<TerrainRelief> live = new();

	//What ExpandAll walks, copied out of live under its lock
	private static readonly List<TerrainRelief> expanding = new();
	private static long expandedFrame = -1;

	/// <summary>The numbers the ring's draw carries - where the paint mask is; see Terrain.Mask.</summary>
	public RenderAttributes Attributes { get; } = new();

	/// <summary>Where the eye was, in the terrain's metres, when the ring was built round it.</summary>
	public Vector3 Eye => builtAt;

	/// <summary>Whether there is a ring the card has made to draw.</summary>
	public bool Drawn => expanded && indices > 0;

	public TerrainRelief()
	{
		lock ( live ) live.Add( this );

		shape.Attributes = Attributes;

		//Made this frame counts: the card makes it before any pass or map draws it - see ExpandAll.
		shape.Ready = () => Drawn || ( waiting && ParamsIndex != 0 );

		CoreCardMeshes.Add( shape );
	}

	/// <summary>Where the ring is drawn: world, material, the terrain's place, and its layers' greatest lift.</summary>
	public void Stand( SceneWorld world, Material wears, Transform terrain, float lift )
	{
		shape.World = world;
		shape.Wears = wears;
		shape.Transform = terrain;

		//The drawer reads the mesh's parts as it queues, so they are laid again only when they change
		if ( lift != this.lift ) Hold( lift );
	}

	private float lift;

	//The mesh pointed at the ring as it was last built, in a box grown by what can lift it.
	private void Hold( float lifted )
	{
		lift = lifted;

		var grown = new BBox( box.Mins - new Vector3( lift ), box.Maxs + new Vector3( lift ) );
		shape.Hold( vertexBuffer, indexBuffer, triangles > 0 ? indices : 0, grown );
	}

	/// <summary>Whether the eye has gone far enough from where the ring was built that it wants building again.</summary>
	public bool Wants( Vector3 eye ) => !built || eye.Distance( builtAt ) > Moved;

	/// <summary>Builds the ring from the ground near the eye and its chunks' palettes, in the terrain's metres.</summary>
	public void Build( Vector3 eye, IEnumerable<(TerrainSoup Ground, byte[] Palette)> grounds )
	{
		builtAt = eye;
		built = true;

		corners.Clear();
		cuts.Clear();

		var reach = Reach + Fade;

		Span<float> slots = stackalloc float[Terrain.MaxLayers];

		foreach ( var (ground, palette) in grounds )
		{
			var points = ground.Vertices;
			var order = ground.Indices;
			var numbers = TerrainChunkBaker.PaletteNumbers( palette );

			for ( var t = 0; t + 2 < order.Count; t += 3 )
			{
				var a = points[order[t]];
				var b = points[order[t + 1]];
				var c = points[order[t + 2]];

				var near = MathF.Min( a.Position.Distance( eye ), MathF.Min( b.Position.Distance( eye ), c.Position.Distance( eye ) ) );
				if ( near > reach ) continue;

				corners.Add( CornerOf( a, palette, numbers, slots ) );
				corners.Add( CornerOf( b, palette, numbers, slots ) );
				corners.Add( CornerOf( c, palette, numbers, slots ) );

				//Cuts a side by how near it is: a triangle in the fade's last stretch is lifted by nothing and only covers the sunk ground.
				uint n = near < 4.0f ? 16u : near < 8.0f ? 8u : near < 12.0f ? 4u : near < Reach ? 2u : 1u;

				cuts.Add( n );
				cuts.Add( 0 );
				cuts.Add( 0 );
				cuts.Add( 0 );
			}
		}

		triangles = corners.Count / 3;

		box = default;

		for ( var k = 0; k < corners.Count; k++ )
		{
			var at = new Vector3( corners[k].Place.x, corners[k].Place.y, corners[k].Place.z );
			box = k == 0 ? new BBox( at, at ) : box.AddPoint( at );
		}

		//Where each triangle's vertices and indices start, coarser all round until the whole ring fits.
		while ( true )
		{
			long v = 0, i = 0;

			for ( var t = 0; t < triangles; t++ )
			{
				var n = (long)cuts[t * 4];

				cuts[( t * 4 ) + 1] = (uint)v;
				cuts[( t * 4 ) + 2] = (uint)i;

				v += ( n + 1 ) * ( n + 2 ) / 2;
				i += 3 * n * n;
			}

			if ( v <= MostVertices || triangles == 0 )
			{
				vertices = (int)v;
				indices = (int)i;
				break;
			}

			var any = false;

			for ( var t = 0; t < triangles; t++ )
			{
				if ( cuts[t * 4] <= 1 ) continue;

				cuts[t * 4] /= 2;
				any = true;
			}

			if ( !any ) break;
		}

		expanded = false;
		waiting = triangles > 0;

		if ( !waiting ) return;

		cornerBuffer = Fit( cornerBuffer, corners.Count, GpuBuffer.UsageFlags.Structured );
		cutBuffer = Fit( cutBuffer, cuts.Count, GpuBuffer.UsageFlags.Structured );

		if ( vertexBuffer is null || vertexBuffer.ElementCount < vertices )
		{
			vertexBuffer?.Dispose();
			vertexBuffer = new GpuBuffer( Grown( vertices ), PackedVertex.Size, GpuBuffer.UsageFlags.Vertex | GpuBuffer.UsageFlags.Structured, "terrain relief vertices" );
		}

		if ( indexBuffer is null || indexBuffer.ElementCount < indices )
		{
			indexBuffer?.Dispose();
			indexBuffer = new GpuBuffer( Grown( indices ), sizeof( uint ), GpuBuffer.UsageFlags.Index | GpuBuffer.UsageFlags.Structured, "terrain relief indices" );
		}

		cornerBuffer.SetData( corners );
		cutBuffer.SetData( cuts );

		Hold( lift );
	}

	/// <summary>The ring is to be made again where it is - the layers it reads changed.</summary>
	public void Again()
	{
		if ( triangles > 0 ) waiting = true;
	}

	private static int Grown( int count ) => Math.Max( 1024, count + ( count / 2 ) );

	private static GpuBuffer<T> Fit<T>( GpuBuffer<T> had, int count, GpuBuffer.UsageFlags usage ) where T : unmanaged
	{
		if ( had is not null && had.ElementCount >= count ) return had;

		had?.Dispose();
		return new GpuBuffer<T>( Grown( count ), usage, "terrain relief" );
	}

	private static Corner CornerOf( in TerrainSoup.Vertex v, byte[] palette, Vector4 numbers, Span<float> slots )
	{
		TerrainChunkBaker.Slots( v.Paint, palette, slots );

		return new Corner
		{
			Place = new Vector4( v.Position, 0.0f ),
			Normal = new Vector4( v.Normal, 0.0f ),
			Paint = new Vector4( slots[0], slots[1], slots[2], slots[3] ),
			PaintMore = new Vector4( slots[4], slots[5], slots[6], slots[7] ),
			Palette = numbers,
		};
	}

	/// <summary>The numbers picture the layers are read out of.</summary>
	public int ParamsIndex;

	private static ComputeShader Expand => field ??= new ComputeShader( "shaders/engine/terrain_relief.shader", "MainCs" );

	/// <summary>Makes every ring built since the last, between targets before shading, once a turn.</summary>
	internal static void ExpandAll()
	{
		if ( expandedFrame == EngineLoop.LoopFrames || CoreRenderDevice.PortableFrame || !CoreNative.Available ) return;

		expandedFrame = EngineLoop.LoopFrames;

		lock ( live )
		{
			expanding.Clear();
			expanding.AddRange( live );
		}

		Span<ulong> buffers = stackalloc ulong[4];

		foreach ( var one in expanding )
		{
			if ( !one.waiting || one.ParamsIndex == 0 ) continue;

			buffers[0] = one.cornerBuffer.Handle;
			buffers[1] = one.cutBuffer.Handle;
			buffers[2] = one.vertexBuffer.Handle;
			buffers[3] = one.indexBuffer.Handle;

			Expand.Attributes.Set( "Relief", new Vector4( one.triangles, one.ParamsIndex, 0.0f, 0.0f ) );

			if ( !Expand.RunApartOver( buffers, one.triangles, 1, 1 ) ) continue;

			one.waiting = false;
			one.expanded = true;
		}
	}

	public void Dispose()
	{
		lock ( live ) live.Remove( this );

		CoreCardMeshes.Remove( shape );

		cornerBuffer?.Dispose();
		cutBuffer?.Dispose();
		vertexBuffer?.Dispose();
		indexBuffer?.Dispose();
	}
}
