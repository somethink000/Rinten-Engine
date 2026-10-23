namespace Rinten.Audio;

/// <summary>Just a test - don't count on this sticking around</summary>
[Expose]
public sealed class HighPassProcessor : AudioProcessor<HighPassProcessor.State>
{
	/// <summary>Cutoff frequency of the high-pass filter (0 to 1, where 1 is Nyquist frequency).</summary>
	[Range( 0, 1 )]
	public float Cutoff { get; set; } = 0.5f;

	public class State : ListenerState
	{
		internal PerChannel<float> PreviousInput;
		internal PerChannel<float> PreviousOutput;
	}

	//Processes each channel individually using a simple one-pole high-pass filter.
	protected override void ProcessSingleChannel( AudioChannel channel, Span<float> input )
	{
		if ( input.Length == 0 ) return;

		//An RC high-pass: y = a * ( y' + x - x' ), with a = RC / ( RC + dt ) for the cutoff as a share of Nyquist
		var alpha = 1.0f - LowPassProcessor.Smoothing( Cutoff );
		var prevIn = CurrentState.PreviousInput.Get( channel );
		var prevOut = CurrentState.PreviousOutput.Get( channel );

		for ( var i = 0; i < input.Length; i++ )
		{
			var current = input[i];
			prevOut = alpha * ( prevOut + current - prevIn );
			prevIn = current;
			input[i] = prevOut;
		}

		CurrentState.PreviousInput.Set( channel, prevIn );
		CurrentState.PreviousOutput.Set( channel, prevOut );
	}
}
