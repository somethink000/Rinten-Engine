using System.Text.Json.Nodes;
namespace Rinten;

public partial class SceneFile
{
	//Version 5 to 6 - baked lighting became two blocks, lightmaps and envmap probes: its flat settings are the lightmaps',
	//the lightmap asset their Asset, and the atlas's widest their Resolution
	[Expose, JsonUpgrader( typeof( SceneFile ), 6 )]
	internal static void Upgrader_v6( JsonObject obj )
	{
		if ( obj["SceneProperties"] is not JsonObject properties || properties["Lighting"] is not JsonObject flat ) return;
		if ( flat.ContainsKey( "Lightmaps" ) ) return;

		var lightmaps = new JsonObject();

		foreach ( var (key, value) in flat )
		{
			if ( key == "Enabled" ) continue;

			var name = key switch { "Lightmap" => "Asset", "MaxResolution" => "Resolution", _ => key };
			lightmaps[name] = value?.DeepClone();
		}

		properties["Lighting"] = new JsonObject
		{
			["Enabled"] = flat["Enabled"]?.DeepClone() ?? true,
			["Lightmaps"] = lightmaps,
		};
	}
}
