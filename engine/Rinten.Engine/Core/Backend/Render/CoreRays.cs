using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rinten.Bake;
using Vec3 = System.Numerics.Vector3;

namespace Rinten;

//The scene as hardware rays see it (VkRays), built once in a view's recording for everything tracing it: its structure,
//and each instance's record of what it reflects and glows - read by the live bounce and the near light (ray_light.shader)
internal static class CoreRays
{
	/// <summary>Whether rays are traced: the player's setting, where the card can.</summary>
	internal static bool On => Wanted && VkRays.On && !CoreRenderDevice.PortableFrame;

	[ConVar( "r_raytracing", ConVarFlags.Saved,
		Help = "Hardware ray tracing for lighting, where the card has it (0 off, 1 on). Off by default - see RenderSettings.RayTracing." )]
	internal static bool Wanted { get; set; }

	/// <summary>The structure as this view built it, nought for none.</summary>
	internal static ulong Address { get; private set; }

	/// <summary>Each instance's albedo and glow, by the number the structure gives it.</summary>
	internal static readonly List<(Vec3 Albedo, Vec3 Glow)> Records = new();

	//Texels a row of the records' picture, two a record
	private const int Across = 512;

	private static readonly List<VkRays.Instance> placed = new();

	//The records' picture as last written, and what was in it
	private static ulong colours;
	private static int colourRows;
	private static Half[] written = [];

	//Each material's mean colour and glow, as the bake reads them - see BakeMaterial
	private static readonly Dictionary<Material, (Vec3 Albedo, Vec3 Glow)> known = new();
	private static readonly Dictionary<Texture, Vec3> means = new();

	/// <summary>Builds the scene's structure into the view's recording; false where there is nothing to trace.</summary>
	internal static bool Build( Scene scene )
	{
		Address = 0;
		Records.Clear();
		placed.Clear();

		if ( !On || !scene.IsValid() ) return false;

		Gather( scene );
		if ( placed.Count == 0 ) return false;

		Address = VkFrame.RaysBuild( CollectionsMarshal.AsSpan( placed ) );
		return Address != 0;
	}

	/// <summary>The frame block's rays lane: the address in 24-bit parts xy and top 16 bits w, the records z.</summary>
	internal static void Lay( float[] frame )
	{
		var at = CoreSceneRenderer.RaysAt;
		Array.Clear( frame, at, 4 );

		if ( Address == 0 || WriteColours() is not (> 0 and var picture) ) return;

		//A float holds 24 bits whole; RADV's addresses run to the top of the 64
		frame[at + 0] = Address & 0xFFFFFF;
		frame[at + 1] = ( Address >> 24 ) & 0xFFFFFF;
		frame[at + 2] = picture;
		frame[at + 3] = Address >> 48;
	}

	//The records into their picture, only where they changed; its table index
	private static uint WriteColours()
	{
		var rows = Math.Max( ( ( Records.Count * 2 ) + Across - 1 ) / Across, 1 );
		var texels = new Half[Across * rows * 4];

		for ( var i = 0; i < Records.Count; ++i )
		{
			var (albedo, glow) = Records[i];
			var at = i * 8;

			texels[at] = (Half)albedo.X;
			texels[at + 1] = (Half)albedo.Y;
			texels[at + 2] = (Half)albedo.Z;
			texels[at + 3] = (Half)1.0f;
			texels[at + 4] = (Half)MathF.Min( glow.X, 60000.0f );
			texels[at + 5] = (Half)MathF.Min( glow.Y, 60000.0f );
			texels[at + 6] = (Half)MathF.Min( glow.Z, 60000.0f );
		}

		if ( colours != 0 && rows == colourRows && texels.AsSpan().SequenceEqual( written ) ) return CoreRender.TextureIndex( colours );

		if ( colours != 0 && rows == colourRows )
		{
			CoreRenderDevice.WriteTextureLevel( colours, 0, 0, 0, Across, rows, MemoryMarshal.AsBytes( texels.AsSpan() ), Across * 8 );
		}
		else
		{
			if ( colours != 0 ) CoreRenderDevice.DestroyTexture( colours );
			colours = CoreRenderDevice.CreateTextureHalf( texels, Across, rows );
			colourRows = rows;
		}

		written = texels;
		return colours == 0 ? 0u : CoreRender.TextureIndex( colours );
	}

	//Every drawn rigid mesh of the scene as an instance, and its record: what it reflects, and what it glows if it moves
	private static void Gather( Scene scene )
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

			placed.Add( new VkRays.Instance { Mesh = mesh, Transform = tables.RecordAt( i ).World, Record = (uint)Records.Count } );
			Records.Add( (albedo, glow) );
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
		if ( known.TryGetValue( material, out var found ) ) return found;

		var tint = CoreResources.LiveTint( material );
		var albedo = MeanOf( material.GetTexture( "Color" ) ) * new Vec3( tint.r, tint.g, tint.b );
		albedo *= 1.0f - Math.Clamp( material.GetFloat( "Metalness", 0.0f ), 0.0f, 1.0f );

		var glow = Vec3.Zero;

		if ( CoreResources.Feature( material, "F_EMISSION" ) != 0 )
		{
			var said = material.core.Values.TryGetValue( "EmissiveColor", out var value ) ? new Vec3( value.x, value.y, value.z ) : Vec3.One;
			glow = said * MeanOf( material.GetTexture( "Emission" ) ) * material.GetFloat( "EmissionStrength", 0.0f );
		}

		found = (albedo, glow);
		known[material] = found;
		return found;
	}

	private static Vec3 MeanOf( Texture texture )
	{
		if ( texture is null ) return Vec3.One;
		if ( means.TryGetValue( texture, out var found ) ) return found;

		var mean = BakePicture.Of( texture, true )?.Mean() ?? System.Numerics.Vector4.One;
		found = new Vec3( mean.X, mean.Y, mean.Z );
		means[texture] = found;
		return found;
	}
}
