using NativeEngine;
using Rinten.Rendering;
using System.Runtime.InteropServices;

namespace Rinten;

public static partial class Graphics
{
	const int maxDynamicVertexBuffer = 512 * 1024;

	//This is our entry point into the engine for all draws. screen says the vertices are in the pixels of the view,
	//which is what a quad is unless the attributes carry a world panel - see Place for what becomes of it either way.
	internal static unsafe void DrawInternal<T>( T* vertices, int vertCount, ushort* indices, int indexCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles, bool screen = false ) where T : unmanaged
	{
		ArgumentNullException.ThrowIfNull( material, nameof( material ) );
		AssertRenderBlock();

		if ( vertices is null || vertCount <= 0 ) return;
		if ( !CoreImmediate.Capturing ) return;

		//A struct of somebody's own is drawn by the shader that knows its layout, and there is no
		//such shader here yet. The engine's own vertex is what everything in the game draws with.
		if ( typeof( T ) != typeof( Vertex ) )
		{
			Warn( $"drawing {typeof( T ).Name} vertices needs a shader that knows their layout" );
			return;
		}

		var lines = primitiveType is PrimitiveType.Lines or PrimitiveType.LineStrip
			or PrimitiveType.LinesWithAdjacency or PrimitiveType.LineStripWithAdjacency;

		//Expand strips into lists (alternating winding for triangles), since our pipelines take lists
		scoped Span<ushort> strip = default;

		if ( primitiveType is PrimitiveType.TriangleStrip or PrimitiveType.LineStrip )
		{
			var pairs = primitiveType is PrimitiveType.LineStrip;
			var per = pairs ? 2 : 3;

			var corners = indices is null || indexCount <= 0 ? vertCount : indexCount;
			if ( corners < per ) return;

			if ( indices is null && vertCount > ushort.MaxValue + 1 )
			{
				Warn( $"a strip of {vertCount} vertices is too long to draw without indices" );
				return;
			}

			var runs = corners - per + 1;
			var stripLength = runs * per;
			strip = stripLength <= 1536 ? stackalloc ushort[stripLength] : new ushort[stripLength];

			for ( var i = 0; i < runs; i++ )
			{
				if ( pairs )
				{
					strip[i * 2] = indices is null ? (ushort)i : indices[i];
					strip[i * 2 + 1] = indices is null ? (ushort)(i + 1) : indices[i + 1];
					continue;
				}

				int a = i, b = i + 1;
				if ( (i & 1) != 0 ) (a, b) = (b, a);

				strip[i * 3] = indices is null ? (ushort)a : indices[a];
				strip[i * 3 + 1] = indices is null ? (ushort)b : indices[b];
				strip[i * 3 + 2] = indices is null ? (ushort)(i + 2) : indices[i + 2];
			}

			primitiveType = pairs ? PrimitiveType.Lines : PrimitiveType.Triangles;
		}

		//Points would come out as something else entirely, which is worse than not coming out.
		if ( !lines && primitiveType is not (PrimitiveType.Triangles or PrimitiveType.TrianglesWithAdjacency) )
		{
			Warn( $"{primitiveType} is not a shape this draws" );
			return;
		}

		var taken = CoreImmediate.Take( vertCount );

		new ReadOnlySpan<Vertex>( (Vertex*)vertices, vertCount ).CopyTo( taken );

		scoped ReadOnlySpan<ushort> order = !strip.IsEmpty ? strip
			: indices is null || indexCount <= 0 ? default
			: new ReadOnlySpan<ushort>( indices, indexCount );

		var expanded = CoreImmediate.Expand( taken, vertCount, order, out var count );

		//Expanding borrows an array of its own and this one is finished with, so it goes back now rather than at
		//the end of the view - nothing else is holding it. Without indices the batch draws this one, and keeps it.
		if ( !ReferenceEquals( expanded, taken ) ) CoreImmediate.Give( taken );

		if ( count <= 0 ) return;

		//A draw that brought no attributes of its own is read off the view's, which is where DrawText puts its picture. One set
		//or the other, not both: a draw with its own set is not made to wear what the view's still holds from the last draw.
		var source = attributes ?? Attributes;

		screen = Place( expanded, count, source, screen );

		//The draw's own picture, where it has one: what DrawText rasterised, or what a point put in the slot.
		var picture = source.GetTexture( "Texture" );
		if ( picture is not null && !picture.IsValid ) picture = null;

		//Only file-backed materials have a colour map path; shader-only ones would draw missing texture
		var map = material.ResourcePath ?? "";

		CoreImmediate.Add( new CoreImmediate.Batch( expanded, count, lines, Transform.Zero,
			Color.White, map, IgnoreDepth || screen, picture, screen ) );
	}

	//Transform of the object whose render override is running, so its world panels draw at it
	internal static Transform ObjectTransform { get; set; } = Transform.Zero;

	//Moves world panel quads into world space on the CPU (y flipped); returns if still in screen pixels
	private static bool Place( Vertex[] vertices, int count, RenderAttributes attributes, bool screen )
	{
		if ( attributes.GetComboInt( "D_WORLDPANEL" ) != 1 ) return screen;

		var world = attributes.GetMatrix( "WorldMat", Matrix.Identity ) * (Matrix)CoreSceneRenderer.WorldOf( ObjectTransform );

		for ( var i = 0; i < count; i++ )
		{
			var at = vertices[i].Position;
			vertices[i].Position = world.Transform( new Vector3( at.x, -at.y, at.z ) );
		}

		return false;
	}

	//Whether what is drawn now is drawn over the world rather than in it. Their render context carried the depth state of whatever was being drawn.
	//Nothing sets one here, so an immediate draw is part of the world unless something says otherwise - which the overlay hooks do, around themselves.
	internal static bool IgnoreDepth { get; set; }

	//Says a thing once. An immediate draw that cannot be made happens every frame it
	//is asked for, and the log is not the place to find that out sixty times a second.
	private static void Warn( string message )
	{
		lock ( warned )
		{
			if ( !warned.Add( message ) ) return;
		}

		Log.Warning( message );
	}

	private static readonly HashSet<string> warned = new();

	/// <summary>Draw a bunch of vertices</summary>
	public static unsafe void Draw( Span<Vertex> vertices, int vertCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles )
	{
		fixed ( Vertex* ptr = vertices )
		{
			DrawInternal( ptr, vertCount, default, default, material, attributes, primitiveType );
		}
	}

	//Draw a bunch of vertices TODO: make this public TODO: Is this safe to be public TODO: Is VertexLayoutManager.Get Safe
	internal static unsafe void Draw<T>( Span<T> vertices, int vertCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles ) where T : unmanaged
	{
		fixed ( T* ptr = vertices )
		{
			DrawInternal( ptr, vertCount, default, default, material, attributes, primitiveType );
		}
	}

	/// <summary>Draw a bunch of vertices</summary>
	public static unsafe void Draw( List<Vertex> vertices, int vertCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles )
	{
		Draw( CollectionsMarshal.AsSpan( vertices ), vertCount, material, attributes, primitiveType );
	}

	//Draw a bunch of vertices TODO: make this public TODO: Is this safe to be public TODO: Is VertexLayoutManager.Get Safe
	internal static unsafe void Draw<T>( List<T> vertices, int vertCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles ) where T : unmanaged
	{
		Draw<T>( CollectionsMarshal.AsSpan( vertices ), vertCount, material, attributes, primitiveType );
	}

	//Triangles in their vertex colours and nothing else - what a ribbon or a piece of debug geometry
	//is. No material: the batch names none, and the drawer takes that as the plain coloured pipeline.
	internal static unsafe void DrawColoured( Span<Vertex> vertices, int vertCount, Span<ushort> indices, int indexCount )
	{
		AssertRenderBlock();

		if ( vertCount <= 0 ) return;
		if ( !CoreImmediate.Capturing ) return;

		//Indexed runs expand directly from caller data; only non-indexed runs are copied into the batch
		Vertex[] expanded;
		int count;

		if ( indexCount > 0 )
		{
			expanded = CoreImmediate.Expand( (ReadOnlySpan<Vertex>)vertices[..vertCount], vertCount,
				(ReadOnlySpan<ushort>)indices[..indexCount], out count );
		}
		else
		{
			expanded = CoreImmediate.Take( vertCount );
			vertices[..vertCount].CopyTo( expanded );
			count = vertCount;
		}

		if ( count <= 0 ) return;

		CoreImmediate.Add( new CoreImmediate.Batch( expanded, count, false, Transform.Zero, Color.White, "", IgnoreDepth ) );
	}

	/// <summary>Draw a bunch of vertices</summary>
	public static unsafe void Draw( Span<Vertex> vertices, int vertCount, Span<ushort> indices, int indexCount, Material material, RenderAttributes attributes = null, PrimitiveType primitiveType = PrimitiveType.Triangles )
	{
		fixed ( Vertex* vptr = vertices )
		fixed ( ushort* iptr = indices )
		{
			DrawInternal( vptr, vertCount, iptr, indexCount, material, attributes, primitiveType );
		}
	}

	static Vertex[] screenQuad;

	/// <summary>Draw a screen space quad using the passed material. Your material should be using a screenspace shader so it will actually render to the screen and not in worldspace at 0,0,0</summary>
	public static void Blit( Material material, RenderAttributes attributes = null )
	{
		if ( screenQuad == null )
		{
			screenQuad = new Vertex[4];
			screenQuad[0].Position = new Vector3( -1, -1, 0.5f );
			screenQuad[0].Normal = new Vector3( 0, 0, -1 );
			screenQuad[0].TexCoord0 = new Vector4( 0, 1, 0, 0 );

			screenQuad[1].Position = new Vector3( 1, -1, 0.5f );
			screenQuad[1].Normal = new Vector3( 0, 0, -1 );
			screenQuad[1].TexCoord0 = new Vector4( 1, 1, 1, 0 );

			screenQuad[2].Position = new Vector3( -1, 1, 0.5f );
			screenQuad[2].Normal = new Vector3( 0, 1, -1 );
			screenQuad[2].TexCoord0 = new Vector4( 0, 0, 0, 1 );

			screenQuad[3].Position = new Vector3( 1, 1, 0.5f );
			screenQuad[3].Normal = new Vector3( 0, 0, -1 );
			screenQuad[3].TexCoord0 = new Vector4( 1, 0, 1, 1 );
		}

		Draw( screenQuad.AsSpan(), 4, material, attributes, PrimitiveType.TriangleStrip );
	}

	/// <summary>Render a SceneObject</summary>
	public static void Render( SceneObject obj, Transform? transform = null, Color? color = null, Material material = null )
	{
		AssertRenderBlock();

		if ( !obj.IsValid() || !CoreImmediate.Capturing ) return;

		if ( obj is SceneCustomObject custom )
		{
			var was = ObjectTransform;

			ObjectTransform = transform ?? custom.Transform;

			try
			{
				custom.RenderInternal();
			}
			finally
			{
				ObjectTransform = was;
			}

			return;
		}

		if ( obj.Model is { } model )
		{
			CoreImmediate.Add( new CoreImmediate.ModelBatch( model, transform ?? obj.Transform, color ?? obj.ColorTint, Override: material ) );
			return;
		}

		Warn( $"Graphics.Render has nothing to draw of a {obj.GetType().Name} - it has no model and does not draw itself" );
	}

	/// <summary>Draw a quad in screenspace</summary>
	public static unsafe void DrawQuad( in Rect rect, in Material material, in Color color, RenderAttributes attributes = null )
	{
		attributes ??= Attributes;

		var color32 = color.ToColor32();

		var vertices = stackalloc Vertex[4]
		{
			new Vertex( new Vector2( rect.Left, rect.Top ), new Vector2( 0, 0 ), color32 ),
			new Vertex( new Vector2( rect.Right, rect.Top ), new Vector2( 1, 0 ), color32 ),
			new Vertex( new Vector2( rect.Left, rect.Bottom ), new Vector2( 0, 1 ), color32 ),
			new Vertex( new Vector2( rect.Right, rect.Bottom ), new Vector2( 1, 1 ), color32 )
		};

		DrawInternal( vertices, 4, default, default, material, attributes, PrimitiveType.TriangleStrip, screen: true );
	}

	//Draw a rotated quad in screenspace
	internal static unsafe void DrawQuad( in Rect rect, in float angle, in Material material, in Color color, RenderAttributes attributes = null )
	{
		attributes ??= Attributes;

		var color32 = color.ToColor32();

		var rads = angle.DegreeToRadian();
		var cos = MathF.Cos( rads );
		var sin = MathF.Sin( rads );

		var origin = rect.Center;
		var addX = new Vector2( cos, sin ) * rect.Width * 0.5f;
		var addY = new Vector2( -sin, cos ) * rect.Height * 0.5f;

		var vertices = stackalloc Vertex[4]
		{
			new Vertex( origin - addX - addY, new Vector2( 0, 0 ), color32 ),
			new Vertex( origin + addX - addY, new Vector2( 1, 0 ), color32 ),
			new Vertex( origin - addX + addY, new Vector2( 0, 1 ), color32 ),
			new Vertex( origin + addX + addY, new Vector2( 1, 1 ), color32 )
		};

		DrawInternal( vertices, 4, default, default, material, attributes, PrimitiveType.TriangleStrip, screen: true );
	}

	/// <summary>Draws a text quad in screenspace using the Material.UI.Text material.</summary>
	public static Rect DrawText( in Rect position, string text, Color color, string fontFamily = "Roboto", float fontSize = 20.0f, float fontWeight = 450, TextFlag flags = TextFlag.Center )
	{
		return DrawText( position, new TextRendering.Scope( text, color, fontSize, fontFamily, (int)fontWeight ), flags );
	}

	/// <summary>Draws a text quad in screenspace using the Material.UI.Text material.</summary>
	public static Rect DrawText( in Rect position, in TextRendering.Scope scope, TextFlag flags = TextFlag.Center )
	{
		var texture = TextRendering.GetOrCreateTexture( scope, flag: flags );

		Attributes.Set( "Texture", texture );
		Attributes.Set( "SamplerIndex", SamplerState.GetBindlessIndex( new SamplerState() { Filter = scope.FilterMode } ) );

		var rect = position.Align( texture.Size, flags );
		DrawQuad( rect.Floor(), Material.UI.Text, Color.White );

		return rect;
	}

	//Draws a rotated text quad in screenspace using the Material.UI.Text material.
	internal static void DrawText( in Rect position, float angle, in TextRendering.Scope scope, TextFlag flags = TextFlag.Center )
	{
		var texture = TextRendering.GetOrCreateTexture( scope, flag: flags );

		Attributes.Set( "Texture", texture );
		Attributes.Set( "SamplerIndex", SamplerState.GetBindlessIndex( new SamplerState() { Filter = scope.FilterMode } ) );

		var rect = position.Align( texture.Size, flags );
		DrawQuad( rect, angle, Material.UI.Text, Color.White );
	}

	/// <summary>Draws a text quad in screenspace using the Material.UI.Text material.</summary>
	public static Rect DrawText( in Vector2 position, string text, Color color, string fontFamily = "Roboto", float fontSize = 20.0f, float fontWeight = 450 )
	{
		if ( string.IsNullOrEmpty( text ) ) text = null;

		return DrawText( new Rect( position, 4096 ), text, color, fontFamily, fontSize, fontWeight, TextFlag.LeftTop );
	}

	/// <summary>Measure how big some text will be, without having to render it</summary>
	public static Rect MeasureText( in Rect position, string text, string fontFamily = "Roboto", float fontSize = 20.0f, float fontWeight = 450, TextFlag flags = TextFlag.Center )
	{
		return MeasureText( position, new TextRendering.Scope( text, Color.White, fontSize, fontFamily, (int)fontWeight ), flags );
	}

	/// <summary>Measure how big some text will be, without having to render it</summary>
	public static Rect MeasureText( in Rect position, in TextRendering.Scope scope, TextFlag flags = TextFlag.Center )
	{
		//Measure via the shaped text block only, without rasterising a texture: the cached one where this text was drawn, else a
		//block kept for measuring - text measured every frame as it changes made a cache entry of each string just to measure it
		var clip = position.Size == default ? new Vector2( 8096 ) : position.Size;

		if ( Application.IsHeadless ) return new Rect( position.Position, 0 );

		if ( !TextRendering.TryGetTextBlock( scope, flags, clip, out var block ) ) block = TextRendering.Scratch( scope, flags );

		if ( block.IsEmpty )
		{
			var picture = TextRendering.GetOrCreateTexture( scope, position.Size, flags );
			return new Rect( position.Position, picture.Size );
		}

		var size = block.Texture is { IsValid: true } made ? made.Size : block.PictureSize( new Rect( 0, clip ) );
		return new Rect( position.Position, size );
	}

	/// <summary>Calls DrawText with "Material Icons" font. You can get a list of icons here https://fonts.google.com/icons?selected=Material+Icons</summary>
	public static Rect DrawIcon( Rect rect, string iconName, Color color, float fontSize = 20.0f, TextFlag alignment = TextFlag.Center )
	{
		if ( string.IsNullOrEmpty( iconName ) )
			iconName = "people";

		return DrawText( rect, iconName?.ToLower(), color, "Material Icons", fontSize, flags: alignment );
	}

	/// <summary>Draw a rounded rectangle, with optional border, in Material.UI.Box</summary>
	public static unsafe void DrawRoundedRectangle( in Rect rect, in Color color, in Vector4 cornerRadius = default, in Vector4 borderWidth = default, in Color borderColor = default )
	{
		Attributes.Set( "BoxPosition", new Vector2( rect.Left, rect.Top ) );
		Attributes.Set( "BoxSize", new Vector2( rect.Width, rect.Height ) );
		Attributes.Set( "BorderRadius", cornerRadius );
		Attributes.SetCombo( "D_BACKGROUND_IMAGE", 0 );

		if ( !borderWidth.IsNearZeroLength )
		{
			Attributes.Set( "HasBorder", 1 );
			Attributes.SetCombo( "D_BORDER_IMAGE", 0 );

			Attributes.Set( "BorderSize", borderWidth );
			Attributes.Set( "BorderColorL", borderColor );
			Attributes.Set( "BorderColorT", borderColor );
			Attributes.Set( "BorderColorR", borderColor );
			Attributes.Set( "BorderColorB", borderColor );
		}
		else
		{
			Attributes.Set( "HasBorder", 0 );
			Attributes.SetCombo( "D_BORDER_IMAGE", 0 );
		}

		DrawQuad( rect, Material.UI.Box, color );
	}

}
