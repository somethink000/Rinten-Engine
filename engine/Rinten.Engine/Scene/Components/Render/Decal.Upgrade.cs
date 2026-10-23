using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten;

public partial class Decal
{
	public override int ComponentVersion => 3;

	[Expose, JsonUpgrader( typeof( Decal ), 2 )]
	public static void Upgrader_v2( JsonObject json )
	{
		if ( !json.ContainsKey( "ColorTint" ) ) return;

		try
		{
			var color32 = json["ColorTint"].Deserialize<Color32>();
			json["ColorTint"] = JsonSerializer.SerializeToNode( color32.ToColor(), EnumConvert.Plain );
		}
		catch { }
	}

	[Expose, JsonUpgrader( typeof( Decal ), 3 )]
	public static void Upgrader_v3( JsonObject json )
	{
		if ( !json.ContainsKey( "ColorTint" ) ) return;

		try
		{
			var color = json["ColorTint"].Deserialize<Color>();
			json["ColorTint"] = JsonSerializer.SerializeToNode( (ParticleGradient)color, EnumConvert.Plain );
		}
		catch { }
	}
}
