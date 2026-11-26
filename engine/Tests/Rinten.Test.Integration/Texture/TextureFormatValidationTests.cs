using System;

namespace TextureTests;

[TestClass]
public class TextureFormatValidationTests
{
	[TestMethod]
	public void UndefinedFormatsAreRejected()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.Create( 4, 4, (ImageFormat)5000 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.Create( 4, 4, (ImageFormat)(-50) ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.CreateRenderTarget().WithFormat( (ImageFormat)5000 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.CreateRenderTarget().WithFormat( ImageFormat.None ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.CreateVolume( 4, 4, 4, (ImageFormat)5000 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.CreateCube( 4, 4, (ImageFormat)5000 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Texture.CreateArray( 4, 4, 1, (ImageFormat)5000 ) );
	}

	[TestMethod]
	public void DefaultFormatStillResolves()
	{
		using var texture = Texture.Create( 4, 4, ImageFormat.Default ).Finish();
		Assert.AreEqual( ImageFormat.RGBA8888, texture.ImageFormat );
	}
}
