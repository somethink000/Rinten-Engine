namespace Rinten;

//Static model instances (grass, rocks, trees) stored only as GPU instance table records, no CPU cost
internal sealed class StaticInstanceSet : IDisposable
{
	private readonly Scene scene;
	private readonly List<int> slots = new();
	private readonly List<int> runs = new();

	public StaticInstanceSet( Scene scene )
	{
		this.scene = scene;
	}

	/// <summary>How many copies it holds.</summary>
	public int Count => slots.Count;

	/// <summary>One more copy. <paramref name="lodGroup"/> names the detail group it keeps its detail by with distance; null or empty is the model's own - see <see cref="Model.LodGroup"/>.</summary>
	public void Add( Model model, in Transform transform, bool castShadows = true, Material over = null, float lodScale = 1.0f, string lodGroup = null )
	{
		if ( Application.IsHeadless || scene is null || model is null ) return;

		var slot = CoreSceneTables.Of( scene ).AddStatic( model, transform, over, castShadows, lodScale, lodGroup );
		if ( slot >= 0 ) slots.Add( slot );
	}

	/// <summary>A run of copies the card places itself, all inside a sphere - see CoreSceneTables.AddRun. False where the tables will not take it.</summary>
	public bool AddRun( Model model, int count, System.Numerics.Vector4 sphere, in Transform frame, IInstanceRun placer, bool castShadows, float lodScale, string lodGroup )
	{
		if ( Application.IsHeadless || scene is null || model is null ) return false;

		var head = CoreSceneTables.Of( scene ).AddRun( model, count, sphere, frame, placer, null, castShadows, lodScale, lodGroup );
		if ( head < 0 ) return false;

		runs.Add( head );
		return true;
	}

	/// <summary>Whether its runs are drawn from clusters: false where one cannot be, null while a model is not loaded yet.</summary>
	public bool? RunsDrawn
	{
		get
		{
			if ( runs.Count == 0 ) return null;

			var tables = CoreSceneTables.Of( scene );
			bool? all = true;

			foreach ( var head in runs )
			{
				var drawn = tables.RunDrawn( head );
				if ( drawn == false ) return false;
				if ( drawn is null ) all = null;
			}

			return all;
		}
	}

	/// <summary>Takes every copy away.</summary>
	public void Clear()
	{
		if ( slots.Count == 0 && runs.Count == 0 ) return;

		var tables = CoreSceneTables.Of( scene );

		foreach ( var slot in slots ) tables.RemoveStatic( slot );
		foreach ( var head in runs ) tables.RemoveRun( head );

		slots.Clear();
		runs.Clear();
	}

	public void Dispose() => Clear();
}
