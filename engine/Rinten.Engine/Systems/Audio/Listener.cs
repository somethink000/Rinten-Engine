namespace Rinten.Audio;

//Listens for sounds in a scene.
internal class Listener : IValid, IDisposable
{
	public bool IsValid => !_destroyed;

	private static readonly List<Listener> _active = [];
	private static readonly List<Listener> _removed = [];

	/// <summary>Currently active listeners.</summary>
	public static IReadOnlyList<Listener> Active => _active.AsReadOnly();

	/// <summary>Listener's that have been removed this frame.</summary>
	public static IReadOnlyList<Listener> Removed => _removed.AsReadOnly();

	//Direct access to active listeners list. Use for iteration without allocation.
	internal static List<Listener> ActiveList => _active;

	//Direct access to removed listeners list. Use for iteration without allocation.
	internal static List<Listener> RemovedList => _removed;

	private Transform _transform;

	/// <summary>Listener's world transform, where we are listening to sounds from.</summary>
	public Transform Transform
	{
		get
		{
			ThreadSafe.AssertIsMainThread();
			return _transform;
		}
		set
		{
			ThreadSafe.AssertIsMainThread();
			_transform = value;
		}
	}

	//Mixer thread safe transform.
	internal Transform MixTransform { get; set; }

	//For the Doppler shift
	internal VelocityTracker Motion;

	/// <summary>Listener's world position, where we are listening to sounds from.</summary>
	public Vector3 Position => Transform.Position;

	//Weak reference to the scene, so we don't prevent GC of the scene when listeners are held in static lists.
	private readonly WeakReference<Scene> _sceneRef;

	/// <summary>Scene this listener belongs to. May return null if the scene has been collected.</summary>
	public Scene Scene => _sceneRef is not null && _sceneRef.TryGetTarget( out var scene ) ? scene : null;

	private bool _destroyed;

	public static readonly Listener Local = new();

	private Listener()
	{
	}

	public Listener( Scene scene )
	{
		_sceneRef = new WeakReference<Scene>( scene );

		_active.Add( this );
	}

	public void Dispose()
	{
		if ( _destroyed )
			return;

		_removed.Add( this );
		_active.Remove( this );

		_destroyed = true;

		GC.SuppressFinalize( this );
	}

	~Listener()
	{
		//The listener lists are the main thread's.
		using var finalizing = MainThread.InFinalizer();
		MainThread.Queue( Dispose );
	}

	public static void Clear()
	{
		_active.Clear();
		_removed.Clear();
	}
}
