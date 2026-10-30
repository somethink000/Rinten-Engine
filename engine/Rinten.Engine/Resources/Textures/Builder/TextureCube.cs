using NativeEngine;

namespace Rinten;

public struct TextureCubeBuilder
{
	internal TextureBuilder builder = new();
	internal int Width { set => builder._config.m_nWidth = (short)value; }
	internal int Height { set => builder._config.m_nHeight = (short)value; }
	internal int Depth { set => builder._config.m_nDepth = (short)value; }
	internal ImageFormat Format { set => builder._config.m_nImageFormat = value; }


	internal string _name = null;
	internal byte[] _data = null;
	internal int _dataLength = 0;
	internal IntPtr _dataPtr = IntPtr.Zero;
	internal bool _asAnonymous = true;

	internal bool HasData
	{
		get
		{
			return (_data != null || _dataPtr != IntPtr.Zero) && _dataLength > 0;
		}
	}

	public TextureCubeBuilder()
	{
		builder._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_CUBE_TEXTURE;
		builder._config.m_nDepth = 0;
	}


	#region Common methods

	public TextureCubeBuilder WithStaticUsage()
	{
		builder.WithStaticUsage();
		return this;
	}

	public TextureCubeBuilder WithSemiStaticUsage()
	{
		builder.WithSemiStaticUsage();
		return this;
	}

	public TextureCubeBuilder WithDynamicUsage()
	{
		builder.WithDynamicUsage();
		return this;
	}

	public TextureCubeBuilder WithGPUOnlyUsage()
	{
		builder.WithGPUOnlyUsage();
		return this;
	}

	public TextureCubeBuilder WithUAVBinding()
	{
		builder.WithUAVBinding();
		return this;
	}

	public TextureCubeBuilder WithMips( int mips )
	{
		builder.WithMips( mips );
		return this;
	}

	public TextureCubeBuilder WithFormat( ImageFormat format )
	{
		builder.WithFormat( format );
		return this;
	}

	public TextureCubeBuilder WithScreenFormat()
	{
		builder.WithScreenFormat();
		return this;
	}

	public TextureCubeBuilder WithDepthFormat()
	{
		builder.WithDepthFormat();
		return this;
	}

	public TextureCubeBuilder WithMultiSample2X()
	{
		return WithMultisample( MultisampleAmount.Multisample2x );
	}

	public TextureCubeBuilder WithMultiSample4X()
	{
		return WithMultisample( MultisampleAmount.Multisample4x );
	}

	public TextureCubeBuilder WithMultiSample6X()
	{
		return WithMultisample( MultisampleAmount.Multisample6x );
	}

	public TextureCubeBuilder WithMultiSample8X()
	{
		return WithMultisample( MultisampleAmount.Multisample8x );
	}

	public TextureCubeBuilder WithMultiSample16X()
	{
		return WithMultisample( MultisampleAmount.Multisample16x );
	}

	public TextureCubeBuilder WithScreenMultiSample()
	{
		return WithMultisample( MultisampleAmount.MultisampleScreen );
	}

	#endregion

	/// <summary>Provide a name to identify the texture by</summary>
	public TextureCubeBuilder WithName( string name )
	{
		_name = name;
		return this;
	}


	/// <summary>Initialize texture with pre-existing texture data</summary>
	public TextureCubeBuilder WithData( byte[] data )
	{
		return WithData( data, data.Length );
	}

	/// <summary>Initialize texture with pre-existing texture data</summary>
	public TextureCubeBuilder WithData( byte[] data, int dataLength )
	{
		if ( dataLength > data.Length )
		{
			throw new System.Exception( "Data length exceeds the data" );
		}
		if ( dataLength < 0 )
		{
			throw new System.Exception( "Data length is less than zero" );
		}

		_data = data;
		_dataLength = dataLength;
		return this;
	}

	//Create a texture with data using an UNSAFE intptr
	internal TextureCubeBuilder WithData( IntPtr data, int dataLength )
	{
		_dataPtr = data;
		_dataLength = dataLength;
		return this;
	}

	/// <summary>Define which how much multisampling the current texture should use</summary>
	public TextureCubeBuilder WithMultisample( MultisampleAmount amount )
	{
		builder.WithMSAA( amount );
		return this;
	}

	/// <summary>Set whether the texture is an anonymous texture or not</summary>
	public TextureCubeBuilder WithAnonymous( bool isAnonymous )
	{
		_asAnonymous = isAnonymous;
		return this;
	}

	public TextureCubeBuilder WithArrayCount( int count )
	{
		builder._config.m_nDepth = (short)count;
		return this;
	}

	/// <summary>Build and create the actual texture</summary>
	public Texture Finish()
	{

		if ( builder._config.m_nDepth > 1 )
		{
			builder._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_TEXTURE_ARRAY;
		}

		builder._config.m_nNumMipLevels = Math.Max( builder._config.m_nNumMipLevels, (short)1 );
		builder._config.m_nWidth = Math.Max( builder._config.m_nWidth, (short)1 );
		builder._config.m_nHeight = Math.Max( builder._config.m_nHeight, (short)1 );
		builder._config.m_nDepth = Math.Max( builder._config.m_nDepth, (short)1 );

		//If UAV, imply we want to access it as an array for compute shaders too
		if ( (builder._config.m_nFlags & RuntimeTextureSpecificationFlags.TSPEC_UAV) != 0 )
		{
			builder._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_CUBE_CAN_SAMPLE_AS_ARRAY;
		}

		if ( builder._config.m_nImageFormat == ImageFormat.Default )
			builder._config.m_nImageFormat = ImageFormat.RGBA8888;

		if ( HasData )
		{
			int memoryRequiredForTexture = ImageFormatSize.Total( builder._config.m_nWidth, builder._config.m_nHeight, builder._config.m_nDepth, builder._config.m_nNumMipLevels, builder._config.m_nImageFormat ) * 6;

			if ( _dataLength < memoryRequiredForTexture )
			{
				throw new Exception( $"{_dataLength} is not enough data to create texture! {memoryRequiredForTexture} bytes are required! You're missing {_dataLength - memoryRequiredForTexture} bytes!" );
			}
			//We're passing excessive data in places which we need to fix up, commenting this
			//out for now because it breaks a bunch of shit like thumbnails in the main menu
			/*
			else if ( _dataLength > memoryRequiredForTexture )
			{
				throw new Exception( $"{_dataLength} is too much data to create texture! {memoryRequiredForTexture} bytes are required! You have {_dataLength - memoryRequiredForTexture} extra bytes! Remove them" );
			}*/
		}

		if ( _dataPtr != IntPtr.Zero )
		{
			return Texture.Create( _name, _asAnonymous, builder, _dataPtr, _dataLength );
		}

		return builder.Create( _name, _asAnonymous, _data, _dataLength );
	}

	//Custom methods

	/// <summary>Create texture with a predefined size</summary>
	public TextureCubeBuilder WithSize( int width, int height )
	{
		Width = width;
		Height = height;
		return this;
	}

	/// <summary>Create texture with a predefined size</summary>
	public TextureCubeBuilder WithSize( Vector2 size )
	{
		Width = size.x.CeilToInt();
		Height = size.y.CeilToInt();
		return this;
	}

	public TextureCubeBuilder AsRenderTarget()
	{
		builder._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET;
		builder._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_RENDER_TARGET_SAMPLEABLE;

		builder._config.m_nUsage = TextureUsage.TEXTURE_USAGE_GPU_ONLY;
		builder._config.m_nScope = TextureScope.TEXTURE_SCOPE_GLOBAL;
		return this;
	}
}
