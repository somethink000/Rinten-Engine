namespace Rinten.Rendering;

using NativeEngine;
using System.Buffers;
using System.Runtime.InteropServices;

//This object renders every sprite registered to it in a single draw call. It takes care of sorting, sampling, and the whole
//pipeline regarding sprites. The SceneSpriteSystem is responsible for pushing sprites into this object depending on its properties.
internal sealed class SpriteBatchSceneObject : SceneCustomObject
{
	internal readonly record struct SpriteGroup( SpriteData[] SharedSprites, int Offset, int Count, BBox Bounds );
	public bool Sorted { get; set; } = false;
	public bool Filtered { get; set; } = false;
	public bool Additive { get; set; } = false;
	public bool Opaque { get; set; } = false;

	internal Dictionary<Guid, SpriteRenderer> Components = new();

	private static ComputeShader SpriteComputeShader => field ??= new( "sprite/sprite_cs" );
	private static ComputeShader SortComputeShader => field ??= new( "sort_cs" );
	private static readonly uint[] ZeroUint = [0];
	private readonly GpuBuffer<uint> SpriteAtomicCounter;

	//Material for the whole batch (one batch is one draw), or the engine's sprite shader if none
	internal Material Material { get; set; }

	//The engine's own, which a batch that was given none draws with.
	private static Material DefaultMaterial => field ??= Material.FromShader( "shaders/sprite/sprite_ps.shader" );

	//Material actually bound: the given one if it is a sprite shader, else the engine's default
	private Material DrawnWith
		=> Material is not null && CoreResources.IsSpriteShader( Material.core?.Shader )
			? Material
			: DefaultMaterial;

	internal Dictionary<Guid, SpriteGroup> SpriteGroups = [];

	/// <summary>Flags for sprite rendering, must match SpriteFlags in sprite_ps.shader</summary>
	[Flags]
	public enum SpriteFlags
	{
		None = 0x0,
		CastShadows = 0x1,
		FlipX = 0x2,
		FlipY = 0x4,
		SnapToFrame = 0x8,

		/// <summary>Drawn by adding to what is behind it. Set by the batch on every sprite it uploads, because the pipeline that adds reads no alpha - see VkPipelines - so the shader has to fold the alpha into the colour itself, and this is how it knows to.</summary>
		Additive = 0x10,

		/// <summary>Pixel art: the picture is read as hard-edged texels, see SpriteRenderer.TextureFilter. The shader does it, because a picture here is bound with the one sampler it was made with and there is no table of others to pick from.</summary>
		Pixelated = 0x20,
	}

	//GPU Resident representation of a sprite. Four-byte packing, which is how sprite_ps.shader reads it: packed to sixteen, Mono lays a Vector4
	//on a sixteen-byte boundary where CoreCLR does not, and a sprite in a browser read its motion blur and everything after it twelve bytes off.
	[StructLayout( LayoutKind.Sequential, Pack = 4 )]
	public struct SpriteData
	{
		public Vector3 Position;
		public Vector3 Rotation;
		public Vector2 Scale;
		public uint TintColor;      //Packed RGBA8
		public uint OverlayColor;   //Packed RGBA8
		public int TextureHandle;
		public int RenderFlags;
		public uint BillboardMode;
		public uint FogStrengthCutout;  //Lower 16 bits: fog, upper 16 bits: alpha cutout
		public uint Lighting;
		public float DepthFeather;
		public int Splots = 0;
		public int Sequence = 0;
		public float SequenceTime = 0;
		public float RotationOffset = -1.0f;
		public Vector4 MotionBlur;
		public Vector3 Velocity = Vector3.Zero;
		public Vector4 BlendSheetUV;
		public Vector2 Offset;
		public uint CameraFade;     //Two halves: lower 16 bits near distance, upper 16 bits far distance - see sprite_ps.shader
		public Vector4 Region = new( 0, 0, 1, 1 );  //The part of the picture drawn: top left and size, in texture coordinates

		//The bit of Lighting that asks for the scene's 2D lighting, above the 3D lit byte and below the tint's
		//exponent - see Light2DAt in sprite.slang
		internal const uint Lit2D = 1u << 8;

		//The bit of Lighting that makes the sprite block 2D light by its picture's shape - see light2d_sprites.slang
		internal const uint Occluder2D = 1u << 9;

		//How brightly the sprite glows into the 2D lighting, in sixteenths, in the top byte of Lighting - see Emission2DOf
		internal const int Emission2DShift = 24;

		//A 2D emission strength as the top byte of Lighting carries it: sixteenths, up to just under sixteen
		internal static uint Emission2DOf( float strength )
			=> (uint)Math.Clamp( (int)MathF.Round( strength * 16.0f ), 0, 255 ) << Emission2DShift;

		public SpriteData()
		{

		}

		//Helper method to pack Color to RGBA8
		internal static uint PackColor( Color color )
		{
			byte r = (byte)(color.r * 255f);
			byte g = (byte)(color.g * 255f);
			byte b = (byte)(color.b * 255f);
			byte a = (byte)(color.a * 255f);
			return (uint)(r | (g << 8) | (b << 16) | (a << 24));
		}

		//Pack fog strength and alpha cutout into a single uint
		internal static uint PackFogAndAlphaCutout( float fogStrength, float alphaCutout )
		{
			ushort fogPacked = (ushort)(fogStrength.Clamp( 0f, 1f ) * 65535f);
			ushort alphaPacked = (ushort)(alphaCutout.Clamp( 0f, 1f ) * 65535f);
			return (uint)(fogPacked | (alphaPacked << 16));
		}

		//Pack the two camera fade distances (metres) into a single uint as halves
		internal static uint PackCameraFade( float near, float far )
		{
			uint nearPacked = BitConverter.HalfToUInt16Bits( (Half)MathF.Max( near, 0f ) );
			uint farPacked = BitConverter.HalfToUInt16Bits( (Half)MathF.Max( far, 0f ) );
			return nearPacked | (farPacked << 16);
		}
	}
	struct SpriteVertex
	{
		public Vector4 position;
		public Vector4 normal;
		public Vector2 uv;

		public SpriteVertex( Vector3 pos, Vector3 norm, Vector2 inUv )
		{
			position = new Vector4( pos, 1.0f );
			normal = new Vector4( norm, 0.0f );
			uv = inUv;
		}
	}

	const int DefaultBufferSize = 16;
	int CurrentBufferSize = DefaultBufferSize;

	int _splotCount = 0;
	int SplotCount
	{
		get
		{
			if ( _splotCount != 0 )
			{
				return _splotCount;
			}

			//Use precomputed splot counts to avoid iteration
			int splotCount = 0;
			foreach ( var group in SpriteGroups )
			{
				if ( _precomputedSplotCounts.TryGetValue( group.Key, out int precomputedCount ) )
				{
					splotCount += precomputedCount;
				}
				else
				{
					//Fallback
					var spriteGroup = group.Value;

					for ( int i = 0; i < spriteGroup.Count; i++ )
					{
						int index = spriteGroup.Offset + i;
						int leading = spriteGroup.SharedSprites[index].MotionBlur.x > 1 ? 2 : 1;
						splotCount += spriteGroup.SharedSprites[index].Splots * leading;
					}
				}
			}

			_splotCount = splotCount;

			return _splotCount;
		}
	}

	int SpriteCount
	{
		get
		{
			int sum = Components.Count;
			foreach ( var group in SpriteGroups )
			{
				sum += group.Value.Count;
			}

			return sum;
		}
	}

	bool GPUUploadQueued = false;

	//How many sprites a batch has to hold before its upload is worth sharing out across threads - see UploadOnHost.
	const int ParallelSprites = 2048;

	//Set by UploadOnHost after building the staging buffer, consumed by RenderSceneObject for early-out guard.
	int _pendingSpriteCount = 0;

	GpuBuffer<SpriteData> SpriteBuffer;
	GpuBuffer<SpriteData> SpriteBufferOut;

	GpuBuffer<SpriteVertex> VertexBuffer;
	GpuBuffer<int> IndexBuffer;
	GpuBuffer<uint> GPUSortingBuffer;
	GpuBuffer<float> GPUDistanceBuffer;

	SpriteData[] SpriteDataBuffer = null!;
	bool SpriteDataBufferRented = false;


	int _renderInstanceCount;

	public SpriteBatchSceneObject( Scene scene ) : base( scene.SceneWorld )
	{
		InitializeSpriteMesh();

		//GPU buffers
		SpriteBuffer = new( CurrentBufferSize );
		SpriteBufferOut = new( CurrentBufferSize );
		GPUSortingBuffer = new( CurrentBufferSize );
		GPUDistanceBuffer = new( CurrentBufferSize );
		SpriteAtomicCounter = new( 1 );
	}

	//Create the initialize sprite mesh that will be instanced
	private void InitializeSpriteMesh()
	{
		//Vertex pulling buffer
		const float spriteSize = 0.25f;
		SpriteVertex[] vertices =
		[
			new ( new ( -spriteSize, -spriteSize, 0 ), Vector3.Forward, new ( 0, 0 ) ),
			new ( new (  spriteSize, -spriteSize, 0 ), Vector3.Forward, new ( 1, 0 ) ),
			new ( new (  spriteSize,  spriteSize, 0 ), Vector3.Forward, new ( 1, 1 ) ),
			new ( new ( -spriteSize,  spriteSize, 0 ), Vector3.Forward, new ( 0, 1 ) ),
		];
		VertexBuffer = new( 4 );
		VertexBuffer.SetData( vertices.AsSpan() );

		//Index buffer
		int[] indices = { 0, 1, 2, 0, 2, 3 };
		IndexBuffer = new( 6, GpuBuffer.UsageFlags.Index );
		IndexBuffer.SetData( indices );
	}

	~SpriteBatchSceneObject()
	{
		if ( SpriteDataBufferRented )
		{
			ArrayPool<SpriteData>.Shared.Return( SpriteDataBuffer, clearArray: false );
		}
	}


	/// <summary>Resizes GPU buffers to the nearest power of 2</summary>
	public void ResizeBuffers()
	{
		int allocationSize = SplotCount + SpriteCount;
		if ( allocationSize <= CurrentBufferSize )
		{
			return;
		}

		ResizeBuffers( allocationSize );
	}

	//Resizes GPU buffers to accommodate the specified allocation size
	private void ResizeBuffers( int allocationSize )
	{
		CurrentBufferSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2( (uint)allocationSize );

		SpriteBuffer?.Dispose();
		SpriteBufferOut?.Dispose();
		GPUSortingBuffer?.Dispose();
		GPUDistanceBuffer?.Dispose();

		SpriteBuffer = new( CurrentBufferSize );
		SpriteBufferOut = new( CurrentBufferSize );
		GPUSortingBuffer = new( CurrentBufferSize );
		GPUDistanceBuffer = new( CurrentBufferSize );
	}

	private readonly Dictionary<Guid, int> _precomputedSplotCounts = [];

	//Pre-allocated buffer to avoid GC allocations in hot path
	private SpriteRenderer[] _componentBuffer = new SpriteRenderer[16];
	private readonly object _boundsLock = new();

	public void RegisterSprite( Guid ownerId, SpriteData[] sharedSprites, int offset, int count, int splotCount, BBox bounds )
	{
		SpriteGroups[ownerId] = new( sharedSprites, offset, count, bounds );
		_precomputedSplotCounts[ownerId] = splotCount;
		OnChanged();
	}

	public void RegisterSprite( Guid id, SpriteRenderer component )
	{
		Components[id] = component;
		OnChanged();
	}

	public void UnregisterSprite( Guid id )
	{
		Components.Remove( id );
		OnChanged();
	}

	public void UnregisterSpriteGroup( Guid ownerId )
	{
		if ( SpriteGroups.Remove( ownerId ) )
		{
			//Clean up precomputed splot count
			_precomputedSplotCounts.Remove( ownerId );

			OnChanged();
		}
	}

	public void UpdateSprite( Guid id, SpriteRenderer component )
	{
		if ( Components.ContainsKey( id ) )
		{
			Components[id] = component;
			OnChanged();
		}
	}

	public bool ContainsSprite( Guid id )
	{
		return Components.ContainsKey( id );
	}

	public void OnChanged()
	{
		//Clear cached splot count to force recalculation
		_splotCount = 0;

		GPUUploadQueued = true;
	}

	/// <summary>Copy host buffers onto GPU</summary>
	public void UploadOnHost()
	{
		if ( !GPUUploadQueued && !Sorted )
		{
			return;
		}

		int spriteCount = SpriteCount;
		int splotCount = SplotCount;
		int componentCount = Components.Count;

		//Resize GPU buffers here on the main thread, not in OnChanged, because RenderSceneObject may be using them concurrently on a render thread.
		int requiredSize = splotCount + spriteCount;
		if ( requiredSize > CurrentBufferSize )
		{
			ResizeBuffers( requiredSize );
		}

		//Staging buffer only needs to hold component sprites — particle groups are uploaded directly from SharedSprites in RenderSceneObject.
		if ( componentCount > 0 && (SpriteDataBuffer == null || SpriteDataBuffer.Length < componentCount) )
		{
			if ( SpriteDataBufferRented )
			{
				ArrayPool<SpriteData>.Shared.Return( SpriteDataBuffer, clearArray: false );
			}

			SpriteDataBuffer = ArrayPool<SpriteData>.Shared.Rent( componentCount );
			SpriteDataBufferRented = true;
		}

		var boundsMin = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
		var boundsMax = new Vector3( float.MinValue, float.MinValue, float.MinValue );

		//Upload sprites
		if ( componentCount > 0 )
		{
			//Use pre-allocated buffer to avoid GC allocation
			if ( _componentBuffer.Length < componentCount )
			{
				_componentBuffer = new SpriteRenderer[componentCount * 2];
			}

			int index = 0;
			foreach ( var component in Components.Values )
			{
				_componentBuffer[index++] = component;
			}

			var (mins, maxs) = SpriteBounds( componentCount );

			boundsMin = Vector3.Min( boundsMin, mins );
			boundsMax = Vector3.Max( boundsMax, maxs );
		}

		foreach ( var spriteGroup in SpriteGroups.Values )
		{
			boundsMin = Vector3.Min( boundsMin, spriteGroup.Bounds.Mins );
			boundsMax = Vector3.Max( boundsMax, spriteGroup.Bounds.Maxs );
		}

		//Use a degenerate zero-size bounds for empty batches so they can be frustum-culled.
		Bounds = spriteCount > 0 ? new BBox( boundsMin, boundsMax ) : default;

		_pendingSpriteCount = spriteCount;
		GPUUploadQueued = false;

		Upload( spriteCount, splotCount, componentCount );

		//A new frame's worth of views starts from the first of the buffers a sorted batch draws through - see RenderSceneObject.
		_viewsThisFrame = 0;
	}

	private static readonly (Vector3 mins, Vector3 maxs) NoBounds =
		(new Vector3( float.MaxValue, float.MaxValue, float.MaxValue ), new Vector3( float.MinValue, float.MinValue, float.MinValue ));

	//The component sprites into the staging buffer, and the box they fill. Its own method, and the work a method of its own too, so that the plain loop
	//captures nothing: as local functions over the caller's bounds they made two closures every upload, whichever way the sprites were then walked.
	private (Vector3 mins, Vector3 maxs) SpriteBounds( int componentCount )
	{
		//Parallel.For only pays off past a few thousand sprites; small batches use a plain loop
		if ( componentCount < ParallelSprites || Rinten.Utility.Parallel.InOrder )
		{
			var local = NoBounds;

			for ( var i = 0; i < componentCount; i++ ) local = One( i, local );

			return local;
		}

		return SpriteBoundsShared( componentCount );
	}

	//Apart from SpriteBounds, whose plain loop would otherwise make this closure too:
	//one is made where the variables it captures begin, which is the method's start.
	private (Vector3 mins, Vector3 maxs) SpriteBoundsShared( int componentCount )
	{
		var total = NoBounds;
		var boundsLock = _boundsLock;

		Parallel.For<(Vector3 mins, Vector3 maxs)>( 0, componentCount,
			() => NoBounds, ( i, _, local ) => One( i, local ), local =>
			{
				lock ( boundsLock )
					total = (Vector3.Min( total.mins, local.mins ), Vector3.Max( total.maxs, local.maxs ));
			} );

		return total;
	}

	//One sprite's transform into the staging buffer, and the box it grows the batch's bounds to.
	private (Vector3 mins, Vector3 maxs) One( int i, (Vector3 mins, Vector3 maxs) local )
	{
		var c = _componentBuffer[i];
		var transform = c.WorldTransform;
		var spriteSize = c.Size;
		var rotation = c.WorldRotation.Angles().AsVector3();

		if ( c.Billboard == SpriteRenderer.BillboardMode.Always || c.Billboard == SpriteRenderer.BillboardMode.YOnly )
		{
			//We only care about roll in this case
			rotation.x = 0;
			rotation.y = 0;
		}

		spriteSize = spriteSize.Abs();

		//Adjust for aspect ratio - the frame's, which is a cell of its picture when the picture is a sheet.
		var frame = c.CurrentFrame;
		var pixels = frame?.PixelSize ?? Vector2.One;
		var aspectRatio = pixels.x / MathF.Max( pixels.y, 1f );
		var size = spriteSize / 2f;
		var pos = transform.Position;
		//A sprite is a quad on the XY plane facing +Z: width along x, height along y, and nothing at all along the normal.
		var scale = new Vector3( transform.Scale.x * size.x, transform.Scale.y * size.y, transform.Scale.z );
		if ( aspectRatio < 1f )
			scale *= new Vector3( aspectRatio, 1f, 1f );
		else
			scale *= new Vector3( 1f, 1f / aspectRatio, 1f );

		pos = pos.RotateAround( transform.Position, transform.Rotation );
		transform = transform.WithScale( scale ).WithPosition( pos );

		var renderFlags = SpriteFlags.None;
		if ( c.FlipHorizontal ) renderFlags |= SpriteFlags.FlipX;
		if ( c.FlipVertical ) renderFlags |= SpriteFlags.FlipY;
		if ( c.TextureFilter == FilterMode.Point ) renderFlags |= SpriteFlags.Pixelated;

		var rgbe = c.Color.ToRgbe();
		var alpha = (byte)(c.Color.a.Clamp( 0.0f, 1.0f ) * 255.0f);
		var tintColor = new Color32( rgbe.r, rgbe.g, rgbe.b, alpha );

		var overlayRgbe = c.OverlayColor.ToRgbe();
		var overlayAlpha = (byte)(c.OverlayColor.a.Clamp( 0.0f, 1.0f ) * 255.0f);
		var overlayColor = new Color32( overlayRgbe.r, overlayRgbe.g, overlayRgbe.b, overlayAlpha );

		int lightingFlag = c.Lighting ? 1 : 0;
		uint packedExponent = (uint)(((byte)lightingFlag) | rgbe.a << 16);
		if ( c.Lit2D ) packedExponent |= SpriteData.Lit2D;
		if ( c.Occluder2D ) packedExponent |= SpriteData.Occluder2D;
		packedExponent |= SpriteData.Emission2DOf( c.Emission2D );

		uint packedFogAndAlpha = SpriteData.PackFogAndAlphaCutout( c.FogStrength, c.AlphaCutoff );

		var spritePos = transform.Position;
		var spriteScale = new Vector2( transform.Scale.x, transform.Scale.y );

		SpriteDataBuffer[i] = new SpriteData
		{
			Position = spritePos,
			Rotation = new( rotation.x, rotation.y, rotation.z ),
			Scale = spriteScale,
			TextureHandle = frame is null ? Texture.Transparent.SpriteIndex : ( frame.Texture ?? Texture.Invalid ).SpriteIndex,
			Region = frame?.RegionUv ?? new Vector4( 0, 0, 1, 1 ),
			TintColor = tintColor.RawInt,
			OverlayColor = overlayColor.RawInt,
			RenderFlags = (int)renderFlags,
			BillboardMode = (uint)c.Billboard,
			FogStrengthCutout = packedFogAndAlpha,
			Lighting = packedExponent,
			DepthFeather = c.DepthFeather,
			CameraFade = SpriteData.PackCameraFade( c.CameraFadeNear, c.CameraFadeFar ),
			Offset = c.Pivot
		};

		var pivot = c.Pivot;
		float halfSize = MathF.Max(
			MathF.Max( pivot.x, 1f - pivot.x ) * 2f * spriteScale.x,
			MathF.Max( pivot.y, 1f - pivot.y ) * 2f * spriteScale.y
		);
		var expand = new Vector3( halfSize, halfSize, halfSize );
		return (Vector3.Min( local.mins, spritePos - expand ), Vector3.Max( local.maxs, spritePos + expand ));
	}

	//Builds all sprite data into one buffer and uploads it in one call; also expands motion blur copies
	private void Upload( int spriteCount, int splotCount, int componentCount )
	{
		_renderInstanceCount = 0;

		if ( spriteCount <= 0 ) return;

		var room = spriteCount + splotCount;

		if ( _uploadBuffer is null || _uploadBuffer.Length < room )
		{
			if ( _uploadBuffer is not null )
				ArrayPool<SpriteData>.Shared.Return( _uploadBuffer, clearArray: false );

			_uploadBuffer = ArrayPool<SpriteData>.Shared.Rent( room );
		}

		var at = 0;

		if ( componentCount > 0 && SpriteDataBuffer is not null )
		{
			Array.Copy( SpriteDataBuffer, 0, _uploadBuffer, 0, componentCount );
			Additively( 0, componentCount );
			at = componentCount;
		}

		//Where each group's run goes, worked out first so the copies can be made at once. A sprite is a hundred and thirty-six bytes, so a scene
		//with sixteen thousand particles in it gathers two megabytes here, and one thread does not fill this machine's bus - see ParallelGather.
		_gather.Clear();

		foreach ( var group in SpriteGroups.Values )
		{
			if ( group.Count <= 0 || group.SharedSprites is null ) continue;
			if ( at + group.Count > spriteCount ) continue;

			_gather.Add( new Run( group.SharedSprites, group.Offset, at, group.Count ) );

			at += group.Count;
		}

		if ( at - componentCount >= ParallelGather && _gather.Count > 1 && !Rinten.Utility.Parallel.InOrder )
		{
			Parallel.For( 0, _gather.Count, gatherOne ??= GatherOne );
		}
		else
		{
			for ( var i = 0; i < _gather.Count; i++ ) GatherOne( i );
		}

		if ( at <= 0 ) return;

		var sprites = at;

		for ( var i = 0; i < sprites && at < room; i++ )
		{
			at = Splot( _uploadBuffer, i, at, room );
		}

		//The blurs are copies of sprites that already carry it, but a splot may be built rather than copied, and the flag costs nothing to set twice.
		Additively( sprites, at - sprites );

		_renderInstanceCount = at;

		//A sorted batch is put on the device once per view instead, in the order that view wants - see
		//RenderSceneObject. And the portable frame puts none on it at all: its runs go with the draw - see DrawRuns.
		if ( Sorted || CoreRenderDevice.PortableFrame ) return;

		SpriteBuffer.SetData( _uploadBuffer.AsSpan( 0, at ) );
	}

	//One group's sprites, and where in the upload buffer they go.
	private readonly record struct Run( SpriteData[] From, int At, int Into, int Count );

	private readonly List<Run> _gather = new();
	private Action<int> gatherOne;

	//How many sprites are worth sharing the gather out over threads. A fork costs about two and a half microseconds on this machine, and a sprite
	//is a hundred and thirty-six bytes: four thousand of them is half a megabyte of copying, tens of microseconds, and well past the line.
	private const int ParallelGather = 4096;

	private void GatherOne( int i )
	{
		var run = _gather[i];

		Array.Copy( run.From, run.At, _uploadBuffer, run.Into, run.Count );

		//While the run is still in the cache. The flag used to be written over the whole buffer in a pass of its own, which on a scene with
		//sixteen thousand particles in it read and wrote two megabytes to set one integer in every hundred and thirty-six bytes of it.
		Additively( run.Into, run.Count );
	}

	//Marks a run of the upload buffer as additive, where this batch is.
	private void Additively( int from, int count )
	{
		if ( !Additive || count <= 0 ) return;

		var run = _uploadBuffer.AsSpan( from, count );

		for ( var i = 0; i < run.Length; i++ ) run[i].RenderFlags |= (int)SpriteFlags.Additive;
	}

	//Writes motion blur copies of one sprite along its velocity, fading out; returns the next index
	private static int Splot( SpriteData[] into, int index, int at, int room )
	{
		ref readonly var sprite = ref into[index];

		var splots = sprite.Splots;
		if ( splots <= 0 ) return at;

		var velocity = sprite.Velocity;
		var speed = velocity.Length;
		if ( speed <= 0.0001f ) return at;

		var leading = sprite.MotionBlur.x > 1.5f;
		var spacing = sprite.MotionBlur.z;
		var opacity = sprite.MotionBlur.w;

		var width = MathF.Max( sprite.Scale.x, sprite.Scale.y ) * 2.0f;
		var step = velocity * ( ( spacing * width ) / speed );

		var alpha = ( sprite.TintColor >> 24 ) & 0xFF;

		for ( var k = 1; k <= splots; k++ )
		{
			var fade = opacity * ( 1.0f - ( k / (float)( splots + 1 ) ) );
			var faded = ( sprite.TintColor & 0x00FFFFFFu ) | ( (uint)( alpha * fade ) << 24 );

			var copy = sprite;
			copy.Splots = 0;
			copy.TintColor = faded;

			if ( at >= room ) break;

			copy.Position = sprite.Position - ( step * k );
			into[at++] = copy;

			if ( !leading || at >= room ) continue;

			copy.Position = sprite.Position + ( step * k );
			into[at++] = copy;
		}

		return at;
	}

	private SpriteData[] _uploadBuffer;

	private const int GroupSize = 256;
	private const int MaxDimGroups = 1024;
	private const int MaxDimThreads = GroupSize * MaxDimGroups;

	/// <summary>Draws the batch as instanced quads; translucent ones sorted far-to-near on CPU, buffer per view</summary>
	public override void RenderSceneObject()
	{
		base.RenderSceneObject();

		if ( _renderInstanceCount <= 0 ) return;
		if ( !IndexBuffer.IsValid() ) return;
		if ( DrawnWith is null ) return;

		if ( CoreRenderDevice.PortableFrame )
		{
			DrawRuns( Sorted ? SortForThisView() : _uploadBuffer, _renderInstanceCount );
			return;
		}

		var buffer = Sorted ? SortedForThisView() : SpriteBuffer;
		if ( buffer is null || !buffer.IsValid() ) return;

		var attributes = RenderAttributes.Pool.Get();

		try
		{
			//How it is composited is pipeline state rather than something the shader compiles twice
			//- see Graphics.DrawIndexedInstanced, which keeps a pipeline per answer to these two.
			attributes.SetCombo( "D_BLEND", Additive ? 1 : 0 );
			attributes.SetCombo( "D_OPAQUE", Opaque ? 1 : 0 );

			attributes.Set( "Sprites", (GpuBuffer)buffer );

			Graphics.DrawIndexedInstanced( (GpuBuffer)IndexBuffer, DrawnWith, _renderInstanceCount, attributes );
		}
		finally
		{
			RenderAttributes.Pool.Return( attributes );
		}
	}

	//Portable frame path: one draw per run of sprites sharing a texture, keeping sort order
	private void DrawRuns( SpriteData[] sprites, int count )
	{
		if ( sprites is null || count <= 0 ) return;

		var size = System.Runtime.CompilerServices.Unsafe.SizeOf<SpriteData>();
		var perRun = Graphics.InstancesPerRun( size );
		var bytes = MemoryMarshal.AsBytes( sprites.AsSpan( 0, count ) );

		var attributes = RenderAttributes.Pool.Get();

		try
		{
			attributes.SetCombo( "D_BLEND", Additive ? 1 : 0 );
			attributes.SetCombo( "D_OPAQUE", Opaque ? 1 : 0 );

			for ( var start = 0; start < count; )
			{
				var picture = sprites[start].TextureHandle;
				var end = start + 1;

				while ( end < count && end - start < perRun && sprites[end].TextureHandle == picture ) end++;

				Graphics.DrawIndexedInstancedRun( (GpuBuffer)IndexBuffer, DrawnWith, end - start,
					bytes.Slice( start * size, ( end - start ) * size ),
					CoreRender.PortableTexture( picture ), attributes );

				start = end;
			}
		}
		finally
		{
			RenderAttributes.Pool.Return( attributes );
		}
	}

	//The sprites in the order the view being gathered wants them: far to near.
	private SpriteData[] SortForThisView()
	{
		var count = _renderInstanceCount;

		if ( _sortKeys.Length < count )
		{
			_sortKeys = new float[Math.Max( count, _sortKeys.Length * 2 )];
			_sortOrder = new int[_sortKeys.Length];
			_sortScratch = new SpriteData[_sortKeys.Length];
		}

		//Sort by depth along the view direction, not distance to the eye, so orthographic order is right
		var eye = Graphics.CameraPosition;
		var forward = Graphics.CameraRotation.Forward;

		for ( var i = 0; i < count; i++ )
		{
			_sortKeys[i] = -Vector3.Dot( _uploadBuffer[i].Position - eye, forward );
			_sortOrder[i] = i;
		}

		//What is sorted is where each sprite is, not the sprite. A sprite is a hundred and thirty-six bytes and a
		//sort moves its items n log n times; the order is four, and the sprites are then laid out once, in order.
		Array.Sort( _sortKeys, _sortOrder, 0, count );

		for ( var i = 0; i < count; i++ )
		{
			_sortScratch[i] = _uploadBuffer[_sortOrder[i]];
		}

		return _sortScratch;
	}

	//The sprites in the order the view being gathered wants them, on the device - see RenderSceneObject for why there is one of these per view.
	private GpuBuffer<SpriteData> SortedForThisView()
	{
		var count = _renderInstanceCount;

		SortForThisView();

		var slot = _viewsThisFrame++;

		while ( _viewBuffers.Count <= slot )
			_viewBuffers.Add( null );

		var buffer = _viewBuffers[slot];

		if ( buffer is null || buffer.ElementCount < count )
		{
			buffer?.Dispose();
			buffer = new GpuBuffer<SpriteData>( (int)System.Numerics.BitOperations.RoundUpToPowerOf2( (uint)Math.Max( count, DefaultBufferSize ) ) );
			_viewBuffers[slot] = buffer;
		}

		buffer.SetData( _sortScratch.AsSpan( 0, count ) );

		return buffer;
	}

	private float[] _sortKeys = [];
	private int[] _sortOrder = [];
	private SpriteData[] _sortScratch = [];

	//One buffer for each view a frame has drawn this batch in so far.
	private readonly List<GpuBuffer<SpriteData>> _viewBuffers = [];
	private int _viewsThisFrame;
}
