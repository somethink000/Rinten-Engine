using System.Runtime.InteropServices;
using Editor.Inspectors;

namespace Editor.MeshEditor;

partial class VertexPaintTool
{
	public override Panel CreateToolSidebarPanel()
	{
		return new VertexPaintToolPanel( this );
	}

	[StyleSheet( "VertexPaintTool.UI.cs.scss" )]
	public class VertexPaintToolPanel : ToolSettingsPanel
	{
		readonly Panel _blendRow;
		readonly Panel _channels;
		readonly Inspectors.ControlSheet _paintRow;
		readonly Rinten.UI.Label _selectionCountLabel;
		readonly VertexPaintTool _tool;
		readonly Panel _noiseSettings;
		readonly Panel _occlusionSettings;
		readonly Panel _curvatureSettings;

		ColorPickerPopup _colorPicker;

		//What the selection count last said, so it is only written again when it moves.
		(PaintLimitMode Mode, int Count) _shownCount = (PaintLimitMode.Everything, -1);

		[Shortcut( "vertexpainttool.OpenColorPicker", "F", typeof( SceneViewPanel ) )]
		void OpenColorPicker()
		{
			if ( _tool.Mode != PaintMode.Color )
				return;

			//Already open: the shortcut again is not a second picker.
			if ( _colorPicker.IsValid() ) return;

			//Under the cursor, the way it always opened: the key is pressed with the pointer in the view, and outside the
			//mapping dock this panel is built for its keys and kept out of sight - see ToolSidebarPanel.BuildSettings.
			_colorPicker = HostWidget is { } host && host.IsValid()
				? ColorPickerPopup.OpenFor( host, _tool.Color, c => _tool.Color = c, hasAlpha: false, hdr: false )
				: ColorPickerPopup.Open( _paintRow, _tool.Color, c => _tool.Color = c, hasAlpha: false, hdr: false );
		}

		[Shortcut( "vertexpainttool.togglepaintmode", "M", typeof( SceneViewPanel ) )]
		void TogglePaintMode()
		{
			_tool.Mode = _tool.Mode == PaintMode.Blend ? PaintMode.Color : PaintMode.Blend;
			UpdateModeVisibility( _tool.Mode );
		}

		[Shortcut( "vertexpainttool.floodfill", "Backspace", typeof( SceneViewPanel ) )]
		void FloodFill() => _tool.FillSelection();

		[Shortcut( "vertexpainttool.invertfill", "SHIFT+Backspace", typeof( SceneViewPanel ) )]
		void InvertFill() => _tool.InvertSelection();

		[Shortcut( "vertexpainttool.resetpaint", "CTRL+Backspace", typeof( SceneViewPanel ) )]
		void ResetPaint() => _tool.ResetPaintData();

		public VertexPaintToolPanel( VertexPaintTool tool )
		{
			_tool = tool;

			AddTitle( "Vertex Paint Tool", "brush" );

			var so = tool.GetSerialized();

			{
				var group = AddGroup( "Paint On" );

				AddSheet( group.Content ).AddPropertyRow( so.GetProperty( nameof( tool.LimitMode ) ) );

				_selectionCountLabel = group.Content.AddChild<Rinten.UI.Label>( "note" );

				var sheet = AddSheet( group.Content );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.LimitToActiveMaterial ) ) );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.PaintBackfacing ) ) );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.ScaleWithDistance ) ) );
			}
			{
				var group = AddGroup( "Painting" );

				var modeProp = so.GetProperty( nameof( tool.Mode ) );

				var sheet = AddSheet( group.Content );
				sheet.AddPropertyRow( modeProp );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.Radius ) ) );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.Strength ) ) );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.Hardness ) ) );

				_blendRow = group.Content.AddChild<Panel>( "blend-row" );

				_paintRow = AddSheet( group.Content );
				_paintRow.AddPropertyRow( so.GetProperty( nameof( tool.Color ) ) );

				_channels = group.Content.AddChild<Panel>( "channels" );

				var material = tool.Tool.ActiveMaterial;
				var blendCount = GetVertexPaintLayerCount( material );

				var masks = new[]
				{
					(BlendMask.A, new Vector4( 0, 0, 0, 1 ), "0" ),
					(BlendMask.R, new Vector4( 1, 0, 0, 0 ), "1" ),
					(BlendMask.G, new Vector4( 0, 1, 0, 0 ), "2" ),
					(BlendMask.B, new Vector4( 0, 0, 1, 0 ), "3" ),
				};

				var blendButtons = new List<BlendButton>();

				if ( blendCount == 0 )
				{
					var label = _blendRow.AddChild<Rinten.UI.Label>( "note unsupported" );
					label.Text = "Active material does not support vertex painting.";
				}
				else
				{
					var size = blendCount switch
					{
						1 => 96,
						2 => 64,
						3 => 52,
						_ => 42
					};

					for ( int i = 0; i < blendCount + 1 && i < masks.Length; i++ )
					{
						var (maskId, maskVec, layerLabel) = masks[i];

						var button = new BlendButton( CreateBlendTexture( material, size, maskVec ), size, layerLabel )
						{
							Selected = i == 1
						};

						var channel = i;

						//Right-click takes the layer alone; left-click takes it and leaves the ones above it on - the same two the widget had.
						button.Clicked = ( right ) =>
						{
							foreach ( var b in blendButtons )
								b.Selected = false;

							button.Selected = true;

							if ( right ) tool.SetChannelDisableOther( channel );
							else tool.SetChannelEnableOther( channel );
						};

						blendButtons.Add( button );
						_blendRow.AddChild( button );

						if ( maskId == BlendMask.R ) AddChannel( so.GetProperty( nameof( tool.ChannelR ) ), channel );
						if ( maskId == BlendMask.G ) AddChannel( so.GetProperty( nameof( tool.ChannelG ) ), channel );
						if ( maskId == BlendMask.B ) AddChannel( so.GetProperty( nameof( tool.ChannelB ) ), channel );
						if ( maskId == BlendMask.A ) AddChannel( so.GetProperty( nameof( tool.ChannelA ) ), channel );
					}
				}

				modeProp.OnChanged += ( e ) => UpdateModeVisibility( tool.Mode );
			}

			{
				var group = AddGroup( "Fill Options", collapsible: true );

				//Stacked one to a row as they always were, and with their keys on them now - Backspace and its two chords.
				var grid = AddButtons( group, 1 );

				AddButton( grid, "Flood Fill", "format_color_fill", "vertexpainttool.floodfill", () => tool.FillSelection() )
					.Tooltip = "Fill the current selection with the active color/blend";

				AddButton( grid, "Invert Selection", "flip", "vertexpainttool.invertfill", () => tool.InvertSelection() )
					.Tooltip = "Invert the current selection";

				AddButton( grid, "Reset Paint Data", "format_color_reset", "vertexpainttool.resetpaint", () => tool.ResetPaintData() )
					.Tooltip = "Reset the selection back to default";

				var fillModeProp = so.GetProperty( nameof( tool.FillMode ) );
				AddSheet( group.Content ).AddPropertyRow( fillModeProp );

				_noiseSettings = group.Content.AddChild<Panel>( "fill-settings" );

				var noiseSheet = AddSheet( _noiseSettings );
				noiseSheet.AddPropertyRow( so.GetProperty( nameof( tool.NoiseScale ) ) );
				noiseSheet.AddPropertyRow( so.GetProperty( nameof( tool.NoiseSeed ) ) );
				noiseSheet.AddPropertyRow( so.GetProperty( nameof( tool.NoiseContrast ) ) );

				_noiseSettings.AddChild( new NoisePreviewPanel( tool ) );

				_occlusionSettings = group.Content.AddChild<Panel>( "fill-settings" );
				AddSheet( _occlusionSettings ).AddPropertyRow( so.GetProperty( nameof( tool.OcclusionRadius ) ) );

				_curvatureSettings = group.Content.AddChild<Panel>( "fill-settings" );
				AddSheet( _curvatureSettings ).AddPropertyRow( so.GetProperty( nameof( tool.CurvatureAngle ) ) );

				fillModeProp.OnChanged += ( e ) => UpdateFillModeVisibility( tool.FillMode );
			}

			{
				var group = AddGroup( "Visualization", collapsible: true );

				var sheet = AddSheet( group.Content );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.ShowVerts ) ) );
				sheet.AddPropertyRow( so.GetProperty( nameof( tool.ShowSelection ) ) );
			}

			AddStretch();

			AddShortcuts(
				("Paint", "LMB"),
				("Erase", "Ctrl+LMB"),
				("Sample Color", "(Hold) V"),
				("Adjust Radius", "Shift+MMB Drag"),
				("Adjust Strength", "Ctrl+MMB ↕"),
				("Adjust Hardness", "Ctrl+MMB ↔"),
				("Add Selection", "Shift+RMB")
			);

			UpdateModeVisibility( tool.Mode );
			UpdateFillModeVisibility( tool.FillMode );
			UpdateSelectionCount();
		}

		//One layer's switch and amount: whether the brush writes it, and how much. The two controls bare rather than as sheet
		//rows - a row each would put "Enabled" and "Value" beside every layer, and the number in front says which layer it is.
		void AddChannel( SerializedProperty property, int channel )
		{
			if ( !property.TryGetAsObject( out var so ) ) return;

			var row = _channels.AddChild<Panel>( "channel" );

			var label = row.AddChild<Rinten.UI.Label>( "channel-label" );
			label.Text = $"{channel}";

			if ( PanelControlFactory.CreateFor( so.GetProperty( nameof( Channel.Enabled ) ) ) is { } enabled )
				row.AddChild( enabled ).AddClass( "channel-enabled" );

			if ( PanelControlFactory.CreateFor( so.GetProperty( nameof( Channel.Value ) ) ) is { } value )
				row.AddChild( value ).AddClass( "channel-value" );
		}

		static int GetVertexPaintLayerCount( Material material )
		{
			if ( !material.IsValid() )
				return 0;

			if ( material.Flags.GetInt( "VertexPaintUI5Layer" ) == 1 ) return 4;
			if ( material.Flags.GetInt( "VertexPaintUI4Layer" ) == 1 ) return 3;
			if ( material.Flags.GetInt( "VertexPaintUI3Layer" ) == 1 ) return 2;
			if ( material.Flags.GetInt( "VertexPaintUI2Layer" ) == 1 ) return 1;

			return 0;
		}

		void UpdateModeVisibility( PaintMode mode )
		{
			_blendRow.SetClass( "hidden", mode != PaintMode.Blend );
			_channels.SetClass( "hidden", mode != PaintMode.Blend );
			_paintRow.SetClass( "hidden", mode != PaintMode.Color );
		}

		void UpdateFillModeVisibility( PaintFillMode mode )
		{
			_noiseSettings.SetClass( "hidden", mode != PaintFillMode.Noise );
			_occlusionSettings.SetClass( "hidden", mode != PaintFillMode.Occlusion );
			_curvatureSettings.SetClass( "hidden", mode != PaintFillMode.Curvature );
		}

		public override void Tick()
		{
			base.Tick();

			UpdateSelectionCount();
		}

		void UpdateSelectionCount()
		{
			if ( _tool.LimitMode == PaintLimitMode.Everything )
			{
				_selectionCountLabel.SetClass( "hidden", true );
				_shownCount = (PaintLimitMode.Everything, -1);
				return;
			}

			var count = _tool.LimitMode switch
			{
				PaintLimitMode.Objects => _tool._selectedMeshes.Count,
				PaintLimitMode.Faces => _tool.GetSelectedElements<MeshFace>().Count(),
				PaintLimitMode.Edges => _tool.GetSelectedElements<MeshEdge>().Count(),
				PaintLimitMode.Vertices => _tool.GetSelectedElements<Editor.MeshEditor.MeshVertex>().Count(),
				_ => 0
			};

			_selectionCountLabel.SetClass( "hidden", false );

			//Counted every frame, the way the widget counted it; written only when the count or what is being counted changes.
			if ( _shownCount == (_tool.LimitMode, count) ) return;

			_shownCount = (_tool.LimitMode, count);

			var name = _tool.LimitMode switch
			{
				PaintLimitMode.Objects => count == 1 ? "object" : "objects",
				PaintLimitMode.Faces => count == 1 ? "face" : "faces",
				PaintLimitMode.Edges => count == 1 ? "edge" : "edges",
				PaintLimitMode.Vertices => count == 1 ? "vertex" : "vertices",
				_ => "selected"
			};

			_selectionCountLabel.Text = $"{count} {name} selected";
		}

		//One blend layer: the material as that layer paints it, and its number under it. Lit when it is the layer being painted.
		sealed class BlendButton : Panel
		{
			/// <summary>Told which button pressed it: true for the right.</summary>
			public Action<bool> Clicked;

			public bool Selected
			{
				get => HasClass( "selected" );
				set => SetClass( "selected", value );
			}

			public BlendButton( Texture preview, float size, string label )
			{
				AddClass( "blend-button" );

				var picture = AddChild<Rinten.UI.Image>( "preview" );
				picture.Texture = preview;
				picture.Style.Width = size;
				picture.Style.Height = size;

				var text = AddChild<Rinten.UI.Label>( "label" );
				text.Text = label;
			}

			protected override void OnClick( MousePanelEvent e )
			{
				base.OnClick( e );

				Clicked?.Invoke( false );
				e.StopPropagation();
			}

			protected override void OnRightClick( MousePanelEvent e )
			{
				base.OnRightClick( e );

				Clicked?.Invoke( true );
				e.StopPropagation();
			}
		}

		//What the noise fill will lay down, in grey: the same falloff the fill reads, sampled over a flat strip. At half the panel's
		//resolution, as the widget drew it, and made again only when the width or one of the three settings it reads has moved.
		sealed class NoisePreviewPanel : Rinten.UI.Image
		{
			//Half the ninety six it is drawn at.
			const int PixelHeight = 48;

			readonly VertexPaintTool _tool;

			int _width;
			float _scale;
			int _seed;
			float _contrast;

			public NoisePreviewPanel( VertexPaintTool tool )
			{
				_tool = tool;

				AddClass( "noise-preview" );
			}

			public override void Tick()
			{
				base.Tick();

				//Nothing to size it by until it has been laid out.
				if ( Box.Rect.Width <= 0 || Box.Rect.Height <= 0 ) return;

				var w = Math.Max( 8, (int)(PixelHeight * Box.Rect.Width / Box.Rect.Height) );

				if ( Texture is not null && _width == w
					&& _scale == _tool.NoiseScale && _seed == _tool.NoiseSeed && _contrast == _tool.NoiseContrast )
					return;

				_width = w;
				_scale = _tool.NoiseScale;
				_seed = _tool.NoiseSeed;
				_contrast = _tool.NoiseContrast;

				var h = PixelHeight;
				var unitsPerPixel = 2048f / h;
				var data = new byte[w * h * 4];

				for ( int y = 0; y < h; y++ )
				{
					for ( int x = 0; x < w; x++ )
					{
						var v = _tool.GetNoiseFalloff( new Vector3( x * unitsPerPixel, y * unitsPerPixel, 0 ) );
						var c = (byte)(v.Clamp( 0f, 1f ) * 255f);

						var i = (y * w + x) * 4;
						data[i + 0] = c;
						data[i + 1] = c;
						data[i + 2] = c;
						data[i + 3] = 255;
					}
				}

				Texture = Rinten.Texture.Create( w, h )
					.WithData( data )
					.WithName( "vertex_paint_noise_preview" )
					.Finish();
			}
		}

		[StructLayout( LayoutKind.Sequential )]
		struct MeshVertex( Vector3 position, Vector3 normal, Vector4 tangent, Vector2 texcoord, Color32 blend, Color32 color )
		{
			[VertexLayout.Position] public Vector3 Position = position;
			[VertexLayout.Normal] public Vector3 Normal = normal;
			[VertexLayout.Tangent] public Vector4 Tangent = tangent;
			[VertexLayout.TexCoord] public Vector2 Texcoord = texcoord;
			[VertexLayout.TexCoord( 4 )] public Color32 Blend = blend;
			[VertexLayout.TexCoord( 5 )] public Color32 Color = color;
		}

		static Mesh CreatePlane( Color32 mask )
		{
			var material = Material.Load( "materials/dev/gray_grid_8.vmat" );
			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( 4, new[]
			{
				//Laid on the ground, which the camera above it looks straight down on.
				new MeshVertex( new Vector3( -50, 0, -50 ), Vector3.Up, new Vector4( 1, 0, 0, 1 ), new Vector2( 0, 0 ), mask, Color.White ),
				new MeshVertex( new Vector3( 50, 0, -50 ), Vector3.Up,  new Vector4( 1, 0, 0, 1 ), new Vector2( 1, 0 ), mask, Color.White ),
				new MeshVertex( new Vector3( 50, 0, 50 ), Vector3.Up,  new Vector4( 1, 0, 0, 1 ), new Vector2( 1, 1 ), mask, Color.White ),
				new MeshVertex( new Vector3( -50, 0, 50 ), Vector3.Up,  new Vector4( 1, 0, 0, 1 ), new Vector2( 0, 1 ), mask, Color.White ),
			} );
			mesh.CreateIndexBuffer( 6, new[] { 0, 1, 2, 2, 3, 0 } );
			mesh.Bounds = BBox.FromPositionAndSize( 0, 100 );

			return mesh;
		}

		//The material as one blend layer paints it, as a picture a panel can show.
		static Texture CreateBlendTexture( Material material, Vector2 size, Vector4 mask )
		{
			var world = new SceneWorld();

			var camera = new SceneCamera
			{
				BackgroundColor = Color.Black,
				Ortho = true,
				Rotation = Rotation.FromPitch( 90 ),
				Position = Vector3.Up * 5.0f,
				OrthoHeight = 2.5f,
				World = world
			};

			var light = new SceneSpotLight( world )
			{
				Radius = 100.0f,
				LightColor = Color.White * 0.7f,
				Position = Vector3.Up * 2.5f,
				ConeOuter = 89,
				ConeInner = 75,
				ShadowsEnabled = true
			};

			light.Rotation = Rotation.From( 90, 0, 0 );

			var mesh = CreatePlane( new Color( mask.x, mask.y, mask.z, mask.w ) );
			var model = Model.Builder
				.AddMesh( mesh )
				.Create();

			var obj = new SceneObject( world, model );
			obj.Transform = new Transform
			{
				Position = Vector3.Zero,
				Rotation = Rotation.Identity,
				Scale = new Vector3( 1, 1, size.x / size.y )
			};

			obj.SetMaterialOverride( material );

			var pixmap = new Pixmap( size );
			camera.RenderToPixmap( pixmap );

			world.Delete();
			camera.Dispose();

			return pixmap.picture?.ToTexture( mips: false );
		}
	}
}
