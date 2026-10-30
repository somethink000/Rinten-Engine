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

	//Past this many in view the rest are left out
	private const int MostProbes = 20000;

	//Metres; nearer than this a probe fills the view, past the last it is under a pixel
	private const float Nearest = 4.0f;
	private const float Fine = 10.0f;
	private const float Farthest = 60.0f;

	private const int Around = 10;
	private const int Down = 6;

	//The fine ball's corner normals once, and its triangles as indices into them
	private static readonly Vector3[] ballNormals = BallNormals();
	private static readonly int[] ballCorners = BallCorners();
	private static readonly Color32[] shades = new Color32[ballNormals.Length];

	internal static void Draw( Scene scene )
	{
		if ( mode == 0 || scene?.LightingIfAny?.Data is not { HasVolume: true } data ) return;
		if ( mode == 2 && !data.HasSky ) return;

		var list = Gizmo.GizmoDraw.Target;
		if ( list is null ) return;

		list.Begin( false, Transform.Zero, 1.0f, false, false, Gizmo.GizmoDraw.LifeFor( 0.0f ) );

		var eye = Gizmo.Camera.Position;
		var turn = Gizmo.Camera.Rotation;
		var forward = turn.Forward;
		var step = data.VolumeStep;
		var radius = MathF.Min( step * 0.15f, 0.15f );

		var lo = ( eye - Farthest - data.VolumeOrigin ) / step;
		var hi = ( eye + Farthest - data.VolumeOrigin ) / step;
		var x0 = Math.Max( (int)MathF.Ceiling( lo.x ), 0 );
		var y0 = Math.Max( (int)MathF.Ceiling( lo.y ), 0 );
		var z0 = Math.Max( (int)MathF.Ceiling( lo.z ), 0 );
		var x1 = Math.Min( (int)MathF.Floor( hi.x ), data.VolumeX - 1 );
		var y1 = Math.Min( (int)MathF.Floor( hi.y ), data.VolumeY - 1 );
		var z1 = Math.Min( (int)MathF.Floor( hi.z ), data.VolumeZ - 1 );

		//A far probe is a disc facing the eye: its middle shaded toward the eye, its rim by the four ways across
		Span<Vector3> rim = [turn.Up, turn.Right, -turn.Up, -turn.Right];
		Span<Color32> rimShade = stackalloc Color32[4];
		var drawn = 0;

		for ( var z = z0; z <= z1; z++ )
			for ( var y = y0; y <= y1; y++ )
				for ( var x = x0; x <= x1; x++ )
				{
					var at = data.VolumeOrigin + ( new Vector3( x, y, z ) * step );
					var toward = at - eye;
					var away = toward.Length;

					//Behind the eye or wider than any view's corner - half angle 60 degrees
					if ( away < Nearest || away > Farthest || Vector3.Dot( toward, forward ) < away * 0.5f ) continue;
					if ( drawn++ >= MostProbes ) return;

					var index = ( x + ( data.VolumeX * ( y + ( data.VolumeY * z ) ) ) ) * 4;

					if ( away < Fine )
					{
						for ( var i = 0; i < ballNormals.Length; i++ ) shades[i] = Shade( data, index, ballNormals[i] );
						foreach ( var corner in ballCorners ) list.Add( at + ( ballNormals[corner] * radius ), shades[corner] );
						continue;
					}

					var middle = Shade( data, index, -forward );
					for ( var i = 0; i < 4; i++ ) rimShade[i] = Shade( data, index, rim[i] );

					for ( var i = 0; i < 4; i++ )
					{
						var next = ( i + 1 ) & 3;
						list.Add( at, middle );
						list.Add( at + ( rim[i] * radius ), rimShade[i] );
						list.Add( at + ( rim[next] * radius ), rimShade[next] );
					}
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

	//A unit ball's corners, y up: rows from pole to pole, each Around + 1 wide so the seam closes
	private static Vector3[] BallNormals()
	{
		var normals = new Vector3[( Around + 1 ) * ( Down + 1 )];

		for ( var y = 0; y <= Down; y++ )
			for ( var x = 0; x <= Around; x++ )
			{
				var v = y / (float)Down * MathF.PI;
				var h = x / (float)Around * MathF.Tau;
				normals[x + ( y * ( Around + 1 ) )] = new Vector3( MathF.Sin( v ) * MathF.Cos( h ), MathF.Cos( v ), MathF.Sin( v ) * MathF.Sin( h ) );
			}

		return normals;
	}

	private static int[] BallCorners()
	{
		var corners = new List<int>();
		int At( int x, int y ) => x + ( y * ( Around + 1 ) );

		for ( var y = 0; y < Down; y++ )
			for ( var x = 0; x < Around; x++ )
			{
				corners.AddRange( [At( x, y ), At( x, y + 1 ), At( x + 1, y )] );
				corners.AddRange( [At( x + 1, y ), At( x, y + 1 ), At( x + 1, y + 1 )] );
			}

		return corners.ToArray();
	}
}
