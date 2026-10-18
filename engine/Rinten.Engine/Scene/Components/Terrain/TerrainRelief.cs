using System.Runtime.InteropServices;

namespace Rinten;

//The ring of relief round the eye: the ground's triangles near it cut finer the nearer they are, and lifted along their normal by the
//layers' height maps times their displacement on the card - see terrain_relief.shader - then drawn with the ground's own material over
//the ground, whose corners sink a little under it, by a list on every camera after the depth pre-pass, so the sunk ground under it is never shaded. The full frame alone; collision never sees it.
internal sealed class TerrainRelief : IDisposable
{
	/// <summary>Metres from the eye the lift reaches, the last of which it fades over, and how far the ground's own corners sink under it.</summary>
	public const float Reach = 16.0f;
	public const float Fade = 4.0f;
	public const float Sink = 0.02f;

	//How far the eye goes before the ring is built round it again, in metres.
	private const float Moved = 1.0f;

	//What one ring may come to - a few tens of megabytes at most; past it every triangle is cut coarser until it fits.
	private const int MostVertices = 400_000;

	//What the card reads of a corner: where it is, its normal, and its eight layers' paint.
	[StructLayout( LayoutKind.Sequential )]
	private struct Corner
	{
		public Vector4 Place, Normal, Paint, PaintMore;
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

	//What every camera of the scene runs after the opaque world, and the cameras it is on.
	private readonly Rendering.CommandList list = new( "Terrain relief" );
	private readonly HashSet<CameraComponent> on = new();
	private Material wears;

	private static readonly List<TerrainRelief> live = new();
	private static long expandedFrame = -1;

	/// <summary>Where the eye was, in the terrain's metres, when the ring was built round it.</summary>
	public Vector3 Eye => builtAt;

	/// <summary>Whether there is a ring the card has made to draw.</summary>
	public bool Drawn => expanded && indices > 0;

	public TerrainRelief()
	{
		lock ( live ) live.Add( this );
	}

	/// <summary>The material the ring is drawn with: the ground's own.</summary>
	public Material Wears
	{
		set
		{
			if ( ReferenceEquals( wears, value ) ) return;

			wears = value;
			Record();
		}
	}

	//What the list draws: the ring as it was last built, once the card has made it.
	private void Record()
	{
		list.Reset();

		if ( wears is null || vertexBuffer is null || indexBuffer is null || indices == 0 ) return;

		list.DrawMesh( vertexBuffer, indexBuffer, indices, wears, () => Drawn );
	}

	/// <summary>Puts the list on every camera that draws the scene, the editor's among them, and takes it off those gone. On the main thread.</summary>
	public void Show( Scene scene )
	{
		on.RemoveWhere( camera => !camera.IsValid() );

		foreach ( var camera in scene.GetAll<CameraComponent>() ) Show( camera );

		if ( scene.EditorCamera is { } editor ) Show( editor );
	}

	private void Show( CameraComponent camera )
	{
		if ( !camera.IsValid() || !on.Add( camera ) ) return;

		camera.AddCommandList( list, Rendering.Stage.AfterDepthPrepass, -1000 );
	}

	/// <summary>Whether the eye has gone far enough from where the ring was built that it wants building again.</summary>
	public bool Wants( Vector3 eye ) => !built || eye.Distance( builtAt ) > Moved;

	/// <summary>Builds the ring round the eye out of the grounds near it, all in the terrain's metres. On the main thread.</summary>
	public void Build( Vector3 eye, IEnumerable<TerrainSoup> grounds )
	{
		builtAt = eye;
		built = true;

		corners.Clear();
		cuts.Clear();

		var reach = Reach + Fade;

		foreach ( var ground in grounds )
		{
			var points = ground.Vertices;
			var order = ground.Indices;

			for ( var t = 0; t + 2 < order.Count; t += 3 )
			{
				var a = points[order[t]];
				var b = points[order[t + 1]];
				var c = points[order[t + 2]];

				var near = MathF.Min( a.Position.Distance( eye ), MathF.Min( b.Position.Distance( eye ), c.Position.Distance( eye ) ) );
				if ( near > reach ) continue;

				corners.Add( CornerOf( a ) );
				corners.Add( CornerOf( b ) );
				corners.Add( CornerOf( c ) );

				//Cuts a side by how near it is: a triangle in the fade's last stretch is lifted by nothing and only covers the sunk ground.
				uint n = near < 4.0f ? 16u : near < 8.0f ? 8u : near < 12.0f ? 4u : near < Reach ? 2u : 1u;

				cuts.Add( n );
				cuts.Add( 0 );
				cuts.Add( 0 );
				cuts.Add( 0 );
			}
		}

		triangles = corners.Count / 3;

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

		Record();
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

	private static Corner CornerOf( in TerrainSoup.Vertex v ) => new()
	{
		Place = new Vector4( v.Position, 0.0f ),
		Normal = new Vector4( v.Normal, 0.0f ),
		Paint = new Vector4( v.Layers.r / 255.0f, v.Layers.g / 255.0f, v.Layers.b / 255.0f, v.Layers.a / 255.0f ),
		PaintMore = v.Layers2,
	};

	/// <summary>The numbers picture the layers are read out of.</summary>
	public int ParamsIndex;

	private static ComputeShader Expand => field ??= new ComputeShader( "shaders/engine/terrain_relief.shader", "MainCs" );

	/// <summary>Makes every ring built since the last. Between targets, before anything shades; once a turn of the loop, whichever view asks first.</summary>
	internal static void ExpandAll()
	{
		if ( expandedFrame == EngineLoop.LoopFrames || CoreRenderDevice.PortableFrame || !CoreNative.Available ) return;

		expandedFrame = EngineLoop.LoopFrames;

		TerrainRelief[] all;
		lock ( live ) all = live.ToArray();

		Span<ulong> buffers = stackalloc ulong[4];

		foreach ( var one in all )
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

		foreach ( var camera in on )
			if ( camera.IsValid() ) camera.RemoveCommandList( list );

		on.Clear();

		cornerBuffer?.Dispose();
		cutBuffer?.Dispose();
		vertexBuffer?.Dispose();
		indexBuffer?.Dispose();
	}
}
