using NativeEngine;

namespace Rinten;

/// <summary>Emits light from a point in space, or from a panel, disc or tube - see <see cref="AreaShape"/>.</summary>
[Expose]
[Title( "Point Light" )]
[Category( "Light" )]
[Icon( "light_mode" )]
[EditorHandle( "materials/gizmo/pointlight.png" )]
[Alias( "PointLightComponent" )]
public class PointLight : Light
{
	ScenePointLight _so;

	[Property]
	public float Radius
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			if ( _so.IsValid() )
				_so.Radius = value;
		}
	} = 5.0f;

	/// <summary>What the light gives off from: a point, or a panel, disc or tube of <see cref="AreaSize"/>.</summary>
	[Property, Group( "Shape" ), Title( "Shape" )]
	public AreaShapes AreaShape { get; set; }

	//A panel's width along right and height along up; a disc's diameter and a tube's length are x
	//A tube's radius is Source Width
	/// <summary>How big the shape is, in metres.</summary>
	[Property, Group( "Shape" ), Title( "Size" ), HideIf( nameof( AreaShape ), AreaShapes.Point )]
	public Vector2 AreaSize { get; set; } = new( 1.0f, 0.5f );

	/// <summary>What a point light gives off from - see <see cref="AreaShape"/>.</summary>
	public enum AreaShapes
	{
		/// <summary>A point, or a ball as wide as its source width.</summary>
		[Icon( "circle" )]
		Point = 0,

		/// <summary>A panel facing forward, lit on its front.</summary>
		[Icon( "rectangle" )]
		Rectangle = 1,

		/// <summary>A round panel facing forward, lit on its front.</summary>
		[Icon( "radio_button_unchecked" )]
		Disc = 2,

		/// <summary>A glowing tube along its right.</summary>
		[Icon( "horizontal_rule" )]
		Tube = 3,
	}

	protected override ScenePointLight CreateSceneObject()
	{
		return _so = new ScenePointLight( Scene.SceneWorld, WorldPosition, Radius, LightColor )
		{
			Radius = Radius,
		};
	}

	protected override void OnAwake()
	{
		Tags.Add( "light_point" );

		base.OnAwake();
	}

	protected override void DrawGizmos()
	{
		using var scope = Gizmo.Scope( gizmoPath ??= $"light-{GetHashCode()}" );

		if ( AreaShape != AreaShapes.Point )
		{
			Gizmo.Draw.Color = LightColor.WithAlpha( Gizmo.IsSelected ? 1.0f : 0.6f );
			DrawShape();
		}

		if ( Gizmo.IsSelected )
		{
			Gizmo.Draw.Color = LightColor.WithAlpha( 0.9f );
			Gizmo.Draw.LineSphere( new Sphere( Vector3.Zero, Radius ), 12 );
		}

		if ( Gizmo.IsHovered && Gizmo.Settings.Selection )
		{
			Gizmo.Draw.Color = LightColor.WithAlpha( 0.4f );
			Gizmo.Draw.LineSphere( new Sphere( Vector3.Zero, Radius ), 12 );
		}
	}

	//The shape in the light's own space, with a stroke forward from a panel's middle for the side it lights
	void DrawShape()
	{
		var halfWidth = AreaSize.x * 0.5f;
		var halfHeight = AreaSize.y * 0.5f;

		switch ( AreaShape )
		{
			case AreaShapes.Tube:
				var radius = MathF.Max( SourceRadius, 0.005f );
				Gizmo.Draw.Line( Vector3.Left * halfWidth + Vector3.Up * radius, Vector3.Right * halfWidth + Vector3.Up * radius );
				Gizmo.Draw.Line( Vector3.Left * halfWidth + Vector3.Down * radius, Vector3.Right * halfWidth + Vector3.Down * radius );
				return;

			case AreaShapes.Disc:
				const int segments = 24;

				for ( var i = 0; i < segments; i++ )
				{
					var a = i * MathF.Tau / segments;
					var b = ( i + 1 ) * MathF.Tau / segments;

					Gizmo.Draw.Line( ( Vector3.Right * MathF.Cos( a ) + Vector3.Up * MathF.Sin( a ) ) * halfWidth,
						( Vector3.Right * MathF.Cos( b ) + Vector3.Up * MathF.Sin( b ) ) * halfWidth );
				}

				break;

			default:
				var x = Vector3.Right * halfWidth;
				var y = Vector3.Up * halfHeight;
				Gizmo.Draw.Line( -x - y, x - y );
				Gizmo.Draw.Line( x - y, x + y );
				Gizmo.Draw.Line( x + y, -x + y );
				Gizmo.Draw.Line( -x + y, -x - y );
				break;
		}

		Gizmo.Draw.Line( Vector3.Zero, Vector3.Forward * MathF.Max( halfWidth, 0.1f ) );
	}

	//This component's gizmo scope name, made once
	string gizmoPath;
}
