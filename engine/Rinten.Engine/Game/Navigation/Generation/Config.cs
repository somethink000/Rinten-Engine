namespace Rinten.Navigation.Generation;

//Basically rcConfig with a frew extra fields
internal struct Config
{
	public static Config CreateValidatedConfig(
		Vector2Int tilePosition,
		BBox tileBoundsWorld,
		float cellSize,
		float cellHeight,
		float agentHeight,
		float agentRadius,
		float agentStepSize,
		float agentMaxSlope )
	{
		var cfg = new Config();

		//Metres: five centimetres across and a centimetre up at the finest
		cfg.CellSize = MathF.Max( cellSize, 0.05f );
		cfg.CellHeight = MathF.Max( cellHeight, 0.01f );

		cfg.WalkableSlopeAngle = Math.Clamp( agentMaxSlope, 0.0f, 90.0f );

		cfg.WalkableHeight = (int)MathF.Ceiling( agentHeight / cfg.CellHeight );
		cfg.WalkableHeight = Math.Max( cfg.WalkableHeight, 3 );

		cfg.WalkableClimb = (int)MathF.Floor( agentStepSize / cfg.CellHeight );
		cfg.WalkableClimb = Math.Max( cfg.WalkableClimb, 0 );

		cfg.WalkableRadius = (int)MathF.Ceiling( agentRadius / cfg.CellSize );
		cfg.WalkableRadius = Math.Max( cfg.WalkableRadius, 0 );

		cfg.MaxVertsPerPoly = (int)Constants.VERTS_PER_POLYGON;

		//Six and a half metres, in voxels
		cfg.MaxEdgeLen = (int)(6.5f / cfg.CellSize);
		cfg.MaxEdgeLen = Math.Max( cfg.MaxEdgeLen, 0 );

		cfg.MaxSimplificationError = 2.0f;
		cfg.MaxSimplificationError = Math.Max( cfg.MaxSimplificationError, 0.0f );

		cfg.MinRegionArea = (int)(8.0f * 8.0f);
		cfg.MinRegionArea = Math.Max( cfg.MinRegionArea, 0 );

		cfg.MergeRegionArea = (int)(12.0f * 12.0f);
		cfg.MergeRegionArea = Math.Max( cfg.MergeRegionArea, 0 );

		cfg.BorderSize = cfg.WalkableRadius + 3;
		cfg.BorderSize = Math.Max( cfg.BorderSize, 0 );
		cfg.Bounds = tileBoundsWorld;

		//Pad X/Z bounds by the border so the tile sees nearby geometry; Y needs none as tiles don't stack
		cfg.Bounds.Mins.x -= cfg.BorderSize * cfg.CellSize;
		cfg.Bounds.Mins.z -= cfg.BorderSize * cfg.CellSize;

		cfg.Bounds.Maxs.x += cfg.BorderSize * cfg.CellSize;
		cfg.Bounds.Maxs.z += cfg.BorderSize * cfg.CellSize;

		cfg.TileX = tilePosition.x;
		cfg.TileY = tilePosition.y;

		var sizeX = cfg.Bounds.Maxs.x - cfg.Bounds.Mins.x;
		var sizeZ = cfg.Bounds.Maxs.z - cfg.Bounds.Mins.z;
		if ( !MathX.AlmostEqual( sizeX, sizeZ, 0.1f ) )
		{
			Log.Warning( $"Tile bounds {sizeX}x{sizeZ} should be square" );
		}
		if ( !MathX.AlmostEqual( sizeX.UnsignedMod( cfg.CellSize ), 0f, 0.01f ) && !MathX.AlmostEqual( sizeX.UnsignedMod( cfg.CellSize ), cfg.CellSize, 0.01f ) )
		{
			Log.Warning( $"Tile bounds {sizeX}x{sizeZ} should be divisible by cell size {cfg.CellSize}" );
		}

		cfg.TileSizeXY = (int)(sizeX / cfg.CellSize + 0.5f);

		return cfg;
	}

	/// <summary>The tiles x position in tile coordinates.</summary>
	public int TileX;

	/// <summary>The tiles y position in tile coordinates.</summary>
	public int TileY;

	/// <summary>The width/height size of tile's on the xy-plane. [Limit: &gt;= 0] [Units: vx]</summary>
	public int TileSizeXY;

	/// <summary>The size of the non-navigable border around the heightfield. [Limit: &gt;=0] [Units: vx]</summary>
	public int BorderSize;

	/// <summary>The xz-plane cell size to use for fields. [Limit: &gt; 0] [Units: wu]</summary>
	public float CellSize;

	/// <summary>The y-axis cell size to use for fields. [Limit: &gt; 0] [Units: wu]</summary>
	public float CellHeight;

	/// <summary>The bounds of the field's AABB. [Units: wu]</summary>
	public BBox Bounds;

	/// <summary>The maximum slope that is considered walkable. [Limits: 0 &lt;= value &lt; 90] [Units: Degrees]</summary>
	public float WalkableSlopeAngle;

	/// <summary>Minimum floor to 'ceiling' height that will still allow the floor area to be considered walkable. [Limit: &gt;= 3] [Units: vx]</summary>
	public int WalkableHeight;

	/// <summary>Maximum ledge height that is considered to still be traversable. [Limit: &gt;=0] [Units: vx]</summary>
	public int WalkableClimb;

	/// <summary>The distance to erode/shrink the walkable area of the heightfield away from obstructions. [Limit: &gt;=0] [Units: vx]</summary>
	public int WalkableRadius;

	/// <summary>The maximum allowed length for contour edges along the border of the mesh. [Limit: &gt;=0] [Units: vx]</summary>
	public int MaxEdgeLen;

	/// <summary>The maximum distance a simplified contour's border edges should deviate the original raw contour. [Limit: &gt;=0] [Units: vx]</summary>
	public float MaxSimplificationError;

	/// <summary>The minimum number of cells allowed to form isolated island areas. [Limit: &gt;=0] [Units: vx]</summary>
	public int MinRegionArea;

	/// <summary>Any regions with a span count smaller than this value will, if possible, be merged with larger regions. [Limit: &gt;=0] [Units: vx]</summary>
	public int MergeRegionArea;

	/// <summary>The maximum number of vertices allowed for polygons generated during the contour to polygon conversion process. [Limit: &gt;= 3]</summary>
	public int MaxVertsPerPoly;
};
