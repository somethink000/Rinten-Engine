using Rinten.Engine;
using SkiaSharp;
using Topten.RichTextKit;

namespace Rinten;

//Not walked by a hotload: it holds Skia's faces and the engine's own watchers, nothing a swap could replace. Walking it
//rebuilt the loaded-font table in place, and text laid out meanwhile found no Inter and fell back to the system font.
[SkipHotload]
internal class FontManager : FontMapper, ICharacterMatcher
{
	public static FontManager Instance = new FontManager();

	static FontManager()
	{
		//Off Windows, try our loaded faces for missing characters first; the system often has none
		if ( !OperatingSystem.IsWindows() )
			FontFallback.CharacterMatcher = Instance;
	}

	/// <summary>The face for a character the style's own face doesn't have: the first loaded face that has it, then whatever the system's font manager finds.</summary>
	public SKTypeface MatchCharacter( string familyName, int weight, int width, SKFontStyleSlant slant, string[] bcp47, int character )
	{
		var faces = Faces();

		//The faces already open first, then the rest - opening each, which on a page is a download, only when
		//no open face has the character. What a face that is never opened costs is its line in the index.
		foreach ( var font in faces )
		{
			if ( font.IsOpen && font.Typeface is { } face && face.ContainsGlyph( character ) )
				return face;
		}

		if ( faces.Any( x => !x.IsOpen ) && SaidOpening( character ) )
			Log.Info( $"FontManager: no open face has U+{character:X4} ('{familyName}'), opening the rest to look" );

		foreach ( var font in faces )
		{
			if ( !font.IsOpen && font.Typeface is { } face && face.ContainsGlyph( character ) )
				return face;
		}

		return SKFontManager.Default.MatchCharacter( familyName, weight, width, slant, bcp47, character );
	}

	//The loaded faces as they stand, copied again only once Generation says they changed: asked for every character a fallback is looked for
	private Face[] Faces()
	{
		lock ( LoadedFonts )
		{
			if ( snapshot is null || snapshotGeneration != Generation )
			{
				snapshotGeneration = Generation;
				snapshot = LoadedFonts.Values.ToArray();
			}

			return snapshot;
		}
	}

	private Face[] snapshot;
	private int snapshotGeneration;

	//The characters a search of the unopened faces has been said for.
	private static readonly HashSet<int> saidOpening = new();

	private static bool SaidOpening( int character )
	{
		lock ( saidOpening ) return saidOpening.Add( character );
	}

	//Moves whenever a face is loaded or the loaded faces are cleared - what text measured
	//or drawn before was measured against no longer stands. See Rinten.UI.TextPictures.
	internal static int Generation;

	public IEnumerable<string> FontFamilies
	{
		get
		{
			lock ( LoadedFonts )
			{
				return LoadedFonts.Values.Select( x => x.Family ).Distinct().ToList();
			}
		}
	}

	//One face: what it is, and the face itself once something has asked for it. Read from an index where the build wrote one, and then the file
	//stays unread until text is set in this face - see FontIndex. Read at once where there is no index, and then it is open from the start.
	sealed class Face
	{
		public string Family;
		public int Weight;
		public int Width;
		public SKFontStyleSlant Slant;
		public bool IsMenu;

		/// <summary>Where to read it from, until it has been read.</summary>
		public BaseFileSystem FileSystem;
		public string Path;

		private SKTypeface typeface;
		private bool opened;
		private bool fetching;

		/// <summary>Whether the file is on its way - see <see cref="WebContent.Request"/>. The face is null until it lands.</summary>
		public bool IsPending => fetching && !opened;

		public bool IsItalic => Slant != SKFontStyleSlant.Upright;

		public bool IsOpen => opened;

		/// <summary>The face, read the first time it is asked for. Null where the file would not read.</summary>
		public SKTypeface Typeface
		{
			get
			{
				if ( opened ) return typeface;

				lock ( this )
				{
					if ( opened ) return typeface;

					//Not here yet: a page fetches a face when text first wants it, and the text is set in another
					//face until it lands - then the cache is dropped and the text is laid out again in this one.
					if ( !FileSystem.FileExists( Path ) )
					{
						if ( fetching ) return null;

						fetching = WebContent.Request( Path, Arrived );
						if ( fetching ) return null;
					}

					typeface = Open( FileSystem.OpenRead( Path ) );
					FileSystem = null;
					opened = true;

					if ( typeface is not null ) Log.Trace( $"Opened font {Family} weight {Weight} width {Width}" );
					return typeface;
				}
			}
		}

		private static void Arrived()
		{
			lock ( Instance.Cache )
			{
				Instance.Cache.Clear();
			}

			System.Threading.Interlocked.Increment( ref Generation );
		}

		/// <summary>A face already read.</summary>
		public static Face Opened( SKTypeface typeface, bool isMenu ) => new()
		{
			Family = typeface.FamilyName,
			Weight = typeface.FontWeight,
			Width = typeface.FontWidth,
			Slant = typeface.FontSlant,
			IsMenu = isMenu,
			typeface = typeface,
			opened = true,
		};

		public void Dispose()
		{
			if ( opened ) typeface?.Dispose();
			typeface = null;
		}
	}

	//Keyed on the face itself, width included: a condensed file and a normal one share a family, weight and slant, and whichever loaded first used to win.
	Dictionary<(string family, int weight, int width, SKFontStyleSlant slant), Face> LoadedFonts = new();
	Dictionary<(string family, int weight, bool italic), SKTypeface> Cache = new();

	//A face out of a font file's bytes. Null where they are not one.
	private static SKTypeface Open( System.IO.Stream stream )
	{
		if ( stream == null ) return null;

		using ( stream )
		{
			//Out of a copy of the bytes rather than off the stream, which Skia reads by calling back into managed code - see Image.Load.
			using var memory = new System.IO.MemoryStream();
			stream.CopyTo( memory );
			using var data = SKData.CreateCopy( memory.ToArray() );

			return SKTypeface.FromData( data );
		}
	}

	private void Load( System.IO.Stream stream )
	{
		var face = Open( stream );
		if ( face is null ) return;

		if ( !Add( Face.Opened( face, GlobalContext.Current == GlobalContext.Menu ) ) )
			face.Dispose();
	}

	//Takes in a face. False where one of the same family, weight, width and slant is
	//in already - the first file in wins, later copies of the same face are dropped.
	private bool Add( Face face )
	{
		var key = (face.Family, face.Weight, face.Width, face.Slant);

		lock ( LoadedFonts )
		{
			if ( LoadedFonts.ContainsKey( key ) )
				return false;

			LoadedFonts[key] = face;
			System.Threading.Interlocked.Increment( ref Generation );
		}

		return true;
	}

	List<FileWatch> watchers = new();

	public void LoadAll( BaseFileSystem fileSystem )
	{
		lock ( Cache )
		{
			//empty cache new fonts can replace fallbacks and best-matches
			Cache.Clear();
		}

		//A built game's fonts carry an index of what each file is, and are opened when text asks for them - see FontIndex.
		if ( fileSystem.FileExists( $"/fonts/{FontIndex.FileName}" ) )
		{
			var isMenu = GlobalContext.Current == GlobalContext.Menu;
			var entries = FontIndex.Read( fileSystem.ReadAllText( $"/fonts/{FontIndex.FileName}" ) );

			foreach ( var entry in entries )
			{
				Add( new Face
				{
					Family = entry.Family,
					Weight = entry.Weight,
					Width = entry.Width,
					Slant = entry.Slant,
					IsMenu = isMenu,
					FileSystem = fileSystem,
					Path = $"/fonts/{entry.Path}",
				} );
			}

			Log.Info( $"{entries.Count} fonts indexed, opened when used" );
			return;
		}

		var fontFiles = fileSystem.FindFile( "/fonts/", "*.ttf", true )
			.Union( fileSystem.FindFile( "/fonts/", "*.otf", true ) )
			.ToArray();

		var watch = System.Diagnostics.Stopwatch.StartNew();

		Parallel.ForEach( fontFiles, ( string font ) =>
		{
			Load( fileSystem.OpenRead( $"/fonts/{font}" ) );
		} );

		//Where there were any: this is called for each context that mounts fonts, and the ones with none have nothing to say about how long they took.
		if ( fontFiles.Length > 0 )
			Log.Trace( $"{fontFiles.Length} fonts in {watch.Elapsed.TotalMilliseconds:F0}ms" );

		//Load any new fonts
		var ttfWatch = fileSystem.Watch( $"*.ttf" );
		ttfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );
		var otfWatch = fileSystem.Watch( $"*.otf" );
		otfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );

		watchers.Add( ttfWatch );
		watchers.Add( otfWatch );
	}

	private void OnFontFilesChanged( FileWatch w, BaseFileSystem fs )
	{
		lock ( Cache )
		{
			Cache.Clear();
		}

		foreach ( var file in w.Changes )
		{
			Load( fs.OpenRead( file ) );
		}
	}

	//The closest face we have in the style's family: matching slant if there is one, then the
	//closest weight. Ties are settled by the faces themselves, never by the order they loaded in.
	private SKTypeface GetBestTypeface( IStyle style, out bool pending )
	{
		pending = false;
		Face chosen;

		lock ( LoadedFonts )
		{
			//Must be of same family
			var familyFonts = LoadedFonts.Values
			.Where( x => string.Equals( x.Family, style.FontFamily, StringComparison.OrdinalIgnoreCase ) );
			if ( !familyFonts.Any() ) return null;

			//Get matching slants, if no matching fallback to regular
			var slantFonts = familyFonts.Where( x => x.IsItalic == style.FontItalic );
			if ( slantFonts.Any() ) familyFonts = slantFonts;

			//Normal width beats condensed or expanded (Skia files those under the plain
			//family name), then the closest weight. The last two only make the order total.
			chosen = familyFonts
				.OrderBy( x => Math.Abs( x.Width - (int)SKFontStyleWidth.Normal ) )
				.ThenBy( x => WeightDistance( x.Weight, style.FontWeight ) )
				.ThenBy( x => x.Width )
				.ThenBy( x => x.Slant )
				.First();
		}

		//Chosen by what the faces are, and only this one read - outside the lock, because on a page reading it is a download.
		var face = chosen.Typeface;
		pending = face is null && chosen.IsPending;
		return face;
	}

	//A face to set text in while the one it asked for is still being fetched: one already open in the same family, then any open one. Null when none is.
	private SKTypeface Meanwhile( IStyle style )
	{
		lock ( LoadedFonts )
		{
			var open = LoadedFonts.Values.Where( x => x.IsOpen && x.Typeface is not null );

			return ( open.FirstOrDefault( x => string.Equals( x.Family, style.FontFamily, StringComparison.OrdinalIgnoreCase ) )
				?? open.FirstOrDefault() )?.Typeface;
		}
	}

	//How far a face's weight is from the one asked for. Ties go the way CSS settles them: heavier for 400 and anything above 500, lighter for the rest.
	static int WeightDistance( int weight, int wanted )
	{
		bool preferHeavier = wanted == 400 || wanted > 500;
		bool preferredSide = (weight > wanted) == preferHeavier;
		return Math.Abs( weight - wanted ) * 2 + (preferredSide ? 0 : 1);
	}

	public override SKTypeface TypefaceFromStyle( IStyle style, bool ignoreFontVariants )
	{
		var key = (style.FontFamily, style.FontWeight, style.FontItalic);

		lock ( Cache )
		{
			if ( Cache.TryGetValue( key, out var cachedFace ) ) return cachedFace;
		}

		var f = GetBestTypeface( style, out var pending );

		//The face is on its way: set the text in another meanwhile, and remember nothing - the next layout asks again, and gets it once it is here.
		if ( f is null && pending )
			return Meanwhile( style ) ?? Default.TypefaceFromStyle( style, ignoreFontVariants );

		//Fallback on system font
		if ( f is null )
		{
			//Warn once per missing family, and only if the system doesn't have it either
			if ( !string.IsNullOrEmpty( style.FontFamily ) && SaidMissing( style.FontFamily ) )
			{
				using var systemFonts = SKFontManager.Default.GetFontStyles( style.FontFamily );
				if ( systemFonts.Count == 0 )
					Log.Warning( $"FontManager: Font '{style.FontFamily}' not found" );
			}

			f = Default.TypefaceFromStyle( style, ignoreFontVariants );
		}

		lock ( Cache )
		{
			Cache[key] = f;
		}

		return f;
	}

	//The families already complained about.
	private static readonly HashSet<string> saidMissing = new( StringComparer.OrdinalIgnoreCase );

	//Whether this family has not been complained about yet, and remembers that it has been now.
	private static bool SaidMissing( string family )
	{
		lock ( saidMissing )
		{
			return saidMissing.Add( family ?? "" );
		}
	}

	public void Clear( bool removeMenu )
	{
		foreach ( var watcher in watchers )
		{
			watcher.Dispose();
		}
		watchers.Clear();

		lock ( LoadedFonts )
		{
			foreach ( var (key, font) in LoadedFonts.ToArray() )
			{
				if ( !removeMenu && font.IsMenu )
					continue;

				LoadedFonts.Remove( key );
				font.Dispose();
			}
		}

		lock ( Cache )
		{
			Cache.Clear();
		}

		System.Threading.Interlocked.Increment( ref Generation );
	}
}

