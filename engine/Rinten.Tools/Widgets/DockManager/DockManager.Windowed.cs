using System;

namespace Editor;

/// <summary>Panels opened into a window of their own rather than straight into a layout.</summary>
public partial class DockManager
{
	/// <summary>Whether this panel is open, docked in this layout or floating in a window of its own.</summary>
	public bool IsPanelOpen( string name ) => IsDockOpen( name );

	/// <summary>Open or put away a registered panel. Opening floats it in a window of its own unless it is already open, in which case it is raised where it is.</summary>
	public void SetPanelOpen( string name, bool open )
	{
		if ( open ) OpenPanelWindow( name );
		else ClosePanel( name );
	}

	/// <summary>Open a registered panel floating in a window of its own: a dock of this layout, so its title bar's dock button puts it back.</summary>
	public void OpenPanelWindow( string name )
	{
		//Already open, docked or floating: raised where it is rather than made twice
		if ( FindDockWidget( name ) is { } existing && IsDockOpen( name ) )
		{
			if ( floating.TryGetValue( existing, out var window ) && window.IsValid() ) window.Focus();
			else existing.SetAsCurrentTab();

			return;
		}

		SetDockState( name, true );

		if ( FindDockWidget( name ) is { } dock )
			FloatManaged( dock );
	}

	/// <summary>Open a registered panel docked in this layout, where it was registered to go; one floating in a window of its own comes back in.</summary>
	public void OpenPanelDocked( string name )
	{
		if ( FindDockWidget( name ) is { } existing ) DockFloating( existing );

		SetDockState( name, true );
	}

	/// <summary>Put a panel away, wherever it is, if the panel agrees - see DockWidget.CanClose.</summary>
	public void ClosePanel( string name )
	{
		if ( FindDockWidget( name ) is not { } dock || dock.IsClosed ) return;
		if ( !dock.MayPutAway() ) return;

		dock.PutAway();
	}
}
