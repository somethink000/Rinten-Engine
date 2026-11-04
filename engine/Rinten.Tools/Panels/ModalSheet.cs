using Rinten.UI;

namespace Editor;

/// <summary>Modal panel over a window that blocks input below except the title bar; closed by its opener</summary>
public sealed class ModalSheet : Panel
{
	private ModalSheet()
	{
		//The look - blurred and dimmed behind, see design.scss - by class, so the sheet is restyled without a build.
		AddClass( "modal-sheet" );

		Style.Position = PositionMode.Absolute;
		Style.Left = 0;
		Style.Right = 0;
		Style.Bottom = 0;
		Style.AlignItems = Align.Center;
		Style.JustifyContent = Justify.Center;
		Style.PointerEvents = PointerEvents.All;
	}

	/// <summary>Holds content over the window until closed; fill stretches it over the whole window with no blur.</summary>
	public static ModalSheet Open( Window over, Panel content, bool fill = false )
	{
		if ( content is null ) return null;

		var layer = over?.Shell?.Root is { } root ? EditorOverlay.For( root ) : EditorOverlay.ForActiveWindow();

		if ( layer is null ) return null;

		var sheet = new ModalSheet();

		//Content covers the whole window, so the stylesheet leaves out the blur behind it
		if ( fill )
		{
			sheet.AddClass( "fills" );
			sheet.Style.AlignItems = Align.Stretch;
		}

		//Under the bar where the window draws its own.
		sheet.Style.Top = Length.Pixels( over?.MenuWidget is TitleBar ? TitleBar.BarHeight : 0 );

		layer.AddChild( sheet );
		sheet.AddChild( content );

		Window.PushModal( sheet );

		return sheet;
	}

	/// <summary>Whether a press beside the content, or Escape, takes the sheet away - the launcher opened over a project that is open, which there is nothing to lose by leaving. Off for one that has to be answered.</summary>
	public bool Dismissable
	{
		get => AcceptsFocus;
		set
		{
			AcceptsFocus = value;
			if ( value ) Focus();
		}
	}

	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		if ( !Dismissable || e.Target != this ) return;

		Close();
		e.StopPropagation();
	}

	public override void OnButtonTyped( ButtonEvent e )
	{
		base.OnButtonTyped( e );

		if ( Dismissable && e.Pressed && e.Button == "escape" ) Close();
	}

	/// <summary>Takes the sheet and what is on it away, and gives the window back.</summary>
	public void Close() => Delete();

	public override void OnDeleted()
	{
		base.OnDeleted();

		Window.PopModal( this );
	}
}
