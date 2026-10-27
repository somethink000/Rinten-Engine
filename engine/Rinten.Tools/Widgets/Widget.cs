using Rinten.UI;
using System;
using System.Diagnostics;

namespace Editor
{
	[Expose]
	public partial class Widget : EditorObject
	{


		public Widget() : this( null, false )
		{

		}

		/// <summary>The default widget constructor</summary>
		public Widget( Widget parent, bool isDarkWindow = false )
		{
			MakeSurface( parent );
			EditorShortcuts.Register( this );
		}

		/// <summary>Makes the widget not interactable. This is also usually be reflected visually by the widget. The widget will not process any keyboard or mouse inputs. Applies retroactively to all children.</summary>
		public bool Enabled
		{
			get
			{
				return enabled;
			}
			set
			{
				if ( !IsValid ) return;

				if ( enabled == value ) return;

				enabled = value;
				Update();
			}
		}

		bool _readonly;
		/// <summary>Makes the widget read only. I.e. You can copy text of a text entry, but can't edit it. Applies retroactively to all children.</summary>
		public virtual bool ReadOnly
		{
			get => Parent != null && Parent.ReadOnly ? true : _readonly;
			set => _readonly = value;
		}

		//True for anything that is read or written in - a field, a text area, the console. The editor's own shortcuts stand aside for it, so that copying a line out of the log is copying a line out of the log rather than copying whatever happens to be selected in the scene.
		/// <summary>Whether this widget wants the keys somebody types to itself.</summary>
		public virtual bool WantsRawKeys => false;

		/// <summary>Parent widget. If non null, position of this widget will be relative to the parent widget. Certain events will also propagate to the parent widget if unhandled.</summary>
		public Widget Parent
		{
			get => parent;
			set
			{
				if ( value == this ) throw new Exception( "Trying to parent to self" );

				SetPanelParent( value );
			}
		}

		/// <summary>Find the closest ancestor widget of type</summary>
		public T GetAncestor<T>() where T : Widget
		{
			if ( this is T t ) return t;
			return Parent?.GetAncestor<T>();
		}

		/// <summary>Get all descendants of type T</summary>
		public IEnumerable<T> GetDescendants<T>() where T : Widget
		{
			if ( this is T t ) yield return t;

			foreach ( var child in Children )
			{
				foreach ( var descendant in child.GetDescendants<T>() )
				{
					yield return descendant;
				}
			}
		}

		/// <summary>Returns whether or not the specified Widget is a descendent of this Widget.</summary>
		public bool IsDescendantOf( Widget parent )
		{
			if ( Parent == parent ) return true;
			if ( Parent == null ) return false;
			return Parent.IsDescendantOf( parent );
		}

		/// <summary>Returns whether or not the specified Widget is an ancestor of this Widget.</summary>
		public bool IsAncestorOf( Widget child )
		{
			if ( child.Parent == this ) return true;
			if ( child.Parent == null ) return false;
			return IsAncestorOf( child.Parent );
		}

		public Margin ContentMargins
		{
			get => contentMargins;
			set
			{
				contentMargins = value;
				UpdateGeometry();
			}
		}

		public Rect ContentRect
		{
			get
			{
				return LocalRect.Shrink( contentMargins );
			}
		}

		/// <summary>Size of this widget.</summary>
		public Vector2 Size
		{
			get => size;
			set
			{
				var wanted = new Vector2(
					value.x.Clamp( MinimumWidth, MaximumWidth ),
					value.y.Clamp( MinimumHeight, MaximumHeight ) );

				if ( size == wanted ) return;

				size = wanted;

				//Somebody said how big this is. Until a layout says otherwise, that is the size it wants to be.
				SizeFromLayout = false;

				MarkNeedsArrange();
				OnResize();
				UpdateGeometry();
				Update();
			}
		}

		/// <summary>This panel's rect at 0,0</summary>
		[Hide]
		public Rect LocalRect => new Rect( 0, Size );

		/// <summary>This panel's rect in screen coordinates</summary>
		[Hide]
		public Rect ScreenRect => new Rect( ScreenPosition, Size );

		/// <summary>Utility to interact with a widget's width - use Size where possible</summary>
		[Hide]
		public float Width
		{
			get => Size.x;
			set => Size = Size.WithX( value );
		}

		/// <summary>Utility to interact with a widget's width - use Size where possible</summary>
		[Hide]
		public float Height
		{
			get => Size.y;
			set => Size = Size.WithY( value );
		}

		/// <summary>Sets <see cref="MinimumWidth"/> and <see cref="MinimumHeight"/> simultaneously.</summary>
		public Vector2 MinimumSize
		{
			get => new Vector2( MinimumWidth, MinimumHeight );
			set
			{
				MinimumWidth = value.x;
				MinimumHeight = value.y;
			}
		}

		/// <summary>This widgets width should never be smaller than the given value.</summary>
		public float MinimumWidth { get; set; }

		/// <summary>This widgets height should never be smaller than the given value.</summary>
		public float MinimumHeight { get; set; }

		Vector2 _fixedSize;

		//Sentinel meaning no fixed size.
		internal const float UnboundedSize = (1 << 24) - 1;

		/// <summary>Sets the fixed height for this widget</summary>
		public float FixedHeight
		{
			get => _fixedSize.y;
			set
			{
				_fixedSize.y = value;

				if ( value >= UnboundedSize )
				{
					MinimumHeight = 0;
					MaximumHeight = FloatClamp;
					return;
				}

				MinimumHeight = value;
				MaximumHeight = value;
				Size = Size.WithY( value );
			}
		}

		/// <summary>Sets the fixed width for this widget</summary>
		public float FixedWidth
		{
			get => _fixedSize.x;
			set
			{
				_fixedSize.x = value;

				if ( value >= UnboundedSize )
				{
					MinimumWidth = 0;
					MaximumWidth = FloatClamp;
					return;
				}

				MinimumWidth = value;
				MaximumWidth = value;
				Size = Size.WithX( value );
			}
		}

		public Vector2 FixedSize
		{
			get => _fixedSize;

			set
			{
				//Through the two of them, so that the width and the height each get the same reading of what they were given.
				FixedWidth = value.x;
				FixedHeight = value.y;
			}
		}

		/// <summary>This widgets width should never be larger than the given value.</summary>
		public float MaximumWidth
		{
			get => MaximumSize.x;
			set => MaximumSize = MaximumSize.WithX( value );
		}

		/// <summary>This widgets height should never be larger than the given value.</summary>
		public float MaximumHeight
		{
			get => MaximumSize.y;
			set => MaximumSize = MaximumSize.WithY( value );
		}


		const float FloatClamp = 1000000;

		/// <summary>Sets <see cref="MaximumWidth"/> and <see cref="MaximumHeight"/> simultaneously.</summary>
		public Vector2 MaximumSize
		{
			get
			{
				var value = maximumSize;

				if ( value.x > FloatClamp ) value = value.WithX( FloatClamp );
				if ( value.y > FloatClamp ) value = value.WithY( FloatClamp );

				return value;
			}
			set
			{
				if ( value.x > FloatClamp ) value = value.WithX( FloatClamp );
				if ( value.y > FloatClamp ) value = value.WithY( FloatClamp );

				maximumSize = value;
				Size = Size;
			}
		}

		/// <summary>Position of this widget, relative to its parent if it has one.</summary>
		public Vector2 Position
		{
			get => position;
			set
			{
				if ( Position == value ) return;

				position = value;

				OnMoved();
				Moved?.Invoke();
				Update();
			}
		}

		/// <summary>Whether this widget is visible or not, in the tree. This will return false if a parent is hidden. You might want to set Hidden if you're looking to check local visible status on a widget.</summary>
		public bool Visible
		{
			get
			{
				return IsValid && visible && (parent?.Visible ?? true);
			}
			set
			{
				if ( !IsValid ) return;

				if ( visible == value ) return;

				visible = value;

				if ( parent is null && this is Window window )
				{
					if ( value ) window.Show();
					else if ( window.Shell is not null ) window.Shell.Shown = false;
				}

				UpdateGeometry();

				//The tick is what puts the "hidden" class on the panel in front of this widget,
				//and a surface that ticks on demand has to be told there is something to do.
				Surface?.RequestTick();
			}
		}

		/// <summary>Whether this widget is hidden. This differs from Visible because this will return the state for this particular widget, where as Visible returns false if a parent is hidden etc.</summary>
		public bool Hidden
		{
			get
			{
				return IsValid && !visible;
			}
			set
			{
				if ( !IsValid ) return;

				Visible = !value;
			}
		}

		/// <summary>Name of the widget, usually for debugging purposes.</summary>
		public string Name
		{
			get => widgetName;
			set
			{
				widgetName = value;
			}
		}

		public bool TranslucentBackground
		{
			get => HasFlag( Flag.WA_TranslucentBackground );
			set => SetFlag( Flag.WA_TranslucentBackground, value );
		}

		public bool NoSystemBackground
		{
			get => HasFlag( Flag.WA_NoSystemBackground );
			set => SetFlag( Flag.WA_NoSystemBackground, value );
		}

		public bool TransparentForMouseEvents
		{
			get => HasFlag( Flag.WA_TransparentForMouseEvents );
			set
			{
				if ( HasFlag( Flag.WA_TransparentForMouseEvents ) == value ) return;

				SetFlag( Flag.WA_TransparentForMouseEvents, value );

				//Read by the surface's tick and written onto the panel's PointerEvents there, so the tick has to happen.
				Surface?.RequestTick();
			}
		}

		public bool ShowWithoutActivating
		{
			get => HasFlag( Flag.WA_ShowWithoutActivating );
			set => SetFlag( Flag.WA_ShowWithoutActivating, value );
		}

		public bool MouseTracking
		{
			get => mouseTracking;
			set
			{
				//A panel hears the pointer move over it whether or not a button is down, so this is already how ours behave.
				mouseTracking = value;
			}
		}

		private bool mouseTracking;

		/// <summary>Accept drag and dropping shit on us</summary>
		public bool AcceptDrops
		{
			get => acceptDrops;
			set
			{
				//Dragging things between windows is the window system's, and ours is not wired to
				//it yet; the widget remembers that it wanted to be, and nothing is dropped on it.
				acceptDrops = value;
			}
		}

		private bool acceptDrops;

		public bool IsFramelessWindow
		{
			get => HasWindowFlag( WindowFlags.Window | WindowFlags.FramelessWindowHint );
			set
			{
				WindowFlags = value ? (WindowFlags.Window | WindowFlags.FramelessWindowHint) : WindowFlags.Widget;
			}
		}

		public bool IsTooltip
		{
			get => HasWindowFlag( WindowFlags.ToolTip );
			set
			{
				WindowFlags = value ? (WindowFlags.ToolTip | WindowFlags.FramelessWindowHint | WindowFlags.NoDropShadowWindowHint) : WindowFlags.Widget;
			}
		}

		public bool IsPopup
		{
			get => HasWindowFlag( WindowFlags.Popup );
			set
			{
				WindowFlags = value ? (WindowFlags.Popup | WindowFlags.FramelessWindowHint | WindowFlags.NoDropShadowWindowHint) : WindowFlags.Widget;
			}
		}

		public bool IsWindow
		{
			get => HasWindowFlag( WindowFlags.Window );
			set => SetWindowFlag( WindowFlags.Window, value );
		}

		public bool HasMaximizeButton
		{
			set
			{
				SetWindowFlag( WindowFlags.Customized, value );
				SetWindowFlag( WindowFlags.MaximizeButton, value );
			}

			get => !HasWindowFlag( WindowFlags.MaximizeButton );
		}

		/// <summary>Delete this widget when close is pressed</summary>
		public bool DeleteOnClose
		{
			get => HasFlag( Flag.DeleteOnClose );
			set => SetFlag( Flag.DeleteOnClose, value );
		}


		/// <summary>The scale this widget is using (multiplying Size by this value gives the actual native size)</summary>
		public float DpiScale => Surface is { IsValid: true } surface ? surface.ScaleToScreen : 1.0f;

		public void Focus( bool activateWindow = true )
		{
			//The panel takes the focus, because that is what the keyboard is handed to; the widget hears about it from there.
			Surface?.Focus();
		}

		/// <summary>Clear keyboard focus from this widget.</summary>
		public void Blur()
		{
			Surface?.Blur();
		}

		/// <summary>Whether this widget has keyboard focus.</summary>
		public bool IsFocused
		{
			get => (Surface?.HasFocus ?? false);
		}

		/// <summary>Whether the keyboard is in the window this widget is in.</summary>
		public bool IsActiveWindow
		{
			get
			{
				if ( !IsValid ) return false;

				for ( var widget = this; widget.IsValid(); widget = widget.Parent )
				{
					if ( widget is Window { IsOnScreen: true } window ) return window.HasKeyboard;
				}

				return Window.AnyHasKeyboard;
			}
		}

		/// <summary>Sets the focus mode for this widget. This determines both how it will get focus and whether it will receive keyboard input.</summary>
		public FocusMode FocusMode
		{
			get => focusMode;
			set
			{
				focusMode = value;

				//A panel that takes no focus is one the keyboard is never handed to, which is what the mode says.
				if ( Surface is not null ) Surface.AcceptsFocus = value != FocusMode.None;
			}
		}

		private FocusMode focusMode = FocusMode.None;

		/// <summary>Enables or disables the context menu on this widget.</summary>
		public bool ContextMenuEnabled { get; set; } = true;

		internal enum Flag
		{
			WA_Disabled = 0,
			WA_UnderMouse = 1,
			WA_MouseTracking = 2,
			WA_ContentsPropagated = 3, //## deprecated
			WA_OpaquePaintEvent = 4,

			WA_StaticContents = 5,
			WA_LaidOut = 7,
			WA_PaintOnScreen = 8,
			WA_NoSystemBackground = 9,
			WA_UpdatesDisabled = 10,
			WA_Mapped = 11,

			WA_InputMethodEnabled = 14,
			WA_WState_Visible = 15,
			WA_WState_Hidden = 16,

			WA_ForceDisabled = 32,
			WA_KeyCompression = 33,
			WA_PendingMoveEvent = 34,
			WA_PendingResizeEvent = 35,
			WA_SetPalette = 36,
			WA_SetFont = 37,
			WA_SetCursor = 38,
			WA_NoChildEventsFromChildren = 39,
			WA_WindowModified = 41,
			WA_Resized = 42,
			WA_Moved = 43,
			WA_PendingUpdate = 44,
			WA_InvalidSize = 45,

			WA_CustomWhatsThis = 47,
			WA_LayoutOnEntireRect = 48,
			WA_OutsideWSRange = 49,
			WA_GrabbedShortcut = 50,
			WA_TransparentForMouseEvents = 51,
			WA_PaintUnclipped = 52,
			WA_SetWindowIcon = 53,
			WA_NoMouseReplay = 54,
			DeleteOnClose = 55,
			WA_RightToLeft = 56,
			WA_SetLayoutDirection = 57,
			WA_NoChildEventsForParent = 58,
			WA_ForceUpdatesDisabled = 59,

			WA_WState_Created = 60,
			WA_WState_CompressKeys = 61,
			WA_WState_InPaintEvent = 62,
			WA_WState_Reparented = 63,
			WA_WState_ConfigPending = 64,
			WA_WState_Polished = 66,
			WA_WState_DND = 67, //## deprecated
			WA_WState_OwnSizePolicy = 68,
			WA_WState_ExplicitShowHide = 69,

			WA_ShowModal = 70, //## deprecated
			WA_MouseNoMask = 71,
			WA_GroupLeader = 72, //## deprecated
			WA_NoMousePropagation = 73, //## for now, might go away.
			WA_Hover = 74,
			WA_InputMethodTransparent = 75, //Don't reset IM when user clicks on this (for virtual keyboards on embedded)
			WA_QuitOnClose = 76,

			WA_KeyboardFocusChange = 77,

			WA_AcceptDrops = 78,
			WA_DropSiteRegistered = 79, //internal
			WA_ForceAcceptDrops = WA_DropSiteRegistered, //## deprecated

			WA_WindowPropagation = 80,

			WA_NoX11EventCompression = 81,
			WA_TintedBackground = 82,
			WA_X11OpenGLOverlay = 83,
			WA_AlwaysShowToolTips = 84,
			WA_MacOpaqueSizeGrip = 85,
			WA_SetStyle = 86,

			WA_SetLocale = 87,
			WA_MacShowFocusRect = 88,

			WA_MacNormalSize = 89,  //Mac only
			WA_MacSmallSize = 90,   //Mac only
			WA_MacMiniSize = 91,    //Mac only

			WA_LayoutUsesWidgetRect = 92,
			WA_StyledBackground = 93, //internal

			WA_CanHostQMdiSubWindowTitleBar = 95, //Internal

			WA_MacAlwaysShowToolWindow = 96, //Mac only

			WA_StyleSheet = 97, //internal

			WA_ShowWithoutActivating = 98,

			WA_X11BypassTransientForHint = 99,

			WA_NativeWindow = 100,
			WA_DontCreateNativeAncestors = 101,

			WA_MacVariableSize = 102,    //Mac only

			WA_DontShowOnScreen = 103,

			//window types from http://standards.freedesktop.org/wm-spec/
			WA_X11NetWmWindowTypeDesktop = 104,
			WA_X11NetWmWindowTypeDock = 105,
			WA_X11NetWmWindowTypeToolBar = 106,
			WA_X11NetWmWindowTypeMenu = 107,
			WA_X11NetWmWindowTypeUtility = 108,
			WA_X11NetWmWindowTypeSplash = 109,
			WA_X11NetWmWindowTypeDialog = 110,
			WA_X11NetWmWindowTypeDropDownMenu = 111,
			WA_X11NetWmWindowTypePopupMenu = 112,
			WA_X11NetWmWindowTypeToolTip = 113,
			WA_X11NetWmWindowTypeNotification = 114,
			WA_X11NetWmWindowTypeCombo = 115,
			WA_X11NetWmWindowTypeDND = 116,

			WA_SetWindowModality = 118,
			WA_WState_WindowOpacitySet = 119, //internal
			WA_TranslucentBackground = 120,

			WA_AcceptTouchEvents = 121,
			WA_WState_AcceptedTouchBeginEvent = 122,
			WA_TouchPadAcceptSingleTouchEvents = 123,

			WA_X11DoNotAcceptFocus = 126,
			WA_MacNoShadow = 127,

			WA_AlwaysStackOnTop = 128,

			WA_TabletTracking = 129,

			WA_ContentsMarginsRespectsSafeArea = 130,

			WA_StyleSheetTarget = 131,

			//Add new attributes before this line
			WA_AttributeCount
		}


		private readonly HashSet<Flag> flags = new();
		private WindowFlags windowFlags;

		internal void SetFlag( Flag f, bool b )
		{
			if ( b ) flags.Add( f );
			else flags.Remove( f );
		}

		internal bool HasFlag( Flag f ) => flags.Contains( f );

		public WindowFlags WindowFlags
		{
			get => windowFlags;
			set
			{
				if ( windowFlags == value ) return;

				windowFlags = value;
				WindowFlagsChanged?.Invoke();
			}
		}

		internal void SetWindowFlag( WindowFlags f, bool b ) => WindowFlags = b ? (windowFlags | f) : (windowFlags & ~f);

		//The flags changed. A window's own buttons listen, rather than asking every frame whether the window still has a maximise button.
		internal event System.Action WindowFlagsChanged;

		internal bool HasWindowFlag( WindowFlags f ) => windowFlags.Contains( f );

		/// <summary>Directly set CSS style sheet(s) for this widget. Same format as a .css file.</summary>
		public void SetStyles( string sheet )
		{
		}

		Rinten.FileWatch styleWatch;

		/// <summary>Set a file to load CSS for this widget from.</summary>
		public void SetStylesheetFile( string filename )
		{
			styleWatch?.Dispose();

			styleWatch = Editor.FileSystem.Mounted.Watch( filename );
			styleWatch.OnChanges += ( x ) => LoadStylesheetFile( filename );

			LoadStylesheetFile( filename );
		}

		internal void LoadStylesheetFile( string filename )
		{
			var txt = Editor.FileSystem.Mounted.ReadAllText( filename );
			SetStyles( txt );
		}

		/// <summary>Child widgets of this widget.</summary>
		public IEnumerable<Widget> Children
		{
			get
			{
				return childWidgets.Where( x => x.IsValid ).ToArray();
			}
		}

		/// <summary>Destroys all child widgets of this widget.</summary>
		public void DestroyChildren()
		{
			foreach ( var child in Children )
			{
				child.Destroy();
			}
		}

		internal override void OnDestroyingLater()
		{
			Visible = false;

			//The panel delete is recursive; the widgets are not, unless we do it here.
			DestroyChildren();
			TakeDownSurface();
		}


		CursorShape _cursor = CursorShape.None;

		/// <summary>Cursor override for this widget.</summary>
		public virtual CursorShape Cursor
		{
			get => _cursor;
			set
			{
				_cursor = value;
				_pixmapCursor = null;

				//The panel says what the pointer looks like over it, and the window hands that to the platform.
				if ( Surface is not null ) Surface.Style.Cursor = CursorName( _cursor );
			}
		}

		private Pixmap _pixmapCursor;

		/// <summary>Custom cursor override for this widget. Will override <see cref="Cursor"/> with <see cref="CursorShape.CustomCursor"/>.</summary>
		public virtual Pixmap PixmapCursor
		{
			get => _pixmapCursor;
			set
			{
				//A cursor made of a picture is the window system's to draw, and ours is not asked for one yet.
				_pixmapCursor = value;
				_cursor = value is null ? CursorShape.None : CursorShape.CustomCursor;
			}
		}

		//What the pointer looks like over a panel, in the names a stylesheet uses - which are the ones the window system knows.
		private static string CursorName( CursorShape shape ) => shape switch
		{
			CursorShape.None => null,
			CursorShape.Finger => "pointer",
			CursorShape.IBeam => "text",
			CursorShape.SizeH => "ew-resize",
			CursorShape.SizeV => "ns-resize",
			CursorShape.SizeAll => "move",
			CursorShape.Wait => "wait",
			CursorShape.Cross => "crosshair",
			CursorShape.Forbidden => "not-allowed",
			_ => "default",
		};

		/// <summary>Tell this widget that shit changed and it needs to redraw</summary>
		public virtual void Update()
		{
			if ( !IsValid ) return;

			Surface?.Invalidate();
		}

		//Repaint immediately
		internal void Repaint()
		{
			if ( !IsValid ) return;

			Surface?.Invalidate();
		}


		/// <summary>This widget's top-left in its window, in interface units.</summary>
		public Vector2 ScreenPosition => ToScreen( default );

		/// <summary>Rect of the part drawn at a local point, for widgets with painted rows; null if none</summary>
		public virtual Rect? PartAt( Vector2 local ) => null;

		public Vector2 ToScreen( Vector2 p )
		{
			if ( !IsValid ) return default;

			if ( Surface is { IsValid: true } surface )
			{
				return surface.GetScreenPixelRect().Position * surface.ScaleFromScreen + p;
			}

			for ( var widget = this; widget is not null; widget = widget.Parent )
				p += widget.Position;

			//Widgets are laid out in the body under the caption; the pointer is counted from the top of the window, caption included.
			if ( GetWindow() is Window window )
				p.y += window.Shell?.CaptionHeight ?? 0;

			return p;
		}

		/// <summary>A point in its window's interface units, in this widget's own.</summary>
		public Vector2 FromScreen( Vector2 p ) => p - ToScreen( default );

		/// <summary>Pretends a key was pressed on this widget.</summary>
		public void PostKeyEvent( KeyCode key )
		{
			PanelKeyPress( new KeyEvent( key, string.Empty, KeyboardModifiers.None ) );
		}

		Widget _focusProxy;
		public Widget FocusProxy
		{
			get => _focusProxy;
			set => _focusProxy = value;
		}

		public bool IsUnderMouse => (Surface?.HasHovered ?? false);

		public void SetSizeMode( SizeMode horizontal, SizeMode vertical )
		{
			horizontalSizeMode = horizontal;
			verticalSizeMode = vertical;

			UpdateGeometry();
		}

		public SizeMode HorizontalSizeMode
		{
			get => horizontalSizeMode;
			set
			{
				horizontalSizeMode = value;
				UpdateGeometry();
			}
		}

		public SizeMode VerticalSizeMode
		{
			get => verticalSizeMode;
			set
			{
				verticalSizeMode = value;
				UpdateGeometry();
			}
		}

		//How willing a widget is to grow or shrink, which our own layout reads when it shares out what is left over.

		private SizeMode horizontalSizeMode = SizeMode.Default;
		private SizeMode verticalSizeMode = SizeMode.Default;

		/// <summary>Serialize position and size of this widget to a string, which can then be passed to <see cref="RestoreGeometry"/>.</summary>
		public string SaveGeometry()
		{
			return $"{Position.x} {Position.y} {Size.x} {Size.y}";
		}

		/// <summary>Restore position and size previously stored via <see cref="SaveGeometry"/>. Returns false if the state is empty or no longer valid, e.g. after monitor or DPI changes.</summary>
		public bool RestoreGeometry( string state )
		{
			if ( string.IsNullOrWhiteSpace( state ) )
				return false;

			//Four numbers, as SaveGeometry wrote them.
			var parts = state.Split( ' ' );

			if ( parts.Length != 4 ) return false;

			if ( !float.TryParse( parts[0], out var x ) ) return false;
			if ( !float.TryParse( parts[1], out var y ) ) return false;
			if ( !float.TryParse( parts[2], out var width ) ) return false;
			if ( !float.TryParse( parts[3], out var height ) ) return false;

			Position = new Vector2( x, y );
			Size = new Vector2( width, height );

			return true;
		}

		/// <summary>If set, this text will be displayed after a certain delay of hovering this widget with the mouse cursor.</summary>
		public virtual string ToolTip
		{
			get => toolTip;
			set
			{
				toolTip = value;
			}
		}

		private string toolTip;

		/// <summary>If set, hovering over this widget will set the text of a <see cref="StatusBar"/> of the window the widget belongs to.</summary>
		public string StatusTip
		{
			get => statusTip;
			set
			{
				statusTip = value;
			}
		}

		private string statusTip;

		public event Action<Widget> OnChildValuesChanged;

		protected virtual void Signal( WidgetSignal signal )
		{
			if ( signal.Type == "valuechanged" )
			{
				ChildValuesChanged( signal.SourceWidget );
			}

			if ( !signal.Propagate )
				return;

			Parent?.Signal( signal );
		}

		public virtual void ChildValuesChanged( Widget source )
		{
			OnChildValuesChanged?.Invoke( source );
		}

		public void MakeSignal( string name )
		{
			Signal( new WidgetSignal { SourceWidget = this, Type = name, Propagate = true } );
		}

		//When a value on this widget changed due to user input (ie, checking a box, editing a form) this is called, which sends a signal up the parent widgets.
		protected void SignalValuesChanged()
		{
			BindSystem?.Flush();
			MakeSignal( "valuechanged" );
		}

		/// <summary>Adjusts the size of the widget to fit its contents.</summary>
		public void AdjustSize()
		{
			Size = WantedSize();

			if ( Width < MinimumWidth ) Width = MinimumWidth;
			if ( Height < MinimumHeight ) Height = MinimumHeight;

			//Apply the maximum too, so FixedWidth caps size and popups don't grow off screen
			if ( Width > MaximumWidth ) Width = MaximumWidth;
			if ( Height > MaximumHeight ) Height = MaximumHeight;
		}

		/// <summary>Returns the geometry of the screen this widget is currently on.</summary>
		public Rect ScreenGeometry
		{
			get
			{
				var (x, y, width, height) = Rinten.CoreWindow.UsableDesktop();

				//The game's screen is still the answer where there is no window system to ask - a headless run, a test.
				return width > 1 && height > 1
					? new Rect( x, y, width, height )
					: new Rect( 0, Screen.Size );
			}
		}

		/// <summary>Constrain this widget to the screen it's currently on.</summary>
		public void ConstrainToScreen()
		{
			AdjustSize();

			ConstrainTo( ScreenGeometry );
		}

		/// <summary>Reposition this widget to ensure it is within the given rectangle.</summary>
		public void ConstrainTo( Rect parentRect )
		{
			AdjustSize();

			var p = Position;
			var s = Size;
			var pr = new Rect( p, s );

			if ( pr.Top < parentRect.Top ) p = p.WithY( parentRect.Top );
			if ( pr.Left < parentRect.Left ) p = p.WithX( parentRect.Left );
			if ( pr.Bottom > parentRect.Bottom ) p = p.WithY( parentRect.Bottom - s.y );
			if ( pr.Right > parentRect.Right ) p = p.WithX( parentRect.Right - s.x );

			Position = p;
		}

		public virtual void SetWindowIcon( string name )
		{
			return;
		}

		public virtual void SetWindowIcon( Pixmap icon )
		{
			return;
		}

		public virtual string WindowTitle
		{
			get => windowTitle;
			set
			{
				windowTitle = value;
			}
		}

		private string windowTitle;

		/// <summary>Make this widget visible.</summary>
		public virtual void Show()
		{
			Visible = true;
		}

		/// <summary>Make this widget not visible.</summary>
		public virtual void Hide()
		{
			Visible = false;
		}

		/// <summary>If a window - will close</summary>
		public virtual void Close()
		{
			if ( !IsValid ) return;

			OnClosed();
			Destroy();
		}

		public void MakeMinimized()
		{
			(this as Window)?.Shell?.Minimize();
		}

		public bool IsMinimized => (this as Window)?.Shell?.IsMinimized ?? false;

		public void MakeMaximized()
		{
			(this as Window)?.Shell?.Maximize();
		}

		public bool IsMaximized => (this as Window)?.Shell?.IsMaximized ?? false;

		//Centres this window on screen, or over the main editor window if this isn't it
		internal void CenterWindow()
		{
			( this as Window )?.CentreNow();
		}

		/// <summary>Set the maximized window state without showing the window, unlike <see cref="MakeMaximized"/>.</summary>
		public void SetMaximized( bool maximized )
		{
			if ( maximized ) MakeMaximized();
			else MakeWindowed();
		}

		public void MakeWindowed()
		{
			(this as Window)?.Shell?.Restore();
		}

		/// <summary>Set this window to be modal. This means it will appear on top of everything and block input to everything else. Nothing blocks the rest of the editor yet: a window of ours is a window like any other, so this is remembered and no more.</summary>
		public void SetModal( bool on, bool application = false )
		{
			modal = on;
		}

		private bool modal;

		/// <summary>Returns true if this is a modal window. This means it will appear on top of everything and block input to everything else.</summary>
		public bool IsModal() => modal;


		/// <summary>Calling this will set the WS_EX_NOACTIVATE flag on the window internally, which will stop it taking focus away from other windows.</summary>
		public void DisableWindowActivation()
		{
			return;
		}


		public void SetEffectOpacity( float f )
		{
			//Everything under the widget fades with it, which is what the effect did.
			if ( Surface is not null ) Surface.Style.Opacity = f;
		}

		public float WindowOpacity
		{
			get => windowOpacity;
			set
			{
				windowOpacity = value;

				//A window of ours is as solid as the window system made it; what fades is everything drawn inside it.
				if ( Surface is not null ) Surface.Style.Opacity = value;
			}
		}

		private float windowOpacity = 1.0f;

		int _contentHash = -1234;
		RealTimeSince timeSinceUpdate = 10;

		/// <summary>Call every frame/tick to redraw this Widget on content change</summary>
		public bool SetContentHash( int hash, float secondsDebounce = 0.1f )
		{
			if ( timeSinceUpdate < secondsDebounce )
				return false;

			timeSinceUpdate = 0;

			if ( _contentHash == hash ) return false;

			_contentHash = hash;
			Update();
			return true;
		}

		/// <summary>Call every frame/tick to redraw this Widget on content change</summary>
		public bool SetContentHash( Func<int> getHash, float secondsDebounce = 0.1f )
		{
			if ( timeSinceUpdate < secondsDebounce )
				return false;

			timeSinceUpdate = 0;

			var hash = getHash();

			if ( _contentHash == hash ) return false;

			_contentHash = hash;
			Update();
			return true;
		}

		/// <summary>If true, Update will call</summary>
		public bool UpdatesEnabled { get; set; } = true;

		/// <summary>Align this widget to its parents edge, with an offset.</summary>
		public virtual void AlignToParent( TextFlag alignment, Vector2 offset = default )
		{
			AdjustSize();

			if ( Parent is null )
				return;

			var rect = Parent.LocalRect;
			var size = Size + offset * 2;
			var childRect = rect.Align( size, alignment );
			Position = childRect.Position + offset;
		}

		/// <summary>Tell everything that the geometry of this has changed</summary>
		public void UpdateGeometry()
		{
			//Whatever is arranging this widget gets to do it again on the next pass, and the panel in front of it follows on its tick.
			MarkNeedsArrange();
			Update();
		}

		/// <summary>Get the top level window widget</summary>
		public Widget GetWindow()
		{
			for ( var widget = this; widget.IsValid(); widget = widget.Parent )
			{
				if ( widget is Window window ) return window;
			}

			return null;
		}
	}


	public class WidgetSignal
	{
		public string Type;
		public Widget SourceWidget;
		public bool Propagate;
	}

	public enum FocusMode
	{
		/// <summary>Do not accept focus.</summary>
		None = 0,

		/// <summary>Accept focus by tabbing.</summary>
		Tab = 0x1,

		/// <summary>Accept focus by being clicked on.</summary>
		Click = 0x2,

		/// <summary>Accept focus by clicking or tabbing.</summary>
		TabOrClick = Tab | Click | 0x8,

		/// <summary>Accept focus when using the mouse wheel too.</summary>
		TabOrClickOrWheel = TabOrClick | 0x4
	}

	internal enum WindowModality
	{
		None,
		Window,
		Application
	}

	/// <summary>Hides the widget while rebuilding; the outermost dispose arranges and shows.</summary>
	public class SuspendUpdates : System.IDisposable
	{
		static int depth;
		static Widget root;
		static long scopeStartTicks;

		Widget Widget;
		bool oldState;

		public static SuspendUpdates For( Widget widget )
		{
			if ( widget is null )
				return null;

			if ( widget.Hidden )
				return null;

			return new SuspendUpdates( widget );
		}

		internal SuspendUpdates( Widget widget )
		{
			Widget = widget;

			oldState = Widget.Hidden;
			Widget.Hidden = true;

			if ( depth++ == 0 )
			{
				root = Widget;
				scopeStartTicks = Stopwatch.GetTimestamp();
			}
		}

		public void Dispose()
		{
			Widget.Hidden = oldState;

			var isOutermost = --depth <= 0;
			if ( !isOutermost )
				return;

			var arrangeRoot = root;
			root = null;
			depth = 0;

			if ( !arrangeRoot.IsValid() )
				return;

			var finalize = Stopwatch.StartNew();

			double arrangeMs = 0;
			int arrangePasses = 0;
			bool arrangeStable = true;
			if ( EditorPreferences.UiSuspendArrangeEnabled )
			{
				( arrangePasses, arrangeStable ) = arrangeRoot.SettleFlexSubtree();

				arrangeMs = finalize.Elapsed.TotalMilliseconds;
			}

			finalize.Restart();

			double paintMs = 0;
			if ( arrangeRoot.Visible && EditorPreferences.UiSuspendBurstPaintEnabled )
			{
				try
				{
					WidgetSurface.BeginBurstPaint();
					arrangeRoot.PaintSubtree();
				}
				finally
				{
					WidgetSurface.EndBurstPaint();
				}

				paintMs = finalize.Elapsed.TotalMilliseconds;
				finalize.Restart();
			}

			arrangeRoot.UpdateGeometry();
			var geometryMs = finalize.Elapsed.TotalMilliseconds;

			if ( EditorPreferences.UiSuspendProfilerLog )
			{
				var totalMs = ( Stopwatch.GetTimestamp() - scopeStartTicks ) * 1000.0 / Stopwatch.Frequency;
				var widgets = CountSubtree( arrangeRoot );
				var label = string.IsNullOrEmpty( arrangeRoot.Name ) ? arrangeRoot.GetType().Name : $"{arrangeRoot.GetType().Name} ({arrangeRoot.Name})";

				var arrangePassesNote = arrangePasses > 0
					? !arrangeStable ? $" x{arrangePasses} limit" : $" x{arrangePasses}"
					: "";

				Log.Info(
					$"SuspendUpdates {label}: {totalMs:F1} ms total, {widgets} widgets (arrange {arrangeMs:F1}{arrangePassesNote}, paint {paintMs:F1}, geometry {geometryMs:F1})" );
			}
		}

		static int CountSubtree( Widget widget )
		{
			var count = 1;

			foreach ( var child in widget.childWidgets )
			{
				if ( child.IsValid() )
					count += CountSubtree( child );
			}

			return count;
		}
	}

}
