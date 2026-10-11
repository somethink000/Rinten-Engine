namespace Rinten;

//A shaped lamp in a glowing mesh: a panel for a flat one, a tube for a long one, a ball otherwise; never baked,
//since the glow itself is - see BakeScene.TowardsGlow
/// <summary>Lights the scene from this object's glowing material in the shape of its mesh, as the frame draws it.</summary>
[Expose]
[Title( "Mesh Light" )]
[Category( "Light" )]
[Icon( "wb_incandescent" )]
public sealed class MeshLight : Component, Component.ExecuteInEditor
{
	/// <summary>How much light the lamp gives against what the material's glow says.</summary>
	[Property, Range( 0, 4 )]
	public float Strength { get; set; } = 1.0f;

	/// <summary>How far the lamp reaches, in metres.</summary>
	[Property, Range( 0.5f, 50.0f )]
	public float Radius { get; set; } = 8.0f;

	//The lamp stands inside its mesh, which shadows all but a panel's front
	/// <summary>Whether the lamp casts shadows.</summary>
	[Property]
	public bool Shadows { get; set; }

	//Thinner than this share of the next side is a panel; a middle side under this share of the longest is a tube
	private const float Flat = 0.2f;
	private const float Long = 0.3f;

	private GameObject holder;
	private PointLight lamp;

	protected override void OnEnabled()
	{
		holder = new GameObject( GameObject, true, "Mesh Light" );
		holder.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

		lamp = holder.Components.Create<PointLight>();
		lamp.NotBaked = true;
		lamp.Attenuation = 1.0f;

		Fit();
	}

	protected override void OnDisabled()
	{
		holder?.Destroy();
		holder = null;
		lamp = null;
	}

	protected override void OnUpdate() => Fit();

	//The lamp to the renderer's model, scale and glow as they stand
	private void Fit()
	{
		if ( !lamp.IsValid() ) return;

		var renderer = GetComponent<ModelRenderer>();
		var glow = renderer.IsValid() ? GlowOf( renderer ) : default;

		lamp.Enabled = glow.Strength > 0.0f;
		if ( !lamp.Enabled ) return;

		var bounds = renderer.Model.Bounds;
		var scale = WorldScale;
		var size = new Vector3( bounds.Size.x * MathF.Abs( scale.x ), bounds.Size.y * MathF.Abs( scale.y ), bounds.Size.z * MathF.Abs( scale.z ) );

		//The local axes longest first
		Span<int> order = stackalloc int[3] { 0, 1, 2 };
		order.Sort( ( a, b ) => size[b].CompareTo( size[a] ) );

		var longest = size[order[0]];
		var middle = size[order[1]];
		var thinnest = size[order[2]];

		var forward = Axis( order[2] );
		var up = Axis( order[1] );
		var centre = bounds.Center;

		float area;

		if ( thinnest < middle * Flat )
		{
			//Lit on the side forward points to: stood just off that face, clear of the mesh
			lamp.AreaShape = PointLight.AreaShapes.Rectangle;
			lamp.AreaSize = new Vector2( longest, middle );
			centre += forward * ( ( bounds.Size[order[2]] * 0.5f ) + 0.005f );
			area = longest * middle;
		}
		else if ( middle < longest * Long )
		{
			lamp.AreaShape = PointLight.AreaShapes.Tube;
			lamp.AreaSize = new Vector2( longest, 0.0f );
			lamp.SourceRadius = ( middle + thinnest ) * 0.25f;
			area = longest * lamp.SourceRadius * 2.0f;
		}
		else
		{
			lamp.AreaShape = PointLight.AreaShapes.Point;
			lamp.SourceRadius = ( longest + middle + thinnest ) / 6.0f;
			area = MathF.PI * lamp.SourceRadius * lamp.SourceRadius;
		}

		holder.LocalPosition = centre;
		holder.LocalRotation = Rotation.LookAt( forward, up );

		//Radiance times area is the intensity a point light of that colour gives straight on
		lamp.LightColor = glow.Colour;
		lamp.Brightness = glow.Strength * Strength * area;
		lamp.Radius = Radius;
		lamp.Shadows = Shadows;
	}

	private static Vector3 Axis( int index ) => index switch
	{
		0 => new Vector3( 1.0f, 0.0f, 0.0f ),
		1 => new Vector3( 0.0f, 1.0f, 0.0f ),
		_ => new Vector3( 0.0f, 0.0f, 1.0f ),
	};

	//The first glowing material it draws with: its colour and strength, as the surface shader reads them
	private static (Color Colour, float Strength) GlowOf( ModelRenderer renderer )
	{
		if ( renderer.Model is not { } model || !model.IsValid() ) return default;

		if ( renderer.MaterialOverride is { } over ) return GlowOf( over );

		foreach ( var material in model.Materials )
		{
			var glow = GlowOf( material );
			if ( glow.Strength > 0.0f ) return glow;
		}

		return default;
	}

	private static (Color Colour, float Strength) GlowOf( Material material )
	{
		if ( material?.core is null || material.GetFeature( "F_EMISSION" ) == 0 ) return default;

		var strength = material.GetFloat( "EmissionStrength", 0.0f );
		var colour = material.core.Values.TryGetValue( "EmissiveColor", out var said ) ? new Color( said.x, said.y, said.z ) : Color.White;

		return (colour, strength);
	}
}
