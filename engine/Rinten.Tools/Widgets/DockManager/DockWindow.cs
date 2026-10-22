using Rinten.Engine;

namespace Editor;

/// <summary>A window that is built from docking windows.</summary>
public partial class DockWindow : Window
{
	/// <summary>The dock manager for this window, created automatically.</summary>
	public DockManager DockManager { get; init; }

	public DockWindow()
	{
		DockManager = new DockManager( this );
		Canvas = DockManager;
	}

	string DockCookie => $"Window.{StateCookie}.Dock";


	//Open and arrange this window's default docks. Override this to define your default layout.
	protected virtual void BuildDefaultLayout()
	{
	}

	/// <summary>Restores window geometry and, on top of the base window, the saved dock layout.</summary>
	public override void RestoreFromStateCookie()
	{
		base.RestoreFromStateCookie();

		if ( string.IsNullOrWhiteSpace( StateCookie ) )
			return;

		RestoreLayout();
	}

	/// <summary>Restore the saved dock layout, or the default layout if there's nothing to restore.</summary>
	public void RestoreLayout()
	{
		var saved = string.IsNullOrWhiteSpace( StateCookie ) ? null : ProjectCookie.GetString( DockCookie, null );
		if ( !string.IsNullOrWhiteSpace( saved ) && DockManager.RestoreState( saved ) )
			return;

		ResetLayout();
	}

	/// <summary>Reset the window back to its default dock layout.</summary>
	public void ResetLayout()
	{
		DockManager.ForgetMissing();

		foreach ( var dock in DockManager.DockTypes )
			DockManager.SetDockState( dock.Title, false );

		BuildDefaultLayout();
	}

	/// <summary>Persist the current dock layout. Happens automatically when the window closes or the app exits.</summary>
	public void SaveLayout()
	{
		if ( string.IsNullOrWhiteSpace( StateCookie ) || !DockManager.IsValid() )
			return;

		//Docks missing after a failed compile: keep the saved layout instead of writing the partial one
		if ( DockManager.IsMissingDocks && LastCompileFailed )
		{
			if ( !saidMissing )
			{
				saidMissing = true;
				Log.Warning( $"{StateCookie}: layout not saved - it is missing {string.Join( ", ", DockManager.MissingDocks )}, whose project did not compile. The saved one is kept." );
			}

			return;
		}

		ProjectCookie.SetString( DockCookie, DockManager.State );
	}

	//Whether the refusal above has been said already, which is once a window.
	private bool saidMissing;

	//Whether the most recent compile of the code finished with errors.
	private static bool LastCompileFailed
		=> Project.CompileGroup?.BuildResult is { Diagnostics: not null } result && !result.Success;

	protected override void OnClosed()
	{
		SaveLayout();
		base.OnClosed();
	}

	[Event( "app.exit" )]
	void SaveLayoutOnExit() => SaveLayout();

	/// <summary>Populate a view menu with dock toggle options and a reset layout action.</summary>
	public void CreateDynamicViewMenu( Menu menu )
	{
		menu.Clear();

		IToolsDll.Current?.RunEvent( "tools.editorwindow.createview", menu );

		//The main window's menu targets the workspace in front, not always its own layout
		var target = ( this == EditorWindow ? DocksInFront : null ) ?? DockManager;

		//The panels registered against this window can be opened in any layout; the manager in front is told about them before it is asked what it has.
		target.AdoptDockTypes( DockManager );

		//A layout that is not a window of its own has no default to go back to.
		if ( target.Window.IsValid() )
		{
			menu.AddOption( "Reset Layout", "restart_alt", target.Window.ResetLayout );
			menu.AddSeparator();
		}

		foreach ( var dock in target.DockTypes.OrderBy( x => x.Title ) )
		{
			var title = dock.Title;

			//Floated in a window of its own rather than put into the layout - see DockManager.OpenPanelWindow. A
			//panel already docked here is raised where it is instead, because that is where somebody put it.
			//Right-clicked, it goes straight into the layout instead
			var o = menu.AddOption( title, dock.Icon );
			o.Checkable = true;
			o.Checked = target.IsPanelOpen( title );
			o.Toggled += ( b ) => target.SetPanelOpen( title, b );
			o.RightClicked = () => target.OpenPanelDocked( title );
		}

		IToolsDll.Current?.RunEvent( "tools.editorwindow.postcreateview", menu );
	}

	/// <summary>The dock manager of the workspace tab on screen; null if none. Use this for View menu actions</summary>
	public static DockManager DocksInFront
	{
		get
		{
			var docks = WorkspaceArea.Current?.CurrentDocks;

			return docks.IsValid() ? docks : null;
		}
	}

	/// <summary>The window whose docks the user is looking at, where that layout belongs to a window at all.</summary>
	public static DockWindow InFront => DocksInFront?.Window;
}
