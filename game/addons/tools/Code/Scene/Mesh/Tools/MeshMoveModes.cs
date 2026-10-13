namespace Editor.MeshEditor;

/// <summary>Move-mode column (position, rotate, resize...) and its W E R T Y keys</summary>
public static class MeshMoveModes
{
	//Which key each mode alias is on, by the identifier the key is registered under.
	private static readonly (string Alias, string Shortcut)[] Keys =
	[
		("mesh.position.mode", "tools.position-tool"),
		("mesh.rotate.mode", "tools.rotate-tool"),
		("mesh.resize.mode", "tools.resize-tool"),
		("mesh.scale.mode", "tools.scale-tool"),
		("mesh.pivot.mode", "tools.pivot-tool"),
	];

	/// <summary>The shortcut identifier a move mode alias is pressed by, or null where it has no key.</summary>
	public static string ShortcutFor( string alias )
	{
		if ( string.IsNullOrEmpty( alias ) ) return null;

		foreach ( var (known, shortcut) in Keys )
		{
			if ( known == alias ) return shortcut;
		}

		return null;
	}

	//For the buttons that show these modes elsewhere - see MoveModePanel .
	/// <summary>The keys a move mode alias is on right now, or empty.</summary>
	public static string KeysFor( string alias )
		=> ShortcutFor( alias ) is { } shortcut ? EditorShortcuts.GetKeys( shortcut ) ?? "" : "";

	/// <summary>The column, for a mesh tool.</summary>
	public static ToolGroup Group( MeshTool tool )
	{
		var group = new ToolGroup( "Move", ToolButtonSize.Option );

		foreach ( var type in EditorTypeLibrary.GetTypes<MoveMode>().OrderBy( x => x.Order ) )
		{
			if ( type.IsAbstract ) continue;

			var target = type.TargetType;

			group.Add( new ToolCommand( type.Icon, type.Title, () => Activate( tool, type ) )
			{
				Description = type.Description,
				Shortcut = ShortcutFor( Alias( type ) ),
				Active = () => tool.MoveMode?.GetType() == target,
			} );
		}

		return ToolGroup.OrNull( group );
	}

	private static string Alias( TypeDescription type )
		=> type.GetAttribute<AliasAttribute>()?.Value?.FirstOrDefault();

	private static void Activate( MeshTool tool, TypeDescription type )
	{
		if ( tool.MoveMode?.GetType() == type.TargetType ) return;

		tool.MoveMode = type.Create<MoveMode>();
	}
}
