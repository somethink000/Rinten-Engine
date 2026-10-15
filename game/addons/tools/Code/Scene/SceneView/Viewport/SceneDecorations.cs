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

	//A selected object's model renderers, gathered here each frame - see DrawSelection.
	readonly List<ModelRenderer> selectedRenderers = new();

	public void DrawSelection( SceneEditorSession session )
	{
		if ( session is null ) return;
		if ( !Gizmo.Settings.Selection ) return;

		//Walked rather than OfType and SelectMany: three iterators, a closure and a list for every selected object, every frame.
		foreach ( var selected in session.Selection )
		{
			if ( selected is not GameObject go ) continue;

			selectedRenderers.Clear();
			go.Components.GetAll( FindMode.InSelf | FindMode.Enabled | FindMode.InDescendants, selectedRenderers );

			foreach ( var modelRenderer in selectedRenderers )
			{
				if ( modelRenderer.Model == null )
					continue;

				using ( Gizmo.ObjectScope( modelRenderer.GameObject, default ) )
				{
					Gizmo.Transform = modelRenderer.GameObject.WorldTransform;
					Gizmo.Draw.Color = Gizmo.Colors.Selected;
					Gizmo.Draw.LineBBox( modelRenderer.Model.Bounds );
				}
			}
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
