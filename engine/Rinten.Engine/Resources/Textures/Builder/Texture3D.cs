using NativeEngine;
using System;

namespace Rinten
{
	public struct Texture3DBuilder
	{
		internal TextureBuilder config = new();
		internal int Width { set => config._config.m_nWidth = (short)value; }
		internal int Height { set => config._config.m_nHeight = (short)value; }
		internal int Depth { set => config._config.m_nDepth = (short)value; }
		internal ImageFormat Format { set => config._config.m_nImageFormat = value; }


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

		public Texture3DBuilder()
		{
			config._config.m_nFlags |= RuntimeTextureSpecificationFlags.TSPEC_VOLUME_TEXTURE;
			config._config.m_nNumMipLevels = 1;
		}

		#region Common methods

		public Texture3DBuilder WithStaticUsage()
		{
			config.WithStaticUsage();
			return this;
		}

		public Texture3DBuilder WithSemiStaticUsage()
		{
			config.WithStaticUsage();
			return this;
		}

		public Texture3DBuilder WithDynamicUsage()
		{
			config.WithDynamicUsage();
			return this;
		}

		public Texture3DBuilder WithGPUOnlyUsage()
		{
			config.WithGPUOnlyUsage();
			return this;
		}

		public Texture3DBuilder WithUAVBinding()
		{
			config.WithUAVBinding();
			return this;
		}

		public Texture3DBuilder WithMips( int mips )
		{
			config.WithMips( mips );
			return this;
		}

		public Texture3DBuilder WithFormat( ImageFormat format )
		{
			config.WithFormat( format );
			return this;
		}

		public Texture3DBuilder WithScreenFormat()
		{
			config.WithScreenFormat();
			return this;
		}

		public Texture3DBuilder WithDepthFormat()
		{
			config.WithDepthFormat();
			return this;
		}

		public Texture3DBuilder WithMultiSample2X()
		{
			return WithMultisample( MultisampleAmount.Multisample2x );
		}

		public Texture3DBuilder WithMultiSample4X()
		{
			return WithMultisample( MultisampleAmount.Multisample4x );
		}

		public Texture3DBuilder WithMultiSample6X()
		{
			return WithMultisample( MultisampleAmount.Multisample6x );
		}

		public Texture3DBuilder WithMultiSample8X()
		{
			return WithMultisample( MultisampleAmount.Multisample8x );
		}

		public Texture3DBuilder WithMultiSample16X()
		{
			return WithMultisample( MultisampleAmount.Multisample16x );
		}

		public Texture3DBuilder WithScreenMultiSample()
		{
			return WithMultisample( MultisampleAmount.MultisampleScreen );
		}

		#endregion

		/// <summary>Provide a name to identify the texture by</summary>
		public Texture3DBuilder WithName( string name )
		{
			_name = name;
			return this;
		}

		/// <summary>Initialize texture with pre-existing texture data</summary>
		public Texture3DBuilder WithData( byte[] data )
		{
			return WithData( data, data.Length );
		}

		/// <summary>Initialize texture with pre-existing texture data</summary>
		public Texture3DBuilder WithData( byte[] data, int dataLength )
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
		internal Texture3DBuilder WithData( IntPtr data, int dataLength )
		{
			_dataPtr = data;
			_dataLength = dataLength;
			return this;
		}

		/// <summary>Define which how much multisampling the current texture should use</summary>
		public Texture3DBuilder WithMultisample( MultisampleAmount amount )
		{
			config.WithMSAA( amount );
			return this;
		}

		/// <summary>Set whether the texture is an anonymous texture or not</summary>
		public Texture3DBuilder WithAnonymous( bool isAnonymous )
		{
			_asAnonymous = isAnonymous;
			return this;
		}

		/// <summary>Build and create the actual texture</summary>
		public Texture Finish()
		{
			config._config.m_nNumMipLevels = Math.Max( config._config.m_nNumMipLevels, (short)1 );
			config._config.m_nWidth = Math.Max( config._config.m_nWidth, (short)1 );
			config._config.m_nHeight = Math.Max( config._config.m_nHeight, (short)1 );
			config._config.m_nDepth = Math.Max( config._config.m_nDepth, (short)1 );

			if ( config._config.m_nImageFormat == ImageFormat.Default )
				config._config.m_nImageFormat = ImageFormat.RGBA8888;

			if ( HasData )
			{
				int memoryRequiredForTexture = ImageFormatSize.Total( config._config.m_nWidth, config._config.m_nHeight, config._config.m_nDepth, 1, config._config.m_nImageFormat );
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
				return Texture.Create( _name, _asAnonymous, config, _dataPtr, _dataLength );
			}

			return config.Create( _name, _asAnonymous, _data, _dataLength );
		}


		//Custom methods

		/// <summary>Create texture with a predefined size</summary>
		public Texture3DBuilder WithSize( int width = 1, int height = 1, int depth = 1 )
		{
			Width = width;
			Height = height;
			Depth = depth;
			return this;
		}

		/// <summary>Create texture with a predefined size</summary>
		public Texture3DBuilder WithSize( Vector3 size )
		{
			Width = size.x.CeilToInt();
			Height = size.y.CeilToInt();
			Depth = size.z.CeilToInt();
			return this;
		}
	}
}
