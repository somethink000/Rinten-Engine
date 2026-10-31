using System;

namespace Editor;

//What the editor draws over a scene that is not the scene: the outline around what is selected, the
//one under the pointer, the light a prefab is dressed with, and the bar the camera's speed is shown on.
internal sealed class SceneDecorations
{
	//How long the speed bar stays up after the last turn of the wheel, and how long it takes to go.
	private const float SpeedBarHeld = 1.0f;
	private const float SpeedBarFade = 0.5f;

	private readonly ViewportState state;
	private readonly Func<Vector2> surfaceSize;

	public SceneDecorations( ViewportState state, Func<Vector2> surfaceSize )
	{
		this.state = state;
		this.surfaceSize = surfaceSize;
	}

	/// <summary>Lights a prefab scene that has no light of its own with the preview light, unless the viewport settings turn it off.</summary>
	public void DressPrefabScene( Scene scene )
	{
		if ( scene is not PrefabScene prefabScene )
			return;

		if ( !prefabScene.Active )
			return;

		DressPrefabLight( prefabScene );
	}

	//Turned to the view's starting yaw - see SceneViewportPanel.State
	void DressPrefabLight( PrefabScene prefabScene )
	{
		if ( state.EnablePrefabLighting )
			PreviewLighting.Light( prefabScene, new Angles( 0, 45, 0 ) );
		else
			PreviewLighting.Remove( prefabScene );
	}

	//The tag the viewport's hover trace passes over, as the array WithoutTags takes - made once, not every frame.
	static readonly string[] HiddenTag = { "hidden" };

	//Which selected renderers get a box: a walk of every descendant, so worked out at a rate rather than each frame
	readonly List<ModelRenderer> selectedRenderers = new();
	readonly List<(ModelRenderer Renderer, float Apparent)> boxedRenderers = new();
	const float BoxedRenderersSeconds = 0.1f;
	int boxedRenderersHash;
	RealTimeSince boxedRenderersFound;

	//A big selection boxes only what looks largest: past this the boxes are noise, and cost a frame
	const int MostBoxes = 256;

	public void DrawSelection( SceneEditorSession session )
	{
		if ( session is null ) return;
		if ( !Gizmo.Settings.Selection ) return;

		var hash = session.Selection.GetHashCode();

		if ( hash != boxedRenderersHash || boxedRenderersFound >= BoxedRenderersSeconds )
		{
			boxedRenderersHash = hash;
			boxedRenderersFound = 0;
			FindBoxedRenderers( session );
		}

		//Each box's transform is read here, so a dragged selection's boxes keep up
		foreach ( var (modelRenderer, _) in boxedRenderers )
		{
			if ( !modelRenderer.IsValid() || modelRenderer.Model == null )
				continue;

			using ( Gizmo.ObjectScope( modelRenderer.GameObject, default ) )
			{
				Gizmo.Transform = modelRenderer.GameObject.WorldTransform;
				Gizmo.Draw.Color = Gizmo.Colors.Selected;
				Gizmo.Draw.LineBBox( modelRenderer.Model.Bounds );
			}
		}
	}

	void FindBoxedRenderers( SceneEditorSession session )
	{
		selectedRenderers.Clear();
		boxedRenderers.Clear();

		foreach ( var selected in session.Selection )
		{
			if ( selected is GameObject go )
				go.Components.GetAll( FindMode.InSelf | FindMode.Enabled | FindMode.InDescendants, selectedRenderers );
		}

		var frustum = Gizmo.Camera.GetFrustum( new Rect( 0, 0, Gizmo.Camera.Size.x, Gizmo.Camera.Size.y ) );
		var eye = Gizmo.Camera.Position;

		foreach ( var modelRenderer in selectedRenderers )
		{
			if ( modelRenderer.Model == null )
				continue;

			var world = modelRenderer.Model.Bounds.Transform( modelRenderer.GameObject.WorldTransform );

			if ( frustum.IsInside( world, true ) )
				boxedRenderers.Add( (modelRenderer, world.Size.Length / Math.Max( world.Center.Distance( eye ), 0.01f )) );
		}

		if ( boxedRenderers.Count > MostBoxes )
		{
			boxedRenderers.Sort( static ( a, b ) => b.Apparent.CompareTo( a.Apparent ) );
			boxedRenderers.RemoveRange( MostBoxes, boxedRenderers.Count - MostBoxes );
		}

		selectedRenderers.Clear();
	}

	public void DrawHovered( SceneEditorSession session )
	{
		if ( session is null ) return;
		if ( !Gizmo.Settings.Selection ) return;

		//Trace models in the scene, hover the hit one
		var tr = session.Scene.Trace.Ray( Gizmo.CurrentRay, Gizmo.RayDepth )
					.UseRenderMeshes( true )
					.UsePhysicsWorld( false )
					.WithoutTags( HiddenTag )
					.Run();

		if ( !tr.Hit || !tr.Component.IsValid() )
			return;

		using ( Gizmo.ObjectScope( tr.GameObject, tr.GameObject.WorldTransform ) )
		{
			Gizmo.Hitbox.DepthBias = 1;
			Gizmo.Hitbox.TrySetHovered( tr.Distance );

			if ( !Gizmo.IsHovered )
				return;

			if ( tr.Component is ModelRenderer mr && mr.Model is not null && !session.Selection.Contains( tr.GameObject ) )
			{
				Gizmo.Draw.Color = Gizmo.Colors.Active.WithAlpha( MathF.Sin( RealTime.Now * 20.0f ).Remap( -1, 1, 0.3f, 0.8f ) );
				Gizmo.Draw.LineBBox( mr.Model.Bounds );
			}
		}
	}

	/// <summary>Camera speed bar on the right, shown briefly while changing; logarithmic scale</summary>
	public void DrawCameraSpeed( float timeSinceChange )
	{
		if ( timeSinceChange >= SpeedBarHeld + SpeedBarFade )
			return;

		var fade = Math.Clamp( (SpeedBarHeld + SpeedBarFade - timeSinceChange) / SpeedBarFade, 0f, 1f );

		using ( Gizmo.Scope( "speed-overlay" ) )
		{
			var view = new Rect( 0, 0, surfaceSize().x, surfaceSize().y );

			var width = 5f;
			var height = MathF.Min( 200f, view.Height * 0.4f );
			var margin = 16f;

			var track = new Rect( view.Width - margin - width, (view.Height - height) * 0.5f, width, height );

			var speed = EditorPreferences.CameraSpeed;
			var filled = SpeedFraction( speed );

			var fill = new Rect( track.Left, track.Bottom - (track.Height * filled), track.Width, track.Height * filled );

			//The well, then what is in it. Same two colours as a field anywhere else in the editor: a dark hole and the accent.
			Gizmo.Draw.Color = Color.White;

			Gizmo.Draw.ScreenRect( track, Theme.Sunken.WithAlpha( 0.85f * fade ),
				new Vector4( width * 0.5f ), Theme.Edge.WithAlpha( fade ), new Vector4( 1 ) );

			if ( filled > 0.001f )
			{
				Gizmo.Draw.ScreenRect( fill, Theme.Primary.WithAlpha( fade ), new Vector4( width * 0.5f ) );
			}

			//The number, above the bar and away from the edge, in the same ink as everything else.
			Gizmo.Draw.Color = Theme.Ink.WithAlpha( fade );

			Gizmo.Draw.ScreenText( $"{speed:0.##} m/s",
				new Vector2( track.Right, track.Top - 10f ), "Inter", 12f, TextFlag.RightBottom );
		}
	}

	//Where a speed sits on the bar, nought at the bottom and one at the top.
	private static float SpeedFraction( float speed )
	{
		const float slowest = 0.25f;
		const float fastest = 100.0f;

		var at = MathF.Log( Math.Clamp( speed, slowest, fastest ) / slowest );
		var whole = MathF.Log( fastest / slowest );

		return Math.Clamp( at / whole, 0f, 1f );
	}
}
