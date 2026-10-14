using System.Text.Json.Nodes;

namespace Rinten;

public partial class EnvmapProbe
{
	public override int ComponentVersion => 2;

	//v1 - Changed RenderDynamically bool to Mode enum.
	[Expose, JsonUpgrader( typeof( EnvmapProbe ), 1 )]
	static void Upgrader_v1( JsonObject obj )
	{
		if ( obj.TryGetPropertyValue( "RenderDynamically", out var renderDynamically ) )
		{
			var isDynamic = (bool)renderDynamically;
			obj["Mode"] = isDynamic ? "Realtime" : "CustomTexture";
		}
	}

	//v2 - Two modes: Realtime is Runtime, and CustomTexture, which nothing drew, is Baked; captures stop at 512
	[Expose, JsonUpgrader( typeof( EnvmapProbe ), 2 )]
	static void Upgrader_v2( JsonObject obj )
	{
		obj["Mode"] = obj["Mode"]?.GetValue<string>() == "Realtime" ? "Runtime" : "Baked";

		if ( obj["Resolution"]?.GetValue<string>() == "Huge" ) obj["Resolution"] = "Large";

		obj.Remove( "Texture" );
		obj.Remove( "BakedTexture" );
		obj.Remove( "MaxDistance" );
		obj.Remove( "RenderExcludeTags" );
	}
}
