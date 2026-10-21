using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Rinten.Rendering;


public sealed unsafe partial class CommandList
{
	readonly Lock _lock = new Lock();

	private string _debugName;
	private string _markerName = "CommandList";

	public string DebugName
	{
		get => _debugName;
		set
		{
			//The same name again is the ordinary case - an effect's list is reset and named every frame - and the marker was concatenated anew each time.
			if ( string.Equals( _debugName, value, StringComparison.Ordinal ) ) return;

			_debugName = value;
			_markerName = string.IsNullOrEmpty( value ) ? "CommandList" : string.Concat( "CommandList: ", value );
		}
	}

	public bool Enabled { get; set; }
	public Flag Flags { get; set; }

	public CommandList()
	{
		Enabled = true;

		Attributes = new AttributeAccess( this, GetLocalAttributes );
	}

	public CommandList( string debugName ) : this()
	{
		DebugName = debugName;
	}

	//Holds the function and state data for a single command. Target: fit within 2 cache lines (128 bytes).
	struct Entry
	{
		public delegate*< ref Entry, CommandList, void > Execute;

		//These should store REFERENCE types only. If you store value types here they
		//will be boxed and allocate. It's not the end of the world, but it's something.
		public object Object1;
		public object Object2;
		public object Object3;
		public object Object4;
		public object Object5;

		public StringToken Token;

		public Vector4 Data1;
		public Vector4 Data2;
		public Vector4 Data3;
	}

	//An ordered list of entries that will execute on the render thread. All access (record and execute) is serialized by _lock - recording
	//and executing the same list from two threads at once is caller misuse, but the lock guarantees it can't crash or corrupt state.
	readonly List<Entry> _entries = new List<Entry>( 8 );

	//Attributes as set so far, written at run time so set/draw/set/draw keeps its order
	internal readonly RenderAttributes Values = new();

	//The frame's pictures this list's attributes name, by the name they were set under - Attributes.Set( token,
	//target.ColorTexture, mip ). Written as the list runs; what a compute pass stores into is found here.
	internal readonly Dictionary<StringToken, (string Name, int Mip)> Pictures = new();

	//The picture and level set under a name - the attribute's, or the shader's own.
	internal bool PictureOf( string key, string name, out CorePasses.Named picture, out int mip )
	{
		picture = null;
		mip = 0;

		if ( !Pictures.TryGetValue( key, out var set ) && !Pictures.TryGetValue( name, out set ) ) return false;

		picture = CorePasses.Find( set.Name );
		mip = set.Mip;

		return picture is { Picture: not 0 };
	}

	//What a picture asked for in one of their formats is made in here - see CorePasses.
	internal static CoreRender.PictureKind PictureKindOf( ImageFormat format ) => format switch
	{
		ImageFormat.RGBA8888 or ImageFormat.BGRA8888 or ImageFormat.RGBA8888_LINEAR or ImageFormat.BGRA8888_LINEAR or
			ImageFormat.RGBX8888 or ImageFormat.BGRX8888 or ImageFormat.A8 or ImageFormat.I8 => CoreRender.PictureKind.Colour,
		ImageFormat.R16F => CoreRender.PictureKind.Single,
		ImageFormat.RG1616F => CoreRender.PictureKind.Pair,
		ImageFormat.RGBA32323232F => CoreRender.PictureKind.Full,
		ImageFormat.R32F => CoreRender.PictureKind.SingleFull,
		ImageFormat.RGBA1010102 or ImageFormat.BGRA1010102 => CoreRender.PictureKind.Ten,
		_ => CoreRender.PictureKind.Hdr,
	};

	private static readonly HashSet<string> notCarriedOut = new();

	//A command this renderer cannot carry out as asked, said once rather than dropped without a word - a list
	//that draws nothing and says nothing reads as a shader that will not compile. why says what stops it.
	private static void NotCarriedOut( string what, string why )
	{
		lock ( notCarriedOut )
		{
			if ( !notCarriedOut.Add( what ) ) return;
		}

		Log.Warning( $"CommandList.{what} is skipped: {why}" );
	}

	//What marks an entry as a box - a quad or words, which a list draws a run of at once; see CoreListDraws.FlushBoxes.
	private const float BoxMark = -9.0f;



	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	void AddEntry( delegate*< ref Entry, CommandList, void > execute, Entry data )
	{
		data.Execute = execute;

		lock ( _lock )
			_entries.Add( data );
	}

	[Obsolete]
	RenderAttributes attributes => Graphics.Attributes;

	/// <summary>Access to simple 2D painting functions to draw shapes and text.</summary>
	public HudPainter Paint => new HudPainter( this );

	//This lives for the lifetime of the command list and is used to store temporary render targets and other state.
	private class State
	{
		public Dictionary<string, RenderTarget> renderTargets = new();

		/// <summary>Should be called at the end of usage</summary>
		public void Reset()
		{
			//We just clear the list - RenderTargets get freed and re-added to the pool automatically.
			renderTargets.Clear();
		}

		/// <summary>Sneaky way for externals to get render target</summary>
		public RenderTarget GetRenderTarget( string name )
		{
			if ( renderTargets.TryGetValue( name, out var target ) )
				return target;

			return default;
		}
	}

	State state;

	public void Reset()
	{
		//Serialize against execution: clearing the entry list or the render-target cache
		//while another thread is mid-execute would corrupt them. The lock makes that safe.
		lock ( _lock )
		{
			Attributes.ClearRenderTargets();
			Values.Clear();
			Pictures.Clear();
			_entries.Clear();
		}
	}

	//The one command of this list that our renderer carries out - see CorePostBlit. It is what every post-process effect is made of, and until it did something no shader written outside the engine could run over a frame at all.
	/// <summary>Draws a material over the whole screen.</summary>
	public void Blit( Material material, RenderAttributes attributes = null, string entry = null )
		=> Blit( material, attributes, entry, 0, false );

	//A blit laid over the frame a particular way, or reading the frame's chain of halvings - see BasePostProcess.BlitMode, which is what says either.
	internal void Blit( Material material, RenderAttributes attributes, string entry, int blend, bool frameMips )
	{
		static void Execute( ref Entry recorded, CommandList commandList )
		{
			CorePostBlit.Draw( recorded.Object1 as Material, recorded.Object2 as RenderAttributes, commandList,
				recorded.Object5 as string, (int)recorded.Data1.x, recorded.Data1.y > 0.5f );
		}

		AddEntry( &Execute, new Entry
		{
			Object1 = material,
			Object2 = attributes,
			Object5 = entry,
			Data1 = new Vector4( blend, frameMips ? 1.0f : 0.0f, 0.0f, 0.0f ),
		} );
	}

	/// <summary>Applies a point effect (float3 Apply(float3, float2)); folded with others into one pass</summary>
	public void Apply( Material material, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry recorded, CommandList commandList )
		{
			CorePostBlit.Point( recorded.Object1 as Material, recorded.Object2 as RenderAttributes, commandList );
		}

		AddEntry( &Execute, new Entry { Object1 = material, Object2 = attributes, Data3 = new Vector4( 0, 0, 0, ApplyMark ) } );
	}

	//Whether nothing has been recorded since the last reset - a list a camera always carries, its HUD's, is empty on
	//every frame nobody drew one, and a stage holding only empty lists has nothing to run. See CoreSceneDrawer.Hung.
	internal bool IsEmpty
	{
		get
		{
			lock ( _lock ) return _entries.Count == 0;
		}
	}

	//What marks an entry as an Apply - see OnlyApply.
	private const float ApplyMark = -7.0f;

	//Whether this list is one Apply and nothing else, and what it lays - which is how the drawer tells a list that only names the frame's curve.
	internal bool OnlyApply( out Material material, out RenderAttributes attributes )
	{
		lock ( _lock )
		{
			if ( _entries.Count == 1 && _entries[0].Data3.w == ApplyMark )
			{
				material = _entries[0].Object1 as Material;
				attributes = _entries[0].Object2 as RenderAttributes;
				return true;
			}
		}

		material = null;
		attributes = null;

		return false;
	}

	/// <summary>Draws a quad in the pixels of what the list draws into.</summary>
	public void DrawQuad( Rect rect, Material material, Color color )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			var rect = new Rect( entry.Data1.x, entry.Data1.y, entry.Data1.z, entry.Data1.w );
			var color = new Color( entry.Data2.x, entry.Data2.y, entry.Data2.z, entry.Data2.w );
			var material = entry.Object1 as Material;

			switch ( CoreListDraws.Where )
			{
				case CoreListDraws.Place.Stage:
					CoreListDraws.Quad( commandList, rect, material, color );
					break;

				case CoreListDraws.Place.Walk:
					if ( material is null ) break;
					commandList.Values.MergeTo( Graphics.Attributes );
					Graphics.DrawQuad( rect, material, color );
					break;

				default:
					CoreListDraws.Nowhere( "DrawQuad" );
					break;
			}
		}

		AddEntry( &Execute, new Entry
		{
			Data1 = new Vector4( rect.Left, rect.Top, rect.Width, rect.Height ),
			Object1 = material,
			Data2 = new Vector4( color.r, color.g, color.b, color.a ),
			Data3 = new Vector4( 0, 0, 0, BoxMark ),
		} );
	}

	public void DrawScreenQuad( Material material, Color color )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//A full-screen pass - what a blit is.
			CorePostBlit.Draw( entry.Object1 as Material, null, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = material, Data1 = new Vector4( color.r, color.g, color.b, color.a ) } );
	}

	[Obsolete]
	public void Set( StringToken token, float f )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( f, 0, 0, 0 ) } );
	}

	[Obsolete] public void Set( StringToken token, double f ) => Set( token, (float)f );

	[Obsolete]
	public void Set( StringToken token, Vector2 vector2 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, new Vector2( entry.Data1.x, entry.Data1.y ) );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( vector2.x, vector2.y, 0, 0 ) } );
	}

	[Obsolete]
	public void Set( StringToken token, Vector3 vector3 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ) );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( vector3.x, vector3.y, vector3.z, 0 ) } );
	}

	[Obsolete]
	public void Set( StringToken token, Vector4 vector4 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1 );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = vector4 } );
	}

	[Obsolete]
	public void Set( StringToken token, int i )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, (int)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( i, 0, 0, 0 ) } );
	}

	[Obsolete]
	public void Set( StringToken token, bool b )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1.x != 0.0f );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( b ? 1 : 0, 0, 0, 0 ) } );
	}

	[Obsolete]
	public void Set( StringToken token, Matrix matrix )
	{
		//Boxed: a matrix is more numbers than an entry carries.
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object2 is Matrix matrix ) commandList.Values.Set( entry.Token, matrix );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = matrix } );
	}

	[Obsolete]
	public void Set( StringToken token, GpuBuffer buffer )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			if ( entry.Object2 is GpuBuffer buffer ) commandList.Values.Set( entry.Token, buffer );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = buffer } );
	}

	[Obsolete]
	public void Set( StringToken token, Texture texture )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			if ( entry.Object2 is Texture texture ) commandList.Values.Set( entry.Token, texture );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = texture } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, GpuBuffer buffer )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			if ( entry.Object2 is GpuBuffer buffer ) commandList.Values.Set( entry.Token, buffer );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = buffer } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, int i )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, (int)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( i, 0, 0, 0 ) } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, bool b )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1.x != 0.0f );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( b ? 1 : 0, 0, 0, 0 ) } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, float f )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( f, 0, 0, 0 ) } );
	}

	[Obsolete] public void SetGlobal( StringToken token, double f ) => SetGlobal( token, (float)f );

	[Obsolete]
	public void SetGlobal( StringToken token, Vector2 vector2 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, new Vector2( entry.Data1.x, entry.Data1.y ) );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( vector2.x, vector2.y, 0, 0 ) } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, Vector3 vector3 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ) );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = new Vector4( vector3.x, vector3.y, vector3.z, 0 ) } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, Vector4 vector4 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			commandList.Values.Set( entry.Token, entry.Data1 );
		}
		AddEntry( &Execute, new Entry { Token = token, Data1 = vector4 } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, Matrix matrix )
	{
		//Boxed: a matrix is more numbers than an entry carries.
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object2 is Matrix matrix ) commandList.Values.Set( entry.Token, matrix );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = matrix } );
	}

	[Obsolete]
	public void SetGlobal( StringToken token, Texture texture )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//What the passes after it in this list read under that name.
			if ( entry.Object2 is Texture texture ) commandList.Values.Set( entry.Token, texture );
		}
		AddEntry( &Execute, new Entry { Token = token, Object2 = texture } );
	}

	/// <summary>Takes a copy of the framebuffer and returns a handle to it</summary>
	[Obsolete]
	public RenderTargetHandle GrabFrameTexture( string token, bool withMips = false ) => Attributes.GrabFrameTexture( token, withMips );

	/// <summary>Takes a copy of the depthbuffer and returns a handle to it</summary>
	[Obsolete]
	public RenderTargetHandle GrabDepthTexture( string token ) => Attributes.GrabDepthTexture( token );

	/// <summary>Run this CommandList here</summary>
	public void InsertList( CommandList otherBuffer )
	{
		if ( otherBuffer == this ) return;

		//TODO - check to make sure we don't create an infinite loop? maybe make a local int here, increment every call, throw exception if it's over 2?
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is CommandList other && other != commandList ) other.ExecuteOnRenderThread();
		}
		AddEntry( &Execute, new Entry { Object1 = otherBuffer } );
	}

	//Whether the entry being run is the list's last - how a blit knows that nothing of this list draws after it. See CorePostBlit.Lands.
	internal bool AtLast { get; private set; }

	//Run this command list
	internal void ExecuteOnRenderThread()
	{
		if ( !Enabled )
			return;

		//lock - we only want to excute this once at a time, because we have local state (renderTargets). If
		//this turns out to be a big problem we can probably create a system where we pass the stat around.
		lock ( _lock )
		{
			//Store previous state
			var lastState = state;

			//Get a new state
			state = ObjectPool<State>.Get();

			try
			{
				//In the order they were recorded.
				for ( var at = 0; at < _entries.Count; at++ )
				{
					var entry = _entries[at];

					if ( entry.Execute is null ) continue;

					//A run of boxes is drawn as one, before whatever follows it that is not a box - see CoreListDraws.FlushBoxes.
					if ( entry.Data3.w != BoxMark ) CoreListDraws.FlushBoxes();

					AtLast = at == _entries.Count - 1;
					entry.Execute( ref entry, this );
				}
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"running the command list {DebugName}" );
			}
			finally
			{
				//And the last run, into whatever it was opened in, before the list that runs next names somewhere else.
				CoreListDraws.FlushBoxes();

				AtLast = false;
				state.Reset();
				ObjectPool<State>.Return( state );

				state = lastState;
			}
		}
	}

	/// <summary>Command buffer flags allow us to skip command buffers if the camera doesn't want a particular thing. Like post processing.</summary>
	public enum Flag
	{
		None = 0,
		PostProcess = 2,
		Hud = 4,
	}

	//At a camera's stage, with its own materials into whatever the list has open, tested against the world's depth - see CoreListDraws.Model. From inside a scene object's own drawing, with the world, as Graphics.DrawModel draws it.
	/// <summary>Draws a single model at the given Transform immediately.</summary>
	public void DrawModel( Model model, Transform transform, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Model model ) return;

			var transform = new Transform(
				new Vector3( entry.Data1.x, entry.Data1.y, entry.Data1.z ),
				new Rotation( entry.Data2.z, entry.Data2.w, entry.Data3.x, entry.Data3.y ),
				new Vector3( entry.Data1.w, entry.Data2.x, entry.Data2.y ) );

			commandList.DrawModelsNow( model, System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan( ref transform, 1 ),
				entry.Object2 as RenderAttributes, "DrawModel" );
		}
		AddEntry( &Execute, new Entry
		{
			Object1 = model,
			Object2 = attributes,
			Data1 = new Vector4( transform.Position.x, transform.Position.y, transform.Position.z, transform.Scale.x ),
			Data2 = new Vector4( transform.Scale.y, transform.Scale.z, transform.Rotation.x, transform.Rotation.y ),
			Data3 = new Vector4( transform.Rotation.z, transform.Rotation.w, 0, 0 )
		} );
	}

	//A model where each transform says, drawn where the list is being run - see DrawModel.
	private void DrawModelsNow( Model model, ReadOnlySpan<Transform> transforms, RenderAttributes attributes, string what, int level = -1 )
	{
		switch ( CoreListDraws.Where )
		{
			case CoreListDraws.Place.Stage:
				CoreListDraws.Model( this, model, transforms, attributes, level: level );
				break;

			case CoreListDraws.Place.Walk:
				Graphics.DrawModelsAt( model, transforms, attributes ?? Values, level );
				break;

			default:
				CoreListDraws.Nowhere( what );
				break;
		}
	}

	/// <summary>Draws one copy of the model per transform at the given LOD; DAG models draw at finest</summary>
	public void DrawModelInstanced( Model model, Span<Transform> transforms, int lodLevel, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Model model || entry.Object5 is not Transform[] transforms ) return;

			commandList.DrawModelsNow( model, transforms, entry.Object2 as RenderAttributes, "DrawModelInstanced", (int)entry.Data1.x );
		}

		AddEntry( &Execute, new Entry { Object1 = model, Object5 = transforms.ToArray(), Object2 = attributes, Data1 = new Vector4( Math.Max( lodLevel, 0 ), 0, 0, 0 ) } );
	}

	/// <summary>Draws multiple instances of a model using GPU instancing, assuming standard implemented shaders. Use `GetTransformMatrix( int instance )` in shaders to access the instance transform. There is a limit of 1,048,576 transform slots per frame when using this method.</summary>
	public void DrawModelInstanced( Model model, Span<Transform> transforms, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Model model || entry.Object5 is not Transform[] transforms ) return;

			commandList.DrawModelsNow( model, transforms, entry.Object2 as RenderAttributes, "DrawModelInstanced" );
		}
		//We need to copy the transforms to the heap to make sure they still exist when the action is executed. We also
		//discussed using a list/array as parameter, but that could lead to issues if the list/array is modified after the call.
		var transformsCopy = transforms.ToArray();
		AddEntry( &Execute, new Entry { Object1 = model, Object5 = transformsCopy, Object2 = attributes } );
	}

	/// <summary>Draws the model with instance count read on the GPU from indirect args; use SV_InstanceID</summary>
	public void DrawModelInstancedIndirect( Model model, GpuBuffer buffer, int bufferOffset = 0, RenderAttributes attributes = null )
	{
		ArgumentNullException.ThrowIfNull( buffer );

		if ( bufferOffset < 0 || bufferOffset + 8L > (long)buffer.ElementCount * buffer.ElementSize )
			throw new ArgumentOutOfRangeException( nameof( bufferOffset ) );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Model model || entry.Object2 is not GpuBuffer buffer ) return;

			var attributes = entry.Object3 as RenderAttributes;
			var at = (int)entry.Data1.x;

			switch ( CoreListDraws.Where )
			{
				case CoreListDraws.Place.Stage:
					var origin = Transform.Zero;
					CoreListDraws.Model( commandList, model, System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan( ref origin, 1 ),
						attributes, countFrom: buffer, countAt: at );
					break;

				case CoreListDraws.Place.Walk:
					Graphics.DrawModelInstancedIndirect( model, buffer, at, attributes ?? commandList.Values );
					break;

				default:
					CoreListDraws.Nowhere( "DrawModelInstancedIndirect" );
					break;
			}
		}
		AddEntry( &Execute, new Entry { Object1 = model, Object2 = buffer, Data1 = new Vector4( bufferOffset, 0, 0, 0 ), Object3 = attributes } );
	}

	/// <summary>Not supported: no vertex stage reads per-instance transforms from a buffer</summary>
	public void DrawModelInstancedIndirect( Model model, GpuBuffer transformBuffer, GpuBuffer indirectArgs, int argsOffset = 0, int lodLevel = 0, RenderAttributes attributes = null )
	{
		NotCarriedOut( "DrawModelInstancedIndirect( transforms )",
			"no vertex stage here reads a world matrix per instance out of a buffer - see the remarks on this overload" );
	}

	/// <summary>Draws multiple instances of a model using GPU instancing. This is similar to <see cref="DrawModelInstancedIndirect(Model, GpuBuffer, int, RenderAttributes)"/>, except the count is provided from the CPU rather than via a GPU buffer. Use `SV_InstanceID` semantic in shaders to access the rendered instance.</summary>
	public void DrawModelInstanced( Model model, int count, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Model model ) return;

			var count = (int)entry.Data1.x;
			var attributes = entry.Object2 as RenderAttributes;

			switch ( CoreListDraws.Where )
			{
				case CoreListDraws.Place.Stage:
					var origin = Transform.Zero;
					CoreListDraws.Model( commandList, model, System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan( ref origin, 1 ),
						attributes, instances: count );
					break;

				case CoreListDraws.Place.Walk:
					Graphics.DrawModelInstanced( model, count, attributes ?? commandList.Values );
					break;

				default:
					CoreListDraws.Nowhere( "DrawModelInstanced" );
					break;
			}
		}
		AddEntry( &Execute, new Entry { Object1 = model, Data1 = new Vector4( count, 0, 0, 0 ), Object2 = attributes } );
	}

	/// <summary>Draws geometry using a vertex buffer and material.</summary>
	public void Draw<T>( GpuBuffer<T> vertexBuffer, Material material, int startVertex = 0, int vertexCount = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles ) where T : unmanaged
	{
		Graphics.ValidateVertexBuffer( vertexBuffer );
		Graphics.ValidateMaterial( material );

		if ( vertexCount == 0 ) vertexCount = vertexBuffer.ElementCount - startVertex;

		if ( startVertex < 0 || vertexCount < 0 || (long)startVertex + vertexCount > vertexBuffer.ElementCount )
			throw new ArgumentOutOfRangeException( nameof( vertexCount ) );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer<T> vertices || entry.Object2 is not Material material ) return;

			Graphics.DrawVerticesOutOf( vertices, material, (int)entry.Data1.x, (int)entry.Data1.y,
				entry.Object3 as RenderAttributes, (Graphics.PrimitiveType)(int)entry.Data1.z, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = vertexBuffer, Object2 = material, Data1 = new Vector4( startVertex, vertexCount, (int)primitiveType, 0 ), Object3 = attributes } );
	}

	/// <summary>Draws indexed geometry using vertex and index buffers.</summary>
	public void DrawIndexed<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, int startIndex = 0, int indexCount = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles ) where T : unmanaged
	{
		Graphics.ValidateVertexBuffer( vertexBuffer );
		Graphics.ValidateIndexBuffer( indexBuffer );
		Graphics.ValidateMaterial( material );

		if ( indexCount == 0 ) indexCount = indexBuffer.ElementCount - startIndex;

		if ( startIndex < 0 || indexCount < 0 || (long)startIndex + indexCount > indexBuffer.ElementCount )
			throw new ArgumentOutOfRangeException( nameof( indexCount ) );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer<T> vertices || entry.Object2 is not GpuBuffer indices || entry.Object3 is not Material material ) return;

			Graphics.DrawIndexedOutOf( vertices, indices, material, (int)entry.Data1.x, (int)entry.Data1.y,
				entry.Object4 as RenderAttributes, (Graphics.PrimitiveType)(int)entry.Data1.z, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = vertexBuffer, Object2 = indexBuffer, Object3 = material, Data1 = new Vector4( startIndex, indexCount, (int)primitiveType, 0 ), Object4 = attributes } );
	}

	/// <summary>Draws instanced geometry using a vertex buffer and indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles ) where T : unmanaged
		=> DrawInstancedIndirect( vertexBuffer, material, indirectBuffer, bufferOffset, attributes, primitiveType, 1 );

	/// <summary>Draws instanced geometry using a vertex buffer, executing one or more indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset, RenderAttributes attributes, Graphics.PrimitiveType primitiveType, uint drawCount, uint stride = 0 ) where T : unmanaged
	{
		Graphics.ValidateVertexBuffer( vertexBuffer );
		Graphics.ValidateIndirectBuffer( indirectBuffer );
		Graphics.ValidateMaterial( material );
		Graphics.GetIndirectBufferOffset( bufferOffset, stride, System.Runtime.InteropServices.Marshal.SizeOf<GpuBuffer.IndirectDrawArguments>() );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer<T> vertices || entry.Object2 is not Material material || entry.Object3 is not GpuBuffer indirect ) return;

			Graphics.DrawInstancedIndirectOutOf( vertices, material, indirect, (uint)entry.Data1.x, entry.Object4 as RenderAttributes,
				(Graphics.PrimitiveType)(int)entry.Data1.y, (uint)entry.Data1.z, (uint)entry.Data1.w, commandList, Graphics.BindsAsVertices<T>() );
		}
		AddEntry( &Execute, new Entry { Object1 = vertexBuffer, Object2 = material, Object3 = indirectBuffer, Data1 = new Vector4( bufferOffset, (int)primitiveType, drawCount, stride ), Object4 = attributes } );
	}

	//Vertex data is accessed in shader through buffer attribute and SV_VertexID.
	/// <summary>Draws instanced geometry using indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawInstancedIndirect( Material material, GpuBuffer indirectBuffer, uint bufferOffset = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles )
		=> DrawInstancedIndirect( material, indirectBuffer, bufferOffset, attributes, primitiveType, 1 );

	//Vertex data is accessed in shader through buffer attribute and SV_VertexID.
	/// <summary>Draws instanced geometry, executing one or more indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawInstancedIndirect( Material material, GpuBuffer indirectBuffer, uint bufferOffset, RenderAttributes attributes, Graphics.PrimitiveType primitiveType, uint drawCount, uint stride = 0 )
	{
		Graphics.ValidateIndirectBuffer( indirectBuffer );
		Graphics.ValidateMaterial( material );
		Graphics.GetIndirectBufferOffset( bufferOffset, stride, System.Runtime.InteropServices.Marshal.SizeOf<GpuBuffer.IndirectDrawArguments>() );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Material material || entry.Object2 is not GpuBuffer indirect ) return;

			Graphics.DrawInstancedIndirectOutOf( null, material, indirect, (uint)entry.Data1.x, entry.Object3 as RenderAttributes,
				(Graphics.PrimitiveType)(int)entry.Data1.y, (uint)entry.Data1.z, (uint)entry.Data1.w, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = material, Object2 = indirectBuffer, Data1 = new Vector4( bufferOffset, (int)primitiveType, drawCount, stride ), Object3 = attributes } );
	}

	/// <summary>Draws instanced indexed geometry using indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawIndexedInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles ) where T : unmanaged
		=> DrawIndexedInstancedIndirect( vertexBuffer, indexBuffer, material, indirectBuffer, bufferOffset, attributes, primitiveType, 1 );

	/// <summary>Draws instanced indexed geometry, executing one or more indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawIndexedInstancedIndirect<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset, RenderAttributes attributes, Graphics.PrimitiveType primitiveType, uint drawCount, uint stride = 0 ) where T : unmanaged
	{
		Graphics.ValidateVertexBuffer( vertexBuffer );
		Graphics.ValidateIndexBuffer( indexBuffer );
		Graphics.ValidateIndirectBuffer( indirectBuffer );
		Graphics.ValidateMaterial( material );
		Graphics.GetIndirectBufferOffset( bufferOffset, stride, System.Runtime.InteropServices.Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>() );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer<T> vertices || entry.Object2 is not GpuBuffer indices ||
				entry.Object3 is not Material material || entry.Object4 is not GpuBuffer indirect )
			{
				return;
			}

			Graphics.DrawIndexedInstancedIndirectOutOf( vertices, Graphics.BindsAsVertices<T>(), indices, material, indirect, (uint)entry.Data1.x,
				entry.Object5 as RenderAttributes, (Graphics.PrimitiveType)(int)entry.Data1.y, (uint)entry.Data1.z, (uint)entry.Data1.w, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = vertexBuffer, Object2 = indexBuffer, Object3 = material, Object4 = indirectBuffer, Data1 = new Vector4( bufferOffset, (int)primitiveType, drawCount, stride ), Object5 = attributes } );
	}

	//Vertex data is accessed in shader through buffer attribute and SV_VertexID.
	/// <summary>Draws instanced indexed geometry using indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawIndexedInstancedIndirect( GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset = 0, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles )
		=> DrawIndexedInstancedIndirect( indexBuffer, material, indirectBuffer, bufferOffset, attributes, primitiveType, 1 );

	//Vertex data is accessed in shader through buffer attribute and SV_VertexID.
	/// <summary>Draws instanced indexed geometry, executing one or more indirect draw arguments stored in a GPU buffer.</summary>
	public void DrawIndexedInstancedIndirect( GpuBuffer indexBuffer, Material material, GpuBuffer indirectBuffer, uint bufferOffset, RenderAttributes attributes, Graphics.PrimitiveType primitiveType, uint drawCount, uint stride = 0 )
	{
		Graphics.ValidateIndexBuffer( indexBuffer );
		Graphics.ValidateIndirectBuffer( indirectBuffer );
		Graphics.ValidateMaterial( material );
		Graphics.GetIndirectBufferOffset( bufferOffset, stride, System.Runtime.InteropServices.Marshal.SizeOf<GpuBuffer.IndirectDrawIndexedArguments>() );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer indices || entry.Object2 is not Material material || entry.Object3 is not GpuBuffer indirect ) return;

			Graphics.DrawIndexedInstancedIndirectOutOf( null, false, indices, material, indirect, (uint)entry.Data1.x,
				entry.Object4 as RenderAttributes, (Graphics.PrimitiveType)(int)entry.Data1.y, (uint)entry.Data1.z, (uint)entry.Data1.w, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = indexBuffer, Object2 = material, Object3 = indirectBuffer, Data1 = new Vector4( bufferOffset, (int)primitiveType, drawCount, stride ), Object4 = attributes } );
	}

	//PackedVertex vertices and indices drawn with a material's stages and the draw's numbers into the world
	//For card-made geometry such as the relief ring; nothing where ready says no
	internal void DrawMesh( GpuBuffer vertices, GpuBuffer indices, int count, Material material, Func<bool> ready, RenderAttributes attributes = null )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer vertices || entry.Object2 is not GpuBuffer indices || entry.Object3 is not Material material ) return;
			if ( entry.Object4 is Func<bool> ready && !ready() ) return;

			var shape = new CoreBufferDraw
			{
				Vertices = vertices.Handle,
				Indices = indices.Handle,
				WideIndices = indices.ElementSize == sizeof( uint ),
				Count = (int)entry.Data1.x,
				Instances = 1,
			};

			Graphics.DrawOutOf( shape, null, material, entry.Object5 as RenderAttributes, Graphics.PrimitiveType.Triangles, commandList );
		}
		AddEntry( &Execute, new Entry
		{
			Object1 = vertices, Object2 = indices, Object3 = material, Object4 = ready, Object5 = attributes, Data1 = new Vector4( count, 0, 0, 0 ),
		} );
	}

	//No vertex buffer: the index buffer names the corners and the shader reads everything else out of the attributes' buffers at set 2 - see Graphics.DrawOutOf.
	/// <summary>Draws indexed geometry with instancing. Each instance shares the same index buffer.</summary>
	public void DrawIndexedInstanced( GpuBuffer indexBuffer, Material material, int instanceCount, RenderAttributes attributes = null, Graphics.PrimitiveType primitiveType = Graphics.PrimitiveType.Triangles )
	{
		Graphics.ValidateIndexBuffer( indexBuffer );
		Graphics.ValidateMaterial( material );

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer indices || entry.Object2 is not Material material ) return;

			Graphics.DrawIndexedInstancedOutOf( indices, material, (int)entry.Data1.x, entry.Object3 as RenderAttributes,
				(Graphics.PrimitiveType)(int)entry.Data1.y, commandList );
		}
		AddEntry( &Execute, new Entry { Object1 = indexBuffer, Object2 = material, Data1 = new Vector4( instanceCount, (int)primitiveType, 0, 0 ), Object3 = attributes } );
	}

	/// <summary>Get a screen sized temporary render target. You should release the returned handle when you're done to return the textures to the pool.</summary>
	public RenderTargetHandle GetRenderTarget( string name, ImageFormat format, int numMips = 1, int sizeFactor = 1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//One of the frame's pictures, the stage's size divided - see CorePasses.
			var (width, height) = CorePasses.Sized( (int)entry.Data1.y );
			CorePasses.Acquire( entry.Object5 as string, PictureKindOf( (ImageFormat)(int)entry.Data1.x ), width, height, (int)entry.Data1.z );
		}
		AddEntry( &Execute, new Entry { Object5 = name, Data1 = new Vector4( (int)format, sizeFactor, numMips, 0 ) } );
		return new RenderTargetHandle { Name = name };
	}

	/// <summary>Get a screen sized temporary render target. You should release the returned handle when you're done to return the textures to the pool.</summary>
	public RenderTargetHandle GetRenderTarget( string name, int sizeFactor = 1, ImageFormat colorFormat = ImageFormat.Default, ImageFormat depthFormat = ImageFormat.Default, MultisampleAmount msaa = MultisampleAmount.MultisampleNone, int numMips = 1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//One of the frame's pictures, the stage's size divided - see CorePasses.
			var (width, height) = CorePasses.Sized( (int)entry.Data1.x );
			CorePasses.Acquire( entry.Object5 as string, PictureKindOf( (ImageFormat)(int)entry.Data1.y ), width, height, (int)entry.Data2.x );
		}
		AddEntry( &Execute, new Entry { Object5 = name, Data1 = new Vector4( sizeFactor, (int)colorFormat, (int)depthFormat, (int)msaa ), Data2 = new Vector4( numMips, 0, 0, 0 ) } );
		return new RenderTargetHandle { Name = name };
	}

	/// <summary>Get a temporary render target. You should release the returned handle when you're done to return the textures to the pool.</summary>
	public RenderTargetHandle GetRenderTarget( string name, int width, int height, ImageFormat colorFormat = ImageFormat.Default, ImageFormat depthFormat = ImageFormat.Default, MultisampleAmount msaa = MultisampleAmount.MultisampleNone, int numMips = 1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//One of the frame's pictures, of the size asked - see CorePasses.
			CorePasses.Acquire( entry.Object5 as string, PictureKindOf( (ImageFormat)(int)entry.Data1.z ),
				(int)entry.Data1.x, (int)entry.Data1.y, (int)entry.Data2.y );
		}
		AddEntry( &Execute, new Entry { Object5 = name, Data1 = new Vector4( width, height, (int)colorFormat, (int)depthFormat ), Data2 = new Vector4( (int)msaa, numMips, 0, 0 ) } );
		return new RenderTargetHandle { Name = name };
	}

	/// <summary>One of the frame's pictures in one of this renderer's own formats - what an effect of the engine's asks for; GetRenderTarget is the same thing in their formats. The stage's size divided by <paramref name="sizeFactor"/>, with <paramref name="levels"/> levels - see CorePasses.Acquire.</summary>
	public RenderTargetHandle GetPicture( string name, PictureFormat format, int sizeFactor = 1, int levels = 1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			var (width, height) = CorePasses.Sized( (int)entry.Data1.y );
			CorePasses.Acquire( entry.Object5 as string, (CoreRender.PictureKind)(int)entry.Data1.x, width, height, (int)entry.Data1.z );
		}
		AddEntry( &Execute, new Entry { Object5 = name, Data1 = new Vector4( (int)format, sizeFactor, levels, 0 ) } );
		return new RenderTargetHandle { Name = name };
	}

	/// <summary>We're no longer using this RT, return it to the pool</summary>
	public void ReleaseRenderTarget( RenderTargetHandle handle )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			CorePasses.Release( entry.Object5 as string );
		}
		AddEntry( &Execute, new Entry { Object5 = handle.Name } );
	}

	/// <summary>Draws into a named frame picture, cleared on first use each frame; unknown names draw to the frame</summary>
	public void SetRenderTarget( RenderTargetHandle handle )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			var name = entry.Object5 as string;

			if ( !CorePasses.Want( name ) )
				NotCarriedOut( $"SetRenderTarget( {name} )", "GetRenderTarget made no picture of that name - the draws that follow go into the frame" );
		}
		AddEntry( &Execute, new Entry { Object5 = handle.Name } );
	}

	/// <summary>Draws into a frame picture copied to the target's colour texture when drawing there ends</summary>
	public void SetRenderTarget( RenderTarget target )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not RenderTarget target ) return;

			if ( !CorePostBlit.Ready )
			{
				NotCarriedOut( "SetRenderTarget( RenderTarget )", "a render target is drawn into at one of a camera's stages, where the frame is open" );
				return;
			}

			if ( !CorePasses.WantTexture( target ) )
				NotCarriedOut( $"SetRenderTarget( {target} )", "no picture of its shape would be made to draw it in" );
		}
		AddEntry( &Execute, new Entry { Object1 = target } );
	}

	/// <summary>Set the current render target. Setting this will bind the render target and change the viewport to match it.</summary>
	public void ClearRenderTarget()
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//The draws after this go into the frame again. What the picture holds is read by its ColorTexture, set on the pass that wants it.
			CorePasses.Unwant();
		}
		AddEntry( &Execute, default );
	}

	/// <summary>Set the color texture from this named render target to this attribute</summary>
	[Obsolete]
	public void Set( StringToken token, RenderTargetHandle.ColorTextureRef buffer, int mip = -1 ) => Attributes.Set( token, buffer, mip );

	/// <summary>Set the color texture from this named render target to this attribute</summary>
	[Obsolete]
	public void SetGlobal( StringToken token, RenderTargetHandle.ColorIndexRef buffer ) => GlobalAttributes.Set( token, buffer );


	public void DispatchCompute( ComputeShader compute, int threadsX, int threadsY, int threadsZ )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			( entry.Object1 as ComputeShader )?.RunInList( commandList, (int)entry.Data1.x, (int)entry.Data1.y, (int)entry.Data1.z );
		}
		AddEntry( &Execute, new Entry { Object1 = compute, Data1 = new Vector4( threadsX, threadsY, threadsZ, 0 ) } );
	}

	public void DispatchComputeIndirect( ComputeShader compute, GpuBuffer indirectBuffer, uint indirectElementOffset = 0 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object2 is not GpuBuffer indirect ) return;

			( entry.Object1 as ComputeShader )?.RunIndirectInList( commandList, indirect, (uint)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Object1 = compute, Object2 = indirectBuffer, Data1 = new Vector4( indirectElementOffset, 0, 0, 0 ) } );
	}

	/// <summary>A handle to the viewport size</summary>
	public RenderTargetHandle.SizeHandle ViewportSize => new RenderTargetHandle.SizeHandle { Name = "$vp" };

	/// <summary>A handle to the viewport size divided by a factor. Useful for dispatching at half or quarter resolution.</summary>
	public RenderTargetHandle.SizeHandle ViewportSizeScaled( int divisor ) => new RenderTargetHandle.SizeHandle { Name = "$vp", Divisor = Math.Max( 1, divisor ) };

	/// <summary>Dispatch a compute shader</summary>
	public void DispatchCompute( ComputeShader compute, RenderTargetHandle.SizeHandle dimension )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//A thread a texel of the picture named, or of the stage's picture.
			var name = entry.Object5 as string;
			var divisor = Math.Max( (int)entry.Data1.x, 1 );
			int width, height;

			if ( name == "$vp" ) ( width, height ) = CorePasses.Sized( divisor );
			else if ( CorePasses.Find( name ) is { } picture )
			{
				width = Math.Max( picture.Width / divisor, 1 );
				height = Math.Max( picture.Height / divisor, 1 );
			}
			else return;

			( entry.Object1 as ComputeShader )?.RunInList( commandList, width, height, 1 );
		}
		AddEntry( &Execute, new Entry { Object1 = compute, Object5 = dimension.Name, Data1 = new Vector4( dimension.Divisor, 0, 0, 0 ) } );
	}

	//Called during rendering, convert RenderTargetHandle.SizeHandle to a dimension
	Vector3Int? GetDimension( string name, int divisor = 0 )
	{
		Vector3Int result;

		if ( name == "$vp" )
		{
			result = new Vector3Int( Graphics.Viewport.Width.CeilToInt(), Graphics.Viewport.Height.CeilToInt(), 1 );
		}
		else
		{
			var rt = state.renderTargets.GetValueOrDefault( name );
			if ( rt is null ) return default;

			result = new Vector3Int( rt.Width, rt.Height, 1 );
		}

		if ( divisor > 1 )
		{
			result.x = Math.Max( 1, result.x / divisor );
			result.y = Math.Max( 1, result.y / divisor );
		}

		return result;
	}

	/// <summary>Clear the current drawing context to given color.</summary>
	public void Clear( Color color, bool clearColor = true, bool clearDepth = true, bool clearStencil = true )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//The picture the list is drawing into - cleared by the next draw into it.
			CorePasses.ClearWanted( new Color( entry.Data1.x, entry.Data1.y, entry.Data1.z, entry.Data1.w ) );
		}
		AddEntry( &Execute, new Entry { Data1 = new Vector4( color.r, color.g, color.b, color.a ), Data2 = new Vector4( clearColor ? 1 : 0, clearDepth ? 1 : 0, clearStencil ? 1 : 0, 0 ) } );
	}

	//On the device, every level of it, in the order the list runs - see VkTextures.ClearTexture. What the texture holds on the processor, if anything reads it there, is left as it was.
	/// <summary>Clears the given texture to a solid color.</summary>
	public void Clear( Texture texture, Color color = default )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Texture texture ) return;

			var device = texture.core?.Device() ?? 0;
			if ( device == 0 ) return;

			//A copy into it may be owed first, and a clear is not recorded in a pass.
			CorePasses.Settle();
			CorePasses.Close();

			CoreRenderDevice.ClearTexture( device, new Color( entry.Data1.x, entry.Data1.y, entry.Data1.z, entry.Data1.w ) );
		}
		AddEntry( &Execute, new Entry { Object1 = texture, Data1 = new Vector4( color.r, color.g, color.b, color.a ) } );
	}

	/// <summary>Clears the color texture of the given render target handle to a solid color.</summary>
	public void Clear( RenderTargetHandle handle, Color color = default )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( CorePasses.Find( entry.Object5 as string ) is not { Picture: not 0 } picture ) return;

			var was = CorePasses.Wanted;

			CorePasses.Want( picture );
			CorePasses.ClearWanted( new Color( entry.Data1.x, entry.Data1.y, entry.Data1.z, entry.Data1.w ) );
			CorePasses.Open( depth: false, out _ );
			CorePasses.Want( was );
		}
		AddEntry( &Execute, new Entry { Object5 = handle.ColorTexture.Name, Data1 = new Vector4( color.r, color.g, color.b, color.a ) } );
	}

	/// <summary>Fills the given GPU buffer with a repeated uint32 value.</summary>
	public void Clear( GpuBuffer buffer, uint value = 0 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			( entry.Object1 as GpuBuffer )?.Clear( (uint)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Data1 = new Vector4( value, 0, 0, 0 ) } );
	}

	/// <summary>Resets the hidden append/structured-buffer counter of <paramref name="buffer"/> to <paramref name="value"/>.</summary>
	public void SetCounterValue( GpuBuffer buffer, uint value = 0 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			( entry.Object1 as GpuBuffer )?.SetCounterValue( (uint)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Data1 = new Vector4( value, 0, 0, 0 ) } );
	}

	/// <summary>Copies the hidden append-buffer counter of <paramref name="buffer"/> into <paramref name="destBuffer"/>. Useful for feeding a survivor count into an indirect draw/dispatch argument buffer.</summary>
	public void CopyStructureCount( GpuBuffer buffer, GpuBuffer destBuffer, int destBufferOffset = 0 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer buffer || entry.Object2 is not GpuBuffer destination ) return;

			buffer.CopyStructureCount( destination, (int)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Object2 = destBuffer, Data1 = new Vector4( destBufferOffset, 0, 0, 0 ) } );
	}

	/// <summary>Executes a barrier transition for the given GPU Texture Resource. Transitions the texture resource to a new pipeline stage and access state.</summary>
	public void ResourceBarrierTransition( Texture texture, ResourceState state, int mip = -1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object1 = texture, Data1 = new Vector4( (int)state, mip, 0, 0 ) } );
	}

	/// <summary>Executes a barrier transition for the color texture of the given render target handle.</summary>
	public void ResourceBarrierTransition( RenderTargetHandle.ColorTextureRef texture, ResourceState state, int mip = -1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object5 = texture.Name, Data1 = new Vector4( (int)state, mip, 0, 0 ) } );
	}

	/// <summary>Executes a barrier transition for the depth texture of the given render target handle.</summary>
	public void ResourceBarrierTransition( RenderTargetHandle.DepthTextureRef texture, ResourceState state, int mip = -1 )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object5 = texture.Name, Data1 = new Vector4( (int)state, mip, 0, 0 ) } );
	}

	/// <summary>Executes a barrier transition for the color texture of the given render target handle.</summary>
	public void ResourceBarrierTransition( RenderTargetHandle handle, ResourceState state, int mip = -1 )
	{
		ResourceBarrierTransition( handle.ColorTexture, state, mip );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource to a new pipeline stage and access state.</summary>
	public void ResourceBarrierTransition( GpuBuffer buffer, ResourceState state )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Data1 = new Vector4( (int)state, 0, 0, 0 ) } );
	}

	/// <summary>Executes a barrier transition for the given GPU Buffer Resource. Transitions the buffer resource from a known source state to a specified destination state.</summary>
	public void ResourceBarrierTransition( GpuBuffer buffer, ResourceState before, ResourceState after )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Data1 = new Vector4( (int)before, (int)after, 0, 0 ) } );
	}

	/// <summary>Issues a UAV barrier for the given texture, ensuring writes from prior shader invocations are visible to subsequent ones without changing the resource layout.</summary>
	public void UavBarrier( Texture texture )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object1 = texture } );
	}

	/// <summary>UAV barrier on a render target's colour texture, for UAV writes read as UAV in a later pass</summary>
	public void UavBarrier( RenderTargetHandle.ColorTextureRef texture )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object5 = texture.Name } );
	}

	/// <summary>Issues a UAV barrier for the color texture of the given render target handle.</summary>
	public void UavBarrier( RenderTargetHandle handle ) => UavBarrier( handle.ColorTexture );

	/// <summary>Issues a UAV barrier for the given GPU buffer, ensuring writes from prior shader invocations are visible to subsequent ones.</summary>
	public void UavBarrier( GpuBuffer buffer )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Nothing to do: every pass here leaves what it wrote readable, and waits on what it reads - see VkFrame.PassDispatch and PictureEnd.
		}
		AddEntry( &Execute, new Entry { Object1 = buffer } );
	}

	//Sets up per-object lighting for the given scene object. Nothing to record: every draw here is bound with the frame's lights, the sun's
	//pages and the probes - see VkFrame.BindFrameSet - so an object drawn by a list is lit exactly as the world is, without being told who it is.
	internal void SetupLighting( SceneObject obj, RenderAttributes attr = null )
	{
	}

	//Uploads data from an array to a GPU buffer at execution time. Zero-copy: the array reference is stored directly without copying. The
	//caller must ensure the array contents remain stable until the command list has finished executing (i.e. until the next Reset call).
	internal void SetBufferData<T>( GpuBuffer<T> buffer, T[] data, int sourceOffset = 0, int count = -1, int elementOffset = 0 ) where T : unmanaged
	{
		if ( count < 0 ) count = data.Length - sourceOffset;

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not GpuBuffer<T> buffer || entry.Object2 is not T[] data ) return;

			buffer.SetData<T>( data.AsSpan( (int)entry.Data1.z, (int)entry.Data1.y ), (int)entry.Data1.x );
		}
		AddEntry( &Execute, new Entry { Object1 = buffer, Object2 = data, Data1 = new Vector4( elementOffset, count, sourceOffset, 0 ) } );
	}

	//Sneaky way for externals to get render target
	internal RenderTarget GetRenderTarget( string name )
	{
		if ( state.renderTargets != null && state.renderTargets.TryGetValue( name, out var target ) )
			return target;

		return default;
	}

	/// <summary>Generates a mip-map chain for the specified render target. This will generate mipmaps for the color texture of the render target.</summary>
	public void GenerateMipMaps( RenderTargetHandle handle, Graphics.DownsampleMethod method = Graphics.DownsampleMethod.GaussianBlur )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			//Its chain of halvings, out of its top - see VkFrame.PictureChain.
			if ( CorePasses.Find( entry.Object5 as string ) is not { Picture: not 0 } picture ) return;

			//Named in the frame's report. This is the overload a frame picture actually uses - a bloom's kept picture comes back from
			//GetPicture as a handle - and until it had a row the fold was the one part of a bloom that no measurement could see.
			CoreRenderDevice.ZoneBegin( "mip chain" );

			//The least or the most of each four where that was asked - a depth's nearest or farthest; every other method is the fold's average.
			var mode = (Graphics.DownsampleMethod)(int)entry.Data1.x switch
			{
				Graphics.DownsampleMethod.Min => CorePasses.FoldMode.Least,
				Graphics.DownsampleMethod.Max => CorePasses.FoldMode.Most,
				_ => CorePasses.FoldMode.Average,
			};

			try
			{
				CorePasses.Chain( picture, mode );
			}
			finally
			{
				CoreRenderDevice.ZoneEnd();
			}
		}
		AddEntry( &Execute, new Entry { Object5 = handle.Name, Data1 = new Vector4( (int)method, 0, 0, 0 ) } );
	}

	//Its colour texture's levels, each the linear blit of the one above - see GenerateMipMaps.
	/// <summary>Generates a mip-map chain for the specified render target. This will generate mipmaps for the color texture of the render target.</summary>
	public void GenerateMipMaps( RenderTarget target, Graphics.DownsampleMethod method = Graphics.DownsampleMethod.GaussianBlur )
	{
		GenerateMipMaps( target?.ColorTarget, method );
	}

	/// <summary>Generates mips on the GPU by linear 2x2 blits, whatever method is asked for</summary>
	public void GenerateMipMaps( Texture texture, Graphics.DownsampleMethod method = Graphics.DownsampleMethod.GaussianBlur )
	{
		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not Texture texture ) return;

			var device = texture.core?.Device() ?? 0;
			if ( device == 0 ) return;

			//What a list drew into it has to be in it first; and no pass is open.
			CorePasses.Settle();
			CorePasses.Close();

			//Named in the frame's report: the fold is a run of blits nobody asked for by hand, and a bloom
			//that reads eight levels pays for all eight here rather than in the pass that reads them.
			CoreRenderDevice.ZoneBegin( "mip chain" );

			try
			{
				CoreRenderDevice.GenerateTextureMips( device );
			}
			finally
			{
				CoreRenderDevice.ZoneEnd();
			}
		}
		AddEntry( &Execute, new Entry { Object1 = texture, Data1 = new Vector4( (int)method, 0, 0, 0 ) } );
	}

	/// <summary>Draws text within a rectangle using a prepared <see cref="TextRendering.Scope"/>.</summary>
	public void DrawText( TextRendering.Scope scope, Rect rect, TextFlag flags = TextFlag.LeftTop, float angleDegrees = 0f )
	{
		//Resolve the TextBlock at entry-add time so we store a class reference instead of boxing the Scope struct and TextFlag enum into object fields.
		var tb = TextRendering.GetOrCreateTextBlock( scope, flags, 8096 );
		if ( tb is null ) return;

		static void Execute( ref Entry entry, CommandList commandList )
		{
			if ( entry.Object1 is not TextRendering.TextBlock block ) return;

			var rect = new Rect( entry.Data1.x, entry.Data1.y, entry.Data1.z, entry.Data1.w );
			var flags = (TextFlag)(int)entry.Data2.x;

			switch ( CoreListDraws.Where )
			{
				case CoreListDraws.Place.Stage:
					CoreListDraws.Text( commandList, block, rect, flags, entry.Data2.y );
					break;

				case CoreListDraws.Place.Walk:
					CoreListDraws.TextInWalk( commandList, block, rect, flags, entry.Data2.y );
					break;

				default:
					CoreListDraws.Nowhere( "DrawText" );
					break;
			}
		}
		AddEntry( &Execute, new Entry
		{
			Object1 = tb,
			Data1 = new Vector4( rect.Left, rect.Top, rect.Width, rect.Height ),
			Data2 = new Vector4( (float)(int)flags, angleDegrees, 0, 0 ),
			Data3 = new Vector4( 0, 0, 0, BoxMark ),
		} );
	}
}
