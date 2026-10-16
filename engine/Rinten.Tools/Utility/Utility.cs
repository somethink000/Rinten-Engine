using Facepunch.ActionGraphs;
using Rinten.Engine.Settings;
using Rinten.Internal;
using Rinten.Utility;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Editor;

public static partial class EditorUtility
{
	public class OnInspectArgs : EventArgs
	{
		public bool Cancel { get; set; }

		public object Object { get; set; }
	}

	/// <summary>Called when InspectorObject changes</summary>
	public static event Action<OnInspectArgs> OnInspect;

	static object _inspectorObject;

	/// <summary>Set the object to be inspected by the inspector.</summary>
	public static object InspectorObject
	{
		get => _inspectorObject;
		set
		{
			if ( _inspectorObject == value ) return;

			if ( OnInspect == null )
			{
				_inspectorObject = value;
				return;
			}

			var eventArgs = new OnInspectArgs
			{
				Object = value,
				Cancel = false
			};

			OnInspect.Invoke( eventArgs );

			if ( eventArgs.Cancel == false )
			{
				_inspectorObject = eventArgs.Object;
			}
		}
	}

	/// <summary>Puts something in the inspector and brings the inspector into view</summary>
	public static void Inspect( object target )
	{
		if ( target is null ) return;

		var docks = EditorWindow?.DockManager;

		docks?.SetDockState( "Inspector", true );
		docks?.RaiseDock( "Inspector" );

		InspectorObject = target;
	}

	public static void AddLogger( Action<LogEvent> logger )
	{
		Rinten.Diagnostics.Logging.OnMessage += logger;
	}

	public static void RemoveLogger( Action<LogEvent> logger )
	{
		Rinten.Diagnostics.Logging.OnMessage -= logger;
	}

	public static ConCmdAttribute.AutoCompleteResult[] AutoComplete( string text, int maxCount )
	{
		return ConVarSystem.GetAutoComplete( text, maxCount );
	}

	/// <summary>Get all the root panels.</summary>
	public static HashSet<IPanel> GetRootPanels() => IPanel.GetAllRootPanels();

	/// <summary>The widget a panel fronts or is drawn inside; null for game UI panels</summary>
	public static Widget WidgetOf( IPanel panel )
	{
		for ( var at = panel as Rinten.UI.Panel; at is not null; at = at.Parent )
		{
			if ( at is WidgetSurface surface ) return surface.Widget;
		}

		return null;
	}

	/// <summary>The root panels a game owns: every one that is not an editor window's.</summary>
	public static IEnumerable<IPanel> GetGameRootPanels()
	{
		foreach ( var root in IPanel.GetAllRootPanels() )
		{
			if ( root is Rinten.UI.RootPanel { ExternallyDriven: false } )
				yield return root;
		}
	}

	public static void SendToRecycleBin( string filename )
	{
		Rinten.EnginePlatform.SafeRemoveFile( filename );
	}

	/// <summary>Open a folder (or url)</summary>
	public static void OpenFolder( string path )
	{
		System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo()
		{
			FileName = path,
			UseShellExecute = true,
			Verb = "open",
		} );
	}

	/// <summary>Open a folder (or url)</summary>
	public static void OpenFile( string path )
	{
		try
		{
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo()
			{
				FileName = path,
				UseShellExecute = true,
				Verb = "open",
			} );
		}
		catch
		{
			//Show "Open With" dialog
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo()
			{
				FileName = path,
				UseShellExecute = false,
				Verb = "openas",
			} );
		}
	}


	/// <summary>Open given file's folder in OS file explorer and select given file.</summary>
	public static void OpenFileFolder(string path)
	{
    	path = System.IO.Path.GetFullPath(path);

    	if (OperatingSystem.IsWindows())
    	{
    	    System.Diagnostics.Process.Start("explorer.exe", string.Format("/select,\"{0}\"", path));
    	}
    	else if ( OperatingSystem.IsLinux() )
		{
		    string directory = System.IO.Path.GetDirectoryName( path );

		    try
		    {
		        // dbus-send splits array elements on commas, so escape them
		        string uriPath = new Uri( path ).AbsoluteUri.Replace( ",", "%2C" );

		        var psi = new System.Diagnostics.ProcessStartInfo( "dbus-send" )
		        {
		            UseShellExecute = false,
		        };

		        psi.ArgumentList.Add( "--session" );
		        psi.ArgumentList.Add( "--print-reply" );
		        psi.ArgumentList.Add( "--dest=org.freedesktop.FileManager1" );
		        psi.ArgumentList.Add( "/org/freedesktop/FileManager1" );
		        psi.ArgumentList.Add( "org.freedesktop.FileManager1.ShowItems" );
		        psi.ArgumentList.Add( $"array:string:{uriPath}" );
		        psi.ArgumentList.Add( "string:" );

		        using var process = System.Diagnostics.Process.Start( psi );
		        process.WaitForExit();

		        if ( process.ExitCode != 0 )
		            OpenFolder( directory );
		    }
		    catch ( Exception e )
		    {
		        Log.Warning( e.Message );
		        OpenFolder( directory );
		    }
		}
	}

	private static string GetDestinationPath( string source, string directory )
	{
		var fileName = System.IO.Path.GetFileName( source );
		var destinationPath = System.IO.Path.Combine( directory, fileName );

		return destinationPath;
	}

	//Copies a file to a directory
	internal static void CopyFileToDirectory( string filePath, string directory, bool overwrite = true )
	{
		var destinationPath = GetDestinationPath( filePath, directory );

		if ( !System.IO.File.Exists( filePath ) )
			throw new FileNotFoundException( $"File '{filePath}' doesn't exist" );

		if ( !System.IO.Directory.Exists( directory ) )
			throw new DirectoryNotFoundException( $"Directory '{directory}' doesn't exist" );

		System.IO.File.Copy( filePath, destinationPath, overwrite );
	}

	/// <summary>Moves an asset's source and compiled files to a directory (if they exist)</summary>
	public static void MoveAssetToDirectory( Asset asset, string directory, bool overwrite = true )
	{
		var currentPath = System.IO.Path.GetDirectoryName( asset.AbsolutePath );

		//fuck me c# - is this how we compare paths
		var dirA = System.IO.Path.GetFullPath( currentPath ).ToLower().Trim( '/', '\\' );
		var dirB = System.IO.Path.GetFullPath( directory ).ToLower().Trim( '/', '\\' );

		//already in this directory!
		if ( dirA == dirB )
			return;

		var filename = System.IO.Path.GetFileName( asset.AbsolutePath );
		MoveAsset( asset, System.IO.Path.Combine( directory, filename ), overwrite );
	}

	public static void RenameDirectory( string directory, string newDirectory, bool recursive = false )
	{
		if ( !System.IO.Directory.Exists( directory ) )
			return;

		//don't allow moving a folder inside a subfolder of itself!
		if ( newDirectory.StartsWith( directory, StringComparison.OrdinalIgnoreCase ) )
			return;

		if ( !System.IO.Directory.Exists( newDirectory ) )
			System.IO.Directory.CreateDirectory( newDirectory );


		var assets = AssetSystem.All.Where( x => x.AbsolutePath.StartsWith( directory.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase ) && x.AbsolutePath.Substring( directory.Length ).Contains( '/' ) );
		foreach ( var asset in assets )
		{
			if ( !System.IO.Path.Exists( asset.AbsolutePath ) ) continue;
			MoveAssetToDirectory( asset, newDirectory );
		}

		var files = System.IO.Directory.GetFiles( directory, "*", System.IO.SearchOption.TopDirectoryOnly );
		foreach ( var file in files )
		{
			var newFile = file.Replace( directory, newDirectory );
			System.IO.File.Move( file, newFile );
		}

		var folders = System.IO.Directory.GetDirectories( directory, "*", System.IO.SearchOption.TopDirectoryOnly );
		foreach ( var folder in folders )
		{
			RenameDirectory( folder, folder.Replace( directory, newDirectory ), true );
		}

		if ( !recursive ) System.IO.Directory.Delete( directory, true );
	}

	/// <summary>Rename an asset, and every file that goes with it, within its folder.</summary>
	public static bool RenameAsset( Asset asset, string newName, bool overwrite = false )
	{
		newName = newName?.Trim().GetFilenameSafe();
		if ( string.IsNullOrEmpty( newName ) )
			return false;

		var current = Asset.RelatedFileRoot( asset.AbsolutePath );

		if ( string.IsNullOrEmpty( System.IO.Path.GetExtension( newName ) ) )
			newName += System.IO.Path.GetExtension( current );

		return MoveAsset( asset, System.IO.Path.Combine( System.IO.Path.GetDirectoryName( current ), newName ), overwrite );
	}

	/// <summary>Move an asset, and every file that goes with it - see <see cref="Asset.RelatedFileExts"/> - to a new path. Checks the whole move can be made before making any of it, so an asset is never left half in one place and half in the other.</summary>
	public static bool MoveAsset( Asset asset, string path, bool overwrite = false )
	{
		//Not lowercased: the name of a file that is opened keeps its case.
		path = Asset.RelatedFileRoot( path?.Trim().Replace( '\\', '/' ) );

		if ( string.IsNullOrEmpty( path ) )
			return false;

		var oldPath = Asset.RelatedFileRoot( asset.AbsolutePath );

		if ( string.IsNullOrEmpty( oldPath ) )
			return false;

		if ( string.Equals( oldPath, path, StringComparison.Ordinal ) )
			return true;

		//Only the capitalisation changes, on a disk that does not care about it: the "file already there" is this one.
		var caseOnly = string.Equals( oldPath, path, StringComparison.OrdinalIgnoreCase ) && !OperatingSystem.IsLinux();

		if ( !overwrite && !caseOnly && Collides( path, "move" ) )
			return false;

		//Held back rather than dropped, so the resource watchers see the old name go and
		//the new one arrive in one batch, and move the resource instead of destroying it.
		using ( FileWatch.Suppress() )
		{
			foreach ( var ext in Asset.RelatedFileExts )
			{
				var file = new FileInfo( oldPath + ext );
				if ( file.Exists ) file.MoveTo( path + ext, overwrite || caseOnly );
			}
		}

		//The file under the new name is registered as its own asset.
		if ( !caseOnly )
			asset.IsDeleted = true;

		return true;
	}

	/// <summary>Copy an asset, and every file that goes with it, to a new path.</summary>
	public static bool CopyAsset( Asset asset, string path, bool overwrite = false )
	{
		path = Asset.RelatedFileRoot( path?.Trim().Replace( '\\', '/' ) );

		if ( string.IsNullOrEmpty( path ) )
			return false;

		var oldPath = Asset.RelatedFileRoot( asset.AbsolutePath );

		if ( string.IsNullOrEmpty( oldPath ) || string.Equals( oldPath, path, StringComparison.Ordinal ) )
			return false;

		if ( !overwrite && Collides( path, "copy" ) )
			return false;

		using ( FileWatch.Suppress() )
		{
			foreach ( var ext in Asset.RelatedFileExts )
			{
				var file = new FileInfo( oldPath + ext );
				if ( file.Exists ) file.CopyTo( path + ext, overwrite );
			}
		}

		return true;
	}

	//Whether any file of an asset at path is already there.
	private static bool Collides( string path, string what )
	{
		foreach ( var ext in Asset.RelatedFileExts )
		{
			if ( !File.Exists( path + ext ) ) continue;

			Log.Error( $"Cannot {what} asset, '{path + ext}' already exists!" );
			return true;
		}

		return false;
	}

	/// <summary>Copies an asset, and every file that goes with it, to a directory.</summary>
	public static void CopyAssetToDirectory( Asset asset, string directory, bool overwrite = true )
	{
		if ( !System.IO.Directory.Exists( directory ) )
			throw new DirectoryNotFoundException( $"Directory '{directory}' doesn't exist" );

		var filename = System.IO.Path.GetFileName( Asset.RelatedFileRoot( asset.AbsolutePath ) );
		CopyAsset( asset, System.IO.Path.Combine( directory, filename ), overwrite );
	}

	public static Task<bool> PutAsync( Stream fileStream, string endpoint, Rinten.Utility.DataProgress.Callback progress = null, CancellationToken token = default )
	{
		try
		{
			return Rinten.Utility.Web.PutAsync( fileStream, endpoint, token, progress );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"PutAsync failed: {e.Message}" );
			return Task.FromResult( false );
		}
	}

	public static Task<bool> DownloadAsync( string url, string targetfile, Rinten.Utility.DataProgress.Callback progress = null, CancellationToken token = default )
	{
		try
		{
			return Rinten.Utility.Web.DownloadFile( url, targetfile, token, progress );
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"DownloadAsync failed: {e.Message}" );
			return Task.FromResult( false );
		}
	}

	//People are lazy and will be calling this all the time if we let them. Lets keep it hidden for as long as possible Ask
	//whichever window is in front to draw itself again. Ours redraw when something changes, so there is nothing to ask.
	internal static void RedrawActiveWindow()
	{
	}

	public static SceneWorld CreateSceneWorld()
	{
		return new SceneWorld() { IsTransient = false };
	}

	static SoundHandle lastSound;

	/// <summary>Stop a sound playing from an asset preview</summary>
	public static void StopAssetSound()
	{
		lastSound?.Stop();
		lastSound = null;
	}

	/// <summary>Plays an asset sound in 2d space</summary>
	public static bool PlayAssetSound( Asset asset )
	{
		StopAssetSound();

		if ( asset == null )
			return false;

		var compiledFile = asset.GetCompiledFile( false );
		if ( string.IsNullOrEmpty( compiledFile ) )
		{
			asset.Compile( false );
			return false;
		}

		if ( asset.AssetType == AssetType.SoundFile )
		{
			var foundFile = SoundFile.Load( compiledFile );
			if ( foundFile is null )
				return false;

			return PlayAssetSound( foundFile );
		}
		else if ( asset.AssetType == AssetType.SoundEvent )
		{
			var foundFile = SoundEvent.Load<SoundEvent>( compiledFile );
			if ( foundFile is null )
				return false;

			return PlayAssetSound( foundFile );
		}

		return false;
	}

	/// <summary>Plays an asset sound in 2d space</summary>
	public static bool PlayAssetSound( SoundEvent file )
	{
		StopAssetSound();

		if ( file is null )
			return false;

		lastSound = Sound.Play( file );

		if ( lastSound.IsValid() )
		{
			lastSound.ListenLocal = true;
			lastSound.Position = Vector3.Forward * 64.0f;
		}

		return lastSound.IsValid();
	}

	/// <summary>Plays an asset sound in 2d space</summary>
	public static bool PlayAssetSound( SoundFile file )
	{
		StopAssetSound();

		if ( file is null )
			return false;

		lastSound = Sound.PlayFile( file, 0.5f, 1.0f );

		if ( lastSound.IsValid() )
		{
			lastSound.ListenLocal = true;
			lastSound.Position = Vector3.Forward * 64.0f;
		}

		return lastSound.IsValid();
	}

	/// <summary>Plays a sound event</summary>
	public static SoundHandle PlaySound( string sound, float startTime = 0.0f )
	{
		var foundFile = SoundFile.Load( sound );
		if ( foundFile is null )
			return null;

		var s = Sound.PlayFile( foundFile );
		s.Time = startTime;
		s.ListenLocal = true;
		s.Position = Vector3.Forward * 64.0f;

		return s;
	}

	/// <summary>Plays a sound via the OS, which is the way you play a sound if you want it to be heard when the game is tabbed away</summary>
	public static bool PlayRawSound( string file )
	{
		var fullPath = EngineFileSystem.Assets.GetFullPath( file );
		if ( fullPath == null ) fullPath = EngineFileSystem.CoreContent.GetFullPath( file );

		if ( fullPath == null )
			return false;

		//Their sound system played this one outside the game's mixer so it carried on while the window
		//was tabbed away. Ours has no such door, so it goes through the mixer like everything else.
		var sound = SoundFile.Load( file );

		if ( sound is null ) return false;

		//Played locally, as scene sounds need a listener the editor scene usually lacks
		var handle = Sound.PlayFile( sound );
		handle.ListenLocal = true;
		handle.TargetMixer = Rinten.Audio.Mixer.FindMixerByName( "UI" );

		return true;
	}

	/// <summary>Delete the cached package info. This will cause any future requests to get fresh information from the backend. This is useful if you just updated something and want to see the changes.</summary>
	public static void ClearPackageCache()
	{
		Package.ClearCache();
	}

	/// <summary>Get a serialized object for this object. Because you're in the editor, this is an unrestricted object, we aren't whitelisting or using TypeLibrary.</summary>
	public static SerializedObject GetSerializedObject( object obj )
	{
		return new ReflectionSerializedObject( obj );
	}

	/// <summary>Create a video writer</summary>
	public static VideoWriter CreateVideoWriter( string path, VideoWriter.Config config )
	{
		return new VideoWriter( path, config );
	}

	static bool disabledStreaming = false;

	/// <summary>Force textures to load fully when loading a model etc..</summary>
	public static IDisposable DisableTextureStreaming()
	{
		bool prev = disabledStreaming;
		disabledStreaming = true;
		return default;
	}

	/// <summary>Where the legacy base content is installed, or null if it is not on this machine.</summary>
	public static string FindSBoxInstall() => Rinten.Porting.SBox.SBoxBaseContent.Find();

	/// <summary>The legacy content install a folder is, or null where it is not one - so a path somebody typed or picked can be checked before it is written down.</summary>
	public static string SBoxInstallAt( string folder ) => Rinten.Porting.SBox.SBoxBaseContent.Holds( folder );

	/// <summary>Quit the whole engine</summary>
	public static void Quit( bool toLauncher = false )
	{
		try { Rinten.CoreUiWindow.DropDisplay(); } catch { }

		Rinten.Application.Exit();

		if ( toLauncher )
		{
			ProcessStartInfo info = new ProcessStartInfo( EngineExecutables.EditorFile );
			info.WorkingDirectory = System.Environment.CurrentDirectory;

			Process.Start( info );
		}
	}

	/// <summary>True if we're currently recording a video (using the video command, or F6)</summary>
	public static bool IsRecordingVideo => ScreenRecorder.IsRecording();

	/// <summary>Display a message over the window that asked - or, with no parent, the one being used. It returns at once; the card is dismissed by its button. The icon is not drawn - it is kept so that existing callers still compile.</summary>
	public static void DisplayDialog( string title, string message, string okay = "Okay", string icon = "⚠️", Widget parent = null )
		=> EditorModal.Notice( EditorOverlay.For( parent ), title, message, okay );

	/// <summary>Ask a yes or no question over the window that asked. <paramref name="action"/> runs on yes; it returns at once. The icon is not drawn.</summary>
	public static void DisplayDialog( string title, string message, string noLabel, string yesLabel, Action action, string icon = "❓", Widget parent = null )
		=> EditorModal.Confirm( EditorOverlay.For( parent ), title, message, action, yesLabel, noLabel );

	/// <summary>Show a popup control sheet for this. You should set parent to the control from this this sheet is created. If you do that properly, when that control is deleted, this popup will get deleted too. If you set it to null then the control sheet will stay open until it's closed.</summary>
	public static Widget OpenControlSheet( SerializedObject so, Widget parent, bool createWindow = true )
	{
		var originalType = so.Targets.FirstOrDefault()?.GetType() ?? null;
		if ( originalType is null ) return null;

		//Store a list of types to check (including original and base types)
		var typesToCheck = new List<Type>();
		var currentType = originalType;

		//Build list of types to check while maintaining reference to original type
		while ( currentType is not null )
		{
			typesToCheck.Add( currentType );
			currentType = currentType.BaseType;
		}

		foreach ( var typeToCheck in typesToCheck )
		{
			//Make IPopupEditor<T> with the original target type
			var genericType = typeof( IPopupEditor<> ).MakeGenericType( typeToCheck );

			bool IsSpecificEditor( TypeDescription widgetType )
			{
				var interfaces = widgetType.Interfaces
					.Where( x => x.IsGenericType )
					.ToList();

				//This is somehow necessary because GetInterfaces() lists out System.Object, and TextureGenerator for TextureGeneratorPopup

				var mostSpecificInterface = interfaces.FirstOrDefault( x =>
					x.GetGenericTypeDefinition() != typeof( IPopupEditor<> ) &&
					x.GetGenericArguments().Any( arg => arg != typeof( object ) && arg.IsAssignableTo( typeToCheck ) ) );

				var isSpecific = mostSpecificInterface != null;
				return isSpecific;
			}

			//Find any Widgets that implement it
			var allWidgets = EditorTypeLibrary.GetTypes<Widget>()
				.Where( x => x.Interfaces.Contains( genericType ) )
				.ToList();

			var list = allWidgets
				.OrderByDescending( x => IsSpecificEditor( x ) )
				.ThenBy( x => x.Name )
				.ToList();

			var found = list.FirstOrDefault();

			if ( found is not null )
			{
				try
				{
					var editor = found.Create<Widget>( [so, parent] );

					if ( createWindow )
					{
						var window = new PopupEditorWindow( parent );
						window.Layout.Add( editor );

						window.WindowTitle = $"Editing {so.TypeTitle}";
						window.SetWindowIcon( so.TypeIcon ?? "edit_note" );

						if ( so.ParentProperty is not null )
						{
							window.WindowTitle = $"Editing {so.ParentProperty.DisplayName} ({so.TypeTitle})";
						}

						window.ShowWindowAtCursor();
					}

					return editor;
				}
				catch ( System.Exception e )
				{
					Log.Error( e, $"Exception when creating {found.FullName}( SerializedObject target, Widget parent )" );
				}
			}
		}

		Log.Warning( $"OpenControlSheet - No editor found for {originalType.Name}" );
		return default;
	}

	/// <summary>Gets every search path seperated by ;</summary>
	public static string GetSearchPaths()
	{
		//The search paths are whatever the editor has mounted.
		return string.Join( ';', Project.All.Where( x => x.Active ).Select( x => x.GetAssetsPath() ).Where( x => !string.IsNullOrEmpty( x ) ) );
	}

	public static IEnumerable<string> FontFamilies => FontManager.Instance.FontFamilies;

	/// <summary>Access to the client's render settings</summary>
	public static RenderSettings RenderSettings => Rinten.Engine.Settings.RenderSettings.Instance;

	//Reading these text formats is the importer's job, and it is not written yet, so this says it cannot.
	/// <summary>Some assets are kv3, we want to convert them to json Some assets are kv3, we want to convert them to json.</summary>
	public static string KeyValues3ToJson( string kvString )
	{
		return null;
	}

	/// <summary>Some old ass assets are keyvalues (1). Convert them to Json so we can use them.</summary>
	public static string KeyValues1ToJson( string kvString )
	{
		return null;
	}

	/// <summary>The icon the desktop shows for a file.</summary>
	public static Pixmap GetFileThumbnail( string filePath, int width, int height )
	{
		return null;
	}

	/// <summary>Saves everything and restarts this executable with the same project</summary>
	public static void RestartEditor()
	{
		RestartEditor( enterVr: false );
	}

	public static void RestartEditor( bool enterVr )
	{
		RestartEditor( Project.Current?.ConfigFilePath, enterVr );
	}

	/// <summary>Restarts the editor to open <paramref name="project"/> - or the launcher, with null. For a restart before a project is open, which has no Project.Current to go by.</summary>
	public static void RestartEditor( string project, bool enterVr = false )
	{
		var executable = Environment.ProcessPath;

		if ( string.IsNullOrEmpty( executable ) )
			throw new InvalidOperationException( "This process has no path to start again from" );

		Editor.EditorMainWindow.Quit();

		var info = new ProcessStartInfo( executable )
		{
			UseShellExecute = false,
			WorkingDirectory = Environment.CurrentDirectory,
		};

		if ( !string.IsNullOrWhiteSpace( project ) )
		{
			info.ArgumentList.Add( "-project" );
			info.ArgumentList.Add( project );
		}

		if ( enterVr ) info.Environment["RINTEN_ENTER_VR"] = "1";

		Process.Start( info );
	}

	/// <summary>Open a dialog prompt asking the user to restart the editor.</summary>
	public static void RestartEditorPrompt( string message, string title = "Restart Editor" )
	{
		Dialog.AskConfirm( RestartEditor, message, title, "Restart" );
	}

	/// <summary>Checks if a given folder is a code folder, e.g. [project root]/Code</summary>
	public static bool IsCodeFolder( string fullPath )
	{
		if ( fullPath == null )
			throw new ArgumentNullException( nameof( fullPath ) );

		if ( fullPath.Length == 0 )
			return false;

		var normalizedPath = Path.GetFullPath( fullPath ).TrimEnd( Path.DirectorySeparatorChar );

		foreach ( var project in Project.All )
		{
			var projectCodePath = project.GetCodePath();
			if ( string.IsNullOrEmpty( projectCodePath ) )
				continue;

			var normalizedProjectPath = Path.GetFullPath( projectCodePath ).TrimEnd( Path.DirectorySeparatorChar );

			if ( normalizedPath.Equals( normalizedProjectPath, StringComparison.OrdinalIgnoreCase ) ||
				normalizedPath.StartsWith( normalizedProjectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Checks if a given file is a code file</summary>
	public static bool IsCodeFile( string fullPath )
	{
		return Path.GetExtension( fullPath ).Equals( ".cs", StringComparison.OrdinalIgnoreCase ) ||
			Path.GetExtension( fullPath ).Equals( ".scss", StringComparison.OrdinalIgnoreCase ) ||
			Path.GetExtension( fullPath ).Equals( ".razor", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>Gets the source location for the given scene, used by action graph stack traces, and so the action graph editor knows which asset to save when editing a graph.</summary>
	public static ISourceLocation GetSourceLocation( this Scene scene )
	{
		return scene.OverrideSourceLocation ?? scene.Source?.SerializationOptions.SourceLocation;
	}

	/// <summary>Tries to find a project based on a given directory.</summary>
	public static Project FindProjectByDirectory( string fullPath )
	{
		if ( fullPath == null )
			throw new ArgumentNullException( nameof( fullPath ) );

		if ( fullPath.Length == 0 )
			return null;

		var normalizedPath = Path.GetFullPath( fullPath ).TrimEnd( Path.DirectorySeparatorChar );

		foreach ( var project in Project.All.OrderByDescending( x => x.GetRootPath().Length ) )
		{
			var projectCodePath = project.GetRootPath();
			if ( string.IsNullOrEmpty( projectCodePath ) )
				continue;

			var normalizedProjectPath = Path.GetFullPath( projectCodePath ).TrimEnd( Path.DirectorySeparatorChar );

			if ( normalizedPath.Equals( normalizedProjectPath, StringComparison.OrdinalIgnoreCase ) ||
				normalizedPath.StartsWith( normalizedProjectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			{
				return project;
			}
		}

		return null;
	}

	/// <summary>Gets a GameObjectSystem from its type</summary>
	public static GameObjectSystem GetGameObjectSystem( Scene scene, TypeDescription fromType )
	{
		return scene.GetSystemByType( fromType );
	}

	public static Asset GetAssetFromProject( Project project )
	{
		return project.ProjectSourceObject as Asset;
	}

	//Create a TypeLibrary from a collection of assemblies
	internal static TypeLibrary CreateTypeLibrary( CompilerOutput[] assemblies )
	{
		var library = new TypeLibrary();
		using var packageLoader = new Rinten.PackageLoader( "EditorTypeLibrary", typeof( GameInstanceDll ).Assembly );
		using var enroller = packageLoader.CreateEnroller( "EditorTypeLibrary" );

		enroller.OnAssemblyAdded = ( a ) =>
		{
			library.AddAssembly( a.Assembly, true );
		};

		foreach ( var assm in assemblies )
		{
			var ms = new MemoryStream( assm.AssemblyData );
			enroller.LoadAssemblyFromStream( assm.Compiler.AssemblyName, ms );
		}

		return library;
	}

	/// <summary>Finds a component in the scene and selects it in the editor</summary>
	public static void FindInScene( Component component )
	{
		if ( !component.IsValid() )
			return;

		var go = component.GameObject;

		FindInScene( go );
	}

	/// <summary>Finds a GameObject in the scene and selects it in the editor</summary>
	public static void FindInScene( GameObject go )
	{
		if ( !go.IsValid() )
			return;

		var session = SceneEditorSession.Resolve( go );
		using var scene = session.Scene.Push();
		using ( session.UndoScope( $"Selected {go}" ).Push() )
		{
			session.Selection.Set( go );
		}
	}
}


//This is created using EditorUtility.OpenControlSheet
class PopupEditorWindow : Widget
{
	public PopupEditorWindow( Widget parent ) : base( parent )
	{
		Size = new Vector2( 500, 500 );
		DeleteOnClose = true;
		Layout = Layout.Column();
		WindowFlags = WindowFlags.Tool | WindowFlags.Customized | WindowFlags.WindowSystemMenuHint | WindowFlags.CloseButton;

		Width = 400;

		ShowWindowAtCursor();
	}

	public virtual void ShowWindowAtCursor()
	{
		Show();

		Position = Application.CursorPosition - new Vector2( Width * 0.5f, 3 );
		UpdateGeometry();
		ConstrainToScreen();

		Focus();
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Escape )
		{
			Close();
			e.Accepted = true;
			return;
		}

		base.OnKeyPress( e );
	}
}
