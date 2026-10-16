using Rinten.UI;

namespace Rinten;

public partial class Bitmap
{
	/// <summary>Draws text onto this bitmap</summary>
	public void DrawText( TextRendering.Scope scope, Rect rect, TextFlag flags = TextFlag.Center )
	{
		TextRendering.Scratch( scope, flags ).Render( _canvas, rect );
	}

	//Clears the top left of the bitmap, which a string drawn into a reused bitmap bigger than it covers
	internal void ClearArea( int width, int height )
	{
		_canvas.Save();
		_canvas.ClipRect( new SkiaSharp.SKRect( 0, 0, width, height ) );
		_canvas.Clear( SkiaSharp.SKColors.Transparent );
		_canvas.Restore();
	}

	//The top left of the bitmap's pixels, row after row with nothing between - what an upload of that area reads
	internal unsafe void CopyArea( int width, int height, Span<byte> into )
	{
		var rowBytes = _bitmap.RowBytes;
		var bytes = width * _bitmap.BytesPerPixel;
		var pixels = (byte*)_bitmap.GetPixels();

		for ( var y = 0; y < height; y++ )
			new ReadOnlySpan<byte>( pixels + ( y * rowBytes ), bytes ).CopyTo( into.Slice( y * bytes, bytes ) );
	}

	//Draws a block already made - and measured, so it is not shaped a second time.
	internal void DrawText( TextRendering.TextBlock block, Rect rect )
	{
		block.Render( _canvas, rect );
	}

	//What a widget that draws itself asks before it draws: how wide a label is decides where everything beside it goes.
	/// <summary>How much room this text would take, and where inside the rectangle it would end up, without drawing any of it.</summary>
	public static Rect MeasureText( TextRendering.Scope scope, Rect within, TextFlag flags = TextFlag.Center )
	{
		return TextRendering.Scratch( scope, flags ).Measure( within );
	}
}
