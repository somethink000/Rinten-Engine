namespace Microsoft.AspNetCore.Components;

public partial class RouteAttribute
{
	/// <summary>The page in the game's type library that answers this url and not the one it is under.</summary>
	public static (TypeDescription Type, RouteAttribute Attribute)? FindValidTarget( string url, string parentUrl )
	{
		var val = Game.TypeLibrary.GetTypesWithAttribute<RouteAttribute>()
								.Where( x => x.Attribute.IsUrl( url ) && !x.Attribute.IsUrl( parentUrl ) )
								.OrderByDescending( x => x.Attribute.Url.Count( c => c == '*' ) )
								.FirstOrDefault();

		if ( val.Type == null )
			return null;

		return val;
	}
}
