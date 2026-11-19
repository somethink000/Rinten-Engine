using Rinten;
using System.Threading;

namespace Editor;

[DropObject( "material", "mat", "mat_c" )]
partial class MaterialDropObject : BaseDropObject
{
	Material material;
	Component currentPreviewComponent;
	Material previewComponentOriginalMaterial;
	int currentPreviewTriangleIndex;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		Asset asset = await InstallAsset( dragData, token );

		if ( asset is null )
			return;

		if ( token.IsCancellationRequested )
			return;

		material = asset.LoadResource<Material>();
	}

	public override void OnUpdate()
	{
		ResetPreviewMaterial();

		//Use the component the trace actually hit; Components.Get can miss it
		if ( material is not null && Target() is Component.IMaterialSetter c )
		{
			currentPreviewTriangleIndex = trace.Triangle;
			previewComponentOriginalMaterial = c.GetMaterial( currentPreviewTriangleIndex );
			currentPreviewComponent = (Component)c;

			c.SetMaterial( material, currentPreviewTriangleIndex );
		}

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
		{
			var rot = Rotation.LookAt( trace.Normal, Vector3.Up ) * Rotation.From( 90, 0, 0 );
			var pos = trace.EndPosition + trace.Normal * PivotPosition.Length;

			using var scope = Gizmo.Scope( "DropObject", new Transform( pos, rot ) );
			Gizmo.Draw.Color = Color.White;
			Gizmo.Draw.Text( PackageStatus, new Transform( Bounds.Center ), "Inter", 14 );
		}
	}

	//What the material would go on: the component the ray landed on, or - where the hit
	//named an object without naming a component - whichever of its renderers takes one.
	private Component.IMaterialSetter Target()
	{
		if ( trace.Component is Component.IMaterialSetter c )
			return c;

		if ( !trace.GameObject.IsValid() )
			return null;

		return trace.GameObject.Components.Get<Component.IMaterialSetter>( FindMode.EnabledInSelf );
	}

	//The ground this was dropped on, if it was dropped on ground.
	private Terrain Terrain()
	{
		if ( trace.Component is Terrain hit ) return hit;

		if ( !trace.GameObject.IsValid() ) return null;

		return trace.GameObject.Components.Get<Terrain>( FindMode.EnabledInSelf );
	}

	private void ResetPreviewMaterial()
	{
		if ( currentPreviewComponent.IsValid() )
		{
			((Component.IMaterialSetter)currentPreviewComponent).SetMaterial( previewComponentOriginalMaterial, currentPreviewTriangleIndex );
		}
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		ResetPreviewMaterial();

		if ( Target() is Component.IMaterialSetter c )
		{
			//Every other drop puts the scene it is dropping into on the ambient stack before it touches SceneEditorSession.Active - see ModelDropObject,
			//PrefabDropObject, DecalDropObject. This one didn't, so the undo scope it opened was pushing a change with nowhere ambient to push it into.
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Active.UndoScope( "Drop Material" ).WithComponentChanges( c as Component ).Push() )
			{
				c.SetMaterial( material, trace.Triangle );
			}
		}
		else if ( Terrain() is { } ground )
		{
			//Ground does not wear one material, it is painted out of a list of them - so a material dropped on it is a layer added rather than a surface
			//replaced. This used to be a drop object of its own, for the resource type layers used to be; they are ordinary materials now, so it is this one.
			using var scope = SceneEditorSession.Scope();

			using ( SceneEditorSession.Active.UndoScope( "Add Terrain Layer" ).WithComponentChanges( ground ).Push() )
			{
				if ( material is not null && ground.Storage is not null &&
					!ground.Storage.Materials.Contains( material ) )
				{
					ground.Storage.Materials.Add( material );
					ground.UpdateMaterials();
				}
			}

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( ground.GameObject );
		}
		else
		{
			//Log why the material didn't land: which part of the trace was empty, where, and its tags
			Log.Warning( trace.Hit
				? $"MaterialDropObject: hit an object that sets no materials at {trace.HitPosition} " +
				  $"(component {trace.Component?.GetType().Name ?? "none"}, object {trace.GameObject?.Name ?? "none"}, " +
				  $"tags [{string.Join( ", ", trace.Shape?.Tags?.TryGetAll() ?? Enumerable.Empty<string>() )}])"
				: "MaterialDropObject: trace missed, nothing to drop the material onto" );
		}
	}

	public override void OnDestroy()
	{
		if ( !Dropped )
		{
			ResetPreviewMaterial();
		}
	}
}
