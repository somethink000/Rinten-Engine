
using Rinten.Rendering;

namespace Rinten;

//I don't want to expose SceneObjects to people anymore. They should be using the scene system.
//It's better if we can keep things internal so we don't have the burden of supporting api layout.
internal class SceneTrailObject : SceneLineObject
{
	record struct TrailPoint( Vector3 Position, Vector3 Normal, float Uv, float Delta );

	//Ring buffer of points, oldest first; dropping the oldest is O(1), unlike List.RemoveRange
	TrailPoint[] _points = new TrailPoint[32];
	int _head;
	int _count;

	TrailPoint? lastPoint;

	//The i-th point, oldest first.
	private ref TrailPoint At( int i ) => ref _points[( _head + i ) % _points.Length];

	//Makes room for MaxPoints points, keeping what is held.
	private void Room( int wanted )
	{
		if ( wanted <= _points.Length ) return;

		var grown = new TrailPoint[wanted];

		for ( var i = 0; i < _count; i++ ) grown[i] = At( i );

		_points = grown;
		_head = 0;
	}

	/// <summary>Total maximum points we're allowing</summary>
	public int MaxPoints { get; set; } = 32;

	/// <summary>Wait until we're this far away before adding a new point</summary>
	public float PointDistance { get; set; } = 5;

	/// <summary>Texture details, in a nice stuct to hide the bs</summary>
	public TrailTextureConfig Texturing { get; set; } = TrailTextureConfig.Default;

	public Gradient TrailColor { get; set; } = global::Color.White;
	public Curve Width { get; set; } = 10;
	public Color LineTint { get; set; } = Color.White;
	public float LineScale { get; set; } = 1;

	public BlendMode BlendMode
	{
		get => Attributes.GetComboEnum( "D_BLENDMODE", BlendMode.Normal );
		set => Attributes.SetComboEnum( "D_BLENDMODE", value );
	}

	/// <summary>How long the trail lasts - or 0 for infinite</summary>
	public float LifeTime { get; set; } = 1.0f;

	float scrollTime = 0;

	public SceneTrailObject( SceneWorld sceneWorld ) : base( sceneWorld )
	{
		BlendMode = BlendMode.Normal;
	}

	public bool IsEmpty => _count <= 1;

	/// <summary>Forget every point and the scroll, keeping the room they took - for a trail handed to another particle.</summary>
	public void Reset()
	{
		_count = 0;
		_head = 0;
		lastPoint = null;
		scrollTime = 0;

		Clear();
	}
	public int PointCount => _count;

	/// <summary>Try to add a position to this trail. Returns true on success.</summary>
	public bool TryAddPosition( Vector3 worldPosition )
	{
		return TryAddPosition( worldPosition, Vector3.Up );
	}

	/// <summary>Try to add a position to this trail. Returns true on success.</summary>
	public bool TryAddPosition( Vector3 worldPosition, Vector3 worldNormal )
	{
		float distance = lastPoint?.Position.Distance( worldPosition ) ?? 0;

		if ( lastPoint.HasValue && distance < PointDistance )
			return false;

		float uv = lastPoint?.Uv ?? 0;

		uv += distance;

		lastPoint = new TrailPoint( worldPosition, worldNormal, uv, 1 );

		Room( Math.Max( MaxPoints, 1 ) );

		if ( _count < _points.Length )
		{
			_points[( _head + _count ) % _points.Length] = lastPoint.Value;
			_count++;
		}
		else
		{
			//Full: the new point takes the oldest one's place and the ring turns.
			_points[_head] = lastPoint.Value;
			_head = ( _head + 1 ) % _points.Length;
		}

		//And where the limit has been lowered since, the extra oldest ones go.
		var most = Math.Max( MaxPoints, 1 );

		if ( _count > most )
		{
			_head = ( _head + ( _count - most ) ) % _points.Length;
			_count = most;
		}

		return true;
	}

	/// <summary>Advance the time for this trail. Will fade out points and scoll the texture.</summary>
	public void AdvanceTime( float f )
	{
		scrollTime += f * Texturing.Scroll;

		if ( LifeTime <= 0 )
			return;

		f = f / LifeTime;

		int kept = 0;

		for ( int i = 0; i < _count; i++ )
		{
			var e = At( i );
			e.Delta -= f;

			if ( e.Delta <= 0 ) continue;

			At( kept++ ) = e;
		}

		_count = kept;
	}

	/// <summary>Build the vertices for this object. TODO: We can move this to build automatically in a thread</summary>
	public void Build()
	{
		var count = _count;
		if ( count <= 1 )
		{
			Clear();
			return;
		}

		Material = Texturing.Material;

		StartLine();

		for ( int j = count - 1; j >= 0; j-- )
		{
			var p = At( j );

			float delta = 1 - ((float)j / (float)count);

			float uv = 0;

			if ( !Texturing.WorldSpace )
			{
				uv = delta * Texturing.Scale;
			}
			else
			{
				uv = p.Uv / Texturing.UnitsPerTexture;
			}

			uv += scrollTime + Texturing.Offset;

			delta = 1 - (p.Delta * (1 - delta));
			float width = Width.Evaluate( delta ) * 0.5f;
			AddLinePoint( p.Position, p.Normal, TrailColor.Evaluate( delta ) * LineTint, width * LineScale, uv );
		}

		EndLine();
	}
}

/// <summary>Defines how a trail is going to be textured. Used by TrailRenderer.</summary>
[Expose]
public struct TrailTextureConfig
{
	public TrailTextureConfig()
	{

	}

	public static TrailTextureConfig Default { get; } = new TrailTextureConfig
	{
		WorldSpace = true,
		UnitsPerTexture = 10,
		Scale = 1,
		Offset = 0,
		Scroll = 0,
		FilterMode = FilterMode.Anisotropic,
		TextureAddressMode = TextureAddressMode.Wrap
	};

	[Hide, Obsolete( "Use Material property instead" )]
	public Texture Texture { get; set; }

	[KeyProperty, Validate( nameof( DoesMaterialUseLineShader ), "Material should derive from 'line.shader'.", LogLevel.Warn )]
	public Material Material { get; set; }

	[Group( "Texture Coordinates" )]
	[Property]
	public bool WorldSpace { get; set; }

	[Group( "Texture Coordinates" )]
	[ShowIf( "WorldSpace", true )]
	[Property]
	public float UnitsPerTexture { get; set; }

	[Group( "Texture Coordinates" )]
	[ShowIf( "WorldSpace", false )]
	[Property]
	public float Scale { get; set; }

	[Group( "Texture Coordinates" )]
	[Property]
	public float Offset { get; set; }

	[Group( "Texture Coordinates" )]
	[Property]
	public float Scroll { get; set; }

	[Group( "Texture Coordinates" )]
	[Property]
	public FilterMode FilterMode { get; set; } = FilterMode.Anisotropic;

	[Group( "Texture Coordinates" )]
	[Property]
	public TextureAddressMode TextureAddressMode { get; set; } = TextureAddressMode.Wrap;

	/// <summary>If true the texture will be clamped instead of repeating</summary>
	[Hide, Obsolete( "Use Texture Address Mode property instead" )]
	public bool Clamp { get; set; }

	public bool DoesMaterialUseLineShader( Material value )
	{
		//Null is fine, in that case we will load a default material
		return value == null || value.ShaderName == "shaders/line.shader";
	}
}
