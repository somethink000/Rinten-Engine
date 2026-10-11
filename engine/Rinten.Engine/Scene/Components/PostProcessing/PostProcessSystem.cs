using Rinten.Volumes;
namespace Rinten;

//Collects and applies post-process effects from cameras and volumes; supports editor previews
/// <summary>Builds the post-processing effects of cameras and volumes onto each camera, editor previews included.</summary>
public sealed partial class PostProcessSystem : GameObjectSystem<PostProcessSystem>, Component.ISceneStage
{
	[ConVar( "r_postprocess", ConVarFlags.Saved, Help = "Enable or disable post process effects." )]
	internal static bool EnablePostProcess { get; set; } = true;

	public PostProcessSystem( Scene scene ) : base( scene )
	{

	}

	//Whether the effects have been taken off the cameras since the switch went down - see EnablePostProcess.
	private bool taken;

	//Called at the very end of the scene update, after all other components have
	//been ticked. We use it to update our post processing effects for each camera.
	void Component.ISceneStage.End()
	{
		if ( Application.IsDedicatedServer )
			return;

		//When disabled, strip effects off cameras; they're rebuilt when it's turned back on
		if ( !EnablePostProcess )
		{
			//Once, when it goes off - not every tick. What a camera holds does not grow back while the switch is
			//down, and walking every camera to empty lists that are already empty is a tick's work for nothing.
			if ( !taken )
			{
				taken = true;

				cameras.Clear();
				Scene.GetAll( cameras );

				foreach ( var one in cameras )
					if ( one.IsValid() ) one.PostProcess.Clear();
			}

			return;
		}

		taken = false;

		//Editor behavior is special
		if ( Scene.IsEditor )
		{
			UpdateEditorScene();
			return;
		}

		cameras.Clear();
		Scene.GetAll( cameras );

		foreach ( var cc in cameras )
		{
			UpdateCamera( cc );
		}
	}

	//Editor viewports draw through their own hidden cameras, so build effects onto each of them too
	void UpdateEditorScene()
	{
		//Which camera the effects come from, and whether a volume is being previewed on its own instead.
		CameraComponent source = Scene.Camera;
		PostProcessVolume preview = null;

		if ( Scene.Editor?.SelectedGameObject is GameObject go )
		{
			if ( go.GetComponentInParent<CameraComponent>( false, true ) is CameraComponent selected )
			{
				source = selected;
			}
			else if ( go.GetComponentInParent<PostProcessVolume>( false, true ) is PostProcessVolume volume && volume.EditorPreview )
			{
				preview = volume;
			}
		}

		//And which cameras they are hung on: every viewport's own, and the scene's, which is what
		//the game view draws through. A snapshot, as the array was, into a list kept between ticks.
		cameras.Clear();
		Scene.GetAll( cameras );

		foreach ( var target in cameras )
		{
			if ( !target.IsValid() ) continue;
			if ( !target.IsSceneEditorCamera && target != Scene.Camera ) continue;

			if ( preview is not null ) PreviewVolume( preview, target );
			else if ( source.IsValid() ) UpdateCamera( source, target );
			else target.PostProcess.Clear();

		}
	}



	//Builds effects from source onto target; they differ in the editor, where target is a viewport camera
	private void UpdateCamera( CameraComponent source, CameraComponent target = null )
	{
		target ??= source;

		target.PostProcess.Clear();

		if ( !source.EnablePostProcessing || !target.EnablePostProcessing )
			return;

		var anchor = target.PostProcessAnchor.IsValid() ? target.PostProcessAnchor : source.PostProcessAnchor;
		var pos = anchor.IsValid() ? anchor.WorldPosition : target.WorldPosition;

		//Lists cleared and reused: this runs per camera per tick
		effects.Clear();

		gathered.Clear();
		source.GetComponentsInChildren( gathered );

		foreach ( var effect in gathered )
		{
			if ( Keeps( effect ) ) effects.Add( new WeightedEffect { Effect = effect, Weight = 1 } );
		}

		//A scene without a volume system has no volumes - a preview rendering one asset has neither
		if ( Scene.GetSystem<VolumeSystem>() is { } volumeSystem )
		{
			inVolumes.Clear();
			volumeSystem.FindAll( pos, inVolumes );

			standing.Clear();

			foreach ( var volume in inVolumes )
			{
				if ( volume.IsValid() ) standing.Add( volume );
			}

			//By priority, which decides who overrides whom; sorted in place, as the list is this system's own
			standing.Sort( static ( a, b ) => a.Priority.CompareTo( b.Priority ) );

			foreach ( var volume in standing )
			{
				var weight = volume.GetWeight( pos );

				gathered.Clear();
				volume.GetComponentsInChildren( gathered );

				foreach ( var effect in gathered )
				{
					if ( Keeps( effect ) ) effects.Add( new WeightedEffect { Effect = effect, Weight = weight } );
				}
			}
		}

		Build( target );
	}

	//Whether an effect goes in: a live one, and in a headset one it can take - see BasePostProcess.AllowedInHeadset
	private static bool Keeps( BasePostProcess effect )
		=> effect.IsValid() && ( !Game.IsRunningInVR || effect.AllowedInHeadset );

	//Builds effects one kind at a time so volume copies blend with the camera's; groups without GroupBy
	private void Build( CameraComponent target )
	{
		//Before the effects, which may step aside for it
		CoreRayLight.Build( target );

		foreach ( var of in kinds.Values ) of.Clear();

		foreach ( var entry in effects )
		{
			var kind = entry.Effect.GetType();

			if ( !kinds.TryGetValue( kind, out var of ) )
			{
				of = new List<WeightedEffect>();
				kinds[kind] = of;
			}

			of.Add( entry );
		}

		foreach ( var of in kinds.Values )
		{
			if ( of.Count == 0 ) continue;

			var ctx = new PostProcessContext
			{
				Camera = target,
				//The list itself: an effect reads it inside its Build and nowhere after, so it is not copied
				Components = of,
			};

			of[0].Effect.Build( ctx );
		}
	}

	//What one camera's effects are gathered into, kept between ticks.
	private readonly List<WeightedEffect> effects = new();

	//The queries' own results, kept between ticks as well. Each of these was a fresh list or a pair of iterators per
	//camera per tick - the cameras, the effects under a camera and under each volume, and the volumes at a point.
	private readonly List<CameraComponent> cameras = new();
	private readonly List<BasePostProcess> gathered = new();
	private readonly List<PostProcessVolume> inVolumes = new();

	//And the volumes it is standing in, to be sorted by priority.
	private readonly List<PostProcessVolume> standing = new();

	//The gathered effects by kind, kept between ticks with their lists, which are cleared and reused: the same
	//handful of kinds appears every tick
	private readonly Dictionary<Type, List<WeightedEffect>> kinds = new();

	//Called in editor mode, when a volume is selected: only what that volume does, on whichever camera is drawing.
	private void PreviewVolume( PostProcessVolume volume, CameraComponent target )
	{
		target.PostProcess.Clear();

		if ( !target.EnablePostProcessing )
			return;

		effects.Clear();

		gathered.Clear();
		volume.GetComponentsInChildren( gathered );

		foreach ( var effect in gathered )
		{
			if ( Keeps( effect ) ) effects.Add( new WeightedEffect { Effect = effect, Weight = 1 } );
		}

		Build( target );
	}
}
