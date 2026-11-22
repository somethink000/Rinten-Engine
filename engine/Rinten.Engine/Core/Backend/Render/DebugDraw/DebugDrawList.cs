using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten;

//Debug shapes drawn by Gizmo.Draw, one list per gizmo instance, drawn over the world by the scene drawer
internal sealed class DebugDrawList
{
	//How long something stays in the list. Untimed is until the owner's next frame - for a view, the next time its frame is
	//recorded; for a scene, the next update, or the next fixed update where it was asked for in one. Timed counts down in seconds.
	internal readonly record struct Life( bool Timed, bool Fixed, float Seconds )
	{
		public static readonly Life Frame = default;

		public static Life For( float seconds, bool inFixedUpdate )
			=> seconds > 0.0f ? new Life( true, inFixedUpdate, seconds ) : new Life( false, inFixedUpdate, 0.0f );
	}

	//What every kind of item has: how long it stays, and what it borrowed.
	private interface ILived
	{
		Life Life { get; set; }

		//The pooled array it holds, if it holds one.
		Vertex[] Borrowed { get; }
	}

	//Lines or triangles, in the space of Transform, coloured per vertex. The array
	//is borrowed from the shared vertex pool and is usually longer than Count.
	internal struct Shape : ILived
	{
		public Vertex[] Vertices;
		public int Count;
		public bool Lines;
		public Transform Transform;

		/// <summary>Line width in the window's pixels; one is the card's own line.</summary>
		public float Thickness;

		public bool IgnoreDepth;
		public bool CullBackfaces;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => Vertices;

		public readonly ReadOnlySpan<Vertex> Span => Vertices.AsSpan( 0, Count );

		public readonly bool DrawnLike( in Shape other )
			=> Lines == other.Lines && IgnoreDepth == other.IgnoreDepth && CullBackfaces == other.CullBackfaces &&
				Thickness == other.Thickness && Transform == other.Transform;
	}

	//A sphere drawn as rings, one of which always faces the eye - so it can only be built by whoever knows where the eye is.
	internal struct Ring : ILived
	{
		public Vector3 Center;
		public float Radius;
		public Transform Transform;
		public Color32 Color;
		public bool IgnoreDepth;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => null;
	}

	//Size is metres when WorldSpace and pixels otherwise. The position is already in the world. No picture is a plain square.
	/// <summary>A picture standing at a point in the world, turned to face the eye.</summary>
	internal struct Sprite : ILived
	{
		public Vector3 Position;
		public Vector2 Size;
		public bool WorldSpace;
		public float Angle;
		public Color Tint;
		public Texture Picture;
		public bool IgnoreDepth;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => null;
	}

	//Where a label is put.
	internal enum Placing
	{
		/// <summary>At <see cref="Label.Position"/>'s x and y, in the window's pixels.</summary>
		Screen,

		/// <summary>On the screen, wherever the world point projects to, plus the offset - and nowhere when it is behind the eye.</summary>
		Projected,

		/// <summary>In the world at the point, turned to face the eye, a pixel of the text being <see cref="Label.Scale"/> metres.</summary>
		Facing,

		/// <summary>In the world at the point, lying in <see cref="Label.Rotation"/>'s plane.</summary>
		Plane,
	}

	//Text, rasterised once by the drawer's label cache and drawn as a picture.
	internal struct Label : ILived
	{
		public TextRendering.Scope Scope;
		public Placing Placing;
		public Vector3 Position;
		public Vector2 Offset;
		public Rotation Rotation;
		public float Scale;

		/// <summary>Degrees about the anchor, on the screen.</summary>
		public float Angle;

		public TextFlag Flags;
		public bool IgnoreDepth;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => null;
	}

	//A whole mesh's edges kept on the card: the vertices and the line index buffer are made once, so a frame is
	//one indexed draw however many edges there are - see CoreGizmoWire.
	internal struct Wire : ILived
	{
		public ulong Vertices;
		public ulong Indices;
		public int IndexCount;
		public Transform Transform;
		public Color32 Color;
		public bool IgnoreDepth;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => null;
	}

	//Triangles in the window's pixels, drawn flat over everything: a box select, the camera speed bar, a picture put on the screen.
	internal struct Flat : ILived
	{
		public Vertex[] Vertices;
		public int Count;
		public Texture Picture;
		public Life Life;

		Life ILived.Life { readonly get => Life; set => Life = value; }
		readonly Vertex[] ILived.Borrowed => Vertices;

		public readonly ReadOnlySpan<Vertex> Span => Vertices.AsSpan( 0, Count );
	}

	internal readonly List<Shape> Shapes = new();
	internal readonly List<Wire> Wires = new();
	internal readonly List<Ring> Rings = new();
	internal readonly List<Sprite> Sprites = new();
	internal readonly List<Label> Labels = new();
	internal readonly List<Flat> Flats = new();

	//The models a gizmo drew this frame. Scene objects, because the callers pose them, tint them
	//and dress them after they are handed back; drawn with the world through the immediate models.
	internal readonly List<SceneModel> Models = new();

	//A frame that asks for more than this has lost track of what it is drawing; the rest of it is dropped rather than the list emptied.
	private const int Most = 65536;

	internal bool IsEmpty => Shapes.Count == 0 && Wires.Count == 0 && Rings.Count == 0 && Sprites.Count == 0 &&
		Labels.Count == 0 && Flats.Count == 0 && Models.Count == 0;

	//Everything gone, and every borrowed array back in the pool.
	internal void Clear()
	{
		foreach ( var shape in Shapes ) CoreImmediate.Give( shape.Vertices );
		foreach ( var flat in Flats ) CoreImmediate.Give( flat.Vertices );

		Shapes.Clear();
		Wires.Clear();
		Rings.Clear();
		Sprites.Clear();
		Labels.Clear();
		Flats.Clear();
		Models.Clear();
		open = false;
	}

	//-- shapes -----------------------------------------------------------------

	//Whether the last shape is the one Add writes to.
	private bool open;

	//Makes the last shape one drawn this way, carrying on from it where it already is
	//one - so a run of lines in one colour after another is one shape and one draw.
	internal void Begin( bool lines, in Transform transform, float thickness, bool ignoreDepth, bool cullBackfaces, Life life )
	{
		var wanted = new Shape
		{
			Lines = lines,
			Transform = transform,
			Thickness = thickness,
			IgnoreDepth = ignoreDepth,
			CullBackfaces = cullBackfaces,
			Life = life,
		};

		var shapes = CollectionsMarshal.AsSpan( Shapes );

		if ( shapes.Length > 0 )
		{
			ref var last = ref shapes[^1];

			if ( last.DrawnLike( wanted ) && last.Life == life )
			{
				open = true;
				return;
			}
		}

		if ( Shapes.Count >= Most )
		{
			open = false;
			return;
		}

		wanted.Vertices = CoreImmediate.Take( 64 );
		Shapes.Add( wanted );
		open = true;
	}

	//One more vertex on the shape Begin opened.
	internal void Add( in Vertex vertex )
	{
		if ( !open ) return;

		ref var last = ref CollectionsMarshal.AsSpan( Shapes )[^1];

		if ( last.Count == last.Vertices.Length )
		{
			var grown = CoreImmediate.Take( last.Count * 2 );
			last.Vertices.AsSpan( 0, last.Count ).CopyTo( grown );
			CoreImmediate.Give( last.Vertices );
			last.Vertices = grown;
		}

		last.Vertices[last.Count++] = vertex;
	}

	internal void Add( Vector3 position, Color32 color ) => Add( new Vertex( position, color ) );

	//Several at once, onto the shape Begin opened.
	internal void Add( ReadOnlySpan<Vertex> vertices )
	{
		foreach ( var vertex in vertices ) Add( vertex );
	}

	//-- the rest ---------------------------------------------------------------

	internal void Add( in Wire wire )
	{
		if ( wire.IndexCount >= 2 && wire.Vertices != 0 && wire.Indices != 0 && Wires.Count < Most )
			Wires.Add( wire );
	}

	internal void Add( in Ring ring )
	{
		if ( Rings.Count < Most ) Rings.Add( ring );
	}

	internal void Add( in Sprite sprite )
	{
		if ( Sprites.Count < Most ) Sprites.Add( sprite );
	}

	internal void Add( in Label label )
	{
		if ( string.IsNullOrEmpty( label.Scope.Text ) ) return;
		if ( Labels.Count < Most ) Labels.Add( label );
	}

	//Screen triangles, copied in. Fewer than three is not a triangle and is dropped.
	internal void AddFlat( ReadOnlySpan<Vertex> vertices, Texture picture, Life life )
	{
		if ( vertices.Length < 3 || Flats.Count >= Most ) return;

		var into = CoreImmediate.Take( vertices.Length );
		vertices.CopyTo( into );

		Flats.Add( new Flat { Vertices = into, Count = vertices.Length, Picture = picture, Life = life } );
	}

	internal void Add( SceneModel model )
	{
		if ( model is not null && Models.Count < Most ) Models.Add( model );
	}

	//-- lifetimes --------------------------------------------------------------

	//The start of an update: the untimed things asked for outside a fixed update go, and the timed ones are delta older.
	internal void Age( float delta )
	{
		//A model is asked for again every frame it is wanted, and the instance that made it lets it go when it is not.
		Models.Clear();

		Sweep( Shapes, delta, fixedOnly: false );
		Sweep( Wires, delta, fixedOnly: false );
		Sweep( Rings, delta, fixedOnly: false );
		Sweep( Sprites, delta, fixedOnly: false );
		Sweep( Labels, delta, fixedOnly: false );
		Sweep( Flats, delta, fixedOnly: false );
		open = false;
	}

	//The start of a fixed update: the untimed things asked for in the last one go.
	internal void AgeFixed()
	{
		Sweep( Shapes, 0.0f, fixedOnly: true );
		Sweep( Wires, 0.0f, fixedOnly: true );
		Sweep( Rings, 0.0f, fixedOnly: true );
		Sweep( Sprites, 0.0f, fixedOnly: true );
		Sweep( Labels, 0.0f, fixedOnly: true );
		Sweep( Flats, 0.0f, fixedOnly: true );
		open = false;
	}

	//One walk that writes the survivors down as it goes, keeping their order - the order is the order they are drawn in.
	private static void Sweep<T>( List<T> list, float delta, bool fixedOnly ) where T : struct, ILived
	{
		var items = CollectionsMarshal.AsSpan( list );
		var kept = 0;

		for ( var i = 0; i < items.Length; i++ )
		{
			ref var item = ref items[i];
			var life = item.Life;

			bool going;

			if ( fixedOnly )
			{
				going = !life.Timed && life.Fixed;
			}
			else if ( life.Timed )
			{
				item.Life = life with { Seconds = life.Seconds - delta };
				going = item.Life.Seconds < 0.0f;
			}
			else
			{
				going = !life.Fixed;
			}

			if ( going )
			{
				CoreImmediate.Give( item.Borrowed );
				continue;
			}

			items[kept++] = item;
		}

		if ( kept < items.Length ) list.RemoveRange( kept, items.Length - kept );
	}
}
