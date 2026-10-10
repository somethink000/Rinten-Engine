namespace Editor;

/// <summary>The bar across the top of a scene view.</summary>
[StyleSheet( "ViewportToolsPanel.cs.scss" )]
public partial class ViewportToolsPanel : EditorPanel
{
	private SceneViewPanel sceneView;

	/// <summary>One thing on the bar: a button, a bit of live text, or a widget that has not been ported yet.</summary>
	public sealed class Item
	{
		/// <summary>The icon it wears, asked for each time in case it changed.</summary>
		public Func<string> Icon { get; set; }

		/// <summary>Live text instead of an icon - a frame time, a resolution.</summary>
		public Func<string> Text { get; set; }

		/// <summary>What the pointer says it does.</summary>
		public string ToolTip { get; set; }

		/// <summary>What pressing it does.</summary>
		public Action Action { get; set; }

		/// <summary>Whether whatever it turns on is on.</summary>
		public Func<bool> IsActive { get; set; }

		/// <summary>Whether it can be pressed at all.</summary>
		public bool Enabled { get; set; } = true;

		/// <summary>The icon's colour where it says something on its own - a stop button is red - and the plain one otherwise.</summary>
		public Color Color { get; set; } = Theme.TextLight;

		/// <summary>An extra class for the stylesheet, for the odd one out.</summary>
		public string Class { get; set; }

		/// <summary>A panel built in code standing here - what a tool put on the bar itself.</summary>
		public Panel Panel { get; set; }

		/// <summary>The gizmo visibility dropdown on the scene toolbar.</summary>
		public bool GizmoSettingsMenu { get; set; }

		/// <summary>The network settings popup on the scene toolbar.</summary>
		public bool NetworkSettingsMenu { get; set; }

		/// <summary>The build panel - baking the scene's lighting and the like - beside the play controls.</summary>
		public bool BuildMenu { get; set; }

		/// <summary>The view settings popup on the scene toolbar.</summary>
		public bool ViewSettingsMenu { get; set; }

		/// <summary>The forced resolution picker on the game view's toolbar.</summary>
		public bool ResolutionMenu { get; set; }

		/// <summary>A snap step field on the scene toolbar.</summary>
		public SnapStepKind? SnapStep { get; set; }
	}

	/// <summary>A run of buttons drawn as one block, or the line drawn between two of them.</summary>
	public sealed class Group
	{
		public List<Item> Items { get; } = new();

		/// <summary>Whether it is on the bar at all right now.</summary>
		public bool Visible { get; set; } = true;

		/// <summary>A line, rather than a block of buttons.</summary>
		public bool Divider { get; set; }

		/// <summary>No block drawn behind it - for what a tool put on the bar itself, which brings its own look.</summary>
		public bool Plain { get; set; }
	}

	/// <summary>What applies to the scene, whatever you are doing to it.</summary>
	public List<Group> Left { get; } = new();

	/// <summary>Playing, pausing and ejecting.</summary>
	public List<Group> Center { get; } = new();

	/// <summary>This view's own settings.</summary>
	public List<Group> Right { get; } = new();

	private ViewportToolbar toolbar;

	internal SceneViewPanel SceneView => sceneView;

	public ViewportToolsPanel( SceneViewPanel sceneView )
	{
		this.sceneView = sceneView;

		AddClass( "viewport-tools" );

		toolbar = new ViewportToolbar { Tools = this };

		AddChild( toolbar );

		Rebuild();
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		ClearSections();
	}

	/// <summary>Says what is on the bar, from scratch. Called when the view changes into or out of the game view, which is a different bar.</summary>
	public void Rebuild()
	{
		ClearSections();

		//Held by name as well as by the bar, and the bar was just taken apart
		toolExtensionGroup = null;
		toolExtensionDivider = null;

		if ( sceneView.CurrentView == SceneViewMode.Game )
		{
			BuildToolbarGame( Left );
		}
		else
		{
			//The tool modes used to start this row. They are down the side of the view now, with what each of them can do beside
			//it - see ViewportToolBar - and what is left here is what applies to any scene, whatever you are doing to it.
			BuildToolbarScene( Left );
			BuildToolExtensionToolbar( Left );
		}

		BuildPlayToolbar( Center );
		BuildToolbarRight( Right );

		Refresh();
	}

	/// <summary>The bar has something new to say, without anything being added to it.</summary>
	public void Refresh() => toolbar?.StateHasChanged();

	//Takes everything off the bar, and gets rid of the panels that were standing in on it. Nothing else is holding those: a tool
	//makes a new toolbar each time it is picked. Left behind, each one is still a panel somewhere in the window being drawn.
	private void ClearSections()
	{
		foreach ( var section in new[] { Left, Center, Right } )
		{
			foreach ( var group in section )
			{
				foreach ( var item in group.Items )
				{
					item.Panel?.Delete();
				}
			}

			section.Clear();
		}
	}

	[Shortcut( "editor.toggle-fullscreen", "F3", ShortcutType.Window )]
	internal static void ToggleFullscreen()
	{
		//The dock, because a window is made fullscreen by taking a widget out of the layout and the scene view inside it is markup now.
		EditorWindow.SetFullscreen( SceneDock.Active );
	}

	[EditorEvent.Frame]
	public void OnViewportToolsFrame()
	{
		UpdateToolExtensionToolbar();
	}

	//A new block of buttons at the end of a run.
	private Group AddGroup( List<Group> section, bool plain = false )
	{
		var group = new Group { Plain = plain };

		section.Add( group );

		return group;
	}

	//A line between what came before and what comes after.
	private void AddDivider( List<Group> section )
	{
		section.Add( new Group { Divider = true } );
	}

	//A button that lights up while what it turns on is on.
	private Item AddToggleButton( Group group, string tooltip, Func<string> getIcon, Func<bool> getVal, Action<bool> setVal )
	{
		//A setting that cannot be read or written right now is a button that does nothing, rather than an exception every time the bar is drawn.
		var read = () => { try { return getVal(); } catch ( System.Exception ) { return false; } };
		var write = ( bool value ) => { try { setVal( value ); } catch ( System.Exception ) { } };

		var item = new Item
		{
			Icon = getIcon,
			ToolTip = tooltip,
			IsActive = read,
			Action = () => write( !read() )
		};

		group.Items.Add( item );

		return item;
	}

	//A button that just does something.
	private Item AddButton( Group group, string tooltip, string icon, Action onClick )
	{
		var item = new Item
		{
			Icon = () => icon,
			ToolTip = tooltip,
			Action = onClick
		};

		group.Items.Add( item );

		return item;
	}
}
