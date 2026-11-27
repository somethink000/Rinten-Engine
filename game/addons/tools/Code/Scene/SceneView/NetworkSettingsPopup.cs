using Editor.Inspectors;
using Editor.Preferences;
using Rinten.Network;
using Rinten.UI;
using System.Diagnostics;

namespace Editor;

/// <summary>Panel popup for lobby and network simulation settings.</summary>
[StyleSheet( "ViewportToolPopup.scss" )]
public sealed class NetworkSettingsPopup : Popup
{
	public static NetworkSettingsPopup Open( Panel anchor )
	{
		if ( anchor is null ) return null;

		return new NetworkSettingsPopup( anchor );
	}

	readonly List<MenuRow> rows = new();

	NetworkSettingsPopup( Panel anchor )
	{
		SetPositioning( anchor, PositionMode.BelowRight, 4 );
		AnchorPanel = anchor;
		CloseWhenParentIsHidden = true;
		AddClass( "editor-popup" );
		AddClass( "network-settings-popup" );
		AddClass( "inspector" );

		var sheet = AddChild<Inspectors.ControlSheet>( "sheet" );
		var settings = new LobbySettings();
		var settingsSo = settings.GetSerialized();

		var properties = new List<SerializedProperty>
		{
			settingsSo.GetProperty( nameof( LobbySettings.LobbyPrivacy ) ),
			settingsSo.GetProperty( nameof( LobbySettings.SimulateLag ) ),
			settingsSo.GetProperty( nameof( LobbySettings.SimulatePacketLoss ) ),
		};

		sheet.Target = properties;
		sheet.Rebuild();

		var body = AddChild<Panel>( "body" );

		AddSeparator( body );
		rows.Add( new MenuRow( body, "Start Hosting", "dns", EditorUtility.Network.StartHosting, () => !EditorUtility.Network.Active ) );
		rows.Add( new MenuRow( body, "Disconnect", "phonelink_erase", EditorUtility.Network.Disconnect, () => EditorUtility.Network.Active ) );

		AddSeparator( body );
		rows.Add( new MenuRow( body, "Join via new instance", "connected_tv", SpawnProcess, () => EditorUtility.Network.Hosting ) );
		rows.Add( new MenuRow( body, "Start dedicated server", "terminal", SpawnDedicatedServer ) );

		AddSeparator( body );
		rows.Add( new MenuRow( body, "Preferences", "tune", OpenPreferences ) );

		SyncRows();
		SyncAnchor();
	}

	public override void Tick()
	{
		base.Tick();
		SyncRows();
	}

	void SyncRows()
	{
		foreach ( var row in rows )
			row.Sync();
	}

	static void AddSeparator( Panel body ) => body.AddChild<Panel>( "separator" ).AddClass( "separator" );

	static void OpenPreferences()
	{
		var window = EditorPreferencesWindow.OpenEditorPreferences();
		window.SwitchPage<NetworkingPage>();
	}

	static void SpawnDedicatedServer()
	{
		using var p = new Process();
		p.StartInfo.FileName = EngineExecutables.ServerFile;
		p.StartInfo.WorkingDirectory = Environment.CurrentDirectory;

		p.StartInfo.ArgumentList.Add( "+game" );
		p.StartInfo.ArgumentList.Add( Project.Current.GetProjectPath() );

		AddUserCommandLineArgs( p.StartInfo, EditorPreferences.DedicatedServerCommandLineArgs );

		p.Start();
	}

	static void SpawnProcess()
	{
		using var p = new Process();

		//Output not redirected: nothing reads it, and a full pipe would stall the instance
		p.StartInfo.FileName = EngineExecutables.EditorFile;
		p.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
		p.StartInfo.UseShellExecute = false;

		p.StartInfo.ArgumentList.Add( "-joinlocal" );

		int instanceCount = Process.GetProcessesByName( EngineExecutables.Editor ).Length;
		p.StartInfo.ArgumentList.Add( "+instanceid" );
		p.StartInfo.ArgumentList.Add( (instanceCount + 1).ToString() );

		if ( EditorPreferences.WindowedLocalInstances )
		{
			p.StartInfo.ArgumentList.Add( "-sw" );
			p.StartInfo.ArgumentList.Add( "-720" );
		}

		AddUserCommandLineArgs( p.StartInfo, EditorPreferences.NewInstanceCommandLineArgs );

		p.Start();
	}

	static void AddUserCommandLineArgs( ProcessStartInfo startInfo, string argumentString )
	{
		if ( string.IsNullOrWhiteSpace( argumentString ) )
			return;

		var args = argumentString.Split( ' ',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

		foreach ( var arg in args )
			startInfo.ArgumentList.Add( arg );
	}

	sealed class LobbySettings
	{
		public LobbyPrivacy LobbyPrivacy
		{
			get => EditorUtility.Network.HostPrivacy;
			set => EditorUtility.Network.HostPrivacy = value;
		}

		[Range( 0f, 500f ), Step( 25f )]
		public int SimulateLag
		{
			get => EditorUtility.Network.SimulatedLag;
			set => EditorUtility.Network.SimulatedLag = value;
		}

		[Range( 0f, 100f ), Step( 0.5f )]
		public float SimulatePacketLoss
		{
			get => EditorUtility.Network.SimulatedPacketLoss;
			set => EditorUtility.Network.SimulatedPacketLoss = value;
		}
	}

	sealed class MenuRow
	{
		readonly Panel row;
		readonly Func<bool> enabled;

		public MenuRow( Panel parent, string title, string icon, Action action, Func<bool> enabled = null )
		{
			this.enabled = enabled;

			row = parent.AddChild<Panel>( "menu-row" );
			row.AddClass( "menu-row" );

			if ( !string.IsNullOrEmpty( icon ) )
				row.AddChild<IconPanel>( "icon" ).Text = icon;

			row.AddChild<Rinten.UI.Label>( "caption" ).Text = title;

			row.AddEventListener( "onmousedown", _ =>
			{
				if ( IsEnabled() ) action();
			} );
		}

		public void Sync() => row.SetClass( "disabled", !IsEnabled() );

		bool IsEnabled() => enabled?.Invoke() ?? true;
	}
}
