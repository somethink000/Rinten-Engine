using System.Collections;

namespace HalfEdgeMesh;

partial class Mesh
{
	public VertexData<TData> CreateVertexData<TData>( string name ) where TData : struct =>
		VertexList.CreateDataStream<VertexData<TData>>( name );

	public FaceData<TData> CreateFaceData<TData>( string name ) where TData : struct =>
		FaceList.CreateDataStream<FaceData<TData>>( name );

	public HalfEdgeData<TData> CreateHalfEdgeData<TData>( string name ) where TData : struct =>
		HalfEdgeList.CreateDataStream<HalfEdgeData<TData>>( name );

	//Grows with every change to the vertices, faces or half edges
	internal long Version => VertexList.Version + FaceList.Version + HalfEdgeList.Version;

	//Runs before any change to the vertices, faces or half edges
	internal Action Changing
	{
		set
		{
			VertexList.Changing = value;
			FaceList.Changing = value;
			HalfEdgeList.Changing = value;
		}
	}
}

internal interface IDataStream
{
	internal void Allocate( int sourceIndex );
	internal void AllocateMultiple( int count );
}

internal sealed class VertexData<TData> : ComponentData<TData> where TData : struct
{
	public TData this[VertexHandle h] { get => this[h.Index]; set => this[h.Index] = value; }
}

internal sealed class FaceData<TData> : ComponentData<TData> where TData : struct
{
	public TData this[FaceHandle h] { get => this[h.Index]; set => this[h.Index] = value; }
}

internal sealed class HalfEdgeData<TData> : ComponentData<TData> where TData : struct
{
	public TData this[HalfEdgeHandle h] { get => this[h.Index]; set => this[h.Index] = value; }
}

internal abstract class ComponentData<T> : IEnumerable<T>, IDataStream where T : struct
{
	internal readonly List<T> _list = new();

	//Grows with every change, so a cache of the data can tell it is still current
	internal long Version { get; private set; }

	//Runs before every change, for a value worked out from the data as it stood
	internal Action Changing;

	void Touch()
	{
		Version++;
		Changing?.Invoke();
	}

	public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public int Count => _list.Count;

	public T this[int index]
	{
		get => index >= 0 && index < Count ? _list[index] : default;
		set
		{
			if ( index >= 0 && index < Count )
			{
				Touch();
				_list[index] = value;
			}
		}
	}

	public void CopyFrom( T[] source )
	{
		Touch();

		int count = Math.Min( _list.Count, source.Length );
		for ( int i = 0; i < count; i++ )
			_list[i] = source[i];
	}

	/// <summary>What a new element holds when there is nothing to copy it from.</summary>
	public T Default { get; set; }

	void IDataStream.Allocate( int sourceIndex )
	{
		Touch();
		_list.Add( sourceIndex >= 0 && sourceIndex < Count ? _list[sourceIndex] : Default );
	}

	void IDataStream.AllocateMultiple( int count )
	{
		Touch();
		_list.Capacity += count;
		for ( var i = 0; i < count; i++ )
			_list.Add( Default );
	}
}

internal class ComponentList<T> : IEnumerable<T>
{
	private readonly List<T> _list = new();
	private readonly List<bool> _active = new();
	private readonly Dictionary<string, IDataStream> _streams = new();

	//Grows with every change; see ComponentData.Version
	internal long Version { get; private set; }

	//Runs before every change; see ComponentData.Changing
	internal Action Changing;

	void Touch()
	{
		Version++;
		Changing?.Invoke();
	}

	public int Count => _list.Count;

	public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public IEnumerable<int> ActiveList => Enumerable.Range( 0, Count ).Where( x => _active[x] );

	public T this[int index]
	{
		get => _list[index];
		internal set
		{
			Touch();
			_list[index] = value;
		}
	}

	public int Allocate( T component, int sourceIndex = -1 )
	{
		Touch();
		_list.Add( component );
		_active.Add( true );

		foreach ( var stream in _streams.Values )
			stream.Allocate( sourceIndex );

		return Count - 1;
	}

	public void AllocateMultiple( int count, T component )
	{
		Touch();
		_list.Capacity += count;
		for ( var i = 0; i < count; i++ )
		{
			_list.Add( component );
			_active.Add( true );
		}

		foreach ( var stream in _streams.Values )
			stream.AllocateMultiple( count );
	}

	public void Deallocate( int index )
	{
		Touch();
		_active[index] = false;
	}

	public bool IsAllocated( int index )
	{
		if ( index < 0 || index >= _active.Count )
			return false;

		return _active[index];
	}

	public TDataStream CreateDataStream<TDataStream>( string name ) where TDataStream : IDataStream, new()
	{
		if ( _streams.ContainsKey( name ) )
			throw new ArgumentException( "A stream with the same name already exists.", nameof( name ) );

		var stream = new TDataStream();
		stream.AllocateMultiple( _list.Count );
		_streams[name] = stream;

		return stream;
	}
}
