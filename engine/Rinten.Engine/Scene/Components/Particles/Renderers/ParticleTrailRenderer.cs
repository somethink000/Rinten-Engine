
namespace Rinten;

//Built by ParticlePlayer from a .fx file, not added by hand - see ParticleEffect.
/// <summary>Renders a trail for each particle in the effect.</summary>
[Hide]
[Title( "Particle Trail Renderer" )]
[Category( "Effects" )]
[Icon( "category" )]
public sealed class ParticleTrailRenderer : ParticleController, Component.ExecuteInEditor
{
	[Property, Order( -100 ), InlineEditor( Label = false ), Group( "Advanced Rendering", StartFolded = true )] public RenderOptions RenderOptions { get; } = new RenderOptions( null );

	[Group( "Trail" )]
	[Property] public int MaxPoints { get; set; } = 64;

	/// <summary>How far the trail travels before it lays down another point, in metres.</summary>
	[Group( "Trail" )]
	[Property] public float PointDistance { get; set; } = 0.2f;

	[Group( "Trail" )]
	[Property] public float LifeTime { get; set; } = 2;

	[Group( "Appearance" )]
	[Property, InlineEditor( Label = false )] public TrailTextureConfig Texturing { get; set; } = TrailTextureConfig.Default;

	[Group( "Appearance" )]
	[Property] public Gradient Color { get; set; } = global::Color.Cyan;

	/// <summary>How wide the trail is, in metres, over its life.</summary>
	[Group( "Appearance" )]
	[Property] public Curve Width { get; set; } = 0.127f;

	[Group( "Particles" )]
	[Property] public bool TintFromParticle { get; set; } = true;

	[Group( "Particles" )]
	[Property] public bool ScaleFromParticle { get; set; } = true;

	[Group( "Rendering" )]
	[Property] public bool Wireframe { get; set; }

	[Group( "Rendering" )]
	[Property] public bool Opaque { get; set; } = true;

	[ShowIf( "Opaque", true )]
	[Group( "Rendering" )]
	[Property] public bool CastShadows { get; set; } = false;

	[ShowIf( "Opaque", false )]
	[Group( "Rendering" )]
	[Property] public BlendMode BlendMode { get; set; } = BlendMode.Normal;

	List<SceneTrailObject> adopted = new();

	//Trails whose particle is gone and whose tail has faded, and listeners of gone particles, kept for the next particles:
	//one of each per particle was objects and arrays every frame
	readonly System.Collections.Concurrent.ConcurrentBag<SceneTrailObject> spare = new();
	readonly System.Collections.Concurrent.ConcurrentBag<ParticleTrail> spareListeners = new();

	internal Material _defaultMaterial;

	protected override void OnParticleCreated( Particle p )
	{
		if ( Application.IsHeadless )
			return;

		p.AddListener( spareListeners.TryTake( out var kept ) ? kept : new ParticleTrail( this ), this );
	}

	//If we have to do this where we want to wait for a SceneSystem effect to finish before destroying it more than
	//once, we should really think about the ability to add them to a system that runs through them and does it for us.


	internal void Adopt( SceneTrailObject obj )
	{
		lock ( adopted )
		{
			adopted.Add( obj );
		}
	}

	//A trail for a new particle, from the spares where there is one
	internal SceneTrailObject Take()
	{
		while ( spare.TryTake( out var kept ) )
		{
			if ( !kept.IsValid() ) continue;

			kept.RenderingEnabled = true;
			return kept;
		}

		return new SceneTrailObject( Scene.SceneWorld );
	}

	internal void Spare( ParticleTrail listener ) => spareListeners.Add( listener );

	//Back to the spares, empty and not drawn
	internal void Return( SceneTrailObject obj )
	{
		obj.Reset();
		obj.RenderingEnabled = false;
		spare.Add( obj );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !Texturing.Material.IsValid() )
		{
#pragma warning disable CS0618
			if ( Texturing.Texture.IsValid() )
			{
				_defaultMaterial.Set( "g_tColor", Texturing.Texture );
#pragma warning restore CS0618
			}
			else
			{
				_defaultMaterial.Set( "g_tColor", Texture.White );
			}
		}

		Rinten.Jobs.JobSystem.For( adopted.Count, (adopted, Time.Delta), static ( s, i ) =>
		{
			s.adopted[i].AdvanceTime( s.Delta );
			s.adopted[i].Build();
		} );

		adopted.RemoveAll( retire ??= Retire );
	}

	Predicate<SceneTrailObject> retire;

	//An adopted trail that has faded goes back to the spares
	bool Retire( SceneTrailObject obj )
	{
		if ( !obj.IsEmpty ) return false;

		Return( obj );
		return true;
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();

		adopted = new();
		_defaultMaterial = Material.Load( "materials/default/line.mat" ).CreateCopy();
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		foreach ( var obj in adopted )
		{
			obj.Delete();
		}

		adopted.Clear();

		while ( spare.TryTake( out var kept ) ) kept.Delete();
		spareListeners.Clear();
	}
}


class ParticleTrail : Particle.BaseListener
{
	public ParticleTrailRenderer Renderer;

	SceneTrailObject so;

	public ParticleTrail( ParticleTrailRenderer renderer )
	{
		Renderer = renderer;
	}

	public override void OnEnabled( Particle p )
	{
		so = Renderer.Take();
		so.Tags.SetFrom( Renderer.GameObject.Tags );
		so.MaxPoints = Renderer.MaxPoints;
		so.PointDistance = Renderer.PointDistance;
		so.LifeTime = Renderer.LifeTime;
		so.Texturing = Renderer.Texturing;
		if ( !Renderer.Texturing.Material.IsValid() )
		{
			so.Texturing = so.Texturing with { Material = Renderer._defaultMaterial };
		}
		so.TrailColor = Renderer.Color;
		so.Width = Renderer.Width;
		so.Flags.CastShadows = Renderer.Opaque && Renderer.CastShadows;
		so.Opaque = Renderer.Opaque;
		so.BlendMode = Renderer.BlendMode;
		so.Wireframe = Renderer.Wireframe;
		so.SamplerState = new() { Filter = Renderer.Texturing.FilterMode, AddressModeU = Renderer.Texturing.TextureAddressMode, AddressModeV = Renderer.Texturing.TextureAddressMode };
	}

	public override void OnDisabled( Particle p )
	{
		if ( so is null ) return;

		if ( !Renderer.Active ) so.Delete();
		else if ( so.IsEmpty ) Renderer.Return( so );
		else Renderer.Adopt( so );

		so = null;

		if ( Renderer.Active ) Renderer.Spare( this );
	}

	public override void OnUpdate( Particle p, float dt )
	{
		if ( so is null ) return;

		if ( Renderer.TintFromParticle )
			so.LineTint = p.Color;

		if ( Renderer.ScaleFromParticle )
			so.LineScale = p.Size.x;

		so.Transform = new Transform( p.Position );
		so.TryAddPosition( p.Position );
		so.AdvanceTime( Time.Delta );
		so.Build();

		Renderer.RenderOptions.Apply( so );
	}
}
