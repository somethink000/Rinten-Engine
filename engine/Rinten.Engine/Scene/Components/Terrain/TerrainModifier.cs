using System.Collections.Generic;

namespace Rinten;

/// <summary>Frozen overlay applied to chunk copies at bake time, never to the mesh: Shape and/or Solid</summary>
public abstract class TerrainModifier
{
	/// <summary>Everything it can touch, in the terrain's own space. Ground outside it is never shown to it.</summary>
	public BBox Bounds;

	/// <summary>Moves or repaints the ground's vertices. Only what is inside <see cref="Bounds"/> is its to touch.</summary>
	public virtual void Shape( TerrainSoup ground ) { }

	/// <summary>Geometry set into the ground, or null. A triangle's material is nought for the terrain's own paint, or a place in <see cref="Materials"/> plus one.</summary>
	public virtual TerrainSoup Solid => null;

	/// <summary>What the solid's triangles wear.</summary>
	public virtual IReadOnlyList<Material> Materials => [];

	/// <summary>A hollow modelled from the inside - the mouth of a cave - rather than a solid: the ground across its opening goes and all of it stays.</summary>
	public virtual bool Carve => false;

	/// <summary>Whether nothing should be scattered at a point of the ground. Ground a solid covers is gone already; this is for the verge of a road.</summary>
	public virtual bool Clears( Vector3 point ) => false;
}

/// <summary>A model as a modifier: what <see cref="TerrainMeshInsert"/> lays over the ground, and what <see cref="Terrain.Embed(TerrainModifier)"/> sets into it for good.</summary>
public sealed class TerrainModelModifier : TerrainModifier
{
	private readonly TerrainSoup solid;
	private readonly List<Material> materials;
	private readonly bool carve;

	public override TerrainSoup Solid => solid;
	public override IReadOnlyList<Material> Materials => materials;
	public override bool Carve => carve;

	private TerrainModelModifier( TerrainSoup solid, List<Material> materials, bool carve )
	{
		this.solid = solid;
		this.materials = materials;
		this.carve = carve;

		var any = false;

		foreach ( var vertex in solid.Vertices )
		{
			Bounds = any ? Bounds.AddPoint( vertex.Position ) : new BBox( vertex.Position, vertex.Position );
			any = true;
		}
	}

	public static TerrainModelModifier From( Model model, Transform toTerrain, Material materialOverride = null,
		bool wearTerrain = false, bool carve = false )
	{
		if ( model is null ) return null;

		var vertices = model.GetVertices();
		var indices = model.GetIndices();

		if ( vertices is null || indices is null || indices.Length < 3 ) return null;

		var scale = toTerrain.Scale;
		var mirrored = scale.x * scale.y * scale.z < 0.0f;

		var solid = new TerrainSoup();

		foreach ( var v in vertices )
		{
			var tangent = toTerrain.Rotation * new Vector3( v.Tangent.x, v.Tangent.y, v.Tangent.z );

			solid.Vertices.Add( new TerrainSoup.Vertex
			{
				Position = toTerrain.PointToWorld( v.Position ),
				Normal = ( toTerrain.Rotation * v.Normal ).Normal,
				Tangent = new Vector4( tangent.x, tangent.y, tangent.z, v.Tangent.w ),
				Uv = new Vector2( v.TexCoord0.x, v.TexCoord0.y ),
				Paint = TerrainPaint.Only( 0 ),
			} );
		}

		var worn = model.Materials;
		var materials = new List<Material>();

		for ( var m = 0; m < model.MeshCount; m++ )
		{
			model.MeshDrawRange( m, out var first, out var count, out var baseVertex );
			count -= count % 3;

			ushort slot = 0;

			if ( !wearTerrain )
			{
				var material = materialOverride ?? ( m < worn.Length ? worn[m] : null );
				var at = materials.IndexOf( material );

				if ( at < 0 ) { materials.Add( material ); at = materials.Count - 1; }

				slot = (ushort)( at + 1 );
			}

			for ( var i = 0; i < count; i += 3 )
			{
				var a = baseVertex + (int)indices[first + i];
				var b = baseVertex + (int)indices[first + i + 1];
				var c = baseVertex + (int)indices[first + i + 2];

				//Turned inside out by a mirroring scale; turned back.
				if ( mirrored ) ( b, c ) = ( c, b );

				solid.Indices.Add( a );
				solid.Indices.Add( b );
				solid.Indices.Add( c );
				solid.Materials.Add( slot );
			}
		}

		return solid.Indices.Count < 3 ? null : new TerrainModelModifier( solid, materials, carve );
	}
}

//The engine ships one - a model, TerrainMeshInsert. A road, a river bed, a foundation are a project's to write: derive from this and answer Bake, or hand a modifier straight to Terrain.SetModifier from code. See docs/samples/TerrainRoad.cs.
/// <summary>A component that lays a <see cref="TerrainModifier"/> over the terrain it stands on, for as long as it stands there: move it and the ground follows.</summary>
[Category( "World" )]
public abstract class TerrainInsert : Component, Component.ExecuteInEditor
{
	/// <summary>The terrain it is laid over, once it has found one.</summary>
	[Hide, System.Text.Json.Serialization.JsonIgnore]
	public Terrain Terrain { get; private set; }

	protected override void OnEnabled()
	{
		Transform.OnTransformChanged += Changed;
		Attach();
	}

	protected override void OnDisabled() => Detach();
	protected override void OnDestroy() => Detach();

	//Finds the terrain it stands on and joins it.
	internal void Attach()
	{
		if ( Terrain.IsValid() || !Active ) return;

		var found = Components.GetInAncestorsOrSelf<Terrain>();

		if ( !found.IsValid() && Scene is not null )
		{
			foreach ( var one in Scene.GetAll<Terrain>() )
			{
				if ( !one.IsValid() || one.Storage is null ) continue;

				found ??= one;

				var local = one.WorldTransform.PointToLocal( WorldPosition );
				var size = one.Storage.TerrainSize;

				if ( local.x >= 0.0f && local.z >= 0.0f && local.x <= size && local.z <= size )
				{
					found = one;
					break;
				}
			}
		}

		if ( !found.IsValid() ) return;

		Terrain = found;
		Changed();
	}

	private void Detach()
	{
		Transform.OnTransformChanged -= Changed;

		if ( Terrain.IsValid() ) Terrain.SetModifier( this, null );

		Terrain = null;
	}

	/// <summary>Says the insert is not what it was - moved, or a property changed - so the ground around it is baked again.</summary>
	public void Changed()
	{
		if ( !Terrain.IsValid() ) { Attach(); return; }

		Terrain.SetModifier( this, Active && Terrain.Storage is not null ? Bake( Terrain ) : null );
	}

	/// <summary>The insert as it stands now, in the terrain's own space. Null for nothing.</summary>
	public abstract TerrainModifier Bake( Terrain terrain );
}

/// <summary>A model laid into the ground while it stands there - a rock, a cliff, the mouth of a cave - cut into the terrain's chunks by a real union and still an object that can be moved. <see cref="Terrain.Embed(TerrainModifier)"/> makes it ground for good.</summary>
[Expose]
[Title( "Terrain Mesh Insert" )]
[Category( "World" )]
[Icon( "landslide" )]
public sealed class TerrainMeshInsert : TerrainInsert
{
	public enum InsertMode
	{
		/// <summary>A solid standing in the ground: the ground inside it goes, and so does what it has below the ground.</summary>
		[Icon( "landscape" )]
		Embed,

		/// <summary>A hollow let into the ground, modelled from the inside: the ground across its opening goes, and all of it stays.</summary>
		[Icon( "download" )]
		Carve,
	}

	/// <summary>What is set in. Taken from a model renderer beside this the first time, if there is one - which is then switched off, since the terrain draws it now.</summary>
	[Property]
	public Model Model
	{
		get => field;
		set { if ( field == value ) return; field = value; Changed(); }
	}

	[Property]
	public InsertMode Mode
	{
		get => field;
		set { if ( field == value ) return; field = value; Changed(); }
	}

	/// <summary>Painted as the ground is rather than with its own materials, so the ground's texture runs up over it unbroken.</summary>
	[Property]
	public bool WearTerrain
	{
		get => field;
		set { if ( field == value ) return; field = value; Changed(); }
	}

	/// <summary>Drawn with this instead of the model's materials.</summary>
	[Property, HideIf( nameof( WearTerrain ), true )]
	public Material MaterialOverride
	{
		get => field;
		set { if ( field == value ) return; field = value; Changed(); }
	}

	protected override void OnEnabled()
	{
		if ( Model is null && Components.Get<ModelRenderer>() is { } beside && beside.Model is not null )
		{
			Model = beside.Model;
			MaterialOverride ??= beside.MaterialOverride;
			beside.Enabled = false;
		}

		base.OnEnabled();
	}

	public override TerrainModifier Bake( Terrain terrain )
		=> TerrainModelModifier.From( Model, terrain.WorldTransform.ToLocal( WorldTransform ), MaterialOverride, WearTerrain, Mode == InsertMode.Carve );
}
