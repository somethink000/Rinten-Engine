using System;

namespace Editor
{
	public partial class Option : EditorObject
	{
		//Which widget this option belongs to - a menu, a tool bar, a field with buttons in it.
		internal Widget ParentWidget { get; private set; }

		/// <summary>Puts this option on a widget.</summary>
		public void SetParent( Widget parent )
		{
			ParentWidget = parent;
		}

		/// <summary>Called when the option is picked.</summary>
		public Action Triggered;

		/// <summary>Called when a checkable option is toggled.</summary>
		public Action<bool> Toggled;

		/// <summary>Called instead of the pick when the option's menu row is right-clicked; the menu closes after.</summary>
		public Action RightClicked;

		/// <summary>Asked for the checked state rather than keeping one, where the thing being toggled is somewhere else.</summary>
		public Func<bool> FetchCheckedState;

		string _text;

		/// <summary>What this row of the menu says.</summary>
		public string Text
		{
			get => _text;
			set
			{
				_text = value;
				UpdateText();
			}
		}

		/// <summary>Text to display if <see cref="Text"/> is empty.</summary>
		public string IconText
		{
			get => iconText;
			set
			{
				iconText = value;
			}
		}

		private string iconText;

		/// <summary>Whether this option is a toggle option. <see cref="Checked"/>.</summary>
		public bool Checkable
		{
			get => checkable;
			set
			{
				checkable = value;
			}
		}

		/// <summary>Whether this option is toggled/checked. <see cref="Checkable"/>.</summary>
		public bool Checked
		{
			get
			{
				return FetchCheckedState?.Invoke() ?? isChecked;
			}
			set
			{
				//Read Checked, not the field: fetched state lives elsewhere and the field can be stale
				var was = Checked;

				isChecked = value;

				if ( was == value ) return;

				InternalToggled( value );
			}
		}

		[Obsolete( $"Use {nameof( ToolTip )}" )]
		public string Tooltip
		{
			get => ToolTip;
			set => ToolTip = value;
		}

		private string _tooltip;

		public string ToolTip
		{
			get => _tooltip;
			set
			{
				_tooltip = value;
				tooltip = value;

				return;
			}
		}

		[Obsolete( $"Use {nameof( StatusTip )}" )]
		public string StatusText
		{
			get => StatusTip;
			set => StatusTip = value;
		}

		public string StatusTip
		{
			get => statusText;
			set
			{
				statusText = value;
			}
		}

		/// <summary>Whether this option can be clicked. Will also be visually different.</summary>
		public bool Enabled
		{
			get => enabled;
			set
			{
				enabled = value;
			}
		}


		[Obsolete( "Please use ShortcutName, which takes a shortcut ident (such as editor.save) instead of keys (such as CTRL+S)." )]
		public string Shortcut { get => _shortcutName; set => _shortcutName = value; }

		public string ShortcutName
		{
			get => _shortcutName;
			set
			{
				_shortcutName = value;
				UpdateText();
			}
		}
		string _shortcutName;

		/// <summary>Dim right-hand text for non-shortcut info like a path tail; clipped from the left</summary>
		public string Note
		{
			get => _note;
			set
			{
				_note = value;
				UpdateText();
			}
		}
		string _note;

		string _icon;
		Pixmap _iconImage;

		/// <summary>The icon for this option.</summary>
		public string Icon
		{
			get => _icon;
			set
			{
				if ( _icon == value ) return;

				_icon = value;
				icon = value;

				return;
			}
		}

		/// <summary>The icon for this option.</summary>
		public Pixmap IconImage
		{
			get => _iconImage;
			set
			{
				if ( _iconImage == value ) return;

				_iconImage = value;

				return;
			}
		}

		public Option( string title = null, string icon = null, Action action = null ) : this( null, title, icon, action )
		{
		}

		/// <summary>One row of a menu: what it says, what it shows, and what it does.</summary>
		public Option( EditorObject parent, string title = null, string icon = null, Action action = null )
		{
			if ( title != null ) Text = title;
			if ( icon != null ) Icon = icon;
			if ( action != null ) Triggered = action;
		}

		public Option( EditorObject parent, string title, Pixmap icon, Action action = null )
		{
			if ( title != null ) Text = title;
			if ( icon != null ) IconImage = icon;
			if ( action != null ) Triggered = action;
		}

		//Called when this option was clicked..
		protected virtual void OnTriggered()
		{
			Triggered?.Invoke();
		}

		internal void InternalTriggered()
		{
			OnTriggered();
		}

		//The same, for a menu of ours that was clicked.
		internal void OnTriggeredInternal() => OnTriggered();

		[Event( "keybinds.update" )]
		void UpdateText()
		{
			//A menu of ours draws the text and the keys itself, from the two properties, so there is nothing to push anywhere.
			text = _text;
			shortcut = string.IsNullOrEmpty( _shortcutName )
				? _note
				: EditorShortcuts.GetDisplayKeys( _shortcutName );
		}

		//Called when this option was toggled.
		protected virtual void OnToggled( bool b )
		{
			Toggled?.Invoke( b );
		}

		internal void InternalToggled( bool b )
		{
			OnToggled( b );
		}

		//The menu holding this is about to open, so anything that asks for its state rather than keeping it is asked now.
		internal void AboutToShow()
		{
			if ( FetchCheckedState is not null )
			{
				Checked = FetchCheckedState();
			}
		}

		/// <summary>Takes this option off whatever it was put on.</summary>
		public override void OnDestroyed()
		{
			base.OnDestroyed();

			switch ( ParentWidget )
			{
				case Menu menu:
					menu.RemoveOption( this );
					break;

				case ToolBar toolbar:
					toolbar.RemoveOption( this );
					break;
			}

			ParentWidget = null;
		}

		/// <summary>Sets an icon for the option via a raw image.</summary>
		public void SetIcon( Pixmap pixmap )
		{
			_iconImage = pixmap;
		}
	}
}
