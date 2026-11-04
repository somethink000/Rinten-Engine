namespace Rinten;

/// <summary>Provides the ability to generate <see cref="Model"/>s at runtime. A new instance is available from <see cref="Model.Builder"/>.</summary>
public sealed partial class ModelBuilder
{
	private string modelName;
	private float mass = 1000;
	private string surfaceProperty = "";
	private bool collides = true;
	private int clusterLevels;
	private bool noClusters;

	/// <summary>Sets the mass, in kilograms, used by collision shapes added directly to this builder. The default is 1000.</summary>
	public ModelBuilder WithMass( float mass )
	{
		this.mass = mass;
		return this;
	}

	/// <summary>Sets the surface used by collision shapes added directly to this builder.</summary>
	public ModelBuilder WithSurface( string name )
	{
		surfaceProperty = name;
		return this;
	}

	/// <summary>Builds the model without a collision mesh made from what it draws. For geometry that is only ever looked at - or whose collision is somebody else's business, as a terrain's is its height field's.</summary>
	public ModelBuilder WithoutCollision()
	{
		collides = false;
		return this;
	}

	/// <summary>Sets the name used to identify the model.</summary>
	public ModelBuilder WithName( string name )
	{
		modelName = name;
		return this;
	}

	/// <summary>Single cluster level, no LOD chain: much cheaper, for close-up or soon-rebuilt geometry</summary>
	public ModelBuilder WithoutLevelsOfDetail()
	{
		clusterLevels = 1;
		return this;
	}

	//No clusters at all, for a model something else draws; cut at level nought only if it is ever drawn
	internal ModelBuilder WithoutClusters()
	{
		noClusters = true;
		return this;
	}

	//The model is a piece of something larger - a terrain chunk - whose open edges are where its neighbours
	//meet it: every level keeps them, so two pieces at any two levels still meet without a crack.
	internal ModelBuilder WithLockedBorder()
	{
		lockedBorder = true;
		return this;
	}

	private bool lockedBorder;

	/// <summary>Builds the model.</summary>
	public unsafe Model Create()
	{
		//The legacy model builder takes render meshes. Where that renderer is not here,
		//the meshes kept their vertices themselves and ours builds the model out of those.
		return CreateOnCore();
	}

	/// <summary>Compiled model bytes without creating a Model, so it can run off the main thread; null if empty</summary>
	public unsafe byte[] ToBytes()
	{
		var builder = Filled( out var foreign );
		if ( builder is null ) return null;

		try
		{
			//A mesh whose vertices only its own shader understands rides on the model beside
			//the file rather than in it, and there is nowhere in the bytes to put it.
			return foreign.Count > 0 ? null : builder.ToBytes();
		}
		finally
		{
			builder.Dispose();
		}
	}
}
