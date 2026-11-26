using System;

namespace TextureTests;

//Each span is a slice of a larger backing array so an unchecked write stays inside managed memory.
[TestClass]
public class TextureReadBoundsTests
{
	[TestMethod]
	public void GetPixelsRejectsNegativeDestinationX()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var backing = new Color32[64];

		Assert.ThrowsException<ArgumentException>( () =>
			texture.GetPixels( (0, 0, 4, 1), 0, 0, backing.AsSpan( 16, 16 ), ImageFormat.RGBA8888, (-2, 0, 4, 1), 4 ) );
	}

	[TestMethod]
	public void GetPixelsRejectsNegativeDestinationY()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var backing = new Color32[64];

		Assert.ThrowsException<ArgumentException>( () =>
			texture.GetPixels( (0, 0, 4, 2), 0, 0, backing.AsSpan( 16, 16 ), ImageFormat.RGBA8888, (0, -1, 4, 2), 4 ) );
	}

	[TestMethod]
	public void GetPixelsRejectsStrideNarrowerThanRow()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var backing = new Color32[64];

		Assert.ThrowsException<ArgumentException>( () =>
			texture.GetPixels( (0, 0, 4, 4), 0, 0, backing.AsSpan( 16, 4 ), ImageFormat.RGBA8888, (0, 0, 4, 4), 0 ) );
	}

	[TestMethod]
	public void GetPixelsRejectsDestinationRectLargerThanSource()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var buffer = new Color32[16];

		Assert.ThrowsException<ArgumentException>( () =>
			texture.GetPixels( (0, 0, 2, 2), 0, 0, buffer.AsSpan(), ImageFormat.RGBA8888, (0, 0, 4, 4), 4 ) );
	}

	[TestMethod]
	public void GetPixelsRejectsDestinationSizeLargerThanSource()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var buffer = new Color32[16];

		Assert.ThrowsException<ArgumentException>( () =>
			texture.GetPixels( (0, 0, 2, 2), 0, 0, buffer.AsSpan(), ImageFormat.RGBA8888, (4, 4) ) );
	}

	[TestMethod]
	public void GetPixelsRejectsSliceOutOfRange()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var buffer = new Color32[16];

		Assert.ThrowsException<ArgumentOutOfRangeException>( () =>
			texture.GetPixels( (0, 0, 4, 4), 100000, 0, buffer.AsSpan(), ImageFormat.RGBA8888 ) );
	}

	[TestMethod]
	public void GetPixelsAsyncRejectsMipAndSliceOutOfRange()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var buffer = new Color32[16];

		Assert.ThrowsException<ArgumentOutOfRangeException>( () =>
			texture.GetPixelsAsync<Color32>( _ => { }, ImageFormat.RGBA8888, (0, 0, 4, 4), 0, 1 ) );

		Assert.ThrowsException<ArgumentOutOfRangeException>( () =>
			texture.GetPixelsAsync<Color32>( _ => { }, ImageFormat.RGBA8888, (0, 0, 4, 4), 1, 0 ) );
	}

	[TestMethod]
	public void GetPixelsAsync3DAcceptsDefaultDestinationFormat()
	{
		using var texture = Texture.CreateVolume( 4, 4, 4 ).Finish();

		Color32[] result = null;
		texture.GetPixelsAsync3D<Color32>( pixels => result = pixels.ToArray(), srcBox: (0, 0, 0, 4, 4, 4) );

		Assert.IsNotNull( result );
		Assert.AreEqual( 4 * 4 * 4, result.Length );
	}

	[TestMethod]
	public void GetPixelsAsync3DRejectsMipOutOfRange()
	{
		using var texture = Texture.CreateVolume( 4, 4, 4 ).Finish();

		Assert.ThrowsException<ArgumentOutOfRangeException>( () =>
			texture.GetPixelsAsync3D<Color32>( _ => { }, ImageFormat.RGBA8888, (0, 0, 0, 4, 4, 4), 1 ) );
	}

	[TestMethod]
	public void UpdateRejectsNegativeRegion()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var data = new byte[64];

		Assert.ThrowsException<ArgumentException>( () => texture.Update( data, -1, 0, 1, 1 ) );
		Assert.ThrowsException<ArgumentException>( () => texture.Update( data, 0, -1, 1, 1 ) );
	}

	[TestMethod]
	public void UpdateRejectsRegionOutsideTexture()
	{
		using var texture = Texture.Create( 4, 4 ).Finish();
		var data = new byte[64];

		Assert.ThrowsException<ArgumentException>( () => texture.Update( data, 4, 4, 1, 1 ) );
	}
}
