using Rinten.Rendering;
namespace Rinten;

//Holds a list of post processing layers for a camera
internal class PostProcessLayers
{
	public Dictionary<Stage, List<PostProcessLayer>> Layers = new();

	/// <summary>Whether an effect this frame reads how far what moved moved - see BlitMode.WantsMotion.</summary>
	public bool WantsMotion;

	/// <summary>The bloom the portable frame draws itself, from Bloom's numbers - see CorePortableGlow.</summary>
	public CorePortableGlow.Settings? Glow;

	//Layers taken out of the stages by Clear, to be handed out again by CreateLayer: the same effects hang the same layers every frame.
	private readonly Stack<PostProcessLayer> spare = new();

	public void Clear()
	{
		//The lists stay with their stages, emptied: a dictionary cleared is a list allocated per stage per frame.
		foreach ( var list in Layers.Values )
		{
			foreach ( var layer in list ) spare.Push( layer );

			list.Clear();
		}

		WantsMotion = false;
		Glow = null;
	}

	/// <summary>Add a new post process layer to a specific stage</summary>
	public PostProcessLayer CreateLayer( Stage stage )
	{
		var layer = spare.Count > 0 ? spare.Pop() : new PostProcessLayer();

		layer.CommandList = null;
		layer.Order = 0;
		layer.Name = null;

		if ( !Layers.TryGetValue( stage, out var list ) )
		{
			list = [];
			Layers[stage] = list;
		}

		list.Add( layer );

		return layer;
	}

	//Adds this stage's layer lists; checked before stage setup since most stages have none
	internal void ListsAt( Stage stage, List<(int Order, int At, CommandList List)> into )
	{
		if ( !Layers.TryGetValue( stage, out var list ) ) return;

		foreach ( var layer in list )
		{
			if ( layer.CommandList is not null ) into.Add( ( layer.Order, into.Count, layer.CommandList ) );
		}
	}

	public int Count( Stage stage )
		=> Layers.TryGetValue( stage, out var list ) ? list.Count : 0;
}

internal record struct WeightedEffect
{
	public BasePostProcess Effect;
	public float Weight;
}

//A layer is placed on a specific Render Stage is ordered relative to other layers on that stage
internal class PostProcessLayer
{
	public CommandList CommandList;
	public int Order;
	public string Name;
}
