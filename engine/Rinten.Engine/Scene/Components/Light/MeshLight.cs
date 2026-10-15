namespace Rinten;

//A shaped lamp in a glowing mesh: a panel for a flat one, a tube for a long one, a ball otherwise; never baked,
//since a still glow itself is - see BakeScene.TowardsGlow - and a moving one has its bounce relit (CoreLiveBounce)
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

	//Which way a panel faces along its thin axis, and the transform that was found for - see Facing
	private float facing = 1.0f;
	private Transform facedFor;

	//Emission maps' means, read once a picture: the colour the bake gives their glow - see BakeMaterial.MeanGlow
	private static readonly Dictionary<Texture, Vector3> means = new();

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
			//Lit on the open side: stood just off that face, clear of the mesh
			forward *= Facing( forward );
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
		//A still glow in a bake has its diffuse light there already, as UE's emissive meshes; the lamp adds the reflections
		var baked = GameObject.IsStatic && Scene.LightingIfAny is { HasBake: true };
		lamp.Contribution = baked ? Light.LightContribution.Specular
			: Light.LightContribution.Diffuse | Light.LightContribution.Specular | Light.LightContribution.Transmissive;

		lamp.LightColor = glow.Colour;
		lamp.Brightness = glow.Strength * Strength * area;
		lamp.Radius = Radius;
		lamp.Shadows = Shadows;
	}

	//Along the thin axis or against it: the side a short ray finds open, or down for a level panel - a ceiling light
	private float Facing( Vector3 axis )
	{
		var transform = WorldTransform;
		if ( facedFor == transform ) return facing;
		facedFor = transform;

		var world = WorldRotation * axis;
		var centre = WorldPosition;

		bool Open( Vector3 way ) => !Scene.Trace.Ray( centre, centre + ( way * 1.0f ) ).IgnoreGameObjectHierarchy( GameObject ).Run().Hit;

		var ahead = Open( world );
		var behind = Open( -world );

		facing = ahead != behind ? ( ahead ? 1.0f : -1.0f )
			: MathF.Abs( world.y ) > 0.7f ? ( Vector3.Dot( world, Vector3.Down ) >= 0.0f ? 1.0f : -1.0f )
			: 1.0f;

		return facing;
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

	//As BakeMaterial.Of reads it: strength times colour times the map's mean, the albedo's too where it says so
	private static (Color Colour, float Strength) GlowOf( Material material )
	{
		if ( material?.core is null || CoreResources.Feature( material, "F_EMISSION" ) == 0 ) return default;

		var strength = material.GetFloat( "EmissionStrength", 0.0f );
		var colour = material.core.Values.TryGetValue( "EmissiveColor", out var said ) ? new Vector3( said.x, said.y, said.z ) : Vector3.One;

		colour *= MeanOf( material.GetTexture( "Emission" ) );
		if ( material.GetFloat( "EmissiveUseAlbedo", 0.0f ) > 0.5f ) colour *= MeanOf( material.GetTexture( "Color" ) );

		//The brightest channel goes to the strength, so the colour stays a colour
		var peak = MathF.Max( colour.x, MathF.Max( colour.y, colour.z ) );
		if ( peak <= 0.0f ) return default;

		colour /= peak;
		return (new Color( colour.x, colour.y, colour.z ), strength * peak);
	}

	private static Vector3 MeanOf( Texture texture )
	{
		if ( texture is null ) return Vector3.One;
		if ( means.TryGetValue( texture, out var known ) ) return known;

		var mean = Bake.BakePicture.Of( texture, true )?.Mean() ?? System.Numerics.Vector4.One;
		known = new Vector3( mean.X, mean.Y, mean.Z );
		means[texture] = known;

		return known;
	}
}
