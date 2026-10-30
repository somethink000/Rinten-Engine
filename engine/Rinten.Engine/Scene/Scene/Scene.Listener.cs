namespace Rinten;

partial class Scene
{
	//Default scene sound listener, at the camera.
	internal Audio.Listener Listener { get; set; }

	//Multiple scene sound listeners, created by audio listener components.
	internal HashSet<Audio.Listener> Listeners { get; private set; }

	//Update default sound listener to camera.
	private void UpdateDefaultListener()
	{
		//default sound listener, it might get overriden anyway
		Listener ??= new( this );

		//In a headset the ears are where the head is and turn the way it turns - the camera keeps only its
		//yaw there, and a sound that stayed put while the head turned would be the first thing to feel wrong.
		Listener.Transform = Game.IsRunningInVR && Input.VR is not null
			? Input.VR.Head.WithScale( 1 )
			: new( Camera.WorldPosition, Camera.WorldRotation );
	}

	//Dispose any sound listeners we created.
	private void DisposeListeners()
	{
		Listener?.Dispose();
		Listener = default;

		if ( Listeners is not null )
		{
			foreach ( var listener in Listeners )
			{
				listener?.Dispose();
			}

			Listeners.Clear();
			Listeners = default;
		}
	}

	//Add a new sound listener and return it.
	internal Audio.Listener AddListener()
	{
		Listeners ??= new();
		var listener = new Audio.Listener( this );
		Listeners.Add( listener );
		return listener;
	}

	//Remove and dispose a scene sound listener.
	internal void RemoveListener( Audio.Listener listener )
	{
		if ( listener is null )
			return;

		if ( Listeners.Remove( listener ) )
		{
			listener?.Dispose();
		}
	}

	//The first valid listener, else the scene's own
	internal Transform GetPrimaryListenerTransform()
	{
		if ( Listeners is { Count: > 0 } )
		{
			foreach ( var listener in Listeners )
			{
				if ( listener.IsValid() )
					return listener.Transform;
			}
		}

		return Listener?.Transform ?? global::Transform.Zero;
	}

	//Find the closest sound listener at this point.
	internal Audio.Listener FindClosestListener( Vector3 point )
	{
		if ( Listeners is null || Listeners.Count == 0 )
			return Listener;

		var result = Listener;
		var bestDistance = result is null ? float.MaxValue : Vector3.DistanceBetweenSquared( result.Position, point );

		foreach ( var listener in Listeners )
		{
			var distance = Vector3.DistanceBetweenSquared( listener.Position, point );

			if ( distance < bestDistance )
			{
				bestDistance = distance;
				result = listener;
			}
		}

		return result;
	}
}
