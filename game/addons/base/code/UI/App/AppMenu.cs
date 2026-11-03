namespace Rinten.UI;

/// <summary>One entry of a title bar menu; a null in a menu's list is a separator line.</summary>
public sealed record AppMenuItem( string Text, Action Run, string Shortcut = "", bool Enabled = true, bool Checked = false );

/// <summary>A title bar menu. Its items are asked for each time it opens, so they follow the app's state.</summary>
public sealed record AppMenu( string Title, Func<IEnumerable<AppMenuItem>> Items );
