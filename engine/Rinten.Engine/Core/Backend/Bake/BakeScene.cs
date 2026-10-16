using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//A picture a material reads as linear light with its alpha, small enough to keep for a whole bake
internal sealed class BakePicture
{
	public int Width, Height;
	public Vec4[] Texels;

	//The biggest a picture is kept: bounced light is soft, and a cut-out leaf needs no more.
	private const int Largest = 256;

	/// <summary>Reads a texture at the first level no wider than the bake wants, colour linearised.</summary>
	public static BakePicture Of( Texture texture, bool colour )
	{
		if ( texture is null || !texture.IsValid() || texture.Width <= 0 || texture.Height <= 0 ) return null;

		var mip = 0;
		while ( mip < texture.Mips - 1 && Math.Max( texture.Width >> mip, texture.Height >> mip ) > Largest ) mip++;

		Color32[] pixels;
		var width = Math.Max( texture.Width >> mip, 1 );
		var height = Math.Max( texture.Height >> mip, 1 );

		try
		{
			pixels = texture.GetPixels( mip );
		}
		catch ( Exception )
		{
			//A streamed, compressed map has nothing to read back: the picture it was made from, off the disk
			pixels = FromSource( texture.core?.Name, out width, out height );

			if ( pixels is null )
			{
				Log.Warning( $"the bake could not read {texture.core?.Name ?? texture.ResourcePath} - it takes it as white" );
				return null;
			}
		}

		if ( pixels is null || pixels.Length < width * height ) return null;

		var made = new BakePicture { Width = width, Height = height, Texels = new Vec4[width * height] };
		var linear = BakeMath.Linear;

		for ( var i = 0; i < width * height; ++i )
		{
			var p = pixels[i];

			made.Texels[i] = colour
				? new Vec4( linear[p.r], linear[p.g], linear[p.b], p.a / 255.0f )
				: new Vec4( p.r / 255.0f, p.g / 255.0f, p.b / 255.0f, p.a / 255.0f );
		}

		return made;
	}

	//A picture file decoded on the processor, averaged in linear light to no wider than Largest; null if unreadable
	private static Color32[] FromSource( string path, out int width, out int height )
	{
		width = height = 0;
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		try
		{
			using var bitmap = CoreImportedTexture.IsTheirs( path )
				? CoreImportedTexture.Load( path )
				: FileSystem.Mounted.FileExists( path ) ? Bitmap.CreateFromBytes( FileSystem.Mounted.ReadAllBytes( path ).ToArray() ) : null;

			if ( bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 ) return null;

			var all = bitmap.GetPixels32();
			var step = 1;
			while ( Math.Max( bitmap.Width / step, bitmap.Height / step ) > Largest ) step *= 2;

			width = Math.Max( bitmap.Width / step, 1 );
			height = Math.Max( bitmap.Height / step, 1 );

			var kept = new Color32[width * height];
			var linear = BakeMath.Linear;

			for ( var y = 0; y < height; ++y )
			{
				for ( var x = 0; x < width; ++x )
				{
					var sum = Vec4.Zero;

					for ( var dy = 0; dy < step; ++dy )
						for ( var dx = 0; dx < step; ++dx )
						{
							var p = all[( Math.Min( ( y * step ) + dy, bitmap.Height - 1 ) * bitmap.Width ) + Math.Min( ( x * step ) + dx, bitmap.Width - 1 )];
							sum += new Vec4( linear[p.r], linear[p.g], linear[p.b], p.a / 255.0f );
						}

					sum /= step * step;
					kept[( y * width ) + x] = new Color32( Encode( sum.X ), Encode( sum.Y ), Encode( sum.Z ), (byte)MathF.Round( sum.W * 255.0f ) );
				}
			}

			return kept;
		}
		catch ( Exception )
		{
			return null;
		}
	}

	//Linear light back to an sRGB byte, for an average taken in linear
	private static byte Encode( float linear )
	{
		var c = Math.Clamp( linear, 0.0f, 1.0f );
		var s = c <= 0.0031308f ? c * 12.92f : ( 1.055f * MathF.Pow( c, 1.0f / 2.4f ) ) - 0.055f;
		return (byte)MathF.Round( s * 255.0f );
	}

	private Vec4? mean;

	/// <summary>Every texel's average, worked out once.</summary>
	public Vec4 Mean()
	{
		if ( mean is { } held ) return held;

		var sum = Vec4.Zero;
		foreach ( var texel in Texels ) sum += texel;

		mean = Texels.Length > 0 ? sum / Texels.Length : Vec4.One;
		return mean.Value;
	}

	/// <summary>Bilinear, repeating - the way the material's own sampler reads it.</summary>
	public Vec4 Sample( float u, float v )
	{
		var x = ( u * Width ) - 0.5f;
		var y = ( v * Height ) - 0.5f;

		var fx = MathF.Floor( x );
		var fy = MathF.Floor( y );

		var tx = x - fx;
		var ty = y - fy;

		var x0 = Wrap( (int)fx, Width );
		var y0 = Wrap( (int)fy, Height );
		var x1 = Wrap( x0 + 1, Width );
		var y1 = Wrap( y0 + 1, Height );

		var top = Vec4.Lerp( Texels[( y0 * Width ) + x0], Texels[( y0 * Width ) + x1], tx );
		var bottom = Vec4.Lerp( Texels[( y1 * Width ) + x0], Texels[( y1 * Width ) + x1], tx );

		return Vec4.Lerp( top, bottom, ty );
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private static int Wrap( int i, int n )
	{
		i %= n;
		return i < 0 ? i + n : i;
	}
}

//A flat reflector lamps are mirrored in: its plane, what it mirrors square on, and whether that rises towards a
//grazing angle as water's and glass's does or stays its colour as metal's does - see BakeScene.Reflected
internal readonly record struct BakeMirror( Vec3 Normal, float Offset, Vec3 Reflectance, bool Dielectric );

//What a surface is to the bake: what it reflects, what it gives off, and whether light gets through it.
internal sealed class BakeMaterial
{
	public Vec3 Tint = Vec3.One;
	public float Alpha = 1.0f;
	public BakePicture Colour;

	/// <summary>Metal by the number, times its map where it has one; nought where the material is no metal.</summary>
	public float Metalness;
	public BakePicture MetalnessMap;

	/// <summary>Roughness by the number, times its map where it has one; one where roughness is off.</summary>
	public float Roughness = 1.0f;
	public BakePicture RoughnessMap;

	/// <summary>Metal at a point of the surface, as complex.shader reads it.</summary>
	public float MetalnessAt( System.Numerics.Vector2 uv ) => Math.Clamp( ( MetalnessMap?.Sample( uv.X, uv.Y ).X ?? 1.0f ) * Metalness, 0.0f, 1.0f );

	/// <summary>Roughness at a point of the surface, as complex.shader reads it.</summary>
	public float RoughnessAt( System.Numerics.Vector2 uv ) => Math.Clamp( ( RoughnessMap?.Sample( uv.X, uv.Y ).X ?? 1.0f ) * Roughness, 0.04f, 1.0f );

	public Vec3 Emission;
	public BakePicture EmissionMap;
	public bool EmissionUsesAlbedo;

	public bool Masked;
	public float Cutoff = 0.5f;

	/// <summary>See-through: glass lets the bake's light pass rather than stopping it.</summary>
	public bool Translucent;

	/// <summary>What a see-through surface mirrors square on, which does not get through it.</summary>
	public float Reflectance;

	/// <summary>What of the light on its back a leaf or thin cloth passes to its front, each colour.</summary>
	public Vec3 Transmits;

	public bool TwoSided;

	public bool Glows => Emission.X > 0.0f || Emission.Y > 0.0f || Emission.Z > 0.0f;

	/// <summary>What its pictures make of the emission on average - what a glowing triangle is picked by.</summary>
	public Vec3 MeanGlow()
	{
		var map = EmissionMap?.Mean() ?? Vec4.One;
		var from = EmissionUsesAlbedo ? Colour?.Mean() ?? Vec4.One : Vec4.One;

		return new Vec3( map.X * from.X, map.Y * from.Y, map.Z * from.Z );
	}

	/// <summary>What the bake needs of a material, read as the surface shader reads it - see complex.shader.</summary>
	public static BakeMaterial Of( Material material, Dictionary<Texture, BakePicture> pictures )
	{
		var made = new BakeMaterial();

		if ( material is null || material.core is null ) return made;

		CoreResources.Live( material );

		var flags = CoreResources.LiveFlags( material );
		made.Masked = flags.Masked;
		made.Translucent = flags.Translucent;
		made.TwoSided = flags.TwoSided;

		var tint = CoreResources.LiveTint( material );
		made.Tint = new Vec3( tint.r, tint.g, tint.b );
		made.Alpha = tint.a;

		made.Colour = Picture( material.GetTexture( "Color" ), true, pictures );
		made.Cutoff = material.GetFloat( "AlphaCutoff", 0.5f );

		//A glass pane holds back its Opacity and mirrors its Reflectance; the rest hold back alpha - see glass.shader
		if ( material.ShaderName?.EndsWith( "glass.shader", StringComparison.OrdinalIgnoreCase ) == true )
		{
			made.Alpha = Math.Clamp( CoreResources.Number( material.core, "g_flOpacity", 0.1f ), 0.0f, 1.0f );
			made.Reflectance = Math.Clamp( CoreResources.Number( material.core, "g_flReflectance", 0.04f ), 0.0f, 1.0f );
		}

		//Water lets the light through to its bed but what its surface mirrors, which is water's own 2% - see water.shader
		if ( material.ShaderName?.EndsWith( "water.shader", StringComparison.OrdinalIgnoreCase ) == true )
		{
			made.Alpha = 0.0f;
			made.Reflectance = 0.02f;
		}

		//Light through a leaf or thin cloth, as F_TRANSMISSION shades it: amount, colour and the thickness map's mean
		if ( CoreResources.Feature( material, "F_TRANSMISSION" ) != 0 )
		{
			var colour = CoreResources.Vector( material.core, "g_vTransmissionColour", new Vector4( 1.0f, 1.0f, 1.0f, 0.0f ) );
			var thick = Picture( material.GetTexture( "g_tThickness" ), false, pictures );

			made.Transmits = Vec3.Max( new Vec3( colour.x, colour.y, colour.z ), Vec3.Zero )
				* ( MathF.Max( CoreResources.Number( material.core, "g_flTransmission", 0.5f ), 0.0f ) * ( thick?.Mean().X ?? 1.0f ) );
		}

		//As complex.shader: the number times the picture, nought metal and fully rough where the feature is off
		if ( CoreResources.Feature( material, "F_METALNESS" ) != 0 )
		{
			made.Metalness = Math.Clamp( material.GetFloat( "Metalness", 0.0f ), 0.0f, 1.0f );
			made.MetalnessMap = Picture( material.GetTexture( "Metalness" ), false, pictures );
		}

		if ( CoreResources.Feature( material, "F_ROUGHNESS" ) != 0 )
		{
			made.Roughness = material.GetFloat( "Roughness", 0.5f );
			made.RoughnessMap = Picture( material.GetTexture( "Roughness" ), false, pictures );
		}

		var strength = material.GetFloat( "EmissionStrength", 0.0f );

		if ( strength > 0.0f && CoreResources.Feature( material, "F_EMISSION" ) != 0 )
		{
			var glow = material.core.Values.TryGetValue( "EmissiveColor", out var said )
				? new Vec3( said.x, said.y, said.z )
				: Vec3.One;

			made.Emission = glow * strength;
			made.EmissionMap = Picture( material.GetTexture( "Emission" ), true, pictures );
			made.EmissionUsesAlbedo = material.GetFloat( "EmissiveUseAlbedo", 0.0f ) > 0.5f;
		}

		return made;
	}

	private static BakePicture Picture( Texture texture, bool colour, Dictionary<Texture, BakePicture> pictures )
	{
		if ( texture is null ) return null;
		if ( pictures.TryGetValue( texture, out var held ) ) return held;

		held = BakePicture.Of( texture, colour );
		pictures[texture] = held;
		return held;
	}
}

//A light as the bake traces it - the frame's numbers, see CoreSceneRenderer.BuildFrame.
internal sealed class BakeLight
{
	public int Kind; //0 sun, 1 point, 2 spot
	public Vec3 Position;
	public float Range;
	public Vec3 Colour;
	public Vec3 Forward;
	public float CosInner, CosOuter;
	public float Falloff = 1.0f;
	public float Radius;
	public bool Shadows = true;

	/// <summary>The light component this is, so the frame can find what was baked of it.</summary>
	public Guid Id;

	/// <summary>The whole of it is in the bake, and none of it in the frame - see Light.NotBaked.</summary>
	public bool Whole;

	/// <summary>A point light's shape (PointLight.AreaShapes), half extents and width axis - see AreaLamp.</summary>
	public int Shape;
	public float HalfWidth, HalfHeight;
	public Vec3 Right;

	/// <summary>How big the shape is: a tube's as seen side on.</summary>
	public float Area => Shape == 2 ? BakeMath.Pi * HalfWidth * HalfWidth : 4.0f * HalfWidth * HalfHeight;
}

//Where a ray landed, as the lighting wants it.
internal struct BakeSurface
{
	public Vec3 Position;
	public Vec3 Normal;
	public Vec3 Geometric;
	public Vec3 Albedo;
	public Vec3 Emission;
	public bool Back;

	/// <summary>What it mirrors square on: four per cent where it is not metal, its colour where it is.</summary>
	public Vec3 Specular;
	public float Roughness;
}

//Static triangles, materials, lights and sky for the bake; built on the main thread, then read-only
internal sealed class BakeScene
{
	public readonly BakeBvh Bvh = new();

	//A vertex of the world, in the world.
	public Vec3[] Positions = Array.Empty<Vec3>();
	public Vec3[] Normals = Array.Empty<Vec3>();
	public System.Numerics.Vector2[] Uv0 = Array.Empty<System.Numerics.Vector2>();
	public System.Numerics.Vector2[] Uv1 = Array.Empty<System.Numerics.Vector2>();
	public Vec3[] Colours = Array.Empty<Vec3>();

	//A triangle: its three vertices, which way it faces, what it is made of.
	public int[] Corners = Array.Empty<int>();
	public Vec3[] Facing = Array.Empty<Vec3>();
	public int[] MaterialOf = Array.Empty<int>();

	/// <summary>The renderer's own tint, a triangle - it multiplies the material's.</summary>
	public Vec3[] TintOf = Array.Empty<Vec3>();

	public readonly List<BakeMaterial> Materials = new();
	public readonly List<BakeLight> Lights = new();

	//Glowing triangles a shadow ray is sent to, picked by power - see TowardsGlow; and their power summed so far, each
	public int[] Emitters = Array.Empty<int>();
	public float[] EmitterSum = Array.Empty<float>();

	/// <summary>Per world triangle: TowardsGlow's chance per square metre of picking it, nought for never.</summary>
	public float[] GlowDensity = Array.Empty<float>();

	/// <summary>Per world triangle: its BakedGlow group, one to four, or nought for the main bake.</summary>
	public int[] GroupOf = Array.Empty<int>();

	/// <summary>What is being baked: nought the main bake, else one glow group's light alone - see UsePass.</summary>
	public int Pass { get; private set; }

	/// <summary>What stands still and has a sheet of its own to be baked onto.</summary>
	public readonly List<BakeReceiver> Receivers = new();

	public BBox Bounds;

	/// <summary>The sky as it was when gathered.</summary>
	public readonly BakeSky Sky = new();

	public float SkyStrength = 1.0f;
	public float AlbedoBoost = 1.0f;
	public int Bounces = 4;

	/// <summary>Texels along the sheet's side once BakeSheet.Pack has laid it out; nought before.</summary>
	public int AtlasSide;

	/// <summary>The gathering pass being traced on the processor; null traces whole paths.</summary>
	public BakeGather Gathering
	{
		get;
		set
		{
			field = value;
			if ( value is not null ) atlasSpots ??= AtlasSpots();
		}
	}

	//Each vertex's spot on the sheet in texels, negative off it - as BakeGpu packs it for the card
	private System.Numerics.Vector2[] atlasSpots;

	/// <summary>How wide the sun is, as the cosine of its angular radius.</summary>
	public float SunCosine = MathF.Cos( 0.27f * MathF.PI / 180.0f );

	/// <summary>How far off a surface a ray starts, in metres - see Offset.</summary>
	public const float Lift = 0.002f;

	//Built as it goes, then made arrays.
	private readonly List<Vec3> positions = new();
	private readonly List<Vec3> normals = new();
	private readonly List<System.Numerics.Vector2> uv0 = new();
	private readonly List<System.Numerics.Vector2> uv1 = new();
	private readonly List<Vec3> colours = new();
	private readonly List<int> corners = new();
	private readonly List<int> materialOf = new();
	private readonly List<Vec3> tintOf = new();
	private readonly List<int> groupOf = new();
	private readonly Dictionary<Material, int> materialIndex = new();
	private readonly Dictionary<Texture, BakePicture> pictures = new();

	/// <summary>Takes everything static out of a scene. Main thread: reads components and materials.</summary>
	//Whether it is written into the scene: one made at run time has a new id each session, which no bake can
	//name again, so it stays live - as only a level's own lights bake in UE and Source 2
	internal static bool Saved( Component component )
	{
		if ( component.Flags.Contains( ComponentFlags.NotSaved ) ) return false;

		for ( var go = component.GameObject; go is not null && go is not Scene; go = go.Parent )
			if ( go.Flags.Contains( GameObjectFlags.NotSaved ) ) return false;

		return true;
	}

	public void Gather( Scene scene )
	{
		ThreadSafe.AssertIsMainThread();

		Bounds = default;
		var any = false;

		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
		{
			if ( !renderer.IsValid() || !renderer.Enabled || !renderer.Active ) continue;
			if ( renderer is SkinnedModelRenderer ) continue;
			if ( !renderer.GameObject.IsStatic || !Saved( renderer ) ) continue;

			var model = renderer.Model;
			if ( model is null || !model.IsValid() || model.IsError ) continue;

			var casts = renderer.RenderType != ModelRenderer.ShadowRenderType.Off;
			var seen = renderer.RenderType != ModelRenderer.ShadowRenderType.ShadowsOnly;

			var first = corners.Count / 3;
			var hasSheet = AddModel( model, renderer.WorldTransform, renderer.Tint, renderer.BodyGroups,
				renderer.MaterialOverride, renderer.MaterialGroup );
			var count = ( corners.Count / 3 ) - first;

			if ( count == 0 ) continue;

			var glow = renderer.GetComponent<BakedGlow>();
			Group( glow is { Enabled: true } ? glow.Layer + 1 : 0 );

			Grow( first, count, ref any );

			//A thing told to cast nothing is still something light arrives on; it just stops nothing.
			//A thing only there for its shadow is the reverse, and has nothing to be baked onto.
			if ( !casts ) Transparent( first, count );

			if ( seen && hasSheet ) Receivers.Add( new BakeReceiver( renderer, model, first, count ) );
		}

		//The level's own meshes, built in the editor: each face an island of their sheet - see PolygonMesh.LayLightmap
		foreach ( var built in scene.GetAllComponents<MeshComponent>() )
		{
			if ( !built.IsValid() || !built.Enabled || !built.Active || !built.GameObject.IsStatic || !Saved( built ) ) continue;
			if ( built.Model is not { } model || !model.IsValid() ) continue;

			var first = corners.Count / 3;
			var hasSheet = AddModel( model, built.WorldTransform, Color.White, ulong.MaxValue, null, null );
			var count = ( corners.Count / 3 ) - first;

			if ( count == 0 ) continue;

			var glow = built.GetComponent<BakedGlow>();
			Group( glow is { Enabled: true } ? glow.Layer + 1 : 0 );

			Grow( first, count, ref any );

			if ( built.RenderType == ModelRenderer.ShadowRenderType.Off ) Transparent( first, count );

			if ( built.RenderType != ModelRenderer.ShadowRenderType.ShadowsOnly && hasSheet )
				Receivers.Add( new BakeReceiver( built, model, first, count ) );
		}

		//The ground: the terrain's chunks are models of their own with no sheet among their coordinates - the second
		//set carries paint - so they stop light and throw it back, and read the volume like anything without one.
		foreach ( var terrain in scene.GetAllComponents<Terrain>() )
		{
			if ( !terrain.IsValid() || !terrain.Enabled || !terrain.Active ) continue;

			foreach ( var part in scene.SceneWorld.SceneObjects )
			{
				if ( part is null || !part.IsValid() || part.Component != terrain ) continue;
				if ( part.Model is not { } model || !model.IsValid() ) continue;

				var first = corners.Count / 3;
				AddModel( model, part.Transform, Color.White, ulong.MaxValue, part.MaterialOverride, null );
				Grow( first, ( corners.Count / 3 ) - first, ref any );
			}
		}

		Positions = positions.ToArray();
		Normals = normals.ToArray();
		Uv0 = uv0.ToArray();
		Uv1 = uv1.ToArray();
		Colours = colours.ToArray();
		Corners = corners.ToArray();
		MaterialOf = materialOf.ToArray();
		TintOf = tintOf.ToArray();

		Group( 0 );
		GroupOf = groupOf.ToArray();

		GatherLights( scene );
		GatherSky( scene );
	}

	//Whether a triangle lets every ray through - see Transparent.
	private readonly HashSet<int> clear = new();

	private void Transparent( int first, int count )
	{
		for ( var t = first; t < first + count; ++t ) clear.Add( t );
	}

	//The triangles added since the last call, into a glow group
	private void Group( int group )
	{
		while ( groupOf.Count < corners.Count / 3 ) groupOf.Add( group );
	}

	/// <summary>The glow groups with something glowing in them, in order.</summary>
	public List<int> GlowGroups()
	{
		var found = new List<int>();

		for ( var t = 0; t < GroupOf.Length; ++t )
			if ( GroupOf[t] > 0 && !found.Contains( GroupOf[t] ) && Materials[MaterialOf[t]].Glows ) found.Add( GroupOf[t] );

		found.Sort();
		return found;
	}

	/// <summary>Bakes nought, the main bake, or one glow group's light alone: no lamps, no sky, only its glow.</summary>
	public void UsePass( int pass )
	{
		Pass = pass;
		GatherEmitters();
	}

	private void Grow( int first, int count, ref bool any )
	{
		for ( var t = first; t < first + count; ++t )
		{
			for ( var k = 0; k < 3; ++k )
			{
				var p = positions[corners[( t * 3 ) + k]];
				var point = new Vector3( p.X, p.Y, p.Z );

				Bounds = any ? Bounds.AddPoint( point ) : new BBox( point, point );
				any = true;
			}
		}
	}

	//One model's triangles into the world; true where it has a lightmap sheet (a second UV set)
	private bool AddModel( Model model, Transform transform, Color tint, ulong bodyGroups, Material over, string materialGroup )
	{
		var vertices = model.GetVertices();
		var indices = model.GetIndices();

		if ( vertices is null || indices is null || vertices.Length == 0 ) return false;

		var mask = bodyGroups == ulong.MaxValue ? model.core.DefaultMask : bodyGroups;

		List<Material> group = null;

		if ( over is null && !string.IsNullOrEmpty( materialGroup ) )
			group = new List<Material>( model.GetMaterials( materialGroup ) );

		var materials = over is null && group is null ? model.Materials : default;

		var rotation = transform.Rotation;
		var scale = transform.Scale;
		var inverse = new Vector3(
			MathF.Abs( scale.x ) > 1e-8f ? 1.0f / scale.x : 0.0f,
			MathF.Abs( scale.y ) > 1e-8f ? 1.0f / scale.y : 0.0f,
			MathF.Abs( scale.z ) > 1e-8f ? 1.0f / scale.z : 0.0f );

		//A mirrored transform turns every triangle inside out.
		var mirrored = scale.x * scale.y * scale.z < 0.0f;

		var linearTint = new Vec3( tint.r, tint.g, tint.b );
		var sheet = false;

		for ( var mesh = 0; mesh < model.MeshCount; ++mesh )
		{
			var owner = model.core.MeshGroup( mesh );
			if ( owner != 0 && ( owner & mask ) == 0 ) continue;

			model.MeshDrawRange( mesh, out var firstIndex, out var indexCount, out var baseVertex );
			if ( indexCount < 3 || firstIndex < 0 || firstIndex + indexCount > indices.Length ) continue;

			var material = over ?? ( group is not null
				? ( mesh < group.Count ? group[mesh] : null )
				: ( mesh < materials.Length ? materials[mesh] : null ) );

			var materialAt = MaterialIndex( material );

			//The mesh's vertices once each, however many triangles share them.
			var local = new Dictionary<int, int>();
			Span<int> at = stackalloc int[3];

			for ( var i = 0; i + 2 < indexCount; i += 3 )
			{
				var good = true;

				for ( var k = 0; k < 3; ++k )
				{
					var v = baseVertex + (int)indices[firstIndex + i + k];
					if ( (uint)v >= (uint)vertices.Length ) { good = false; break; }

					if ( !local.TryGetValue( v, out var made ) )
					{
						var vertex = vertices[v];

						made = positions.Count;
						local[v] = made;

						var world = transform.PointToWorld( vertex.Position );
						var normal = ( rotation * ( vertex.Normal * inverse ) ).Normal;

						positions.Add( new Vec3( world.x, world.y, world.z ) );
						normals.Add( new Vec3( normal.x, normal.y, normal.z ) );
						uv0.Add( new System.Numerics.Vector2( vertex.TexCoord0.x, vertex.TexCoord0.y ) );
						uv1.Add( new System.Numerics.Vector2( vertex.TexCoord1.x, vertex.TexCoord1.y ) );
						colours.Add( new Vec3( vertex.Color.r, vertex.Color.g, vertex.Color.b ) / 255.0f );

						if ( vertex.TexCoord1.x != 0.0f || vertex.TexCoord1.y != 0.0f ) sheet = true;
					}

					at[k] = made;
				}

				if ( !good ) continue;

				if ( mirrored )
				{
					corners.Add( at[0] );
					corners.Add( at[2] );
					corners.Add( at[1] );
				}
				else
				{
					corners.Add( at[0] );
					corners.Add( at[1] );
					corners.Add( at[2] );
				}

				materialOf.Add( materialAt );
				tintOf.Add( linearTint );
			}
		}

		return sheet;
	}

	//Where a mesh with no material stands: plain white, which is what the frame draws it as.
	private int plain = -1;

	private int MaterialIndex( Material material )
	{
		if ( material is null )
		{
			if ( plain < 0 )
			{
				plain = Materials.Count;
				Materials.Add( new BakeMaterial() );
			}

			return plain;
		}

		if ( materialIndex.TryGetValue( material, out var found ) ) return found;

		found = Materials.Count;
		Materials.Add( BakeMaterial.Of( material, pictures ) );
		materialIndex[material] = found;

		return found;
	}

	private void GatherLights( Scene scene )
	{
		foreach ( var light in scene.GetAllComponents<Light>() )
		{
			if ( !light.IsValid() || !light.Enabled || !light.Active || light.NotBaked || !Saved( light ) ) continue;
			if ( !( ( light.Contribution & Light.LightContribution.Diffuse ) == Light.LightContribution.Diffuse ) ) continue;

			var colour = light.EmittedColor;
			var forward = light.WorldRotation.Forward.Normal;
			var position = light.WorldPosition;

			var made = new BakeLight
			{
				Colour = new Vec3( colour.r, colour.g, colour.b ),
				Forward = new Vec3( forward.x, forward.y, forward.z ),
				Position = new Vec3( position.x, position.y, position.z ),
				Falloff = light.Falloff,
				Radius = MathF.Max( 0.0f, light.SourceRadius ),
				Shadows = light.Shadows,
				Id = light.Id,

				//All of it in the bake - see Light.NotBaked
				Whole = true,
			};

			switch ( light )
			{
				case SpotLight spot:
					made.Kind = 2;
					made.Range = CoreSceneRenderer.EffectiveRange( spot.Radius, colour, spot.Falloff );
					made.CosInner = MathF.Cos( spot.ConeInner.DegreeToRadian() );
					made.CosOuter = MathF.Cos( spot.ConeOuter.DegreeToRadian() );
					break;

				case PointLight point:
					made.Kind = 1;
					made.Range = CoreSceneRenderer.EffectiveRange( point.Radius, colour, point.Falloff );

					if ( point.AreaShape != PointLight.AreaShapes.Point )
					{
						var right = point.WorldRotation.Right.Normal;
						var size = point.AreaSize;

						made.Shape = (int)point.AreaShape;
						made.Right = new Vec3( right.x, right.y, right.z );
						made.HalfWidth = MathF.Max( size.x * 0.5f, 0.001f );
						made.HalfHeight = made.Shape == 3 ? MathF.Max( point.SourceRadius, 0.005f )
							: made.Shape == 2 ? made.HalfWidth : MathF.Max( size.y * 0.5f, 0.001f );
					}

					break;

				case DirectionalLight:
					made.Kind = 0;
					break;

				default:
					continue;
			}

			if ( made.Colour == Vec3.Zero ) continue;

			Lights.Add( made );
		}
	}

	/// <summary>A random point on a shaped lamp, and its emitting cosine towards a point - nought from behind.</summary>
	public static Vec3 PointOn( BakeLight light, Vec3 position, ref BakeRandom random, out float cosine )
	{
		var u = random.Float();
		var v = random.Float();
		var up = Vec3.Cross( light.Forward, light.Right );

		Vec3 point, facing;

		if ( light.Shape == 3 )
		{
			//Side on, as AreaLamp draws it: the panel through the axis facing the point
			var across = Vec3.Cross( light.Right, position - light.Position );
			var side = across.Length();
			across = side > 1e-5f ? across / side : up;

			point = light.Position + ( light.Right * ( light.HalfWidth * ( ( 2.0f * u ) - 1.0f ) ) ) + ( across * ( light.HalfHeight * ( ( 2.0f * v ) - 1.0f ) ) );
			facing = Vec3.Cross( across, light.Right );
		}
		else if ( light.Shape == 2 )
		{
			var r = light.HalfWidth * MathF.Sqrt( u );
			var angle = 2.0f * BakeMath.Pi * v;

			point = light.Position + ( ( ( light.Right * MathF.Cos( angle ) ) + ( up * MathF.Sin( angle ) ) ) * r );
			facing = light.Forward;
		}
		else
		{
			point = light.Position + ( light.Right * ( light.HalfWidth * ( ( 2.0f * u ) - 1.0f ) ) ) + ( up * ( light.HalfHeight * ( ( 2.0f * v ) - 1.0f ) ) );
			facing = light.Forward;
		}

		var away = position - point;
		var length = away.Length();
		cosine = length > 1e-6f ? Vec3.Dot( away / length, facing ) : 0.0f;

		//A tube gives off all round, so whichever side the point is on
		if ( light.Shape == 3 ) cosine = MathF.Abs( cosine );

		return point;
	}

	private void GatherSky( Scene scene ) => Sky.Gather( scene );

	/// <summary>Builds the tree over what was gathered. Any thread - and it uses all of them.</summary>
	public void Build()
	{
		var triangles = Corners.Length / 3;
		var soup = new Vec3[triangles * 3];
		var tested = new bool[triangles];

		Facing = new Vec3[triangles];

		for ( var t = 0; t < triangles; ++t )
		{
			var a = Positions[Corners[t * 3]];
			var b = Positions[Corners[( t * 3 ) + 1]];
			var c = Positions[Corners[( t * 3 ) + 2]];

			soup[t * 3] = a;
			soup[( t * 3 ) + 1] = b;
			soup[( t * 3 ) + 2] = c;

			//Which way it faces, by its corners' normals rather than by its winding: an importer that
			//turned the winding over and kept the normals is still a surface facing the way it is lit.
			var cross = Vec3.Cross( b - a, c - a );
			var length = cross.Length();
			var face = length > 1e-12f ? cross / length : Normals[Corners[t * 3]];

			var said = Normals[Corners[t * 3]] + Normals[Corners[( t * 3 ) + 1]] + Normals[Corners[( t * 3 ) + 2]];
			if ( Vec3.Dot( face, said ) < 0.0f ) face = -face;

			Facing[t] = face;

			var material = Materials[MaterialOf[t]];
			tested[t] = clear.Contains( t ) || material.Translucent || material.Masked;
		}

		Bvh.Build( soup, tested, Through );
		GatherEmitters();
		FindMirrors();
	}

	/// <summary>The flat reflectors lamps are mirrored in, the strongest few - see Reflected.</summary>
	public BakeMirror[] Mirrors = [];

	/// <summary>Each world triangle's mirror in Mirrors, or -1.</summary>
	public int[] MirrorOf = [];

	//Most mirrors a bake looks in: each is three rays more per lamp per light sample
	private const int MostMirrors = 4;

	//The flat reflectors that throw lamps' light onto what faces them: water and glass by their reflectance, and
	//metal polished enough to show an image; a plane's triangles together, a quarter of a square metre at least (a
	//polished ball's facets are no mirror), the strongest few by area times reflectance
	private void FindMirrors()
	{
		var triangles = MaterialOf.Length;
		var planes = new Dictionary<(int, int, int, int, bool), (BakeMirror Mirror, float Area, float Power, List<int> Triangles)>();

		MirrorOf = new int[triangles];
		Array.Fill( MirrorOf, -1 );

		for ( var t = 0; t < triangles; ++t )
		{
			var material = Materials[MaterialOf[t]];
			var uv = Interpolate( Uv0, t, 1.0f / 3.0f, 1.0f / 3.0f );
			Vec3 reflectance;

			if ( material.Translucent && material.Reflectance > 0.0f ) reflectance = new Vec3( material.Reflectance );
			else if ( !material.Translucent && material.MetalnessAt( uv ) >= 0.5f && material.RoughnessAt( uv ) <= 0.25f )
			{
				var texel = material.Colour?.Sample( uv.X, uv.Y ) ?? Vec4.One;
				reflectance = Vec3.Clamp( new Vec3( texel.X, texel.Y, texel.Z ) * material.Tint * TintOf[t], Vec3.Zero, Vec3.One );
			}
			else continue;

			//See-through ones mirror from either side, so either way round is the one plane
			var dielectric = material.Translucent;
			var n = Facing[t];
			if ( dielectric && ( n.X + ( n.Y * 2.0f ) + ( n.Z * 4.0f ) ) < 0.0f ) n = -n;

			var offset = Vec3.Dot( n, Positions[Corners[t * 3]] );
			var key = ((int)MathF.Round( n.X * 50.0f ), (int)MathF.Round( n.Y * 50.0f ), (int)MathF.Round( n.Z * 50.0f ),
				(int)MathF.Round( offset * 50.0f ), dielectric);

			if ( !planes.TryGetValue( key, out var plane ) ) plane = (new BakeMirror( n, offset, reflectance, dielectric ), 0.0f, 0.0f, new List<int>());

			plane.Area += Area( t );
			plane.Power += Area( t ) * BakeMath.Luma( reflectance );
			plane.Triangles.Add( t );
			planes[key] = plane;
		}

		var kept = new List<BakeMirror>();

		foreach ( var plane in planes.Values.Where( p => p.Area >= 0.25f && p.Power > 0.0f ).OrderByDescending( p => p.Power ).Take( MostMirrors ) )
		{
			foreach ( var t in plane.Triangles ) MirrorOf[t] = kept.Count;
			kept.Add( plane.Mirror );
		}

		Mirrors = kept.ToArray();
	}

	//The triangles that glow and that a ray can find, by area times what they give off on average
	private void GatherEmitters()
	{
		var triangles = Corners.Length / 3;
		var found = new List<int>();
		var sums = new List<float>();
		var total = 0.0f;

		GlowDensity = new float[triangles];

		for ( var t = 0; t < triangles; ++t )
		{
			var material = Materials[MaterialOf[t]];
			if ( !material.Glows || GroupOf[t] != Pass || ThroughMode( t ) == 1 ) continue;

			var power = Area( t ) * BakeMath.Luma( material.Emission * material.MeanGlow() );
			if ( !( power > 0.0f ) ) continue;

			total += power;
			found.Add( t );
			sums.Add( total );
		}

		Emitters = found.ToArray();
		EmitterSum = sums.ToArray();

		for ( var i = 0; i < Emitters.Length; ++i )
		{
			var t = Emitters[i];
			var power = EmitterSum[i] - ( i > 0 ? EmitterSum[i - 1] : 0.0f );

			GlowDensity[t] = power / total / Area( t );
		}
	}

	/// <summary>What a triangle reflects at a point, as Surface reads it - the denoiser's guide.</summary>
	internal Vec3 AlbedoAt( int t, float u, float v )
	{
		var material = Materials[MaterialOf[t]];
		var uv = Interpolate( Uv0, t, u, v );
		var texel = material.Colour?.Sample( uv.X, uv.Y ) ?? Vec4.One;

		return new Vec3( texel.X, texel.Y, texel.Z ) * material.Tint * TintOf[t] * Interpolate( Colours, t, u, v ) * ( 1.0f - material.MetalnessAt( uv ) );
	}

	internal float Area( int t )
	{
		var a = Positions[Corners[t * 3]];
		return Vec3.Cross( Positions[Corners[( t * 3 ) + 1]] - a, Positions[Corners[( t * 3 ) + 2]] - a ).Length() * 0.5f;
	}

	//How a ray meets a triangle of the world, for the card - see BakeGpu: nought it stops, one
	//it always passes (glass, or cleared), two it asks the material's cut-out, as Through does.
	internal int ThroughMode( int t )
	{
		if ( clear.Contains( t ) ) return 1;

		var material = Materials[MaterialOf[t]];

		if ( material.Translucent ) return 1;

		return material.Masked ? 2 : 0;
	}

	/// <summary>Whether any triangle is see-through glass, which lets only part of a ray by.</summary>
	public bool Veiled => veiled ??= Array.Exists( MaterialOf, m => Materials[m].Translucent );

	private bool? veiled;

	//How much of a ray gets by a triangle it crosses, each colour: none of a solid one, all or none of a cut-out where
	//cut, and of glass what its mirror leaves at this angle and its opacity lets by or scatters on; bake_trace.slang's Veil
	private Vec3 Veil( int t, float u, float v, bool tested, Vec3 direction )
	{
		if ( !tested ) return Vec3.Zero;

		var material = Materials[MaterialOf[t]];
		if ( clear.Contains( t ) || !material.Translucent ) return Through( t, u, v ) ? Vec3.One : Vec3.Zero;

		var uv = Interpolate( Uv0, t, u, v );
		var texel = material.Colour?.Sample( uv.X, uv.Y ) ?? Vec4.One;
		var opacity = Math.Clamp( material.Alpha * texel.W, 0.0f, 1.0f );
		var colour = Vec3.Clamp( new Vec3( texel.X, texel.Y, texel.Z ) * material.Tint * TintOf[t], Vec3.Zero, Vec3.One );

		var f0 = material.Reflectance;
		var cosine = Math.Clamp( MathF.Abs( Vec3.Dot( direction, Facing[t] ) ), 0.0f, 1.0f );
		var mirrored = f0 + ( ( 1.0f - f0 ) * MathF.Pow( 1.0f - cosine, 5.0f ) );
		var lets = ( Vec3.Lerp( Vec3.One, colour, opacity ) * ( 1.0f - opacity ) ) + ( colour * ( opacity * 0.5f ) );

		return lets * ( 1.0f - mirrored );
	}

	/// <summary>How much of a ray gets through to a distance, each colour; yes or no where there is no glass.</summary>
	public Vec3 Transmittance( Vec3 origin, Vec3 direction, float furthest )
		=> Veiled ? Bvh.Transmittance( origin, direction, furthest, Veil ) : Bvh.Blocked( origin, direction, furthest ) ? Vec3.Zero : Vec3.One;

	//Whether any colour of a transmittance gets through
	private static bool Passes( Vec3 through ) => through.X > 0.0f || through.Y > 0.0f || through.Z > 0.0f;

	/// <summary>What glass this thread's last Arriving's first ray crossed let by: an escape's share of sky.</summary>
	public static float FirstVeil => firstVeil;

	[ThreadStatic] private static float firstVeil;

	//Whether a ray passes a triangle that is not solid all over, at a point of it.
	private bool Through( int t, float u, float v )
	{
		if ( clear.Contains( t ) ) return true;

		var material = Materials[MaterialOf[t]];

		if ( material.Translucent ) return true;
		if ( !material.Masked ) return false;

		var alpha = material.Alpha;

		if ( material.Colour is { } picture )
		{
			var uv = Interpolate( Uv0, t, u, v );
			alpha *= picture.Sample( uv.X, uv.Y ).W;
		}

		return alpha < material.Cutoff;
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public System.Numerics.Vector2 Interpolate( System.Numerics.Vector2[] values, int t, float u, float v )
		=> ( values[Corners[t * 3]] * ( 1.0f - u - v ) ) + ( values[Corners[( t * 3 ) + 1]] * u ) + ( values[Corners[( t * 3 ) + 2]] * v );

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public Vec3 Interpolate( Vec3[] values, int t, float u, float v )
		=> ( values[Corners[t * 3]] * ( 1.0f - u - v ) ) + ( values[Corners[( t * 3 ) + 1]] * u ) + ( values[Corners[( t * 3 ) + 2]] * v );

	/// <summary>What a ray found, as a surface.</summary>
	public BakeSurface Surface( in BakeHit hit, Vec3 origin, Vec3 direction )
	{
		var t = hit.Triangle;
		var material = Materials[MaterialOf[t]];

		BakeSurface s;
		s.Position = origin + ( direction * hit.Distance );
		s.Geometric = Facing[t];

		var normal = Interpolate( Normals, t, hit.U, hit.V );
		var length = normal.Length();
		s.Normal = length > 1e-8f ? normal / length : s.Geometric;

		s.Back = Vec3.Dot( direction, s.Geometric ) > 0.0f;

		//A sheet drawn from both sides is lit from both sides: from behind it is the same surface facing the other way.
		if ( s.Back && material.TwoSided )
		{
			s.Geometric = -s.Geometric;
			s.Normal = -s.Normal;
			s.Back = false;
		}

		var uv = Interpolate( Uv0, t, hit.U, hit.V );
		var texel = material.Colour?.Sample( uv.X, uv.Y ) ?? Vec4.One;
		var vertex = Interpolate( Colours, t, hit.U, hit.V );

		var albedo = new Vec3( texel.X, texel.Y, texel.Z ) * material.Tint * TintOf[t] * vertex;

		//Never quite white: a surface reflecting all bounces forever, and no paint does. Metal keeps none
		//of it diffuse; what it and everything else mirrors is the specular colour - see Sheen
		var metal = material.MetalnessAt( uv );
		s.Albedo = Vec3.Min( albedo * ( 1.0f - metal ) * AlbedoBoost, new Vec3( 0.95f ) );
		s.Specular = Vec3.Lerp( new Vec3( 0.04f ), Vec3.Min( albedo, Vec3.One ), metal );
		s.Roughness = material.RoughnessAt( uv );

		s.Emission = material.Glows && GroupOf[t] == Pass ? GlowOf( material, uv, texel ) : Vec3.Zero;

		return s;
	}

	private static Vec3 GlowOf( BakeMaterial material, System.Numerics.Vector2 uv, Vec4 texel )
	{
		var glow = material.EmissionUsesAlbedo ? new Vec3( texel.X, texel.Y, texel.Z ) : Vec3.One;
		var map = material.EmissionMap?.Sample( uv.X, uv.Y ) ?? Vec4.One;

		return new Vec3( map.X, map.Y, map.Z ) * glow * material.Emission;
	}

	/// <summary>The light a surface mirrors towards a way, off its GGX lobe - bake_trace.slang's Sheen.</summary>
	public Vec3 Sheen( in BakeSurface s, Vec3 towards, ref BakeRandom random )
	{
		var nDotV = Vec3.Dot( s.Normal, towards );
		if ( nDotV <= 1e-4f ) return Vec3.Zero;

		var mirrored = BakeMath.Luma( s.Specular ) + 0.02f;
		var taken = Math.Clamp( mirrored / ( mirrored + BakeMath.Luma( s.Albedo ) ), 0.05f, 1.0f );
		if ( random.Float() >= taken ) return Vec3.Zero;

		var alpha = MathF.Max( s.Roughness * s.Roughness, 1e-3f );
		var u = random.Float();
		var v = random.Float();

		var cosine = MathF.Sqrt( ( 1.0f - v ) / ( 1.0f + ( ( ( alpha * alpha ) - 1.0f ) * v ) ) );
		var sine = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - ( cosine * cosine ) ) );
		var angle = 2.0f * BakeMath.Pi * u;

		BakeMath.Basis( s.Normal, out var right, out var up );
		var between = Vec3.Normalize( ( right * ( sine * MathF.Cos( angle ) ) ) + ( up * ( sine * MathF.Sin( angle ) ) ) + ( s.Normal * cosine ) );

		var vDotH = Vec3.Dot( towards, between );
		if ( vDotH <= 0.0f ) return Vec3.Zero;

		var l = ( 2.0f * vDotH * between ) - towards;
		var nDotL = Vec3.Dot( s.Normal, l );
		if ( nDotL <= 0.0f || Vec3.Dot( l, s.Geometric ) <= 0.0f ) return Vec3.Zero;

		var k = alpha * 0.5f;
		var smith = ( nDotV / ( ( nDotV * ( 1.0f - k ) ) + k ) ) * ( nDotL / ( ( nDotL * ( 1.0f - k ) ) + k ) );
		var fresnel = s.Specular + ( ( Vec3.One - s.Specular ) * MathF.Pow( 1.0f - vDotH, 5.0f ) );
		var share = fresnel * ( smith * vDotH / MathF.Max( nDotV * Math.Clamp( Vec3.Dot( s.Normal, between ), 0.0f, 1.0f ), 1e-4f ) ) / taken;

		var from = Offset( s.Position, s.Geometric, l );
		var found = Bvh.Nearest( from, l, 1e6f, out var hit );
		if ( Veiled ) share *= Transmittance( from, l, found ? hit.Distance * 0.9999f : 1e6f );
		if ( !found ) return share * Environment( l );

		var there = Surface( hit, from, l );
		if ( there.Back ) return Vec3.Zero;

		var leaving = there.Emission;

		if ( Cached( hit, out var cached ) ) leaving += there.Albedo * cached;
		else
		{
			var ignored = Vec3.Zero;
			leaving += there.Albedo * ( Direct( there.Position, there.Normal, there.Geometric, ref random, false, ref ignored )
				+ GlowDirect( there.Position, there.Normal, there.Geometric, ref random, ref ignored ) );
		}

		return share * leaving;
	}

	/// <summary>What a leaf or thin cloth passes to its front of one sample of its back's light, over pi.</summary>
	//As foliage.shader's F_TRANSMISSION without the glow towards the eye: half of what a front would take, so pi over
	//two of the back's irradiance over pi - the sky it sees counted here, as the frame's sky is for the front alone
	public Vec3 Transmission( Vec3 position, Vec3 normal, Vec3 geometric, Vec3 passes, int mode, bool aiming, ref BakeRandom random )
	{
		var n = -normal;
		var g = -geometric;

		var axis = n;
		var direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );

		if ( Vec3.Dot( direction, g ) <= 0.0f )
		{
			axis = g;
			direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );
		}

		var density = Emitters.Length > 0 || aiming ? Vec3.Dot( axis, direction ) * BakeMath.InvPi : 0.0f;
		var arriving = Arriving( Offset( position, g, direction ), direction, density, ref random, out var inside, out var escaped );
		if ( inside ) return Vec3.Zero;

		if ( escaped ) arriving += Environment( direction ) * firstVeil;

		var litLight = TowardsCache( position, g, ref random, out var towardsLit, out var litChance );

		if ( litChance > 0.0f )
		{
			var facing = Vec3.Dot( n, towardsLit );
			if ( facing > 0.0f ) arriving += litLight * ( facing * BakeMath.InvPi * Weight( litChance, facing * BakeMath.InvPi ) / litChance );
		}

		if ( ( mode & BakeGpu.ModeNoDirect ) == 0 )
		{
			var ignored = Vec3.Zero;
			arriving += Direct( position, n, g, ref random, ( mode & BakeGpu.ModeAllLights ) == 0, ref ignored )
				+ GlowDirect( position, n, g, ref random, ref ignored );
		}

		return passes * arriving * ( BakeMath.Pi * 0.5f );
	}

	/// <summary>A world triangle's chance per square metre of being where a gather's light sample lands.</summary>
	public float CacheDensity( int t ) => Gathering?.Lights is { Count: > 0 } lights ? lights.Floats[t] : 0.0f;

	/// <summary>The light over pi the last pass left at a ray's landing on a lightmapped triangle.</summary>
	public bool Cached( in BakeHit hit, out Vec3 light )
	{
		light = Vec3.Zero;
		if ( Gathering?.Cache is not { } cache || atlasSpots is null ) return false;

		var a = atlasSpots[Corners[hit.Triangle * 3]];
		var b = atlasSpots[Corners[( hit.Triangle * 3 ) + 1]];
		var c = atlasSpots[Corners[( hit.Triangle * 3 ) + 2]];
		if ( a.X < 0.0f || b.X < 0.0f || c.X < 0.0f ) return false;

		var spot = ( a * ( 1.0f - hit.U - hit.V ) ) + ( b * hit.U ) + ( c * hit.V );
		var x = Math.Clamp( (int)spot.X, 0, AtlasSide - 1 );
		var y = Math.Clamp( (int)spot.Y, 0, AtlasSide - 1 );
		var at = ( ( y * AtlasSide ) + x ) * 4;

		light = new Vec3( cache[at], cache[at + 1], cache[at + 2] );
		return true;
	}

	//Where each vertex of a receiver lands on the sheet, in texels; off it, negative
	private System.Numerics.Vector2[] AtlasSpots()
	{
		var spots = new System.Numerics.Vector2[Normals.Length];
		Array.Fill( spots, new System.Numerics.Vector2( -1.0f ) );

		foreach ( var receiver in Receivers )
		{
			for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
			{
				for ( var k = 0; k < 3; ++k )
				{
					var v = Corners[( t * 3 ) + k];
					var uv = System.Numerics.Vector2.Clamp( Uv1[v], System.Numerics.Vector2.Zero, System.Numerics.Vector2.One );
					spots[v] = ( uv * receiver.Side ) + new System.Numerics.Vector2( receiver.X, receiver.Y );
				}
			}
		}

		return spots;
	}

	/// <summary>A point on a lit triangle of the cache, picked by power - bake_trace.slang's TowardsCache.</summary>
	//The light it leaves towards the surface, over pi, unweighed; its way and the chance per steradian it was picked
	public Vec3 TowardsCache( Vec3 position, Vec3 geometric, ref BakeRandom random, out Vec3 aim, out float chance )
	{
		aim = Vec3.Zero;
		chance = 0.0f;

		if ( Gathering?.Lights is not { Count: > 0 } lights ) return Vec3.Zero;

		var floats = lights.Floats;
		var list = MaterialOf.Length;
		var count = lights.Count;

		var picked = floats[list + ( ( count - 1 ) * CacheLights.RecordFloats ) + 1] * random.Float();
		var root = MathF.Sqrt( random.Float() );
		var turn = random.Float();

		//The first whose power summed so far passes the pick
		var low = 0;
		var high = count - 1;

		while ( low < high )
		{
			var middle = ( low + high ) / 2;
			if ( floats[list + ( middle * CacheLights.RecordFloats ) + 1] > picked ) high = middle;
			else low = middle + 1;
		}

		var record = list + ( low * CacheLights.RecordFloats );

		var hit = new BakeHit { Triangle = BitConverter.SingleToInt32Bits( floats[record] ), U = root * ( 1.0f - turn ), V = root * turn };
		var corner = new Vec3( floats[record + 2], floats[record + 3], floats[record + 4] );
		var one = new Vec3( floats[record + 5], floats[record + 6], floats[record + 7] );
		var two = new Vec3( floats[record + 8], floats[record + 9], floats[record + 10] );

		var toPoint = corner + ( one * hit.U ) + ( two * hit.V ) - position;
		hit.Distance = toPoint.Length();
		if ( hit.Distance < 1e-5f ) return Vec3.Zero;

		aim = toPoint / hit.Distance;
		if ( Vec3.Dot( geometric, aim ) <= 0.0f ) return Vec3.Zero;

		var s = Surface( hit, position, aim );
		var facing = -Vec3.Dot( aim, s.Geometric );
		if ( s.Back || facing <= 1e-6f ) return Vec3.Zero;

		if ( !Cached( hit, out var cached ) ) return Vec3.Zero;
		var through = Transmittance( Offset( position, geometric, aim ), aim, ( hit.Distance * 0.999f ) - ( Lift * 2.0f ) );
		if ( !Passes( through ) ) return Vec3.Zero;

		chance = CacheDensity( hit.Triangle ) * hit.Distance * hit.Distance / facing;
		return s.Albedo * cached * through;
	}

	//Power heuristic: how much of a sample picked one way counts, against the chance the other way had of it
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static float Weight( float picked, float other ) => picked * picked / MathF.Max( ( picked * picked ) + ( other * other ), 1e-30f );

	/// <summary>A shadow ray to a glow picked by power: its light over that direction's chance, MIS-weighted.</summary>
	//Against a cosine bounce about normal on a surface, or a direction from the whole sphere where onSurface is false
	public Vec3 TowardsGlow( Vec3 position, Vec3 normal, Vec3 geometric, bool onSurface, ref BakeRandom random, out Vec3 aim )
	{
		aim = Vec3.Zero;

		if ( Emitters.Length == 0 ) return Vec3.Zero;

		var picked = EmitterSum[^1] * random.Float();
		var i = Array.BinarySearch( EmitterSum, picked );
		i = Math.Min( i < 0 ? ~i : i + 1, Emitters.Length - 1 );

		var t = Emitters[i];
		var root = MathF.Sqrt( random.Float() );
		var turn = random.Float();
		var u = root * ( 1.0f - turn );
		var v = root * turn;

		var point = Interpolate( Positions, t, u, v );
		var toPoint = point - position;
		var distance = toPoint.Length();
		if ( distance < 1e-5f ) return Vec3.Zero;

		aim = toPoint / distance;

		var material = Materials[MaterialOf[t]];
		var facing = Vec3.Dot( aim, Facing[t] );

		//Seen from behind it gives nothing - a ray arriving there finds the back of it
		var cosine = material.TwoSided ? MathF.Abs( facing ) : -facing;
		if ( cosine <= 1e-6f ) return Vec3.Zero;

		var bounce = BakeMath.InvPi * 0.25f;

		if ( onSurface )
		{
			var nDotL = Vec3.Dot( normal, aim );
			if ( nDotL <= 0.0f || Vec3.Dot( geometric, aim ) <= 0.0f ) return Vec3.Zero;

			bounce = nDotL * BakeMath.InvPi;
		}

		if ( material.Masked && Through( t, u, v ) ) return Vec3.Zero;

		var from = onSurface ? Offset( position, geometric, aim ) : position;
		var through = Transmittance( from, aim, ( distance * 0.999f ) - ( Lift * 2.0f ) );
		if ( !Passes( through ) ) return Vec3.Zero;

		var uv = Interpolate( Uv0, t, u, v );
		var texel = material.EmissionUsesAlbedo ? material.Colour?.Sample( uv.X, uv.Y ) ?? Vec4.One : Vec4.One;

		var density = GlowDensity[t] * distance * distance / cosine;

		return GlowOf( material, uv, texel ) * ( through * Weight( density, bounce ) / density );
	}

	/// <summary>Glowing triangles' direct light over pi on a surface, and its lean; added to Direct for a texel.</summary>
	public Vec3 GlowDirect( Vec3 position, Vec3 normal, Vec3 geometric, ref BakeRandom random, ref Vec3 towards )
	{
		if ( Emitters.Length == 0 ) return Vec3.Zero;

		var seen = TowardsGlow( position, normal, geometric, true, ref random, out var aim );
		if ( seen == Vec3.Zero ) return Vec3.Zero;

		var arriving = seen * ( Vec3.Dot( normal, aim ) * BakeMath.InvPi );
		towards += aim * BakeMath.Luma( arriving );

		return arriving;
	}

	/// <summary>A point a little off a surface, for a ray to leave from without finding the surface itself.</summary>
	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public static Vec3 Offset( Vec3 position, Vec3 geometric, Vec3 direction )
	{
		var side = Vec3.Dot( direction, geometric ) >= 0.0f ? 1.0f : -1.0f;
		var scale = MathF.Max( MathF.Max( MathF.Abs( position.X ), MathF.Abs( position.Y ) ), MathF.Abs( position.Z ) );

		return position + ( geometric * ( side * ( Lift + ( scale * 2e-6f ) ) ) );
	}

	/// <summary>The sky from one direction as a bounced ray sees it: none in a glow group's pass.</summary>
	public Vec3 Environment( Vec3 direction )
		=> Pass != 0 ? Vec3.Zero : Sky.Radiance( direction ) * SkyStrength;

	/// <summary>Direct lamp light over pi at a point, and its dominant direction; as surface_shading.slang.</summary>
	public Vec3 Direct( Vec3 position, Vec3 normal, Vec3 geometric, ref BakeRandom random, bool bakedOnly, ref Vec3 towards )
	{
		var sum = Vec3.Zero;
		if ( Pass != 0 ) return sum;

		foreach ( var light in Lights )
		{
			if ( bakedOnly && !light.Whole ) continue;

			var arriving = DirectOne( light, position, normal, geometric, ref random, out var l );

			sum += arriving;
			towards += l * BakeMath.Luma( arriving );

			for ( var m = 0; m < Mirrors.Length; ++m )
			{
				var mirrored = Reflected( light, m, position, normal, geometric, ref random, out var down );

				sum += mirrored;
				towards += down * BakeMath.Luma( mirrored );
			}
		}

		return sum;
	}

	/// <summary>A baked lamp's light at a squared distance: its falloff to its radius, then the inverse square.</summary>
	public static float Spread( BakeLight light, float square )
	{
		//No edge where the frame's lamp ends, as VRAD's lights have none: past it the light goes on as light does
		var edge = MathF.Max( light.Range * light.Range, 1e-4f );
		var near = MathF.Min( square, edge );
		var own = 1.0f / ( light.Falloff == 1.0f ? near : MathF.Pow( near, light.Falloff ) );

		return square > edge ? own * edge / square : own;
	}

	/// <summary>One light's share of <see cref="Direct"/>, and which way it arrives from.</summary>
	public Vec3 DirectOne( BakeLight light, Vec3 position, Vec3 normal, Vec3 geometric, ref BakeRandom random, out Vec3 l )
	{
		var lit = Unshadowed( light, position, normal, geometric, ref random, out l, out var aim, out var distance );
		if ( !Passes( lit ) || !light.Shadows ) return lit;

		//What glass between lets by - see Transmittance
		return lit * Transmittance( Offset( position, geometric, aim ), aim, distance - ( Lift * 2.0f ) );
	}

	//A lamp's light where nothing stands between, the way to the point on it sampled and how far that is
	private Vec3 Unshadowed( BakeLight light, Vec3 position, Vec3 normal, Vec3 geometric, ref BakeRandom random, out Vec3 l,
		out Vec3 aim, out float distance )
	{
		l = Vec3.Zero;
		aim = Vec3.Zero;
		distance = 0.0f;

		{
			var attenuation = 1.0f;

			if ( light.Kind == 0 )
			{
				l = -light.Forward;
				distance = 1e5f;
				aim = BakeMath.InCone( l, SunCosine, random.Float(), random.Float() );
			}
			else if ( light.Kind == 1 && light.Shape != 0 )
			{
				var centre = light.Position - position;
				var area = light.Area;
				var on = PointOn( light, position, ref random, out var cosine );
				if ( cosine <= 0.0f || area <= 1e-8f ) return Vec3.Zero;

				var toPoint = on - position;
				distance = toPoint.Length();
				if ( distance < 1e-5f ) return Vec3.Zero;

				aim = toPoint / distance;
				l = aim;

				//As AreaLamp: radiance colour over area, the lamp's falloff kept as its ratio to the inverse square
				var square = MathF.Max( centre.LengthSquared(), 0.25f * area );
				var ratio = square * Spread( light, square );

				attenuation = cosine * ratio / MathF.Max( distance * distance, 1e-4f );
			}
			else
			{
				var toLight = light.Position - position;
				distance = toLight.Length();

				if ( distance < 1e-5f ) return Vec3.Zero;

				l = toLight / distance;
				attenuation = Spread( light, MathF.Max( distance * distance, 1e-4f ) );

				if ( light.Kind == 2 )
				{
					var aligned = Vec3.Dot( -l, light.Forward );
					attenuation *= BakeMath.Saturate( ( aligned - light.CosOuter ) / MathF.Max( light.CosInner - light.CosOuter, 1e-4f ) );
				}

				//A point on the lamp's disc as this point sees it.
				var at = light.Position;

				if ( light.Radius > 0.0f )
				{
					BakeMath.Basis( l, out var right, out var up );

					var r = light.Radius * MathF.Sqrt( random.Float() );
					var angle = 2.0f * BakeMath.Pi * random.Float();

					at += ( right * ( r * MathF.Cos( angle ) ) ) + ( up * ( r * MathF.Sin( angle ) ) );
				}

				var toPoint = at - position;
				distance = toPoint.Length();
				aim = toPoint / MathF.Max( distance, 1e-6f );
			}

			var nDotL = Vec3.Dot( normal, l );
			if ( nDotL <= 0.0f || attenuation <= 0.0f ) return Vec3.Zero;

			//Nothing arrives from under the surface's own plane, whatever the bent normal thinks.
			if ( Vec3.Dot( geometric, aim ) <= 0.0f ) return Vec3.Zero;

			return light.Colour * ( nDotL * attenuation / BakeMath.Pi );
		}
	}

	//A lamp's light off one mirror: the lamp as the point's image behind the mirror sees it, where the way between
	//crosses the mirror and both legs are clear - exact for a flat one, where a ray sent at random almost never finds it
	private Vec3 Reflected( BakeLight light, int index, Vec3 position, Vec3 normal, Vec3 geometric, ref BakeRandom random, out Vec3 down )
	{
		down = Vec3.Zero;

		var mirror = Mirrors[index];
		var n = mirror.Normal;
		var side = Vec3.Dot( n, position ) - mirror.Offset;

		if ( side < 0.0f )
		{
			if ( !mirror.Dielectric ) return Vec3.Zero;

			n = -n;
			side = -side;
		}

		if ( side < 1e-3f ) return Vec3.Zero;

		var image = position - ( n * ( 2.0f * side ) );
		var lit = Unshadowed( light, image, Reflect( normal, n ), Reflect( geometric, n ), ref random, out _, out var aim, out var distance );
		if ( !Passes( lit ) ) return Vec3.Zero;

		//Where the image's way to the lamp crosses the mirror, and the real way from the point to there
		var rise = Vec3.Dot( n, aim );
		if ( rise <= 1e-4f ) return Vec3.Zero;

		var reach = side / rise;
		if ( reach >= distance ) return Vec3.Zero;

		var spot = image + ( aim * reach );
		if ( !Bvh.Touches( spot + ( n * 0.01f ), -n, 0.02f, t => MirrorOf[t] == index ) ) return Vec3.Zero;

		var way = Reflect( aim, n );
		var through = Vec3.One;

		if ( light.Shadows )
		{
			through = Transmittance( Offset( position, geometric, way ), way, reach - ( Lift * 2.0f ) );
			if ( !Passes( through ) ) return Vec3.Zero;

			through *= Transmittance( Offset( spot, n, aim ), aim, distance - reach - ( Lift * 2.0f ) );
		}

		//Schlick's for water and glass, rising to all of it at a grazing angle; metal mirrors its colour
		var fresnel = mirror.Dielectric ? mirror.Reflectance + ( ( Vec3.One - mirror.Reflectance ) * MathF.Pow( 1.0f - rise, 5.0f ) ) : mirror.Reflectance;

		down = way;
		return lit * through * fresnel;
	}

	private static Vec3 Reflect( Vec3 v, Vec3 n ) => v - ( n * ( 2.0f * Vec3.Dot( v, n ) ) );

	/// <summary>Path-traced radiance from one direction; lamps and glows sampled, roulette past bounce two.</summary>
	//Density: the caller's chance per steradian of this direction when it also sampled a glow, else nought
	//Escaped: the ray left straight to the sky, which the frame lights by the visibility kept instead - see SkyLit
	public Vec3 Arriving( Vec3 origin, Vec3 direction, float density, ref BakeRandom random, out bool inside, out bool escaped )
	{
		inside = false;
		escaped = false;

		var carried = Vec3.One;
		var gathered = Vec3.Zero;

		firstVeil = 1.0f;

		for ( var bounce = 0; ; ++bounce )
		{
			var found = Bvh.Nearest( origin, direction, 1e6f, out var hit );

			//Glass on the way lets only its share by - nothing to ask where the world has none
			if ( Veiled )
			{
				var through = Transmittance( origin, direction, found ? hit.Distance * 0.9999f : 1e6f );
				carried *= through;
				if ( bounce == 0 ) firstVeil = ( through.X + through.Y + through.Z ) / 3.0f;
			}

			if ( !found )
			{
				//The sky is the frame's for a texel's own escapes, unless a pass gathers it into the light it leaves
				escaped = bounce == 0 && ( Gathering is null || ( Gathering.Mode & BakeGpu.ModeSky ) == 0 );
				return escaped ? gathered : gathered + ( carried * Environment( direction ) );
			}

			var s = Surface( hit, origin, direction );

			//A back face first is a point inside something; a front one however near is what shades it
			if ( s.Back )
			{
				if ( bounce == 0 ) inside = true;
				return gathered;
			}

			if ( s.Emission != Vec3.Zero )
			{
				//What TowardsGlow at the last point could have found as well counts only its share
				var glow = density > 0.0f && Emitters.Length > 0 ? GlowDensity[hit.Triangle] : 0.0f;
				var cosine = MathF.Abs( Vec3.Dot( direction, s.Geometric ) );

				gathered += carried * s.Emission * ( glow > 0.0f && cosine > 1e-6f
					? Weight( density, glow * hit.Distance * hit.Distance / cosine )
					: 1.0f );
			}

			if ( bounce >= Bounces ) return gathered;

			//Gathering: the light the last pass found here, every bounce of it, in place of the rest of the path
			if ( bounce == 0 && Gathering is not null && ( Gathering.Mode & BakeGpu.ModeGather ) != 0 && Cached( hit, out var cached ) )
			{
				//What TowardsCache could have found here as well counts only its share
				var lit = CacheDensity( hit.Triangle );
				var facing = MathF.Abs( Vec3.Dot( direction, s.Geometric ) );
				var share = density > 0.0f && lit > 0.0f && facing > 1e-6f ? Weight( density, lit * hit.Distance * hit.Distance / facing ) : 1.0f;

				return gathered + ( carried * ( ( s.Albedo * cached * share ) + Sheen( s, -direction, ref random ) ) );
			}

			var ignored = Vec3.Zero;
			gathered += carried * s.Albedo * ( Direct( s.Position, s.Normal, s.Geometric, ref random, false, ref ignored )
				+ GlowDirect( s.Position, s.Normal, s.Geometric, ref random, ref ignored ) );

			carried *= s.Albedo;

			if ( bounce >= 1 )
			{
				var keep = MathF.Min( MathF.Max( carried.X, MathF.Max( carried.Y, carried.Z ) ), 0.95f );
				if ( random.Float() >= keep ) return gathered;
				carried /= keep;
			}

			var axis = s.Normal;
			direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );

			//A bent normal can point a ray under the plane it stands on.
			if ( Vec3.Dot( direction, s.Geometric ) <= 0.0f )
			{
				axis = s.Geometric;
				direction = BakeMath.CosineAround( axis, random.Float(), random.Float() );
			}

			density = Emitters.Length > 0 ? Vec3.Dot( axis, direction ) * BakeMath.InvPi : 0.0f;
			origin = Offset( s.Position, s.Geometric, direction );
		}
	}
}

//A static renderer with its own lightmap sheet: its triangles in the world and where its sheet lands in the atlas
internal sealed class BakeReceiver
{
	/// <summary>What is baked onto: a ModelRenderer, or a MeshComponent with its own sheet.</summary>
	public readonly Component Renderer;
	public readonly Model Model;
	public readonly int FirstTriangle;
	public readonly int TriangleCount;

	/// <summary>Where it was packed, in texels, with its side.</summary>
	public int X, Y, Side;

	/// <summary>Its density against the bake's - see ModelRenderer.LightmapScale.</summary>
	public float Scale => Math.Clamp( Renderer switch
	{
		ModelRenderer model => model.LightmapScale,
		MeshComponent mesh => mesh.LightmapScale,
		_ => 1.0f,
	}, 0.125f, 8.0f );

	public BakeReceiver( Component renderer, Model model, int first, int count )
	{
		Renderer = renderer;
		Model = model;
		FirstTriangle = first;
		TriangleCount = count;
	}
}
