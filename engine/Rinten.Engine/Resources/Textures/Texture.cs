using NativeEngine;

namespace Rinten;

/// <summary>A texture is an image used in rendering. Can be a static texture loaded from disk, or a dynamic texture rendered to by code. Can also be 2D, 3D (multiple slices), or a cube texture (6 slices).</summary>
[SkipHotload]
[ResourceType( "tex" )]
public partial class Texture : Resource, IDisposable
{


	//The pixels, where this texture is one of ours. Null for a texture that came from the legacy render device, which is every
	//texture while that is loaded. One or the other, never both: what made the texture decides which it is, and everything below asks this first.
	internal CoreTexture core;

	private CTextureDesc _desc;
	bool gotdesc;

	//Allow the texture to keep a reference to its parent object (like a videoplayer).
	internal object ParentObject;

	//Set when used by a material or attributes; such a video is assumed on screen and not paused
	internal bool UsedOutsidePanels;

	//Bumped when the picture is swapped (CopyFrom), so the size may have changed. A version, not a flag - one texture can
	//be shared by many panels, and a flag is consumed by whichever one happens to rebuild first, leaving the rest stale.
	internal int DirtyVersion;

	//A texture with no core is invalid, not an exception; this is queried from loader threads
	/// <summary>Whether this texture is an error or invalid or not.</summary>
	public override bool IsError => core?.Data is null;

	public override bool IsValid => core?.Data is not null;

	/// <summary>Flags providing hints about this texture</summary>
	public TextureFlags Flags { get; set; } = TextureFlags.None;

	//One of ours. Made by Create and by everything that builds a texture out of pixels it already has.
	internal Texture( CoreTexture core )
	{
		this.core = core ?? throw new Exception( "Texture cannot be null!" );

		_desc = core.Desc;
		gotdesc = true;
	}

	~Texture()
	{
		using var finalizing = MainThread.InFinalizer();
		Destroy();
	}

	//Zero when not in the bindless table; slot zero is left empty so an unset index draws nothing
	/// <summary>Where this picture sits in the table every shader can index.</summary>
	public int Index => core is null ? 0 : (int)CoreRender.TextureIndex( core.Device() );

	//The number a sprite names this picture by: its place in the table, or in the portable frame - which has no table
	//- a number of its own, which the sprite batch turns back into the picture to bind. See CoreRender.PortableSlot.
	internal int SpriteIndex => core is null ? 0
		: CoreRenderDevice.PortableFrame ? (int)CoreRender.PortableSlot( core.Device() )
		: Index;

	//Makes this texture become the passed one; take moves ownership so the picture isn't freed twice
	internal bool CopyFrom( Texture texture, bool take = true )
	{
		if ( !texture.IsValid() )
			return false;

		//One of ours becomes the other one by taking its pixels.
		var previous = core;

		core = texture.core;

		//And its shape with it. The shape is remembered the first time it is asked for, so without this a placeholder that
		//took a picture of another size went on answering with its own - a video's texture said 1x1 for as long as it played.
		_desc = core.Desc;
		gotdesc = true;

		DirtyVersion++;

		//Take ownership so two textures never both free the same picture
		if ( take ) texture.core = null;

		//Release the old device picture on the main thread, or the descriptor pool runs out
		if ( previous is not null && !ReferenceEquals( previous, core ) )
			MainThread.Queue( previous.Release );

		return take;
	}

	internal CTextureDesc Desc
	{
		get
		{
			if ( gotdesc ) return _desc;

			gotdesc = true;
			_desc = core.Desc;
			return _desc;
		}
	}

	/// <summary>Width of the texture in pixels.</summary>
	public int Width => Desc.m_nWidth;

	/// <summary>Height of the texture in pixels.</summary>
	public int Height => Desc.m_nHeight;

	/// <summary>Depth of a 3D texture in pixels, or slice count for 2D texture arrays, or 6 for slices of cubemap.</summary>
	public int Depth => Desc.IsCube ? 6 : Desc.m_nDepth;

	/// <summary>Number of <a href="https://en.wikipedia.org/wiki/Mipmap">mip maps</a> this texture has.</summary>
	public int Mips => Desc.m_nNumMipLevels;

	/// <summary>Returns a Vector2 representing the size of the texture (width, height)</summary>
	public Vector2 Size => new( Width, Height );

	/// <summary>Whether this texture has finished loading or not.</summary>
	public bool IsLoaded { get; internal set; } = true;

	/// <summary>True if this is a multi-frame animated image (GIF, animated WebP) driven by <see cref="Tick"/>.</summary>
	public bool IsAnimated { get; internal set; }

	/// <summary>Image format of this texture.</summary>
	public ImageFormat ImageFormat => Desc.m_nImageFormat;

	//Ours are not streamed - the pixels are in memory and stay there - so one of ours was used this frame by definition.
	/// <summary>Returns how many frames ago this texture was last used by the renderer</summary>
	public int LastUsed => 0;

	/// <summary>Gets if the texture has UAV access</summary>
	public bool UAVAccess => ( ( Desc.m_nFlags & RuntimeTextureSpecificationFlags.TSPEC_UAV ) == RuntimeTextureSpecificationFlags.TSPEC_UAV );

	internal RenderMultisampleType MultisampleType
	{
		get
		{
			//Not tracked: always the default
			return default;
		}
	}

	internal override void Destroy()
	{
		var c = core;
		if ( c is null ) return;

		core = null;

		//Freeing the picture is the device's, so it happens where the device is - and a finaliser is on whatever thread the runtime chose.
		MainThread.Queue( c.Release );
	}

	/// <summary>Releases the handle now instead of waiting for the finalizer</summary>
	public void Dispose()
	{
		Destroy();
	}

	internal void TryReload( BaseFileSystem filesystem, string filename )
	{
		//Try to load the texture again, make a new texture
		var newTex = TryToLoad( filesystem, filename, false );

		//FromNative can return this same cached wrapper - nothing to copy.
		if ( newTex is null || ReferenceEquals( newTex, this ) )
			return;

		//If success, copy from this texture, always releasing the temporary handle. Freed only where it was not
		//taken: taking it moves the picture, and freeing it afterwards would destroy the one this texture now draws.
		if ( !CopyFrom( newTex ) )
			newTex.Dispose();
	}

	/// <summary>If this texture is a sprite sheet, will return information about the sheet, which is generally used in the shader. You don't really need to think about the contents.</summary>
	public Vector4 SequenceData
	{
		get
		{
			//Not tracked: always the default
			return default;
		}
	}

	/// <summary>The count of sequences in this texture, if any. The rest of the sequence data is encoded into the texture itself.</summary>
	public int SequenceCount { get; private set; }

	class SequenceInfo
	{
		public float Length { get; set; }
		public int FrameCount { get; internal set; }
		public bool Looped { get; internal set; }
	}

	SequenceInfo[] sequences;

	/// <summary>Get the frame count for this sequence</summary>
	public int GetSequenceFrameCount( int sequenceId )
	{
		if ( sequences is null ) return 0;
		if ( sequenceId < 0 ) return 0;
		if ( sequenceId >= sequences.Length ) return 0;

		return sequences[sequenceId].FrameCount;
	}

	//Get the total length of this seqence
	private float GetSequenceLength( int sequenceId )
	{
		if ( sequences is null ) return 0;
		if ( sequenceId < 0 ) return 0;
		if ( sequenceId >= sequences.Length ) return 0;

		return sequences[sequenceId].Length;
	}

	//TODO: Fill this out, build a structure of Sequence[] for people to access Then make it so we can actually preview them
	private void UpdateSheetInfo()
	{
		HasAnimatedSequences = false;

		//Sheets are a thing of theirs: a texture asset of ours has no sequences in it, and one built from pixels never did.
		return;
	}

	public bool HasAnimatedSequences { get; private set; }

	/// <summary>Tells texture streaming this texture is being used. This is usually automatic, but useful for bindless pipelines.</summary>
	public void MarkUsed( int requiredMipSize = 0 )
	{
		return;
	}

	internal bool IsRenderTarget => ( ( core.Desc.m_nFlags & RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET ) == RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET );

	//Mark this texture as loading, create a texture in a task, replace this texture with it, mark it as loaded.
	//This is for situations where you create a placeholder texture then replace it with the real texture later.
	internal async Task ReplacementAsync( Task<Texture> task )
	{
		IsLoaded = false;

		//Wait for the new texture to exist
		var texture = await task;

		//replace us with the new texture
		if ( texture is not null && texture != this )
		{
			var taken = this.CopyFrom( texture );

			//update any animation instance (eg for gifs) to point to this as well
			if ( Animations.FirstOrDefault( x => x.Texture.TryGetTarget( out var t ) && ReferenceEquals( t, texture ) ) is { } animation )
			{
				animation.Texture.SetTarget( this );
			}

			//As in TryReload: only what was not taken.
			if ( !taken ) texture.Dispose();
		}

		IsLoaded = true;
	}
}

/// <summary>Flags providing hints about a texture</summary>
public enum TextureFlags
{
	None = 0,

	/// <summary>Hint that this texture has pre-multiplied alpha</summary>
	PremultipliedAlpha = 1 << 0,
}
