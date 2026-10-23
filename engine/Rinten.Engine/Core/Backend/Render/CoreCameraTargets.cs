using System.Collections.Generic;

namespace Rinten;

//Extra scene cameras: to textures (copied each frame) or overlaid on screen; each has its own view
internal static class CoreCameraTargets
{
	private static readonly Dictionary<CameraComponent, CoreScenePreview> views = new();
	private static readonly List<CameraComponent> gone = new();
	private static readonly List<CameraComponent> onScreen = new();

	//Which scene was drawn for which frame - once a frame each, however many views ask.
	private static readonly Dictionary<Scene, long> drawnAt = new();

	//What each scene's screen cameras drew this frame, in the order they are laid down.
	private static readonly Dictionary<Scene, List<(ulong Picture, Rect Area)>> layers = new();

	//Draws every camera of the scene but the one the screen is drawn from. Called by whatever is about to draw the scene for
	//somebody to look at, before its own frame opens; a scene is drawn this way once a frame however many of those there are.
	internal static void Draw( Scene scene )
	{
		if ( !scene.IsValid() ) return;

		var frame = EngineLoop.LoopFrames;
		if ( drawnAt.TryGetValue( scene, out var was ) && was == frame ) return;
		drawnAt[scene] = frame;

		if ( !layers.TryGetValue( scene, out var laid ) )
		{
			laid = new List<(ulong, Rect)>();
			layers[scene] = laid;
		}

		laid.Clear();
		onScreen.Clear();

		var main = scene.Camera;
		var screen = Screen.Size;

		foreach ( var camera in scene.GetAllComponents<CameraComponent>() )
		{
			if ( !camera.IsValid() || !camera.Active || camera.IsSceneEditorCamera || camera == main ) continue;

			if ( camera.RenderTarget is { IsValid: true } target )
			{
				Preview( camera ).RenderInto( scene, camera, target );
				continue;
			}

			//Laid over the game's screen; an editor looking at the scene does so through a camera of its own, and has no screen for these to go on.
			if ( !scene.IsEditor ) onScreen.Add( camera );
		}

		//Lowest first: what is laid down later is over what is laid before it.
		onScreen.Sort( static ( a, b ) => a.Priority.CompareTo( b.Priority ) );

		foreach ( var camera in onScreen )
		{
			var area = AreaOf( camera.Viewport, screen );
			if ( area.Width < 1 || area.Height < 1 ) continue;

			var picture = Preview( camera ).RenderPicture( scene, camera, (int)area.Width, (int)area.Height );
			if ( picture != 0 ) laid.Add( (picture, area) );
		}

		Forget();
	}

	//The pictures a scene's screen cameras drew this frame and where each goes, lowest priority
	//first - for the interface to lay down under its panels. Empty for a scene with none.
	internal static IReadOnlyList<(ulong Picture, Rect Area)> Layers( Scene scene )
		=> scene is not null && layers.TryGetValue( scene, out var laid ) && drawnAt.GetValueOrDefault( scene ) == EngineLoop.LoopFrames
			? laid
			: Array.Empty<(ulong, Rect)>();

	//A camera's normalised rectangle - x, y, width, height - in the screen's pixels.
	private static Rect AreaOf( Vector4 viewport, Vector2 screen )
	{
		var x = Math.Clamp( viewport.x, 0.0f, 1.0f );
		var y = Math.Clamp( viewport.y, 0.0f, 1.0f );
		var width = Math.Clamp( viewport.z, 0.0f, 1.0f - x );
		var height = Math.Clamp( viewport.w, 0.0f, 1.0f - y );

		return new Rect( MathF.Round( x * screen.x ), MathF.Round( y * screen.y ),
			MathF.Round( width * screen.x ), MathF.Round( height * screen.y ) );
	}

	private static CoreScenePreview Preview( CameraComponent camera )
	{
		if ( !views.TryGetValue( camera, out var preview ) )
		{
			preview = new CoreScenePreview { GameView = true };
			views[camera] = preview;
		}

		return preview;
	}

	//Lets go of the views of cameras that are gone, switched off, or now the screen's own.
	private static void Forget()
	{
		gone.Clear();

		foreach ( var (camera, _) in views )
		{
			if ( !camera.IsValid() || !camera.Active || camera == camera.Scene?.Camera ) gone.Add( camera );
		}

		foreach ( var camera in gone )
		{
			if ( views.Remove( camera, out var preview ) ) preview.Dispose();
		}

		//Removing while walking a dictionary is allowed, so no copy of the keys every frame
		foreach ( var scene in drawnAt.Keys )
		{
			if ( scene.IsValid() ) continue;

			drawnAt.Remove( scene );
			layers.Remove( scene );
		}
	}

	//Every view, on device teardown.
	internal static void Release()
	{
		foreach ( var preview in views.Values ) preview.Dispose();

		views.Clear();
		drawnAt.Clear();
		layers.Clear();
	}
}
