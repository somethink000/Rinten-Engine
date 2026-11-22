using System.Collections.Generic;
using Editor.Inspectors;

namespace Editor.Materials;

/// <summary>Dropdown of a shader feature's value names, from ShaderFeatureNamesAttribute (no enum exists)</summary>
[CustomEditor( typeof( string ), WithAllAttributes = [typeof( ShaderFeatureNamesAttribute )] )]
public sealed class ShaderFeatureControl : StringDropdownControl
{
	protected override List<Rinten.UI.Option> BuildOptions()
	{
		var options = new List<Rinten.UI.Option>();

		if ( Property is null || !Property.TryGetAttribute<ShaderFeatureNamesAttribute>( out var names ) )
			return options;

		foreach ( var name in names.Names )
			options.Add( new Rinten.UI.Option( name, name ) );

		return options;
	}
}
