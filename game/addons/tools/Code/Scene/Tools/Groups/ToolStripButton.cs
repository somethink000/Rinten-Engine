using Rinten.UI;

namespace Editor;

/// <summary>Tool strip button: icon and lit state; picture icons are tinted white</summary>
public sealed class ToolStripButton : Panel
{
	private readonly IToolCommand command;
	private readonly IconPanel icon;
	private readonly IconPanel badge;

	private string shownIcon;

	public ToolStripButton( IToolCommand command, bool labelled = false )
	{
		this.command = command;

		AddClass( "tool-strip-button" );

		icon = AddChild<IconPanel>( "icon" );

		//Said in words beside the icon, where there are too many buttons to tell apart by their pictures.
		if ( labelled )
		{
			AddClass( "labelled" );
			AddChild<Rinten.UI.Label>( "text" ).Text = command.Title;
		}

		if ( !string.IsNullOrEmpty( command.Badge ) )
		{
			badge = AddChild<IconPanel>( "badge" );
			badge.Text = command.Badge;
		}

		Tooltip = command.ToolTip;

		Sync();
	}

	/// <summary>The command this button presses.</summary>
	public IToolCommand Command => command;

	//What the button shows is the command's icon and two of its states, and those are read here - not written. The tick asks this once a frame
	//and takes the panel's turn only when it moves; a strip of these used to take a full turn each, every frame, to find the same three answers.
	protected override int BuildHash() => HashCode.Combine( command.Icon, command.IsActive, command.IsEnabled );

	protected override void OnBuildHashChanged() => Sync();

	//Says what the command says, writing only what changed.
	private void Sync()
	{
		var name = command.Icon;

		if ( name != shownIcon )
		{
			shownIcon = name;
			ShowIcon( name );
		}

		SetClass( "active", command.IsActive );
		SetClass( "disabled", !command.IsEnabled );
	}

	//A letter of the icon font
	private void ShowIcon( string name )
	{
		icon.Text = name ?? string.Empty;
	}

	protected override void OnClick( MousePanelEvent e )
	{
		base.OnClick( e );

		if ( command.IsEnabled ) command.Execute();

		e.StopPropagation();
	}
}
