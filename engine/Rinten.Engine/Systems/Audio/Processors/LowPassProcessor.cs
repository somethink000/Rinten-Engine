namespace Rinten.Audio;

/// <summary>Just a test - don't count on this sticking around</summary>
[Expose]
public sealed class LowPassProcessor : AudioProcessor<LowPassProcessor.State>
{
	/// <summary>Cutoff frequency for the low-pass filter (normalized 0 to 1).</summary>
	[Range( 0, 1 )]
	public float Cutoff { get; set; } = 0.5f;

	public class State : ListenerState
	{
		internal PerChannel<float> PreviousSample;
	}

	//One pole, per sample: the recursion is serial, so lanes of a vector would each be a filter of every eighth sample
	protected override void ProcessSingleChannel( AudioChannel channel, Span<float> input )
	{
		var alpha = Smoothing( Cutoff );
		var previous = CurrentState.PreviousSample.Get( channel );

		for ( var i = 0; i < input.Length; i++ )
		{
			previous += alpha * ( input[i] - previous );
			input[i] = previous;
		}

		CurrentState.PreviousSample.Set( channel, previous );
	}

	//How far each sample moves toward the input for a cutoff given as a share of Nyquist - an RC filter's dt / ( RC + dt )
	internal static float Smoothing( float cutoff )
	{
		var frequency = Math.Clamp( cutoff, 0.0f, 1.0f ) * 0.5f;
		var rc = 1.0f / ( 2.0f * MathF.PI * MathF.Max( frequency, 1e-6f ) );

		return 1.0f / ( rc + 1.0f );
	}
}
