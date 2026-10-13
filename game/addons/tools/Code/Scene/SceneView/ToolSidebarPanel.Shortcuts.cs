namespace Editor;

partial class ToolSidebarPanel
{
	//Strip keys, declared on every scene view; each presses its button, so no button means no-op

	//Digits run down the mode column starting at 2: objects, faces, edges, vertices, shape, extras

	[Shortcut( "tools.object-selection", "2", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateObjectMode() => Press( "tools.object-selection" );

	[Shortcut( "tools.face-tool", "3", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateFaceMode() => Press( "tools.face-tool" );

	[Shortcut( "tools.edge-tool", "4", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateEdgeMode() => Press( "tools.edge-tool" );

	[Shortcut( "tools.vertex-tool", "5", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateVertexMode() => Press( "tools.vertex-tool" );

	[Shortcut( "tools.shape-tool", "6", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateShapeMode() => Press( "tools.shape-tool" );

	[Shortcut( "tools.vertex-paint-tool", "7", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateVertexPaintMode() => Press( "tools.vertex-paint-tool" );

	[Shortcut( "tools.displacement-tool", "8", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateDisplacementMode() => Press( "tools.displacement-tool" );

	//W E R T Y for move modes (Q is camera down) - see MeshMoveModes

	[Shortcut( "tools.position-tool", "w", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivatePositionMode() => Press( "tools.position-tool" );

	[Shortcut( "tools.rotate-tool", "e", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateRotateMode() => Press( "tools.rotate-tool" );

	[Shortcut( "tools.resize-tool", "r", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateResizeMode() => Press( "tools.resize-tool" );

	[Shortcut( "tools.scale-tool", "t", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivateScaleMode() => Press( "tools.scale-tool" );

	[Shortcut( "tools.pivot-tool", "y", typeof( SceneViewPanel ), ShortcutType.Window )]
	public void ActivatePivotMode() => Press( "tools.pivot-tool" );
}
