using Rinten.Rendering;

namespace Template;


public sealed partial class Player : Component
{
	[Property, Category( "Relatives" )] public CameraComponent Camera { get; set; }


	/// <summary>Units a second.</summary>
	[Property, Category( "Movement" )] public float Speed { get; set; } = 4.0f;

	/// <summary>What holding Run multiplies the speed by.</summary>
	[Property, Category( "Movement" )] public float RunScale { get; set; } = 2.0f;

	/// <summary>Pixels of the picture to a unit of the world.</summary>
	[Property, Category( "Look" )] public float PixelsPerUnit { get; set; } = 8.0f;


	//An eight by eight face, a row a string: '#' ink, 'o' skin, anything else clear.
	private static readonly string[] Face =
	[
		"..####..",
		".#oooo#.",
		"#oooooo#",
		"#o#oo#o#",
		"#oooooo#",
		"#o#..#o#",
		".#o##o#.",
		"..####..",
	];



	protected override void OnStart()
	{
		if ( GetComponent<SpriteRenderer>() is not null ) return;

		var renderer = AddComponent<SpriteRenderer>();
		renderer.Sprite = Sprite.FromTexture( Picture() );
		renderer.Size = Face.Length / PixelsPerUnit;
		renderer.TextureFilter = FilterMode.Point;
		renderer.FogStrength = 0.0f;
	}



	protected override void OnUpdate()
	{
		//The world is the XY plane seen down Z: forward is up the screen, left is left.
		var move = new Vector3( -Input.AnalogMove.y, Input.AnalogMove.x, 0.0f );
		var speed = Input.Down( "Run" ) ? Speed * RunScale : Speed;

		WorldPosition += move.ClampLength( 1.0f ) * speed * Time.Delta;

		if ( Camera.IsValid() )
			Camera.WorldPosition = new Vector3( WorldPosition.x, WorldPosition.y, Camera.WorldPosition.z );
	}



	private static Texture Picture()
	{
		var size = Face.Length;
		var bytes = new byte[size * size * 4];

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				Color32 colour = Face[y][x] switch
				{
					'#' => new Color32( 34, 32, 52 ),
					'o' => new Color32( 251, 201, 123 ),
					_ => new Color32( 0, 0, 0, 0 ),
				};

				var at = ( ( y * size ) + x ) * 4;
				bytes[at] = colour.r;
				bytes[at + 1] = colour.g;
				bytes[at + 2] = colour.b;
				bytes[at + 3] = colour.a;
			}
		}

		return Texture.Create( size, size ).WithData( bytes ).Finish();
	}
}
