namespace TextureTests;

[TestClass]
public class TgaBoundsTests
{
	//A 24-bit, uncompressed TGA header with the given dimensions and no pixel data.
	static byte[] Header( int width, int height )
	{
		var data = new byte[18];
		data[2] = 2;
		data[12] = (byte)( width & 0xFF );
		data[13] = (byte)( ( width >> 8 ) & 0xFF );
		data[14] = (byte)( height & 0xFF );
		data[15] = (byte)( ( height >> 8 ) & 0xFF );
		data[16] = 24;

		return data;
	}

	[TestMethod]
	public void OversizedImageIsRejected()
	{
		//8193 x 8193 is past the 8192 x 8192 cap, and would ask for a 268 MB buffer before reading a pixel.
		var data = Header( 8193, 8193 );
		Assert.IsTrue( Bitmap.IsTga( data ) );

		using var bitmap = Bitmap.CreateFromTgaBytes( data );
		Assert.IsNull( bitmap );
	}

	[TestMethod]
	public void PixelDataShorterThanTheHeaderClaimsIsRejected()
	{
		//A 2 x 2 24-bit image needs 12 bytes after the header.
		var data = Header( 2, 2 );

		using var bitmap = Bitmap.CreateFromTgaBytes( data );
		Assert.IsNull( bitmap );
	}
}
