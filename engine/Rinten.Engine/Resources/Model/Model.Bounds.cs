
namespace Rinten;

public partial class Model
{
	//Read from the core once: cloning reads it for every object it copies
	private BBox? _physicsBounds;

	/// <summary>Total bounds of all the meshes.</summary>
	public BBox Bounds => core.RenderBounds;

	/// <summary>Total bounds of all the physics shapes.</summary>
	public BBox PhysicsBounds => _physicsBounds ??= core.PhysicsBounds;

	/// <summary>Render view bounds.</summary>
	public BBox RenderBounds => core.RenderBounds;
}
