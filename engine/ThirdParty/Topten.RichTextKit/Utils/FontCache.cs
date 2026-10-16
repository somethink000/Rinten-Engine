using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Topten.RichTextKit.Utils
{
	//The fonts and drop shadows painting and measuring ask for, made once and kept - each was made and thrown away for every run.
	//One set a thread: a font is set up for each paint (its edging, hinting), so one is never painted with from two threads at once.
	internal static class FontCache
	{
		//Past this many the set is let go and starts again - a size animated through every value would otherwise keep one of each
		const int Most = 256;

		[ThreadStatic] static Dictionary<(SKTypeface Typeface, float Size), SKFont> _fonts;
		[ThreadStatic] static Dictionary<(float X, float Y, float Blur, SKColor Color), SKImageFilter> _shadows;

		//A run takes its font again for every paint, so one let go here is never one a run still paints with
		public static SKFont Get( SKTypeface typeface, float size )
		{
			var fonts = _fonts ??= new Dictionary<(SKTypeface, float), SKFont>();

			if ( fonts.TryGetValue( (typeface, size), out var font ) )
				return font;

			if ( fonts.Count >= Most )
			{
				foreach ( var kept in fonts.Values ) kept.Dispose();
				fonts.Clear();
			}

			font = new SKFont( typeface, size );
			fonts[(typeface, size)] = font;
			return font;
		}

		[ThreadStatic] static SKTextBlobBuilder _builder;

		//A run of positioned glyphs as a blob, through one builder a thread: making the blob directly made a builder for each
		public static SKTextBlob Blob( SKFont font, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<SKPoint> positions )
		{
			if ( glyphs.Length == 0 ) return null;

			var builder = _builder ??= new SKTextBlobBuilder();
			var run = builder.AllocateRawPositionedRun( font, glyphs.Length );

			glyphs.CopyTo( run.Glyphs );
			positions.CopyTo( run.Positions );

			return builder.Build();
		}

		//Blobs as native handles, built, drawn and let go through libSkiaSharp itself: SkiaSharp wraps each in an object it registers in its
		//handle table, which for a run that is drawn once and let go was garbage every run painted. A page links the same library statically.
		[DllImport( "libSkiaSharp", CallingConvention = CallingConvention.Cdecl )]
		static extern IntPtr sk_textblob_builder_make( IntPtr builder );

		[DllImport( "libSkiaSharp", CallingConvention = CallingConvention.Cdecl )]
		static extern void sk_textblob_unref( IntPtr blob );

		[DllImport( "libSkiaSharp", CallingConvention = CallingConvention.Cdecl )]
		static extern void sk_canvas_draw_text_blob( IntPtr canvas, IntPtr blob, float x, float y, IntPtr paint );

		/// <summary>A run of positioned glyphs as a native blob, zero for none; let go with <see cref="Release"/>.</summary>
		public static IntPtr NativeBlob( SKFont font, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<SKPoint> positions )
		{
			if ( glyphs.Length == 0 ) return IntPtr.Zero;

			var builder = _builder ??= new SKTextBlobBuilder();
			var run = builder.AllocateRawPositionedRun( font, glyphs.Length );

			glyphs.CopyTo( run.Glyphs );
			positions.CopyTo( run.Positions );

			return sk_textblob_builder_make( builder.Handle );
		}

		public static void Draw( SKCanvas canvas, IntPtr blob, float x, float y, SKPaint paint )
		{
			if ( blob != IntPtr.Zero ) sk_canvas_draw_text_blob( canvas.Handle, blob, x, y, paint.Handle );
		}

		public static void Release( ref IntPtr blob )
		{
			if ( blob != IntPtr.Zero ) sk_textblob_unref( blob );
			blob = IntPtr.Zero;
		}

		public static SKImageFilter DropShadow( float x, float y, float blur, SKColor color )
		{
			var shadows = _shadows ??= new Dictionary<(float, float, float, SKColor), SKImageFilter>();

			if ( shadows.TryGetValue( (x, y, blur, color), out var filter ) )
				return filter;

			if ( shadows.Count >= Most )
			{
				foreach ( var kept in shadows.Values ) kept.Dispose();
				shadows.Clear();
			}

			filter = SKImageFilter.CreateDropShadow( x, y, blur, blur, color );
			shadows[(x, y, blur, color)] = filter;
			return filter;
		}
	}
}
