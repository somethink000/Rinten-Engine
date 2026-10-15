using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>A scene's baked lighting as an asset: its atlas, volume and bounce - see BakedLighting.Lightmap.</summary>
[Expose]
[AssetType( Name = "Lightmap", Extension = "lightmap", Category = "Rendering", Icon = "wb_iridescent" )]
public sealed class BakedLightmap : GameResource
{
	/// <summary>What the last bake into it made; empty until one has.</summary>
	[JsonInclude, Hide]
	public BakedLightingData Data { get; set; } = new();

	/// <summary>Whether a bake has filled it.</summary>
	[JsonIgnore, Hide]
	public bool IsEmpty => Data is not { IsEmpty: false };
}
