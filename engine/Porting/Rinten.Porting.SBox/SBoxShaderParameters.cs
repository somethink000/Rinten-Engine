using System.IO;
using System.Text.RegularExpressions;

namespace Rinten.Porting.SBox;

//A project's own shader keeps its own parameters: what its materials set comes across under the names it reads them by,
//so a rewrite of that shader under the same name finds them. Numbers are carried as written - which ones are lengths only the shader knows.
internal static partial class SBoxShaderParameters
{
	//CreateTexture2D( g_tX ) < Channel( ..., Box( TextureX ), ... ) - a material's TextureX is the shader's g_tX
	[GeneratedRegex( @"CreateTexture2D(?:WithoutSampler)?\s*\(\s*(g_t\w+)\s*\)\s*<[^;]*?\(\s*(Texture\w+)\s*\)" )]
	private static partial Regex Picture();

	/// <summary>Adds the switches, numbers and pictures a material sets for a project's own shader.</summary>
	public static void Carry( MaterialDefinition definition, List<KeyValuePair<string, string>> authored, string shaderFile,
		Func<string, string> carry )
	{
		var pictures = PicturesOf( shaderFile );

		foreach ( var (key, value) in authored )
		{
			if ( string.IsNullOrWhiteSpace( value ) ) continue;

			if ( key.StartsWith( "F_", StringComparison.Ordinal ) )
			{
				if ( int.TryParse( value, out var on ) ) definition.Features.TryAdd( key, on );
				continue;
			}

			if ( key.StartsWith( "g_", StringComparison.Ordinal ) )
			{
				if ( Vector( value ) is { } number ) definition.Numbers.TryAdd( key, number );
				continue;
			}

			if ( !key.StartsWith( "Texture", StringComparison.Ordinal ) ) continue;

			//Colour and normal have slots of their own here, which the material has already filled
			var slot = SBoxMaterialSlots.Of( key, out _ );
			if ( slot is SBoxMaterialSlots.Slot.Colour or SBoxMaterialSlots.Slot.Normal ) continue;

			var name = pictures.GetValueOrDefault( key ) ?? $"g_t{key["Texture".Length..]}";
			if ( definition.Textures.ContainsKey( name ) ) continue;

			var path = carry( value );
			if ( string.IsNullOrEmpty( path ) ) continue;

			definition.Textures[name] = SBoxPorter.Rewrite( path );

			//The colour is cut by it too: the passes that do not run the shader's own cut - the clusters' depth, shadows,
			//far stand-ins - read only the colour's alpha, and would draw every card of a plant whole
			if ( key == "TextureOpacity" && string.IsNullOrEmpty( definition.OpacityMask ) ) definition.OpacityMask = SBoxPorter.Rewrite( path );
		}

		//Their colour tint is the shader's own number, which game code may set at run time; as the material's tint as well it would be applied twice
		if ( definition.Numbers.ContainsKey( "g_vColorTint" ) ) definition.Tint = Color.White;
	}

	//Input name to texture name, from the shader's source; empty where there is none to read
	private static Dictionary<string, string> PicturesOf( string shaderFile )
	{
		var found = new Dictionary<string, string>();

		if ( shaderFile is null || !File.Exists( shaderFile ) ) return found;

		foreach ( Match match in Picture().Matches( File.ReadAllText( shaderFile ) ) )
			found.TryAdd( match.Groups[2].Value, match.Groups[1].Value );

		return found;
	}

	//"0.5" or "[1 0 0 1]" as four numbers, the unwritten ones nought; null for anything else
	private static Vector4? Vector( string value )
	{
		var parts = value.Trim( '[', ']', ' ' ).Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length is 0 or > 4 ) return null;

		var four = new float[4];

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( !float.TryParse( parts[i], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out four[i] ) )
				return null;
		}

		return new Vector4( four[0], four[1], four[2], four[3] );
	}
}
