using static Rinten.Component;

namespace Rinten;

/// <summary>Renders a model in the world</summary>
[Title( "Model Renderer" )]
[Category( "Rendering" )]
[Icon( "free_breakfast" )]
[Alias( "ModelComponentMate", "ModelComponent" )]
public partial class ModelRenderer : Renderer, ExecuteInEditor, ITintable, IMaterialSetter, IHasBounds, IHasModel
{
	//Bumped on any change to how it is drawn, so the renderer table can skip unchanged ones
	internal int DrawVersion { get; private set; }

	ulong _bodyGroups = ulong.MaxValue;

	[Property]
	public Model Model
	{
		get;
		set
		{
			if ( field == value ) return;

			field = value;

			DrawChanged();
			OnModelChanged();
		}
	}

	Color _tint = "#FFFFFF";

	[Property]
	public Color Tint
	{
		get => _tint;
		set
		{
			if ( _tint == value ) return;
			_tint = value;

			DrawChanged();

			if ( _sceneObject.IsValid() )
			{
				_sceneObject.ColorTint = Tint;
			}
		}
	}

	[Property]
	public bool CreateAttachments
	{
		get => _createAttachments;
		set
		{
			if ( _createAttachments == value ) return;
			_createAttachments = value;

			DrawChanged();
			UpdateObject();
		}
	}

	[Property, Model.BodyGroupMask, ShowIf( nameof( HasBodyGroups ), true )]
	[DefaultValue( ulong.MaxValue )]
	public ulong BodyGroups
	{
		get => _bodyGroups;
		set
		{
			if ( _bodyGroups == value ) return;
			_bodyGroups = value;

			DrawChanged();
			Update();
		}
	}

	public bool HasBodyGroups => Model?.Parts.All.Sum( x => x.Choices.Count ) > 1;

	[Property, Model.MaterialGroup, ShowIf( nameof( HasMaterialGroups ), true )]
	[DefaultValue( default )]
	public string MaterialGroup
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			DrawChanged();
			Update();
		}
	}

	public bool HasMaterialGroups => Model?.MaterialGroupCount > 0 && MaterialOverride is null;

	[Title( "Cast Shadows" ), Property, Category( "Lighting" )]
	public ShadowRenderType RenderType
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			DrawChanged();
			Update();
		}
	} = ShadowRenderType.On;

	/// <summary>Lightmap density against the bake's: above one for detail and contact, below for plain walls.</summary>
	[Property, Category( "Lighting" ), Range( 0.125f, 8.0f )]
	public float LightmapScale { get; set; } = 1.0f;

	//Lightmap atlas rect (xy scale, zw offset); set by the bake on load, not saved
	internal System.Numerics.Vector4 LightmapRect
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			DrawChanged();
		}
	}

	//The same in every view - camera, sun pages, lamp faces - so a shadow's caster is the shape drawn; see CullCluster
	/// <summary>Multiplies the error this model's clusters may carry: two draws it twice as coarse, a half twice as fine.</summary>
	[Title( "Detail Bias" ), Property, Category( "Lighting" ), Range( 0.1f, 8.0f )]
	public float LodBias
	{
		get;
		set
		{
			value = Math.Clamp( value, 0.1f, 8.0f );
			if ( field == value ) return;
			field = value;

			DrawChanged();
		}
	} = 1.0f;

	/// <summary>Project detail group (LOD curve); None uses the model's own. Multiplies with LodBias.</summary>
	[Title( "Detail Group" ), Property, Category( "Lighting" ), LodGroupName]
	public string LodGroup
	{
		get;
		set
		{
			if ( string.Equals( field, value, StringComparison.Ordinal ) ) return;
			field = value;

			DrawChanged();
		}
	}

	//Only this group changes; an unset MaxValue mask is first resolved to the model default
	/// <summary>Set body group value by name</summary>
	public void SetBodyGroup( string name, int value )
	{
		if ( !Model.IsValid() ) return;

		BodyGroups = Model.Parts.WithChoice( Model.Parts.Resolve( BodyGroups ), name, value );
	}

	/// <summary>Set body group value by name and choice</summary>
	public void SetBodyGroup( string name, string choice )
	{
		if ( !Model.IsValid() ) return;

		var part = Model.Parts.Get( name );
		if ( part is null ) return;

		var index = part.GetChoiceIndex( choice );
		if ( index < 0 ) return;

		BodyGroups = Model.Parts.WithChoice( Model.Parts.Resolve( BodyGroups ), name, index );
	}

	/// <summary>Set body group value by index</summary>
	public void SetBodyGroup( int part, int value )
	{
		if ( !Model.IsValid() ) return;

		BodyGroups = Model.Parts.WithChoice( Model.Parts.Resolve( BodyGroups ), part, value );
	}

	/// <summary>Get body group value by name</summary>
	public int GetBodyGroup( string name )
	{
		if ( !Model.IsValid() ) return 0;

		return Model.Parts.ChoiceOf( Model.Parts.Resolve( BodyGroups ), name );
	}

	/// <summary>Get body group value by index</summary>
	public int GetBodyGroup( int part )
	{
		if ( !Model.IsValid() ) return 0;

		return Model.Parts.ChoiceOf( Model.Parts.Resolve( BodyGroups ), part );
	}

	internal SceneObject _sceneObject;
	public SceneObject SceneObject => _sceneObject;

	Color ITintable.Color { get => Tint; set => Tint = value; }

	internal void OnModelReloaded()
	{
		if ( !Active ) return;

		OnModelChanged();
	}

	internal Action ModelChanged;

	internal void OnModelChanged()
	{
		ForgetCullSphere();

		if ( _sceneObject.IsValid() )
			UpdateObject();

		ModelChanged?.Invoke();
	}

	//Drawn while the field is empty: the error marker, or the cube if the marker won't load
	internal Model ModelOrError => Model ?? Rinten.Model.Error ?? Rinten.Model.Cube;

	protected virtual void UpdateObject()
	{
		BuildAttachmentHierarchy();

		if ( !_sceneObject.IsValid() )
			return;

		var model = ModelOrError;

		_sceneObject.ColorTint = Tint;
		_sceneObject.Model = model;
		_sceneObject.MeshGroupMask = BodyGroups;
		_sceneObject.Flags.CastShadows = RenderType == ShadowRenderType.On || RenderType == ShadowRenderType.ShadowsOnly;
		_sceneObject.Flags.IsStatic = GameObject.IsStatic;
		_sceneObject.RenderingEnabled = model.HasRenderMeshes();

		RenderOptions.Apply( _sceneObject );

		//Set from the answer either way, so leaving shadows-only turns it back off
		_sceneObject.Flags.SetFlag( Rendering.SceneObjectFlags.ExcludeGameLayer,
			RenderType == ShadowRenderType.ShadowsOnly || ( !RenderOptions.Game && !RenderOptions.Overlay ) );

		if ( HasMaterialGroups )
		{
			_sceneObject.SetMaterialOverride( null );
			_sceneObject.SetMaterialGroup( MaterialGroup );
		}
		else
		{
			_sceneObject.SetMaterialOverride( MaterialOverride );
		}

	}

	protected override void OnEnabled()
	{
		Assert.True( !_sceneObject.IsValid(), "_sceneObject should be null - disable wasn't called" );
		Assert.NotNull( Scene, "Scene should not be null" );

		var model = ModelOrError;

		_sceneObject = new SceneObject( Scene.SceneWorld, model, WorldTransform );
		OnSceneObjectCreated( _sceneObject );

		Transform.OnTransformChanged += OnTransformChanged;
	}

	internal override void OnSceneObjectCreated( SceneObject o )
	{
		base.OnSceneObjectCreated( o );

		if ( o.IsValid() )
		{
			o.Transform = WorldTransform;
			o.Component = this;
			o.Tags.SetFrom( GameObject.Tags );
		}

		ApplyMaterialOverrides();
		UpdateObject();
		OnTransformChanged();
	}

	internal override void OnDisabledInternal()
	{
		try
		{
			ClearAttachmentHierarchy();
		}
		finally
		{
			Transform.OnTransformChanged -= OnTransformChanged;

			BackupRenderAttributes( _sceneObject?.Attributes );
			_sceneObject?.Delete();
			_sceneObject = null;
		}

		base.OnDisabledInternal();
	}

	private void OnTransformChanged()
	{
		ForgetCullSphere();

		if ( _sceneObject.IsValid() )
		{
			_sceneObject.Transform = Transform.InterpolatedWorld;
		}
	}

	//Tags have been updated - lets update our scene object tags
	internal override void OnTagsUpdatedInternal()
	{
		base.OnTagsUpdatedInternal();

		if ( !_sceneObject.IsValid() ) return;

		_sceneObject.Tags.SetFrom( GameObject.Tags );
	}

	public enum ShadowRenderType
	{
		//Render the model with shadows
		[Icon( "wb_shade" )]
		On,
		//Render the model without shadows
		[Icon( "wb_twilight" )]
		Off,
		//Render ONLY the models shadows
		[Icon( "hide_source" )]
		[Title( "Shadows Only" )]
		ShadowsOnly,
	}

	void Update()
	{
		UpdateObject();
		ApplyMaterialOverrides();
	}

	protected override void OnRenderOptionsChanged()
	{
		if ( _sceneObject.IsValid() )
		{
			RenderOptions.Apply( _sceneObject );
		}

		//Which pass it is drawn in is resolved into the tables - see CoreSceneTables.DrawLayer.
		DrawChanged();
	}

	internal override bool GizmoOnlyCatches => true;

	//Whether its hitbox can catch the ray at all, by the cull sphere it keeps; Gizmo.Hitbox.Model tests the mesh
	internal override bool GizmoMayCatch( Ray ray, float depth )
	{
		CullSphere( out var centre, out var radius );

		var towards = centre - ray.Position;
		var along = Vector3.Dot( towards, ray.Forward );
		var across = towards.LengthSquared - ( along * along );

		if ( across > radius * radius || along + radius < 0.0f || along - radius > depth ) return false;

		return !Game.IsPlaying && RenderType != ShadowRenderType.ShadowsOnly && Model.IsValid();
	}

	//Lays a mesh-shaped hitbox so the model can be clicked in the viewport; draws nothing
	protected override void DrawGizmos()
	{
		if ( Game.IsPlaying ) return;

		//One that is only there to cast a shadow is not on the screen to be pointed
		//at. Off is not that - it means drawn without a shadow, which is still drawn.
		if ( RenderType == ShadowRenderType.ShadowsOnly ) return;

		if ( !Model.IsValid() ) return;

		Gizmo.Transform = WorldTransform;
		Gizmo.Hitbox.Model( Model );

		//Draw a hover box, since the hitbox took hover from whatever used to show it; skipped when selected
		if ( !Gizmo.IsHovered || Gizmo.IsSelected ) return;

		Gizmo.Draw.LineThickness = 1.0f;
		Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( 0.6f );
		Gizmo.Draw.LineBBox( Model.Bounds );
	}

	public void SetMaterial( Material material, int triangle = -1 )
	{
		MaterialOverride = material;
	}

	public Material GetMaterial( int triangle = -1 )
	{
		return MaterialOverride;
	}

	/// <summary>Copy everything from another renderer</summary>
	public override void CopyFrom( Renderer other )
	{
		base.CopyFrom( other );

		if ( other is ModelRenderer mr )
		{
			Model = mr.Model;
			Tint = mr.Tint;
			BodyGroups = mr.BodyGroups;
			MaterialGroup = mr.MaterialGroup;

			taggedMaterialOverrides = mr.taggedMaterialOverrides is not null ? new( mr.taggedMaterialOverrides ) : null;
			ApplyMaterialOverrides();
		}
	}

	void IHasModel.OnModelReloaded()
	{
		OnModelReloaded();
	}
}
