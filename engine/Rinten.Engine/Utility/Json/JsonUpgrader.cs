using Rinten.Internal;
using System.Text.Json.Nodes;

namespace Rinten;

[SkipHotload]
internal static class JsonUpgrader
{
	private static (MethodDescription Method, JsonUpgraderAttribute Attribute)[] _methods;

	public static void UpdateUpgraders( TypeLibrary typeLibrary )
	{
		_methods = typeLibrary.GetMethodsWithAttribute<JsonUpgraderAttribute>().ToArray();
	}

	/// <summary>Runs through all upgraders that match its class where our version is lower than the specified version.</summary>
	public static void Upgrade( int version, JsonObject json, Type targetType )
	{
		//Upgraders not yet gathered by UpdateUpgraders: nothing to run
		if ( _methods is null )
			return;

		//An abstract base's upgraders run for what derives from it - Light's for a PointLight
		foreach ( var e in _methods
			.Where( x => x.Attribute.Type == targetType || ( x.Attribute.Type.IsAbstract && x.Attribute.Type.IsAssignableFrom( targetType ) ) )
			.OrderBy( x => x.Attribute.Version )
			.Where( x => x.Attribute.Version > version ) )
		{
			try
			{
				e.Method.Invoke( null, new[] { json } );
			}
			catch ( Exception ex )
			{
				Log.Warning( ex, $"A component version upgrader ({e.Attribute.Type}, version {e.Attribute.Version}) threw an exception while trying to upgrade, so we halted the upgrade." );
				//Let's stop trying to upgrade because something is broken.
				return;
			}
			finally
			{
				//Update our serialized version step by step.
				json["__version"] = e.Attribute.Version;
			}
		}
	}
}
