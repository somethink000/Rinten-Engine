using System;

namespace Editor;

/// <summary>A surface whose keys are its own even when the keyboard is not.</summary>
public interface IShortcutFallback
{
}

//With the focus in here, a key nothing in here takes goes on to the IShortcutFallback surfaces as though nothing were focused.
/// <summary>A surface that holds the focus without holding the keys - a tool's settings docked beside the view they belong to.</summary>
public interface IShortcutPassthrough
{
}

public static class EditorShortcuts
{
	public static List<Entry> Entries = new();
	static Dictionary<Type, List<object>> Targets = new();

	/// <summary>Whether a key may do what it is bound to right now.</summary>
	public static bool AllowShortcuts
	{
		get => ShortcutsLive && _timeSinceInputsBlocked >= 0.05f;
		set => _timeSinceInputsBlocked = value ? 1f : 0f;
	}

	static RealTimeSince _timeSinceInputsBlocked = 0f;

	//The half of AllowShortcuts that the camera can't switch off: a project, loaded, and no modal over it.
	static bool ShortcutsLive => Project.Current is not null && !StartupLoadProject.IsLoading && !Window.ModalBlocksInput;

	/// <summary>Set this to true in a shortcut method to indicate the shortcut should not be consumed, allowing other shortcuts with the same key binding to be tried.</summary>
	public static bool PassShortcut { get; set; }

	/// <summary>Log what each key press did and why a shortcut did or did not fire.</summary>
	[ConVar( "shortcut_debug" )]
	public static bool Debug { get; set; }

	//One line of that trace.
	internal static void Say( string what )
	{
		if ( !Debug ) return;

		Log.Info( $"[shortcut] {what}" );
	}

	internal static RealTimeSince _timeSinceGlobalShortcut = 0f;

	[Event( "editor.created" )]
	static void EditorCreated( EditorMainWindow _ )
	{
		//With no project there are no shortcuts to press - see AllowShortcuts - and opening one registers them:
		//EditorMainWindow.OnProjectOpened. Done here it was a fifth of the frame the editor's docks appear in, for nothing, under the launcher.
		if ( Project.Current is null ) return;

		RegisterShortcuts();
	}

	[EditorEvent.Hotload]
	[Event( "keybinds.update" )]
	internal static void RegisterShortcuts()
	{
		Entries.Clear();

		var methods = EditorTypeLibrary.GetMethodsWithAttribute<ShortcutAttribute>( false );

		foreach ( var method in methods )
		{
			var group = "";
			if ( method.Method.HasAttribute<GroupAttribute>() )
			{
				group = method.Method.GetCustomAttribute<GroupAttribute>().Value;
			}

			//Make sure static methods on things that are not a surface are treated as window shortcuts
			var attributeTarget = method.Attribute.TargetOverride ?? method.Method.TypeDescription.TargetType;
			if ( !IsSurfaceType( attributeTarget ) && method.Attribute.Type == ShortcutType.Widget )
			{
				method.Attribute.Type = ShortcutType.Window;
			}

			var entry = new Entry( method.Method, method.Attribute, group );
			Entries.Add( entry );
		}
	}

	//Whether a shortcut declared here can be gated on something being visible and focused.
	static bool IsSurfaceType( Type type )
	{
		return type.IsSubclassOf( typeof( Widget ) ) || type.IsSubclassOf( typeof( Rinten.UI.Panel ) );
	}

	//A surface's ancestors, itself first: panels up to their host widget, then widget parents
	static IEnumerable<object> Ancestry( object target )
	{
		Widget widget = target as Widget;

		if ( target is Rinten.UI.Panel panel )
		{
			for ( var current = panel; current is not null; current = current.Parent )
			{
				yield return current;

				if ( current is WidgetSurface surface )
				{
					widget = surface.Widget;
					break;
				}
			}
		}

		for ( var current = widget; current.IsValid(); current = current.Parent )
		{
			yield return current;
		}
	}

	internal static void Register( object obj )
	{
		var typeKey = obj.GetType();
		if ( !Targets.ContainsKey( typeKey ) )
			Targets[typeKey] = new List<object>();

		Targets[typeKey].Add( obj );
	}

	//Everything of this type that is still alive. Destroyed widgets keep their place in the list where nothing
	//took them out of it - see EditorObject.Destroy, which does now, and hotload, which does not go through it.
	static bool Alive( object obj ) => obj is not IValid valid || valid.IsValid;

	internal static void Unregister( object obj )
	{
		var typeKey = obj.GetType();
		if ( !Targets.ContainsKey( typeKey ) )
			return;

		Targets[typeKey].Remove( obj );
	}

	//Whether the keyboard belongs to a text field (widget or focused panel chain) rather than the window
	internal static bool Typing( Widget focusedWidget, Rinten.UI.Panel focusedPanel )
	{
		if ( focusedWidget is LineEdit or TextEdit ) return true;
		if ( focusedWidget is { WantsRawKeys: true } ) return true;

		for ( var panel = focusedPanel; panel is not null; panel = panel.Parent )
		{
			if ( panel is IWantsRawKeys { WantsRawKeys: true } ) return true;
			if ( panel.AcceptsImeInput ) return true;
		}

		return false;
	}

	//Whether something in the focus chain is catching a key rather than being typed into.
	internal static bool Catching( Rinten.UI.Panel focusedPanel )
	{
		for ( var panel = focusedPanel; panel is not null; panel = panel.Parent )
		{
			if ( panel is IWantsRawKeys { WantsRawKeys: true, WantsModifiedKeys: true } ) return true;
		}

		return false;
	}

	//Whether this combination is the window's to act on, or belongs to whatever is being typed into. Ctrl and Alt combinations stay the window's while
	//somebody writes - they are not letters - except the four a field uses itself. Shift is not one of these, because shift is how capitals are typed.
	internal static bool Suppressed( Widget focusedWidget, Rinten.UI.Panel focusedPanel, string keys )
	{
		//A key being caught rather than typed takes the whole keyboard, combinations included.
		if ( Catching( focusedPanel ) ) return true;

		if ( !Typing( focusedWidget, focusedPanel ) ) return false;

		var modified = keys.Split( '+' ).Any( x => x == "CTRL" || x == "ALT" );

		if ( !modified ) return true;

		return keys is "CTRL+A" or "CTRL+C" or "CTRL+V" or "CTRL+X";
	}

	//The panel with the keyboard focus, where there is a panel system to ask.
	private static Rinten.UI.Panel FocusedPanel()
	{
		return Rinten.Engine.GlobalContext.Current?.UISystem is null ? null : Rinten.UI.InputFocus.Current;
	}

	//Is keys a function key shortcut (F1-F24) with optional modifiers (CTRL, ALT, SHIFT)?
	internal static bool IsFunctionKeyShortcut( string keys )
	{
		if ( string.IsNullOrEmpty( keys ) ) return false;

		var split = keys.Split( "+", StringSplitOptions.RemoveEmptyEntries );

		var anyFunctionKeys = false;

		foreach ( var key in split )
		{
			//Special case: keys 0-9 will get parsed in Enum.TryParse by numeric value instead of name

			if ( char.IsDigit( key[0] ) || key[0] is '-' or '+' ) return false;

			//We need at least one Fn key

			if ( Enum.TryParse<KeyCode>( key, ignoreCase: true, out var keyCode ) && keyCode is >= KeyCode.F1 and <= KeyCode.F24 )
			{
				anyFunctionKeys = true;
				continue;
			}

			//Modifiers are allowed

			if ( !Enum.TryParse<KeyboardModifiers>( key, ignoreCase: true, out var mod ) || mod == KeyboardModifiers.None )
			{
				return false;
			}
		}

		return anyFunctionKeys;
	}

	internal static bool Invoke( string keys, bool force = false )
	{
		//However it was written down - the entries hold theirs the same way.
		keys = Canonical( keys );

		if ( Suppressed( Application.FocusWidget, FocusedPanel(), keys ) )
		{
			if ( Debug )
				Say( $"'{keys}' went to what is being typed into - "
					+ $"widget {Application.FocusWidget?.GetType().Name ?? "none"}, "
					+ $"panel {FocusedPanel()?.GetType().Name ?? "none"}" );

			return false;
		}

		//Collect only the entries on this combination; sorting the whole list per keystroke was too slow
		List<Entry> matched = null;

		foreach ( var entry in Entries )
		{
			if ( entry.Keys != keys ) continue;

			(matched ??= new List<Entry>()).Add( entry );
		}

		if ( matched is null )
		{
			if ( Debug ) Say( $"'{keys}' - nothing is registered on it" );

			return false;
		}

		if ( Debug ) Say( $"'{keys}' - {matched.Count} shortcut(s) on it" );

		//widgets first, then work up to app-level
		bool hasInvoked = false;
		var groups = matched.OrderBy( x => x.Attribute.Type ).GroupBy( x => x.TargetKey );
		foreach ( var group in groups )
		{
			foreach ( var entry in group )
			{
				//Held is a question anything may ask later, whether or not this is the one the key does something in.
				entry.IsDown = true;

				//First shortcut to take the key wins (use PassShortcut to pass it on); Fn keys always allowed
				if ( !AllowShortcuts && !(ShortcutsLive && IsFunctionKeyShortcut( keys )) )
				{
					if ( Debug ) Say( $"'{keys}' - shortcuts are switched off" );
					continue;
				}

				if ( !hasInvoked && entry.Invoke( force ) )
				{
					hasInvoked = true;
				}
			}
		}

		//Nothing that holds the keyboard took it. The fallback surfaces get it next - see IShortcutFallback.
		if ( !hasInvoked && AllowShortcuts )
		{
			foreach ( var entry in matched )
			{
				if ( !entry.InvokeUnfocused() ) continue;

				hasInvoked = true;
				break;
			}
		}

		return hasInvoked;
	}

	//Whether the keyboard is held by nothing that could want a key: nothing is
	//focused, the focus is in a IShortcutPassthrough, or only a window itself has it.
	internal static bool KeyboardUnclaimed()
	{
		var panel = FocusedPanel();
		var widget = Application.FocusWidget;

		var panelLive = panel is not null && panel.IsValid;
		var widgetLive = widget.IsValid();

		if ( !panelLive && !widgetLive ) return true;

		foreach ( var link in panelLive ? Ancestry( panel ) : Ancestry( widget ) )
		{
			if ( link is IShortcutPassthrough ) return true;
		}

		//A window's own surface, with nothing inside it focused - what a click on the window's chrome leaves behind.
		if ( panelLive ) return panel is WidgetSurface surface && surface.Widget.IsValid() && !surface.Widget.Parent.IsValid();

		return !widget.Parent.IsValid();
	}

	//A key going down or coming up, as the button system names it.
	internal static void Typed( NativeEngine.ButtonCode button, bool down, bool repeat, KeyboardModifiers modifiers )
	{
		var key = KeyName( button );

		if ( string.IsNullOrEmpty( key ) ) return;

		if ( down ) Press( key );
		else Release( key );

		//Only on the way down, and never on the repeats a held key sends: a shortcut happens once, where holding the key is a separate question.
		if ( down && !repeat ) Invoke( Combination( key, modifiers ) );
	}

	//A function key pressed while the game has the keyboard, which is the one way the shortcuts hear anything then.
	internal static void FunctionKey( NativeEngine.ButtonCode button, KeyboardModifiers modifiers )
	{
		var key = KeyName( button );

		if ( string.IsNullOrEmpty( key ) ) return;

		Invoke( Combination( key, modifiers ) );
	}

	//The shortcut system's name for a button.
	private static string KeyName( NativeEngine.ButtonCode button )
	{
		var name = Rinten.EngineInput.CodeToString( button );

		return string.IsNullOrEmpty( name ) ? null : name.ToUpperInvariant();
	}

	//Normalises key names via the button system so DEL and DELETE etc. compare equal
	internal static string Canonical( string keys )
	{
		if ( string.IsNullOrWhiteSpace( keys ) ) return string.Empty;

		var parts = keys.Trim().ToUpperInvariant().Split( '+', StringSplitOptions.RemoveEmptyEntries );

		for ( var i = 0; i < parts.Length; i++ )
		{
			var code = Rinten.EngineInput.StringToButtonCode( parts[i] );

			//A name nothing answers to is left as it was written: it is either a key we have no button for
			//or a mistake, and both are better left readable in the shortcut list than turned into nothing.
			if ( code == NativeEngine.ButtonCode.BUTTON_CODE_INVALID ) continue;

			var name = Rinten.EngineInput.CodeToString( code );

			if ( !string.IsNullOrEmpty( name ) ) parts[i] = name.ToUpperInvariant();
		}

		return string.Join( '+', parts );
	}

	//The keys as a shortcut is written down: what was held, then the key itself, in the order the editor declares its own shortcuts in.
	private static string Combination( string key, KeyboardModifiers modifiers )
	{
		//A modifier on its own is the whole of it, which is how SHIFT is a shortcut of its own for moving the camera quickly.
		if ( key is "SHIFT" or "CTRL" or "ALT" ) return key;

		var held = new List<string>();

		if ( modifiers.Contains( KeyboardModifiers.Ctrl ) ) held.Add( "CTRL" );
		if ( modifiers.Contains( KeyboardModifiers.Alt ) ) held.Add( "ALT" );
		if ( modifiers.Contains( KeyboardModifiers.Shift ) ) held.Add( "SHIFT" );

		held.Add( key );

		return string.Join( '+', held );
	}

	internal static void Press( string key )
	{
		foreach ( var entry in Entries )
		{
			if ( entry.FinalKey == key ) entry.IsDown = true;
		}
	}

	internal static void Release( string key )
	{
		foreach ( var entry in Entries )
		{
			if ( entry.FinalKey == key ) entry.IsDown = false;
		}
	}

	public static void ReleaseAll()
	{
		foreach ( var entry in Entries )
		{
			entry.IsDown = false;
		}
	}

	//Try to invoke any shortcuts bound to mouse wheel up/down. Returns true if a shortcut consumed the wheel event.
	internal static bool InvokeWheel( int delta, KeyboardModifiers modifiers )
	{
		if ( delta == 0 ) return false;

		var wheelKey = delta > 0 ? "MWHEELUP" : "MWHEELDOWN";

		var modifiedKey = wheelKey;
		if ( modifiers.HasFlag( KeyboardModifiers.Shift ) ) modifiedKey = "SHIFT+" + modifiedKey;
		if ( modifiers.HasFlag( KeyboardModifiers.Alt ) ) modifiedKey = "ALT+" + modifiedKey;
		if ( modifiers.HasFlag( KeyboardModifiers.Ctrl ) ) modifiedKey = "CTRL+" + modifiedKey;

		if ( modifiers != KeyboardModifiers.None && Invoke( modifiedKey ) )
			return true;

		if ( Invoke( wheelKey ) )
			return true;

		return false;
	}

	//A loop, not a lambda over identifier: IsDown is asked every frame
	private static Entry Find( string identifier )
	{
		foreach ( var entry in Entries )
		{
			if ( entry.Identifier == identifier ) return entry;
		}

		return null;
	}

	/// <summary>Returns the keybind for a given identifier</summary>
	public static string GetKeys( string identifier )
	{
		var entry = Find( identifier );
		if ( entry != null )
			return entry.Keys;

		return "";
	}

	/// <summary>Returns the pretty key hint for a given identifier</summary>
	public static string GetDisplayKeys( string identifier )
	{
		var entry = Find( identifier );
		if ( entry != null )
			return entry.DisplayKeys;

		return "";
	}

	/// <summary>Returns the default keybind for a given identifier</summary>
	public static string GetDefaultKeys( string identifier )
	{
		var entry = Find( identifier );
		if ( entry != null )
			return entry.Attribute.Keys;

		return "";

	}

	/// <summary>Returns whether a given shortcut is currently being held down</summary>
	public static bool IsDown( string identifier )
	{
		var entry = Find( identifier );
		if ( entry is null ) return false;
		if ( entry.IsDown ) return true;

		//Event tracking misses a key that was already held when fly-mode started (ReleaseAll), and
		//a key that never reached a focused panel. The window system still knows whether it is down.
		return HardwareHeld( entry.Keys );
	}

	//Whether the last key of a bind is held, asked of the keyboard rather than of the shortcut events.
	private static bool HardwareHeld( string keys )
	{
		if ( string.IsNullOrEmpty( keys ) ) return false;

		var key = keys.Split( '+' ).LastOrDefault();
		if ( string.IsNullOrEmpty( key ) ) return false;

		if ( key == "SHIFT" ) return Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Shift );
		if ( key == "CTRL" ) return Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl );
		if ( key == "ALT" ) return Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Alt );

		var code = EditorKeys.FromName( key.ToLowerInvariant() );
		return code != 0 && Application.IsKeyDown( code );
	}

	public class Entry
	{
		public string Identifier { get; set; }
		public string Name { get; set; }
		public string Group { get; set; }
		internal Type TypeKey { get; set; }
		internal Type TargetKey { get; set; }

		public bool IsDown { get; internal set; }

		public string Keys
		{
			get => _keys;
			set
			{
				_keys = Canonical( value );
				FinalKey = _keys.Split( '+' ).LastOrDefault() ?? string.Empty;
				UpdateDisplayKeys();
				if ( Canonical( Attribute.Keys ) == _keys )
					EditorPreferences.ShortcutOverrides.Remove( Identifier );
				else
				{
					var overrides = EditorPreferences.ShortcutOverrides;
					overrides[Identifier] = value;
					EditorPreferences.ShortcutOverrides = overrides;
				}
			}
		}
		string _keys;

		//The main key without modifiers (K of CTRL+SHIFT+K), cached since every key event checks it
		internal string FinalKey { get; private set; } = string.Empty;

		public string DisplayKeys { get; private set; }

		MethodDescription MethodDesc;
		public ShortcutAttribute Attribute { get; init; }

		public Entry( MethodDescription desc, ShortcutAttribute attribute, string group = "" )
		{
			Identifier = attribute.Identifier;
			MethodDesc = desc;
			Attribute = attribute;
			Group = group;
			TargetKey = attribute.TargetOverride ?? desc.TypeDescription.TargetType;
			TypeKey = desc.TypeDescription.TargetType;
			GetNameFromIdent( Identifier );

			ResetKeys();
		}

		public bool Invoke( bool force = false )
		{
			var type = force ? ShortcutType.Application : Attribute.Type;

			//Surfaces of the target type gate whether this shortcut is currently accessible
			IEnumerable<object> gates;
			if ( MethodDesc.IsStatic && TargetKey == TypeKey )
			{
				gates = [OwnLayoutGate()];
				if ( !force ) type = ShortcutType.Window;
			}
			else
			{
				//Prefer the focused gate so forced invokes (e.g. menu options) hit the right scene view
				gates = InstancesOf( TargetKey ).OrderByDescending( ContainsFocus );
			}

			var looked = 0;

			foreach ( var gate in gates )
			{
				looked++;

				if ( !IsAccessible( gate, type ) )
				{
					if ( Debug ) Say( $"  {Identifier}: {Describe( gate )} is not the one - {Refused( gate, type )}" );

					continue;
				}

				var instance = MethodDesc.IsStatic ? null : ResolveInstance( gate );
				if ( !MethodDesc.IsStatic && instance is null )
				{
					if ( Debug ) Say( $"  {Identifier}: {Describe( gate )} would take it, but there is no {TypeKey.Name} under it" );

					continue;
				}

				if ( Debug ) Say( $"  {Identifier}: run on {Describe( gate )}" );

				return Run( instance );
			}

			if ( Debug && looked == 0 )
				Say( $"  {Identifier}: nothing of type {TargetKey.Name} is registered" );

			return false;
		}

		//The second chance: a widget shortcut on a IShortcutFallback surface, when nothing holding the focus took the key.
		internal bool InvokeUnfocused()
		{
			if ( Attribute.Type != ShortcutType.Widget ) return false;
			if ( MethodDesc.IsStatic && TargetKey == TypeKey ) return false;

			var unclaimed = KeyboardUnclaimed();

			foreach ( var gate in InstancesOf( TargetKey ).Where( x => x is IShortcutFallback ).OrderByDescending( UnderPointer ) )
			{
				if ( !IsShowing( gate ) || !IsOnActiveWindow( gate ) || !IsInCurrentLayout( gate ) ) continue;

				if ( !unclaimed && !UnderPointer( gate ) )
				{
					if ( Debug ) Say( $"  {Identifier}: {Describe( gate )} is not the one - the keyboard is held elsewhere and the pointer is not on it" );

					continue;
				}

				var instance = MethodDesc.IsStatic ? null : ResolveInstance( gate );
				if ( !MethodDesc.IsStatic && instance is null ) continue;

				if ( Debug ) Say( $"  {Identifier}: run on {Describe( gate )}, unfocused" );

				return Run( instance );
			}

			return false;
		}

		//Calls the method, and says whether the key was taken.
		bool Run( object instance )
		{
			MethodDesc.Invoke( instance );

			if ( PassShortcut )
			{
				PassShortcut = false;
				return false;
			}

			return true;
		}

		static bool UnderPointer( object gate ) => gate switch
		{
			Widget widget => widget.IsUnderMouse,
			Rinten.UI.Panel panel => panel.HasHovered,
			_ => false,
		};

		//Which of the gate's conditions said no, for the trace.
		static string Refused( object gate, ShortcutType type )
		{
			if ( !IsValidSurface( gate ) ) return "it is gone";

			return type switch
			{
				ShortcutType.Window => !IsOnActiveWindow( gate ) ? "its window is not the active one"
					: "it is not in the layout on screen",
				ShortcutType.Widget => !IsShowing( gate ) ? "it is not showing"
					: "the keyboard is somewhere else",
				_ => "no reason given",
			};
		}

		//The gate, named the way somebody reading a log would name it.
		static string Describe( object gate )
			=> gate is null ? "nothing" : $"{gate.GetType().Name}";

		//Gate for surface-less shortcuts: the scene area wherever it is, else the window (startup, tests)
		static object OwnLayoutGate()
		{
			if ( EditorWindow is not { } window || !window.IsValid() ) return EditorWindow;

			return window.SceneTabs.IsValid() ? window.SceneTabs : window;
		}

		//For a DockWindow embedded as a workspace, use its dock manager; the window itself is never shown
		static object LayoutSurface( object gate )
		{
			if ( gate is DockWindow { DockManager: { } docks } window
				&& docks.IsValid()
				&& !ReferenceEquals( docks.Parent, window ) )
				return docks;

			return gate;
		}

		//Whether focus is in this surface; panel gates also count widgets hosted in the same surface
		static bool ContainsFocus( object gate )
		{
			gate = LayoutSurface( gate );

			if ( Rinten.UI.InputFocus.Current is { } focusedPanel && Ancestry( focusedPanel ).Contains( gate ) )
				return true;

			if ( Application.FocusWidget is not { } focusedWidget || !focusedWidget.IsValid() )
				return false;

			var chain = Ancestry( focusedWidget ).ToList();

			if ( chain.Contains( gate ) ) return true;

			return gate is Rinten.UI.Panel panel
				&& Ancestry( panel ).OfType<Widget>().FirstOrDefault() is { } host
				&& chain.Contains( host );
		}

		static bool IsValidSurface( object gate ) => gate switch
		{
			Widget widget => widget.IsValid(),
			Rinten.UI.Panel panel => panel.IsValid,
			_ => false,
		};

		static bool IsShowing( object gate ) => LayoutSurface( gate ) switch
		{
			Widget widget => widget.Visible && widget.Enabled,
			Rinten.UI.Panel panel => panel.IsVisible,
			_ => false,
		};

		static bool IsOnActiveWindow( object gate ) => LayoutSurface( gate ) switch
		{
			Widget widget => widget.IsActiveWindow,
			Rinten.UI.Panel panel => Ancestry( panel ).OfType<Widget>().FirstOrDefault() is { } host && host.IsActiveWindow,
			_ => false,
		};

		//Whether the gate is in the layout that is on screen. Only asked of window-level shortcuts. A widget-level
		//one already needs the focus, and whatever is being typed into is in the layout on screen by definition.
		static bool IsInCurrentLayout( object gate ) => LayoutSurface( gate ) switch
		{
			Widget widget => WorkspaceArea.InCurrentLayout( widget ),
			Rinten.UI.Panel panel => Ancestry( panel ).OfType<Widget>().FirstOrDefault() is not { } host
				|| WorkspaceArea.InCurrentLayout( host ),
			_ => false,
		};

		static bool IsAccessible( object gate, ShortcutType type )
		{
			if ( !IsValidSurface( gate ) ) return false;

			var surface = LayoutSurface( gate );

			if ( !ReferenceEquals( surface, gate ) && !IsValidSurface( surface ) ) return false;

			return type switch
			{
				ShortcutType.Window => IsOnActiveWindow( gate ) && IsInCurrentLayout( gate ),
				ShortcutType.Widget => IsShowing( gate ) && ContainsFocus( gate ),
				_ => true,
			};
		}

		//All registered widgets and panels of the given type.
		static IEnumerable<object> InstancesOf( Type type )
		{
			return Targets.Where( t => t.Key.IsAssignableTo( type ) )
				.SelectMany( t => t.Value )
				.Where( IsValidSurface );
		}

		//Instance to invoke on: the gate, or for target overrides the nearest declaring-type instance
		object ResolveInstance( object gate )
		{
			if ( TargetKey == TypeKey )
				return gate;

			var candidates = InstancesOf( TypeKey ).ToList();

			foreach ( var scope in Ancestry( gate ) )
			{
				var match = candidates.FirstOrDefault( x => Ancestry( x ).Contains( scope ) );
				if ( match is not null )
					return match;
			}

			return null;
		}

		void ResetKeys()
		{
			if ( EditorPreferences.ShortcutOverrides.ContainsKey( Identifier ) )
				Keys = EditorPreferences.ShortcutOverrides[Identifier];
			else
				Keys = Attribute.Keys;
		}

		void UpdateDisplayKeys()
		{
			DisplayKeys = string.Join( '+', _keys.Split( '+' )
				.Select( x => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase( x.ToLower() ) ) );
		}

		void GetNameFromIdent( string ident )
		{
			var split = ident.Split( '.' ).ToList();
			if ( split.Count == 1 ) split.Insert( 0, "other" ); //"other" is a fallback group for shortcuts that don't have a group
			if ( string.IsNullOrEmpty( Group ) )
			{
				Group = split.FirstOrDefault();
				Group = Group.Replace( "-", " " ).Replace( "_", " " );
				Group = string.Join( " ", Group.Split( ' ' ).Select( x => char.ToUpper( x[0] ) + x.Substring( 1 ) ) );
			}
			var combined = split.Skip( 1 ).Aggregate( ( a, b ) => a + "." + b );
			Name = combined.Replace( "-", " " ).Replace( "_", " " );
			Name = string.Join( " ", Name.Split( ' ' ).Select( x => char.ToUpper( x[0] ) + x.Substring( 1 ) ) );
		}
	}
}
