using Native;
using System;

namespace Editor;

/// <summary>Container for docked widgets</summary>
public partial class DockWidget : Frame
{

	public bool IsClosed => IsClosedManaged;

	/// <summary>The Widget this DockWidget is wrapping.</summary>
	public Widget Widget
	{
		get => WidgetManaged;
		set
		{
			WidgetManaged = value;
		}
	}

	/// <summary>Text tooltip for the title bar widget.</summary>
	public string TabTooltip
	{
		set
		{
			ToolTip = value;
		}
	}

	/// <summary>Prevents the user from closing, moving, floating or pinning this dock.</summary>
	public bool Locked
	{
		set
		{
			//Nothing of ours can be moved or floated yet, so a locked dock is no different from any other one.
			return;
		}
	}

	/// <summary>When true, the dock's minimum size follows its content widget's minimum size. By default docks can shrink to a tiny fixed size regardless of content.</summary>
	public bool MinimumSizeFromContent
	{
		set
		{
			return;
		}
	}

	/// <summary>Raise this dock to the front of any tab group it belongs to.</summary>
	public void SetAsCurrentTab()
	{
		SetAsCurrentTabManaged();
	}

	/// <summary>Whether this dock is floating in its own top-level window.</summary>
	public bool IsFloating => false;

	/// <summary>Detach this dock into its own floating top-level window.</summary>
	public void Float()
	{
		return;
	}

	/// <summary>Toggle the dock open or closed.</summary>
	public void ToggleView( bool open )
	{
		ToggleViewManaged( open );
	}

	internal void CloseDockWidget()
	{
		ToggleViewManaged( false );
	}

	/// <summary>Asked before the person puts this dock away - the cross on its tab, the close of the window it was opened into, or its tick in the View menu. Answer false to keep it: a panel with unsaved work asks about that first, and puts itself away with <see cref="PutAway"/> once it has an answer.</summary>
	public Func<bool> CanClose { get; set; }

	//Whether the person may put this dock away now.
	internal bool MayPutAway() => CanClose?.Invoke() ?? true;

	/// <summary>Puts the dock away the way the person would, without asking <see cref="CanClose"/>: floating, its window goes; docked, its tab is hidden.</summary>
	public void PutAway()
	{
		if ( manager?.CloseFloating( this ) == true ) return;

		ToggleViewManaged( false );
	}
}
