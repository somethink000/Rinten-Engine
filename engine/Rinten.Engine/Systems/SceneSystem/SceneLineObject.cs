using Rinten.Rendering;
using System.Numerics;

namespace Rinten;

/// <summary>A scene object which is used to draw lines</summary>
public class SceneLineObject : SceneCustomObject
{
	public enum CapStyle
	{
		None = 0,
		[Icon( "signal_cellular_4_bar" )]
		Triangle = 1,
		[Icon( "label_important" )]
		Arrow = 2,
		[Icon( "fiber_smart_record" )]
		Rounded = 3
	}

	public enum FaceMode
	{
		[Icon( "settings_ethernet" )]
		Camera = 0,
		[Icon( "rotate_90_degrees_cw" )]
		Normal = 1,
		[Icon( "circle" )]
		Cylinder = 2,
	}

	[Obsolete( "Use Material property instead", false )]
	public Texture LineTexture
	{
		get => Attributes.GetTexture( "BaseTexture" );
		set => Attributes.Set( "BaseTexture", value ?? Texture.White );
	}

	private static Material _defaultMaterial => field ??= Material.FromShader( "line" );
	public Material Material = _defaultMaterial;

	public CapStyle StartCap
	{
		get => (CapStyle)Attributes.GetInt( "StartCap" );
		set => Attributes.Set( "StartCap", (int)value );
	}

	public CapStyle EndCap
	{
		get => (CapStyle)Attributes.GetInt( "EndCap" );
		set => Attributes.Set( "EndCap", (int)value );
	}

	/// <summary>What the ribbon turns to face: the eye, or each point's own normal.</summary>
	public FaceMode Face { get; set; }

	public bool Wireframe
	{
		get => Attributes.GetComboBool( "D_WIREFRAME" );
		set => Attributes.SetCombo( "D_WIREFRAME", value );
	}

	public bool Lighting
	{
		get => Attributes.GetComboBool( "D_ENABLE_LIGHTING" );
		set => Attributes.SetCombo( "D_ENABLE_LIGHTING", value );
	}

	[Obsolete( "Use Sampler State property instead" )]
	public bool Clamped
	{
		get => _clamped;
		set
		{
			_clamped = value;
			if ( _clamped )
			{
				Attributes.Set( "SamplerIndex", SamplerState.GetBindlessIndex( ClampSampler ) );
			}
			else
			{
				Attributes.Set( "SamplerIndex", SamplerState.GetBindlessIndex( WrapSampler ) );
			}
		}
	}

	private bool _clamped = false;

	public SamplerState SamplerState
	{
		get => _samplerState;
		set
		{
			_samplerState = value;
			Attributes.Set( "SamplerIndex", SamplerState.GetBindlessIndex( _samplerState ) );
		}
	}

	private SamplerState _samplerState = new() { Filter = FilterMode.Anisotropic, MaxAnisotropy = 8, AddressModeU = TextureAddressMode.Wrap, AddressModeV = TextureAddressMode.Wrap };

	public int Smoothness
	{
		get => Attributes.GetInt( "Smoothness" );
		set => Attributes.Set( "Smoothness", value );
	}

	public bool Opaque
	{
		set
		{
			Attributes.SetCombo( "D_OPAQUE", value ? 1 : 0 );
			Flags.IsOpaque = value;
			Flags.IsTranslucent = !value;
		}
	}

	private int _tessellationLevel = 1;

	/// <summary>Number of tessellation subdivisions across the width of each line segment. 1 = no tessellation (just left and right), 2 = one subdivision in the middle, etc. Higher values create smoother curves and more detailed geometry but use more vertices.</summary>
	public int TessellationLevel
	{
		get => _tessellationLevel;
		set => _tessellationLevel = Math.Max( 1, value );
	}

	private struct LinePoint( in Vector3 position, in Vector3 normal, Color color, float width, float textureCoord )
	{
		public Vector3 Position = position;
		public Vector3 Normal = normal;
		public Color Color = color;
		public float Width = width;
		public float TextureCoord = textureCoord;
	}

	private static readonly SamplerState WrapSampler = new() { Filter = FilterMode.Anisotropic, MaxAnisotropy = 8, AddressModeU = TextureAddressMode.Wrap, AddressModeV = TextureAddressMode.Wrap };
	private static readonly SamplerState ClampSampler = new() { Filter = FilterMode.Anisotropic, MaxAnisotropy = 8, AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp };
	private LinePoint[] _points = [];
	private BBox _bounds;

	private int _pointCount;

	public SceneLineObject( SceneWorld sceneWorld ) : base( sceneWorld )
	{
#pragma warning disable CS0618
		LineTexture = Texture.White;
#pragma warning restore CS0618
	}

	public void StartLine()
	{
		_pointCount = 0;

		_bounds = BBox.FromPositionAndSize( Transform.Position, 10 );
	}

	public void AddLinePoint( in Vector3 pos, Color color, float width )
	{
		AddLinePoint( pos, color, width, _pointCount / 20.0f );
	}

	public void AddLinePoint( in Vector3 pos, Color color, float width, float textureCoord )
	{
		AddLinePoint( pos, Vector3.Up, color, width, textureCoord );
	}

	public void AddLinePoint( in Vector3 pos, in Vector3 normal, Color color, float width, float textureCoord )
	{
		if ( _pointCount > 0 && _points[_pointCount - 1].Position.DistanceSquared( pos ) < 0.01f )
			return;

		if ( _pointCount >= _points.Length )
		{
			Array.Resize( ref _points, Math.Max( 8, _points.Length * 2 ) );
		}

		_points[_pointCount] = new LinePoint( pos, normal, color, width, textureCoord );

		_pointCount++;
		_bounds = _bounds.AddPoint( pos );
	}

	//The ribbon is made from the points when a view draws it - see RenderSceneObject
	public void EndLine()
	{
		Bounds = _bounds;
	}

	/// <summary>Drop every point, keeping the room they took.</summary>
	public void Clear()
	{
		_pointCount = 0;
	}

	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		//Built on the CPU per view, which is what the facing needs, and drawn through the immediate path
		DrawRibbon();
	}

	private static Vertex[] _ribbonVertices = new Vertex[256];
	private static ushort[] _ribbonIndices = new ushort[768];

	private void DrawRibbon()
	{
		if ( _pointCount < 2 ) return;

		//Sixteen-bit indices: a ribbon longer than that is not a trail.
		var count = Math.Min( _pointCount, 32000 );

		var vertexCount = count * 2;
		var indexCount = ( count - 1 ) * 6;

		if ( _ribbonVertices.Length < vertexCount ) _ribbonVertices = new Vertex[vertexCount * 2];
		if ( _ribbonIndices.Length < indexCount ) _ribbonIndices = new ushort[indexCount * 2];

		var eye = Graphics.CameraPosition;
		var face = Face;

		for ( var i = 0; i < count; i++ )
		{
			ref readonly var point = ref _points[i];

			//Along the line: towards the next point, or from the last one at the very end, so the ribbon does not pinch where it stops.
			var along = i + 1 < count
				? _points[i + 1].Position - point.Position
				: point.Position - _points[i - 1].Position;

			if ( along.IsNearlyZero() ) along = Vector3.Forward;

			//Across it: perpendicular to the line and to what it faces - the eye for a trail, its own normal for a line laid on something.
			var facing = face == FaceMode.Normal
				? point.Normal
				: eye - point.Position;

			var across = Vector3.Cross( along, facing );

			if ( across.IsNearlyZero() ) across = Vector3.Cross( along, Vector3.Up );
			if ( across.IsNearlyZero() ) across = Vector3.Cross( along, Vector3.Right );

			//A point's width is its half-extent - the trail hands it over halved already
			across = across.Normal * point.Width;

			var colour = point.Color.ToColor32();
			var u = point.TextureCoord;

			_ribbonVertices[i * 2] = new Vertex( point.Position - across, new Vector4( u, 0.0f, 0.0f, 0.0f ), colour );
			_ribbonVertices[i * 2 + 1] = new Vertex( point.Position + across, new Vector4( u, 1.0f, 0.0f, 0.0f ), colour );
		}

		for ( var i = 0; i < count - 1; i++ )
		{
			var a = (ushort)( i * 2 );
			var at = i * 6;

			_ribbonIndices[at + 0] = a;
			_ribbonIndices[at + 1] = (ushort)( a + 1 );
			_ribbonIndices[at + 2] = (ushort)( a + 2 );
			_ribbonIndices[at + 3] = (ushort)( a + 1 );
			_ribbonIndices[at + 4] = (ushort)( a + 3 );
			_ribbonIndices[at + 5] = (ushort)( a + 2 );
		}

		//In the vertex colours alone. Their line material was a shader of its own with a colour map in it, and the immediate path has
		//no way to run that; a ribbon coloured along its length by the trail's gradient is nearly all of what the texture did anyway.
		Graphics.DrawColoured( _ribbonVertices.AsSpan(), vertexCount, _ribbonIndices.AsSpan(), indexCount );
	}
}
