using System;
using Rinten.Audio;

namespace AudioTests;

[TestClass]
public class MasterLimiterTest
{
	static readonly int Delay = MasterLimiter.Lookahead - 1;
	const int Frames = 512 * 87;

	static float[][] Run( Func<int, float> signal )
	{
		var limiter = new MasterLimiter();
		var output = new[] { new float[Frames], new float[Frames] };

		for ( var start = 0; start < Frames; start += AudioEngine.MixBufferSize )
		{
			var buffer = new[] { new float[AudioEngine.MixBufferSize], new float[AudioEngine.MixBufferSize] };
			for ( var i = 0; i < AudioEngine.MixBufferSize; i++ )
				buffer[0][i] = buffer[1][i] = signal( start + i );

			limiter.Process( buffer, AudioEngine.MixBufferSize );

			for ( var i = 0; i < AudioEngine.MixBufferSize; i++ )
			{
				output[0][start + i] = buffer[0][i];
				output[1][start + i] = buffer[1][i];
			}
		}

		return output;
	}

	static float Sine( int i, float amplitude ) => amplitude * MathF.Sin( i * 2f * MathF.PI * 220f / AudioEngine.SamplingRate );

	[TestMethod]
	public void QuietPassesUntouched()
	{
		var output = Run( i => Sine( i, 0.5f ) );

		for ( var i = Delay; i < Frames; i++ )
			Assert.AreEqual( Sine( i - Delay, 0.5f ), output[0][i], 1e-5f );
	}

	[TestMethod]
	public void LoudNeverPassesCeilingAndRampsSmoothly()
	{
		//Silence, then a hit twice over full scale, then quiet again.
		static float Signal( int i ) => i < 10000 ? Sine( i, 0.3f ) : i < 20000 ? Sine( i, 2f ) : Sine( i, 0.3f );

		var output = Run( Signal );

		var peak = 0f;
		for ( var i = 0; i < Frames; i++ ) peak = MathF.Max( peak, MathF.Abs( output[0][i] ) );
		Assert.IsTrue( peak <= MasterLimiter.Ceiling + 1e-4f, $"peak {peak}" );

		//Gain is output over input, it never jumps by more than one lookahead step.
		var last = 1f;
		var lastIndex = Delay;

		for ( var i = Delay; i < Frames; i++ )
		{
			var input = Signal( i - Delay );
			if ( MathF.Abs( input ) < 0.05f ) continue;

			var gain = output[0][i] / input;
			Assert.IsTrue( MathF.Abs( gain - last ) <= ( i - lastIndex ) / (float)MasterLimiter.Lookahead + 1e-3f, $"gain jump at {i}: {last} -> {gain}" );
			last = gain;
			lastIndex = i;
		}

		//Back to full level half a second after the hit.
		var after = 20000 + Delay + 22050;
		Assert.AreEqual( Sine( after - Delay, 0.3f ), output[0][after], 0.01f );
	}
}
