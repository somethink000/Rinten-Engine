using NativeEngine;
using System.Runtime.InteropServices;

namespace Rinten
{
	public struct Texture2DBuilder
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

		public Texture2DBuilder()
		{
			config._config.m_nDepth = 1;
		}

		#region Common methods

		public Texture2DBuilder WithStaticUsage()
		{
			config.WithStaticUsage();
			return this;
		}

		public Texture2DBuilder WithSemiStaticUsage()
		{
			config.WithStaticUsage();
			return this;
		}

		public Texture2DBuilder WithDynamicUsage()
		{
			config.WithDynamicUsage();
			return this;
		}

		public Texture2DBuilder WithGPUOnlyUsage()
		{
			config.WithGPUOnlyUsage();
			return this;
		}

		public Texture2DBuilder WithUAVBinding()
		{
			config.WithUAVBinding();
			return this;
		}

		public Texture2DBuilder WithMips()
		{
			int numMips = (int)Math.Log2( Math.Min( config._config.m_nWidth, config._config.m_nHeight ) ) + 1;
			config.WithMips( numMips );
			return this;
		}

		public Texture2DBuilder WithMips( int mips )
		{
			config.WithMips( mips );
			return this;
		}

		public Texture2DBuilder WithFormat( ImageFormat format )
		{
			config.WithFormat( format );
			return this;
		}

		public Texture2DBuilder WithScreenFormat()
		{
			config.WithScreenFormat();
			return this;
		}

		public Texture2DBuilder WithDepthFormat()
		{
			config.WithDepthFormat();
			return this;
		}

		public Texture2DBuilder WithMultiSample2X()
		{
			return WithMultisample( MultisampleAmount.Multisample2x );
		}

		public Texture2DBuilder WithMultiSample4X()
		{
			return WithMultisample( MultisampleAmount.Multisample4x );
		}

		public Texture2DBuilder WithMultiSample6X()
		{
			return WithMultisample( MultisampleAmount.Multisample6x );
		}

		public Texture2DBuilder WithMultiSample8X()
		{
			return WithMultisample( MultisampleAmount.Multisample8x );
		}

		public Texture2DBuilder WithMultiSample16X()
		{
			return WithMultisample( MultisampleAmount.Multisample16x );
		}

		public Texture2DBuilder WithScreenMultiSample()
		{
			return WithMultisample( MultisampleAmount.MultisampleScreen );
		}

		#endregion


		/// <summary>Provide a name to identify the texture by</summary>
		public Texture2DBuilder WithName( string name )
		{
			_name = name;
			return this;
		}

		public Texture2DBuilder WithData( byte[] data )
		{
			return WithData( data, data.Length );
		}

		/// <summary>Initialize texture with pre-existing texture data.</summary>
		public Texture2DBuilder WithData( byte[] data, int dataLength )
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

		/// <summary>Initialize texture with pre-existing texture data.</summary>
		public Texture2DBuilder WithData<T>( ReadOnlySpan<T> data ) where T : struct
		{
			ReadOnlySpan<byte> byteSpan = MemoryMarshal.Cast<T, byte>( data );

			_data = byteSpan.ToArray();
			_dataLength = byteSpan.Length;
			return this;
		}

		//Create a texture with data using an UNSAFE intptr
		internal Texture2DBuilder WithData( IntPtr data, int dataLength )
		{
			_dataPtr = data;
			_dataLength = dataLength;
			return this;
		}

		/// <summary>Use Multi-Sample Anti Aliasing (MSAA) of given sample count.</summary>
		public Texture2DBuilder WithMultisample( MultisampleAmount amount )
		{
			config.WithMSAA( amount );
			return this;
		}

		/// <summary>Set whether the texture is an anonymous texture or not</summary>
		public Texture2DBuilder WithAnonymous( bool isAnonymous )
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
			config._config.m_nDepth = 1;

			if ( config._config.m_nImageFormat == ImageFormat.Default )
				config._config.m_nImageFormat = ImageFormat.RGBA8888;

			if ( HasData )
			{
				int memoryRequiredForTextureWithMips = ImageFormatSize.Total( config._config.m_nWidth, config._config.m_nHeight, config._config.m_nDepth, (config._config.m_nNumMipLevels == 0) ? 1 : config._config.m_nNumMipLevels, config._config.m_nImageFormat );
				int memoryRequiredForTexture = ImageFormatSize.Total( config._config.m_nWidth, config._config.m_nHeight, config._config.m_nDepth, 1, config._config.m_nImageFormat );

				if ( _dataLength != memoryRequiredForTexture && _dataLength != memoryRequiredForTextureWithMips )
				{
					throw new Exception( $"{_dataLength} is wrong for this texture! {memoryRequiredForTexture:n0} bytes are required (or {memoryRequiredForTextureWithMips:n0} with mips)! You sent {_dataLength:n0} bytes!" );
				}
			}

			if ( _dataPtr != IntPtr.Zero )
			{
				return Texture.Create( _name, _asAnonymous, config, _dataPtr, _dataLength );
			}

			return config.Create( _name, _asAnonymous, _data, _dataLength );
		}

		//Custom methods


		/// <summary>Create texture with a predefined size.</summary>
		public Texture2DBuilder WithSize( int width, int height )
		{
			Width = width;
			Height = height;
			return this;
		}

		/// <summary>Create texture with a predefined size</summary>
		public Texture2DBuilder WithSize( Vector2 size )
		{
			Width = size.x.CeilToInt();
			Height = size.y.CeilToInt();
			return this;
		}
	}
}
