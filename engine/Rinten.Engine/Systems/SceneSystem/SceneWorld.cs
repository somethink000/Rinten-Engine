using NativeEngine;
using Rinten.Rendering;

namespace Rinten;

/// <summary>A scene world that contains <see cref="SceneObject"/>s. See <a href="()">Utility.CreateSceneWorld</a>. You may also want a <see cref="SceneCamera"/> to manually render the scene world.</summary>
public sealed partial class SceneWorld : IHandle
{
	[SkipHotload]
	internal static HashSet<SceneWorld> All = new HashSet<SceneWorld>();

	internal HashSet<SceneObject> InternalSceneObjects { get; set; } = new();

	//Self-drawing objects kept separately so the drawer can find them without scanning the whole world
	internal HashSet<SceneObject> InternalHandDrawn { get; set; } = new();

	//And the decals, which are not drawn at all: the surfaces they land on read them - see CoreDecals, which copies these out once a frame.
	internal HashSet<DecalSceneObject> InternalDecals { get; set; } = new();

	//And the lights put straight into the world rather than by a Light component - a particle's glow, a tracer's flash, the lamps
	//an editor preview stands its model under. The frame reads these beside the components; see CoreSceneRenderer.BuildFrame.
	internal HashSet<SceneLight> InternalLights { get; set; } = new();

	//Counts up whenever an object joins or leaves this world, or any object in it is touched - see
	//SceneObject.Version. A frame that finds the number where it left it knows the whole walk would come out the same.
	internal int Version { get; private set; }

	internal void Touch() => Version++;

	//Bumped only when the set of drawn objects changes, unlike Version which changes every frame
	internal int Membership { get; private set; }

	internal void TouchMembership() => Membership++;

	//InternalSceneObjects as an array, made again only when Membership moves; shared, so read it and never write it
	private SceneObject[] objectsNow;
	private int objectsAt = -1;

	internal SceneObject[] ObjectsNow()
	{
		lock ( InternalSceneObjects )
		{
			if ( objectsNow is null || objectsAt != Membership )
			{
				objectsNow = InternalSceneObjects.ToArray();
				objectsAt = Membership;
			}

			return objectsNow;
		}
	}

	/// <summary>List of scene objects belonging to this scene world.</summary>
	public IReadOnlyCollection<SceneObject> SceneObjects
	{
		get
		{
			lock ( InternalSceneObjects )
			{
				return InternalSceneObjects
								.Where( x => x.IsValid() )
								.ToArray(); //Ha Ha I bet no-one ever notices this
			}
		}
	}

	/// <summary>Controls gradient fog settings.</summary>
	public GradientFogSetup GradientFog;

	//The volumes of fog standing in this world - see SceneFogVolume, which puts itself here when it is
	//made and takes itself out when it is deleted. The renderer reads the nearest few into every frame.
	internal readonly List<SceneFogVolume> FogVolumes = new();

	/// <summary>Sets the ambient lighting color</summary>
	public Color AmbientLightColor = Color.Transparent;

	/// <summary>Sets the clear color, if nothing else is drawn, this is the color you will see</summary>
	[System.Obsolete( "Use SceneCamera.BackgroundColor" )]
	public Color ClearColor = Color.Black;

	#region IHandle
	//A pointer to the actual native object


	//IHandle implementation
	void IHandle.HandleInit( IntPtr ptr )
	{
		//A world is made here rather than handed to us.
	}

	void IHandle.HandleDestroy() => OnNativeDestroy();

	//Alive means "not deleted", not "has a pointer". A world has no native half any more, and everything
	//that asks - every scene object's constructor, for one - is asking whether it is still a world.
	bool IHandle.HandleValid() => All.Contains( this );
	#endregion

	//If a world is transient, it means it was created by game code, and should be deleted at the end of the game session. If
	//they're non transient then they were created in the menu, or by the engine code and will be released properly by that code.
	internal bool IsTransient { get; set; }

	internal SceneWorld( HandleCreationData _ ) { }

	//The scene this world belongs to, or null for a standalone world
	internal Scene Scene { get; init; }

	public SceneWorld()
	{
		IsTransient = true;

		All.Add( this );
	}

	/// <summary>Delete this scene world. You shouldn't access it anymore.</summary>
	public void Delete()
	{
		if ( !IsTransient ) return;

		//Whatever is still in it goes with it: an object's world is what says it is alive, and this one is not any more.
		lock ( InternalSceneObjects )
		{
			foreach ( var found in InternalSceneObjects.ToArray() ) found.Delete();

			InternalSceneObjects.Clear();
			Touch();
			TouchMembership();
		}
	}

	internal void OnNativeDestroy()
	{
		//No-op: nothing to do here.
	}

	/// <summary>Kept for compatibility; nothing is pending since objects leave the world when deleted</summary>
	public void DeletePendingObjects()
	{
	}

	//Flush all system-wide queued scene object and world deletes. Normally processed at end-of-frame
	//by FinishRenderingViews, but during shutdown no frame runs so we must flush manually.
	internal static void FlushQueuedDeletes()
	{
	}

	//No-op: there are no compiled maps to stream, levels are GameObject prefabs
	internal void UpdateObjectsForRendering( Vector3 eyePos = default, float farPlane = 127.0f )
	{
	}
}
