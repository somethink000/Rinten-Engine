using System.Threading;

namespace Editor;

[DropObject( "model", "mdl", "mdl_c" )]
partial class ModelDropObject : BaseDropObject
{
	Model model;
	string modelName;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		Asset asset = await InstallAsset( dragData, token );

		if ( asset is null )
			return;

		if ( token.IsCancellationRequested )
			return;

		modelName = System.IO.Path.GetFileNameWithoutExtension( asset.Name ?? asset.Path );

		PackageStatus = "Loading Model";
		model = await Model.LoadAsync( asset.Path );
		PackageStatus = null;

		Bounds = model.Bounds;
		PivotPosition = Bounds.ClosestPoint( Vector3.Down * (254f) );
	}

	public override void OnUpdate()
	{
		using var scope = Gizmo.Scope( "DropObject", traceTransform );

		Gizmo.Draw.Color = Color.White.WithAlpha( 0.3f );
		Gizmo.Draw.LineBBox( Bounds );

		Gizmo.Draw.Color = Color.White;

		if ( model is not null )
		{
			var so = Gizmo.Draw.Model( model );
			if ( so.IsValid() )
			{
				so.Flags.CastShadows = true;
				so.UpdateToBindPose();
			}
		}

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
		{
			Gizmo.Draw.Text( PackageStatus, new Transform( Bounds.Center ), "Inter", 14 );
		}
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		if ( model is null )
			return;

		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Active.UndoScope( "Drop Model" ).WithGameObjectCreations().Push() )
		{
			GameObject = new GameObject( false );
			GameObject.Name = string.IsNullOrWhiteSpace( modelName ) ? model.ResourceName : modelName;
			GameObject.WorldTransform = traceTransform;

			Build( model );

			GameObject.Enabled = true;

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( GameObject );
		}
	}

	//Adds the components the model needs (skinning, morphs, collision), not just a renderer
	private void Build( Model model )
	{
		//Bones, blend shapes, or a graph of its own: three different reasons a model is more than geometry, and the same renderer answers all three.
		var animated = model.BoneCount > 0
			|| model.MorphCount > 0
			|| model.DefaultAnimationGraph is not null;

		ModelRenderer renderer = animated
			? GameObject.Components.Create<SkinnedModelRenderer>()
			: GameObject.Components.Create<ModelRenderer>();

		renderer.Model = model;

		var parts = model.Physics?.Parts?.Count ?? 0;
		if ( parts == 0 ) return;

		//The collision is what makes it a prop here; the preference turns it off.
		if ( !EditorPreferences.PlaceModelsAsProp ) return;

		//One body is a prop's collision and belongs on a collider; several are jointed together, which is a ragdoll and the thing ModelPhysics
		//is for. Giving a prop the ragdoll would have it fall over the moment the scene ran, which is not what dropping a crate asks for.
		if ( parts > 1 && model.BoneCount > 0 )
		{
			GameObject.Components.Create<ModelPhysics>().Model = model;
			return;
		}

		GameObject.Components.Create<ModelCollider>().Model = model;
	}
}
