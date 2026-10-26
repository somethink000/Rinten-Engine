using Silk.NET.Vulkan;

namespace Rinten;

//Pipelines: shaders plus baked fixed-function state; the only place a pass's shape is written down
internal static unsafe partial class VkPipelines
{
	private struct PipelineEntry
	{
		public Pipeline Pipeline;
		public PipelineLayout Layout;

		/// <summary>Whether it is run rather than drawn with.</summary>
		public bool Compute;
	}

	//Max specialization constants; ids must match frame.slang and CoreSceneRenderer.ShadeSpecialize
	private const int SpecializationCount = CoreRenderDevice.SpecializationCount;

	//The pipelines, by the handle that names them - an array and not a map, because
	//the draw path asks this twice for every draw it records. See VkHandleTable.
	private static readonly VkHandleTable<PipelineEntry> pipelines = new();

	//Pipelines the driver compiled this run and time spent, for PerfReport
	internal static int Baked;

	//Ticks spent inside the driver baking them - Stopwatch ticks, read as ms through Ms.
	internal static long BakedTicks;

	//Milliseconds the driver has spent baking pipelines this run.
	internal static double BakedMs => BakedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

	//The longest one bake took, in ticks; the report clears it when it arms
	internal static long SlowestTicks;

	internal static Pipeline Get( ulong handle )
		=> pipelines.TryGet( handle, out var found ) ? found.Pipeline : default;

	internal static PipelineLayout LayoutGet( ulong handle )
		=> pipelines.TryGet( handle, out var found ) ? found.Layout : default;

	//Both at once, which is what a draw wants: one lookup rather than two.
	internal static bool GetBoth( ulong handle, out Pipeline pipeline, out PipelineLayout layout )
	{
		if ( pipelines.TryGet( handle, out var found ) )
		{
			pipeline = found.Pipeline;
			layout = found.Layout;
			return pipeline.Handle != 0;
		}

		pipeline = default;
		layout = default;
		return false;
	}

	internal static bool IsCompute( ulong handle )
		=> pipelines.TryGet( handle, out var found ) && found.Compute;

	//Destroys every pipeline. Called by device shutdown after the swapchain has gone, while the device is still there to destroy them with.
	internal static void DestroyPipelines()
	{
		for ( var at = 0; at < pipelines.Used; at++ )
		{
			if ( !pipelines.Live( at ) ) continue;

			ref var held = ref pipelines.At( at );

			if ( held.Pipeline.Handle != 0 ) VkDevice.vk.DestroyPipeline( VkDevice.device, held.Pipeline, null );
			if ( held.Layout.Handle != 0 ) VkDevice.vk.DestroyPipelineLayout( VkDevice.device, held.Layout, null );
		}

		pipelines.Clear();
		recipes.Clear();
		layered.Clear();
	}

	//SPIR-V bytes into a module. Copied into words first: Vulkan wants the code
	//4-byte aligned, and bytes arriving from a managed array carry no such promise.
	private static ShaderModule MakeModule( byte* code, int size )
	{
		if ( code is null || size <= 0 || ( size % 4 ) != 0 ) return default;

		var words = (uint*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc( (nuint)size, 4 );

		try
		{
			System.Buffer.MemoryCopy( code, words, size, size );

			var info = new ShaderModuleCreateInfo
			{
				SType = StructureType.ShaderModuleCreateInfo,
				CodeSize = (nuint)size,
				PCode = words,
			};

			ShaderModule module;

			if ( VkDevice.vk.CreateShaderModule( VkDevice.device, &info, null, &module ) != Result.Success )
				return default;

			return module;
		}
		finally
		{
			System.Runtime.InteropServices.NativeMemory.AlignedFree( words );
		}
	}

	//The formats a pass into one of the frame's targets has, which a pipeline drawn in it is made for and the pass itself is opened with - one answer,
	//so the two cannot disagree. False where a picture's format was not said. screen is what the window is made in - see VkSwapchain.ScreenFormat.
	internal static bool FormatsOf( int target, int pictureFormat, Format screen, out VkPassFormats formats )
	{
		formats = new VkPassFormats { Colour0 = screen, Colour1 = VkDevice.NormalFormat, Colours = 1 };

		if ( target == VkDevice.TargetScene )
		{
			//The world writes its colour and its normals at once, so the pass that draws it has two attachments and every pipeline in it has to say so.
			formats.Colour0 = VkDevice.WorldFormat;
			formats.Colours = 2;
		}
		else if ( target == VkDevice.TargetPost || target == VkDevice.TargetSceneColour ||
			target == VkDevice.TargetDisplay || target == VkDevice.TargetDisplaySecond )
		{
			formats.Colour0 = VkDevice.WorldFormat;
		}
		//These three fell to the window's format, which is not what their pictures are - and the driver chooses what a pixel stage exports
		//by the format the pipeline names. It came out right by luck: the window's exports half floats, so nothing was clamped on the way.
		else if ( target == VkDevice.TargetFinal || target == VkDevice.TargetFinalSecond )
		{
			formats.Colour0 = VkDevice.FinalFormat;
		}
		else if ( target == VkDevice.TargetVelocity )
		{
			formats.Colour0 = VkDevice.VelocityFormat;
		}
		else if ( target == VkDevice.TargetSkyTable )
		{
			formats.Colour0 = VkDevice.SceneFormat;
		}
		else if ( target == VkDevice.TargetPicture )
		{
			//Whatever the picture was made in - see VkPictures.Shape, which is where the caller learnt it.
			if ( pictureFormat <= 0 )
			{
				VkDevice.SetText( "a pipeline for a picture was not told the picture's format" );
				return false;
			}

			formats.Colour0 = (Format)( pictureFormat & ~VkDevice.PictureWithDepth );
		}

		//Passes with no depth attachment; naming a depth format for them trips validation on every draw
		formats.Depth =
			target == VkDevice.TargetPost || target == VkDevice.TargetSceneColour ||
			target == VkDevice.TargetSkyTable ||
			target == VkDevice.TargetFinal || target == VkDevice.TargetFinalSecond ||
			target == VkDevice.TargetDisplay || target == VkDevice.TargetDisplaySecond ||
			( target == VkDevice.TargetPicture && ( pictureFormat & VkDevice.PictureWithDepth ) == 0 )
				? Format.Undefined
				: VkDevice.DepthFormat;

		return true;
	}

	//Whether each graphics pipeline says what it is before the driver compiles it - RINTEN_PIPELINE_NAMES=1 . Beside RADV_DEBUG=shaderstats,nocache
	//that turns a wall of anonymous register counts into a table by material, which is what finding the stage that lost its occupancy needs.
	internal static readonly bool Labelling = System.Environment.GetEnvironmentVariable( "RINTEN_PIPELINE_NAMES" ) == "1";

	//What the next pipeline is, set by whoever asks for it; see Labelling.
	[System.ThreadStatic] internal static string Label;

	//Graphics pipeline from two SPIR-V modules; specialize is a bitmask of constants, or -1 for defaults
	internal static ulong Create(
		byte* vertexSpirv, int vertexSize, string vertexEntry,
		byte* pixelSpirv, int pixelSize, string pixelEntry,
		int vertexLayout, int depthTest, int topology, int blend,
		int depthWrite, int cullBack, int depthOnly, int target, int rasterFlags,
		int specialize = -1, int pictureFormat = -1, uint viewMask = 0, uint samples = 1 )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !VkDevice.Ready )
			{
				VkDevice.SetText( "there is no device to bake a pipeline on" );
				return 0;
			}

			//Baked against the swapchain's format, so the swapchain has to come first. The format belongs to the surface
			//and survives resizes, so pipelines do not have to be rebuilt with the swapchain - only made after it.
			var format = VkSwapchain.ScreenFormat();

			if ( format == Format.Undefined && depthOnly == 0 && target == VkDevice.TargetScreen )
			{
				VkDevice.SetText( "there is no screen format to bake against" );
				return 0;
			}

			if ( vertexEntry is null || pixelEntry is null )
			{
				VkDevice.SetText( "a pipeline needs both entry point names" );
				return 0;
			}

			var vertex = MakeModule( vertexSpirv, vertexSize );
			var pixel = MakeModule( pixelSpirv, pixelSize );

			var made = default( PipelineEntry );

			var vk = VkDevice.vk;
			var device = VkDevice.device;

			//A mesh stage in the vertex stage's place, where that is what was asked for and the device has
			//them - it makes its own vertices, so there is no vertex input and no assembly to describe.
			var mesh = vertexLayout == VkDevice.VertexMeshShader;

			if ( mesh && !VkDevice.meshShaders )
			{
				VkDevice.SetText( "the device has no mesh shaders" );
				return 0;
			}

			var firstStage = mesh ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit;

			if ( vertex.Handle != 0 && pixel.Handle != 0 )
			{
				//The full portable push range in every layout, both stages, used or not. Declaring exactly what each
				//shader touches would save nothing and make layouts incompatible with each other for no benefit.
				var pushRange = new PushConstantRange
				{
					StageFlags = VkDevice.DrawStages,
					Offset = 0,
					Size = VkDevice.PushConstantSize,
				};

				//Every layout carries all three sets, used or not - identical layouts mean any texture, any bone block and any
				//object's buffers can bind against any pipeline, and a shader that ignores a set costs nothing by having it declared.
				var setLayouts = stackalloc DescriptorSetLayout[4];
				setLayouts[0] = VkDevice.textureSetLayout;
				setLayouts[1] = VkDevice.boneSetLayout;
				setLayouts[2] = VkDevice.objectSetLayout;
				setLayouts[3] = VkDevice.bindlessSetLayout;

				var layoutInfo = new PipelineLayoutCreateInfo
				{
					SType = StructureType.PipelineLayoutCreateInfo,

					//Three where the device has no table of pictures to index. A shader that indexes one is a
					//shader that will not run there, and a layout naming a set that does not exist is invalid.
					SetLayoutCount = VkDevice.bindless ? 4u : 3u,
					PSetLayouts = setLayouts,
					PushConstantRangeCount = 1,
					PPushConstantRanges = &pushRange,
				};

				PipelineLayout layout;

				if ( vk.CreatePipelineLayout( device, &layoutInfo, null, &layout ) == Result.Success )
				{
					made.Layout = layout;

					var entryVertex = stackalloc byte[128];
					var entryPixel = stackalloc byte[128];

					VkDevice.WriteUtf8( vertexEntry, entryVertex, 128 );
					VkDevice.WriteUtf8( pixelEntry, entryPixel, 128 );

					var stages = stackalloc PipelineShaderStageCreateInfo[2];

					stages[0].SType = StructureType.PipelineShaderStageCreateInfo;
					stages[0].Stage = firstStage;
					stages[0].Module = vertex;
					stages[0].PName = entryVertex;

					stages[1].SType = StructureType.PipelineShaderStageCreateInfo;
					stages[1].Stage = ShaderStageFlags.FragmentBit;
					stages[1].Module = pixel;
					stages[1].PName = entryPixel;

					//One word a constant, in the order the ids are given. Handed to both stages: a constant the shader did not declare is
					//ignored by the driver, and declaring which stage uses which would be a second list to keep in step with frame.slang.
					var specValues = stackalloc uint[SpecializationCount];
					var specEntries = stackalloc SpecializationMapEntry[SpecializationCount];

					for ( var c = 0; c < SpecializationCount; c++ )
					{
						specValues[c] = (uint)( ( specialize >> c ) & 1 );

						specEntries[c] = new SpecializationMapEntry
						{
							ConstantID = (uint)c,
							Offset = (uint)( c * sizeof( uint ) ),
							Size = (nuint)sizeof( uint ),
						};
					}

					var specInfo = new SpecializationInfo
					{
						MapEntryCount = (uint)SpecializationCount,
						PMapEntries = specEntries,
						DataSize = (nuint)( SpecializationCount * sizeof( uint ) ),
						PData = specValues,
					};

					if ( specialize >= 0 )
					{
						stages[0].PSpecializationInfo = &specInfo;
						stages[1].PSpecializationInfo = &specInfo;
					}

					//Every layout's bindings and attributes, laid out below and chosen by vertexLayout.
					var bindings = stackalloc VertexInputBindingDescription[3];
					var attributes = stackalloc VertexInputAttributeDescription[16];

					var bindingCount = 0u;
					var attributeCount = 0u;

					switch ( vertexLayout )
					{
						case VkDevice.VertexEngine:
						{
							//The engine vertex, exactly as .mdl_c and PackedVertex lay it out - EngineVertexStride bytes, offsets fixed by that format. Shaders
							//address these by explicit [[vk::location(n)]], so a shader that wants only position and normal is not at the mercy of declaration order.
							bindings[0] = new VertexInputBindingDescription( 0, VkDevice.EngineVertexStride, VertexInputRate.Vertex );
							bindingCount = 1;
							attributeCount = WriteEngineAttributes( attributes );
							break;
						}

						case VkDevice.VertexLine:
						{
							//The line vertex: a position and a colour, nothing else.
							bindings[0] = new VertexInputBindingDescription( 0, 16, VertexInputRate.Vertex );
							bindingCount = 1;

							attributes[0] = new VertexInputAttributeDescription( 0, 0, Format.R32G32B32Sfloat, 0 );
							attributes[1] = new VertexInputAttributeDescription( 1, 0, Format.R8G8B8A8Unorm, 12 );
							attributeCount = 2;
							break;
						}

						case VkDevice.VertexSkinned:
						{
							//Skinned: the engine vertex as it always was, and the skin beside it in a second buffer - so
							//one mesh's vertices serve both paths and a skinned model needs no second copy of its geometry.
							bindings[0] = new VertexInputBindingDescription( 0, VkDevice.EngineVertexStride, VertexInputRate.Vertex );
							bindings[1] = new VertexInputBindingDescription( 1, VkDevice.EngineSideStride, VertexInputRate.Vertex );
							bindingCount = 2;

							attributeCount = WriteEngineAttributes( attributes );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 6, 1, Format.R8G8B8A8Uint, 0 );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 7, 1, Format.R8G8B8A8Unorm, 4 );
							break;
						}

						case VkDevice.VertexMorphed:
						{
							//Morphed: engine vertex plus a per-vertex blend shape run; the deltas live in a storage buffer
							bindings[0] = new VertexInputBindingDescription( 0, VkDevice.EngineVertexStride, VertexInputRate.Vertex );
							bindings[1] = new VertexInputBindingDescription( 1, VkDevice.EngineSideStride, VertexInputRate.Vertex );
							bindingCount = 2;

							attributeCount = WriteEngineAttributes( attributes );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 8, 1, Format.R32G32Uint, 0 );
							break;
						}

						case VkDevice.VertexMorphedSkinned:
						{
							//And both, for a character's head: the skin in binding 1 and the runs after it.
							bindings[0] = new VertexInputBindingDescription( 0, VkDevice.EngineVertexStride, VertexInputRate.Vertex );
							bindings[1] = new VertexInputBindingDescription( 1, VkDevice.EngineSideStride, VertexInputRate.Vertex );
							bindings[2] = new VertexInputBindingDescription( 2, VkDevice.EngineSideStride, VertexInputRate.Vertex );
							bindingCount = 3;

							attributeCount = WriteEngineAttributes( attributes );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 6, 1, Format.R8G8B8A8Uint, 0 );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 7, 1, Format.R8G8B8A8Unorm, 4 );
							attributes[attributeCount++] = new VertexInputAttributeDescription( 8, 2, Format.R32G32Uint, 0 );
							break;
						}

						case VkDevice.VertexUi:
						{
							//UI box vertex carries its picture index so boxes with different textures share one draw
							bindings[0] = new VertexInputBindingDescription( 0, 168, VertexInputRate.Vertex );
							bindingCount = 1;

							attributes[0] = new VertexInputAttributeDescription( 0, 0, Format.R32G32Sfloat, 0 );          //position
							attributes[1] = new VertexInputAttributeDescription( 1, 0, Format.R32G32Sfloat, 8 );          //uv
							attributes[2] = new VertexInputAttributeDescription( 2, 0, Format.R32G32B32A32Sfloat, 16 );   //rect
							attributes[3] = new VertexInputAttributeDescription( 3, 0, Format.R32G32B32A32Sfloat, 32 );   //corner radii
							attributes[4] = new VertexInputAttributeDescription( 4, 0, Format.R8G8B8A8Unorm, 48 );        //fill
							attributes[5] = new VertexInputAttributeDescription( 5, 0, Format.R32G32B32A32Uint, 52 );     //border colours, a side each, packed
							attributes[6] = new VertexInputAttributeDescription( 6, 0, Format.R32G32B32A32Sfloat, 68 );   //width, flags, angle, blur
							attributes[7] = new VertexInputAttributeDescription( 7, 0, Format.R32G32B32A32Sfloat, 84 );   //clip
							attributes[8] = new VertexInputAttributeDescription( 8, 0, Format.R32G32B32A32Sfloat, 100 );  //brightness, saturate, contrast, invert
							attributes[9] = new VertexInputAttributeDescription( 9, 0, Format.R32G32Sfloat, 116 );        //hue rotation, sepia
							attributes[10] = new VertexInputAttributeDescription( 10, 0, Format.R32G32Sfloat, 124 );      //where the corner was before it was moved
							attributes[11] = new VertexInputAttributeDescription( 11, 0, Format.R32Uint, 132 );           //which picture in the table, nought for none
							attributes[12] = new VertexInputAttributeDescription( 12, 0, Format.R32G32B32A32Sfloat, 136 ); //the corners the clip was rounded by
							attributes[13] = new VertexInputAttributeDescription( 13, 0, Format.R32G32B32A32Uint, 152 );  //vertical radii, box's then clip's, half floats
							attributeCount = 14;
							break;
						}

						default:
							//SBC_VERTEX_NONE: the shader reads gl_VertexIndex and nothing else, so the vertex input state stays empty.
							break;
					}

					//Only what the stage reads - see VkPipelines.Inputs.
					if ( attributeCount > 0 )
						attributeCount = KeepRead( attributes, attributeCount, VertexInputsRead( vertexSpirv, vertexSize, vertexEntry ) );

					var vertexInput = new PipelineVertexInputStateCreateInfo
					{
						SType = StructureType.PipelineVertexInputStateCreateInfo,
						VertexBindingDescriptionCount = bindingCount,
						PVertexBindingDescriptions = bindingCount > 0 ? bindings : null,
						VertexAttributeDescriptionCount = attributeCount,
						PVertexAttributeDescriptions = attributeCount > 0 ? attributes : null,
					};

					var assembly = new PipelineInputAssemblyStateCreateInfo
					{
						SType = StructureType.PipelineInputAssemblyStateCreateInfo,
						Topology = topology == 1 ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList,
					};

					//Dynamic, set at draw: baking the window size into every pipeline would mean rebuilding all of them on every resize.
					var viewport = new PipelineViewportStateCreateInfo
					{
						SType = StructureType.PipelineViewportStateCreateInfo,
						ViewportCount = 1,
						ScissorCount = 1,
					};

					var dynamicStates = stackalloc DynamicState[2];
					dynamicStates[0] = DynamicState.Viewport;
					dynamicStates[1] = DynamicState.Scissor;

					var dynamic = new PipelineDynamicStateCreateInfo
					{
						SType = StructureType.PipelineDynamicStateCreateInfo,
						DynamicStateCount = 2,
						PDynamicStates = dynamicStates,
					};

					//Counter-clockwise is front, which both importers produce: glTF says so, ufbx is asked for a right-handed
					//space, and that space is ours - nothing is converted, so nothing can turn a triangle inside out.
					var raster = new PipelineRasterizationStateCreateInfo
					{
						SType = StructureType.PipelineRasterizationStateCreateInfo,

						//Edges rather than faces, where the caller asked for it and the device can. A pipeline is the only place this can be said in Vulkan, which is why
						//a wireframe view is a second pipeline per shader rather than a flag at the draw - and why nothing is baked for it until somebody turns it on.
						PolygonMode = ( rasterFlags & 1 ) != 0 && VkDevice.fillLines ? PolygonMode.Line : PolygonMode.Fill,
						CullMode = cullBack != 0 ? CullModeFlags.BackBit : CullModeFlags.None,
						FrontFace = FrontFace.CounterClockwise,
						LineWidth = 1.0f,
					};

					var multisample = new PipelineMultisampleStateCreateInfo
					{
						SType = StructureType.PipelineMultisampleStateCreateInfo,
						RasterizationSamples = samples > 1 ? (SampleCountFlags)samples : SampleCountFlags.Count1Bit,
					};

					var blendAttachment = new PipelineColorBlendAttachmentState
					{
						ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
							ColorComponentFlags.BBit | ColorComponentFlags.ABit,
					};

					//One for what fades and two for what only ever adds. Additive is what a pass that gathers wants:
					//several passes over the same texel whose answers are meant to sum, and nothing to fade against.
					if ( blend == 3 )
					{
						//Screen: what is drawn fills in what the picture has left before white rather than pushing it past it. src*(1-dst) + dst, which is
						//the same arithmetic as 1-(1-a)(1-b) with the hardware doing it - so a pass that adds light needs no read of what it is adding to.
						blendAttachment.BlendEnable = true;
						blendAttachment.SrcColorBlendFactor = BlendFactor.OneMinusDstColor;
						blendAttachment.DstColorBlendFactor = BlendFactor.One;
						blendAttachment.ColorBlendOp = BlendOp.Add;
						blendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
						blendAttachment.DstAlphaBlendFactor = BlendFactor.One;
						blendAttachment.AlphaBlendOp = BlendOp.Add;
					}
					else if ( blend == 2 )
					{
						blendAttachment.BlendEnable = true;
						blendAttachment.SrcColorBlendFactor = BlendFactor.One;
						blendAttachment.DstColorBlendFactor = BlendFactor.One;
						blendAttachment.ColorBlendOp = BlendOp.Add;

						//The alpha marks a texel as written rather than measuring anything, so the larger of
						//the two is the answer - two passes that both wrote it do not make it twice as written.
						blendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
						blendAttachment.DstAlphaBlendFactor = BlendFactor.One;
						blendAttachment.AlphaBlendOp = BlendOp.Max;
					}
					else if ( blend == 6 )
					{
						//Multiplied into what is there: the picture darkened by what is drawn, and left alone where that is white.
						blendAttachment.BlendEnable = true;
						blendAttachment.SrcColorBlendFactor = BlendFactor.DstColor;
						blendAttachment.DstColorBlendFactor = BlendFactor.Zero;
						blendAttachment.ColorBlendOp = BlendOp.Add;
						blendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
						blendAttachment.DstAlphaBlendFactor = BlendFactor.One;
						blendAttachment.AlphaBlendOp = BlendOp.Add;
					}
					else if ( blend == 5 )
					{
						//Added by its own alpha: as much of what is drawn as the alpha says, on top of what is
						//there - how reflections are laid over the frame, as much as each surface is a mirror.
						blendAttachment.BlendEnable = true;
						blendAttachment.SrcColorBlendFactor = BlendFactor.SrcAlpha;
						blendAttachment.DstColorBlendFactor = BlendFactor.One;
						blendAttachment.ColorBlendOp = BlendOp.Add;
						blendAttachment.SrcAlphaBlendFactor = BlendFactor.Zero;
						blendAttachment.DstAlphaBlendFactor = BlendFactor.One;
						blendAttachment.AlphaBlendOp = BlendOp.Add;
					}
					else if ( blend != 0 )
					{
						blendAttachment.BlendEnable = true;
						blendAttachment.SrcColorBlendFactor = BlendFactor.SrcAlpha;
						blendAttachment.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
						blendAttachment.ColorBlendOp = BlendOp.Add;
						blendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
						blendAttachment.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
						blendAttachment.AlphaBlendOp = BlendOp.Add;
					}

					//One blend state per colour attachment, and the world has two: the normals are
					//not blended whatever the colour does, because half a normal is not a direction.
					var blendAttachments = stackalloc PipelineColorBlendAttachmentState[2];
					blendAttachments[0] = blendAttachment;
					blendAttachments[1] = blendAttachment;

					//Only where the device allows the two to differ. Vulkan requires every attachment to have the same blend state
					//without independentBlend, and a pipeline that breaks that rule is invalid however well it happens to draw.
					if ( VkDevice.independentBlend ) blendAttachments[1].BlendEnable = false;

					//And a pass that has nothing to say about the shape - the sky - does not write the normals at all where the two may differ: a screen of
					//writes, a third of the sky's bytes, of what was either the clear's value again or nothing anybody reads. See CoreSceneDrawer.DrawSky.
					if ( VkDevice.independentBlend && ( rasterFlags & 2 ) != 0 ) blendAttachments[1].ColorWriteMask = 0;

					var blendCount = target == VkDevice.TargetScene ? 2u : 1u;

					//A depth-only pipeline has no colour attachments.
					var colourless = depthOnly != 0;

					var blendState = new PipelineColorBlendStateCreateInfo
					{
						SType = StructureType.PipelineColorBlendStateCreateInfo,
						AttachmentCount = colourless ? 0 : blendCount,
						PAttachments = colourless ? null : blendAttachments,
					};

					//Every frame carries the depth buffer, so every pipeline names its format - whether this one tests against it is its own business.
					var depthState = new PipelineDepthStencilStateCreateInfo
					{
						SType = StructureType.PipelineDepthStencilStateCreateInfo,
						DepthTestEnable = depthTest != 0,
						DepthWriteEnable = depthWrite != 0,

						//LessOrEqual so a depth pre-pass doesn't reject itself; 2 is Equal, 3 is Always
						DepthCompareOp = depthTest == 2 ? CompareOp.Equal
							: depthTest == 3 ? CompareOp.Always : CompareOp.LessOrEqual,
					};

					//The pipeline names the formats it will meet, which is why it has to be told which picture it is
					//for - see FormatsOf. How that is said to the driver is the dialect's: see DescribePass below.
					if ( !FormatsOf( target, pictureFormat, format, out var formats ) ) return 0;

					var colourFormats = stackalloc Format[2];
					colourFormats[0] = formats.Colour0;
					colourFormats[1] = formats.Colour1;

					//A device that foveates takes its rate from the map - see VkFovea - where the pipeline tests the depth: the world, not what is drawn over it.
					var rateState = default( PipelineFragmentShadingRateStateCreateInfoKHR );
					if ( VkFovea.Kind == VkFovea.Kinds.ShadingRate ) VkFovea.RateState( &rateState, followsMap: depthTest != 0 );

					var rendering = new PipelineRenderingCreateInfo
					{
						SType = StructureType.PipelineRenderingCreateInfo,
						PNext = VkFovea.Kind == VkFovea.Kinds.ShadingRate ? &rateState : null,
						ColorAttachmentCount = colourless ? 0 : formats.Colours,
						PColorAttachmentFormats = colourless ? null : colourFormats,
						DepthAttachmentFormat = formats.Depth,

						//The layers a pass drawing both of a headset's eyes goes to - only a pipeline made again for one; see Variant.
						ViewMask = viewMask,
					};

					var info = new GraphicsPipelineCreateInfo
					{
						SType = StructureType.GraphicsPipelineCreateInfo,
						StageCount = 2,
						PStages = stages,
						PVertexInputState = mesh ? null : &vertexInput,
						PInputAssemblyState = mesh ? null : &assembly,
						PViewportState = &viewport,
						PRasterizationState = &raster,
						PMultisampleState = &multisample,
						PDepthStencilState = &depthState,
						PColorBlendState = &blendState,
						PDynamicState = &dynamic,
						Layout = made.Layout,

						//Drawable in a pass with the foveation map attached, which every pipeline has to be while the device foveates - see VkFovea.
						Flags = VkFovea.PipelineFlags,
					};

					//Chained on as it is, or turned into the render pass those formats make - see VkDialect.
					if ( !VkDevice.dialect.DescribePass( &info, &rendering ) ) return 0;

					//Through the cache, so a pipeline the driver has compiled before - in this run or in the last one, from the bytes the managed
					//side saved - is recognised instead of compiled again. Null is legal and only means every bake starts from the SPIR-V.
					Pipeline baked;

					//Said to the same stream the driver prints its statistics to, and before it prints them, so RADV_DEBUG=shaderstats can be read by name. See Labelling.
					if ( Labelling ) System.Console.Error.WriteLine( $"@@ pipeline {Label ?? "(unlabelled)"} spec {specialize} target {target} depthOnly {depthOnly}" );
					Label = null;

					var began = System.Diagnostics.Stopwatch.GetTimestamp();
					var built = vk.CreateGraphicsPipelines( device, VkDevice.pipelineCache, 1, &info, null, &baked );

					var took = System.Diagnostics.Stopwatch.GetTimestamp() - began;
					System.Threading.Interlocked.Add( ref BakedTicks, took );
					System.Threading.Interlocked.Increment( ref Baked );
					if ( took > SlowestTicks ) SlowestTicks = took;

					if ( built == Result.Success ) made.Pipeline = baked;
				}
			}

			//The modules only feed pipeline creation; whichever way that goes, they are not needed afterwards.
			if ( vertex.Handle != 0 ) vk.DestroyShaderModule( device, vertex, null );
			if ( pixel.Handle != 0 ) vk.DestroyShaderModule( device, pixel, null );

			if ( made.Pipeline.Handle == 0 )
			{
				if ( made.Layout.Handle != 0 ) vk.DestroyPipelineLayout( device, made.Layout, null );

				VkDevice.SetText( "the pipeline would not bake - the SPIR-V or the entry point names are wrong" );
				return 0;
			}

			var handle = pipelines.Add( made );

			if ( viewMask == 0 && samples <= 1 )
			{
				Remember( made.Pipeline, new Recipe(
					new ReadOnlySpan<byte>( vertexSpirv, vertexSize ).ToArray(), vertexEntry,
					new ReadOnlySpan<byte>( pixelSpirv, pixelSize ).ToArray(), pixelEntry,
					vertexLayout, depthTest, topology, blend, depthWrite, cullBack, depthOnly, target, rasterFlags,
					specialize, pictureFormat ) );
			}

			//By the handle the managed side bakes it under, which is what the render log prints when a pipeline is made or refused.
			VkDevice.NameHandle( ObjectType.Pipeline, made.Pipeline.Handle, "pipeline", handle );
			VkDevice.NameHandle( ObjectType.PipelineLayout, made.Layout.Handle, "pipeline layout", handle );

			return handle;
		}
	}

	//The six attributes every engine vertex carries, in the fixed order.
	private static uint WriteEngineAttributes( VertexInputAttributeDescription* into )
	{
		into[0] = new VertexInputAttributeDescription( 0, 0, Format.R32G32B32Sfloat, 0 );       //position
		into[1] = new VertexInputAttributeDescription( 1, 0, Format.R8G8B8A8Unorm, 12 );        //colour
		into[2] = new VertexInputAttributeDescription( 2, 0, Format.R32Uint, PackedVertex.FrameOffset );              //normal + tangent, packed
		into[3] = new VertexInputAttributeDescription( 3, 0, Format.R32G32B32A32Sfloat, PackedVertex.TexCoord0Offset ); //texCoord0
		into[4] = new VertexInputAttributeDescription( 4, 0, Format.R32G32B32A32Sfloat, PackedVertex.TexCoord1Offset ); //texCoord1

		//No location five: the tangent is in the word at two. See vertex_frame.slang.
		return 5;
	}

	//Bakes a compute pipeline out of a module and a layout, and owns both from here: the module goes either way, the layout only if the bake fails.
	private static ulong FinishCompute( ShaderModule module, PipelineLayout layout )
	{
		var entry = stackalloc byte[8];
		VkDevice.WriteUtf8( "main", entry, 8 );

		var info = new ComputePipelineCreateInfo
		{
			SType = StructureType.ComputePipelineCreateInfo,
			Layout = layout,
		};

		info.Stage.SType = StructureType.PipelineShaderStageCreateInfo;
		info.Stage.Stage = ShaderStageFlags.ComputeBit;
		info.Stage.Module = module;
		info.Stage.PName = entry;

		Pipeline made;

		var began = System.Diagnostics.Stopwatch.GetTimestamp();
		var result = VkDevice.vk.CreateComputePipelines( VkDevice.device, VkDevice.pipelineCache, 1, &info, null, &made );

		System.Threading.Interlocked.Add( ref BakedTicks, System.Diagnostics.Stopwatch.GetTimestamp() - began );
		System.Threading.Interlocked.Increment( ref Baked );

		VkDevice.vk.DestroyShaderModule( VkDevice.device, module, null );

		if ( result != Result.Success )
		{
			VkDevice.vk.DestroyPipelineLayout( VkDevice.device, layout, null );
			VkDevice.SetText( "the compute pipeline would not bake" );
			return 0;
		}

		return pipelines.Add( new PipelineEntry { Pipeline = made, Layout = layout, Compute = true } );
	}

	//Compute pipeline for a frame pass, bound like a full-screen blit plus the levels it stores into
	internal static ulong PassCreatePipeline( byte* code, int byteCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkDevice.device.Handle == 0 || VkDevice.passSetLayout.Handle == 0 ) return 0;

			var module = MakeModule( code, byteCount );

			if ( module.Handle == 0 )
			{
				VkDevice.SetText( "the pass would not become a module" );
				return 0;
			}

			var push = new PushConstantRange
			{
				StageFlags = ShaderStageFlags.ComputeBit,
				Offset = 0,
				Size = VkDevice.PushConstantSize,
			};

			var sets = stackalloc DescriptorSetLayout[4];
			sets[0] = VkDevice.textureSetLayout;
			sets[1] = VkDevice.boneSetLayout;
			sets[2] = VkDevice.passSetLayout;
			sets[3] = VkDevice.bindlessSetLayout;

			var layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = VkDevice.bindless ? 4u : 3u,
				PSetLayouts = sets,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push,
			};

			PipelineLayout layout;

			if ( VkDevice.vk.CreatePipelineLayout( VkDevice.device, &layoutInfo, null, &layout ) != Result.Success )
			{
				VkDevice.vk.DestroyShaderModule( VkDevice.device, module, null );
				VkDevice.SetText( "the pass's pipeline layout would not be made" );
				return 0;
			}

			return FinishCompute( module, layout );
		}
	}

	internal static ulong PyramidCreatePipeline( byte* code, int byteCount )
		=> SetComputeCreate( code, byteCount, VkDevice.pyramidSetLayout, "the pyramid" );

	//A compute pipeline for the bloom's glow passes: its second set is two pictures to read and the levels to store
	//into. See VkDevice.foldSetLayout. The fold a picture's chain of halvings is filled by - see VkFrame.PictureChain.
	internal static ulong FoldCreatePipeline( byte* code, int byteCount )
		=> SetComputeCreate( code, byteCount, VkDevice.foldSetLayout, "the fold" );

	//A compute pipeline whose second set is a layout of its own rather than a material - pictures it stores into, which a material has no room for.
	private static ulong SetComputeCreate( byte* code, int byteCount, DescriptorSetLayout second, string what )
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkDevice.device.Handle == 0 || second.Handle == 0 ) return 0;

			var module = MakeModule( code, byteCount );

			if ( module.Handle == 0 )
			{
				VkDevice.SetText( $"{what} shader would not become a module" );
				return 0;
			}

			var push = new PushConstantRange
			{
				StageFlags = ShaderStageFlags.ComputeBit,
				Offset = 0,
				Size = VkDevice.PushConstantSize,
			};

			var sets = stackalloc DescriptorSetLayout[2];
			sets[0] = VkDevice.objectSetLayout;
			sets[1] = second;

			var layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 2,
				PSetLayouts = sets,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push,
			};

			PipelineLayout layout;

			if ( VkDevice.vk.CreatePipelineLayout( VkDevice.device, &layoutInfo, null, &layout ) != Result.Success )
			{
				VkDevice.vk.DestroyShaderModule( VkDevice.device, module, null );
				VkDevice.SetText( $"{what} pipeline layout would not be made" );
				return 0;
			}

			return FinishCompute( module, layout );
		}
	}

	//A compute pipeline whose second set is the picture table - every picture the device has, indexed - rather
	//than a material: for a pass that reads a picture no material names, a window's image. See VkFrame.Adaptive.
	internal static ulong ComputeCreateOverTable( byte* code, int byteCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkDevice.device.Handle == 0 || !VkDevice.bindless || VkDevice.bindlessSetLayout.Handle == 0 ) return 0;

			var module = MakeModule( code, byteCount );

			if ( module.Handle == 0 )
			{
				VkDevice.SetText( "the compute shader would not become a module" );
				return 0;
			}

			var push = new PushConstantRange
			{
				StageFlags = ShaderStageFlags.ComputeBit,
				Offset = 0,
				Size = VkDevice.PushConstantSize,
			};

			var sets = stackalloc DescriptorSetLayout[2];
			sets[0] = VkDevice.objectSetLayout;
			sets[1] = VkDevice.bindlessSetLayout;

			var layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 2,
				PSetLayouts = sets,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push,
			};

			PipelineLayout layout;

			if ( VkDevice.vk.CreatePipelineLayout( VkDevice.device, &layoutInfo, null, &layout ) != Result.Success )
			{
				VkDevice.vk.DestroyShaderModule( VkDevice.device, module, null );
				VkDevice.SetText( "the compute pipeline layout would not be made" );
				return 0;
			}

			return FinishCompute( module, layout );
		}
	}

	internal static ulong ComputeCreate( byte* code, int byteCount )
	{
		lock ( VkDevice.renderLock )
		{
			if ( VkDevice.device.Handle == 0 )
			{
				VkDevice.SetText( "there is no device" );
				return 0;
			}

			var module = MakeModule( code, byteCount );

			if ( module.Handle == 0 )
			{
				VkDevice.SetText( "the compute shader would not become a module" );
				return 0;
			}

			//Set 0 is the storage buffers a dispatch is given, and the push block is the same size every
			//other pipeline here carries - a compute pass wants the same handful of numbers a draw does.
			var push = new PushConstantRange
			{
				StageFlags = ShaderStageFlags.ComputeBit,
				Offset = 0,
				Size = VkDevice.PushConstantSize,
			};

			//And set 1 is a picture, in the same shape a draw's material is - four images and their numbers - so a pass can read what the frame drew rather than only
			//what somebody wrote into a buffer for it. A dispatch given no texture leaves it unbound, which is legal as long as the shader does not declare one.
			var sets = stackalloc DescriptorSetLayout[2];
			sets[0] = VkDevice.objectSetLayout;
			sets[1] = VkDevice.textureSetLayout;

			var layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 2,
				PSetLayouts = sets,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push,
			};

			PipelineLayout layout;

			if ( VkDevice.vk.CreatePipelineLayout( VkDevice.device, &layoutInfo, null, &layout ) != Result.Success )
			{
				VkDevice.vk.DestroyShaderModule( VkDevice.device, module, null );
				VkDevice.SetText( "the compute pipeline layout would not be made" );
				return 0;
			}

			return FinishCompute( module, layout );
		}
	}

	internal static void Destroy( ulong pipeline )
	{
		lock ( VkDevice.renderLock )
		{
			if ( !pipelines.Remove( pipeline, out var found ) ) return;

			//A frame may still be using it, so it retires to the graveyard rather than dying here - same as buffers and textures, and no stall.
			VkDevice.RetirePipeline( found.Pipeline, found.Layout );

			Forget( found.Pipeline );
		}
	}
}
