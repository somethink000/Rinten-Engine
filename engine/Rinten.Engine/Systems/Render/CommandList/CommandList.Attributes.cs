using System.Runtime.CompilerServices;

namespace Rinten.Rendering;

public sealed partial class CommandList
{
	public unsafe class AttributeAccess
	{
		//Writes (Grab*Texture) and the clear (Reset) happen under the owning CommandList's _lock, so they're already
		//serialized. GetRenderTarget is the only access not already under that lock, so it takes it explicitly - see below.
		private readonly Dictionary<string, RenderTarget> _renderTargets = new();

		internal Func<RenderAttributes> _get;
		internal CommandList list;

		RenderAttributes attributes => _get();

		internal AttributeAccess( CommandList cmdlist, Func<RenderAttributes> _getter )
		{
			list = cmdlist;
			_get = _getter;
		}

		public void Clear()
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Clear();
			}
			list.AddEntry( &Execute, new Entry { Object1 = this } );
		}

		public void Set( StringToken token, float f )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( f, 0, 0, 0 ) } );
		}

		public void Set( StringToken token, double f ) => Set( token, (float)f );

		public void Set( StringToken token, Vector2 vector2 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, new Vector2( entry.Data1.x, entry.Data1.y ) );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( vector2.x, vector2.y, 0, 0 ) } );
		}

		public void Set( StringToken token, Vector3 vector3 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ) );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( vector3.x, vector3.y, vector3.z, 0 ) } );
		}

		public void Set( StringToken token, Vector4 vector4 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, entry.Data1 );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = vector4 } );
		}

		public void Set( StringToken token, int i )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( i, 0, 0, 0 ) } );
		}

		public void Set( StringToken token, bool b )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.Set( entry.Token, entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( b ? 1 : 0, 0, 0, 0 ) } );
		}

		public void Set( StringToken token, Matrix matrix )
		{
			//Boxed. It was written over Data1 as sixty-four bytes, and an entry has forty-eight from there to its end - the last sixteen went past it.
			static void Execute( ref Entry entry, CommandList commandList )
			{
				if ( entry.Object2 is Matrix matrix ) commandList.Values.Set( entry.Token, matrix );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object2 = matrix, Object4 = this } );
		}

		public void Set( StringToken token, GpuBuffer buffer )
		{
			//What a compute pass over the list runs over - see ComputeShader.
			static void Execute( ref Entry entry, CommandList commandList )
			{
				if ( entry.Object2 is GpuBuffer buffer ) commandList.Values.Set( entry.Token, buffer );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object2 = buffer, Object4 = this } );
		}

		public void Set( StringToken token, Texture texture, int mip = -1 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				if ( entry.Object2 is Texture texture ) commandList.Values.Set( entry.Token, texture, (int)entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object2 = texture, Object4 = this, Data1 = new Vector4( mip, 0, 0, 0 ) } );
		}

		public void Set( StringToken token, SamplerState samplerState )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				if ( entry.Object2 is SamplerState state ) commandList.Values.Set( entry.Token, state );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object2 = samplerState, Object4 = this } );
		}

		//Read by what the list draws after it: a draw out of buffers or a box on the HUD reads the ones it
		//knows, and a list run from inside a scene object's own drawing hands them to the draws it makes there.
		public void SetCombo( StringToken token, int value )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.SetCombo( entry.Token, (int)entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( value, 0, 0, 0 ) } );
		}

		public void SetCombo( StringToken token, bool value )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.SetCombo( entry.Token, entry.Data1.x != 0.0f );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( value ? 1 : 0, 0, 0, 0 ) } );
		}

		public void SetCombo<T>( StringToken token, T t ) where T : unmanaged, Enum
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.SetCombo( entry.Token, (int)entry.Data1.x );
			}
			//Store the enum as its integer value in Data1.x to avoid boxing into Object2.
			var intValue = Unsafe.SizeOf<T>() switch
			{
				1 => Unsafe.As<T, byte>( ref t ),
				2 => (int)Unsafe.As<T, short>( ref t ),
				8 => (int)Unsafe.As<T, long>( ref t ),
				_ => Unsafe.As<T, int>( ref t )
			};
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( intValue, 0, 0, 0 ) } );
		}

		public void SetData<T>( StringToken token, T data ) where T : unmanaged
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				commandList.Values.SetData( entry.Token, commandList.ReadPayload<T>( entry ) );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this }, data );
		}

		/// <summary>Set a special value</summary>
		public void SetValue( StringToken token, RenderValue value )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				switch ( (RenderValue)(int)entry.Data1.x )
				{
					case RenderValue.ColorTarget:
						commandList.Values.Set( entry.Token, (float)CorePostBlit.OpenColourIndex );
						break;

					case RenderValue.DepthTarget:
						commandList.Values.Set( entry.Token, (float)CorePostBlit.OpenDepthIndex );
						break;

					case RenderValue.MsaaCombo:
						commandList.Values.SetCombo( entry.Token, 0 );
						break;
				}
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object4 = this, Data1 = new Vector4( (int)value, 0, 0, 0 ) } );
		}

		/// <summary>Set the color texture from this named render target to this attribute</summary>
		public void Set( StringToken token, RenderTargetHandle.ColorTextureRef buffer, int mip = -1 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				//Where the picture sits in the table, as a number a pass reads it by - and, for a compute pass storing into it, which picture and level.
				var name = entry.Object5 as string;

				commandList.Values.Set( entry.Token, (float)CorePasses.IndexOf( name ) );
				commandList.Pictures[entry.Token] = ( name, (int)entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object5 = buffer.Name, Object4 = this, Data1 = new Vector4( mip, 0, 0, 0 ) } );
		}

		/// <summary>Set the depth texture from this named render target to this attribute</summary>
		public void Set( StringToken token, RenderTargetHandle.DepthTextureRef buffer, int mip = -1 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				//Where the picture sits in the table, as a number a pass reads it by - and, for a compute pass storing into it, which picture and level.
				var name = entry.Object5 as string;

				commandList.Values.Set( entry.Token, (float)CorePasses.IndexOf( name ) );
				commandList.Pictures[entry.Token] = ( name, (int)entry.Data1.x );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object5 = buffer.Name, Object4 = this, Data1 = new Vector4( mip, 0, 0, 0 ) } );
		}

		/// <summary>Set the color texture from this named render target to this attribute</summary>
		public void Set( StringToken token, RenderTargetHandle.ColorIndexRef buffer )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				var name = entry.Object5 as string;

				commandList.Values.Set( entry.Token, (float)CorePasses.IndexOf( name ) );
				commandList.Pictures[entry.Token] = ( name, 0 );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object5 = buffer.Name, Object4 = this } );
		}

		/// <summary>Set the size of this named render target to this float2 attribute</summary>
		public void Set( StringToken token, RenderTargetHandle.SizeHandle size, bool inverse = false )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				var name = entry.Object5 as string;
				int width, height;

				if ( name == "$vp" ) ( width, height ) = CorePasses.Sized( 1 );
				else if ( CorePasses.Find( name ) is { } picture )
				{
					width = picture.Width;
					height = picture.Height;
				}
				else return;

				commandList.Values.Set( entry.Token, entry.Data1.x != 0.0f
					? new Vector2( 1.0f / width, 1.0f / height )
					: new Vector2( width, height ) );
			}
			list.AddEntry( &Execute, new Entry { Token = token, Object5 = size.Name, Object4 = this, Data1 = new Vector4( inverse ? 1 : 0, 0, 0, 0 ) } );
		}

		/// <summary>Takes a copy of the current viewport's color texture and stores it in targetName on renderAttributes.</summary>
		public RenderTargetHandle GrabFrameTexture( string token = "FrameTexture", bool withMips = false )
		{
			return GrabFrameTexture( token, withMips ? Graphics.DownsampleMethod.GaussianBlur : Graphics.DownsampleMethod.None );
		}

		/// <summary>Takes a copy of the current viewport's color texture and stores it in targetName on renderAttributes.</summary>
		public RenderTargetHandle GrabFrameTexture( string token, Graphics.DownsampleMethod downsampleMethod, int maxMips = 0 )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				//The frame as it stands, copied into a picture of that name - see CorePasses.Grab.
				var name = entry.Object5 as string;
				var made = CorePasses.Grab( name, (int)entry.Data1.x != (int)Graphics.DownsampleMethod.None );

				if ( made is null ) return;

				commandList.Values.Set( name, (float)made.Index );
				commandList.Pictures[name] = ( name, 0 );
			}
			list.AddEntry( &Execute, new Entry { Object5 = token, Object2 = this, Data1 = new Vector4( (int)downsampleMethod, maxMips, 0, 0 ) } );

			return new RenderTargetHandle { Name = token };
		}

		/// <summary>Takes a copy of the current viewport's depth texture and stores it in targetName on renderAttributes.</summary>
		public RenderTargetHandle GrabDepthTexture( string token = "DepthTexture" )
		{
			static void Execute( ref Entry entry, CommandList commandList )
			{
				//The world's depth under that name - read, never copied.
				var name = entry.Object5 as string;
				var size = CorePostBlit.OpenSize;

				CorePasses.Alias( name, CorePostBlit.OpenDepthIndex, (int)size.x, (int)size.y );
				commandList.Values.Set( name, (float)CorePasses.IndexOf( name ) );
			}
			list.AddEntry( &Execute, new Entry { Object5 = token, Object2 = this } );

			return new RenderTargetHandle { Name = token };
		}

		/// <summary>Get the actual render target by name. Useful for externals that need to access the render target directly.</summary>
		public RenderTarget GetRenderTarget( string name )
		{
			//Serialize against execute-thread writes / Reset clears, which hold the CommandList's _lock.
			lock ( list._lock )
			{
				if ( _renderTargets.TryGetValue( name, out var rt ) )
					return rt;
				return null;
			}
		}

		internal void ClearRenderTargets()
		{
			//Caller (Reset) already holds the CommandList's _lock.
			_renderTargets.Clear();
		}
	}

	/// <summary>These are the attributes for the current view. Setting a variable here will let you pass it down to other places in the render pipeline.</summary>
	[Obsolete( "Global/frame attributes are deprecated. Use a local Attributes set or pipeline texture slots instead." )]
	public AttributeAccess GlobalAttributes => Attributes;

	/// <summary>Access to the local attributes. What these are depends on where the command list is being called. If we're calling from a renderable, these are the attributes for that renderable.</summary>
	public AttributeAccess Attributes { get; private set; }

	[Obsolete( "Frame attributes are deprecated. Use a local Attributes set or pipeline texture slots instead." )]
	RenderAttributes GetFrameAttributes() => GetLocalAttributes();
	RenderAttributes GetLocalAttributes() => Graphics.Attributes;

}


public enum RenderValue
{
	/// <summary>The color texure we're currently rendering to</summary>
	ColorTarget,

	/// <summary>The depth texture we're currently rendering to</summary>
	DepthTarget,

	/// <summary>Will set the named combo to 1 if MSAA is active, otherwise 0.</summary>
	MsaaCombo,
}



