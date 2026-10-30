namespace Rinten;

//The baked light volume's probes as balls round the editor's eye, each shaded the way it lights a surface facing there
internal static class BakedProbeView
{
	/// <summary>0 off, 1 the light each probe holds, 2 how much sky each sees.</summary>
	[ConVar( "r_light_probes", ConVarFlags.Protected,
		Help = "Draw the lightmapper's light probes: 0 off, 1 their light, 2 the sky they see" )]
	public static int Mode
	{
		get => mode;
		set => mode = Math.Clamp( value, 0, 2 );
	}

	private static int mode;

	//Past this many in view the rest are left out; a dense lattice seen whole is millions of corners
	private const int MostProbes = 20000;

	//Metres; past this a probe is a few pixels and gets the coarse ball
	private const float Fine = 15.0f;

	//Metres; nearer than this a probe fills the view
	private const float Nearest = 4.0f;

	//The ball's corners and normals once, unit size
	private static readonly Vector3[] ball = MakeBall( 14, 8 );
	private static readonly Vector3[] coarse = MakeBall( 6, 4 );

	internal static void Draw( Scene scene )
	{
		if ( mode == 0 || scene?.LightingIfAny?.Data is not { HasVolume: true } data ) return;
		if ( mode == 2 && !data.HasSky ) return;

		var list = Gizmo.GizmoDraw.Target;
		if ( list is null ) return;

		list.Begin( false, Transform.Zero, 1.0f, false, false, Gizmo.GizmoDraw.LifeFor( 0.0f ) );

		var eye = Gizmo.Camera.Position;
		var forward = Gizmo.Camera.Rotation.Forward;
		var step = data.VolumeStep;
		var radius = MathF.Min( step * 0.15f, 0.15f );
		var drawn = 0;

		for ( var z = 0; z < data.VolumeZ; z++ )
			for ( var y = 0; y < data.VolumeY; y++ )
				for ( var x = 0; x < data.VolumeX; x++ )
				{
					var at = data.VolumeOrigin + ( new Vector3( x, y, z ) * step );
					var toward = at - eye;
					var away = toward.Length;

					//Behind the eye or wider than any view's corner - half angle 60 degrees
					if ( away < Nearest || Vector3.Dot( toward, forward ) < away * 0.5f ) continue;
					if ( drawn++ >= MostProbes ) return;

					var index = ( x + ( data.VolumeX * ( y + ( data.VolumeY * z ) ) ) ) * 4;

					foreach ( var normal in away < Fine ? ball : coarse )
						list.Add( at + ( normal * radius ), Shade( data, index, normal ) );
				}
	}

	//What a surface facing normal reads from the probe - BakedVolume in frame.slang
	private static Color32 Shade( BakedLightingData data, int index, Vector3 normal )
	{
		if ( mode == 2 )
		{
			var seen = Facing( data.VolumeSky, index, normal ).Clamp( 0.0f, 1.0f );
			return new Color( seen, seen, seen );
		}

		var light = new Vector3( Facing( data.VolumeRed, index, normal ), Facing( data.VolumeGreen, index, normal ),
			Facing( data.VolumeBlue, index, normal ) );

		//Light has no top; squeezed under one so 1 still reads bright, then put on the screen's curve
		return new Color( Shown( light.x ), Shown( light.y ), Shown( light.z ) );
	}

	private static float Shown( float light ) => MathF.Pow( 1.0f - MathF.Exp( -light ), 1.0f / 2.2f );

	private static float Facing( Half[] four, int index, Vector3 normal ) => MathF.Max( (float)four[index]
		+ ( (float)four[index + 1] * normal.x ) + ( (float)four[index + 2] * normal.y ) + ( (float)four[index + 3] * normal.z ), 0.0f );

	//Triangles of a unit ball, three corners each, y up
	private static Vector3[] MakeBall( int around, int down )
	{
		var corners = new List<Vector3>();

		Vector3 At( int x, int y )
		{
			var v = y / (float)down * MathF.PI;
			var h = x / (float)around * MathF.Tau;
			return new Vector3( MathF.Sin( v ) * MathF.Cos( h ), MathF.Cos( v ), MathF.Sin( v ) * MathF.Sin( h ) );
		}

		for ( var y = 0; y < down; y++ )
			for ( var x = 0; x < around; x++ )
			{
				corners.AddRange( [At( x, y ), At( x, y + 1 ), At( x + 1, y )] );
				corners.AddRange( [At( x + 1, y ), At( x, y + 1 ), At( x + 1, y + 1 )] );
			}

		return corners.ToArray();
	}
}
