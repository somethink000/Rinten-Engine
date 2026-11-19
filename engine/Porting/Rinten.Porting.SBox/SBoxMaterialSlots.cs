namespace Rinten.Porting.SBox;

//Guesses a material slot from its parameter name, after stripping g_t, g_fl, Texture, layer letter
//Matches words like colour, normal, rough, metal, occlusion, emission; others are left alone
internal static class SBoxMaterialSlots
{
	//A slot of ours, or none where the name is about something else.
	internal enum Slot
	{
		None,
		Colour,
		Normal,
		Roughness,
		Metalness,
		Occlusion,
		Emission,
	}

	//One authored value that turned out to belong to a slot: a picture, or a constant where the shader was given a number instead of one.
	internal readonly record struct Candidate( Slot Slot, int Layer, string Key, string Path, Color? Constant );

	//What in a name is decoration rather than meaning.
	private static readonly string[] Prefixes = { "g_tex", "g_t", "g_v", "g_fl", "g_b", "g_n", "g_", "texture", "tex" };

	//Everything in a material that belongs to a slot of ours, in the order it was written.
	internal static List<Candidate> Read( List<KeyValuePair<string, string>> values )
	{
		var found = new List<Candidate>();

		if ( values is null ) return found;

		foreach ( var (key, value) in values )
		{
			var slot = Of( key, out var layer );
			if ( slot == Slot.None ) continue;

			if ( string.IsNullOrWhiteSpace( value ) ) continue;

			//A slot holds a picture or a constant, and a constant is written as [r g b a]. Only one of the two is a path.
			if ( value.StartsWith( '[' ) )
			{
				found.Add( new Candidate( slot, layer, key, null, Constant( value ) ) );
				continue;
			}

			//Bare numbers (e.g. "0.6") are scalar constants, not texture paths
			if ( float.TryParse( value, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out var one ) )
			{
				found.Add( new Candidate( slot, layer, key, null, new Color( one, one, one, one ) ) );
				continue;
			}

			found.Add( new Candidate( slot, layer, key, value, null ) );
		}

		return found;
	}

	//Picture for a slot, else its constant; pictures beat constants, the first layer beats later ones
	internal static string Texture( List<Candidate> found, Slot slot, out Color? constant )
	{
		constant = null;

		Candidate? picture = null;
		Candidate? number = null;

		foreach ( var candidate in found )
		{
			if ( candidate.Slot != slot ) continue;

			if ( candidate.Path is not null )
			{
				if ( picture is null || Rank( candidate ) < Rank( picture.Value ) ) picture = candidate;
				continue;
			}

			if ( number is null || Rank( candidate ) < Rank( number.Value ) ) number = candidate;
		}

		if ( picture is { } best ) return best.Path;

		constant = number?.Constant;
		return "";
	}

	//Earlier layer first, then the plainer name: g_tColor is the colour, g_tAgeColor a detail laid over it
	private static int Rank( Candidate candidate ) => candidate.Layer * 1000 + Stripped( candidate.Key ).Length;

	//A name without its decoration prefix
	private static string Stripped( string key )
	{
		var name = key.ToLowerInvariant();

		foreach ( var prefix in Prefixes )
		{
			if ( name.StartsWith( prefix, StringComparison.Ordinal ) ) return name[prefix.Length..];
		}

		return name;
	}

	//The number a slot was given, where it was given one rather than a picture - a roughness of one, a metalness of nothing.
	internal static float? Scalar( List<Candidate> found, Slot slot )
	{
		Texture( found, slot, out var constant );

		return constant?.r;
	}

	//How many layers a blended material has: the highest lettered colour it actually names, and one for a material that is not blended at all.
	internal static int Layers( List<Candidate> found )
	{
		var layers = 0;

		foreach ( var candidate in found )
		{
			if ( candidate.Slot != Slot.Colour || candidate.Path is null ) continue;

			if ( candidate.Layer + 1 > layers ) layers = candidate.Layer + 1;
		}

		return layers;
	}

	//Which slot a name is for, and which layer of a blend it belongs to.
	internal static Slot Of( string key, out int layer )
	{
		layer = 0;

		if ( string.IsNullOrWhiteSpace( key ) ) return Slot.None;

		var name = key.ToLowerInvariant();

		foreach ( var prefix in Prefixes )
		{
			if ( !name.StartsWith( prefix, StringComparison.Ordinal ) ) continue;

			name = name[prefix.Length..];
			break;
		}

		var whole = Word( name );

		//Strips a trailing layer letter a-d only if the rest of the name still means the same
		if ( name.Length >= 2 && name[^1] is >= 'a' and <= 'd' )
		{
			var trimmed = Word( name[..^1] );

			if ( trimmed != Slot.None && ( whole == Slot.None || trimmed == whole ) )
			{
				layer = name[^1] - 'a';
				return trimmed;
			}
		}

		return whole;
	}

	//Slot word in a name; emission and metal are checked first so broader words don't win
	private static Slot Word( string name )
	{
		if ( name.Length == 0 ) return Slot.None;

		if ( Has( name, "emissi", "selfillum", "glow", "incandesc" ) ) return Slot.Emission;

		if ( Has( name, "mask", "blend", "weight", "vertexpaint" ) ) return Slot.None;

		if ( Has( name, "normal", "bump" ) ) return Slot.Normal;
		if ( Has( name, "rough", "gloss", "smooth" ) ) return Slot.Roughness;
		if ( Has( name, "metal" ) ) return Slot.Metalness;
		if ( name == "ao" || Has( name, "occlusion", "ambientoc" ) ) return Slot.Occlusion;

		if ( Has( name, "color", "colour", "albedo", "diffuse", "basecolor", "translucen" ) ) return Slot.Colour;

		return Slot.None;
	}

	private static bool Has( string name, params string[] words )
	{
		foreach ( var word in words )
		{
			if ( name.Contains( word, StringComparison.Ordinal ) ) return true;
		}

		return false;
	}

	//[r g b a] as a colour, or null if it is not four numbers.
	private static Color? Constant( string value )
	{
		var inside = value.Trim().Trim( '[', ']' ).Trim();
		var parts = inside.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		if ( parts.Length is < 3 or > 4 ) return null;

		var channels = new float[4] { 0.0f, 0.0f, 0.0f, 1.0f };

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( !float.TryParse( parts[i], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out channels[i] ) )
				return null;
		}

		return new Color( channels[0], channels[1], channels[2], channels[3] );
	}
}
