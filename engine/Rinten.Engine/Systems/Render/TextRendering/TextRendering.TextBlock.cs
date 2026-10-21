using Rinten.UI;
using SkiaSharp;

namespace Rinten;

public static partial class TextRendering
{
	//We'll expose this at some point, but will probably be as Rinten.TextBlock - and then need to think about ownership and caching
	internal class TextBlock : IDisposable
	{
		public Texture Texture;

		public TextFlag Flags;
		public Vector2 Clip;
		public bool IsEmpty;
		public Rendering.FilterMode FilterMode;
		internal int CacheKey;

		public RealTimeSince TimeSinceUsed;

		Scope _scope;
		Margin _effectMargin = default;

		//What it says and how - for a draw that rasterises it its own way, see CoreListDraws.Text.
		internal Scope Scope => _scope;

		public TextBlock( Scope scope, Vector2 clip, TextFlag flag )
		{
			Assert.False( Application.IsHeadless );

			Flags = flag;
			Clip = clip;

			Initialize( scope );
		}

		public TextBlock()
		{
		}

		internal void Initialize( Scope scope )
		{
			_scope = scope;
			IsEmpty = string.IsNullOrEmpty( _scope.Text );
			FilterMode = scope.FilterMode;
			TimeSinceUsed = 0;
			_effectMargin = default;

			if ( scope.Outline.Enabled && scope.Outline.Size > 0 )
			{
				_effectMargin.Left = MathF.Max( _effectMargin.Left, scope.Outline.Size ).CeilToInt();
				_effectMargin.Right = MathF.Max( _effectMargin.Left, scope.Outline.Size ).CeilToInt();
				_effectMargin.Top = MathF.Max( _effectMargin.Left, scope.Outline.Size ).CeilToInt();
				_effectMargin.Bottom = MathF.Max( _effectMargin.Left, scope.Outline.Size ).CeilToInt();
			}

			if ( scope.OutlineUnder.Enabled && scope.OutlineUnder.Size > 0 )
			{
				_effectMargin.Left = MathF.Max( _effectMargin.Left, scope.OutlineUnder.Size ).CeilToInt();
				_effectMargin.Right = MathF.Max( _effectMargin.Left, scope.OutlineUnder.Size ).CeilToInt();
				_effectMargin.Top = MathF.Max( _effectMargin.Left, scope.OutlineUnder.Size ).CeilToInt();
				_effectMargin.Bottom = MathF.Max( _effectMargin.Left, scope.OutlineUnder.Size ).CeilToInt();
			}

			if ( scope.Shadow.Enabled )
			{
				_effectMargin.Left = MathF.Max( _effectMargin.Left, scope.Shadow.Size + -scope.Shadow.Offset.x ).CeilToInt();
				_effectMargin.Right = MathF.Max( _effectMargin.Right, scope.Shadow.Size + scope.Shadow.Offset.x ).CeilToInt();
				_effectMargin.Top = MathF.Max( _effectMargin.Top, scope.Shadow.Size + -scope.Shadow.Offset.y ).CeilToInt();
				_effectMargin.Bottom = MathF.Max( _effectMargin.Bottom, scope.Shadow.Size + scope.Shadow.Offset.y ).CeilToInt();
			}

			if ( scope.ShadowUnder.Enabled )
			{
				_effectMargin.Left = MathF.Max( _effectMargin.Left, scope.ShadowUnder.Size + -scope.ShadowUnder.Offset.x ).CeilToInt();
				_effectMargin.Right = MathF.Max( _effectMargin.Right, scope.ShadowUnder.Size + scope.ShadowUnder.Offset.x ).CeilToInt();
				_effectMargin.Top = MathF.Max( _effectMargin.Top, scope.ShadowUnder.Size + -scope.ShadowUnder.Offset.y ).CeilToInt();
				_effectMargin.Bottom = MathF.Max( _effectMargin.Bottom, scope.ShadowUnder.Size + scope.ShadowUnder.Offset.y ).CeilToInt();
			}

			//don't let shit get crazy
			_effectMargin.Left = MathF.Min( _effectMargin.Left, 512 );
			_effectMargin.Right = MathF.Min( _effectMargin.Right, 512 );
			_effectMargin.Top = MathF.Min( _effectMargin.Top, 512 );
			_effectMargin.Bottom = MathF.Min( _effectMargin.Bottom, 512 );
		}

		public virtual void Dispose()
		{
			Texture?.Dispose();
			Texture = null;
		}

		Topten.RichTextKit.TextAlignment GetAlignment()
		{
			if ( Flags.Contains( TextFlag.Left ) ) return Topten.RichTextKit.TextAlignment.Left;
			if ( Flags.Contains( TextFlag.CenterHorizontally ) ) return Topten.RichTextKit.TextAlignment.Center;
			if ( Flags.Contains( TextFlag.Right ) ) return Topten.RichTextKit.TextAlignment.Right;

			return Topten.RichTextKit.TextAlignment.Left;
		}

		/// <summary>Where the text would land inside a rectangle, and how big it would be, without drawing it.</summary>
		public Rect Measure( Rect targetRect )
		{
			if ( IsEmpty ) return new Rect( targetRect.Position, 0 );

			var block = Build( targetRect );

			return targetRect.Align( new Vector2( block.MeasuredWidth, block.MeasuredHeight ), Flags );
		}

		//Whether shaped text is kept between frames. -noshapedtext is the control half of measuring what keeping it is worth.
		private static readonly bool KeepShaped =
			!Rinten.Utility.CommandLine.HasSwitch( "-noshapedtext" );

		//Shaped text is cached, keyed on text, style, flags and box size rounded up to whole pixels
		private sealed record Built( int Key, Topten.RichTextKit.TextBlock Block );
		private Built lastBuilt;

		//A block reused for one string after another (see Reuse) shapes into these, kept, rather than into new ones for the table
		bool reusing;
		Topten.RichTextKit.TextBlock own;
		Topten.RichTextKit.Style ownStyle;
		int ownKey;

		//Set up for another string in the same RichTextKit block: text drawn once needs no shaped-text table,
		//and a block a string would make its buffers anew each time
		internal void Reuse( in Scope scope, TextFlag flags )
		{
			reusing = true;
			Flags = flags;
			Initialize( scope );
		}

		private Topten.RichTextKit.TextBlock Build( Rect targetRect )
		{
			var clipped = !Flags.Contains( TextFlag.DontClip );

			var width = clipped ? MathF.Max( targetRect.Width, 0 ).CeilToInt() : -1;
			var height = clipped ? MathF.Max( targetRect.Height, 0 ).CeilToInt() : -1;

			var hash = new HashCode();
			hash.Add( _scope );
			hash.Add( Flags );
			hash.Add( width );
			hash.Add( height );

			var key = hash.ToHashCode();

			if ( reusing ) return Reshape( key, clipped, width, height );

			//This block's own last answer first: a measure and then a paint of the same text ask twice, and the shared
			//table below stops keeping anything once it is full - which is exactly when text is changing fastest.
			if ( lastBuilt is { } last && last.Key == key ) return last.Block;

			if ( KeepShaped && Shaped.TryGetValue( key, out var kept ) )
			{
				kept.SinceUsed = 0;
				return kept.Block;
			}

			var block = new Topten.RichTextKit.TextBlock();
			block.FontMapper = FontManager.Instance;

			block.Alignment = GetAlignment();

			if ( Flags.Contains( TextFlag.SingleLine ) ) //should we remove any newlines?
			{
				block.MaxLines = 1;
			}

			if ( clipped )
			{
				block.MaxWidth = width;
				block.MaxHeight = height;
			}

			var style = new Topten.RichTextKit.Style();
			_scope.ToStyle( style );

			block.AddText( _scope.Text, style );

			if ( KeepShaped && RoomForShaped ) Shaped[key] = new Shaping { Block = block };

			//One reference, written whole: a block asked from two threads never pairs one's key with the other's text.
			lastBuilt = new Built( key, block );

			return block;
		}

		private Topten.RichTextKit.TextBlock Reshape( int key, bool clipped, int width, int height )
		{
			if ( own is not null && ownKey == key ) return own;

			own ??= new Topten.RichTextKit.TextBlock { FontMapper = FontManager.Instance };
			own.Clear();

			own.Alignment = GetAlignment();
			own.MaxLines = Flags.Contains( TextFlag.SingleLine ) ? 1 : null;
			own.MaxWidth = clipped ? width : null;
			own.MaxHeight = clipped ? height : null;

			ownStyle ??= new Topten.RichTextKit.Style();
			ownStyle.ClearEffects();
			_scope.ToStyle( ownStyle );

			own.AddText( _scope.Text, ownStyle );
			ownKey = key;

			return own;
		}

		//The size of the picture MakeReady would make - the text, its negative letter spacing and the room its shadows and
		//outlines take - without making it. Shaped through Build, so a Render of the same block after it is not shaped again.
		internal Vector2 PictureSize() => PictureSize( new Rect( 0 ) );

		//The same, shaped in clip - as MakeReady shapes it in Clip.
		internal Vector2 PictureSize( Rect clip )
		{
			var margin = _effectMargin.EdgeSize;

			if ( IsEmpty ) return new Vector2( 2 + margin.x.CeilToInt(), 2 + margin.y.CeilToInt() );

			var block = Build( clip );

			int width = block.MeasuredWidth.CeilToInt().Clamp( 2, 4096 );
			int height = block.MeasuredHeight.CeilToInt().Clamp( 2, 4096 );

			var spacing = _scope.LetterSpacing;
			if ( spacing < 0 ) width += Math.Abs( (int)MathF.Floor( spacing ) );

			return new Vector2( width + margin.x.CeilToInt(), height + margin.y.CeilToInt() );
		}

		[ThreadStatic] static Topten.RichTextKit.TextPaintOptions paintOptions;

		public void Render( SKCanvas canvas, Rect targetRect )
		{
			if ( IsEmpty )
				return;

			var block = Build( targetRect );

			//One a thread, set for each paint - only these three are ever set on it
			var o = paintOptions ??= new Topten.RichTextKit.TextPaintOptions();
			o.Edging = _scope.FontSmooth == FontSmooth.Never ? SKFontEdging.Alias : SKFontEdging.SubpixelAntialias;
			o.Hinting = SKFontHinting.Normal;
			o.SubpixelPositioning = true;

			if ( !IsEmpty )
			{
				var rect = new Rect( 0, 0, block.MeasuredWidth, block.MeasuredHeight );
				rect = targetRect.Align( rect.Size, Flags );

				//Horizontal alignment is already done by the block, so only vertical alignment is applied here
				var left = block.MaxWidth.HasValue ? targetRect.Left : rect.Left;

				SKPoint drawPosition = new SKPoint( left, rect.Top );

				block.Paint( canvas, drawPosition, o );
			}
		}

		public void MakeReady()
		{
			TimeSinceUsed = 0;

			if ( Texture != null )
				return;

			//todo - we could probably expose shadows and outlines.. but later down the road.

			var block = new Topten.RichTextKit.TextBlock();
			block.FontMapper = FontManager.Instance;
			block.MaxWidth = Clip.x;
			block.MaxHeight = Clip.y;
			block.Alignment = GetAlignment();

			if ( Flags.Contains( TextFlag.SingleLine ) ) //should we remove any newlines?
			{
				block.MaxLines = 1;
			}

			if ( Flags.Contains( TextFlag.DontClip ) )
			{
				block.MaxWidth = null;
				block.MaxHeight = null;
			}

			//Build text block
			var style = new Topten.RichTextKit.Style();
			_scope.ToStyle( style );

			block.AddText( IsEmpty ? "." : _scope.Text, style );

			//Build Text

			var pad = block.MeasuredPadding;

			int width = block.MeasuredWidth.CeilToInt().Clamp( 2, 4096 );
			int height = block.MeasuredHeight.CeilToInt().Clamp( 2, 4096 );

			if ( style.LetterSpacing < 0 )
				width += Math.Abs( (int)MathF.Floor( style.LetterSpacing ) );

			var marginEdge = _effectMargin.EdgeSize;
			width += marginEdge.x.CeilToInt();
			height += marginEdge.y.CeilToInt();

			using ( var bitmap = new SkiaSharp.SKBitmap( width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul ) )
			using ( var canvas = new SkiaSharp.SKCanvas( bitmap ) )
			{
				var o = new Topten.RichTextKit.TextPaintOptions
				{
					Edging = _scope.FontSmooth switch
					{
						FontSmooth.Never => SKFontEdging.Alias,
						_ => SKFontEdging.Antialias,
					},

					Hinting = SKFontHinting.Full
				};

				canvas.Clear( style.TextColor.WithAlpha( 0 ) );

				if ( !IsEmpty )
				{
					SKPoint drawPosition = new SKPoint( _effectMargin.Left - pad.Left, _effectMargin.Top - pad.Top );

					block.Paint( canvas, drawPosition, o );
				}

				//Always use the max number of mips
				var mips = (int)MathF.Log2( MathF.Min( width, height ) ) + 1;
				mips = mips.Clamp( 1, 8 );

				Texture = Texture.Create( width, height, ImageFormat.BGRA8888 )
									.WithName( "textblock" )
									.WithData( bitmap.GetPixels(), width * height * bitmap.BytesPerPixel )
									.WithStaticUsage()
									.WithMips( mips )
									.Finish();
			}

			//Re-register so Tick() can evict this block again if it was evicted while a CommandList still held a reference and triggered a texture rebuild.
			if ( CacheKey != 0 ) Dictionary.TryAdd( CacheKey, this );
		}


	}
}
