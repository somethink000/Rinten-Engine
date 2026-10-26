namespace Rinten;

public partial class Terrain
{
	//The ring of relief round the eye, where a layer displaces - see TerrainRelief
	private TerrainRelief relief;

	//The ground under the ring changed - a chunk stood - so it is built again even where the eye has not moved
	private bool reliefStale;

	//Whether the numbers picture last told of a ring
	private bool reliefWritten;

	/// <summary>How far from the eye, in metres, the layers' Displacement lifts the ground - its last quarter fading it out; nought for none.</summary>
	[Property, Group( "Relief" ), Range( 0.0f, 200.0f )]
	public float ReliefDistance { get; set; } = 40.0f;

	//Full frame and an unturned terrain only: the ring's corners are placed by its scale and position alone
	private bool Relieved => !Application.IsHeadless && CoreNative.Available && !CoreRenderDevice.PortableFrame
		&& ReliefDistance > 0.0f && MathF.Abs( WorldRotation.w ) > 0.99999f
		&& Storage is { } storage && storage.Materials.Any( Displaces );

	//The most any layer lifts the ground, in metres.
	private float Lift()
	{
		var most = 0.0f;

		foreach ( var layer in Storage.Materials )
			if ( Displaces( layer ) ) most = MathF.Max( most, CoreResources.Number( layer.core, "g_flDisplacement", 0.0f ) );

		return most;
	}

	private static bool Displaces( Material layer )
		=> layer is not null && CoreResources.Number( layer.core, "g_flDisplacement", 0.0f ) > 0.0f && layer.GetTexture( "g_tHeight" ) is not null;

	//Once a frame: the ring built again round the eye where it has gone far enough, or the ground under it changed
	private void PumpRelief()
	{
		if ( !Relieved )
		{
			DropRelief();
			return;
		}

		if ( Scene is null ) return;

		//The eye the scene's detail is reckoned from - the editor's camera in an editor scene
		var seen = Scene.Detail.Looking ? Scene.Detail.Eye : Scene.DetailCamera?.WorldPosition;

		if ( seen is not { } at ) return;

		var eye = WorldTransform.PointToLocal( at );

		if ( relief is null )
		{
			relief = new TerrainRelief();
			SayPaint( relief.Attributes, default );
		}

		relief.ParamsIndex = ParamsMap?.Index ?? 0;
		relief.Stand( Scene.SceneWorld, DrawnWith, WorldTransform, Lift() );

		//The ring and the numbers it is placed by change in the same frame - see FillRelief.
		if ( relief.Take() ) UpdateParamsMap();

		var pixels = Scene.Detail.Looking ? Scene.Detail.PixelsAtAMetre : UnseenPixelsAtAMetre;

		if ( relief.Laying || ( !reliefStale && !relief.Wants( eye, pixels, ReliefDistance ) ) ) return;

		reliefStale = false;

		relief.Build( eye, pixels, ReliefDistance, AroundEye( eye, ReliefDistance ).ToArray() );
	}

	//The grounds of every chunk within a reach of the eye, in the terrain's metres, with their palettes
	private IEnumerable<(TerrainSoup, byte[])> AroundEye( Vector3 eye, float reach )
	{
		if ( chunks.Length == 0 ) yield break;

		var x0 = Math.Max( (int)MathF.Floor( ( eye.x - reach - chunkOrigin.x ) / chunkSpan ), 0 );
		var x1 = Math.Min( (int)MathF.Floor( ( eye.x + reach - chunkOrigin.x ) / chunkSpan ), chunksWide - 1 );
		var z0 = Math.Max( (int)MathF.Floor( ( eye.z - reach - chunkOrigin.y ) / chunkSpan ), 0 );
		var z1 = Math.Min( (int)MathF.Floor( ( eye.z + reach - chunkOrigin.y ) / chunkSpan ), chunksDeep - 1 );

		for ( var z = z0; z <= z1; z++ )
			for ( var x = x0; x <= x1; x++ )
				if ( chunks[( z * chunksWide ) + x].Baked is { Ground: { } ground, Palette: { } palette } ) yield return (ground, palette);
	}

	private void DropRelief()
	{
		relief?.Dispose();
		relief = null;

		if ( !reliefWritten ) return;

		UpdateParamsMap();
	}

	//The ring into the numbers picture: where it was built, its reach and fade, its rise over the ground, the terrain's
	//place and how long a cut is a metre out (Ring in terrain_layers.slang)
	private void FillRelief()
	{
		reliefWritten = relief is not null;
		if ( relief is null ) return;

		var eye = relief.Eye;
		var scale = WorldScale;
		var at = WorldPosition;

		WriteFloat( 0, RowRing, eye.x );
		WriteFloat( 1, RowRing, eye.y );
		WriteFloat( 2, RowRing, eye.z );
		WriteFloat( 3, RowRing, relief.Reach );
		WriteFloat( 4, RowRing, relief.Fade );
		WriteFloat( 5, RowRing, TerrainRelief.Rise );
		WriteFloat( 6, RowRing, scale.x );
		WriteFloat( 7, RowRing, scale.y );
		WriteFloat( 8, RowRing, scale.z );
		WriteFloat( 9, RowRing, at.x );
		WriteFloat( 10, RowRing, at.y );
		WriteFloat( 11, RowRing, at.z );
		WriteFloat( 12, RowRing, relief.CutPerMetre );
	}
}
