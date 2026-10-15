using System.Text.Json.Nodes;
namespace Rinten;

public partial class SceneFile
{
	//Version 4 to 5 - baked lighting left its component for the scene: the first one found, settings and bake,
	//goes into the scene's properties, and its object goes too where nothing else was on it
	[Expose, JsonUpgrader( typeof( SceneFile ), 5 )]
	internal static void Upgrader_v5( JsonObject obj )
	{
		if ( obj["GameObjects"] is not JsonArray roots ) return;

		JsonObject found = null;
		TakeBakedLighting( roots, ref found );

		if ( found is null ) return;

		var properties = obj["SceneProperties"] as JsonObject ?? new JsonObject();
		obj["SceneProperties"] = properties;

		if ( properties.ContainsKey( "Lighting" ) ) return;

		var lighting = new JsonObject();

		foreach ( var (key, value) in found )
		{
			if ( key.StartsWith( "__" ) || key == "Flags" || key.StartsWith( "On" ) ) continue;
			lighting[key] = value?.DeepClone();
		}

		lighting["Enabled"] = found["__enabled"]?.GetValue<bool>() ?? true;
		properties["Lighting"] = lighting;
	}

	//Every BakedLighting component out of the objects, the first kept; an object left with nothing on it and no children goes
	private static void TakeBakedLighting( JsonArray objects, ref JsonObject found )
	{
		for ( var i = objects.Count - 1; i >= 0; i-- )
		{
			if ( objects[i] is not JsonObject go ) continue;

			if ( go["Children"] is JsonArray children ) TakeBakedLighting( children, ref found );

			if ( go["Components"] is not JsonArray components ) continue;

			var had = false;

			for ( var c = components.Count - 1; c >= 0; c-- )
			{
				if ( components[c] is not JsonObject component ) continue;
				if ( component["__type"]?.GetValue<string>() != "Rinten.BakedLighting" ) continue;

				found = component;
				components.RemoveAt( c );
				had = true;
			}

			var empty = components.Count == 0 && ( go["Children"] is not JsonArray left || left.Count == 0 );
			if ( had && empty ) objects.RemoveAt( i );
		}
	}
}
