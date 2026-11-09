namespace Editor.Inspectors;

static class GradientPaint
{
	public static Texture CreateTexture( Gradient gradient, int width )
	{
		width = Math.Clamp( width, 1, 2048 );

		using var bitmap = new Bitmap( width, 1 );

		Fill( bitmap, gradient, width );

		return bitmap.ToTexture( mips: false );
	}

	public static void ApplyTexture( Image image, Gradient gradient, int width )
	{
		if ( image is null || !image.IsValid() )
			return;

		if ( width < 1 )
			return;

		width = Math.Clamp( width, 1, 2048 );

		using var bitmap = new Bitmap( width, 1 );

		Fill( bitmap, gradient, width );

		//The texture the image already has, filled again where it still fits.
		if ( image.Texture is Texture existing && existing.Width == width && existing.Height == 1 )
		{
			existing.Update( bitmap );
			return;
		}

		image.Texture = bitmap.ToTexture( mips: false );
	}

	static void Fill( Bitmap bitmap, Gradient gradient, int width )
	{
		for ( var x = 0; x < width; x++ )
		{
			var t = width > 1 ? x / (float)(width - 1) : 0f;

			bitmap.SetPixel( x, 0, gradient.Evaluate( t ) );
		}
	}
}
