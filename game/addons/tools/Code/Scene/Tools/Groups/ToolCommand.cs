namespace Editor;

/// <summary>A tool command made of delegates, for callers with no type of their own to put one on.</summary>
public sealed class ToolCommand : IToolCommand
{
	private readonly Func<string> icon;
	private readonly Action run;

	public ToolCommand( string icon, string title, Action run ) : this( () => icon, title, run )
	{
	}

	public ToolCommand( Func<string> icon, string title, Action run )
	{
		this.icon = icon;
		this.run = run;

		Title = title;
	}

	public string Title { get; }

	/// <summary>The line under the title. Nothing, where nothing was said.</summary>
	public string Description { get; init; }

	/// <summary>The shortcut identifier that presses this. None, where nothing was said.</summary>
	public string Shortcut { get; init; }

	/// <summary>The heading it is listed under, on a strip that lists its commands by name.</summary>
	public string Section { get; init; }

	/// <summary>The small glyph over the corner. None, where nothing was said.</summary>
	public string Badge { get; init; }

	/// <summary>Whether what this turns on is on. Never, where nothing was said.</summary>
	public Func<bool> Active { get; init; }

	/// <summary>Whether it can be run. Always, where nothing was said.</summary>
	public Func<bool> Enabled { get; init; }

	public string Icon => Read( icon, null );

	/// <summary>The two lines, as the tooltip shows them - see <see cref="ToolCommandText.ToolTip"/>.</summary>
	public string ToolTip => ToolCommandText.ToolTip( this );

	public bool IsActive => Read( Active, false );

	public bool IsEnabled => Read( Enabled, true );

	public void Execute() => run?.Invoke();

	private static T Read<T>( Func<T> source, T fallback )
	{
		if ( source is null ) return fallback;

		try
		{
			return source();
		}
		catch ( Exception )
		{
			return fallback;
		}
	}
}

//Here rather than on each command, so every command reads the keybinds page the same way and every tooltip says the same thing.
/// <summary>The words a tool command shows: its tooltip.</summary>
public static class ToolCommandText
{
	/// <summary>The keys the command's shortcut is on right now, upper case, or null where it has no shortcut or the shortcut has no keys.</summary>
	public static string Keys( IToolCommand command )
	{
		if ( string.IsNullOrEmpty( command?.Shortcut ) ) return null;

		var keys = EditorShortcuts.GetKeys( command.Shortcut )?.Trim().ToUpperInvariant();

		return string.IsNullOrEmpty( keys ) ? null : keys;
	}

	/// <summary><c>&lt;b&gt;Title (KEYS)&lt;/b&gt;&lt;br&gt;Description</c>, with the keys and the description left out where there are none.</summary>
	public static string ToolTip( IToolCommand command )
	{
		var title = command.Title ?? string.Empty;
		var keys = Keys( command );

		if ( keys is not null ) title += $" ({keys})";

		return string.IsNullOrEmpty( command.Description )
			? $"<b>{title}</b>"
			: $"<b>{title}</b><br>{command.Description}";
	}
}
