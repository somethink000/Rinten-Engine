using System.Runtime.InteropServices;

namespace Rinten;

//The relief ring: ground near the eye cut finer the nearer and lifted by layer heights (terrain_relief.shader), drawn
//as a card mesh (CoreCardMeshes) with the ground's material so it casts. Full frame only; collision never sees it
internal sealed class TerrainRelief : IDisposable
{
	/// <summary>How far the ground's corners sink under the ring, in metres.</summary>
	public const float Sink = 0.02f;

	//The share of the ring's distance its lift fades over at the far edge
	private const float FadeShare = 0.25f;

	/// <summary>Metres the laid ring lifts in full, and fades over beyond that - see Terrain.ReliefDistance.</summary>
	public float Reach => builtDistance * ( 1.0f - FadeShare );
	public float Fade => builtDistance * FadeShare;
	private float builtDistance;

	//How far the eye goes before the ring is built round it again, in metres.
	private const float Moved = 1.0f;

	//What one ring may come to - a few tens of megabytes at most; past it every triangle is cut coarser until it fits.
	private const int MostVertices = 400_000;

	//Pixels a cut's side aims to cover on screen - finer, the ground seen edge-on is shaded in quads that are mostly outside
	//their triangles, and drawn again into every depth and shadow pass - and the shortest a cut gets, in metres, however
	//coarse the ground: a terrain made from a height map has sides of metres
	private const float CutPixels = 16.0f;
	private const float LeastCut = 0.05f;

	//What the card reads of a corner: where it is, its normal, its chunk's eight slots' paint, and which layers those are.
	[StructLayout( LayoutKind.Sequential )]
	private struct Corner
	{
		public Vector4 Place, Normal, Paint, PaintMore, Palette;
	}

	//A ring as a worker lays it out: the eye it is round, its corners and cuts, and what they come to
	private sealed class Laid
	{
		public Vector3 Eye;
		public float Pixels, Distance;
		public readonly List<Corner> Corners = new();
		public readonly List<uint> Cuts = new();
		public int Triangles, Vertices, Indices;
		public BBox Box;
	}

	//One at a time: written by the worker while laying is out, then read until the frame has written it to the card (unsent)
	private readonly Laid laid = new();
	private System.Threading.Tasks.Task laying;
	private bool unsent;

	private Vector3 askedAt;
	private float askedPixels, askedDistance;
	private bool asked;

	private GpuBuffer<Corner> cornerBuffer;
	private GpuBuffer<uint> cutBuffer;
	private GpuBuffer vertexBuffer, indexBuffer;

	private int triangles, vertices, indices;

	//Built and waiting for the card, and what the card last made of it: the ring is drawn only once it has.
	private bool waiting, expanded;

	private Vector3 builtAt;

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

	/// <summary>Metres of a cut's side a metre from the eye, for the ring on the card - see terrain_relief.shader.</summary>
	public float CutPerMetre => builtPixels > 0.0f ? CutPixels / builtPixels : 0.0f;
	private float builtPixels;

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

	/// <summary>Whether the eye has gone far enough, or the screen changed enough, that the ring wants building again.</summary>
	public bool Wants( Vector3 eye, float pixels, float distance ) => !asked || eye.Distance( askedAt ) > Moved ||
		MathF.Abs( pixels - askedPixels ) > askedPixels * 0.1f || distance != askedDistance;

	/// <summary>Whether a ring is being laid out on a worker now, or is laid out and not yet written to the card.</summary>
	public bool Laying => laying is not null || unsent;

	/// <summary>Lays the ring out on a worker round the eye, for pixels a metre a metre away; Take puts it on the card.</summary>
	public void Build( Vector3 eye, float pixels, float distance, (TerrainSoup Ground, byte[] Palette)[] grounds )
	{
		if ( Laying ) return;

		askedAt = eye;
		askedPixels = pixels;
		askedDistance = distance;
		asked = true;

		laying = System.Threading.Tasks.Task.Run( () => Lay( laid, eye, pixels, distance, grounds ) );
	}

	/// <summary>Puts a ring the worker finished on the card; true when the ring changed, so its place is written again.</summary>
	public bool Take()
	{
		if ( laying is not { IsCompleted: true } done ) return false;

		laying = null;

		if ( done.IsFaulted )
		{
			Log.Warning( done.Exception, "Terrain relief ring was not laid out" );
			return false;
		}

		builtAt = laid.Eye;
		builtPixels = laid.Pixels;
		builtDistance = laid.Distance;

		triangles = laid.Triangles;
		vertices = laid.Vertices;
		indices = laid.Indices;
		box = laid.Box;

		expanded = false;
		waiting = triangles > 0;

		if ( !waiting )
		{
			Hold( lift );
			return true;
		}

		cornerBuffer = Fit( cornerBuffer, laid.Corners.Count, GpuBuffer.UsageFlags.Structured );
		cutBuffer = Fit( cutBuffer, laid.Cuts.Count, GpuBuffer.UsageFlags.Structured );

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

		//Written by the frame in its own order rather than here - see ExpandAll.
		unsent = true;

		Hold( lift );
		return true;
	}

	//The ring's corners and cuts out of the grounds within reach of the eye, on a worker: nothing here touches the scene or the card
	private static void Lay( Laid into, Vector3 eye, float pixels, float distance, (TerrainSoup Ground, byte[] Palette)[] grounds )
	{
		var corners = into.Corners;
		var cuts = into.Cuts;

		into.Eye = eye;
		into.Pixels = pixels;
		into.Distance = distance;
		corners.Clear();
		cuts.Clear();

		var full = distance * ( 1.0f - FadeShare );
		var reachSquared = distance * distance;

		Span<float> slots = stackalloc float[Terrain.MaxLayers];

		foreach ( var (ground, palette) in grounds )
		{
			var points = CollectionsMarshal.AsSpan( ground.Vertices );
			var order = CollectionsMarshal.AsSpan( ground.Indices );
			var numbers = TerrainChunkBaker.PaletteNumbers( palette );

			for ( var t = 0; t + 2 < order.Length; t += 3 )
			{
				ref readonly var a = ref points[order[t]];
				ref readonly var b = ref points[order[t + 1]];
				ref readonly var c = ref points[order[t + 2]];

				var nearSquared = MathF.Min( ( a.Position - eye ).LengthSquared,
					MathF.Min( ( b.Position - eye ).LengthSquared, ( c.Position - eye ).LengthSquared ) );

				if ( nearSquared > reachSquared ) continue;

				var near = MathF.Sqrt( nearSquared );

				corners.Add( CornerOf( a, palette, numbers, slots ) );
				corners.Add( CornerOf( b, palette, numbers, slots ) );
				corners.Add( CornerOf( c, palette, numbers, slots ) );

				//Cut so a side covers CutPixels where it is nearest the eye, across and - seen at a slant, by how squarely it faces
				//the eye - shorter the other way, the two met halfway; one in the fade's last stretch is lifted by nothing
				var side = MathF.Sqrt( MathF.Max( ( b.Position - a.Position ).LengthSquared,
					MathF.Max( ( c.Position - b.Position ).LengthSquared, ( a.Position - c.Position ).LengthSquared ) ) );

				var away = MathF.Max( near, 0.5f );
				var facing = Vector3.Cross( b.Position - a.Position, c.Position - a.Position ).Normal;
				var square = MathF.Abs( Vector3.Dot( facing, ( eye - ( ( a.Position + b.Position + c.Position ) / 3.0f ) ).Normal ) );
				var across = side * pixels / away * MathF.Sqrt( MathF.Max( square, 0.05f ) );

				var n = near >= full ? 1u : (uint)Math.Clamp( MathF.Ceiling( across / CutPixels ), 1.0f, MathF.Max( MathF.Ceiling( side / LeastCut ), 1.0f ) );

				cuts.Add( n );
				cuts.Add( 0 );
				cuts.Add( 0 );
				cuts.Add( 0 );
			}
		}

		var triangles = corners.Count / 3;
		var box = default( BBox );

		for ( var k = 0; k < corners.Count; k++ )
		{
			var at = new Vector3( corners[k].Place.x, corners[k].Place.y, corners[k].Place.z );
			box = k == 0 ? new BBox( at, at ) : box.AddPoint( at );
		}

		var cutSpan = CollectionsMarshal.AsSpan( cuts );

		//Where each triangle's vertices and indices start, coarser all round until the whole ring fits.
		while ( true )
		{
			long v = 0, i = 0;

			for ( var t = 0; t < triangles; t++ )
			{
				var n = (long)cutSpan[t * 4];

				cutSpan[( t * 4 ) + 1] = (uint)v;
				cutSpan[( t * 4 ) + 2] = (uint)i;

				v += ( n + 1 ) * ( n + 2 ) / 2;
				i += 3 * n * n;
			}

			if ( v <= MostVertices || triangles == 0 )
			{
				into.Vertices = (int)v;
				into.Indices = (int)i;
				break;
			}

			var any = false;

			for ( var t = 0; t < triangles; t++ )
			{
				if ( cutSpan[t * 4] <= 1 ) continue;

				cutSpan[t * 4] /= 2;
				any = true;
			}

			if ( !any ) break;
		}

		into.Triangles = triangles;
		into.Box = box;
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

			//In the frame being recorded: a write ahead of it is a submit of its own, and a few of those in a row wait on the card
			if ( one.unsent )
			{
				one.cornerBuffer.SetData( one.laid.Corners );
				one.cutBuffer.SetData( one.laid.Cuts );
				one.unsent = false;
			}

			buffers[0] = one.cornerBuffer.Handle;
			buffers[1] = one.cutBuffer.Handle;
			buffers[2] = one.vertexBuffer.Handle;
			buffers[3] = one.indexBuffer.Handle;

			Expand.Attributes.Set( "Relief", new Vector4( one.triangles, one.ParamsIndex, one.vertices, one.indices / 3 ) );

			if ( !Expand.RunApartOver( buffers, one.vertices + ( one.indices / 3 ), 1, 1 ) ) continue;

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
