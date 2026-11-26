using Rinten;

namespace Editor;

/// <summary>One docked panel: a title, a widget inside it, and whether it is open.</summary>
public partial class DockWidget
{
	private DockManager manager;
	private Widget content;
	private Widget header;
	private string title = string.Empty;
	private string icon;

	private bool closed;

	//Whether this dock ever shows a tab of its own.
	internal bool NoTab { get; set; }

	//Whether this dock has been torn off into a window of its own. It stays on its edge's list while it is out there - that list is
	//where it goes back to, and which edge it came from is the answer nobody wants to be asked for again when the window is closed.
	internal bool Floating { get; set; }

	/// <summary>The material icon the tab wears beside its name, if any.</summary>
	public string Icon
	{
		get => icon;
		set
		{
			icon = value;
			manager?.Update();
		}
	}

	/// <summary>What the tab says.</summary>
	public string Title
	{
		get => OnPanels ? title : WindowTitle;
		set
		{
			if ( OnPanels )
			{
				title = value ?? string.Empty;

				manager?.Update();
				return;
			}

			WindowTitle = value;
		}
	}

	//One of ours: a frame with something inside it.
	internal DockWidget( DockManager manager, string name, string icon, Widget widget ) : base()
	{
		MakeSurface( manager );

		this.manager = manager;
		this.icon = icon;

		title = name ?? string.Empty;

		WidgetManaged = widget;
	}

	static string DockClass( Widget widget ) => "dock-" + widget.GetType().Name.ToLowerInvariant();

	//The widget this dock is showing.

	/// <summary>What stands over the content, whatever the content is set to - the document tabs over an editor's central view.</summary>
	internal Widget Header
	{
		get => header;
		set
		{
			if ( header == value ) return;

			if ( header.IsValid() )
				header.SetPanelParent( null );

			header = value;
			header?.SetPanelParent( this );

			MarkNeedsArrange();
		}
	}
	internal Widget WidgetManaged
	{
		get => content;
		set
		{
			if ( content == value ) return;

			var previous = content;
			content = value;

			if ( previous.IsValid() )
				previous.SetPanelParent( null );

			content?.SetPanelParent( this );

			//Says what it holds, so a sheet can dress one kind of dock without knowing the others: dock-inspector, dock-console.
			if ( previous is not null )
				Surface?.SetClass( DockClass( previous ), false );

			if ( content is not null )
				Surface?.SetClass( DockClass( content ), true );

			MarkNeedsArrange();
		}
	}

	private protected override void OnArrange()
	{
		base.OnArrange();

		if ( !OnPanels ) return;

		var top = 0.0f;

		if ( header.IsValid() )
		{
			top = header.MinimumHeight;
			header.Position = 0;
			header.Size = new Vector2( Size.x, top );
		}

		if ( content is null ) return;

		content.Position = new Vector2( 0, top );
		content.Size = new Vector2( Size.x, Size.y - top );
	}

	protected override void OnPaint()
	{
		if ( !OnPanels ) { base.OnPaint(); return; }

		//Nothing: a dock is glass, and the stylesheet says so (.dockwidget).
	}

	//Shows or hides the dock, as the view menu does.
	internal void ToggleViewManaged( bool open )
	{
		closed = !open;

		Visible = open;

		manager?.MarkNeedsArrange();
		manager?.Update();
	}

	internal bool IsClosedManaged => closed;

	//Brings this dock to the front of the edge it shares.
	internal void SetAsCurrentTabManaged()
	{
		var side = manager?.SideOf( this );

		if ( side is null ) return;

		var open = System.Linq.Enumerable.ToList( side.Open );
		var at = open.IndexOf( this );

		if ( at < 0 ) return;

		side.Current = at;

		manager.MarkNeedsArrange();
		manager.Update();
	}
}
