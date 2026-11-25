using System;
using Rinten.Audio;

namespace AudioTests;

[TestClass]
public class BiquadTest
{
	const int Rate = 44100;
	const int Frames = 8192;
	const float Amplitude = 0.5f;

	static float[] Tone( float hz )
	{
		var samples = new float[Frames];
		for ( var i = 0; i < Frames; i++ )
			samples[i] = Amplitude * MathF.Sin( i * 2f * MathF.PI * hz / Rate );

		return samples;
	}

	static float Rms( ReadOnlySpan<float> samples, int skip = 1024 )
	{
		double sum = 0;
		var count = 0;

		for ( var i = skip; i < samples.Length; i++ )
		{
			sum += samples[i] * samples[i];
			count++;
		}

		return (float)Math.Sqrt( sum / Math.Max( 1, count ) );
	}

	static float Run( Biquad filter, float hz )
	{
		var samples = Tone( hz );
		float z1 = 0, z2 = 0;
		filter.Process( samples, ref z1, ref z2 );
		return Rms( samples );
	}

	[TestMethod]
	public void LowPassKeepsLowsAndCutsHighs()
	{
		var low = Run( Biquad.LowPass( 1000, 0.707f, Rate ), 100 );
		var high = Run( Biquad.LowPass( 1000, 0.707f, Rate ), 8000 );

		Assert.IsTrue( high < low * 0.1f, $"8 kHz should be far quieter than 100 Hz: {high} vs {low}" );
	}

	[TestMethod]
	public void HighPassKeepsHighsAndCutsLows()
	{
		var low = Run( Biquad.HighPass( 1000, 0.707f, Rate ), 100 );
		var high = Run( Biquad.HighPass( 1000, 0.707f, Rate ), 8000 );

		Assert.IsTrue( low < high * 0.1f, $"100 Hz should be far quieter than 8 kHz: {low} vs {high}" );
	}

	[TestMethod]
	public void StateDecaysToSilence()
	{
		var samples = new float[Frames];
		samples[0] = 1f;

		float z1 = 0, z2 = 0;

		for ( var offset = 0; offset < Frames; offset += 512 )
			Biquad.LowPass( 500, 0.707f, Rate ).Process( samples.AsSpan( offset, 512 ), ref z1, ref z2 );

		Assert.IsFalse( samples.AsSpan( Frames - 512 ).ToArray().Any( x => float.IsNaN( x ) || float.IsInfinity( x ) ) );
		Assert.IsTrue( MathF.Abs( samples[Frames - 1] ) < 1e-6f );
	}
}
