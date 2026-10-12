using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rinten.Bake;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//A scene's light with no bake, by hardware rays (ray_grid.slang): probe cascades about a view's camera, a share
//traced a frame and kept as a running mean - DDGI, as NVIDIA's RTXGI; one a view, as each has its own camera
internal sealed class CoreProbeGrid
{
	//Probes along x and z, and up, in every cascade; the first's step in metres, each further one four times as wide
	private const int Across = 24;
	private const int Up = 12;
	private const float FirstStep = 1.0f;
	private const int Cascades = 2;

	//Rays a probe casts an update, and the most probes traced a frame - all of them about every seven frames
	private const int RaysPerProbe = 64;
	private const int ProbesPerFrame = 2048;

	//How much of a probe's light an update keeps, the rest new
	private const float Kept = 0.9f;

	//Milliseconds between looks at the sky
	private const long SkyEvery = 250;

	//The parameters' layout, in float4s - ray_grid.slang reads the same
	private const int LampsAt = 6;
	private const int TracedLamps = 32;
	private const int SkyAt = LampsAt + ( TracedLamps * 3 );
	private const int CascadesAt = SkyAt + 6;
	private const int RecordsAt = CascadesAt + ( Cascades * 2 );

	private const int PerCascade = Across * Up * Across;
	private const int Probes = PerCascade * Cascades;

	private static ulong pipeline;
	private static int pipelineBuilt = -1;

	private ulong state, output;
	private readonly ulong[] pictures = new ulong[4];
	private readonly ulong[] parameters = new ulong[3];
	private readonly int[] parameterRoom = new int[3];
	private int frame, first, turn;

	private readonly BakeSky sky = new();
	private long skyLooked;
	private bool skyRead;
	private readonly Vec3[] skyColours = new Vec3[6];

	private readonly List<(float Distance, Light Light)> nearest = new();

	/// <summary>Whether a scene's light comes from the grid: rays on and no bake to read instead.</summary>
	internal static bool Takes( Scene scene ) => CoreRays.On && scene.IsValid() && !CoreBakedLighting.Baked( scene );

	/// <summary>Traces this frame's share of the probes about the eye and lays the grid into the frame block; clears the
	/// lanes where the scene does not take it.</summary>
	public void Trace( Scene scene, Vector3 eye, float[] frame )
	{
		Array.Clear( frame, CoreSceneRenderer.GridAt, 16 );

		if ( !Takes( scene ) || CoreRays.Address == 0 || !Ready() ) return;

		var now = System.Environment.TickCount64;

		if ( !skyRead || now - skyLooked >= SkyEvery )
		{
			skyRead = true;
			skyLooked = now;
			sky.Gather( scene );
			SkyOf();
		}

		var words = Parameters( scene, eye );
		var slot = this.frame++ % parameters.Length;

		if ( parameters[slot] == 0 || parameterRoom[slot] < words.Length )
		{
			if ( parameters[slot] != 0 ) CoreRenderDevice.DestroyBuffer( parameters[slot] );
			parameterRoom[slot] = words.Length * 2;
			parameters[slot] = CoreRenderDevice.CreateBuffer( new byte[parameterRoom[slot] * 4] );
		}

		if ( parameters[slot] == 0 || !CoreRenderDevice.WriteBuffer( parameters[slot], 0, MemoryMarshal.AsBytes( words.AsSpan() ) ) ) return;

		Span<uint> push = stackalloc uint[4] { (uint)first, ProbesPerFrame, (uint)turn++, 0 };
		Span<ulong> buffers = stackalloc ulong[3] { parameters[slot], state, output };

		if ( !CoreRenderDevice.Dispatch( pipeline, buffers, ( ProbesPerFrame + 63 ) / 64, 1, 1, MemoryMarshal.AsBytes( push ) ) ) return;

		first = ( first + ProbesPerFrame ) % Probes;

		for ( var i = 0; i < pictures.Length; ++i )
			VkFrame.RaysCopy( output, (long)Probes * 8 * i, pictures[i], Across * Up, Across * Cascades );


		Lay( frame, eye );
	}

	//The pipeline, buffers and pictures, made the first time they are wanted
	private bool Ready()
	{
		if ( pipelineBuilt != CoreShaderCache.Revision )
		{
			if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
			pipelineBuilt = CoreShaderCache.Revision;

			var code = CoreShaderCache.Pass( EngineShaders.RayGrid, "main", CoreShaderCompiler.Stage.Compute );
			pipeline = code is null ? 0 : CoreRenderDevice.CreateCompute( code );

			if ( pipeline == 0 ) Log.Warning( $"hardware rays: the probe grid's shader would not build ({CoreRenderDevice.ShaderError})" );
		}

		if ( pipeline == 0 ) return false;

		if ( state == 0 ) state = CoreRenderDevice.CreateBuffer( new byte[Probes * 6 * 16] );
		if ( output == 0 ) output = CoreRenderDevice.CreateBuffer( new byte[Probes * 4 * 8] );

		for ( var i = 0; i < pictures.Length; ++i )
			if ( pictures[i] == 0 ) pictures[i] = CoreRenderDevice.CreateTextureHalf( new Half[Probes * 4], Across * Up, Across * Cascades );

		return state != 0 && output != 0 && pictures[0] != 0 && pictures[1] != 0 && pictures[2] != 0 && pictures[3] != 0;
	}

	//The first cell of a cascade about the eye, in that cascade's steps
	private static Vec3 FirstCell( Vector3 eye, float step )
		=> new( MathF.Floor( eye.x / step ) - ( Across / 2 ), MathF.Floor( eye.y / step ) - ( Up / 2 ), MathF.Floor( eye.z / step ) - ( Across / 2 ) );

	private static float StepOf( int cascade ) => FirstStep * MathF.Pow( 4.0f, cascade );

	//The frame block's grid lanes: the four pictures, the counts, and each cascade's first cell and step - see GridLight
	private void Lay( float[] frame, Vector3 eye )
	{
		var at = CoreSceneRenderer.GridAt;

		for ( var i = 0; i < pictures.Length; ++i ) frame[at + i] = CoreRender.TextureIndex( pictures[i] );

		frame[at + 4] = Cascades;
		frame[at + 5] = Across;
		frame[at + 6] = Up;
		frame[at + 7] = Across;

		for ( var c = 0; c < Cascades; ++c )
		{
			var step = StepOf( c );
			var cell = FirstCell( eye, step );
			var lane = at + 8 + ( c * 4 );

			frame[lane] = cell.X;
			frame[lane + 1] = cell.Y;
			frame[lane + 2] = cell.Z;
			frame[lane + 3] = step;
		}
	}

	//The sky as six colours along the axes, each the mean of its quarter of the sphere - see Sky in ray_grid.slang
	private void SkyOf()
	{
		Span<Vec3> axes = [Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY, Vec3.UnitZ, -Vec3.UnitZ];
		const int Taps = 48;

		for ( var a = 0; a < 6; ++a )
		{
			var sum = Vec3.Zero;
			var weight = 0.0f;

			for ( var i = 0; i < Taps; ++i )
			{
				var way = BakeMath.AnyDirection( ( i + 0.5f ) / Taps, ( ( i * 0.618034f ) % 1.0f ) );
				var w = Vec3.Dot( way, axes[a] );
				if ( w <= 0.0f ) continue;

				sum += sky.Radiance( way ) * w;
				weight += w;
			}

			skyColours[a] = weight > 0.0f ? sum / weight : Vec3.Zero;
		}
	}

	//The words ray_grid.slang reads: header, lamps, sky, cascades, records
	private float[] Parameters( Scene scene, Vector3 eye )
	{
		var records = CoreRays.Records;
		var words = new float[( RecordsAt + ( records.Count * 2 ) ) * 4];

		void Put( int at, float x, float y, float z, float w )
		{
			words[at * 4] = x;
			words[( at * 4 ) + 1] = y;
			words[( at * 4 ) + 2] = z;
			words[( at * 4 ) + 3] = w;
		}

		float Bits( uint value ) => BitConverter.UInt32BitsToSingle( value );

		var lamps = Lamps( scene, eye );
		var address = CoreRays.Address;

		Put( 0, Bits( (uint)address ), Bits( (uint)( address >> 32 ) ), Bits( Probes ), Bits( RaysPerProbe ) );
		Put( 1, Bits( (uint)lamps ), Bits( Cascades ), 0.0f, 0.0f );

		var sun = CoreSceneRenderer.FirstEnabled<DirectionalLight>( scene );

		if ( sun is not null && sun.Contribution.HasFlag( Light.LightContribution.Diffuse ) )
		{
			var towards = -sun.WorldRotation.Forward.Normal;
			var colour = sun.EmittedColor;

			Put( 3, towards.x, towards.y, towards.z, 0.0f );
			Put( 4, colour.r, colour.g, colour.b, Kept );
		}
		else Put( 4, 0.0f, 0.0f, 0.0f, Kept );

		for ( var i = 0; i < lamps; ++i )
		{
			var light = nearest[i].Light;
			var at = LampsAt + ( i * 3 );
			var position = light.WorldPosition;
			var emitted = light.EmittedColor;
			var radius = light is SpotLight spot ? spot.Radius : ( (PointLight)light ).Radius;

			Put( at, position.x, position.y, position.z, CoreSceneRenderer.EffectiveRange( radius, emitted, light.Falloff ) );
			Put( at + 1, emitted.r, emitted.g, emitted.b, light.Falloff );

			if ( light is SpotLight cone )
			{
				var forward = cone.WorldRotation.Forward.Normal;
				Put( at + 2, forward.x, forward.y, forward.z, MathF.Cos( cone.ConeOuter.DegreeToRadian() ) );
			}
		}

		for ( var a = 0; a < 6; ++a ) Put( SkyAt + a, skyColours[a].X, skyColours[a].Y, skyColours[a].Z, 0.0f );

		for ( var c = 0; c < Cascades; ++c )
		{
			var step = StepOf( c );
			var cell = FirstCell( eye, step );

			Put( CascadesAt + ( c * 2 ), cell.X, cell.Y, cell.Z, step );
			Put( CascadesAt + ( c * 2 ) + 1, Bits( Across ), Bits( Up ), Bits( Across ), Bits( (uint)( c * PerCascade ) ) );
		}

		for ( var i = 0; i < records.Count; ++i )
		{
			var (albedo, glow) = records[i];
			Put( RecordsAt + ( i * 2 ), albedo.X, albedo.Y, albedo.Z, 1.0f );
			Put( RecordsAt + ( i * 2 ) + 1, glow.X, glow.Y, glow.Z, 0.0f );
		}

		return words;
	}

	//The lamps nearest the eye that light anything diffusely, the nearest first, as many as are traced; how many
	private int Lamps( Scene scene, Vector3 eye )
	{
		nearest.Clear();

		foreach ( var light in scene.GetAllComponents<Light>() )
		{
			if ( light is not ( PointLight or SpotLight ) || !light.IsValid() || !light.Enabled || !light.Active ) continue;
			if ( !light.Contribution.HasFlag( Light.LightContribution.Diffuse ) ) continue;

			var emitted = light.EmittedColor;
			if ( emitted.r <= 0.0f && emitted.g <= 0.0f && emitted.b <= 0.0f ) continue;

			nearest.Add( (light.WorldPosition.Distance( eye ), light) );
		}

		nearest.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );

		return Math.Min( nearest.Count, TracedLamps );
	}

	/// <summary>The buffers and pictures off the card.</summary>
	public void Release()
	{
		if ( state != 0 ) CoreRenderDevice.DestroyBuffer( state );
		if ( output != 0 ) CoreRenderDevice.DestroyBuffer( output );

		for ( var i = 0; i < parameters.Length; ++i )
		{
			if ( parameters[i] != 0 ) CoreRenderDevice.DestroyBuffer( parameters[i] );
			parameters[i] = 0;
		}

		for ( var i = 0; i < pictures.Length; ++i )
		{
			if ( pictures[i] != 0 ) CoreRenderDevice.DestroyTexture( pictures[i] );
			pictures[i] = 0;
		}

		state = output = 0;
	}
}
