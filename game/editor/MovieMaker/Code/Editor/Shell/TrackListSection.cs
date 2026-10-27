using Rinten.MovieMaker;
using Rinten.UI;
using System.Linq;

namespace Editor.MovieMaker;

#nullable enable

/// <summary>The list of tracks, and the file and player tools above it.</summary>
[StyleSheet( "MovieSection.cs.scss" )]
public sealed class TrackListSection : MovieSection
{
	public TrackListPanel TrackList { get; }

	public TrackListSection( MovieEditor editor, Session session )
		: base( editor, session )
	{
		AddClass( "tracklist-section" );

		TrackList = new TrackListPanel( session ) { Parent = Body };

		BuildFileGroup( session );
		BuildPlayerGroup( session );
	}

	private void BuildFileGroup( Session session )
	{
		var group = ToolBar.AddGroup( permanent: true );

		group.AddAction(
			new ToolBarItemDisplay( "File", "folder",
				"Actions for saving / loading / importing movies, or switching player components." ),
			() => OpenFileMenu( group, session ) );
	}

	private void OpenFileMenu( MovieToolBarGroup group, Session session )
	{
		var menu = new Menu();

		menu.AddHeading( "File" );

		menu.AddOption( "New Movie", "note_add", Editor.SwitchToNewEmbedded );

		menu.AddSeparator();

		var openMenu = menu.AddMenu( "Open Movie", "file_open" );

		openMenu.AddOptions( ResourceLibrary.GetAll<MovieResource>().ToArray(),
			x => $"{x.ResourcePath}:video_file", Editor.SwitchResource );

		session.CreateImportMenu( menu );

		menu.AddSeparator();

		menu.AddOption( "Save Movie", "save", Editor.Save, shortcut: "CTRL+S" );

		var saveAsMenu = menu.AddMenu( "Save Movie As..", "save_as" );

		var embed = saveAsMenu.AddOption( "Embedded", "attach_file", Editor.SwitchToEmbedded );

		embed.Checkable = true;
		embed.Checked = session.Resource is EmbeddedMovieResource;
		embed.ToolTip = "Store the movie inside this Movie Player component, embedded in the current scene or prefab.";

		saveAsMenu.AddOption( "New Movie Resource", "video_file", Editor.SaveFileAs );

		menu.AddOption( "Export Video..", "video_file", () => VideoExportWindow.Show( session, session.LoopTimeRange ) );

		menu.AddHeading( "Help" );

		menu.AddOption( "Documentation", "help", () => EditorUtility.OpenFile( MovieEditor.HelpUrl ) )
			.ToolTip = "Open the MovieMaker documentation.";

		menu.OpenAt( group.GetScreenStyleRect().BottomLeft, group );
	}

	private void BuildPlayerGroup( Session session )
	{
		var group = ToolBar.AddGroup( permanent: true );

		group.AddPanel( new PlayerDropDown( session ) );
	}
}

/// <summary>Which movie player in the scene is being edited.</summary>
public sealed class PlayerDropDown : IconDropDown<MoviePlayer?>
{
	private readonly Session _session;

	public PlayerDropDown( Session session )
		: base( null, () => session.Player, value => session.Editor.Switch( value! ) )
	{
		_session = session;

		AddClass( "player-dropdown" );
	}

	protected override IEnumerable<MoviePlayer?> OnGetOptions() =>
		_session.Player.Scene.IsValid ? _session.Player.Scene.GetAllComponents<MoviePlayer>() : [];

	protected override string OnGetOptionTitle( MoviePlayer? option ) => option?.GameObject?.Name ?? "None";

	protected override void OnBuildPreview( Panel host, MoviePlayer? value )
	{
		host.AddChild<Rinten.UI.Label>( "text" ).Text = OnGetOptionTitle( value );
	}

	protected override void OnBuildPopup( MovieDropDownPopup popup )
	{
		base.OnBuildPopup( popup );

		popup.AddSeparator();
		popup.AddOption( "Create New Movie Player", "live_tv", _session.Editor.CreateNewPlayer );
	}
}
