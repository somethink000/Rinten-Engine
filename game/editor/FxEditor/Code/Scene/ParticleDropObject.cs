using System.Threading;

namespace Editor.FxEditor;

//Lets a .fx be dropped into the scene as an object with a ParticlePlayer playing it
[DropObject( "fx", "fx" )]
partial class ParticleDropObject : BaseDropObject
{
	ParticleDefinition _definition;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		var asset = await InstallAsset( dragData, token );

		if ( asset is null || token.IsCancellationRequested )
			return;

		PackageStatus = "Loading Effect";
		_definition = asset.LoadResource<ParticleDefinition>();
		PackageStatus = null;
	}

	//The effect itself is not played here: it would have to be built into the scene to be seen, and what is being dragged is not in the scene yet. The handle the player wears is what stands in for it, the same one a dropped sound shows.
	/// <summary>What is under the cursor while the effect is being carried.</summary>
	public override void OnUpdate()
	{
		using var scope = Gizmo.Scope( "DropObject", traceTransform );

		Gizmo.Draw.Color = Color.White;
		Gizmo.Draw.Sprite( Vector3.Zero, 0.7f * Gizmo.Settings.GizmoScale, "materials/gizmo/particles.png" );

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
			Gizmo.Draw.Text( PackageStatus, new Transform( Vector3.Up * 0.4f ), "Inter", 14 );
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		if ( _definition is null )
			return;

		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Active.UndoScope( "Drop Particle Effect" ).WithGameObjectCreations().Push() )
		{
			GameObject = new GameObject();
			GameObject.Name = System.IO.Path.GetFileNameWithoutExtension( _definition.ResourceName ?? "Effect" );
			GameObject.WorldTransform = traceTransform;

			var player = GameObject.Components.GetOrCreate<ParticlePlayer>();
			player.Definition = _definition;

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( GameObject );
		}
	}
}
