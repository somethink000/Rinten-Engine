using Rinten.Html;
using Rinten.Rendering;
using SkiaSharp;
using System.Buffers;
using Topten.RichTextKit;

namespace Rinten.UI;

internal sealed class TextBlock : IDisposable
{
	[ConVar( ConVarFlags.Protected, Help = "Enable rendering text to textures" )]
	public static bool ui_rendertext { get; set; } = true;

	public Action OnTextureChanged { get; set; }

	public string Text { get; internal set; }

	public bool ShouldDrawSelection = false;
	public int SelectionStart { get; internal set; } = 0;
	public int SelectionEnd { get; internal set; } = 0;
	public Color SelectionColor { get; set; } = Color.Cyan.WithAlpha( 0.39f );
	public bool IsTruncated { get; internal set; }
	public bool IsHtml { get; internal set; }

	//What was measured the other way goes when this changes. It is not part of the font hash that decides a rebuild, and the sizes are kept per width - every one of them worked out under the answer this used to give.
	/// <summary>Whether the panel this belongs to says the text stays on one line.</summary>
	public bool NoWrap
	{
		get;
		internal set
		{
			if ( field == value ) return;

			field = value;

			SizeCache?.Clear();
		}
	}

	public Func<INode, Styles> LookupStyles { get; set; }

	public Vector2 BlockSize;

	internal Texture Texture;

	//we keep the last texture around incase we can re-use it
	Texture LastTexture;

	//The shared picture Texture is, while it is one - see TextPictures.
	TextPictures.Picture held;

	/// <summary>Whether the text texture has mips, for world panels drawn at varying sizes</summary>
	public bool Mipped
	{
		get;
		internal set
		{
			if ( field == value ) return;

			//Let go of under the old answer, which is the one that says how it was made.
			ReleaseTexture();
			DisposeLast();

			field = value;
		}
	}

	//The text the block was last built from, which is what its sizes and pictures are kept under.
	string builtText;

	//Whether this block's sizes and picture are shared by what decides them. Not markup: that also depends on stylesheets.
	bool Shareable => TextPictures.Enabled && !IsHtml && builtText is not null;

	//The key a size or picture of this block at this room is kept under.
	TextPictures.Key KeyAt( float width, float height )
	{
		var overflow = TextOverflow != TextOverflow.None;
		//Rounded up, as the block is laid out: at CeilToInt( width ) + 1.
		var w = float.IsNaN( width ) || ( DoesNotWrap && !overflow ) ? int.MinValue : width.CeilToInt();
		var h = overflow && !float.IsNaN( height ) ? height.CeilToInt() : int.MinValue;

		return new TextPictures.Key( builtText, FontHash, DoesNotWrap, w, h, Mipped );
	}

	internal void SetText( string text )
	{
		Text = text;
		IsHtml = false;
	}

	Rinten.Html.Node htmlNode;

	internal void SetHtml( string text )
	{
		if ( IsHtml && Text == text ) return;

		Text = text;
		IsHtml = true;

		try
		{
			htmlNode = default;
			htmlNode = Rinten.Html.Node.Parse( Text );
		}
		catch { }
	}

	public void Dirty()
	{
		FontHash = default;
	}

	Topten.RichTextKit.TextBlock Block;
	Topten.RichTextKit.Style Style;
	Topten.RichTextKit.TextGradient Gradient;

	int FontHash;

	//The style the Style object was last set up for - see UpdateStyles
	int builtStyleHash;
	//int ParentHash;

	float FontSize;
	int? FontWeight;
	TextAlign TextAlign;
	TextOverflow TextOverflow;
	FilterMode TextFilter;
	TextDecoration TextDecoration;
	FontStyle FontStyle;
	FontVariantNumeric? FontVariantNumeric;
	WordBreak WordBreak;
	TextTransform? TextTransform;
	Length? LetterSpacing;
	Length? WordSpacing;
	Length? LineHeight;
	Align AlignItems;
	WhiteSpace? WhiteSpace;
	GradientInfo GradientInfo;
	FontSmooth Smooth;

	Margin EffectMargin;

	//Whether this block stays on one line - said by the panel, or by the sheet (nowrap, or pre).
	bool DoesNotWrap => NoWrap || WhiteSpace is UI.WhiteSpace.NoWrap or UI.WhiteSpace.Pre;

	Dictionary<int, Vector2> SizeCache = new Dictionary<int, Vector2>();

	//The block's text ends with a line break. Checked on the collapsed text rather than Text, since
	//white-space collapsing can strip a trailing newline and leave the block with no line for it.
	bool EndsWithNewline;

	static bool EndsWithLineBreak( string text ) => text is { Length: > 0 } && text[^1] is '\n' or '\u2029';

	/// <summary>Where the first line's baseline is, down from the top of the text laid out to this width: what a row that lines its items up on a baseline asks for. A block with no lines answers with its bottom, as a box with no text does.</summary>
	public float FirstBaseline( float width, float height )
	{
		Measure( width, height );

		if ( !float.IsNaN( width ) ) width = width.CeilToInt();
		Block.MaxWidth = DoesNotWrap || float.IsNaN( width ) ? null : (width + 1);

		var lines = Block.Lines;
		if ( lines.Count == 0 ) return Block.MeasuredHeight.CeilToInt();

		return lines[0].YCoord + lines[0].BaseLine;
	}

	public Vector2 Measure( float width, float height )
	{
		if ( !float.IsNaN( width ) ) width = width.CeilToInt();
		if ( !float.IsNaN( height ) ) height = height.CeilToInt();

		var hash = (int)width;
		if ( SizeCache.TryGetValue( hash, out var size ) )
			return size;

		//Measured by some other block showing the same text in the same style at the same room: the size is theirs,
		//and the block is left unlaid - what reads its lines lays it out when it asks, at the width set here.
		var shareable = Shareable;
		var key = shareable ? KeyAt( width, height ) : default;

		if ( shareable && TextPictures.TryGetSize( key, out size ) )
		{
			Block.MaxWidth = DoesNotWrap || float.IsNaN( width ) ? null : (width + 1);
			if ( TextOverflow != TextOverflow.None ) Block.MaxHeight = float.IsNaN( height ) ? null : (height + 1);

			SizeCache[hash] = size;
			TextStats.SizeHit();
			return size;
		}

		var measureStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var measureAllocated = TextStats.Allocated;

		//Measured at its own width when it does not wrap, so the room it is given is not also the width it breaks itself into lines
		//at - a field with less room than its text is one line that runs past the edge, not several that make the field taller.
		Block.MaxWidth = DoesNotWrap || float.IsNaN( width ) ? null : (width + 1);

		if ( TextOverflow != TextOverflow.None )
		{
			Block.MaxHeight = float.IsNaN( height ) ? null : (height + 1);
		}

		var s = new Vector2( Block.MeasuredWidth.CeilToInt(), Block.MeasuredHeight.CeilToInt() );

		SizeCache[hash] = s;
		if ( shareable ) TextPictures.PutSize( key, s );

		TextStats.MeasureMiss( System.Diagnostics.Stopwatch.GetTimestamp() - measureStart );
		TextStats.Garbage( TextStats.Allocated - measureAllocated, label: true );

		return s;
	}

	void WaitTextureReady()
	{
		if ( TextureRebuild == null ) return;
		using var perfScope = Performance.Scope( "TextBlock.WaitRebuild" );
		TextureRebuild.Wait();
		TextureRebuild = null;
	}

	//The finished text texture, or null; for renderers drawing the text directly, without descriptors
	internal Texture FinishedTexture()
	{
		WaitTextureReady();

		return Texture;
	}

	//How much room the picture keeps around the text for what spills out of it - shadows, outlines - which the text's own rect knows nothing about.
	internal Vector2 TextureMargin => EffectMargin.Position;

	//Build a text descriptor into the target RenderLayer.
	internal void BuildDescriptors( RenderLayer target, BlendMode blendMode, Styles currentStyle, Rect textrect, float opacity )
	{
		WaitTextureReady();

		if ( Texture is null ) return;
		if ( BlockSize == 0 ) return;

		if ( currentStyle.TextAlign == TextAlign.Center )
		{
			textrect.Left += (textrect.Width - BlockSize.x) * 0.5f;
		}
		else if ( currentStyle.TextAlign == TextAlign.Right )
		{
			textrect.Left = textrect.Right - BlockSize.x;
		}

		if ( currentStyle.AlignItems == Align.Center )
		{
			textrect.Top += (textrect.Height - BlockSize.y) * 0.5f;
		}
		else if ( currentStyle.AlignItems == Align.FlexEnd )
		{
			textrect.Top = textrect.Bottom - BlockSize.y;
		}

		textrect.Size = Texture.Size;
		textrect.Position -= EffectMargin.Position;

		var color = Color.White;
		color.a *= opacity;

		if ( color.a <= 0 ) return;

		var rect = textrect.Floor();

		var desc = new BoxDrawDescriptor( rect, new Color( 0, 0, 0, 0 ) )
		{
			BackgroundImage = Texture,
			BackgroundRect = new Vector4( 0, 0, rect.Width, rect.Height ),
			BackgroundTint = color,
			OverrideBlendMode = blendMode == BlendMode.Normal ? BlendMode.PremultipliedAlpha : blendMode,
			PremultiplyAlpha = true,
			FilterMode = TextFilter,
		};

		target.AddBox( desc );
	}



	public Rect CaretRect( int caretPosition )
	{
		var codepoint = CaretToCodePointIndex( caretPosition );

		//Skias caret includes newlines however for rendering, we don't want this It also appears AltPosition is absolutely fucked and changes nothing
		var cp = new CaretPosition { AltPosition = false, CodePointIndex = codepoint };
		var pos = Block.GetCaretInfo( cp );

		float xPosition = pos.CaretRectangle.Left;
		float yPosition = pos.CaretRectangle.Top;

		if ( codepoint > 0 && codepoint == Block.Length && EndsWithNewline && pos.LineIndex < Block.Lines.Count )
		{
			xPosition = 0;
			yPosition += Block.Lines[pos.LineIndex].Height;
		}

		return new Rect( xPosition, yPosition, pos.CaretRectangle.Width, pos.CaretRectangle.Height );
	}

	public int GetLetterAt( Vector2 pos )
	{
		if ( Block == null ) return -1;

		var result = Block.HitTest( pos.x, pos.y );

		return Block.LookupCaretIndex( result.ClosestCodePointIndex );
	}

	public HtmlSpan GetSpanAt( Vector2 pos )
	{
		if ( Block == null ) return default;
		if ( HtmlSpans is null ) return default;

		var result = Block.HitTest( pos.x, pos.y );
		return HtmlSpans.Where( x => x.from <= result.OverCodePointIndex && x.to > result.OverCodePointIndex ).FirstOrDefault();
	}

	public bool UpdateStyles( Styles style )
	{
		//Inter ships with the project, so it always resolves; it is also the editor theme's font
		var fontFamily = style.FontFamily ?? "Inter";
		var fontColor = style.FontColor ?? Color.Black;
		var fontSize = style.FontSize ?? Length.Pixels( 13 ).Value;

		FontSize = fontSize.GetPixels( 100 ); //this should probably be screen height for font length?
		FontSize = MathF.Round( FontSize * 32.0f ) / 32.0f; //round the font size so we're not redrawing for no reason on font size lerp
		FontWeight = style.FontWeight;
		TextAlign = style.TextAlign.Value;
		TextDecoration = style.TextDecorationLine.Value;
		FontStyle = style.FontStyle.Value;
		FontVariantNumeric = style.FontVariantNumeric;
		AlignItems = style.AlignItems.Value;
		LetterSpacing = style.LetterSpacing;
		WordSpacing = style.WordSpacing;
		LineHeight = style.LineHeight;
		WhiteSpace = style.WhiteSpace;
		TextTransform = style.TextTransform;
		GradientInfo = style.TextGradient;
		TextOverflow = style.TextOverflow.Value;
		TextFilter = style.TextFilter.Value;
		WordBreak = style.WordBreak.Value;
		Smooth = style.FontSmooth.Value;

		var hash = HashCode.Combine( FontSize, fontColor, fontFamily, FontWeight, TextAlign, WhiteSpace, TextDecoration, FontStyle );
		hash = HashCode.Combine( hash, LetterSpacing, TextTransform, style.TextShadow );
		hash = HashCode.Combine( hash, style.TextStrokeWidth, style.TextStrokeColor, style.TextDecorationColor, style.TextDecorationThickness, style.TextDecorationSkipInk, style.TextDecorationStyle );
		hash = HashCode.Combine( hash, style.TextUnderlineOffset, style.TextOverlineOffset, style.TextLineThroughOffset, style.TextGradient, style.TextOverflow, style.WordBreak, style.LineHeight );
		hash = HashCode.Combine( hash, style.WordSpacing );
		//IsHtml too: the same string is a different block of text read as markup than read as words, and
		//a label told to start reading it as markup would otherwise keep the one it had already built.
		hash = HashCode.Combine( hash, Smooth, FontVariantNumeric );

		//What the style alone is: a label whose text changes and whose look does not is shaped again, its style and effects kept
		var styleHash = hash;

		hash = HashCode.Combine( hash, Text, SelectionStart, SelectionEnd, ShouldDrawSelection, IsHtml );

		//And the faces there are: one that arrived since the text was set may be the one it asked for - see FontManager.Generation
		hash = HashCode.Combine( hash, FontManager.Generation );

		if ( FontHash == hash && Block != null )
			return false;

		//Create a hash of things on the font that could change

		FontHash = hash;
		var restyleStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var allocatedBefore = TextStats.Allocated;

		if ( Style is null || styleHash != builtStyleHash )
		{
			builtStyleHash = styleHash;

			Style ??= new Style();

			Style.FontFamily = fontFamily;
			Style.FontSize = FontSize;
			Style.FontWeight = FontWeight ?? 400;
			Style.FontItalic = FontStyle != FontStyle.None;
			Style.FontVariantNumeric = FontVariantNumeric ?? UI.FontVariantNumeric.Normal;
			Style.TextColor = fontColor.ToSk();
			Style.Underline = UnderlineStyle.None;
			Style.StrokeInkSkip = style.TextDecorationSkipInk == TextSkipInk.All;
			Style.UnderlineOffset = style.TextUnderlineOffset.Value.GetPixels( 100 );
			Style.OverlineOffset = style.TextOverlineOffset.Value.GetPixels( 100 );
			Style.StrikeThroughOffset = style.TextLineThroughOffset.Value.GetPixels( 100 );

			switch ( style.TextDecorationStyle )
			{
				case TextDecorationStyle.Solid:
					Style.UnderlineStrokeType = UnderlineType.Solid;
					break;
				case TextDecorationStyle.Double:
					Style.UnderlineStrokeType = UnderlineType.Double;
					break;
				case TextDecorationStyle.Dotted:
					Style.UnderlineStrokeType = UnderlineType.Dotted;
					break;
				case TextDecorationStyle.Dashed:
					Style.UnderlineStrokeType = UnderlineType.Dashed;
					break;
				case TextDecorationStyle.Wavy:
					Style.UnderlineStrokeType = UnderlineType.Wavy;
					break;
				default:
					Style.UnderlineStrokeType = UnderlineType.Solid;
					break;
			}

			Style.UnderlineColor = (style.TextDecorationColor ?? fontColor).ToSk();
			Style.StrokeThickness = style.TextDecorationThickness?.GetPixels( 100.0f );
			Style.Underline |= (TextDecoration & UI.TextDecoration.Underline) != 0 ? UnderlineStyle.Gapped : UnderlineStyle.None;
			Style.Underline |= (TextDecoration & UI.TextDecoration.Overline) != 0 ? UnderlineStyle.Overline : UnderlineStyle.None;
			Style.StrikeThrough = (TextDecoration & UI.TextDecoration.LineThrough) != 0 ? StrikeThroughStyle.Solid : StrikeThroughStyle.None;
			Style.LetterSpacing = LetterSpacing.Value.GetPixels( 1000.0f );
			Style.WordSpacing = WordSpacing.Value.GetPixels( 1000.0f );
			Style.LineHeight = GetLineHeightMultiplier();

			Style.ClearEffects();
			Gradient = null;

			EffectMargin = default;

			if ( !style.TextGradient.ColorOffsets.IsDefaultOrEmpty )
			{
				var colors = style.TextGradient.ColorOffsets.Select( x => SkiaCompat.ToSk( x.color ) ).ToArray();
				var stops = style.TextGradient.ColorOffsets.Select( x => x.offset.Value ).ToArray();

				if ( style.TextGradient.GradientType == GradientInfo.GradientTypes.Linear )
				{
					Gradient = TextGradient.Linear( colors, stops, style.TextGradient.Angle );
				}

				if ( style.TextGradient.GradientType == GradientInfo.GradientTypes.Radial )
				{
					Gradient = TextGradient.Radial( colors, stops, 0, new SKPoint( 0.5f, 0.5f ), (RadialSizeMode)style.TextGradient.SizeMode );
				}
			}

			if ( style.TextShadow != null && !style.TextShadow.IsNone )
			{
				foreach ( var shadow in style.TextShadow )
				{
					var effect = TextEffect.DropShadow( shadow.Color.ToSk(), shadow.OffsetX, shadow.OffsetY, shadow.Blur );
					effect.Width = 0;
					effect.BlurSize = MathF.Max( effect.BlurSize, 0.01f );
					Style.AddEffect( effect );

					var shadowSize = (effect.Width + shadow.Blur) * 2.0f;

					EffectMargin.Left = MathF.Max( EffectMargin.Left, shadowSize + -shadow.OffsetX ).CeilToInt();
					EffectMargin.Right = MathF.Max( EffectMargin.Right, shadowSize + shadow.OffsetX ).CeilToInt();
					EffectMargin.Top = MathF.Max( EffectMargin.Top, shadowSize + -shadow.OffsetY ).CeilToInt();
					EffectMargin.Bottom = MathF.Max( EffectMargin.Bottom, shadowSize + shadow.OffsetY ).CeilToInt();
				}
			}

			if ( style.TextStrokeWidth.Value.Value > 0.0f )
			{
				var color = style.TextStrokeColor ?? style.FontColor ?? Color.Black;

				var size = style.TextStrokeWidth.Value.GetPixels( 1.0f );
				var effect = TextEffect.Outline( color.ToSk(), size );
				effect.StrokeMiter = 2.0f;
				effect.StrokeJoin = SKStrokeJoin.Round;
				Style.AddEffect( effect );

				EffectMargin.Left = MathF.Max( EffectMargin.Left, size ).CeilToInt();
				EffectMargin.Right = MathF.Max( EffectMargin.Right, size ).CeilToInt();
				EffectMargin.Top = MathF.Max( EffectMargin.Top, size ).CeilToInt();
				EffectMargin.Bottom = MathF.Max( EffectMargin.Bottom, size ).CeilToInt();
			}
		}

		if ( Block == null )
		{
			Block = new Topten.RichTextKit.TextBlock();
			Block.FontMapper = FontManager.Instance;
		}

		Block.Clear();
		EndsWithNewline = false;
		Block.Alignment = (Topten.RichTextKit.TextAlignment)TextAlign;
		Block.Overflow = (Topten.RichTextKit.TextOverflow)TextOverflow;
		Block.WordBreak = (Topten.RichTextKit.WordBreakMode)WordBreak;
		Block.NoWrap = DoesNotWrap;

		if ( IsHtml && !string.IsNullOrWhiteSpace( Text ) )
		{
			try
			{
				HtmlSpans = new List<HtmlSpan>();

				var html = htmlNode;
				if ( html is not null )
				{
					BuildBlockFromHtml( Block, html, Style );
				}

				if ( LookupStyles is not null )
				{
					foreach ( var span in HtmlSpans )
					{
						var s = LookupStyles( span.node );
						if ( s is null ) continue;

						var sty = Style.Copy();

						sty.FontSize = (style.FontSize ?? Length.Pixels( 13 ).Value).GetPixels( 100 );
						sty.FontSize = MathF.Round( FontSize * 32.0f ) / 32.0f;
						sty.FontFamily = s.FontFamily;
						sty.TextColor = s.FontColor?.ToSk() ?? sty.TextColor;
						sty.BackgroundColor = s.BackgroundColor?.ToSk() ?? sty.BackgroundColor;
						sty.FontWeight = s.FontWeight ?? sty.FontWeight;
						sty.FontItalic = s.FontStyle == FontStyle.Italic;
						sty.FontVariantNumeric = s.FontVariantNumeric ?? sty.FontVariantNumeric;
						sty.Underline = s.TextDecorationLine == UI.TextDecoration.Underline ? UnderlineStyle.Solid : UnderlineStyle.None;
						sty.UnderlineColor = sty.TextColor;
						sty.LetterSpacing = s.LetterSpacing?.GetPixels( 1000.0f ) ?? sty.LetterSpacing;
						sty.WordSpacing = s.WordSpacing?.GetPixels( 1000.0f ) ?? sty.WordSpacing;
						sty.StrikeThrough = (s.TextDecorationLine?.Contains( UI.TextDecoration.LineThrough ) ?? false) ? StrikeThroughStyle.Solid : sty.StrikeThrough;

						Block.ApplyStyle( span.from, span.to - span.from, sty );
					}
				}
			}
			catch ( System.Exception e )
			{
				Log.Warning( e );
			}

			//Nothing came of it: a string that says it is markup and is not, or a parse that threw before it
			//added a word. The characters themselves are a poor label; an empty one is not a label at all.
			if ( Block.Length == 0 )
			{
				var fallback = FixedText( Text );
				Block.AddText( fallback, Style );
				EndsWithNewline = EndsWithLineBreak( fallback );
			}
		}
		else
		{
			var text = FixedText( Text );
			Block.AddText( text, Style );
			EndsWithNewline = EndsWithLineBreak( text );
		}


		builtText = Text;

		SizeCache.Clear();
		ReleaseTexture();

		TextStats.Restyle( System.Diagnostics.Stopwatch.GetTimestamp() - restyleStart );
		TextStats.Garbage( TextStats.Allocated - allocatedBefore, label: true );

		return true;
	}

	public record class HtmlSpan( INode node, int from, int to );
	public List<HtmlSpan> HtmlSpans;

	private void BuildBlockFromHtml( Topten.RichTextKit.TextBlock block, Node node, Style style )
	{
		if ( node.IsComment )
			return;

		if ( node.IsText )
		{
			var startText = block.Length;
			block.AddText( node.InnerHtml, style );
			EndsWithNewline = EndsWithLineBreak( node.InnerHtml );
			var endText = block.Length;

			var span = new HtmlSpan( node?.ParentNode, startText, endText );
			HtmlSpans.Add( span );
		}

		if ( node.Name == "br" )
		{
			block.AddText( "\n", style );
			EndsWithNewline = true;
			return;
		}

		foreach ( var c in node.ChildNodes )
		{
			BuildBlockFromHtml( block, c, style );
		}
	}

	void ReleaseTexture()
	{
		WaitTextureReady();

		if ( Texture == null )
			return;

		if ( held is not null )
		{
			//Shared: let go of, never kept to be painted over - another block may be showing it.
			TextPictures.Release( held );
			held = null;
		}
		else
		{
			DisposeLast();
			LastTexture = Texture;
		}

		Texture = null;
		OnTextureChanged?.Invoke();
	}

	int lastSizeHash = 0;

	void DisposeLast()
	{
		if ( LastTexture is null ) return;
		TextStats.Freed( (long)LastTexture.Width * LastTexture.Height * 4 * ( LastTexture.Mips > 1 ? 4 : 3 ) / 3 );
		LastTexture.Dispose();
		LastTexture = null;
	}

	/// <summary>Called on layout. We should decide here if we actually need to rebuild</summary>
	public void SizeFinalized( float width, float height )
	{
		WaitTextureReady();

		width = width.CeilToInt();
		height = height.CeilToInt();

		int sizeHash = new Vector2( width, height ).GetHashCode();

		if ( lastSizeHash != sizeHash )
		{
			if ( Texture is not null ) TextStats.SizeRelease();
			ReleaseTexture();
			lastSizeHash = sizeHash;

			if ( Text.Length == 0 )
			{
				BlockSize = new Vector2( Block.MeasuredWidth.CeilToInt().Clamp( 2, 4096 ), Block.MeasuredHeight.CeilToInt().Clamp( 2, 4096 ) );
			}
		}

		if ( Text.Length == 0 )
			return;

		if ( Texture == null )
		{
			//threaded
			//TextureRebuild = Task.Run( () => RebuildTexture( width, height ) );

			//blocking
			RebuildTexture( width, height );
		}
	}

	Task TextureRebuild;

	//Actually recreate the texture
	void RebuildTexture( float maxwidth, float maxheight )
	{
		var allocatedBefore = TextStats.Allocated;
		Rebuild( maxwidth, maxheight );
		TextStats.Garbage( TextStats.Allocated - allocatedBefore, label: true );
	}

	unsafe void Rebuild( float maxwidth, float maxheight )
	{
		if ( !ui_rendertext )
			return;

		//Log.Info( $"RenderText: {Text}" );

		bool isEmpty = Text.Length == 0;
		var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( TextOverflow != TextOverflow.None )
		{
			Block.MaxWidth = maxwidth;
			Block.MaxHeight = maxheight;
		}
		else
		{
			//Text that does not wrap is given no width to wrap inside - asking for one is what broke it into lines anyway. Both ways of saying
			//so count: the sheet's white-space, and a label told it is one line (Label.Multiline), which is every single-line field there is.
			Block.MaxWidth = DoesNotWrap ? null : (maxwidth.CeilToInt() + 1);
		}

		//Drawn already, by a block showing the same text in the same style at the same room: its picture, and no layout, paint or upload of our own.
		var shareable = !isEmpty && Shareable;
		var key = shareable ? KeyAt( maxwidth, maxheight ) : default;

		if ( shareable && TextPictures.Take( key ) is { } shared )
		{
			held = shared;
			Texture = shared.Texture;
			BlockSize = shared.BlockSize;
			IsTruncated = shared.IsTruncated;

			TextStats.PictureHit( System.Diagnostics.Stopwatch.GetTimestamp() - t0 );
			OnTextureChanged?.Invoke();
			return;
		}

		int width = Block.MeasuredWidth.CeilToInt().Clamp( 2, 4096 );
		int height = Block.MeasuredHeight.CeilToInt().Clamp( 2, 4096 );

		if ( Style.LetterSpacing < 0 )
			width += Math.Abs( (int)MathF.Floor( Style.LetterSpacing ) );

		BlockSize = new Vector2( width, height );
		IsTruncated = Block.Truncated;

		var marginEdge = EffectMargin.EdgeSize;
		width += marginEdge.x.CeilToInt();
		height += marginEdge.y.CeilToInt();

		if ( isEmpty )
			return;

		if ( Gradient != null && Gradient.GradientType == Topten.RichTextKit.GradientType.Radial )
		{
			var centerX = GradientInfo.OffsetX.GetPixels( width ) / width;
			var centerY = GradientInfo.OffsetY.GetPixels( height ) / height;
			Gradient.Center = new SKPoint( centerX, centerY );
		}

		//The picture a step of 32 bigger than the text, the rest clear, drawn the same: changing text is mostly a few pixels wider or
		//narrower, and at the same step its picture is written over rather than a new one made - the garbage a changing label made
		width = ( width + 31 ) & ~31;
		height = ( height + 31 ) & ~31;

		using var perfScope = Performance.Scope( "TextBlock.RebuildTexture" );
		var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
		long t2 = 0;

		//Into the top left of one bitmap kept for it, grown when a label is bigger than any before it, and read out row by row - a bitmap
		//and a canvas made for every change of text was its garbage
		var scratch = Scratch( width, height );
		var canvas = scratch.Canvas;

		{
			var o = paintOptions ??= new Topten.RichTextKit.TextPaintOptions();
			o.Edging = Smooth == FontSmooth.Never ? SKFontEdging.Alias : SKFontEdging.Antialias;
			o.Hinting = SKFontHinting.Full;
			o.TextGradient = Gradient;
			o.Selection = null;

			canvas.Save();
			canvas.ClipRect( new SKRect( 0, 0, width, height ) );
			canvas.Clear( Style.TextColor.WithAlpha( 0 ) );

			if ( ShouldDrawSelection && (SelectionStart > 0 || SelectionEnd > 0) )
			{
				o.Selection = new TextRange( CaretToCodePointIndex( SelectionStart ), CaretToCodePointIndex( SelectionEnd ) );
				o.SelectionColor = SelectionColor.ToSk();
			}

			Block.Paint( canvas, new SKPoint( EffectMargin.Left - Block.MeasuredPadding.Left, EffectMargin.Top ), o );
			canvas.Restore();
			t2 = System.Diagnostics.Stopwatch.GetTimestamp();

			var pixels = scratch.Read( width, height );

			//A world label's picture carries its smaller levels (a quarter as much again).
			var bytes = (long)width * height * 4 * ( Mipped ? 4 : 3 ) / 3;

			if ( shareable )
			{
				var blockSize = BlockSize;

				//An idle shared picture this size (and with levels, or without, as this one) is painted over rather than a new one made.
				//Its first level is all that's written: a mipped picture's levels are built again from it on the way to the device.
				var spare = TextPictures.TakeSpare( width, height, Mipped );
				var reused = spare is not null;

				if ( reused )
				{
					spare.Update( pixels, 0, 0, width, height );
				}
				else
				{
					spare = NewPicture( pixels, width, height );
				}

				held = TextPictures.Add( key, spare, blockSize, IsTruncated );
				Texture = spare;

				TextStats.Rebuild( t1 - t0, t2 - t1, System.Diagnostics.Stopwatch.GetTimestamp() - t2, bytes, reused );
				OnTextureChanged?.Invoke();
				return;
			}

			if ( LastTexture != null )
			{
				//we already have a texture that is the right size, lets just use that (LastTexture is always of this block's Mipped - changing it lets it go)
				if ( LastTexture.Size == new Vector2( width, height ) )
				{
					LastTexture.Update( pixels, 0, 0, width, height );
					Texture = LastTexture;
					LastTexture = null;
					TextStats.Rebuild( t1 - t0, t2 - t1, System.Diagnostics.Stopwatch.GetTimestamp() - t2, bytes, reused: true );
					OnTextureChanged?.Invoke();
					return;
				}

				DisposeLast();
			}

			Texture = NewPicture( pixels, width, height );

			TextStats.Rebuild( t1 - t0, t2 - t1, System.Diagnostics.Stopwatch.GetTimestamp() - t2, bytes, reused: false );

			OnTextureChanged?.Invoke();
		}
	}

	[ThreadStatic] static Topten.RichTextKit.TextPaintOptions paintOptions;

	//A bitmap a label's text is painted into, kept and grown, and the bytes its top left is read out into
	sealed class ScratchPicture
	{
		public SKBitmap Bitmap;
		public SKCanvas Canvas;
		public byte[] Bytes = [];

		//The top left, row after row, nothing between - what an upload of that much reads
		public unsafe ReadOnlySpan<byte> Read( int width, int height )
		{
			var row = width * 4;
			var count = row * height;
			if ( Bytes.Length < count ) Bytes = new byte[Math.Max( count, Bytes.Length * 2 )];

			var pixels = (byte*)Bitmap.GetPixels();
			for ( var y = 0; y < height; y++ )
				new ReadOnlySpan<byte>( pixels + ( y * Bitmap.RowBytes ), row ).CopyTo( Bytes.AsSpan( y * row, row ) );

			return Bytes.AsSpan( 0, count );
		}
	}

	[ThreadStatic] static ScratchPicture scratchPicture;

	static ScratchPicture Scratch( int width, int height )
	{
		var kept = scratchPicture ??= new ScratchPicture();

		if ( kept.Bitmap is null || kept.Bitmap.Width < width || kept.Bitmap.Height < height )
		{
			var w = Math.Max( width, kept.Bitmap?.Width ?? 0 );
			var h = Math.Max( height, kept.Bitmap?.Height ?? 0 );

			kept.Canvas?.Dispose();
			kept.Bitmap?.Dispose();

			kept.Bitmap = new SKBitmap( w, h, SKColorType.Rgba8888, SKAlphaType.Premul );
			kept.Canvas = new SKCanvas( kept.Bitmap );
		}

		return kept;
	}

	//A new picture of what was just painted, RGBA8, with its levels for a world label.
	unsafe Texture NewPicture( ReadOnlySpan<byte> pixels, int width, int height )
	{
		var debugName = Text;
		if ( debugName.Length > 10 ) debugName = $"{debugName.Substring( 0, 8 )}..";
		if ( debugName.Contains( ':' ) ) debugName = debugName.Replace( ':', '-' );

		//RGBA8 for both: it goes to the device as the bytes it is, where any other order was converted into a new array at every upload
		var builder = Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithName( $"skiatextblock[{debugName}]" );

		if ( Mipped ) builder = builder.WithMips();

		Texture texture;

		fixed ( byte* data = pixels )
		{
			texture = builder
				.WithData( (IntPtr)data, pixels.Length )
				.WithDynamicUsage()
				.Finish();
		}

		//The colours in there are already multiplied by their own transparency, in the space they were drawn in - so our renderer takes
		//the bytes as written and does that arithmetic itself, and a chain's levels are plain averages - see CoreTexture.BuildLevels.
		texture.core.RawPixels = true;

		return texture;
	}

	int CaretToCodePointIndex( int caretPos )
	{
		if ( caretPos < 0 || caretPos > Block.CaretIndicies.Count - 1 )
			return caretPos;

		return Block.CaretIndicies[caretPos];
	}

	const string ParagraphSeparator = "\u2029";

	string FixedText( string text )
	{
		if ( string.IsNullOrEmpty( text ) ) return ".";

		//TODO - if starts with #, look up localized string

		//text = text.Replace( "\r", "" );
		text = text.Replace( "\r\n", ParagraphSeparator );
		text = text.Replace( '\n', (char)0x2029 ); //replace newlines with paragraph, makes them not render as a square with a cross in it

		if ( TextTransform.HasValue )
		{
			switch ( TextTransform.Value )
			{
				case UI.TextTransform.Uppercase:
					text = text.ToUpperInvariant();
					break;

				case UI.TextTransform.Lowercase:
					text = text.ToLowerInvariant();
					break;

				case UI.TextTransform.Capitalize:
					{
						text = System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase( text );
						break;
					}
			}
		}

		text = WhiteSpace switch
		{
			UI.WhiteSpace.Normal or UI.WhiteSpace.NoWrap => text.CollapseWhiteSpace(),
			UI.WhiteSpace.PreLine => text.CollapseSpacesAndPreserveLines(),
			UI.WhiteSpace.Pre or UI.WhiteSpace.PreWrap or UI.WhiteSpace.BreakSpaces => text,
			_ => throw new Exception( $"Unknown white-space value {WhiteSpace}" ),
		};

		return text;
	}

	float GetLineHeightMultiplier()
	{
		var lineHeight = LineHeight.Value;

		if ( lineHeight.Unit == LengthUnit.Percentage )
			return lineHeight.GetFraction();

		if ( lineHeight.Unit == LengthUnit.Pixels )
			return lineHeight.Value / Math.Max( FontSize, 1.0f );

		return 1.0f;
	}

	//What is kept past the caret, so it is not flush against the edge it just reached.
	internal const float CaretMargin = 2.0f;

	//Moves the view so the caret is inside it. The rect the caret is drawn at, which is CaretRect of the caret's own position
	//- asked for the letter before it, the view followed that letter instead, and the caret sat a character outside the field.
	internal void ScrollToCaret( int caretPosition, ref Vector2 scroll, Vector2 visibleBounds )
	{
		Rect caretRect = CaretRect( caretPosition );

		if ( caretRect.Left - CaretMargin < scroll.x )
		{
			scroll.x = caretRect.Left - CaretMargin;
		}
		else if ( caretRect.Right + CaretMargin > scroll.x + visibleBounds.x )
		{
			scroll.x = caretRect.Right - visibleBounds.x + CaretMargin;
		}

		if ( caretRect.Top < scroll.y )
		{
			scroll.y = caretRect.Top;
		}
		else if ( caretRect.Bottom > scroll.y + visibleBounds.y )
		{
			scroll.y = caretRect.Bottom - visibleBounds.y + caretRect.Height;
		}
	}

	public void Dispose()
	{
		ReleaseTexture();

		DisposeLast();

		Block = null;
		Style = null;
		SizeCache = null;
	}
}
