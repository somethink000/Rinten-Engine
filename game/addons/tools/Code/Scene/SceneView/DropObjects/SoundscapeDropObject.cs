using System.Threading;

namespace Editor;

[DropObject( "soundscape", "sndscape", "sndscape_c" )]
partial class SoundscapeDropObject : BaseDropObject
{
	Soundscape sound;

	protected override async Task Initialize( string dragData, CancellationToken token )
	{
		Asset asset = await InstallAsset( dragData, token );

		if ( asset is null )
			return;

		if ( token.IsCancellationRequested )
			return;

		PackageStatus = "Loading Sound";
		sound = asset.LoadResource<Soundscape>();
		PackageStatus = null;
	}

	public override void OnUpdate()
	{
		using var scope = Gizmo.Scope( "DropObject", traceTransform );

		Gizmo.Draw.Color = Color.White;
		Gizmo.Draw.Sprite( Vector3.Zero, 0.7f * Gizmo.Settings.GizmoScale, "materials/gizmo/soundscape.png" );

		if ( !string.IsNullOrWhiteSpace( PackageStatus ) )
		{
			Gizmo.Draw.Text( PackageStatus, new Transform( Vector3.Up * 0.4f ), "Inter", 14 );
		}
	}

	public override async Task OnDrop()
	{
		await WaitForLoad();

		if ( sound is null )
			return;

		using var scene = SceneEditorSession.Scope();

		using ( SceneEditorSession.Active.UndoScope( "Drop Soundscape" ).WithGameObjectCreations().Push() )
		{
			GameObject = new GameObject();
			GameObject.Name = sound.ResourceName;
			GameObject.WorldTransform = traceTransform;

			var component = GameObject.Components.GetOrCreate<SoundscapeTrigger>();
			component.Soundscape = sound;

			EditorScene.Selection.Clear();
			EditorScene.Selection.Add( GameObject );
		}
	}
}
