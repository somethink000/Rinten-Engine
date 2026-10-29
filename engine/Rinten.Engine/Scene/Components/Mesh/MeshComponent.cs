using System.Text.Json.Serialization;
using static Rinten.Component;
using static Rinten.ModelRenderer;

namespace Rinten;

/// <summary>An editable polygon mesh with collision</summary>
[Hide, Expose]
public sealed class MeshComponent : Collider, ExecuteInEditor, IMaterialSetter
{
	[Expose]
	public enum CollisionType
	{
		None,
		Mesh,
		Hull
	}

	[Property, Hide]
	public PolygonMesh Mesh
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;

			RebuildMesh( true );
		}
	}

	[Property, Order( 1 )]
	public CollisionType Collision
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;

			//The model carries one kind of collision, so a different kind is a different model
			RebuildRenderMesh();
			RebuildImmediately();
		}
	} = CollisionType.Mesh;

	//The whole-component tint was removed; tint is per face now (PolygonMesh.SetFaceColor)

	[Property, Order( 3 )]
	public float SmoothingAngle
	{
		get;
		set
		{
			if ( field == value )
				return;

			field = value;
			Mesh?.SetSmoothingAngle( field );
		}
	}

	[Property, Order( 4 )]
	public bool HideInGame
	{
		get;
		set
		{
			if ( field == value )
				return;

			field = value;

			if ( Scene.IsEditor )
				return;

			if ( HideInGame )
			{
				DeleteSceneObject();
			}
			else if ( !_sceneObject.IsValid() && Model is not null )
			{
				_sceneObject = new SceneObject( Scene.SceneWorld, Model, WorldTransform );
				RebuildCellObjects();
				UpdateSceneObject();
			}
		}
	}

	//An edited mesh's origin returns to its middle when the edit ends - see CenterOrigin; this keeps it put
	/// <summary>Keep the origin where it was put, instead of in the middle of the mesh.</summary>
	[Property, Order( 5 ), Title( "Keep Origin" )]
	public bool KeepOrigin { get; set; }

	/// <summary>Lightmap density against the bake's: above one for detail and contact, below for plain walls.</summary>
	[Property, Order( 6 ), Range( 0.125f, 8.0f )]
	public float LightmapScale { get; set; } = 1.0f;

	//How far off the middle an origin may be before it is moved, in metres.
	const float OriginTolerance = 0.0001f;

	/// <summary>Moves the origin to the mesh's bounds centre, keeping mesh and children where they are.</summary>
	public bool CenterOrigin()
	{
		if ( Mesh is null ) return false;

		var middle = Mesh.CalculateBounds().Center;
		if ( middle.Length < OriginTolerance ) return false;

		var children = GameObject.Children;
		var kept = new Transform[children.Count];

		for ( var i = 0; i < children.Count; i++ )
			kept[i] = children[i].IsValid() ? children[i].WorldTransform : default;

		WorldPosition = WorldTransform.PointToWorld( middle );
		Mesh.ApplyTransform( new Transform( -middle ) );
		RebuildMesh();

		for ( var i = 0; i < children.Count && i < kept.Length; i++ )
		{
			if ( children[i].IsValid() ) children[i].WorldTransform = kept[i];
		}

		return true;
	}

	[Title( "Cast Shadows" ), Property, Category( "Lighting" )]
	public ShadowRenderType RenderType
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			UpdateSceneObject();
		}
	} = ShadowRenderType.On;

	[JsonIgnore, Hide]
	public Model Model { get; private set; }

	public override bool IsConcave => Collision == CollisionType.Mesh;

	bool Hidden => !Scene.IsEditor && HideInGame;

	SceneObject _sceneObject;

	//A mesh wider than a world grid cell is drawn as a model a cell, each at its own distance's detail - see
	//PolygonMesh.RenderCells; the whole mesh's object stays, undrawn, for what asks about it as one thing
	List<SceneObject> _cellObjects;

	public void SetMaterial( Material material, int triangle )
	{
		if ( Mesh is null ) return;

		var face = Mesh.TriangleToFace( triangle );
		if ( !face.IsValid ) return;

		Mesh.SetFaceMaterial( face, material );
	}

	public Material GetMaterial( int triangle )
	{
		if ( Mesh is null ) return default;

		var face = Mesh.TriangleToFace( triangle );
		return Mesh.GetFaceMaterial( face );
	}

	internal override void OnEnabledInternal()
	{
		GameObject.Tags.Add( "world" );

		//Mesh needs to build before collider.
		RebuildRenderMesh();

		base.OnEnabledInternal();
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();

		DeleteSceneObject();
	}

	void DeleteSceneObject()
	{
		DeleteCellObjects();

		if ( !_sceneObject.IsValid() ) return;

		_sceneObject.RenderingEnabled = false;
		_sceneObject.Delete();
		_sceneObject = null;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		RebuildMesh();
	}

	//Gives the object a gizmo hitbox so it can be clicked in object mode; part modes lay their own
	protected override void DrawGizmos()
	{
		if ( Game.IsPlaying ) return;
		if ( !Gizmo.Settings.Selection ) return;

		//One that is only there to cast a shadow is not on the screen to be pointed at.
		if ( RenderType == ShadowRenderType.ShadowsOnly ) return;

		if ( !Model.IsValid() ) return;

		Gizmo.Transform = WorldTransform;
		Gizmo.Hitbox.Model( Model );

		//And say so, for the same reason a model does: the pointer lands on this now, and a target nothing draws for is one
		//nobody can tell is there. Not while it is selected - what a selected object looks like is drawn for it already.
		if ( !Gizmo.IsHovered || Gizmo.IsSelected ) return;

		Gizmo.Draw.LineThickness = 1.0f;
		Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( 0.6f );
		Gizmo.Draw.LineBBox( Model.Bounds );
	}

	public void RebuildMesh()
	{
		RebuildMesh( false );
	}

	void RebuildMesh( bool forceRebuild )
	{
		//Only rebuild dirty meshes in editor.
		if ( !Active ) return;
		if ( !Scene.IsEditor ) return;
		if ( Mesh is null ) return;

		if ( forceRebuild || Mesh.IsDirty )
		{
			RebuildRenderMesh();
			RebuildImmediately();
		}
		else if ( Mesh.IsVertexDataDirty )
		{
			//The colours into the model's own vertex arrays first, so the copy the engine holds agrees with the
			//mesh, and so the dirty corners are let go of - without that this branch would fire every frame.
			Mesh.UpdateVertexData();

			//Vertex data isn't re-uploaded to the geometry pool, so rebuild fully for colour changes to show
			RebuildRenderMesh();
			RebuildImmediately();
		}
	}

	protected override void OnTagsChanged()
	{
		base.OnTagsChanged();

		if ( _sceneObject.IsValid() )
		{
			_sceneObject.Tags.SetFrom( Tags );
		}

		foreach ( var cell in _cellObjects ?? [] )
			if ( cell.IsValid() ) cell.Tags.SetFrom( Tags );
	}

	internal override void TransformChanged( GameTransform root )
	{
		if ( _sceneObject.IsValid() )
		{
			_sceneObject.Transform = WorldTransform;
		}

		foreach ( var cell in _cellObjects ?? [] )
			if ( cell.IsValid() ) cell.Transform = WorldTransform;

		if ( Mesh is not null && Scene.IsEditor )
		{
			//Compute face texture parameters on transform change but don't rebuild mesh now.
			var wasDirty = Mesh.IsDirty;
			Mesh.Transform = WorldTransform;
			Mesh.IsDirty = wasDirty;
		}

		base.TransformChanged( root );
	}

	protected override IEnumerable<PhysicsShape> CreatePhysicsShapes( PhysicsBody targetBody, Transform local )
	{
		if ( Collision == CollisionType.None )
			yield break;

		//Our models carry collision Model.Physics can't describe, so let the model add it to the body
		if ( Model?.core is { HasCollision: true } core )
		{
			var before = targetBody.Shapes.Count();
			core.AddCollision( targetBody );

			foreach ( var shape in targetBody.Shapes.Skip( before ) )
			{
				shape.Surface ??= Surface.FindByName( "default" );
				yield return shape;
			}

			yield break;
		}

		if ( Model is null || Model.Physics is null )
			yield break;

		foreach ( var part in Model.Physics.Parts )
		{
			Assert.NotNull( part, "Physics part was null" );

			var bx = local.ToWorld( part.Transform );

			if ( Collision == CollisionType.Mesh )
			{
				foreach ( var mesh in part.Meshes )
				{
					var shape = targetBody.AddShape( mesh, bx, false, true );
					Assert.NotNull( shape, "Mesh shape was null" );

					shape.Surface = mesh.Surface;
					shape.Surfaces = mesh.Surfaces;

					yield return shape;
				}
			}
			else if ( Collision == CollisionType.Hull )
			{
				foreach ( var hull in part.Hulls )
				{
					var shape = targetBody.AddShape( hull, bx );
					Assert.NotNull( shape, "Hull shape was null" );
					shape.Surface = hull.Surface;
					yield return shape;
				}
			}
		}
	}

	void RebuildRenderMesh()
	{
		if ( !Active ) return;
		if ( Mesh is null ) return;

		Mesh.Transform = WorldTransform;
		Mesh.SetSmoothingAngle( SmoothingAngle );
		Model = Mesh.Rebuild( Collision );

		if ( Model is null || Model.MeshCount == 0 )
		{
			DeleteSceneObject();
			return;
		}

		if ( Hidden ) return;

		if ( !_sceneObject.IsValid() )
		{
			_sceneObject = new SceneObject( Scene.SceneWorld, Model, WorldTransform );
		}
		else
		{
			_sceneObject.Model = Model;
			_sceneObject.Transform = WorldTransform;

			//We manually set the model, sceneobject needs to update based on any new materials in it
			_sceneObject.UpdateFlagsBasedOnMaterial();
		}

		RebuildCellObjects();
		UpdateSceneObject();
	}

	//The cells of the mesh made again from the last build, where it is wider than one - see _cellObjects.
	void RebuildCellObjects()
	{
		DeleteCellObjects();

		var cells = Mesh?.RenderCells( WorldTransform );

		if ( cells is not null )
		{
			_cellObjects = new( cells.Count );

			foreach ( var (_, model) in cells )
				_cellObjects.Add( new SceneObject( Scene.SceneWorld, model, WorldTransform ) );
		}

		if ( _sceneObject.IsValid() ) _sceneObject.RenderingEnabled = _cellObjects is null;
	}

	void DeleteCellObjects()
	{
		if ( _cellObjects is null ) return;

		foreach ( var cell in _cellObjects )
		{
			if ( !cell.IsValid() ) continue;

			cell.RenderingEnabled = false;
			cell.Delete();
		}

		_cellObjects = null;
	}

	void UpdateSceneObject()
	{
		if ( !_sceneObject.IsValid() ) return;

		Describe( _sceneObject );
		foreach ( var cell in _cellObjects ?? [] ) if ( cell.IsValid() ) Describe( cell );
	}

	//Lightmap rect (xy scale, zw offset) for the mesh and its cells, which share its sheet; set by the bake, not saved
	internal System.Numerics.Vector4 LightmapRect
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;
			UpdateSceneObject();
		}
	}

	void Describe( SceneObject one )
	{
		one.Component = this;
		one.LightmapRect = LightmapRect;

		//Says what it is, which the frame cannot work out: to the renderer a mesh is triangles with a material,
		//like a prop standing on it. The editor's grid reads it - see CoreSceneDrawer.DrawSurfaceGrid.
		one.Flags.IsEditableMesh = true;

		one.Tags.SetFrom( GameObject.Tags );
		one.Flags.CastShadows = RenderType == ShadowRenderType.On || RenderType == ShadowRenderType.ShadowsOnly;
		one.Flags.IsStatic = GameObject.IsStatic;
	}
}
