using System.Text.Json.Nodes;

namespace Rinten;

public abstract partial class Light
{
	public override int ComponentVersion => 3;

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

	//Version 3: the default falloff went from 0.15 to the inverse square; a lamp left at 0.15 takes it,
	//brightened to give the same light 2 m out
	[Expose, JsonUpgrader( typeof( Light ), 3 )]
	static void Upgrader_v3( JsonObject obj )
	{
		var falloff = obj.TryGetPropertyValue( "Attenuation", out var saved ) && saved is not null ? saved.GetValue<float>() : 0.15f;
		if ( MathF.Abs( falloff - 0.15f ) > 1e-4f ) return;

		obj["Attenuation"] = 1.0f;

		var type = obj.TryGetPropertyValue( "__type", out var named ) ? named?.GetValue<string>() : null;
		if ( type is null || type.EndsWith( "DirectionalLight" ) ) return;

		const float Reference = 2.0f;
		var brightness = obj.TryGetPropertyValue( "Brightness", out var was ) && was is not null ? was.GetValue<float>() : 1.0f;
		obj["Brightness"] = brightness * MathF.Pow( Reference, 2.0f - ( 2.0f * falloff ) );
	}
}
