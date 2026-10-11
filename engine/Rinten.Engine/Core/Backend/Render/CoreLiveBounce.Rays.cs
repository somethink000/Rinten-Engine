using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rinten.Bake;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//The bounce traced on the card where it has hardware rays (VkRays, ray_bounce.slang): every probe casts rays through
//the scene as it stands, moving things included, a share of the probes a frame - DDGI's way, into the same two pictures
internal sealed partial class CoreLiveBounce
{
	/// <summary>Whether the bounce is traced with rays - see CoreRays.On.</summary>
	internal static bool Rays => CoreRays.On;

	//Rays each probe casts an update, and the most probes traced a frame
	private const int RaysPerProbe = 32;
	private const int ProbesPerFrame = 4096;

	//How much of a probe's light an update keeps, the rest new - a mean over about seven updates
	private const float Kept = 0.85f;

	//The parameters' layout, in float4s - ray_bounce.slang reads the same
	private const int LampsAt = 6;
	private const int TracedLamps = 32;
	private const int RecordsAt = LampsAt + ( TracedLamps * 3 );

	private static ulong pipeline;
	private static bool pipelineAsked;

	//The probes' light between updates, what the pictures are copied from, and parameters a frame in flight
	private ulong state, output;
	private readonly ulong[] parameters = new ulong[3];
	private readonly int[] parameterRoom = new int[3];
	private int frame, first, turn;

	//The sun, sky and lamps as last read on the main thread
	private void LookForRays( Scene scene )
	{
		if ( Light == 0 || Lean == 0 )
		{
			var blank = new Half[points * 4];
			Light = CoreRenderDevice.CreateTextureHalf( blank, data.VolumeX * data.VolumeY, data.VolumeZ );
			Lean = CoreRenderDevice.CreateTextureHalf( blank, data.VolumeX * data.VolumeY, data.VolumeZ );
		}

		var now = System.Environment.TickCount64;
		if ( now - looked < Every && sky is not null ) return;
		looked = now;

		var current = spare ?? new BakeSky();
		current.Gather( scene );
		spare = sky;
		sky = current;

		SunOf( scene, out towards, out colour );
		lamps = LampsOf( scene );
	}

	/// <summary>Traces this frame's share of the probes into the pictures, through CoreRays as built.</summary>
	public void Trace( Scene scene )
	{
		if ( !Rays || Light == 0 || Lean == 0 || sky is null || points == 0 ) return;

		if ( !pipelineAsked )
		{
			pipelineAsked = true;
			var code = CoreShaderCache.Pass( EngineShaders.RayBounce, "main", CoreShaderCompiler.Stage.Compute );
			pipeline = code is null ? 0 : CoreRenderDevice.CreateCompute( code );

			if ( pipeline == 0 ) Log.Warning( $"hardware rays: the bounce's shader would not build ({CoreRenderDevice.ShaderError}) - the surfels light it" );
		}

		if ( pipeline == 0 ) return;

		if ( state == 0 ) state = CoreRenderDevice.CreateBuffer( new byte[points * 3 * 16] );
		if ( output == 0 ) output = CoreRenderDevice.CreateBuffer( new byte[points * 2 * 8] );
		if ( state == 0 || output == 0 ) return;

		if ( CoreRays.Address == 0 ) return;

		var words = Parameters( CoreRays.Address, CoreRays.Records );
		var slot = frame++ % parameters.Length;

		if ( parameters[slot] == 0 || parameterRoom[slot] < words.Length )
		{
			if ( parameters[slot] != 0 ) CoreRenderDevice.DestroyBuffer( parameters[slot] );
			parameterRoom[slot] = words.Length * 2;
			parameters[slot] = CoreRenderDevice.CreateBuffer( new byte[parameterRoom[slot] * 4] );
		}

		if ( parameters[slot] == 0 || !CoreRenderDevice.WriteBuffer( parameters[slot], 0, MemoryMarshal.AsBytes( words.AsSpan() ) ) ) return;

		var count = Math.Min( points, ProbesPerFrame );
		Span<uint> push = stackalloc uint[4] { (uint)first, (uint)count, (uint)turn++, 0 };
		Span<ulong> buffers = stackalloc ulong[3] { parameters[slot], state, output };

		if ( !CoreRenderDevice.Dispatch( pipeline, buffers, ( count + 63 ) / 64, 1, 1, MemoryMarshal.AsBytes( push ) ) ) return;

		first = ( first + count ) % points;

		var across = data.VolumeX * data.VolumeY;
		VkFrame.RaysCopy( output, 0, Light, across, data.VolumeZ );
		VkFrame.RaysCopy( output, (long)points * 8, Lean, across, data.VolumeZ );
	}

	//The words ray_bounce.slang reads: header, lamps, records
	private float[] Parameters( ulong address, List<(Vec3 Albedo, Vec3 Glow)> records )
	{
		var words = new float[( RecordsAt + ( records.Count * 2 ) ) * 4];

		void Put( int at, float x, float y, float z, float w )
		{
			words[at * 4] = x;
			words[( at * 4 ) + 1] = y;
			words[( at * 4 ) + 2] = z;
			words[( at * 4 ) + 3] = w;
		}

		float Bits( uint value ) => BitConverter.UInt32BitsToSingle( value );

		var origin = new Vec3( data.VolumeOrigin.x, data.VolumeOrigin.y, data.VolumeOrigin.z );

		Put( 0, Bits( (uint)address ), Bits( (uint)( address >> 32 ) ), Bits( (uint)points ), Bits( RaysPerProbe ) );
		Put( 1, origin.X, origin.Y, origin.Z, data.VolumeStep );
		Put( 2, Bits( (uint)data.VolumeX ), Bits( (uint)data.VolumeY ), Bits( (uint)data.VolumeZ ), Bits( (uint)Math.Min( lamps.Count, TracedLamps ) ) );
		Put( 3, towards.X, towards.Y, towards.Z, 0.0f );
		Put( 4, colour.X, colour.Y, colour.Z, Kept );
		Put( 5, 1.0f, 0.0f, 0.0f, 0.0f );

		for ( var i = 0; i < Math.Min( lamps.Count, TracedLamps ); ++i )
		{
			var lamp = lamps[i];
			var at = LampsAt + ( i * 3 );

			Put( at, lamp.Position.X, lamp.Position.Y, lamp.Position.Z, lamp.Range );
			Put( at + 1, lamp.Colour.X, lamp.Colour.Y, lamp.Colour.Z, lamp.Falloff );

			//A spot's forward and outer cosine, its inner folded into the edge as 1 - see Lamp in ray_bounce.slang
			if ( lamp.Kind == 2 ) Put( at + 2, lamp.Forward.X, lamp.Forward.Y, lamp.Forward.Z, lamp.CosOuter );
		}

		for ( var i = 0; i < records.Count; ++i )
		{
			var (albedo, glow) = records[i];
			Put( RecordsAt + ( i * 2 ), albedo.X, albedo.Y, albedo.Z, 1.0f );
			Put( RecordsAt + ( i * 2 ) + 1, glow.X, glow.Y, glow.Z, 0.0f );
		}

		return words;
	}

	//The buffers off the card
	private void ReleaseRays()
	{
		if ( state != 0 ) CoreRenderDevice.DestroyBuffer( state );
		if ( output != 0 ) CoreRenderDevice.DestroyBuffer( output );

		for ( var i = 0; i < parameters.Length; ++i )
		{
			if ( parameters[i] != 0 ) CoreRenderDevice.DestroyBuffer( parameters[i] );
			parameters[i] = 0;
		}

		state = output = 0;
	}
}
