using Native;
using Rinten.UI;
using System;

namespace Editor
{
	public partial class Menu : Widget
	{
		/// <summary>Whether this menu has any submenus in it.</summary>
		public bool HasMenus => Menus.Count > 0;

		/// <summary>How many menus hang off this one.</summary>
		public int MenuCount => Menus.Count;

		/// <summary>Whether it has any rows of its own worth showing - separators are not.</summary>
		public bool HasOptions => Options.Any( x => x.IsValid && !x.IsSeparator );

		/// <summary>How many options this menu holds, its submenus aside.</summary>
		public int OptionCount => Options.Count;

		/// <summary>Called just before the menu opens.</summary>
		public event Action AboutToShow;

		/// <summary>And just before it closes.</summary>
		public event Action AboutToHide;

		/// <summary>What a submenu's row says.</summary>
		public string Title
		{
			get => title;
			set
			{
				title = value ?? string.Empty;

				ParentMenu?.MarkNeedsArrange();
			}
		}


		private string title = string.Empty;

		string _icon;

		public string Icon
		{
			get => _icon;
			set
			{
				if ( _icon == value ) return;

				_icon = value;

				return;
			}
		}

		//This property holds whether tooltips of menu actions should be visible. This property
		//specifies whether action menu entries show their tooltip. By default, this property is false .
		private bool toolTipsVisible;

		public bool ToolTipsVisible
		{
			get => toolTipsVisible;
			set
			{
				toolTipsVisible = value;
			}
		}

		public Menu ParentMenu { get; private set; }
		public Menu RootMenu => ParentMenu?.RootMenu ?? this;

		public Menu( Widget parent = null ) : base()
		{
			MakeSurface( parent );

			Visible = false;
			DeleteOnClose = true;
		}

		public Menu( string title, Widget parent = null ) : this( parent )
		{
			Title = title;
		}

		protected virtual void OnAboutToShow()
		{
			foreach ( var o in Options )
			{
				o.AboutToShow();
			}

			AboutToShow?.InvokeWithWarning();
		}

		protected virtual void OnAboutToHide()
		{
			AboutToHide?.InvokeWithWarning();
		}

		public virtual Option AddOption( string name, string icon = null, Action action = null, string shortcut = null )
		{
			if ( string.IsNullOrWhiteSpace( icon ) ) icon = null;

			var o = new Option( this, name, icon, action );

			if ( shortcut != null ) o.ShortcutName = shortcut;

			return AddOption( o );
		}

		public virtual Option AddOptionWithImage( string name, Pixmap icon, Action action = null, string shortcut = null )
		{
			var o = new Option( this, name, icon, action );

			if ( shortcut != null ) o.ShortcutName = shortcut;

			return AddOption( o );
		}

		/// <summary>Like AddOption, except will automatically create the menu path from the array of names</summary>
		public Option AddOption( string[] path, string icon = null, Action action = null, string shortcut = null )
		{
			return AddOption( path.Select( x => new PathElement( x, icon ) ).ToArray(), action, shortcut );
		}

		/// <summary>Like AddOption, except will automatically create the menu path from the array of names</summary>
		public Option AddOption( ReadOnlySpan<PathElement> path, Action action = null, string shortcut = null )
		{
			if ( path.Length == 1 )
			{
				return AddOption( path[0].Name, path[0].Icon, action, shortcut );
			}

			var o = FindOrCreateMenu( path[0].Name );
			if ( string.IsNullOrEmpty( o.Icon ) )
			{
				o.Icon = path[0].Icon;
			}

			return o.AddOption( path[1..], action, shortcut );
		}

		public virtual Option AddOption( Option option )
		{
			option.SetParent( this );
			option.Order = ++added;

			Options.Add( option );
			ContentsChanged();

			return option;
		}

		//Counts what has been put in this menu, so a row knows where it goes.
		private int added;

		/// <summary>Add a widget as an action to the menu.<br/> Some widgets such as <see cref="Widget"/> and <see cref="LineEdit"/> require <see cref="Widget.OnMouseReleased"/> to set <see cref="MouseEvent.Accepted"/> to <see langword="true"/> to prevent the menu from closing.</summary>
		public T AddWidget<T>( T widget ) where T : Widget
		{
			//A widget in a menu is a row of the menu that draws itself.
			widget.SetPanelParent( this );
			Widgets.Add( widget );
			ContentsChanged();

			return widget;
		}

		//Insert a widget at a specific position in the menu
		internal T InsertWidgetAt<T>( T widget, int position ) where T : Widget
		{
			widget.SetPanelParent( this );

			Widgets.Insert( Math.Clamp( position, 0, Widgets.Count ), widget );
			ContentsChanged();

			return widget;
		}

		public Label AddHeading( string title )
		{
			var label = new Label( title ) { Color = Theme.Text };

			var option = new Option( this, title )
			{
				IsHeading = true,
				HeadingLabel = label,
				Enabled = false
			};

			AddOption( option );

			return label;
		}

		public void GetPathTo( string path, List<Menu> list )
		{
			GetPathTo( GetSplitPath( path ), list );
		}

		public void GetPathTo( ReadOnlySpan<PathElement> path, List<Menu> list )
		{
			if ( path.Length <= 1 )
			{
				return;
			}

			var menu = FindOrCreateMenu( path[0].Name );
			if ( menu == null ) return;

			menu.Icon ??= path[0].Icon;

			list.Add( menu );

			menu.GetPathTo( path[1..], list );
		}

		public Menu FindOrCreateMenu( string name )
		{
			Menus.RemoveAll( x => !x.IsValid );

			var m = Menus.FirstOrDefault( x => x.Title.ToLower() == name.ToLower() );
			if ( m != null ) return m;

			return AddMenu( name );
		}

		protected List<Menu> Menus = [];
		protected List<Option> Options = [];

		//Every option in this menu and in the menus under it, each with the path it is found at - which is what a search box matches against.
		internal List<(Option Option, string FullPath)> GetAllOptionsRecursive( string pathPrefix = "" )
		{
			var result = new List<(Option, string)>();

			foreach ( var option in Options )
			{
				//An option can have no text - an icon, a widget - and a menu no title, and the search reads every path it is given.
				var fullPath = string.IsNullOrEmpty( pathPrefix ) ? option.Text ?? string.Empty : $"{pathPrefix} > {option.Text}";
				result.Add( (option, fullPath) );
			}

			foreach ( var menu in Menus )
			{
				var newPath = string.IsNullOrEmpty( pathPrefix ) ? menu.Title : $"{pathPrefix} > {menu.Title}";
				result.AddRange( menu.GetAllOptionsRecursive( newPath ) );
			}

			return result;
		}

		//Remove this menu from its parent safely
		internal void RemoveFromParent()
		{
			ParentMenu?.Menus.Remove( this );
		}

		public Menu AddMenu( string name, string icon = null )
		{
			var menu = new Menu( name, this ) { ParentMenu = this };

			if ( icon != null ) menu.Icon = icon;
			return AddMenu( menu );
		}

		public Menu AddMenu( Menu menu )
		{
			Menus.Add( menu );

			menu.ParentMenu = this;
			menu.DeleteOnClose = false;
			menu.Order = ++added;
			ContentsChanged();

			return menu;
		}

		//Where this submenu's row goes among the options, in the order they were added.
		internal int Order { get; set; }

		public Option GetOption( string name )
		{
			return Options.FirstOrDefault( x => x.Text == name );
		}

		public void RemoveOption( string name )
		{
			var o = GetOption( name );
			if ( o == null ) return;
			RemoveOption( o );
		}

		public void RemoveOption( Option option )
		{
			Options.Remove( option );
			ContentsChanged();
		}

		public void RemoveWidget( Widget widget )
		{
			if ( !Widgets.Remove( widget ) ) return;

			widget.SetPanelParent( null );
			ContentsChanged();
		}

		/// <summary>Remove all options</summary>
		public void RemoveOptions()
		{
			Options.Clear();
			ContentsChanged();
		}

		/// <summary>Remove all menus</summary>
		public void RemoveMenus()
		{
			foreach ( var menu in Menus.Where( x => x.IsValid ) )
			{
				menu.Destroy();
			}

			Menus.Clear();
			ContentsChanged();
		}

		/// <summary>Remove all widgets</summary>
		public void RemoveWidgets()
		{
			foreach ( var widget in Widgets ) widget.SetPanelParent( null );

			Widgets.Clear();
			ContentsChanged();
		}

		/// <summary>A line between groups of options: an option with nothing on it.</summary>
		public Option AddSeparator()
		{
			return AddOption( new Option( this ) );
		}

		public void OpenAt( Vector2 position, bool modal = true )
		{
			//Modal or not, ours is a panel on top of the window rather than a window of its own, so there is nothing to block on.
			OpenPanel( position );
		}

		/// <summary>Opens at a point in the window that contains this panel.</summary>
		public void OpenAt( Vector2 position, Panel anchor, bool modal = true )
		{
			if ( anchor is null )
			{
				OpenAt( position, modal );
				return;
			}

			OpenPanel( position, anchor: anchor );
		}

		/// <summary>Open this menu at the mouse cursor position</summary>
		public void OpenAtCursor( bool modal = false )
		{
			OpenAt( Application.CursorPosition, modal );
		}

		/// <summary>Opens at the cursor in the window that contains this panel.</summary>
		public void OpenAtCursor( Panel anchor, bool modal = false )
		{
			OpenAt( Application.CursorPosition, anchor, modal );
		}

		/// <summary>Opens below an anchor with the menu's right edge on the anchor's right edge.</summary>
		public void OpenBelowRight( Panel anchor, float gap = 4f, bool modal = false )
		{
			if ( anchor is null ) return;

			OpenBelowRight( anchor.GetScreenStyleRect(), gap, modal, anchor );
		}

		/// <summary>Opens below an anchor rect with the menu's right edge on the anchor's right edge.</summary>
		public void OpenBelowRight( Rect anchorScreenRect, float gap = 4f, bool modal = false, Panel anchor = null )
		{
			OpenPanel( default, anchorScreenRect, gap, anchor );
		}

		/// <summary>Opens below an anchor with the menu's left edge on the anchor's left edge.</summary>
		public void OpenBelowLeft( Panel anchor, float gap = 4f, bool modal = false )
		{
			if ( anchor is null ) return;

			OpenBelowLeft( anchor.GetScreenStyleRect(), gap, modal, anchor );
		}

		/// <summary>Opens below an anchor rect with the menu's left edge on the anchor's left edge.</summary>
		public void OpenBelowLeft( Rect anchorScreenRect, float gap = 4f, bool modal = false, Panel anchor = null )
		{
			OpenPanel( default, anchorScreenRect, gap, anchor, alignRight: false );
		}

		public void Clear()
		{
			Options.Clear();
			Menus.Clear();
			ContentsChanged();
		}

		public Option SelectedOption
		{
			get
			{
				//Ours has no notion of an option being "active" without the pointer on it, and nothing in the editor reads this on a menu that is not open.
				return null;
			}
		}
	}
}
