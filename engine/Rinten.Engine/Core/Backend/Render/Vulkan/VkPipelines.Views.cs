using Silk.NET.Vulkan;

namespace Rinten;

//Rebuilds pipelines for a two-eye multiview pass or a multisampled one from their kept recipe on first use
internal static unsafe partial class VkPipelines
{
	private sealed record Recipe(
		byte[] Vertex, string VertexEntry, byte[] Pixel, string PixelEntry,
		int VertexLayout, int DepthTest, int Topology, int Blend, int DepthWrite, int CullBack,
		int DepthOnly, int Target, int RasterFlags, int Specialize, int PictureFormat, uint Samples );

	//What each pipeline was made of, by the driver's handle.
	private static readonly Dictionary<ulong, Recipe> recipes = new();

	//Each pipeline made again for a pass of more than one layer or sample: by the driver's handle, the layers and the samples.
	private static readonly Dictionary<(ulong Pipeline, uint Mask, uint Samples), (Pipeline Made, ulong Handle)> layered = new();

	//Said once for a pipeline nobody kept the makings of.
	private static bool saidUnkept;

	//Whether pipeline recipes are kept: for two-eye pipelines where a headset is wanted (CoreXr.Wanted),
	//or wherever a pass may be multisampled (VkFrame.WorldSamples)
	internal static bool KeepRecipes => ( VkDevice.multiview && CoreXr.Wanted ) || VkDevice.mostSamples > 1;

	/// <summary>Whether a bind has to ask for its variant even at one sample: a base may have been baked at more.</summary>
	internal static bool SampledBases { get; private set; }

	//The full frame's world passes are the only ones that draw multisampled there, so they are all it keeps
	private static bool Keeps( int target )
		=> KeepRecipes && ( VkDevice.Tier == RenderTier.Portable || CoreXr.Wanted || target == VkDevice.TargetScene || target == VkDevice.TargetDepth );

	private static void Remember( Pipeline pipeline, Recipe recipe )
	{
		if ( pipeline.Handle == 0 ) return;

		recipes[pipeline.Handle] = recipe;
		SampledBases |= recipe.Samples > 1;
	}

	//The world's pipelines bake at its samples, so its shaders do not compile twice; the depth's are shared with shadows at one
	private static uint BaseSamples( int target ) => target == VkDevice.TargetScene ? VkFrame.WorldSamples : 1u;

	//One copy of each module: most pipelines differ by their state, and a world's SPIR-V is large
	private static readonly Dictionary<int, List<byte[]>> modules = new();

	private static byte[] Shared( ReadOnlySpan<byte> spirv )
	{
		var hash = new HashCode();
		hash.AddBytes( spirv );
		var key = hash.ToHashCode();

		if ( !modules.TryGetValue( key, out var same ) ) modules[key] = same = new List<byte[]>( 1 );

		foreach ( var one in same )
			if ( one.AsSpan().SequenceEqual( spirv ) ) return one;

		var copy = spirv.ToArray();
		same.Add( copy );
		return copy;
	}

	//A pipeline is going: so do its makings and whatever was made again from them.
	private static void Forget( Pipeline pipeline )
	{
		recipes.Remove( pipeline.Handle );

		if ( layered.Count == 0 ) return;

		foreach ( var key in layered.Keys.Where( x => x.Pipeline == pipeline.Handle ).ToArray() )
		{
			Destroy( layered[key].Handle );
			layered.Remove( key );
		}
	}

	//The pipeline for a pass drawing the layers in mask at once, samples a pixel, made the first time it is asked for.
	//The one asked about where it cannot be made - drawn wrong rather than not at all, and said.
	internal static Pipeline Variant( Pipeline single, uint mask, uint samples )
	{
		if ( samples <= 1 ) samples = 1;
		if ( single.Handle == 0 ) return single;

		var known = recipes.TryGetValue( single.Handle, out var recipe );

		//Most binds: already what the pass takes
		if ( mask == 0 && samples == ( known ? recipe.Samples : 1u ) ) return single;

		if ( layered.TryGetValue( (single.Handle, mask, samples), out var kept ) ) return kept.Made;

		if ( !known )
		{
			if ( !saidUnkept )
			{
				saidUnkept = true;
				VkDevice.SayWarning( "a pipeline was drawn with in a pass of both eyes or of more samples, and nothing it was made of was kept" );
			}

			return single;
		}

		ulong handle;

		fixed ( byte* vertex = recipe.Vertex )
		fixed ( byte* pixel = recipe.Pixel )
		{
			handle = Create( vertex, recipe.Vertex.Length, recipe.VertexEntry,
				pixel, recipe.Pixel.Length, recipe.PixelEntry,
				recipe.VertexLayout, recipe.DepthTest, recipe.Topology, recipe.Blend,
				recipe.DepthWrite, recipe.CullBack, recipe.DepthOnly, recipe.Target, recipe.RasterFlags,
				recipe.Specialize, recipe.PictureFormat, mask, samples );
		}

		var made = handle != 0 ? Get( handle ) : single;

		//Kept whichever way it went: a pipeline that would not be made once will not be made the next draw either.
		layered[(single.Handle, mask, samples)] = (made, handle);

		return made;
	}
}
