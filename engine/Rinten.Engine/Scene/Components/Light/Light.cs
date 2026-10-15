using NativeEngine;
using static Rinten.Component;

namespace Rinten;

[Expose]
public abstract partial class Light : Component, IColorProvider, ExecuteInEditor, ITintable
{
	SceneLight _sceneObject;

	internal SceneLight SceneObject => _sceneObject;

	//Source size drives highlight size and shadow penumbra (see surface_shading.slang, frame.slang)
	/// <summary>Half the light's width in metres (a shaded bulb is about 0.1): highlight size, shadow softness.</summary>
	[Property, Group( "Shadows" ), Range( 0.0f, 2.0f ), Title( "Source Radius" )]
	public float SourceRadius { get; set; } = 0.1f;

	/// <summary>The main color of the light</summary>
	[Property]
	public Color LightColor
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			if ( _sceneObject.IsValid() )
				_sceneObject.LightColor = value;
		}
	} = "#E9FAFF";

	//Inverse-square falloff: brightness 1 is a candle; raise this to light a room, radius only cuts off
	/// <summary>How strong the light is, as a multiplier on <see cref="LightColor"/>.</summary>
	[Property, Range( 0, 100 ), Order( -5 )]
	public float Brightness { get; set; } = 1.0f;

	//1 is inverse square, 0 no falloff (ends at radius), above 1 falls off faster
	/// <summary>How fast the light dies with distance, as the power the squared distance is raised to.</summary>
	[Property, Range( 0, 2 ), Order( -4 )]
	public float Attenuation { get; set; } = 1.0f;

	internal float Falloff => Attenuation.Clamp( 0.0f, 2.0f );

	//Colour times brightness; culling range uses this too, so bright lights aren't cut off early
	internal Color EmittedColor => new( LightColor.r * Brightness, LightColor.g * Brightness,
		LightColor.b * Brightness, LightColor.a );

	/// <summary>Whether this light lights the volumetric fog and shows its shadow in it.</summary>
	[Property, Category( "Fog Settings" )]
	public FogInfluence FogMode { get; set; } = FogInfluence.Enabled;

	/// <summary>How much of it lights the volumetric fog - see <see cref="FogMode"/>.</summary>
	[Property, Range( 0, 1 ), Category( "Fog Settings" )]
	public float FogStrength { get; set; } = 1.0f;

	/// <summary>Should this light cast shadows?</summary>
	[Property, Category( "Shadows" ), Order( -10 )]
	public bool Shadows
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			if ( _sceneObject.IsValid() )
				_sceneObject.ShadowsEnabled = value;
		}
	} = true;

	//A baked light is all in the bake - its light, soft shadows and bounces - and costs the frame nothing; what moves
	//takes it from the bake's volume. A light that must shadow or light what moves as it moves is left out of it
	/// <summary>Whether this light is kept out of the bake, lighting only as the frame draws it.</summary>
	[Property, Order( -4 ), Title( "Not Baked" )]
	public bool NotBaked { get; set; }

	//Diffuse and specular are kept apart in shading; the bake leaves out a light without diffuse
	/// <summary>Which lighting terms this light is allowed to contribute to.</summary>
	[Property, EnumButtonGroup, Title( "Contributes" )]
	public LightContribution Contribution { get; set; } = LightContribution.Diffuse | LightContribution.Specular | LightContribution.Transmissive;

	Color IColorProvider.ComponentColor => LightColor;

	Color ITintable.Color { get => LightColor; set => LightColor = value; }

	public enum FogInfluence
	{
		[Icon( "blur_off" )]
		Disabled = SceneLight.FogLightingMode.None,
		[Icon( "blur_linear" )]
		Enabled = SceneLight.FogLightingMode.Dynamic,
		[Icon( "blur_on" )]
		WithoutShadows = SceneLight.FogLightingMode.DynamicNoShadows
	}

	/// <summary>Which lighting terms a light is allowed to contribute to.</summary>
	[Flags]
	public enum LightContribution
	{
		/// <summary>Soft, even shading across a surface.</summary>
		[Icon( "wb_sunny" )]
		Diffuse = 1,

		/// <summary>Glossy highlights and reflections.</summary>
		[Icon( "auto_awesome" )]
		Specular = 2,

		/// <summary>Light passing through surfaces (translucency / subsurface).</summary>
		[Icon( "opacity" )]
		Transmissive = 4
	}

	protected override void OnAwake()
	{
		Tags.Add( "light" );

		base.OnAwake();
	}

	protected override void OnEnabled()
	{
		Assert.True( !_sceneObject.IsValid(), "_sceneObject should be null" );
		Assert.NotNull( Scene, "Scene should not be null" );

		_sceneObject = CreateSceneObject();

		if ( _sceneObject.IsValid() )
		{
			_sceneObject.Component = this;
			_sceneObject.LightColor = LightColor;
			_sceneObject.ShadowsEnabled = Shadows;

			OnTransformChanged();
			OnTagsChanged();

			Transform.OnTransformChanged += OnTransformChanged;
		}
	}

	protected override void OnDisabled()
	{
		Transform.OnTransformChanged -= OnTransformChanged;

		_sceneObject?.Delete();
		_sceneObject = null;
	}

	protected abstract SceneLight CreateSceneObject();

	void OnTransformChanged()
	{
		if ( !_sceneObject.IsValid() )
			return;

		_sceneObject.Transform = WorldTransform;
	}

	//Tags have been updated - lets update our light's tags
	protected override void OnTagsChanged()
	{
		if ( !_sceneObject.IsValid() )
			return;

		_sceneObject?.Tags.SetFrom( Tags );
	}
}
