using Rinten.UI;

namespace Editor;

/// <summary>The tools down the side of the scene view: the mode buttons, the buttons belonging to whichever of them is on, the current tool's settings, and its footer.</summary>
[StyleSheet( "ToolSidebarPanel.cs.scss" )]
public sealed partial class ToolSidebarPanel : EditorPanel
{
	private readonly SceneViewPanel sceneView;

	private readonly Panel strips;
	private readonly Panel settings;
	private readonly Panel footer;

	//The material being painted with - see MeshEditor.MaterialPalettePanel.
	private readonly MeshEditor.MaterialPalettePanel palette;

	//How wide the tool settings panel is when there is one.
	private const float SidebarWidth = 240.0f;

	//The groups on the strips right now, in column order. What the keys look through.
	private readonly List<ToolGroup> groups = new();

	//The widgets borrowed from the current tool, given back when it changes.
	private readonly List<Widget> borrowed = new();

	private EditorTool activeTool;
	private int selectionHash;
	private bool showedMeshTools;
	private bool showedActions;
	private bool builtInMapping;

	public ToolSidebarPanel( SceneViewPanel sceneView )
	{
		this.sceneView = sceneView;

		AddClass( "tool-sidebar" );

		var middle = AddChild<Panel>( "middle" );

		//Buttons first, settings second: on the left edge that puts the button strips
		//against the side of the window, where they are reached without crossing the panel.
		strips = middle.AddChild<Panel>( "strips" );
		settings = middle.AddChild<Panel>( "settings" );

		//Along the bottom: the material palette, which is there whatever tool is in hand and
		//so is made once, and then the tool's own footer, which is built again with the tool.
		var bottom = AddChild<Panel>( "bottom" );

		palette = bottom.AddChild( new MeshEditor.MaterialPalettePanel() );

		footer = bottom.AddChild<Panel>( "footer" );

		showedMeshTools = MeshEditor.MeshToolsSwitch.Shown;
		showedActions = MeshEditor.MeshToolsSwitch.ActionsShown;
		builtInMapping = MappingWorkspaceTools.Active;

		Rebuild();
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		ReleaseBorrowed();
	}

	//The tool whose settings the sidebar shows; rebuilds when tool, selection or the mesh tools switch changes
	EditorTool CurrentTool => sceneView?.Tools?.CurrentSubTool ?? sceneView?.Tools?.CurrentTool;

	//What the sidebar is built from: the tool, its selection where the tool asks to be rebuilt on one, and whether
	//the mesh tools are shown - the same three things the Tick used to read and compare by hand every frame.
	protected override int BuildHash()
	{
		var tool = CurrentTool;
		var rebuilds = tool?.RebuildSidebarOnSelectionChange ?? false;
		var selection = !rebuilds ? 0 : tool.IsDragSelecting ? selectionHash : tool.Selection?.GetHashCode() ?? 0;

		//And the workspace: inside mapping the dock builds the panels that carry the tool's keys and this builds none, so a view that did
		//not build again on the way out kept pressing keys through the dock's panels, built for whatever was selected when mapping was left.
		return HashCode.Combine( tool, selection, MeshEditor.MeshToolsSwitch.Shown, MeshEditor.MeshToolsSwitch.ActionsShown, MappingWorkspaceTools.Active );
	}

	//A kept widget waits for its host, which comes when the window it goes into is laid out. Adopted on the turns it asks for, until none is left waiting.
	protected override bool TickWhenAsked => true;

	public override void Tick()
	{
		base.Tick();

		Adopt();

		if ( Kept.Count > 0 ) RequestTick();
	}

	protected override void OnBuildHashChanged()
	{
		var tool = CurrentTool;
		var hash = tool?.Selection?.GetHashCode() ?? 0;
		var meshTools = MeshEditor.MeshToolsSwitch.Shown;

		var toolChanged = activeTool != tool;
		var selectionChanged = hash != selectionHash && (tool?.RebuildSidebarOnSelectionChange ?? false);
		var actionsOn = MeshEditor.MeshToolsSwitch.ActionsShown;
		var switchChanged = showedMeshTools != meshTools || showedActions != actionsOn;
		var mappingChanged = builtInMapping != MappingWorkspaceTools.Active;

		if ( !toolChanged && !selectionChanged && !switchChanged && !mappingChanged ) return;

		activeTool = tool;
		selectionHash = hash;
		showedMeshTools = meshTools;
		showedActions = actionsOn;
		builtInMapping = MappingWorkspaceTools.Active;

		Rebuild();
	}

	/// <summary>Presses the button a key belongs to, whichever column it is in.</summary>
	public void Press( string shortcut )
	{
		foreach ( var group in groups )
		{
			if ( group.Find( shortcut ) is not { } command ) continue;

			if ( command.IsEnabled ) command.Execute();

			return;
		}

		//Nothing here wears that key: the next shortcut registered under the same identifier gets it, rather than this one swallowing it.
		EditorShortcuts.PassShortcut = true;
	}

	private void ReleaseBorrowed()
	{
		foreach ( var widget in borrowed )
		{
			if ( widget.IsValid() ) widget.Destroy();
		}

		borrowed.Clear();
	}

	private void Rebuild()
	{
		ReleaseBorrowed();
		Kept.Clear();
		groups.Clear();

		strips.DeleteChildren( true );
		settings.DeleteChildren( true );
		footer.DeleteChildren( true );

		//With the mesh tools off it is not there at all: a material is something to paint onto
		//a face, and with no way to select one there is nothing in this view to paint.
		palette.SetClass( "hidden", !MeshEditor.MeshToolsSwitch.Shown );

		var rootTool = sceneView?.Tools?.CurrentTool;
		var subTool = sceneView?.Tools?.CurrentSubTool;

		BuildStrips( rootTool, subTool );
		BuildFooter( rootTool, subTool );
		BuildSettings( rootTool, subTool );
	}

	//Builds the mode strip and the active mode's option strip; none when the mesh tools are off
	private void BuildStrips( EditorTool rootTool, EditorTool subTool )
	{
		if ( !MeshEditor.MeshToolsSwitch.Shown ) return;

		var modes = rootTool?.CreateModeGroup();

		if ( modes is null ) return;

		AddStrip( modes );

		var options = subTool?.CreateOptionGroup() ?? rootTool?.CreateOptionGroup();

		if ( options is not null ) AddStrip( options );

		if ( subTool?.CreateActionGroup() is { } actions ) AddStrip( WithChevron( actions ) );
	}

	//The list of what a mode can run, with a chevron to put it away at the head of it - or, put away, only the chevron that brings it back.
	private static ToolGroup WithChevron( ToolGroup actions )
	{
		var shown = MeshEditor.MeshToolsSwitch.ActionsShown;

		if ( !shown )
		{
			var collapsed = new ToolGroup( actions.Name, ToolButtonSize.Option );

			collapsed.Add( new ToolCommand( "chevron_right", "Show Tool Actions", () => MeshEditor.MeshToolsSwitch.ActionsShown = true ) );

			return collapsed;
		}

		var group = new ToolGroup( actions.Name, ToolButtonSize.Action );

		group.Add( new ToolCommand( "chevron_left", "Hide Actions", () => MeshEditor.MeshToolsSwitch.ActionsShown = false )
		{
			Description = "The same commands are in the Tool Settings panel.",
		} );

		group.AddRange( actions.Commands );

		return group;
	}

	private void AddStrip( ToolGroup group )
	{
		groups.Add( group );
		strips.AddChild( new ToolStripPanel( group ) );
	}

	//Show tool settings on the viewport only outside mapping, for tools that aren't mesh tools
	private static bool ShowsSettings( EditorTool rootTool, EditorTool subTool )
		=> !MappingWorkspaceTools.Active
		&& rootTool is not MeshEditor.MeshTool
		&& subTool is not MeshEditor.PrimitiveTool;

	//Under the strips: the tool's own footer, and the panels that carry its keys. The material palette is beside this rather than in it: it
	//is on every scene view whichever tool is in hand, so it is made once with the panel and not again with every tool - see the constructor.
	private void BuildFooter( EditorTool rootTool, EditorTool subTool )
	{
		if ( ShowsSettings( rootTool, subTool ) )
		{
			if ( (subTool?.CreateToolFooterPanel() ?? rootTool?.CreateToolFooterPanel()) is { } footerPanel )
				footer.AddChild( footerPanel );
			else if ( (subTool?.CreateToolFooter() ?? rootTool?.CreateToolFooter()) is { } footerWidget )
				Borrow( footer, footerWidget );
		}

		//Then the row is padded out, so what comes after it is pushed off the end rather than in front of it.
		footer.AddChild<Panel>( "grow" );

		//A tool's keys live on these. Nothing to see - they are here for their lifetime, which is the tool's. Inside the
		//mapping workspace the dock builds them, and two of them would be two sets of the same keys answering to one press.
		if ( MappingWorkspaceTools.Active ) return;

		if ( rootTool?.CreateShortcutsPanel() is { } rootShortcuts ) footer.AddChild( rootShortcuts );
		if ( subTool?.CreateShortcutsPanel() is { } subShortcuts ) footer.AddChild( subShortcuts );
	}

	//Mesh tool settings are built but hidden, since most mesh shortcuts are declared on them
	private void BuildSettings( EditorTool rootTool, EditorTool subTool )
	{
		settings.SetClass( "hidden", true );

		//Inside mapping the dock builds the settings panel, and two of them would be two sets of the same keys answering to one press.
		if ( MappingWorkspaceTools.Active ) return;

		var toolPanel = subTool?.CreateToolSidebarPanel() ?? rootTool?.CreateToolSidebarPanel();
		var toolWidget = toolPanel is null ? subTool?.CreateToolSidebar() ?? rootTool?.CreateToolSidebar() : null;

		if ( toolPanel is null && !toolWidget.IsValid() ) return;

		var shown = ShowsSettings( rootTool, subTool );

		if ( !shown )
		{
			if ( toolPanel is not null )
			{
				footer.AddChild<Panel>( "kept hidden" ).AddChild( toolPanel );
			}
			else
			{
				Keep( toolWidget );
			}

			return;
		}

		settings.SetClass( "hidden", false );

		if ( toolPanel is not null )
		{
			settings.AddChild( toolPanel );
			return;
		}

		//The hole is 240 wide; a widget's own SizeHint is a thousand, and WidgetPanel sizes the hole from what the widget asks for.
		toolWidget.FixedWidth = SidebarWidth;

		Borrow( settings, toolWidget );
	}

	//Hangs a widget the tool made in a hole in this markup.
	private void Borrow( Panel into, Widget widget )
	{
		borrowed.Add( widget );

		into.AddChild( new WidgetPanel { Widget = widget } );
	}

	//Keeps a hidden settings widget parented under this panel so its shortcuts still resolve
	private void Keep( Widget widget )
	{
		widget.Visible = false;

		borrowed.Add( widget );
		Kept.Add( widget );

		//Adopted on the next turn, and the ones after until its host is there.
		RequestTick();

		Adopt();
	}

	//The kept widgets still waiting for a host.
	private List<Widget> kept = new();

	//Made again where a reload of this code left an instance without it.
	private List<Widget> Kept => kept ??= new();

	private void Adopt()
	{
		if ( Kept.Count == 0 ) return;
		if ( HostWidget is not { } host || !host.IsValid() ) return;

		foreach ( var widget in Kept )
		{
			if ( widget.IsValid() ) widget.Parent = host;
		}

		Kept.Clear();
	}
}
