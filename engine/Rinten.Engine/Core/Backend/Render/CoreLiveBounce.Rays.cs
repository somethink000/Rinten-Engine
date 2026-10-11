using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rinten.Bake;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//The bounce traced on the card where it has hardware rays (VkRays, ray_bounce.slang): every probe casts rays through
//the scene as it stands, moving things included, a share of the probes a frame - DDGI's way, into the same two pictures
internal sealed partial class CoreLiveBounce
{
	/// <summary>Whether the bounce is traced with rays: the player's setting, where the card can.</summary>
	internal static bool Rays => Wanted && VkRays.On && !CoreRenderDevice.PortableFrame;

	[ConVar( "r_raytracing", ConVarFlags.Saved,
		Help = "Hardware ray tracing for lighting, where the card has it (0 off, 1 on). Off by default - see RenderSettings.RayTracing." )]
	internal static bool Wanted { get; set; }

	//Rays each probe casts an update, and the most probes traced a frame
	private const int RaysPerProbe = 32;
	private const int ProbesPerFrame = 4096;

	//How much of a probe's light an update keeps, the rest new - a mean over about seven updates
	private const float Kept = 0.85f;

	//The parameters' layout, in float4s - ray_bounce.slang reads the same
	private const int SkyAt = 6;
	private const int SkySide = 8;
	private const int LampsAt = SkyAt + ( SkySide * SkySide );
	private const int TracedLamps = 32;
	private const int RecordsAt = LampsAt + ( TracedLamps * 3 );

	private static ulong pipeline;
	private static bool pipelineAsked;

	//The probes' light between updates, what the pictures are copied from, and parameters a frame in flight
	private ulong state, output;
	private readonly ulong[] parameters = new ulong[3];
	private readonly int[] parameterRoom = new int[3];
	private int frame, first, turn;

	//Each material's mean colour and glow, as the bake reads them - see BakeMaterial
	private static readonly Dictionary<Material, (Vec3 Albedo, Vec3 Glow)> colours = new();
	private static readonly Dictionary<Texture, Vec3> means = new();

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

	/// <summary>Traces this frame's share of the probes into the pictures; in the frame's recording.</summary>
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

		var placed = new List<VkRays.Instance>();
		var records = new List<(Vec3 Albedo, Vec3 Glow)>();

		Gather( scene, placed, records );
		if ( placed.Count == 0 ) return;

		var address = VkFrame.RaysBuild( CollectionsMarshal.AsSpan( placed ) );
		if ( address == 0 ) return;

		var words = Parameters( address, records );
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

	//Every drawn rigid mesh of the scene as an instance, and its record: what it reflects, and what it glows if it moves
	private static void Gather( Scene scene, List<VkRays.Instance> placed, List<(Vec3 Albedo, Vec3 Glow)> records )
	{
		var tables = CoreSceneTables.Of( scene );

		for ( var i = 0; i < tables.InstanceCount; ++i )
		{
			var entry = tables.EntryAt( i );
			if ( entry is not { Live: true, Mesh: { Pooled: true } mesh } ) continue;
			if ( entry.Renderer is SkinnedModelRenderer || entry.RenderType == ModelRenderer.ShadowRenderType.Off ) continue;
			if ( entry.Layer != CoreSceneTables.DrawLayer.Game ) continue;

			var (albedo, glow) = ColoursOf( entry );

			//A still glow's light is in the bake already
			if ( entry.Renderer is { } renderer && renderer.GameObject.IsStatic ) glow = Vec3.Zero;

			placed.Add( new VkRays.Instance { Mesh = mesh, Transform = tables.RecordAt( i ).World, Record = (uint)records.Count } );
			records.Add( (albedo, glow) );
		}
	}

	//The mean over its materials, times its tint
	private static (Vec3 Albedo, Vec3 Glow) ColoursOf( CoreSceneTables.Entry entry )
	{
		var albedo = Vec3.Zero;
		var glow = Vec3.Zero;
		var counted = 0;

		if ( entry.Override is { } over )
		{
			( albedo, glow ) = ColoursOf( over );
			counted = 1;
		}
		else if ( entry.Model is { } model )
		{
			foreach ( var material in model.Materials )
			{
				var (a, g) = ColoursOf( material );
				albedo += a;
				glow += g;
				counted++;
			}
		}

		if ( counted == 0 ) return (new Vec3( 0.5f ), Vec3.Zero);

		var tint = new Vec3( entry.Tint.r, entry.Tint.g, entry.Tint.b );
		return (Vec3.Min( albedo / counted * tint, new Vec3( 0.95f ) ), glow / counted);
	}

	private static (Vec3 Albedo, Vec3 Glow) ColoursOf( Material material )
	{
		if ( material?.core is null ) return (new Vec3( 0.5f ), Vec3.Zero);
		if ( colours.TryGetValue( material, out var known ) ) return known;

		var tint = CoreResources.LiveTint( material );
		var albedo = MeanOf( material.GetTexture( "Color" ) ) * new Vec3( tint.r, tint.g, tint.b );
		albedo *= 1.0f - Math.Clamp( material.GetFloat( "Metalness", 0.0f ), 0.0f, 1.0f );

		var glow = Vec3.Zero;

		if ( CoreResources.Feature( material, "F_EMISSION" ) != 0 )
		{
			var said = material.core.Values.TryGetValue( "EmissiveColor", out var value ) ? new Vec3( value.x, value.y, value.z ) : Vec3.One;
			glow = said * MeanOf( material.GetTexture( "Emission" ) ) * material.GetFloat( "EmissionStrength", 0.0f );
		}

		known = (albedo, glow);
		colours[material] = known;
		return known;
	}

	private static Vec3 MeanOf( Texture texture )
	{
		if ( texture is null ) return Vec3.One;
		if ( means.TryGetValue( texture, out var known ) ) return known;

		var mean = BakePicture.Of( texture, true )?.Mean() ?? System.Numerics.Vector4.One;
		known = new Vec3( mean.X, mean.Y, mean.Z );
		means[texture] = known;
		return known;
	}

	//The words ray_bounce.slang reads: header, sky table, lamps, records
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

		for ( var cell = 0; cell < SkySide * SkySide; ++cell )
		{
			var ox = ( ( ( ( cell % SkySide ) + 0.5f ) / SkySide ) * 2.0f ) - 1.0f;
			var oy = ( ( ( ( cell / SkySide ) + 0.5f ) / SkySide ) * 2.0f ) - 1.0f;
			var radiance = sky.Radiance( BakeMath.FromOctahedron( ox, oy ) );

			Put( SkyAt + cell, radiance.X, radiance.Y, radiance.Z, 0.0f );
		}

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
