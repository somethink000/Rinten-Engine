using Editor.TreeBrowser;
using Rinten.UI;

namespace Editor.TerrainEditor;

/// <summary>Modify terrains</summary>
[EditorTool]
[Title( "Terrain" )]
[Icon( "landscape" )]
[Alias( "terrain" )]
[Group( "Scene" )]
public partial class TerrainEditorTool : EditorTool
{
	public override IEnumerable<EditorTool> GetSubtools()
	{
		yield return new RaiseLowerTool( this );
		yield return new SmoothTool( this );
		yield return new NoiseTool( this );
		yield return new StampTool( this );
		yield return new FlattenTool( this );
		yield return new DigTool( this );
		yield return new PaintTextureTool( this );
		yield return new HoleTool( this );
		yield return new EmbedTool( this );
		//yield return new SetHeightTool();
	}

	public static BrushList BrushList { get; set; } = new();
	public static Brush Brush => BrushList.Selected;
	public BrushSettings BrushSettings { get; private set; } = new();

	public override Panel CreateShortcutsPanel() => new TerrainToolShortcutsPanel();

	public override void OnEnabled()
	{
		AllowGameObjectSelection = false;

		//if we don't have a terrain already selected.. just grab one
		var selectedTerrain = GetSelectedComponent<Terrain>();
		if ( !selectedTerrain.IsValid() )
		{
			Selection.Clear();
			var first = Scene.GetAllComponents<Terrain>().FirstOrDefault();
			if ( first.IsValid() ) Selection.Add( first.GameObject );
		}

		LoadToolbarCookies();
	}

	public override void OnDisabled()
	{
	}

	/// <summary>Create the sidebar widget for the terrain tool</summary>
	public override Widget CreateToolSidebar()
	{
		var sidebar = new ToolSidebarWidget();

		var info = CurrentTool is not null ? DisplayInfo.For( CurrentTool ) : default;
		var title = string.IsNullOrEmpty( info.Name ) ? "Brush Settings" : info.Name;
		var icon = string.IsNullOrEmpty( info.Icon ) ? "brush" : info.Icon;
		sidebar.AddTitle( title, icon );

		//Brush Type
		{
			var group = sidebar.AddGroup( "Brush Type" );
			group.Add( new BrushPreviewWidget( sidebar ) );
		}

		//Brush Properties
		ControlSheetRow opacityRow;
		{
			var group = sidebar.AddGroup( "Brush Properties" );

			var so = BrushSettings.GetSerialized();
			group.Add( ControlSheetRow.Create( so.GetProperty( nameof( BrushSettings.Size ) ) ) );

			opacityRow = ControlSheetRow.Create( so.GetProperty( nameof( BrushSettings.Opacity ) ) );
			group.Add( opacityRow );
			var rot = group.Add( ControlSheetRow.Create( so.GetProperty( nameof( BrushSettings.Rotation ) ) ) );
			rot.Enabled = !BrushSettings.RandomRotation;
			var rndRot = group.Add( ControlSheetRow.Create( so.GetProperty( nameof( BrushSettings.RandomRotation ) ) ) );
			group.Add( ControlSheetRow.Create( so.GetProperty( nameof( BrushSettings.ShowGridPreview ) ) ) );

			so.OnPropertyChanged += ( prop ) =>
			{
				if ( prop?.Name == nameof( BrushSettings.RandomRotation ) && rot.IsValid() )
					rot.Enabled = !BrushSettings.RandomRotation;
			};
		}

		if ( CurrentTool is StampTool )
		{
			var group = sidebar.AddGroup( "Stamp" );
			var so = StampTool.Settings.GetSerialized();

			group.Add( ControlSheetRow.Create( so.GetProperty( nameof( StampTool.StampSettings.HeightMap ) ) ) );
			group.Add( ControlSheetRow.Create( so.GetProperty( nameof( StampTool.StampSettings.Height ) ) ) );
			group.Add( ControlSheetRow.Create( so.GetProperty( nameof( StampTool.StampSettings.Mode ) ) ) );
		}

		//Paint-only sections — hidden unless PaintTextureTool is active
		var sidebarStretch = new Widget( sidebar ) { VerticalSizeMode = SizeMode.Flexible };
		var paintOnlySection = new PaintOnlySectionWidget( this, opacityRow, sidebarStretch );
		{
			//Material selection
			var terrain = GetSelectedComponent<Terrain>() ?? Scene.Get<Terrain>();

			if ( terrain.IsValid() && terrain.Storage is not null )
			{
				var group = ToolSidebarWidget.CreateGroupWidget( "Materials", SizeMode.Flexible );
				paintOnlySection.Layout.Add( group, 1 );

				var materialList = new TerrainMaterialList( paintOnlySection, terrain );
				materialList.ItemSize += 24;
				materialList.BuildItems();
				group.ContentLayout.Add( materialList, 1 );

				var hlayout = group.ContentLayout.AddRow();
				hlayout.Spacing = 8;
				hlayout.AddStretchCell();

				var newTerrainMat = new Button( "New Terrain Layer" );
				newTerrainMat.Clicked += () => NewTerrainLayer( terrain, materialList );

				var cloudMats = new Button( "Browse", "cloud" );
				cloudMats.Clicked += () =>
				{
					var picker = AssetPicker.Create( null, AssetType.FromExtension( "mat" ) );
					picker.OnAssetPicked = x =>
					{
						var material = x.First().LoadResource<Material>();
						if ( material is null || terrain.Storage.Materials.Count >= Terrain.MostLayers ) return;

						terrain.Storage.Materials.Add( material );
						terrain.UpdateMaterials();
						materialList?.BuildItems();
					};
					picker.Show();
				};

				hlayout.Add( cloudMats );
				hlayout.Add( newTerrainMat, 1 );
			}

			sidebar.Layout.Add( paintOnlySection, 1 );
			sidebar.Layout.Add( sidebarStretch, 1 );
		}

		SetOpacityToolTip( opacityRow );

		sidebar.AddShortcuts(
			("Adjust Size", "Shift+MMB ↔"),
			("Adjust Opacity", "Shift+MMB ↕"),
			("Rotate", "CTRL+MMB ↔"),
			("Switch brush preview", "Capslock")
			);

		return sidebar;
	}

	static void NewTerrainLayer( Terrain terrain, TerrainMaterialList materialList )
	{
		if ( terrain.Storage.Materials.Count >= Terrain.MostLayers || TerrainMaterialList.CreateLayer() is not { } material )
			return;

		terrain.Storage.Materials.Add( material );
		terrain.UpdateMaterials();
		materialList.BuildItems();
	}

	internal static void SetOpacityToolTip( ControlSheetRow row )
	{
		var tip = "Controls how strongly the selected material is painted";
		row.Enabled = true;
		row.ToolTip = tip;
		row.ControlWidget.ToolTip = tip;
	}

	/// <summary>Brush preview drawn with gizmo lines on the ground</summary>
	public void DrawBrushPreview( Transform transform, Terrain terrain = null )
	{
		DrawSimpleBrushPreview( transform.Position, transform, terrain );
	}

	public void DrawSimpleBrushPreview( Vector3 worldPos, Transform tx, Terrain terrain = null )
	{
		var color = Color.FromBytes( 150, 150, 250 );

		if ( Application.KeyboardModifiers.HasFlag( Rinten.KeyboardModifiers.Ctrl ) )
			color = color.AdjustHue( 90 );

		var size = BrushSettings.Size;
		var sections = (int)(MathF.Sqrt( size ) * 20.0f).Clamp( 16, 64 );
		var length = MathX.LerpTo( 0.5f, 1.25f, BrushSettings.Opacity );

		var rotRad = -BrushSettings.Rotation * MathF.PI / 180f;
		var tickDir = new Vector3( 0, MathF.Cos( rotRad ), MathF.Sin( rotRad ) );

		using ( Gizmo.Scope( "TerrainSimpleBrush", worldPos, Rotation.LookAt( tx.Rotation.Up ) ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.Color = color;
			Gizmo.Draw.LineThickness = 4;
			Gizmo.Draw.Line( Vector3.Zero, Vector3.Forward * length );
			Gizmo.Draw.SolidSphere( Vector3.Forward * length, 0.05f );
			Gizmo.Draw.LineCircle( Vector3.Zero, size, 32, sections: sections );

			//How hard the middle of the brush is, so the falloff is something you can see rather than something you find out by pressing.
			Gizmo.Draw.Color = color.WithAlpha( 0.35f );
			Gizmo.Draw.LineThickness = 1;
			Gizmo.Draw.LineCircle( Vector3.Zero, size * 0.5f, 32, sections: sections );

			Gizmo.Draw.LineThickness = 2;
			Gizmo.Draw.Color = color.WithAlpha( 0.6f );
			Gizmo.Draw.Line( tickDir * size * 0.7f, tickDir * size );
		}

		if ( BrushSettings.ShowGridPreview && terrain?.Storage is not null )
			DrawTerrainGrid( worldPos, terrain, size, color );
	}

	//Draws the mesh edges under the brush, showing what will move and how remeshing changes it
	private static void DrawTerrainGrid( Vector3 worldPos, Terrain terrain, float size, Color color )
	{
		if ( terrain.Mesh is not { } mesh ) return;

		var world = terrain.WorldTransform;
		var local = world.PointToLocal( worldPos );
		var reach = size / MathF.Max( world.UniformScale, 1e-6f );

		gridTriangles.Clear();
		mesh.TrianglesInRect( new Vector2( local.x - reach, local.z - reach ), new Vector2( local.x + reach, local.z + reach ), gridTriangles );

		//A brush over more than this is a solid block of lines on screen.
		if ( gridTriangles.Count > 4000 ) return;

		using ( Gizmo.Scope( "TerrainGrid" ) )
		{
			Gizmo.Draw.IgnoreDepth = false;
			Gizmo.Draw.LineThickness = 1;
			Gizmo.Draw.Color = color.WithAlpha( 0.3f );

			foreach ( var t in gridTriangles )
			{
				mesh.GetTriangle( t, out var a, out var b, out var c );

				var pa = mesh.PositionOf( a );
				var pb = mesh.PositionOf( b );
				var pc = mesh.PositionOf( c );

				//In the ball, not just over the square: a cave's roof over the floor being painted is not under the brush.
				if ( ( pa - local ).Length > reach && ( pb - local ).Length > reach && ( pc - local ).Length > reach ) continue;

				//Lifted off the surface a little, each corner along its own normal, so the lines are not lost in it.
				var la = world.PointToWorld( pa + ( mesh.NormalOf( a ) * 0.02f ) );
				var lb = world.PointToWorld( pb + ( mesh.NormalOf( b ) * 0.02f ) );
				var lc = world.PointToWorld( pc + ( mesh.NormalOf( c ) * 0.02f ) );

				Gizmo.Draw.Line( la, lb );
				Gizmo.Draw.Line( lb, lc );
				Gizmo.Draw.Line( lc, la );
			}
		}
	}

	private static readonly List<int> gridTriangles = new();

	[Event( "scene.saved" )]
	static void OnSceneSaved( Scene scene )
	{
		foreach ( var terrain in scene.Components.GetAll<Terrain>( FindMode.EverythingInDescendants ) )
		{
			if ( terrain.Storage is null ) continue;
			if ( string.IsNullOrEmpty( terrain.Storage.ResourcePath ) ) continue;

			var asset = AssetSystem.FindByPath( terrain.Storage.ResourcePath );
			asset?.SaveToDisk( terrain.Storage );
		}
	}
}

//Container that shows/hides itself based on whether PaintTextureTool is the active subtool.
internal class PaintOnlySectionWidget : Widget
{
	readonly TerrainEditorTool _tool;
	readonly ControlSheetRow _opacityRow;
	readonly Widget _sidebarStretch;

	public PaintOnlySectionWidget( TerrainEditorTool tool, ControlSheetRow opacityRow, Widget sidebarStretch ) : base( null )
	{
		_tool = tool;
		_opacityRow = opacityRow;
		_sidebarStretch = sidebarStretch;
		Layout = Layout.Column();
		Layout.Spacing = 2;
		VerticalSizeMode = SizeMode.Flexible;
	}

	[EditorEvent.Frame]
	void Tick()
	{
		bool isPaint = _tool.CurrentTool is PaintTextureTool;
		Hidden = !isPaint;

		if ( _sidebarStretch.IsValid() )
			_sidebarStretch.Hidden = isPaint;

		if ( !_opacityRow.IsValid() ) return;

		if ( !isPaint )
		{
			_opacityRow.Enabled = true;
			_opacityRow.ToolTip = "";
			if ( _opacityRow.ControlWidget.IsValid() ) _opacityRow.ControlWidget.ToolTip = "";
		}
		else
		{
			TerrainEditorTool.SetOpacityToolTip( _opacityRow );
		}
	}
}

//Widget that shows the currently selected brush and opens a popup picker
internal class BrushPreviewWidget : Widget
{
	public BrushPreviewWidget( Widget parent ) : base( parent )
	{
		MinimumSize = new( 32, 32 );
		MaximumSize = new( 48, 48 );

		Cursor = CursorShape.Finger;
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.DrawRect( LocalRect );

		var pixmap = TerrainEditorTool.Brush?.Pixmap;
		if ( pixmap != null )
		{
			Paint.Draw( LocalRect.Contain( pixmap.Size ), pixmap );
		}
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		var popup = new PopupWidget( null );
		popup.Position = Application.CursorPosition;
		popup.Visible = true;
		popup.Layout = Layout.Column();
		popup.Layout.Margin = 10;
		popup.MaximumSize = new Vector2( 300, 150 );

		var list = new BrushListWidget();
		list.BrushSelected += () =>
		{
			popup.Close();
			Update();
		};
		popup.Layout.Add( list );
	}

	[EditorEvent.Frame]
	public void UpdatePreview()
	{
		Update();
	}
}

file class TerrainToolShortcutsPanel : EditorPanel
{
	[Shortcut( "tools.terrain.raise-lower", "1", typeof( SceneViewPanel ) )]
	public void ActivateRaiseLowerTool() => EditorToolManager.SetSubTool( nameof( RaiseLowerTool ) );
	[Shortcut( "tools.terrain.smooth", "2", typeof( SceneViewPanel ) )]
	public void ActivateSmoothTool() => EditorToolManager.SetSubTool( nameof( SmoothTool ) );
	[Shortcut( "tools.terrain.flatten", "3", typeof( SceneViewPanel ) )]
	public void ActivateFlattenTool() => EditorToolManager.SetSubTool( nameof( FlattenTool ) );
	[Shortcut( "tools.terrain.dig", "4", typeof( SceneViewPanel ) )]
	public void ActivateDigTool() => EditorToolManager.SetSubTool( nameof( DigTool ) );
	[Shortcut( "tools.terrain.paint-texture", "5", typeof( SceneViewPanel ) )]
	public void ActivatePaintTextureTool() => EditorToolManager.SetSubTool( nameof( PaintTextureTool ) );
	[Shortcut( "tools.terrain.hole", "6", typeof( SceneViewPanel ) )]
	public void ActivateHoleTool() => EditorToolManager.SetSubTool( nameof( HoleTool ) );
	[Shortcut( "tools.terrain.embed", "7", typeof( SceneViewPanel ) )]
	public void ActivateEmbedTool() => EditorToolManager.SetSubTool( nameof( EmbedTool ) );
	[Shortcut( "tools.terrain.noise", "8", typeof( SceneViewPanel ) )]
	public void ActivateNoiseTool() => EditorToolManager.SetSubTool( nameof( NoiseTool ) );
}
