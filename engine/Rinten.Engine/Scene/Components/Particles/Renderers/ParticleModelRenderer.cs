using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rinten;

//Built by ParticlePlayer from a .fx file, not added by hand - see ParticleEffect.
/// <summary>Renders particles as models, using the particle's position, rotation, and size.</summary>
[Hide]
[Expose]
[Title( "Particle Model Renderer" )]
[Category( "Effects" )]
[Icon( "category" )]
public sealed class ParticleModelRenderer : ParticleController, Component.ExecuteInEditor
{
	/// <summary>Render options for advanced rendering.</summary>
	[Property, Order( -100 ), InlineEditor( Label = false ), Group( "Advanced Rendering", StartFolded = true )]
	public RenderOptions RenderOptions { get; } = new RenderOptions( null );

	/// <summary>Entry for a model, including its material group and body group settings.</summary>
	[Expose]
	public sealed class ModelEntry
	{
		private Model _model;

		/// <summary>The model associated with this entry.</summary>
		[KeyProperty]
		public Model Model
		{
			get => _model;
			set
			{
				if ( _model == value )
					return;

				_model = value;

				MaterialGroup = default;
				BodyGroups = _model?.Parts.DefaultMask ?? default;
			}
		}

		/// <summary>Material group for the model.</summary>
		[Model.MaterialGroup, ShowIf( nameof( HasMaterialGroups ), true )]
		public string MaterialGroup { get; set; }

		/// <summary>Body group mask for the model.</summary>
		[Model.BodyGroupMask, ShowIf( nameof( HasBodyGroups ), true )]
		public ulong BodyGroups { get; set; }

		/// <summary>Indicates whether the model has material groups.</summary>
		[Hide, JsonIgnore]
		public bool HasMaterialGroups => Model?.MaterialGroupCount > 0;

		/// <summary>Indicates whether the model has body groups.</summary>
		[Hide, JsonIgnore]
		public bool HasBodyGroups => Model?.Parts.All.Sum( x => x.Choices.Count ) > 1;

		/// <summary>Converts a <see cref="Model"/> to a <see cref="ModelEntry"/>.</summary>
		public static implicit operator ModelEntry( Model model ) => new() { Model = model };
	}

	/// <summary>List of models for rendering. This property is obsolete; use <see cref="Choices"/> instead.</summary>
	[Hide, Obsolete( "Use Choices" )]
	public List<Model> Models { get; set; } = new();

	/// <summary>List of model entries available for rendering.</summary>
	[Property]
	public List<ModelEntry> Choices { get; set; } = new List<ModelEntry> { Model.Cube };

	/// <summary>Material override for rendering.</summary>
	[Property]
	public Material MaterialOverride { get; set; }

	/// <summary>When set, particles with local space enabled will follow this skinned model's bones. Falls back to a <see cref="SkinnedModelRenderer"/> on this object or its parents when unset.</summary>
	[Property]
	public SkinnedModelRenderer Target { get; set; }

	/// <summary>If true, the models will rotate relative to the this GameObject</summary>
	[Property]
	public bool RotateWithGameObject { get; set; }

	/// <summary>Scale factor for particle rendering.</summary>
	[Property]
	public ParticleFloat Scale { get; set; } = 1;

	/// <summary>Indicates whether particles cast shadows.</summary>
	[Property]
	public bool CastShadows { get; set; } = true;

	/// <summary>A blend shape of the model, and what drives its weight over a particle's life.</summary>
	public readonly record struct MorphDriver( string Name, ParticleFloat Weight );

	/// <summary>A number the shader declared with Attribute( … ), and what drives it.</summary>
	public readonly record struct FloatDriver( string Name, ParticleFloat Value )
	{
		public StringToken Token { get; } = Name;
	}

	/// <summary>A colour the shader declared with Attribute( … ), and the gradient that drives it.</summary>
	public readonly record struct ColorDriver( string Name, ParticleGradient Value )
	{
		public StringToken Token { get; } = Name;
	}

	/// <summary>A three-wide attribute read per particle from a vector binding - live, since it may name a moving anchor.</summary>
	public readonly record struct VectorDriver( string Name, ParticleVectorBinding Value )
	{
		public StringToken Token { get; } = Name;
	}

	/// <summary>The model's blend shapes, driven per particle - set by the Mesh module. Only the shapes named here are written; the rest stay at nought.</summary>
	[Hide, JsonIgnore]
	public List<MorphDriver> Morphs { get; set; } = new();

	/// <summary>Numbers written into the draw for the shader to read - what the material's shader declared with <c>Attribute( "Name" )</c>. Per particle, so a dissolve can run over each one's life. See CoreShaderParams.DrawSlots for how many fit.</summary>
	[Hide, JsonIgnore]
	public List<FloatDriver> FloatAttributes { get; set; } = new();

	[Hide, JsonIgnore]
	public List<ColorDriver> ColorAttributes { get; set; } = new();

	[Hide, JsonIgnore]
	public List<VectorDriver> VectorAttributes { get; set; } = new();

	/// <summary>The player the vector rows are read against.</summary>
	[Hide, JsonIgnore]
	public ParticlePlayer Player { get; set; }

	/// <summary>Whether every draw also carries <c>ParticleAge</c> (nought to one over the life) and <c>ParticleRandom</c>, for a shader that animates itself.</summary>
	[Hide, JsonIgnore]
	public bool WriteParticleAttributes { get; set; } = true;

	/// <summary>Whether each particle draws only the piece of the model it stands for - the mesh its <see cref="Particle.Part"/> names, turned about that piece's own middle - rather than the whole model.</summary>
	[Hide, JsonIgnore]
	public bool PartFromParticle { get; set; }

	//Called when a particle is created.
	protected override void OnParticleCreated( Particle p )
	{
		p.AddListener( spareListeners.TryTake( out var kept ) ? kept : new ParticleModel( this ), this );
	}

	//Dead particles' drawn objects and listeners, by what they draw, kept for the next particles rather than remade
	//Taken and given back from the threads the effect steps on
	readonly System.Collections.Concurrent.ConcurrentDictionary<(Model Model, bool Morphed), System.Collections.Concurrent.ConcurrentBag<SceneObject>> spare = new();
	readonly System.Collections.Concurrent.ConcurrentBag<Particle.BaseListener> spareListeners = new();

	//An object drawing this model, from the spares where there is one
	internal SceneObject Take( Model model, bool morphed )
	{
		if ( spare.TryGetValue( (model, morphed), out var kept ) )
		{
			while ( kept.TryTake( out var so ) )
			{
				if ( !so.IsValid() ) continue;

				so.RenderingEnabled = true;
				return so;
			}
		}

		return morphed
			? new SceneModel( Scene.SceneWorld, model, global::Transform.Zero )
			: new SceneObject( Scene.SceneWorld, model ?? Model.Cube );
	}

	//Back to the spares, not drawn
	internal void Return( SceneObject so, Model model, bool morphed )
	{
		if ( !Active )
		{
			so.Delete();
			return;
		}

		so.RenderingEnabled = false;
		spare.GetOrAdd( (model, morphed), static _ => new() ).Add( so );
	}

	internal void Spare( Particle.BaseListener listener )
	{
		if ( Active ) spareListeners.Add( listener );
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		foreach ( var kept in spare.Values )
		{
			while ( kept.TryTake( out var so ) ) so.Delete();
		}

		spare.Clear();
		spareListeners.Clear();
	}

	/// <summary>Version of the component.</summary>
	public override int ComponentVersion => 1;

	//Upgrades the JSON representation of the particle model renderer to version 1.
	[Expose, JsonUpgrader( typeof( ParticleModelRenderer ), 1 )]
	static void Upgrader_v1( JsonObject obj )
	{
		if ( obj.TryGetPropertyValue( "Models", out var node ) )
		{
			var choices = new JsonArray();

			foreach ( var model in node.AsArray() )
			{
				if ( model is null )
					continue;

				choices.Add( new JsonObject { ["Model"] = model.ToString() } );
			}

			obj["Choices"] = choices;
			obj.Remove( "Models" );
		}
	}
}

//Represents a particle model listener that updates the scene object based on particle properties.
file class ParticleModel : Particle.BaseListener
{
	/// <summary>Renderer associated with this particle model.</summary>
	public ParticleModelRenderer Renderer;

	SceneObject so;

	//What so draws, which is what it goes back to the spares under
	Model drawn;
	bool morphed;

	/// <summary>Initializes a new instance of the <see cref="ParticleModel"/> class.</summary>
	public ParticleModel( ParticleModelRenderer renderer )
	{
		Renderer = renderer;
	}

	/// <summary>Called when the particle is enabled.</summary>
	public override void OnEnabled( Particle p )
	{
		var entry = Random.Shared.FromList( Renderer.Choices );
		var model = entry?.Model;

		//A model with blend shapes is a SceneModel, which is what carries the weights the renderer reads; anything else is the plainer object.
		drawn = model;
		morphed = model is { Morphs.Count: > 0 } && Renderer.Morphs.Count > 0;
		so = Renderer.Take( model, morphed );

		so.Tags.SetFrom( Renderer.GameObject.Tags );

		//Whose it is. A drawn object that names no component is one the editor cannot answer about: clicking it in a viewport selects
		//nothing and a material dropped on it lands nowhere, because every one of those reads the component off what the ray hit.
		so.Component = Renderer;

		if ( model is not null )
		{
			so.MeshGroupMask = entry.BodyGroups;
			so.SetMaterialGroup( entry.MaterialGroup );
		}
	}

	/// <summary>Called when the particle is disabled.</summary>
	public override void OnDisabled( Particle p )
	{
		if ( so.IsValid() ) Renderer.Return( so, drawn, morphed );

		so = null;
		Renderer.Spare( this );
	}

	/// <summary>Updates the particle.</summary>
	public override void OnUpdate( Particle p, float dt )
	{
		if ( !so.IsValid() ) return;

		var rot = p.Angles.ToRotation();

		if ( Renderer.RotateWithGameObject )
		{
			//Rotate the particle with the object
			rot = Renderer.WorldRotation * rot;
		}

		var scale = p.Size * Renderer.Scale.Evaluate( p, 2356 );
		var tx = new Transform( p.Position, rot, scale );

		//One piece of a broken model: the mesh the particle names, with the model moved so the piece's own middle is where
		//the particle is - so it turns about itself, and a Goal back to the piece's home puts the model back together.
		if ( Renderer.PartFromParticle && p.Part >= 0 && so.Model is { } model )
		{
			so.MeshIndex = p.Part;
			tx.Position = tx.PointToWorld( -model.MeshBounds( p.Part ).Center );
		}
		else
		{
			so.MeshIndex = -1;
		}

		so.Transform = tx;
		so.ColorTint = p.Color.WithAlphaMultiplied( p.Alpha );
		so.Flags.CastShadows = Renderer.CastShadows;
		so.SetMaterialOverride( Renderer.MaterialOverride );

		Drive( p );

		Renderer.RenderOptions.Apply( so );
	}

	//The two names written for every particle, turned into tokens once.
	private static readonly StringToken AgeToken = "ParticleAge";
	private static readonly StringToken RandomToken = "ParticleRandom";

	//The blend shapes and the shader's numbers, read for this particle now.
	void Drive( Particle p )
	{
		var morphs = Renderer.Morphs;

		if ( morphs.Count > 0 && so is SceneModel model )
		{
			for ( var i = 0; i < morphs.Count; i++ )
				model.Morphs.Set( morphs[i].Name, morphs[i].Weight.Evaluate( p, 3100 + i ) );
		}

		var floats = Renderer.FloatAttributes;
		var colours = Renderer.ColorAttributes;
		var vectors = Renderer.VectorAttributes;

		if ( floats.Count == 0 && colours.Count == 0 && vectors.Count == 0 && !Renderer.WriteParticleAttributes ) return;

		var attributes = so.Attributes;

		for ( var i = 0; i < floats.Count; i++ )
			attributes.Set( floats[i].Token, floats[i].Value.Evaluate( p, 3200 + i ) );

		for ( var i = 0; i < colours.Count; i++ )
			attributes.Set( colours[i].Token, (Vector4)colours[i].Value.Evaluate( p, 3300 + i ) );

		for ( var i = 0; i < vectors.Count; i++ )
			attributes.Set( vectors[i].Token, new Vector4( vectors[i].Value.Evaluate( p, Renderer.Player ), 1.0f ) );

		if ( Renderer.WriteParticleAttributes )
		{
			attributes.Set( AgeToken, p.LifeDelta );
			attributes.Set( RandomToken, p.Rand( 7, 371 ) );
		}
	}
}
