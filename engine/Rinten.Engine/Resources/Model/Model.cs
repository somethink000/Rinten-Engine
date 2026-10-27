using NativeEngine;
using Rinten.Engine;
using Rinten.Engine.Utility.RayTrace;

namespace Rinten;

/// <summary>A model.</summary>
public sealed partial class Model : Resource
{

	internal bool procedural;

	//What CoreResources knows this model's device mesh by, without holding the model - see CoreResources.Gone
	internal readonly long MeshId = System.Threading.Interlocked.Increment( ref meshIds );
	private static long meshIds;

	//Set when the model came from our own format; checked first, the legacy path is the fallback
	internal CoreModel core;

	//Meshes with non-engine vertex layouts, kept raw for their own shader; null on most models
	internal CoreMesh[] pulled;

	public override bool IsValid => core.IsValid;

	//A model loaded by our own core, from our own compiled format.
	private Model( CoreModel core )
	{
		this.core = core;
		this.Name = core.Name;

		//Where it came from, as everything downstream expects to find it. Our own renderer
		//looks the compiled file up by this, so a model without one is a model nothing can draw.
		this.ResourcePath = core.Name;

		RegisterWeakResourceId( Name );
	}

	//Wrap a model our core loaded, or null if it did not load one.
	internal static Model FromCore( CoreModel core )
		=> core is { IsValid: true } ? new Model( core ) : null;

	//Main thread: this is where a model enters the resource library. The bytes themselves can be built anywhere, which is the point of the pair.
	/// <summary>A model out of the bytes a <see cref="ModelBuilder.ToBytes"/> wrote, under the name given. Null where the bytes are not a model.</summary>
	public static Model FromBytes( byte[] bytes, string name )
		=> bytes is { Length: > 0 } ? FromCore( CoreModel.FromMemory( bytes, name ) ) : null;

	void ReplaceCore( CoreModel next )
	{
		core?.Dispose();
		core = next;
		ForgetGeometry();

		//Everything else read off the old core with it: a model recompiled with a new
		//attachment, material, bone or morph kept the ones it was first loaded with.
		ReloadAttachments();
		_materials = default;
		_bones = null;
		_morphs = null;
		_animationNames = null;

		Name = next.Name;
		ResourcePath = next.Name;
	}

	internal override void Destroy()
	{
		//Its device mesh goes with it, on the main thread, from whichever thread let it go - the finalizer's for a model built at runtime
		var mesh = MeshId;
		MainThread.Queue( () => CoreResources.Gone( mesh ) );

		core?.Dispose();
		core = null;

		base.Destroy();
	}

	//Called when the resource is reloaded. We should clear any cached values.
	internal override void OnReloaded()
	{
		_physics?.Dispose();
		_physics = default;

		_morphs?.Dispose();
		_morphs = null;

		_attachments?.Dispose();
		_attachments = default;

		_animationNames?.Clear();
		_animationNames = default;

		_materials = default;

		_bones = default;
		CoreSkeletons.Forget( Name ?? ResourcePath );
		CoreResources.Forget( this );
		_hitboxset = default;

		_parts?.Dispose();
		_parts = default;

		BaseModel = default;

		_meshInfo = null;

		IToolsDll.Current?.RunEvent( "model.reload", this );

		foreach ( var scene in Scene.Loaded )
		{
			using var scope = scene.Push();

			var components = scene.GetAllComponents<IHasModel>();
			foreach ( var c in components )
			{
				if ( c.Model != this ) continue;

				c.OnModelReloaded();
			}
		}
	}

	//A model of ours has no native handle at all, so the questions below are not questions it can be asked - being ours is the answer.
	/// <summary>Whether this model is an error model or invalid or not.</summary>
	public override bool IsError => !core.IsValid || isMissing;

	//True for the error model standing in for a missing name; IsError only says whether core loaded
	private bool isMissing;

	//Marks as a stand-in; resource path becomes the wanted name so saves keep the reference
	internal void MarkMissing( string wanted )
	{
		isMissing = true;
		RegisterWeakResourceId( wanted );
	}

	/// <summary>Name of the model, usually being its file path.</summary>
	public string Name { get; internal set; }

	/// <summary>Which of the project's detail groups this model keeps its detail by with distance, as its definition said - empty for none. A renderer or a scatter that names a group of its own wears that one instead. See <see cref="Rinten.LodGroup"/>.</summary>
	public string LodGroup => core?.LodGroup ?? "";

	/// <summary>Whether this model is procedural, i.e. it was created at runtime via <see cref="ModelBuilder.Create"/>.</summary>
	public bool IsProcedural => procedural;

	/// <summary>Total number of meshes this model is made out of.</summary>
	public int MeshCount => core.MeshCount;

	/// <summary>Trace against the triangles in this mesh</summary>
	public MeshTraceRequest Trace => new() { targetModel = this };

	//Base model of this model if one was used.
	internal Model BaseModel
	{
		//Only one of theirs was built on top of another - a model of ours, and a model that did not load at all, has nothing under it.
		get => field;
		set;
	}
}

internal interface IHasModel
{
	//The Model associated with this object.
	Model Model { get; }

	//Called by the engine when the associated Model is reloaded. Implementing classes should clear or update any cached data that depends on the model.
	void OnModelReloaded();
}
