using System;

namespace Editor
{
	[AttributeUsage( AttributeTargets.Class )]
	public class EditorAppAttribute : Attribute, Rinten.ITypeAttribute
	{
		public string Title { get; set; }
		public string Icon { get; set; }
		public string Description { get; set; }
		public Type TargetType { get; set; }

		/// <summary>Whether this takes over the editor's workspace instead of opening as its own window.</summary>
		public bool Workspace { get; set; } = true;

		/// <summary>Extensions File > Open offers in this app, e.g. "razor"; by default the asset types it edits</summary>
		public string Opens { get; set; }

		public EditorAppAttribute( string title, string icon, string description )
		{
			Title = title;
			Icon = icon;
			Description = description;
		}

		/// <summary>The app whose editor is this type, or null.</summary>
		public static EditorAppAttribute For( Type type ) =>
			type is null ? null : EditorTypeLibrary.GetAttributes<EditorAppAttribute>().FirstOrDefault( x => x.TargetType == type );

		//The extensions File > Open offers while this app is in front, or none.
		string[] OpenedExtensions()
		{
			var opens = Opens;

			if ( string.IsNullOrWhiteSpace( opens ) )
			{
				opens = string.Join( ",", TargetType.GetCustomAttributes( typeof( EditorForAssetTypeAttribute ), true )
					.OfType<EditorForAssetTypeAttribute>()
					.Select( x => x.Extension ) );
			}

			return opens.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
				.Select( x => x.TrimStart( '.' ).ToLowerInvariant() )
				.Distinct()
				.ToArray();
		}

		//Code lives under Code; everything else an editor opens is an asset.
		static bool IsCodeExtension( string extension ) => extension is "razor" or "cs" or "scss";

		//What a file of this extension is called on the File menu.
		static string KindName( string extension ) =>
			AssetType.FromExtension( extension )?.FriendlyName is { Length: > 0 } name ? name
			: extension == "razor" ? "Razor Panel" : $".{extension}";

		//What File > New makes here: the first asset type this app is the registered editor for that can be made empty.
		string CreatedExtension() => TargetType.GetCustomAttributes( typeof( EditorForAssetTypeAttribute ), true )
			.OfType<EditorForAssetTypeAttribute>()
			.Select( x => x.Extension.TrimStart( '.' ).ToLowerInvariant() )
			.FirstOrDefault( x => EditorTypeLibrary.GetAttributes<AssetTypeAttribute>().Any( a => a.Extension == x ) );

		bool MakesItsOwnFiles => TargetType.IsAssignableTo( typeof( IFileEditor ) );

		/// <summary>A request for a file an app opens, answered by the tools addon's asset picker.</summary>
		public sealed class FileRequest
		{
			/// <summary>What the picker's window says.</summary>
			public string Title { get; init; }

			/// <summary>The extensions it takes, lower case, without the dot.</summary>
			public string[] Extensions { get; init; }

			/// <summary>Where the picker opens: Assets, or Code for a razor panel.</summary>
			public string Folder { get; init; }

			/// <summary>Called with the absolute path of what was picked.</summary>
			public Action<string> Picked { get; init; }

			/// <summary>Set by whoever showed a picker, so the platform's file box is not opened as well.</summary>
			public bool Handled { get; set; }
		}

		/// <summary>File > Open's label while this app is in front; null for an app that opens nothing.</summary>
		public string OpenLabel()
		{
			var names = OpenedExtensions().Select( KindName ).ToArray();

			return names.Length == 0 ? null : $"Open {string.Join( " / ", names )}..";
		}

		/// <summary>File > New's label while this app is in front; null for an app that makes nothing.</summary>
		public string NewLabel()
		{
			var extension = MakesItsOwnFiles ? OpenedExtensions().FirstOrDefault() : CreatedExtension();

			return extension is null ? null : $"New {KindName( extension )}..";
		}

		/// <summary>Makes a file this app works on, named and placed by the user, and opens it; false if it makes none.</summary>
		public bool AskToCreate()
		{
			if ( MakesItsOwnFiles )
			{
				if ( Open() is not IFileEditor editor ) return false;

				editor.NewFile();
				return true;
			}

			if ( CreatedExtension() is not { } extension ) return false;

			CreateResource( $"New {KindName( extension )}", extension, path => OpenFile( path ) );
			return true;
		}

		/// <summary>Asks a name and a folder for a new resource of this extension, makes it, and hands back its path.</summary>
		public static void CreateResource( string title, string extension, Action<string> created )
		{
			//The folder the last one of these went in, as the editors' own save boxes remember it
			var cookie = $"LastSaveLocation.{extension}";
			var folder = ProjectCookie.GetString( cookie, "" );

			if ( !System.IO.Directory.Exists( folder ) ) folder = Project.Current?.GetAssetsPath() ?? "";

			EditorModal.PromptInFolder( null, title, folder, ( name, into ) =>
			{
				name = name.Trim();

				if ( name.EndsWith( $".{extension}", StringComparison.OrdinalIgnoreCase ) )
					name = name[..^( extension.Length + 1 )];

				var path = System.IO.Path.Combine( into, $"{name}.{extension}" );

				if ( System.IO.File.Exists( path ) )
				{
					EditorModal.Notice( null, title, $"{path} already exists." );
					return;
				}

				ProjectCookie.SetString( cookie, into );

				if ( AssetSystem.CreateResource( extension, path ) is null ) return;

				created?.Invoke( path );
			}, initial: $"new {KindName( extension ).ToLowerInvariant()}", icon: "note_add",
				image: AssetType.FromExtension( extension )?.Icon64 );
		}

		/// <summary>Picks a file this app opens and opens it; false for an app that opens nothing.</summary>
		public bool AskForFile()
		{
			var extensions = OpenedExtensions();

			if ( extensions.Length == 0 ) return false;

			PickFile( $"Open in {Title}", extensions, path => OpenFile( path ) );
			return true;
		}

		/// <summary>Picks a file of these extensions from Assets, or Code for razor; the OS file box without the tools addon.</summary>
		public static void PickFile( string title, string[] extensions, Action<string> picked )
		{
			var project = Project.Current;

			var folder = extensions.All( IsCodeExtension ) ? project?.GetCodePath() : project?.GetAssetsPath();

			var request = new FileRequest { Title = title, Extensions = extensions, Folder = folder, Picked = picked };

			EditorEvent.Run( "editor.app.pickfile", request );

			if ( request.Handled ) return;

			var fd = new FileDialog( null );
			fd.Title = title;
			fd.SetFindExistingFile();
			fd.SetModeOpen();

			if ( !string.IsNullOrEmpty( folder ) && System.IO.Directory.Exists( folder ) )
				fd.Directory = folder;

			fd.SetNameFilter( $"{title} ({string.Join( " ", extensions.Select( x => $"*.{x}" ) )})" );

			if ( !fd.Execute() || string.IsNullOrEmpty( fd.SelectedFile ) ) return;

			picked?.Invoke( fd.SelectedFile );
		}

		/// <summary>Opens a file here and gives back the editor holding it: in the workspace, as a document tab of its own.</summary>
		public Widget OpenFile( string path )
		{
			var extension = System.IO.Path.GetExtension( path ).TrimStart( '.' ).ToLowerInvariant();
			var asset = IsCodeExtension( extension ) ? null : AssetSystem.FindByPath( path );

			var assets = asset is not null && TargetType.IsAssignableTo( typeof( IAssetEditor ) );

			if ( !assets && !TargetType.IsAssignableTo( typeof( IFileEditor ) ) )
			{
				Log.Warning( $"{Title}: cannot open {path}" );
				return null;
			}

			void Load( Widget editor )
			{
				if ( assets ) ( editor as IAssetEditor )?.AssetOpen( asset );
				else ( editor as IFileEditor )?.OpenFile( path );
			}

			asset?.RecordOpened();

			if ( Workspace && WorkspaceArea.Current.IsValid() )
			{
				return WorkspaceArea.Current.OpenDocument( TargetType, Title, Icon, path,
					asset?.Name ?? System.IO.Path.GetFileName( path ), Load );
			}

			var opened = Open();
			Load( opened );

			return opened;
		}

		/// <summary>Opens the app - its tab, or a window - and gives back the editor, where there is one.</summary>
		public Widget Open()
		{
			//An editor laid out around the scene takes the scene area along, the way mapping does
			if ( TargetType.IsAssignableTo( typeof( SceneWorkspace ) ) && Rinten.Internal.GlobalToolsNamespace.EditorWindow.IsValid() )
			{
				Rinten.Internal.GlobalToolsNamespace.EditorWindow.OpenSceneWorkspace( Title, Icon, () => EditorTypeLibrary.Create<SceneWorkspace>( TargetType ) );
				return null;
			}

			if ( Workspace && WorkspaceArea.Current.IsValid() )
				return WorkspaceArea.Current.OpenEditor( TargetType, Title, Icon )?.Editor;

			var window = EditorTypeLibrary.Create<Widget>( TargetType );
			window.Show();

			return window;
		}

		/// <summary>Opens an app by its menu title, ignoring case and spaces.</summary>
		[ConCmd( "editor_app" )]
		public static void OpenApp( string title )
		{
			var apps = EditorTypeLibrary.GetAttributes<EditorAppAttribute>().ToList();

			var found = apps.FirstOrDefault( x => Matches( x.Title, title ) );

			if ( found is null )
			{
				Log.Warning( $"editor_app: no app called '{title}' - there is " +
					string.Join( ", ", apps.Select( x => $"'{x.Title}'" ) ) );

				return;
			}

			found.Open();
		}

		//The same name, ignoring case and the spaces between the words.
		static bool Matches( string app, string asked )
		{
			if ( string.IsNullOrWhiteSpace( asked ) ) return false;

			return string.Equals( app.Replace( " ", "" ), asked.Replace( " ", "" ),
				StringComparison.OrdinalIgnoreCase );
		}
	}

	/// <summary>An app whose files are not assets, such as the UI editor's razor panels, opened by path.</summary>
	public interface IFileEditor
	{
		void OpenFile( string path );

		/// <summary>File > New: asks for a new file of this editor's kind and opens it.</summary>
		void NewFile();
	}
}
