using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Rinten;

//Parses a shader's FEATURES block: switches that compile shader parts in or out
internal static class CoreShaderFeatures
{
	//Material fields (Map or Flag annotations) that switch a feature on without naming it
	internal enum Implied
	{
		Nothing,
		Map,
		Flag,
	}

	//One switch, as the shader declared it.
	internal sealed record Feature( string Name, int Least, int Most, string Group,
		IReadOnlyDictionary<int, string> Names, Implied What = Implied.Nothing, string Field = null,
		int? Start = null )
	{
		/// <summary>What this is worth before anybody has set it.</summary>
		public int Default => Hold( Start ?? Least );

		/// <summary>Whether this is on-or-off rather than one of several.</summary>
		public bool IsSwitch => Least == 0 && Most == 1;

		/// <summary>What to call this value in a list, or the number itself.</summary>
		public string NameOf( int value )
			=> Names is not null && Names.TryGetValue( value, out var found ) ? found : value.ToString();

		/// <summary>The value brought inside what the shader allows.</summary>
		public int Hold( int value ) => Math.Clamp( value, Least, Most );
	}

	private const string BlockName = "FEATURES";

	//Nothing, for a shader that declared no features.
	internal static readonly Feature[] None = Array.Empty<Feature>();

	//The features a shader file declares, in the order it declared them. Includes inside the block are followed, once each, so a cycle cannot hang
	//this. A file that is not there contributes nothing rather than throwing: a shader is allowed to be broken, and the compiler is what says so.
	internal static Feature[] Of( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return None;

		//Asked on every material rebuild in play; read again only when the file is written
		var written = File.GetLastWriteTimeUtc( path );

		lock ( known )
		{
			if ( known.TryGetValue( path, out var had ) && had.Written == written ) return had.Features;
		}

		try
		{
			var found = new List<Feature>();
			var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			Gather( path, CompiledFile.TextOf( path ), found, seen );

			var features = found.Count == 0 ? None : found.ToArray();
			lock ( known ) known[path] = (written, features);

			return features;
		}
		catch ( IOException )
		{
			return None;
		}
	}

	private static readonly Dictionary<string, (DateTime Written, Feature[] Features)> known = new( StringComparer.Ordinal );

	//The same, for source already in hand.
	internal static Feature[] Of( string source, string directory )
	{
		if ( string.IsNullOrEmpty( source ) ) return None;

		var found = new List<Feature>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		Gather( Path.Combine( directory ?? "", "source" ), source, found, seen );

		return found.Count == 0 ? None : found.ToArray();
	}

	private static void Gather( string path, string source, List<Feature> into, HashSet<string> seen )
	{
		if ( !Block( source, BlockName, out var body ) ) return;

		var directory = Path.GetDirectoryName( path ) ?? "";

		foreach ( var line in body.Split( '\n' ) )
		{
			var text = line.Trim();

			if ( text.Length == 0 ) continue;

			//An include inside the block is a block of its own, somewhere else. Its whole file is read as if it
			//were the body - a features include is nothing but Feature lines and has no FEATURES around them.
			if ( text.StartsWith( "#include", StringComparison.Ordinal ) )
			{
				var named = Between( text, '"', '"' );

				if ( string.IsNullOrWhiteSpace( named ) ) continue;

				var full = Path.GetFullPath( Path.Combine( directory, named ) );

				//Either the file or the packed one beside it: a build carries the second, and an include skipped
				//here is a block of numbers missing from the text a key is made of - which is a key nothing baked.
			if ( !seen.Add( full ) || !CompiledFile.Exists( full ) ) continue;

				try
				{
					var inner = CompiledFile.TextOf( full );

					//Either wrapped in a block of its own or bare, and both are worth reading: an include written to be included has no reason to repeat the header.
					Gather( full, Block( inner, BlockName, out _ ) ? inner : BlockName + "\n{\n" + inner + "\n}\n",
						into, seen );
				}
				catch ( IOException )
				{
				}

				continue;
			}

			if ( !text.StartsWith( "Feature", StringComparison.Ordinal ) ) continue;

			if ( Read( text ) is { } feature )
			{
				//The first declaration of a name wins, so a shader can include a shared set and
				//then say something different about one of them - which is what an override is.
				if ( !into.Exists( x => string.Equals( x.Name, feature.Name, StringComparison.Ordinal ) ) )
					into.Add( feature );
			}
		}
	}

	//One Feature( … ); line, or null where it is not one.
	private static Feature Read( string text )
	{
		var open = text.IndexOf( '(' );

		if ( open < 0 ) return null;

		//Where the call itself ends, matched rather than searched for: the value names are a bracket inside
		//it and the annotation after it is another, so neither the first nor the last close is the right one.
		var close = Closing( text, open );

		if ( close <= open ) return null;

		var inside = text[( open + 1 )..close];

		//And what a material has that switches it on by itself, if the shader said - see Implied.
		var what = Implied.Nothing;
		string field = null;
		int? start = null;

		var noted = text[( close + 1 )..];

		if ( Between( noted, '<', '>' ) is { } annotations )
		{
			if ( Call( annotations, "Map" ) is { } map ) { what = Implied.Map; field = map.Trim(); }
			else if ( Call( annotations, "Flag" ) is { } flag ) { what = Implied.Flag; field = flag.Trim(); }

			//What it is worth before anybody sets it, where the shader would rather that were not the low end of its range - see Feature.Default.
			if ( Call( annotations, "Default" ) is { } written
				&& int.TryParse( written.Trim(), out var said ) )
			{
				start = said;
			}
		}

		//The group is the last quoted thing on the line; the value names are quoted too, so they have to come out first.
		var names = new Dictionary<int, string>();
		var listed = Between( inside, '(', ')' );

		if ( listed is not null )
		{
			foreach ( var pair in listed.Split( ',' ) )
			{
				var halves = pair.Split( '=' );

				if ( halves.Length != 2 ) continue;

				if ( !int.TryParse( halves[0].Trim(), out var at ) ) continue;

				names[at] = halves[1].Trim().Trim( '"' );
			}

			inside = inside.Remove( inside.IndexOf( '(' ), listed.Length + 2 );
		}

		var parts = inside.Split( ',' );

		if ( parts.Length < 2 ) return null;

		var name = parts[0].Trim();

		if ( name.Length == 0 ) return null;

		var range = parts[1].Trim();
		var dots = range.IndexOf( "..", StringComparison.Ordinal );

		var least = 0;
		var most = 1;

		if ( dots > 0 )
		{
			int.TryParse( range[..dots].Trim(), out least );
			int.TryParse( range[( dots + 2 )..].Trim(), out most );
		}

		if ( most < least ) most = least;

		var group = parts.Length > 2 ? parts[2].Trim().Trim( '"' ) : "";

		return new Feature( name, least, most, group, names, what, field, start );
	}

	//Where the bracket opened at open closes.
	private static int Closing( string text, int open )
	{
		var depth = 0;

		for ( var i = open; i < text.Length; i++ )
		{
			if ( text[i] == '(' ) depth++;
			else if ( text[i] == ')' && --depth == 0 ) return i;
		}

		return -1;
	}

	//What is inside Name( … ) , or null where it is not there.
	private static string Call( string text, string name )
	{
		var at = text.IndexOf( name, StringComparison.Ordinal );

		if ( at < 0 ) return null;

		if ( at > 0 && char.IsLetterOrDigit( text[at - 1] ) ) return null;

		var open = at + name.Length;

		while ( open < text.Length && char.IsWhiteSpace( text[open] ) ) open++;

		if ( open >= text.Length || text[open] != '(' ) return null;

		var close = text.IndexOf( ')', open );

		return close > open ? text[( open + 1 )..close] : null;
	}

	//Resolves every declared feature for a material: the shader's own rule, then explicit overrides
	internal static Dictionary<string, int> For( Feature[] features,
		Func<Implied, string, bool> has, IReadOnlyDictionary<string, int> told )
	{
		var chosen = new Dictionary<string, int>( StringComparer.Ordinal );

		if ( features is not null && has is not null )
		{
			foreach ( var feature in features )
			{
				if ( feature.What == Implied.Nothing || string.IsNullOrWhiteSpace( feature.Field ) ) continue;

				//Naming the picture turns the feature on; otherwise it falls back to its default, not to off
				chosen[feature.Name] = has( feature.What, feature.Field )
					? feature.Most
					: feature.Default;
			}
		}

		if ( told is not null )
		{
			foreach ( var (name, value) in told ) chosen[name] = value;
		}

		return chosen;
	}

	//The body of the FEATURES block, or false where there is none.
	internal static bool Block( string source, string name, out string body )
	{
		body = null;

		var at = Find( source, name );

		if ( at < 0 ) return false;

		var open = source.IndexOf( '{', at );

		if ( open < 0 ) return false;

		var depth = 0;

		for ( var i = open; i < source.Length; i++ )
		{
			if ( source[i] == '{' ) depth++;
			else if ( source[i] == '}' && --depth == 0 )
			{
				body = source[( open + 1 )..i];
				return true;
			}
		}

		return false;
	}

	//The source with the block cut out, for handing to a compiler that would not know what to do with it. Replaced by as many newlines as it
	//took up rather than removed, so that every line number the compiler reports is still the line number in the file somebody is looking at.
	internal static string Without( string source, string name = BlockName )
	{
		if ( string.IsNullOrEmpty( source ) ) return source;

		var at = Find( source, name );

		if ( at < 0 ) return source;

		var open = source.IndexOf( '{', at );

		if ( open < 0 ) return source;

		var depth = 0;

		for ( var i = open; i < source.Length; i++ )
		{
			if ( source[i] == '{' ) depth++;
			else if ( source[i] == '}' && --depth == 0 )
			{
				var cut = source[at..( i + 1 )];
				var lines = new StringBuilder();

				foreach ( var c in cut )
				{
					if ( c == '\n' ) lines.Append( '\n' );
				}

				return source[..at] + lines + source[( i + 1 )..];
			}
		}

		return source;
	}

	//What is between an opener and its closer, for whoever reads a declaration out of one of these blocks.
	internal static string Inside( string text, char open, char close )
		=> Between( text, open, close );

	//Finds the block header as the first word of its own line, so mentions in comments are not matched
	private static int Find( string source, string name )
	{
		var at = 0;

		while ( true )
		{
			at = source.IndexOf( name, at, StringComparison.Ordinal );

			if ( at < 0 ) return -1;

			var lineStart = at == 0 ? 0 : source.LastIndexOf( '\n', at - 1 ) + 1;
			var lineEnd = source.IndexOf( '\n', at + name.Length );

			if ( lineEnd < 0 ) lineEnd = source.Length;

			var before = source.AsSpan( lineStart, at - lineStart ).Trim();
			var after = source.AsSpan( at + name.Length, lineEnd - at - name.Length ).Trim();

			if ( before.IsEmpty && ( after.IsEmpty || after[0] == '{' ) ) return at;

			at += name.Length;
		}
	}

	//What is between the first opener and its closer, or null.
	private static string Between( string text, char open, char close )
	{
		var from = text.IndexOf( open );

		if ( from < 0 ) return null;

		var to = open == close ? text.IndexOf( close, from + 1 ) : text.LastIndexOf( close );

		return to <= from ? null : text[( from + 1 )..to];
	}

	//Defines every declared feature, set or not, since an undefined define is unsafe across dialects
	internal static string Defines( Feature[] features, IReadOnlyDictionary<string, int> chosen )
	{
		if ( features is null || features.Length == 0 ) return "";

		var text = new StringBuilder();

		foreach ( var feature in features )
		{
			var value = feature.Default;

			if ( chosen is not null && chosen.TryGetValue( feature.Name, out var asked ) )
				value = feature.Hold( asked );

			text.Append( "#define " ).Append( feature.Name ).Append( ' ' )
				.Append( value ).Append( '\n' );
		}

		return text.ToString();
	}

	//Same, from a positional combo string, which is what draws carry for compact pipeline keys
	internal static string DefinesFor( Feature[] features, string combo )
	{
		if ( features is null || features.Length == 0 ) return "";

		var values = string.IsNullOrEmpty( combo )
			? Array.Empty<string>()
			: combo.Split( '.' );

		var text = new StringBuilder();

		for ( var i = 0; i < features.Length; i++ )
		{
			var feature = features[i];
			var value = feature.Default;

			if ( i < values.Length && int.TryParse( values[i], out var asked ) )
				value = feature.Hold( asked );

			text.Append( "#define " ).Append( feature.Name ).Append( ' ' )
				.Append( value ).Append( '\n' );
		}

		return text.ToString();
	}

	//Rewrites a combo with some features overridden, keeping the rest positionally
	internal static string With( Feature[] features, string combo, Func<string, int?> asked )
	{
		if ( features is null || features.Length == 0 || asked is null ) return combo;

		var values = string.IsNullOrEmpty( combo ) ? Array.Empty<string>() : combo.Split( '.' );
		var text = new StringBuilder();

		for ( var i = 0; i < features.Length; i++ )
		{
			var feature = features[i];
			var value = feature.Default;

			if ( i < values.Length && int.TryParse( values[i], out var was ) ) value = feature.Hold( was );
			if ( asked( feature.Name ) is { } now ) value = feature.Hold( now );

			if ( text.Length > 0 ) text.Append( '.' );

			text.Append( value );
		}

		return text.ToString();
	}

	//A compact combo key of the declared features' values, in declaration order
	internal static string Combo( Feature[] features, IReadOnlyDictionary<string, int> chosen )
	{
		if ( features is null || features.Length == 0 ) return "";

		var text = new StringBuilder();

		foreach ( var feature in features )
		{
			var value = feature.Default;

			if ( chosen is not null && chosen.TryGetValue( feature.Name, out var asked ) )
				value = feature.Hold( asked );

			if ( text.Length > 0 ) text.Append( '.' );

			text.Append( value );
		}

		return text.ToString();
	}
}
