using SkiaSharp;
using System.IO;

namespace Rinten;

//Font metadata written at export so faces load on first use; folders without it are read whole
internal static class FontIndex
{
	/// <summary>The file's name, inside the fonts folder it describes.</summary>
	public const string FileName = "fonts.index";

	/// <summary>One face, as the index says it.</summary>
	public readonly record struct Entry( string Path, string Family, int Weight, int Width, SKFontStyleSlant Slant );

	/// <summary>Writes the index of every font under <paramref name="folder"/>. False when there were none to index.</summary>
	public static bool Write( string folder )
	{
		if ( !Directory.Exists( folder ) ) return false;

		var lines = new List<string>();

		foreach ( var file in Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories ).Order( StringComparer.Ordinal ) )
		{
			var extension = System.IO.Path.GetExtension( file );
			if ( !extension.Equals( ".ttf", StringComparison.OrdinalIgnoreCase ) && !extension.Equals( ".otf", StringComparison.OrdinalIgnoreCase ) )
				continue;

			using var face = SKTypeface.FromFile( file );
			if ( face is null ) continue;

			var relative = System.IO.Path.GetRelativePath( folder, file ).Replace( '\\', '/' );
			lines.Add( $"{relative}\t{face.FamilyName}\t{face.FontWeight}\t{face.FontWidth}\t{(int)face.FontSlant}" );
		}

		if ( lines.Count == 0 ) return false;

		File.WriteAllLines( System.IO.Path.Combine( folder, FileName ), lines );
		return true;
	}

	/// <summary>The family a font file says it is, or null where it does not read as a font.</summary>
	public static string FamilyOf( string file )
	{
		using var face = SKTypeface.FromFile( file );
		return face?.FamilyName;
	}

	/// <summary>The faces an index names. Lines that do not read are skipped.</summary>
	public static List<Entry> Read( string text )
	{
		var entries = new List<Entry>();

		foreach ( var line in text.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var parts = line.Split( '\t' );
			if ( parts.Length != 5 ) continue;

			if ( !int.TryParse( parts[2], out var weight ) || !int.TryParse( parts[3], out var width ) || !int.TryParse( parts[4], out var slant ) )
				continue;

			entries.Add( new Entry( parts[0], parts[1], weight, width, (SKFontStyleSlant)slant ) );
		}

		return entries;
	}
}
