using NativeEngine;

namespace Rinten;

/// <summary>The ground: an editable mesh, baked into chunk models, drawn and collided as it is.</summary>
[Expose]
[Category( "World" )]
public sealed partial class Terrain : Collider, Component.ExecuteInEditor, Component.IHasBounds
{
	public override bool IsConcave => true;

	//Between OnEnabled and OnDisabled: storage set while the scene is read comes first and would be laid out twice
	private bool running;

	protected override void OnEnabled()
	{
		running = true;
		Create();
		Transform.OnTransformChanged += OnTerrainChanged;
		Storage?.MaterialSettings?.OnChanged += OnTerrainChanged;
	}

	protected override void OnDisabled()
	{
		running = false;
		DestroyInternal();
	}

	protected override void OnDestroy()
	{
		DestroyInternal();

		//The material is this terrain's alone, and so is what it was built into on the device: given back now
		//rather than whenever it is collected - every copy of the scene play mode ran made one of its own.
		CoreResources.Forget( ownMaterial );
		ownMaterial = null;
	}

	void DestroyInternal()
	{
		Transform.OnTransformChanged -= OnTerrainChanged;
		Storage?.MaterialSettings?.OnChanged -= OnTerrainChanged;

		DisposeRenderResources();
	}

	//Tear down the chunks and the numbers picture. Shared by DestroyInternal and Create (which rebuilds them).
	void DisposeRenderResources()
	{
		ReleaseChunks();
		DropRelief();
		DropMask();
		DropGround();

		if ( watched is not null ) watched.Changed -= MeshChanged;
		watched = null;

		ParamsMap?.Dispose();
		ParamsMap = null;
	}

	void OnTerrainChanged()
	{
		if ( chunks.Length == 0 ) return;
		UpdateTerrainNumbers();
	}

	protected override void OnUpdate()
	{
		if ( chunks.Length == 0 )
			return;

		//A storage whose mesh was made again - an import, a new resolution.
		if ( Storage?.Mesh is { } mesh && !ReferenceEquals( mesh, watched ) )
		{
			Create();
			return;
		}

		//What waits to be baked and what the workers finished; then the relief ring, the paint mask and the ground's heights
		PumpChunks();
		PumpRelief();
		PumpMask();
		PumpGround();
	}

	protected override void OnPreRender()
	{
		if ( chunks.Length == 0 || Storage is null )
			return;

		//What the shader reads its numbers out of. Rewritten here rather than only when something changes: a streamed
		//layer texture lands in the bindless table whenever it finishes loading, and its index is one of the numbers.
		UpdateParamsMap();

		//Request the params texture index every frame; that request is what uploads it to the device
		NameParams();

		foreach ( var layer in Storage.Materials )
		{
			if ( layer is null ) continue;

			foreach ( var name in LayerMaps )
			{
				layer.GetTexture( name )?.MarkUsed( 4096 );
			}
		}
	}

	//The pictures a painted layer is made of, by the name its material binds them to.
	private static readonly string[] LayerMaps = ["Color", "Normal", "g_tHeight"];

	protected override void OnTagsChanged()
	{
		base.OnTagsChanged(); //Collider
		EachObject( ( one, _ ) => one.Tags.SetFrom( Tags ) );
	}

	/// <summary>Call on enable or storage change</summary>
	public void Create()
	{
		if ( !Active || !running )
			return;

		DisposeRenderResources();

		if ( Storage is null )
			return;

		//Everything that lays something over a terrain and has not found one yet.
		if ( Scene is not null )
		{
			foreach ( var insert in Scene.GetAll<TerrainInsert>() )
				if ( insert.IsValid() && !insert.Terrain.IsValid() ) insert.Attach();
		}

		CreateChunks();

		if ( !Application.IsHeadless )
		{
			UpdateTerrainNumbers();
			UpdateMaterials();
		}

		//Rebuild the collider
		Rebuild();
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		if ( !Gizmo.Settings.Selection )
			return;

		if ( Gizmo.IsSelected && Storage?.Mesh is { } mesh )
		{
			Gizmo.Draw.Color = Color.White.WithAlpha( 0.4f );
			Gizmo.Draw.LineBBox( mesh.Bounds );
		}
	}

	/// <summary>Given a world ray, finds out the LOCAL position it intersects with this terrain.</summary>
	public bool RayIntersects( Ray ray, float distance, out Vector3 position )
	{
		return RayIntersects( ray, distance, out position, out _, out _ );
	}

	/// <summary>Given a world ray, finds out the LOCAL position it intersects with this terrain.</summary>
	public bool RayIntersects( Ray ray, float distance, out Vector3 position, out Vector3 normal )
	{
		return RayIntersects( ray, distance, out position, out normal, out _ );
	}

	//Where a ray meets the ground as it was sculpted, in the terrain's own space.
	internal bool RayIntersects( Ray ray, float distance, out Vector3 position, out Vector3 normal, out float fraction )
	{
		position = default;
		normal = Vector3.Up;
		fraction = 1f;

		if ( Storage?.Mesh is not { } mesh || distance <= 0.0f )
			return false;

		var local = ray.ToLocal( WorldTransform );
		var direction = local.Forward.Normal;
		var scale = MathF.Max( WorldTransform.UniformScale, 1e-6f );

		if ( !mesh.Trace( local.Position, direction, distance / scale, out var travelled, out _, out normal ) )
			return false;

		position = local.Position + ( direction * travelled );
		fraction = Math.Clamp( travelled * scale / distance, 0.0f, 1.0f );

		return true;
	}

	/// <summary>How high the ground is over a point of the terrain's own plane: its highest surface there, which under an overhang is the overhang's.</summary>
	public float HeightAt( Vector2 local )
	{
		if ( Storage?.Mesh is not { } mesh )
			return 0.0f;

		var top = mesh.Bounds.Maxs.y + 1.0f;
		var reach = top - mesh.Bounds.Mins.y + 2.0f;

		return mesh.Trace( new Vector3( local.x, top, local.y ), Vector3.Down, reach, out var travelled, out _, out _ )
			? top - travelled
			: 0.0f;
	}

	/// <summary>Which way that surface faces, in the terrain's own space.</summary>
	public Vector3 NormalAt( Vector2 local )
	{
		if ( Storage?.Mesh is not { } mesh )
			return Vector3.Up;

		var top = mesh.Bounds.Maxs.y + 1.0f;
		var reach = top - mesh.Bounds.Mins.y + 2.0f;

		return mesh.Trace( new Vector3( local.x, top, local.y ), Vector3.Down, reach, out _, out _, out var normal )
			? normal
			: Vector3.Up;
	}
}
