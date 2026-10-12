using System.Runtime.InteropServices;
using System.Threading;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//The bake's denoiser on the card (bake_denoise.slang): joint non-local means, a square of the
//sheet at a time, in regions small enough that no dispatch comes near what a driver calls hung
internal static class BakeDenoise
{
	//Half the search window, and the bandwidths of each guide; the light's is in its noise's standard deviations at
	//the default strength of three - twice Rousselle's, as a lightmap's bounce is smooth over far more than a pixel's
	private const int HalfSearch = 10;
	private const float Spatial = 5.0f;
	private const float LightBand = 0.9f;
	private const float AlbedoBand = 1.0f;
	private const float NormalBand = 0.1f;

	//Texels a side a dispatch takes
	private const int Region = 64;

	private static ulong pipeline;
	private static int pipelineBuilt = -1;

	//Denoises one square of light in place, width by height; albedo's w is one where a texel was inside something, a
	//normal of nought marks a texel no surface owns, variance is each texel's mean's. False where the card would not
	internal static bool Run( Vec3[] light, Vec3[] bands, Vec4[] albedo, Vec3[] normal, float[] variance, int width, int height,
		float strength, CancellationToken cancel )
	{
		var count = width * height;
		if ( count == 0 || !BakeGpu.Available ) return false;

		//The bandwidths are for light near one: brought there by the square's mean, and back after
		var sum = 0.0f;
		var lit = 0;

		for ( var i = 0; i < count; ++i )
		{
			if ( normal[i] == Vec3.Zero ) continue;

			sum += BakeMath.Luma( light[i] );
			lit++;
		}

		var scale = lit > 0 && sum > 1e-8f ? lit / sum : 1.0f;

		var lightIn = new Vec4[count];
		var bandsIn = new Vec4[count * 3];

		for ( var i = 0; i < count * 3; ++i ) bandsIn[i] = new Vec4( bands[i] * scale, 0.0f );

		var albedoIn = new Vec4[count];
		var normalIn = new Vec4[count];

		for ( var i = 0; i < count; ++i )
		{
			lightIn[i] = new Vec4( light[i] * scale, variance[i] * scale * scale );
			albedoIn[i] = albedo[i];
			normalIn[i] = new Vec4( normal[i], 0.0f );
		}

		ulong lightBuffer = 0, albedoBuffer = 0, normalBuffer = 0, outBuffer = 0, bandsBuffer = 0, bandsOutBuffer = 0, built = 0;
		var made = false;

		BakeGpu.OnMain( () =>
		{
			built = EnsurePipeline();
			if ( built == 0 ) return;

			lightBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( lightIn.AsSpan() ) );
			albedoBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( albedoIn.AsSpan() ) );
			normalBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( normalIn.AsSpan() ) );
			outBuffer = CoreRenderDevice.CreateBuffer( new byte[count * 16] );
			bandsBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( bandsIn.AsSpan() ) );
			bandsOutBuffer = CoreRenderDevice.CreateBuffer( new byte[count * 48] );

			made = lightBuffer != 0 && albedoBuffer != 0 && normalBuffer != 0 && outBuffer != 0 && bandsBuffer != 0 && bandsOutBuffer != 0;
		}, CancellationToken.None );

		try
		{
			if ( !made ) return false;

			for ( var y = 0; y < height; y += Region )
			{
				for ( var x = 0; x < width; x += Region )
				{
					cancel.ThrowIfCancellationRequested();

					var ok = false;
					var (rx, ry) = (x, y);

					BakeGpu.OnMain( () =>
					{
						if ( !CoreGpuWork.Begin( out var own ) ) return;

						try
						{
							Span<uint> push = stackalloc uint[12];
							push[0] = (uint)width;
							push[1] = (uint)height;
							push[2] = (uint)rx;
							push[3] = (uint)ry;
							push[4] = HalfSearch;
							push[5] = BitConverter.SingleToUInt32Bits( Spatial );
							push[6] = BitConverter.SingleToUInt32Bits( LightBand * strength / 3.0f );
							push[7] = BitConverter.SingleToUInt32Bits( AlbedoBand );
							push[8] = BitConverter.SingleToUInt32Bits( NormalBand );
							push[9] = 0;
							push[10] = 0;
							push[11] = 0;

							Span<ulong> buffers = stackalloc ulong[6] { lightBuffer, albedoBuffer, normalBuffer, outBuffer, bandsBuffer, bandsOutBuffer };
							var side = Region / 8;

							ok = CoreRenderDevice.Dispatch( built, buffers, side, side, 1, MemoryMarshal.AsBytes( push ) );
						}
						finally
						{
							CoreGpuWork.End( own );
						}
					}, cancel );

					if ( !ok ) return false;
				}
			}

			var back = Read( outBuffer, count, cancel );
			var bandsBack = Read( bandsOutBuffer, count * 3, cancel );
			if ( back is null || bandsBack is null ) return false;

			for ( var i = 0; i < count; ++i )
			{
				if ( normal[i] == Vec3.Zero ) continue;

				light[i] = new Vec3( back[i].X, back[i].Y, back[i].Z ) / scale;

				for ( var c = 0; c < 3; ++c )
				{
					var b = bandsBack[( i * 3 ) + c];
					bands[( i * 3 ) + c] = new Vec3( b.X, b.Y, b.Z ) / scale;
				}
			}

			return true;
		}
		finally
		{
			BakeGpu.OnMain( () =>
			{
				foreach ( var buffer in new[] { lightBuffer, albedoBuffer, normalBuffer, outBuffer, bandsBuffer, bandsOutBuffer } )
					if ( buffer != 0 ) CoreRenderDevice.DestroyBuffer( buffer );
			}, CancellationToken.None, wait: false );
		}
	}

	//A buffer of float4s back off the card, once everything asked of it is done; null where it never came
	private static Vec4[] Read( ulong buffer, int count, CancellationToken cancel )
	{
		var back = new Vec4[count];
		var arrived = new ManualResetEventSlim();
		var asked = false;

		BakeGpu.OnMain( () =>
		{
			asked = CoreReadbacks.Ask<Vec4>( buffer, 0, (long)count * 16, span =>
			{
				span[..Math.Min( span.Length, back.Length )].CopyTo( back );
				arrived.Set();
			} );
		}, CancellationToken.None );

		return asked && arrived.Wait( TimeSpan.FromMinutes( 5 ), cancel ) ? back : null;
	}

	private static ulong EnsurePipeline()
	{
		if ( pipeline != 0 && pipelineBuilt == CoreShaderCache.Revision ) return pipeline;
		if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );

		pipelineBuilt = CoreShaderCache.Revision;

		var code = CoreShaderCache.Pass( EngineShaders.BakeDenoise, "main", CoreShaderCompiler.Stage.Compute );
		pipeline = code is null ? 0 : CoreRenderDevice.CreateCompute( code );

		if ( pipeline == 0 ) Log.Warning( $"baked lighting: the denoiser would not build ({CoreRenderDevice.ShaderError}) - the filter on the processor smooths instead" );

		return pipeline;
	}
}
