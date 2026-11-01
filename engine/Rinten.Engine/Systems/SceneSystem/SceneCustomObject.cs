namespace Rinten;

/// <summary>A scene object that allows custom rendering within a scene world.</summary>
public class SceneCustomObject : SceneObject
{
	internal SceneCustomObject( HandleCreationData _ ) { }

	public SceneCustomObject( SceneWorld sceneWorld )
	{
		Assert.IsValid( sceneWorld );

		//Infinite until something says otherwise, so an object whose bounds nobody set is drawn rather than culled.
		Bounds = new BBox( new Vector3( float.MinValue * 0.5f ), new Vector3( float.MaxValue * 0.5f ) );

		JoinWorld( sceneWorld );
	}

	//Drawn at a pixel of the screen rather than somewhere in the world - screen text, a picture in a corner. A headset's eyes skip
	//these: there is no screen there, and a picture at the same place in both eyes reads as infinitely far away at the edge of the lens.
	internal bool ScreenAnchored { get; set; }

	/// <summary>Draw order among self-drawing objects, lower first; order not depth, so later draws on top</summary>
	public int DrawOrder { get; set; }

	//Breaks a tie of DrawOrder, lower first; without it a tie falls to the world's set order
	internal int DrawTie { get; set; }

	internal void RenderInternal()
	{
		if ( !this.IsValid() )
			return;

		try
		{
			RenderSceneObject();
		}
		catch ( System.Exception e )
		{
			Log.Error( e );
		}
	}

	/// <summary>Called by default version of <see cref="RenderSceneObject"/>.</summary>
	public Action<SceneObject> RenderOverride;

	/// <summary>Called when this scene object needs to be rendered. Invokes <see cref="RenderOverride"/> by default. See the <see cref="Graphics" /> library for a starting point.</summary>
	public virtual void RenderSceneObject()
	{
		RenderOverride?.Invoke( this );
	}
}
