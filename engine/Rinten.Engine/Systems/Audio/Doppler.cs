namespace Rinten.Audio;

//Pitch shift from a sound and its listener moving along the line between them, applied to the voice's pitch
internal static class Doppler
{
	//Metres per second, at room temperature
	public const float SpeedOfSound = 343f;

	//Velocities in m/s; scale multiplies them, 0 turns the shift off
	public static float Factor( Vector3 source, Vector3 sourceVelocity, Vector3 listener, Vector3 listenerVelocity, float scale )
	{
		if ( scale <= 0f ) return 1f;

		var dir = listener - source;
		var distance = dir.Length;
		if ( distance < 1f ) return 1f;
		dir /= distance;

		//Speeds toward each other along the line, held under half the speed of sound so the factor stays sane.
		float sourceToward = MathF.Min( Vector3.Dot( sourceVelocity, dir ) * scale, SpeedOfSound * 0.5f );
		float listenerToward = MathF.Min( -Vector3.Dot( listenerVelocity, dir ) * scale, SpeedOfSound * 0.5f );

		return ( ( SpeedOfSound + listenerToward ) / ( SpeedOfSound - sourceToward ) ).Clamp( 0.5f, 2f );
	}
}
