namespace Editor.TerrainEditor;

//Onto the vertices, so a cave's floor and the hill over it differ, and onto the mask over the ground's top
/// <summary>Paint the selected layer onto the ground, a quarter of a metre fine.</summary>
[Title( "Paint Texture" )]
[Icon( "brush" )]
[Alias( "tools.terrain.paint-texture" )]
[Group( "1" )]
[Order( 1 )]
public class PaintTextureTool : BaseBrushTool
{
	//How fast a layer goes on at full opacity: all of it in a fifth of a second.
	const float Rate = 5.0f;

	//How many layers a terrain may list.
	const int MostLayers = Terrain.MostLayers;

	public PaintTextureTool( TerrainEditorTool terrainEditorTool ) : base( terrainEditorTool )
	{
	}

	/// <summary>Which of the terrain's layers the brush is painting.</summary>
	public static int SplatChannel { get; set; } = 0;

	public override bool PaintMode { get; set; } = true;

	protected override bool Reshapes => false;

	protected override void OnDab( Terrain terrain, TerrainMesh mesh, Vector3 centre, float radius, float amount, Vector3 viewDirection )
	{
		var layer = Math.Clamp( SplatChannel, 0, Math.Min( terrain.Storage.Materials.Count, MostLayers ) - 1 );
		if ( layer < 0 ) return;

		mesh.Paint( centre, radius, layer, Math.Clamp( amount * Rate, 0.0f, 1.0f ) );
	}
}
