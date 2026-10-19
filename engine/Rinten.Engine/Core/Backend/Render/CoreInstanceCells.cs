using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten;

//Instance table grouped by world grid cell, so the cull tests cells first; moving instances share one
internal sealed class CoreInstanceCells
{
	//The cell the moving go in.
	private const long Moving = long.MinValue;

	//One cell: who is in it, and its bounds from them.
	private sealed class Cell
	{
		public readonly List<int> Slots = new();
		public bool Moving;
		public bool BoundsDirty = true;
		public System.Numerics.Vector3 Low, High;

		/// <summary>The work items its instances can hand the cluster stage in one view, all of them - see CoreSceneTables.ChunksOf.</summary>
		public long Chunks;
	}

	private readonly Dictionary<long, Cell> cells = new();

	//Each slot's cell, and where it stands in that cell's list.
	private Cell[] cellOf = System.Array.Empty<Cell>();
	private int[] placeOf = System.Array.Empty<int>();
	private System.Numerics.Vector4[] sphereOf = System.Array.Empty<System.Numerics.Vector4>();
	private int[] chunksOf = System.Array.Empty<int>();

	//At the last layout, each cell's sphere (a radius below nought taken by every view) and its work - what bounds the cull's work list, see ChunksSeen.
	private System.Numerics.Vector4[] laidSpheres = System.Array.Empty<System.Numerics.Vector4>();
	private long[] laidChunks = System.Array.Empty<long>();

	private bool changed = true;
	private uint[] written = System.Array.Empty<uint>();

	public ulong Buffer { get; private set; }
	private long bufferBytes;

	/// <summary>How many cells there are, the moving one among them.</summary>
	public int Count { get; private set; }

	/// <summary>How many times the cells have been laid out, for the report.</summary>
	public static long Layouts;

	/// <summary>A slot whose record changed: where it is filed now.</summary>
	public void Note( int slot, in CoreSceneTables.InstanceRecord record, CoreSceneTables.Entry entry )
	{
		var drawn = ( record.Flags & CoreMeshShaders.InstanceDrawn ) != 0;
		var key = record.Moving != 0 ? Moving
			: WorldGrid.CellFor( new Vector3( record.Sphere.X, record.Sphere.Y, record.Sphere.Z ), record.Sphere.W ).Key;

		Note( slot, key, drawn, record.Sphere, entry?.CountedChunks ?? 0 );
	}

	/// <summary>A run's slots, every one filed by the sphere they all stand inside - see CoreSceneTables.AddRun.</summary>
	public void NoteRun( int head, int count, System.Numerics.Vector4 sphere, bool drawn, int chunks )
	{
		var key = WorldGrid.CellFor( new Vector3( sphere.X, sphere.Y, sphere.Z ), sphere.W ).Key;

		for ( var slot = head; slot < head + count; slot++ ) Note( slot, key, drawn, sphere, chunks );
	}

	private void Note( int slot, long key, bool drawn, System.Numerics.Vector4 sphere, int chunks )
	{
		if ( slot >= cellOf.Length )
		{
			var size = System.Math.Max( slot + 1, System.Math.Max( 256, cellOf.Length * 2 ) );

			System.Array.Resize( ref cellOf, size );
			System.Array.Resize( ref placeOf, size );
			System.Array.Resize( ref sphereOf, size );
			System.Array.Resize( ref chunksOf, size );
		}

		var was = cellOf[slot];
		Cell now = null;

		if ( drawn && !cells.TryGetValue( key, out now ) )
		{
			now = new Cell { Moving = key == Moving };
			cells[key] = now;
		}

		if ( !ReferenceEquals( was, now ) )
		{
			if ( was is not null ) Take( slot, was );

			if ( now is not null )
			{
				placeOf[slot] = now.Slots.Count;
				now.Slots.Add( slot );
				now.BoundsDirty = true;
				now.Chunks += chunks;
				chunksOf[slot] = chunks;
			}

			cellOf[slot] = now;
			changed = true;
		}
		else if ( now is not null && chunksOf[slot] != chunks )
		{
			now.Chunks += chunks - chunksOf[slot];
			chunksOf[slot] = chunks;
			changed = true;
		}

		if ( now is not null && ReferenceEquals( was, now ) && !now.Moving && sphereOf[slot] != sphere )
		{
			now.BoundsDirty = true;
			changed = true;
		}

		sphereOf[slot] = sphere;
	}

	//Takes a slot out of its cell: the last of the cell's list stands where it stood.
	private void Take( int slot, Cell cell )
	{
		var at = placeOf[slot];
		var last = cell.Slots[^1];

		cell.Slots[at] = last;
		placeOf[last] = at;
		cell.Slots.RemoveAt( cell.Slots.Count - 1 );
		cell.BoundsDirty = true;
		cell.Chunks -= chunksOf[slot];
		chunksOf[slot] = 0;
	}

	/// <summary>Lays the cells out again and sends them up, where anything said to has changed.</summary>
	public void Update( int used )
	{
		if ( !changed ) return;
		changed = false;
		Layouts++;

		var instances = 0;
		Count = 0;

		foreach ( var cell in cells.Values )
		{
			if ( cell.Slots.Count == 0 ) continue;

			Count++;
			instances += cell.Slots.Count;

			if ( !cell.BoundsDirty || cell.Moving ) continue;
			cell.BoundsDirty = false;

			var low = new System.Numerics.Vector3( float.MaxValue );
			var high = new System.Numerics.Vector3( float.MinValue );

			foreach ( var slot in cell.Slots )
			{
				var s = sphereOf[slot];
				var centre = new System.Numerics.Vector3( s.X, s.Y, s.Z );
				low = System.Numerics.Vector3.Min( low, centre - new System.Numerics.Vector3( s.W ) );
				high = System.Numerics.Vector3.Max( high, centre + new System.Numerics.Vector3( s.W ) );
			}

			cell.Low = low;
			cell.High = high;
		}

		//Two uint4 a cell, then the instances, four to a uint4.
		var words = ( Count * 8 ) + ( ( instances + 3 ) & ~3 );
		if ( written.Length < words ) written = new uint[words + ( words / 4 )];

		var c = 0;
		var at = Count * 8;

		if ( laidSpheres.Length < Count )
		{
			laidSpheres = new System.Numerics.Vector4[Count + ( Count / 4 )];
			laidChunks = new long[laidSpheres.Length];
		}

		foreach ( var cell in cells.Values )
		{
			if ( cell.Slots.Count == 0 ) continue;

			var middle = ( cell.Low + cell.High ) * 0.5f;

			//The moving cell is taken whatever is looking.
			var radius = cell.Moving ? -1.0f : ( cell.High - cell.Low ).Length() * 0.5f;

			written[( c * 8 ) + 0] = System.BitConverter.SingleToUInt32Bits( middle.X );
			written[( c * 8 ) + 1] = System.BitConverter.SingleToUInt32Bits( middle.Y );
			written[( c * 8 ) + 2] = System.BitConverter.SingleToUInt32Bits( middle.Z );
			written[( c * 8 ) + 3] = System.BitConverter.SingleToUInt32Bits( radius );
			written[( c * 8 ) + 4] = (uint)( at - ( Count * 8 ) );
			written[( c * 8 ) + 5] = (uint)cell.Slots.Count;

			laidSpheres[c] = new System.Numerics.Vector4( middle, radius );
			laidChunks[c] = cell.Chunks;

			var run = CollectionsMarshal.AsSpan( cell.Slots );
			MemoryMarshal.Cast<int, uint>( run ).CopyTo( written.AsSpan( at ) );
			at += run.Length;
			c++;
		}

		var bytes = (long)words * 4;

		if ( Buffer == 0 || bufferBytes < bytes )
		{
			if ( Buffer != 0 ) CoreRenderDevice.DestroyBuffer( Buffer );

			bufferBytes = System.Math.Max( 4096, bytes + ( bytes / 4 ) );
			Buffer = CoreRenderDevice.ReserveBuffer( bufferBytes );
		}

		if ( Buffer == 0 ) { Count = 0; return; }

		CoreRenderDevice.FrameWrite( Buffer, 0, MemoryMarshal.AsBytes( written.AsSpan( 0, words ) ) );
	}

	//Upper bound on cluster-stage work items: each cell's work per view that sees its sphere
	internal long ChunksSeen( System.ReadOnlySpan<CoreMeshRender.ViewSetup> views )
	{
		var sum = 0L;

		for ( var c = 0; c < Count && c < laidSpheres.Length; c++ )
		{
			var sphere = laidSpheres[c];
			var reach = ( sphere.W * 1.01f ) + 0.01f;

			foreach ( ref readonly var view in views )
			{
				if ( sphere.W < 0.0f || Inside( view, sphere, reach ) ) sum += laidChunks[c];
			}
		}

		return sum;
	}

	private static bool Inside( in CoreMeshRender.ViewSetup view, System.Numerics.Vector4 sphere, float reach )
	{
		var centre = new System.Numerics.Vector3( sphere.X, sphere.Y, sphere.Z );

		static bool Behind( System.Numerics.Vector4 plane, System.Numerics.Vector3 at, float reach )
			=> ( ( plane.X * at.X ) + ( plane.Y * at.Y ) + ( plane.Z * at.Z ) + plane.W ) < -reach;

		return !Behind( view.Plane0, centre, reach ) && !Behind( view.Plane1, centre, reach ) && !Behind( view.Plane2, centre, reach ) &&
			!Behind( view.Plane3, centre, reach ) && !Behind( view.Plane4, centre, reach ) && !Behind( view.Plane5, centre, reach );
	}

	/// <summary>Forgets the lot - every slot is noted again before the next layout.</summary>
	public void Reset()
	{
		cells.Clear();
		System.Array.Clear( cellOf );
		changed = true;
	}

	public void Release()
	{
		if ( Buffer != 0 ) CoreRenderDevice.DestroyBuffer( Buffer );
		Buffer = 0;
		bufferBytes = 0;
		Count = 0;
		changed = true;
	}
}
