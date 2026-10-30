namespace Rinten;

public partial class Scene : GameObject
{
	void DebugDraw()
	{
		NavMesh.DebugDraw();
		BakedProbeView.Draw( this );
	}
}
