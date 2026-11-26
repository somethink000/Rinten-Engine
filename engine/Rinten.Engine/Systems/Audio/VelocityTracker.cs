namespace Rinten.Audio;

//Smoothed velocity from positions; a one-update jump (teleport, camera cut) is ignored, one that holds is taken
internal struct VelocityTracker
{
	const float HalfLife = 0.03f;

	//A change of speed between two updates bigger than this isn't movement: about 50 m/s, thousands of g at 60 fps
	const float MaxJump = 50f;

	Vector3 _last, _lastRaw;
	double _lastTime;
	bool _started;

	public Vector3 Velocity { get; private set; }

	//Until a speed is measured, Velocity is zero and means nothing
	public bool Known { get; private set; }

	public void Update( Vector3 position, double time )
	{
		if ( !_started )
		{
			_started = true;
			_last = position;
			_lastTime = time;
			return;
		}

		float dt = (float)( time - _lastTime );
		if ( dt <= 0f ) return;

		var velocity = ( position - _last ) / dt;
		_last = position;
		_lastTime = time;

		bool jump = ( velocity - _lastRaw ).Length > MaxJump;
		_lastRaw = velocity;
		if ( jump ) return;

		Velocity = Known ? Vector3.Lerp( Velocity, velocity, 1f - MathF.Pow( 0.5f, dt / HalfLife ) ) : velocity;
		Known = true;
	}
}
