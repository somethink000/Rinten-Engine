using System.Text.Json.Nodes;

namespace Rinten;

public abstract partial class Light
{
	public override int ComponentVersion => 2;

	//Version 1: Mode became Static; Mixed and Baked map to Static, a missing Mode to Realtime
	[Expose, JsonUpgrader( typeof( Light ), 1 )]
	static void Upgrader_v1( JsonObject obj )
	{
		var mode = obj.TryGetPropertyValue( "Mode", out var value )
			? value?.GetValue<string>()
			: "Realtime";

		obj["Static"] = !string.Equals( mode, "Realtime", System.StringComparison.OrdinalIgnoreCase );

		obj.Remove( "Mode" );
	}

	//Version 2: every light is baked unless NotBaked; BakedWhole counts only where Baked was set
	[Expose, JsonUpgrader( typeof( Light ), 2 )]
	static void Upgrader_v2( JsonObject obj )
	{
		var baked = obj.TryGetPropertyValue( "Baked", out var was ) && was?.GetValue<bool>() == true;
		var whole = obj.TryGetPropertyValue( "BakedWhole", out var all ) && all?.GetValue<bool>() == true;

		obj["BakedWhole"] = baked && whole;
		obj.Remove( "Baked" );
	}
}
