using System.Reflection;

namespace Rinten;

public abstract partial class Component
{
	//Called in the editor to draw things like bounding boxes etc
	protected virtual void DrawGizmos() { }

	static readonly ReflectionCache<Type, bool> _typeOverridesDrawGizmos = new(
		t => t.GetMethod( nameof( DrawGizmos ), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null )?.DeclaringType != typeof( Component ) );

	internal bool OverridesDrawGizmos => _typeOverridesDrawGizmos[GetType()];

	//Whether its gizmo only lays targets for a click - then GizmoMayCatch says whether the pointer's ray can reach one, and the
	//walk passes an object of nothing else by where it cannot; a scene of fourteen thousand models spent most of its frame there
	internal virtual bool GizmoOnlyCatches => false;

	internal virtual bool GizmoMayCatch( Ray ray, float depth ) => true;

	internal void DrawGizmosInternal()
	{
		//Profiled per component type; editor calls this per viewport for many components
		using var _ = Rinten.Diagnostics.FrameProfiler.PushComponent( this );

		try { DrawGizmos(); }
		catch ( System.Exception e ) { Log.Error( e, $"Exception when calling 'DrawGizmos' on {this}" ); }
	}
}
