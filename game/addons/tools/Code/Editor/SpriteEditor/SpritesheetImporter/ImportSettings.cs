using System.Text.Json.Serialization;

namespace Editor.SpriteEditor;

public class SpritesheetImportSettings
{
	[JsonIgnore, Hide]
	public Action OnFrameCountChanged;

	[Header( "Frame Count" )]
	[Property, MinMax( 1, 128 ), Step( 1 )]
	public int HorizontalFrames
	{
		get => _horizontalFrames;
		set { value = Math.Max( 1, value ); if ( _horizontalFrames == value ) return; _horizontalFrames = value; OnFrameCountChanged?.Invoke(); }
	}
	[Hide] private int _horizontalFrames = 8;

	[Property, MinMax( 1, 128 ), Step( 1 )]
	public int VerticalFrames
	{
		get => _verticalFrames;
		set { value = Math.Max( 1, value ); if ( _verticalFrames == value ) return; _verticalFrames = value; OnFrameCountChanged?.Invoke(); }
	}
	[Hide] private int _verticalFrames = 8;

	[Header( "Padding" )]
	[Property, Title( "Left" ), MinMax( 0, 8192 )]
	public int PaddingLeft { get; set; } = 0;

	[Property, Title( "Top" ), MinMax( 0, 8192 )]
	public int PaddingTop { get; set; } = 0;

	[Property, Title( "Right" ), MinMax( 0, 8192 )]
	public int PaddingRight { get; set; } = 0;

	[Property, Title( "Bottom" ), MinMax( 0, 8192 )]
	public int PaddingBottom { get; set; } = 0;

	[Header( "Separation" )]
	[Property, MinMax( 0, 8192 ), Step( 1 )]
	public int HorizontalSeparation { get; set; } = 0;

	[Property, MinMax( 0, 8192 ), Step( 1 )]
	public int VerticalSeparation { get; set; } = 0;

	/// <summary>Takes another's values without telling anyone the frame count moved.</summary>
	public void CopyFrom( SpritesheetImportSettings other )
	{
		_horizontalFrames = Math.Max( 1, other.HorizontalFrames );
		_verticalFrames = Math.Max( 1, other.VerticalFrames );
		PaddingLeft = other.PaddingLeft;
		PaddingTop = other.PaddingTop;
		PaddingRight = other.PaddingRight;
		PaddingBottom = other.PaddingBottom;
		HorizontalSeparation = other.HorizontalSeparation;
		VerticalSeparation = other.VerticalSeparation;
	}

	/// <summary>Calculates the list of each frame's pixel rects given the size of the source image</summary>
	public List<Rect> GetFrames( int textureWidth, int textureHeight )
	{
		//Available space after stripping padding from all four sides
		int availW = textureWidth - PaddingLeft - PaddingRight;
		int availH = textureHeight - PaddingTop - PaddingBottom;
		int fw = Math.Max( 1, (availW - (HorizontalFrames - 1) * HorizontalSeparation) / HorizontalFrames );
		int fh = Math.Max( 1, (availH - (VerticalFrames - 1) * VerticalSeparation) / VerticalFrames );

		var frames = new List<Rect>( HorizontalFrames * VerticalFrames );

		for ( int row = 0; row < VerticalFrames; row++ )
		{
			for ( int col = 0; col < HorizontalFrames; col++ )
			{
				var x = PaddingLeft + col * (fw + HorizontalSeparation);
				var y = PaddingTop + row * (fh + VerticalSeparation);
				frames.Add( new Rect( x, y, fw, fh ) );
			}
		}

		return frames;
	}
}
