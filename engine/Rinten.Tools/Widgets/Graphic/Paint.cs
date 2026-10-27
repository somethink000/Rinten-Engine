using Rinten.UI;
using Rinten.Utility;
using System;

namespace Editor
{
	public enum PenStyle
	{
		None,
		Solid,
		Dash,
		Dot,
		DashDot,
		DashDotDot
	}

	public enum ElideMode
	{
		Left,
		Right,
		Middle,
		None
	}

	/// <summary>How what is drawn is mixed with what is already there.</summary>
	public enum RenderMode
	{
		Normal,
		Plus,
		Multiply,
		Screen,
		Overlay,
		Darken,
		Lighten,
		ColorDodge,
		ColorBurn,
		HardLight,
		SoftLight,
		Difference,
		Exclusion,
	}

	/// <summary>The editor's painter: a brush, a pen and a font that every widget draws itself with.</summary>
	public static class Paint
	{
		internal static StateFlag state;

		struct FontInfo
		{
			public string FontName;
			public float FontSize;
			public int FontWeight;
			public bool Italic;

			/// <summary>Whether <see cref="FontSize"/> is pixels rather than points.</summary>
			public bool SizeInPixels;

			public void Reset()
			{
				FontName = null;
				FontSize = EditorText.SizeInPoints;
				FontWeight = 400;
				Italic = false;
				SizeInPixels = false;
			}
		}

		static FontInfo fontInfo;

		//What is being drawn into.
		internal static PaintCanvas canvas
		{
			get => painting ?? Scratch;
			set => painting = value;
		}

		private static PaintCanvas painting;
		private static PaintCanvas scratch;

		/// <summary>PaintInto and pixmap snapshots use legacy OnPaint, not panel children.</summary>
		public static bool Snapshot;

		private static PaintCanvas Scratch => scratch ??= new PaintCanvas( new Bitmap( 1, 1 ) );

		public static Rect LocalRect { get; set; }

		public static bool Antialiasing
		{
			set => canvas.Bitmap.SetAntialias( value );
		}

		public static bool TextAntialiasing
		{
			//Text goes through the same antialiasing as everything else.
			set { }
		}

		public static bool BilinearFiltering
		{
			set { }
		}

		//Where things land: the canvas keeps a stack of transforms, as the painter did.

		public static void Push() => canvas.Bitmap.Push();

		public static void Pop() => canvas.Bitmap.Pop();

		public static void Translate( Vector2 tx ) => canvas.Bitmap.TranslateBy( tx );

		public static void Scale( float x, float y ) => canvas.Bitmap.ScaleBy( x, y );

		public static void Scale( float scale ) => canvas.Bitmap.ScaleBy( scale, scale );

		public static void Rotate( float degrees ) => canvas.Bitmap.RotateBy( degrees );

		public static void Rotate( float degrees, Vector2 center ) => canvas.Bitmap.RotateBy( degrees, center );

		//Back to the widget's own units, which are the bitmap's pixels scaled by the display
		public static void ResetTransform()
		{
			canvas.Bitmap.ResetTransform();
			canvas.Bitmap.ScaleBy( _dpiScale, _dpiScale );
		}

		public static void ClipRect( Rect rect ) => canvas.Bitmap.ClipRect( rect );

		//The shapes. Each is filled with the brush and outlined with the pen.

		public static void DrawRect( in Rect rect, float borderRadius ) => canvas.Rect( rect, borderRadius );

		//What tabs are drawn with: round where they show, square where they run into the panel below them.
		/// <summary>A rectangle with a radius per corner, clockwise from the top left.</summary>
		public static void DrawRect( in Rect rect, float topLeft, float topRight, float bottomRight, float bottomLeft )
			=> canvas.Rect( rect, topLeft, topRight, bottomRight, bottomLeft );

		public static void DrawRect( in Rect rect ) => canvas.Rect( rect );

		public static void DrawCircle( in Rect rect ) => canvas.Circle( rect );

		public static void DrawCircle( Vector2 position, Vector2 scale )
			=> DrawCircle( new Rect( position - scale * 0.5f, scale ) );

		public static void DrawArc( Vector2 center, Vector2 radius, float angle, float angleSize )
			=> canvas.Arc( new Rect( center - radius * 0.5f, radius ), angle, angleSize, pie: false );

		public static void DrawPie( Vector2 center, Vector2 radius, float angle, float angleSize )
			=> canvas.Arc( new Rect( center - radius * 0.5f, radius ), angle, angleSize, pie: true );

		public static void DrawSquare( Vector2 position, Vector2 scale )
			=> DrawRect( new Rect( position - scale * 0.5f, scale ) );

		public static void DrawPolygon( IEnumerable<Vector2> points ) => canvas.Polygon( points.ToArray() );

		public static void DrawPolygon( params Vector2[] points ) => canvas.Polygon( points );

		public static void DrawLine( IEnumerable<Vector2> points ) => canvas.Lines( points.ToArray() );

		public static void DrawLine( in Vector2 from, in Vector2 to ) => canvas.Line( from, to );

		public static void DrawPoints( IEnumerable<Vector2> points )
		{
			//A point is the shortest line there is.
			foreach ( var point in points ) canvas.Line( point, point );
		}

		public static void DrawArrow( Vector2 p1, Vector2 p2, float width )
		{
			var delta = (p1 - p2);
			var left = delta.Perpendicular.Normal;

			var a = p1 + left * width * 0.5f;
			var b = p1 - left * width * 0.5f;
			var c = p2;

			DrawPolygon( a, b, c );
		}

		//Words.

		public static Rect DrawText( in Vector2 position, string text )
		{
			return DrawText( new Rect( position ) { Width = 1000, Height = 1000 }, text, TextFlag.Left | TextFlag.Top );
		}

		public static Rect DrawText( in Rect position, string text, TextFlag flags = TextFlag.Center )
			=> canvas.Text( position, text, flags );

		/// <summary>Adds required ellipses to a string if it doesn't fit within the width</summary>
		public static string GetElidedText( string text, float width, ElideMode mode = ElideMode.Right, TextFlag flags = TextFlag.Center )
		{
			if ( string.IsNullOrEmpty( text ) ) return text;
			if ( MeasureText( text ).x <= width ) return text;

			const string ellipsis = "…";

			//Cut a character at a time from whichever end the mode asks for, until what is left plus the dots fits the room we have.
			for ( var keep = text.Length - 1; keep > 0; keep-- )
			{
				var cut = mode switch
				{
					ElideMode.Left => ellipsis + text[^keep..],
					ElideMode.Middle => text[..( ( keep + 1 ) / 2 )] + ellipsis + text[^( keep / 2 )..],
					_ => text[..keep] + ellipsis
				};

				if ( MeasureText( cut ).x <= width ) return cut;
			}

			return ellipsis;
		}

		public static Rect MeasureText( in Rect position, string text, TextFlag flags = TextFlag.Center )
			=> canvas.MeasureText( position, text, flags );

		public static Vector2 MeasureText( string text )
			=> canvas.MeasureText( new Rect( 0, 4096 ), text, TextFlag.LeftTop ).Size;

		/// <summary>The font everything drawn after this is drawn in.</summary>
		public static void SetFont( string name, float size = EditorText.SizeInPoints, int weight = 400,
			bool italic = false, bool sizeInPixels = false )
		{
			fontInfo.FontName = name;
			fontInfo.FontSize = size;
			fontInfo.FontWeight = weight;
			fontInfo.Italic = italic;
			fontInfo.SizeInPixels = sizeInPixels;

			//Weight passed as-is in CSS hundreds (400, 700), not the legacy 0-99 scale
			canvas.SetFont( name, sizeInPixels ? size : size * (96.0f / 72.0f), weight, italic );
		}

		public static void SetDefaultFont( float size = EditorText.SizeInPoints, int weight = 400,
			bool italic = false, bool sizeInPixels = false )
		{
			SetFont( Theme.DefaultFont, size, weight, italic, sizeInPixels );
		}

		public static void SetHeadingFont( float size = 15, int weight = 400, bool italic = false, bool sizeInPixels = false )
		{
			SetFont( Theme.DefaultFont, size, weight, italic, sizeInPixels );
		}

		//The pen and the brush.

		public static void ClearPen()
		{
			_pen = Color.Transparent;

			canvas.ClearPen();
		}

		public static void ClearBrush() => canvas.ClearBrush();

		static float _dpiScale = 1.0f;
		static Color _pen = Color.White;

		public static Color Pen
		{
			get => _pen;
			set
			{
				if ( _pen == value ) return;

				_pen = value;
				ApplyPen();
			}
		}

		static float _penSize = 0.0f;

		public static float PenSize
		{
			get => _penSize;
			set
			{
				if ( _penSize == value ) return;

				_penSize = value;
				ApplyPen();
			}
		}

		static PenStyle _penStyle = PenStyle.Solid;

		public static PenStyle PenStyle
		{
			get => _penStyle;
			set
			{
				if ( _penStyle == value ) return;

				_penStyle = value;
				ApplyPen();
			}
		}

		static void ApplyPen()
		{
			if ( Pen.a <= 0.001f )
			{
				canvas.ClearPen();
				return;
			}

			canvas.SetPen( Pen, PenSize, PenStyle );
		}

		/// <summary>Set the pen and font style from a style</summary>
		public static void SetFont( in Styles style )
		{
			Paint.SetBrushAndPen( Color.Transparent, style.FontColor ?? Color.White );

			//A stylesheet's font-size is pixels. Handed over as points it came out a third bigger than the sheet asked for.
			Paint.SetFont( style.FontFamily ?? Theme.DefaultFont,
				style.FontSize?.GetPixels( 0 ) ?? EditorText.Size, style.FontWeight ?? 400, sizeInPixels: true );
		}

		/// <summary>Draw a rectangle using the background of a style</summary>
		public static void Rect( in Styles styles, in Rect rect )
		{
			Paint.Antialiasing = true;

			if ( styles.HasBorder )
			{
				Paint.SetPen( styles.BorderTopColor ?? Color.White, styles.BorderTopWidth?.GetPixels( 1024 ) ?? 0 );
			}
			else
			{
				Paint.ClearPen();
			}

			if ( styles.BackgroundColor?.a > 0 )
			{
				var bgRect = rect.Shrink( Paint.PenSize * 0.5f );
				Paint.SetBrush( styles.BackgroundColor.Value );
				Paint.DrawRect( bgRect );
			}
		}

		public static void SetPen( in Color color, in float size = 0.0f, in PenStyle style = PenStyle.Solid )
		{
			ThreadSafe.AssertIsMainThread();

			if ( color.a <= 0 )
			{
				ClearPen();
				return;
			}

			Pen = color;
			PenSize = size;
			PenStyle = style;
		}

		public static void SetBrush( in Color color )
		{
			ThreadSafe.AssertIsMainThread();

			if ( color.a <= 0 )
			{
				ClearBrush();
				return;
			}

			canvas.SetBrush( color );
		}

		public static void SetBrushAndPen( Color brushColor, Color penColor, float penSize = 1.0f, PenStyle style = PenStyle.Solid )
		{
			ThreadSafe.AssertIsMainThread();

			SetBrush( brushColor );
			SetPen( penColor, penSize, style );
		}

		public static void SetBrushAndPen( Color brushColor )
		{
			ThreadSafe.AssertIsMainThread();

			SetBrush( brushColor );
			ClearPen();
		}

		public static void SetBrushLinear( Vector2 a_pos, Vector2 b_pos, Color a_color, Color b_color )
			=> canvas.SetBrushLinear( a_pos, b_pos, a_color, b_color );

		public static void SetBrushRadial( Vector2 center, float radius, Color a_color, Color b_color )
			=> canvas.SetBrushRadial( center, radius, 0, a_color, 1, b_color );

		public static void SetBrushRadial( Vector2 center, float radius, float a, Color a_color, float b, Color b_color )
			=> canvas.SetBrushRadial( center, radius, a, a_color, b, b_color );

		//Pictures.

		static Dictionary<string, Pixmap> Images = new();

		public static Pixmap LoadImage( string filename )
		{
			if ( string.IsNullOrWhiteSpace( filename ) )
				filename = "missing.png";

			if ( Images.TryGetValue( filename, out var pixmap ) )
				return pixmap;

			if ( filename.StartsWith( "https://" ) || filename.StartsWith( "http://" ) )
			{
				var t = DownloadToPixmap( filename );
				if ( t.IsCompletedSuccessfully )
				{
					pixmap = t.Result;
				}
				else
				{
					pixmap = new Pixmap( 1, 1 );
					pixmap.Clear( Color.Transparent );
				}

				Images[filename] = pixmap;
			}
			else
			{
				pixmap = Pixmap.FromFile( filename );
				Images[filename] = pixmap;
			}

			return pixmap;
		}

		public static void ForgetImage( string filename )
		{
			if ( string.IsNullOrWhiteSpace( filename ) )
				return;

			List<string> keys = null;

			foreach ( var key in Images.Keys )
			{
				if ( key != filename && !key.StartsWith( filename + ";", StringComparison.Ordinal ) )
					continue;

				keys ??= new();
				keys.Add( key );
			}

			if ( keys is null )
				return;

			foreach ( var key in keys )
			{
				if ( Images.Remove( key, out var pixmap ) )
					pixmap?.Release();
			}
		}

		public static Pixmap LoadImage( string filename, int x, int y )
		{
			var cachedName = $"{filename};{x};{y}";

			if ( Images.TryGetValue( cachedName, out var pixmap ) )
				return pixmap;

			pixmap = LoadImage( filename );
			if ( pixmap is null || x == 0 || y == 0 ) return pixmap;
			if ( pixmap.Width <= 1 || pixmap.Height <= 1 ) return pixmap;

			pixmap = pixmap.Resize( x, y );
			if ( pixmap is not null )
				Images[cachedName] = pixmap;

			return pixmap;
		}

		static async ValueTask<Pixmap> DownloadToPixmap( string filename )
		{
			var crcName = Rinten.Utility.Crc64.FromString( filename.ToLower() ).ToString( "x" );
			var targetPath = FileSystem.WebCache.GetFullPath( "/" ) + $"/{crcName}";

			if ( !FileSystem.WebCache.FileExists( $"/{crcName}" ) )
			{
				await EditorUtility.DownloadAsync( filename, targetPath, default );

				//We downloaded an image successfully, lets redraw the window next frame so it gets drawn
				EditorUtility.RedrawActiveWindow();
			}

			var pixmap = Pixmap.FromFile( targetPath );
			Images[filename] = pixmap;
			return pixmap;
		}

		public static void SetBrush( string image )
		{
			var pixmap = LoadImage( image );

			if ( pixmap != null )
				SetBrush( pixmap );
		}

		//Nothing fills a shape with a picture yet - what wants one draws it with Draw instead - so this is remembered and nothing else.
		/// <summary>A picture as the brush.</summary>
		public static void SetBrush( Pixmap pixmap )
		{
			Assert.NotNull( pixmap );

			BrushImage = pixmap;
		}

		//The picture set as the brush, if one was.
		internal static Pixmap BrushImage { get; private set; }

		public static bool HasSelected => state.Contains( StateFlag.Selected );
		public static bool HasMouseOver => state.Contains( StateFlag.MouseOver );
		public static bool HasPressed => state.Contains( StateFlag.Sunken );
		public static bool HasFocus => state.Contains( StateFlag.HasFocus );
		public static bool HasEnabled => state.Contains( StateFlag.Enabled );

		public static void SetFlags( bool selected, bool mouseOver, bool pressed, bool focused, bool enabled )
		{
			state = 0;
			if ( selected ) state |= StateFlag.Selected;
			if ( mouseOver ) state |= StateFlag.MouseOver;
			if ( pressed ) state |= StateFlag.Sunken;
			if ( focused ) state |= StateFlag.HasFocus;
			if ( enabled ) state |= StateFlag.Enabled;
		}

		//Starts a paint: everything drawn until this is disposed of goes into this canvas, in this state. Whatever was being painted
		//before goes back afterwards, because a widget can be asked to paint itself in the middle of another one doing the same.
		internal static IDisposable Start( PaintCanvas surface, StateFlag flags = StateFlag.None, float scale = 1.0f )
		{
			ThreadSafe.AssertIsMainThread();

			var oldCanvas = painting;
			var oldState = state;
			var oldPen = _pen;
			var oldDpiScale = _dpiScale;

			painting = surface;
			state = flags;
			_pen = Color.White;
			_dpiScale = scale;

			fontInfo.Reset();

			return DisposeAction.Create( () =>
			{
				ThreadSafe.AssertIsMainThread();

				painting = oldCanvas;
				state = oldState;
				_pen = oldPen;
				_dpiScale = oldDpiScale;
			} );
		}

		/// <summary>An icon in the pen's colour, whichever kind of icon it is.</summary>
		public static Rect DrawMonoIcon( Rect rect, string iconName, float pixelHeight, TextFlag alignment = TextFlag.Center )
		{
			if ( string.IsNullOrEmpty( iconName ) ) return rect;

			//A glyph already comes out in the pen's colour.
			if ( !iconName.Contains( '.' ) ) return DrawIcon( rect, iconName, pixelHeight, alignment );

			var where = rect.Align( new Vector2( pixelHeight, pixelHeight ), alignment );
			var wanted = (int)( pixelHeight * _dpiScale );

			var loaded = LoadImage( iconName, wanted, wanted );
			if ( loaded is null ) return where;

			//Kept, because this runs every paint of every button on the strip and recolouring is a bitmap copy and a blend over it.
			var key = ( iconName, wanted, (Color32)Pen.WithAlpha( 1.0f ) );

			if ( !monoIcons.TryGetValue( key, out var tinted ) || !tinted.IsValid() )
			{
				//A cap rather than a sweep: this is one entry per icon per size per colour, and an editor has a few dozen icons and two colours.
				if ( monoIcons.Count > 512 ) monoIcons.Clear();

				tinted = loaded.Recoloured( Pen.WithAlpha( 1.0f ) );
				monoIcons[key] = tinted;
			}

			Draw( where, tinted, Pen.a );

			return where;
		}

		//Recoloured icons, by name, size and colour.
		private static readonly Dictionary<(string, int, Color32), Pixmap> monoIcons = new();

		public static Rect DrawIcon( Rect rect, string iconName, float pixelHeight, TextFlag alignment = TextFlag.Center )
		{
			if ( string.IsNullOrEmpty( iconName ) )
				return rect;

			if ( iconName.Contains( '.' ) )
			{
				var innerRect = rect.Align( new Vector2( pixelHeight, pixelHeight ), alignment );

				Draw( innerRect, iconName, Pen.a );
				return innerRect;
			}

			//save and restore the font
			var of = fontInfo;

			try
			{
				Paint.SetFont( "Material Icons", pixelHeight, 400, sizeInPixels: true );
				return Paint.DrawText( rect, iconName?.ToLower(), alignment );
			}
			finally
			{
				Paint.SetFont( of.FontName, of.FontSize, of.FontWeight, of.Italic, of.SizeInPixels );
			}
		}

		public static void Draw( Rect r, Pixmap pixmap, float alpha = 1.0f, float borderRadius = 0 )
		{
			if ( pixmap is null ) return;

			//The alpha was taken and thrown away before, so every caller asking for a faded picture got a solid one.
			canvas.Image( r, pixmap, alpha );
		}

		public static void DrawTiled( Rect r, Pixmap pixmap, Vector2 origin, Vector2 scale, float alpha = 1.0f )
		{
			if ( pixmap is null ) return;

			canvas.ImageTiled( r, pixmap, origin, scale, alpha );
		}

		public static void Draw( Rect r, string image, float alpha = 1.0f, float borderRadius = 0 )
		{
			//find the image, and resize it to this size to make it nice
			var pixmap = LoadImage( image, (int)(r.Size.x * _dpiScale), (int)(r.Size.y * _dpiScale) );

			Draw( r, pixmap, alpha, borderRadius );
		}

		/// <summary>Draws into a picture rather than into a widget - a thumbnail, or an icon somebody is building.</summary>
		public static IDisposable ToPixmap( Pixmap pixmap )
		{
			ThreadSafe.AssertIsMainThread();

			var target = pixmap.picture ?? new Bitmap( Math.Max( pixmap.Width, 1 ), Math.Max( pixmap.Height, 1 ) );

			var scope = Start( new PaintCanvas( target ), StateFlag.None );

			LocalRect = new Rect( 0, pixmap.Size );

			return scope;
		}

		/// <summary>How what is drawn mixes with what is under it. Only the ordinary way round is drawn so far.</summary>
		public static RenderMode RenderMode
		{
			set { }
		}

		public static Rect DrawTextBox( Rect position, string text, Color textColor, Margin padding, float borderRadius, TextFlag flag )
		{
			//invalid
			if ( position.Left >= position.Right )
				return position;

			var textRect = Paint.MeasureText( position, text, flag );

			Paint.DrawRect( textRect.Grow( padding ), borderRadius );

			Paint.SetPen( textColor );
			Paint.DrawText( position, text, flag );

			return textRect;
		}
	}

	internal enum StateFlag
	{
		None = 0x00000000,
		Enabled = 0x00000001,
		Raised = 0x00000002,
		Sunken = 0x00000004,
		Off = 0x00000008,
		NoChange = 0x00000010,
		On = 0x00000020,
		DownArrow = 0x00000040,
		Horizontal = 0x00000080,
		HasFocus = 0x00000100,
		Top = 0x00000200,
		Bottom = 0x00000400,
		FocusAtBorder = 0x00000800,
		AutoRaise = 0x00001000,
		MouseOver = 0x00002000,
		UpArrow = 0x00004000,
		Selected = 0x00008000,
		Active = 0x00010000,
		Window = 0x00020000,
		Open = 0x00040000,
		Children = 0x00080000,
		Item = 0x00100000,
		Sibling = 0x00200000,
		Editing = 0x00400000,
		KeyboardFocusChange = 0x00800000,
		HasEditFocus = 0x01000000,
		ReadOnly = 0x02000000,
		Small = 0x04000000,
		Mini = 0x08000000
	};
}
