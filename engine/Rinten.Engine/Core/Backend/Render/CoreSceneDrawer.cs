using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Rinten;

//Draws a scene through our core into the open frame (window or own view); shared resources are static
internal sealed partial class CoreSceneDrawer
{

	//A line vertex, matching SBC_VERTEX_LINE: 16 bytes.
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct LineVertex
	{
		public Vector3 Position;
		public Color32 Colour;

		public LineVertex( Vector3 position, Color32 colour )
		{
			Position = position;
			Colour = colour;
		}
	}

	/// <summary>Whether this view is showing the game: game UI on, editor grid off.</summary>
	public Func<bool> GameViewSource { get; set; }

	/// <summary>Whether to draw the editor grid, light/camera icons, and selection.</summary>
	public Func<bool> EditorOverlaysSource { get; set; }

	/// <summary>Whether the grid on the ground is one of those overlays.</summary>
	public Func<bool> GroundGridSource { get; set; }

	/// <summary>Whether the grid is cut into the faces of the editable meshes.</summary>
	public Func<bool> SurfaceGridSource { get; set; }

	/// <summary>What is selected, for the outlines; asked every frame, owned by the scene view.</summary>
	public System.Func<System.Collections.Generic.IEnumerable<GameObject>> SelectionSource { get; set; }

	/// <summary>Whose gizmos to draw: the scene view's instance, so tool previews keep their gizmos.</summary>
	public System.Func<Gizmo.Instance> GizmoSource { get; set; }

	/// <summary>A watched per-frame viewport, not a one-off thumbnail, so the profiler reads the right one.</summary>
	public bool Watched { get; set; }


	//Pipelines, meshes, textures and materials live on the device rather than here - see CoreResources. Held
	//per surface, as they were, every viewport loaded and uploaded its own copy of everything in the scene.

	//Sorts see-through draws furthest first via packed 8-byte distance+index keys, not by moving structs
	private void FurthestFirst( List<Queued> queue, List<int> into )
	{
		into.Clear();

		var count = queue.Count;

		if ( count == 0 ) return;

		if ( sortKeys.Length < count ) sortKeys = new ulong[Math.Max( count, 64 )];

		var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( queue );

		for ( var i = 0; i < count; i++ )
			sortKeys[i] = ( (ulong)~Sortable( items[i].Distance ) << 32 ) | (uint)i;

		sortKeys.AsSpan( 0, count ).Sort();

		for ( var i = 0; i < count; i++ ) into.Add( (int)( sortKeys[i] & 0xFFFFFFFFu ) );
	}

	//A float as a uint that sorts the same: sign bit flipped when positive, every bit when not
	private static uint Sortable( float value )
	{
		var bits = (uint)System.BitConverter.SingleToInt32Bits( value );

		return ( bits & 0x80000000u ) != 0 ? ~bits : bits | 0x80000000u;
	}

	//The numbers FurthestFirst sorts, kept between frames.
	private ulong[] sortKeys = System.Array.Empty<ulong>();

	//And the orders it hands out - one per queue, so no two can be in hand at once.
	private readonly List<int> layerOrder = new();
	private readonly List<int> namedOrder = new();
	private readonly List<int> translucentOrder = new();

	private struct Queued
	{
		public CoreSceneRenderer.SceneMesh Mesh;
		public CoreSceneRenderer.MeshDraw Part;
		public int PushAt;
		public System.Numerics.Matrix4x4 World;
		public Vector3 Center;
		public float Radius;
		public System.Numerics.Matrix4x4[] Pose;
		public bool Skinned;
		public float[] Morphs;
		public CoreSceneRenderer.PipelineKind Kind;
		public Color Tint;
		public bool Moves;

		/// <summary>The thing itself moves (posed or moved), not a shader bending it; picks its shadow maps.</summary>
		public bool InMotion;
		public bool NoShadow;
		public bool ShadowOnly;

		/// <summary>Whether this part belongs to an editable mesh - see DrawSurfaceGrid.</summary>
		public bool EditableMesh;
		public float Distance;

		/// <summary>Copies one draw makes, told apart by SV_InstanceID; nought and one are one.</summary>
		public int Instances;

		/// <summary>A project buffer the copy count is read from, and where; else nought.</summary>
		public ulong CountFrom;
		public long CountAt;
		public ulong ArgsBuffer;
		public long ArgsAt;
	}

	private readonly List<Queued> opaque = new();
	private readonly List<Queued> translucent = new();

	//What is drawn over the world with depth of its own, and what over the interface - see
	//CoreSceneTables.DrawLayer. Every part of each, drawn the classic way after everything it is laid over.
	private readonly List<Queued> overlayLayer = new();
	private readonly List<Queued> afterUiLayer = new();

	//Overlay layers squeeze into depth nought to this, the world nought to one (SetDepthCeiling): in front of
	//the world, with room for a model's parts to sort
	private const float LayerDepth = 0.001f;

	//The frame's push blocks, one after another - see Queued.PushAt.
	private byte[] pushArena = new byte[CoreSceneRenderer.PushSize * 512];
	private int pushUsed;

	private int TakePush()
	{
		var at = pushUsed;

		if ( at + CoreSceneRenderer.PushSize > pushArena.Length )
			System.Array.Resize( ref pushArena, pushArena.Length * 2 );

		pushUsed = at + CoreSceneRenderer.PushSize;

		return at;
	}

	private ReadOnlySpan<byte> PushOf( int at )
		=> pushArena.AsSpan( at, CoreSceneRenderer.PushSize );

	//The viewport this frame is being drawn into.
	private ulong drawingView;

	//Screen pixels per metre of error a metre away, for whole-model impostor choice; nought if not perspective
	private float looseErrorScale;

	private static bool warnedMaterials;

	//The tables of the scene being drawn, and the passes over them.
	private CoreSceneTables tables;
	private readonly CoreMeshRender meshRender = new();

	//What a classic draw wants of its pictures (CoreTextureStreaming.Wear): pixels per metre at its distance,
	//times scale and UV metres. Only when streaming asks
	private void Wears( in CoreSceneRenderer.MeshDraw drawn, in System.Numerics.Matrix4x4 world, Vector3 centre, float radius, Vector3 eye )
	{
		if ( !CoreTextureStreaming.Collecting || drawn.Texture == 0 || !( looseErrorScale > 0.0f ) ) return;

		if ( !( drawn.MetresPerUv > 0.0f ) )
		{
			CoreTextureStreaming.Wear( drawn.Texture, float.MaxValue );
			return;
		}

		var scale = MathF.Sqrt( MathF.Max( MathF.Max(
			( world.M11 * world.M11 ) + ( world.M12 * world.M12 ) + ( world.M13 * world.M13 ),
			( world.M21 * world.M21 ) + ( world.M22 * world.M22 ) + ( world.M23 * world.M23 ) ),
			( world.M31 * world.M31 ) + ( world.M32 * world.M32 ) + ( world.M33 * world.M33 ) ) );

		var away = MathF.Max( eye.Distance( centre ) - radius, 0.25f );
		CoreTextureStreaming.Wear( drawn.Texture, looseErrorScale / away * scale * drawn.MetresPerUv );
	}

	//How far the eye is from a queued draw, for the ordering of glass.
	private static float Reach( in Queued queued )
		=> queued.Radius * ( 1.0f + MathF.Max( 0.0f, queued.Part.Cull.x ) );

	//Queues an instance's parts the classic way (all, or those clusters can't draw), into `into` if given
	private void QueueInstance( CoreSceneTables.Entry entry, ref CoreSceneTables.InstanceRecord record, Vector3 eye, bool all,
		List<Queued> into = null, string shader = null, ulong texture = 0, ReadOnlySpan<float> said = default, bool hidden = false )
	{
		var mesh = entry.Mesh;
		if ( mesh is null ) return;

		var world = record.World;
		var centre = new Vector3( record.Sphere.X, record.Sphere.Y, record.Sphere.Z );
		var radius = record.Sphere.W;
		var tint = entry.Tint;
		var noShadow = ( record.Flags & CoreMeshShaders.InstanceNoShadow ) != 0;
		var shadowOnly = ( record.Flags & CoreMeshShaders.InstanceShadowOnly ) != 0;

		System.Numerics.Matrix4x4[] pose = null;
		var skinned = false;

		if ( entry.Skinned && mesh.IsSkinned && entry.Renderer is SkinnedModelRenderer skin && skin.IsValid() )
		{
			pose = CoreSceneRenderer.PoseBones( mesh, skin.SceneModel, skin.WorldTransform );
			skinned = pose is not null && !CoreSceneRenderer.AtBindPose( pose );
			if ( !skinned ) pose = null;

			//The renderer's own sphere for a posed thing: the mesh's is the bind pose.
			if ( skinned )
			{
				entry.Renderer.CullSphere( out var posedCentre, out var posedRadius );
				if ( posedRadius > 0.0f ) { centre = posedCentre; radius = posedRadius; }
			}
		}

		//From the renderer's object, or the object where none (a particle drawn as a model); none where the device
		//cannot blend (CoreRender.DrawsMorphs), drawing the rest shape
		var morphs = CoreRender.DrawsMorphs && mesh.MorphCount > 0 &&( ( entry.Renderer?.SceneObject ?? entry.Object ) as SceneModel )?.Morphs is SceneObjectMorphCollection held
			? held.Weights()
			: null;

		var chosen = mesh.Chosen( entry.Groups );

		//A whole-drawn model with an impostor draws it where the cull would; otherwise its own meshes
		var standIn = false;

		if ( mesh.ImpostorError > 0.0f && looseErrorScale > 0.0f && !skinned )
		{
			var scale = new Vector3( world.M11, world.M12, world.M13 ).Length;
			var distance = MathF.Max( ( centre - eye ).Length - radius, 0.01f );

			standIn = mesh.ImpostorError * scale * looseErrorScale / distance <= Rinten.Rendering.DrawSettings.MeshLodPixels;
		}

		//One level of a chain, picked as the card picks it - see SceneMesh.PartsAt.
		var parts = standIn ? mesh.Meshes
			: mesh.PartsAt( world, eye, looseErrorScale, Rinten.Rendering.DrawSettings.MeshLodPixels );

		for ( var partAt = 0; partAt < parts.Count; partAt++ )
		{
			var part = parts[partAt];
			if ( entry.MeshIndex >= 0 ? partAt != entry.MeshIndex : !CoreSceneRenderer.SceneMesh.Shows( part, chosen ) ) continue;

			if ( part.StandsIn != standIn ) continue;
			if ( part.IndexCount <= 0 && !mesh.Pulled ) continue;

			var drawn = part;
			var material = entry.MaterialCount > partAt ? CoreMaterialTable.At( (int)tables.MaterialWordAt( entry.MaterialFirst + partAt ) ) : null;

			if ( material is not null )
			{
				drawn = drawn with
				{
					Texture = material.Texture,
					Shader = material.Shader,
					Combo = material.Combo,
					TwoSided = material.TwoSided,
					Translucent = material.Translucent,

					//The table's tint rather than the part's: an override replaced the material, and the tint
					//is part of what it replaced - see CoreSceneTables, which resolves the two together.
					Tint = material.Tint,
				};
			}

			var partTint = drawn.Tint * tint;
			if ( partTint.a < 1.0f && !drawn.Translucent ) drawn = drawn with { Translucent = true };

			//Drawn out of its clusters already, unless everything is drawn here.
			if ( !all && !entry.Loose && material is not null && !material.Loose && !material.Translucent ) continue;

			Wears( in drawn, in world, centre, radius, eye );

			//A pass drawing this one thing draws it with its own shader and its own numbers: an outline is the
			//same mesh in a colour somebody picked, and nothing about the material it usually wears applies.
			if ( shader is not null )
			{
				drawn = drawn with
				{
					Shader = shader,
					Combo = null,
					Translucent = false,
					Texture = texture != 0 ? texture : drawn.Texture,
				};
			}

			var pushAt = TakePush();
			CoreSceneRenderer.ScenePushInto( pushArena.AsSpan( pushAt, CoreSceneRenderer.PushSize ), world, partTint, record.Numbers,
				said.IsEmpty ? MemoryMarshal.CreateReadOnlySpan( ref record.Said0.X, 8 ) : said );

			var queued = new Queued
			{
				Mesh = mesh,
				Part = drawn,
				PushAt = pushAt,
				World = world,
				Center = centre,
				Radius = radius,
				Tint = partTint,
				Pose = pose,
				Skinned = skinned,
				Morphs = morphs,
				NoShadow = noShadow,
				ShadowOnly = shadowOnly || hidden,
				EditableMesh = entry.Object.IsValid() && entry.Object.Flags.IsEditableMesh,
				Moves = CoreSceneRenderer.MovesVertices( drawn.Shader, drawn.Combo ),
				InMotion = skinned || morphs is not null || record.Moving != 0,
				Kind = new CoreSceneRenderer.PipelineKind( skinned, drawn.Translucent, drawn.TwoSided )
				{
					Morphed = morphs is not null && !mesh.Pulled,
					Shader = drawn.Shader,
					Combo = drawn.Combo,
					Pulled = mesh.Pulled,
				},
			};

			if ( into is not null )
			{
				queued.Distance = eye.Distance( centre );
				into.Add( queued );
				continue;
			}

			if ( drawn.Translucent )
			{
				queued.Distance = eye.Distance( centre );
				translucent.Add( queued );
			}
			else
			{
				opaque.Add( queued );
			}
		}
	}

	//The drawer this thread is inside, for project command lists; set only for the length of one draw
	internal static CoreSceneDrawer Drawing { get; private set; }

	//Scratch for the parts of one named thing - see DrawRendererNow.
	private readonly List<Queued> named = new();

	//Draws one renderer now with the caller's material and numbers into the open target, ignoring cull
	internal void DrawRendererNow( Renderer renderer, Material material,
		bool noDepth, RenderAttributes attributes, bool pulled = false )
	{
		if ( tables is null ) return;
		if ( renderer is not ModelRenderer model || !model.IsValid() ) return;
		if ( !material.IsValid() || string.IsNullOrWhiteSpace( material.core?.Shader ) ) return;

		//Into the list's picture or the frame's, over world depth where the draw tests it (CorePasses); where there is
		//no depth to test, only depth-free draws go
		if ( !CorePasses.OpenForDraw( depth: true, orNone: noDepth, out var target, out var pictureFormat,
			out var withDepth, out var finishes ) )
		{
			return;
		}

		noDepth |= !withDepth;

		var slot = tables.SlotOf( model );
		if ( slot < 0 ) return;

		var entry = tables.EntryAt( slot );
		if ( entry?.Mesh is null ) return;

		var live = CoreResources.Live( material );
		if ( live == 0 ) return;

		Span<float> said = stackalloc float[8];

		said.Clear();
		DrawSaid( said, material.core.Shader, attributes );

		named.Clear();

		ref var record = ref tables.RecordAt( slot );

		QueueInstance( entry, ref record, lastEye, all: true,
			into: named, shader: material.core.Shader, texture: live, said: said );

		foreach ( var queued in named )
		{
			//Vertex-pulling meshes need their own shader to know the layout; drawn only when the caller allows
			if ( queued.Mesh.Pulled && !pulled ) continue;

			var kind = queued.Kind with
			{
				Target = target,
				PictureFormat = pictureFormat,
				NoDepth = noDepth,
				Finishes = finishes,
				ScreenChain = null,
				DepthOnly = false,
				Translucent = false,
			};

			SubmitAs( queued, kind );
		}
	}

	//Fills the draw's two own slots from named attributes, falling back to the shader-declared defaults
	internal static void DrawSaid( Span<float> said, string shader, RenderAttributes attributes, RenderAttributes fallback = null )
	{
		var declared = CoreResources.Declared( shader );

		if ( declared is null ) return;

		var slot = 0;

		foreach ( var parameter in declared )
		{
			if ( !parameter.IsAttribute || parameter.IsTexture ) continue;
			if ( slot >= CoreShaderParams.DrawSlots ) break;

			var value = parameter.Default;

			if ( attributes is not null &&
				( attributes.TryGetDeclared( parameter.Attribute, out var set ) ||
					attributes.TryGetDeclared( parameter.Name, out set ) ) )
			{
				value = set;
			}
			else if ( fallback is not null &&
				( fallback.TryGetDeclared( parameter.Attribute, out set ) ||
					fallback.TryGetDeclared( parameter.Name, out set ) ) )
			{
				value = set;
			}

			var at = slot * 4;

			said[at] = value.x;
			said[at + 1] = value.y;
			said[at + 2] = value.z;
			said[at + 3] = value.w;

			slot++;
		}
	}

	//Where the eye was for the frame being drawn - for the queue's own sorting.
	private Vector3 lastEye;

	//Queues one hand-drawn mesh the classic way; into, instance and attribute args as in QueueInstance
	private void QueueMesh( CoreSceneRenderer.SceneMesh mesh, Transform transform, Color modelTint, ulong groups, Vector3 eye,
		System.Numerics.Vector4 numbers = default, ulong overrideTexture = 0, bool? overrideTwoSided = null, bool? overrideTranslucent = null,
		string overrideShader = null, string overrideCombo = null, bool noShadow = false, System.Numerics.Vector4? sphere = null,
		ReadOnlySpan<float> placement = default, bool inMotion = false, List<Queued> into = null,
		int instances = 0, ulong countFrom = 0, long countAt = 0, RenderAttributes attributes = null, int level = -1,
		System.Numerics.Matrix4x4[] pose = null )
	{
		Span<float> said = stackalloc float[8];
		var skinned = pose is not null && mesh.IsSkinned;

		var maxScale = MathF.Max( transform.Scale.x, MathF.Max( transform.Scale.y, transform.Scale.z ) );
		var center = transform.PointToWorld( mesh.BoundCentre );
		var radius = mesh.BoundRadius * maxScale;

		if ( sphere is { } given )
		{
			center = new Vector3( given.X, given.Y, given.Z );
			radius = given.W;
		}

		var world = CoreSceneRenderer.WorldOf( transform );
		var chosen = mesh.Chosen( groups );

		//The level it was asked for, or the one its distance gives - see SceneMesh.PartsAt.
		var parts = mesh.PartsAt( world, eye, looseErrorScale, Rinten.Rendering.DrawSettings.MeshLodPixels, level );

		for ( var partAt = 0; partAt < parts.Count; partAt++ )
		{
			var part = parts[partAt];
			if ( !CoreSceneRenderer.SceneMesh.Shows( part, chosen ) || part.StandsIn ) continue;
			if ( part.IndexCount <= 0 && !mesh.Pulled ) continue;

			var tint = part.Tint * modelTint;
			var pushAt = TakePush();

			var drawn = part;

			if ( overrideTexture != 0 )
			{
				drawn = drawn with
				{
					Texture = overrideTexture,
					TwoSided = overrideTwoSided ?? drawn.TwoSided,
					Translucent = overrideTranslucent ?? drawn.Translucent,
					Shader = overrideShader ?? drawn.Shader,
					Combo = overrideCombo,
				};
			}
			else if ( overrideShader is not null )
			{
				drawn = drawn with { Shader = overrideShader, Combo = overrideCombo };
			}

			if ( tint.a < 1.0f && !drawn.Translucent ) drawn = drawn with { Translucent = true };

			Wears( in drawn, in world, center, radius, eye );

			//The draw's own numbers, read under this part's shader's names, as each part may be another shader
			scoped ReadOnlySpan<float> drawnNumbers = placement;

			if ( attributes is not null )
			{
				said.Clear();
				DrawSaid( said, drawn.Shader, attributes );
				drawnNumbers = said;
			}

			CoreSceneRenderer.ScenePushInto( pushArena.AsSpan( pushAt, CoreSceneRenderer.PushSize ), world, tint, numbers, drawnNumbers );

			var queued = new Queued
			{
				Mesh = mesh,
				Part = drawn,
				PushAt = pushAt,
				World = world,
				Center = center,
				Radius = radius,
				Tint = tint,
				Pose = skinned ? pose : null,
				Skinned = skinned,
				NoShadow = noShadow,
				Moves = CoreSceneRenderer.MovesVertices( drawn.Shader, drawn.Combo ),
				InMotion = inMotion,
				Instances = instances,
				CountFrom = countFrom,
				CountAt = countAt,
				ArgsAt = -1,
				Kind = new CoreSceneRenderer.PipelineKind( skinned, drawn.Translucent, drawn.TwoSided )
				{
					Shader = drawn.Shader,
					Combo = drawn.Combo,
					Pulled = mesh.Pulled,
				},
			};

			if ( into is not null )
			{
				queued.Distance = eye.Distance( center );
				into.Add( queued );
				continue;
			}

			if ( drawn.Translucent )
			{
				queued.Distance = eye.Distance( center );
				translucent.Add( queued );
			}
			else
			{
				opaque.Add( queued );
			}
		}
	}

	//Instances the camera hides by tag, a bit a slot (CoreSceneTables.Hidden); redone only when slots, tags or
	//the camera's tags change
	private uint[] hiddenBits = [];
	private bool hiding;
	private (CoreSceneTables Tables, int Slots, int Count, int TagVersion, string Include, string Exclude) hiddenFor;

	//Works hiddenBits out for a camera, if anything it depends on changed.
	private void UpdateHidden( CameraComponent camera )
	{
		var include = camera.RenderTags;
		var exclude = camera.RenderExcludeTags;

		hiding = tables is not null && ( include.TryGetAll().Any() || exclude.TryGetAll().Any() );

		if ( !hiding )
		{
			meshRender.HiddenFromCamera = default;
			return;
		}

		var key = ( tables, tables.SlotGeneration, tables.InstanceCount, SceneObject.Internal.SceneObjectTags.Version,
			include.ToString(), exclude.ToString() );

		var words = ( tables.InstanceCount + 31 ) / 32;

		if ( key != hiddenFor || hiddenBits.Length < words )
		{
			if ( hiddenBits.Length < words ) hiddenBits = new uint[Math.Max( words, hiddenBits.Length * 2 )];

			tables.Hidden( include.GetTokens(), exclude.GetTokens(), hiddenBits.AsSpan( 0, words ) );
			hiddenFor = key;
		}

		meshRender.HiddenFromCamera = hiddenBits.AsMemory( 0, words );
	}

	//The queue an instance drawn after the world goes in, or null for the world's own.
	private List<Queued> LayerOf( CoreSceneTables.Entry entry ) => entry.Layer switch
	{
		CoreSceneTables.DrawLayer.Overlay => overlayLayer,
		CoreSceneTables.DrawLayer.AfterUi => afterUiLayer,
		_ => null,
	};

	//One layer's draws, squeezed to the near end of the depth range - see LayerDepth - into whichever
	//target is open, solid first and then see-through, furthest first. Nothing the camera's tags hide.
	private void DrawLayer( List<Queued> layer, CoreRender.TargetKind? onto = null )
	{
		if ( layer.Count == 0 ) return;

		CoreRenderDevice.SetDepthCeiling( LayerDepth );

		foreach ( var queued in layer )
		{
			if ( queued.ShadowOnly || queued.Kind.Translucent ) continue;
			Draw( queued );
		}

		FurthestFirst( layer, layerOrder );

		foreach ( var at in layerOrder )
		{
			var queued = layer[at];

			if ( queued.ShadowOnly || !queued.Kind.Translucent ) continue;
			Draw( queued );
		}

		CoreRenderDevice.SetDepthCeiling( 1.0f );

		void Draw( in Queued queued )
		{
			if ( onto is { } finished ) SubmitAs( queued, Wired( queued.Kind with { Target = finished, Finishes = true, ScreenChain = null } ) );
			else Submit( queued );
		}
	}

	private bool HiddenFromCamera( int slot )
		=> hiding && ( slot >> 5 ) < hiddenBits.Length && ( ( hiddenBits[slot >> 5] >> ( slot & 31 ) ) & 1u ) != 0;

	//Everything the frame draws the classic way: the loose instances, or all of them where asked.
	private void Gather( Scene scene, Vector3 eye, bool all, ReadOnlySpan<CoreImmediate.ModelBatch> immediateModels )
	{
		if ( tables is not null )
		{
			if ( all )
			{
				for ( var i = 0; i < tables.InstanceCount; i++ )
				{
					var entry = tables.EntryAt( i );
					if ( entry is null || !entry.Live || entry.Mesh is null || entry.Run > 0 ) continue;
					QueueInstance( entry, ref tables.RecordAt( i ), eye, true, into: LayerOf( entry ), hidden: HiddenFromCamera( i ) );
				}
			}
			else
			{
				foreach ( var i in tables.Loose )
				{
					var entry = tables.EntryAt( i );
					if ( entry is null || !entry.Live || entry.Mesh is null ) continue;
					var layer = LayerOf( entry );
					QueueInstance( entry, ref tables.RecordAt( i ), eye, all: layer is not null, into: layer, hidden: HiddenFromCamera( i ) );
				}
			}
		}

		var counted = false;

		foreach ( var drawn in immediateModels )
		{
			var mesh = CoreResources.Mesh( drawn.Model );
			if ( mesh is null ) continue;

			Over( drawn.Override, out var overrideTexture, out var overrideShader, out var overrideCombo, out var twoSided, out var translucent );

			var countFrom = drawn.CountFrom is { } buffer ? buffer.Handle : 0;
			counted |= countFrom != 0;

			//Drawn by code every frame, so its casting goes in the per-frame maps, never in a page
			QueueMesh( mesh, drawn.Transform, drawn.Tint, ulong.MaxValue, eye,
				overrideTexture: overrideTexture, overrideTwoSided: twoSided, overrideTranslucent: translucent,
				overrideShader: overrideShader, overrideCombo: overrideCombo, inMotion: true,
				instances: drawn.Instances, countFrom: countFrom, countAt: drawn.CountAt, attributes: drawn.Attributes, level: drawn.Level,
				pose: drawn.Pose );
		}

		//What the engine made on the card, in this world: moving casters too, as it is made again as the eye moves.
		if ( scene?.SceneWorld is { } world )
		{
			CoreCardMeshes.In( world, cardMeshes );

			foreach ( var shape in cardMeshes )
			{
				Over( shape.Wears, out var overrideTexture, out var overrideShader, out var overrideCombo, out var twoSided, out var translucent );

				QueueMesh( shape.Mesh, shape.Transform, Color.White, ulong.MaxValue, eye,
					overrideTexture: overrideTexture, overrideTwoSided: twoSided, overrideTranslucent: translucent,
					overrideShader: overrideShader, overrideCombo: overrideCombo, inMotion: true, attributes: shape.Attributes );
			}
		}

		//Arguments for what a project buffer counts, before any pass opens
		if ( counted )
		{
			PrepareCounted( opaque );
			PrepareCounted( translucent );
		}
	}

	private readonly List<CoreCardMeshes.Shape> cardMeshes = new();

	//A material over every part, as a renderer override is: its set, shader and features; nothing where there is none.
	private static void Over( Material material, out ulong texture, out string shader, out string combo, out bool? twoSided, out bool? translucent )
	{
		texture = 0;
		shader = null;
		combo = null;
		twoSided = null;
		translucent = null;

		if ( material is null || !material.IsValid() || material.core is null ) return;

		texture = CoreResources.Live( material );
		shader = material.core.Shader;
		combo = CoreResources.LiveCombo( material );

		var flags = CoreResources.LiveFlags( material );
		twoSided = flags.TwoSided;
		translucent = flags.Translucent;
	}

	//What makes classic draws copies of each other: mesh, part, texture, pipeline handle (cheap to hash)
	private readonly record struct RunKey( ulong Vertices, ulong Indices, int First, int Count,
		int FirstVertex, ulong Texture, ulong Pipeline );

	//Which run each key is, and the draws in each - kept between frames, cleared into.
	private readonly Dictionary<RunKey, int> runOf = new();
	private readonly List<List<int>> runMembers = new();
	private bool[] runTaken = [];

	//Which run a draw belongs to, or -1 - beside the queue, so pass two works none of it out again.
	private int[] runIndex = [];
	private ulong[] runPipeline = [];

	//One ring slot's worth of copies, filled a draw at a time.
	private readonly byte[] runBlock = new byte[CoreRenderDevice.BlockBytes];

	//Whether a draw can join an instanced run (portable, rigid, one copy); -noinstancing disables runs
	private static readonly bool runsOn = !Rinten.Utility.CommandLine.HasSwitch( "-noinstancing" );

	private static bool Runnable( in Queued queued )
		=> runsOn && CoreRenderDevice.PortableFrame &&
			!queued.Skinned && queued.Morphs is null && !queued.Mesh.Pulled &&
			queued.Instances <= 1 && queued.CountFrom == 0 &&
			!queued.Kind.Translucent && !queued.Kind.Lines && !queued.Kind.Wireframe &&
			queued.Mesh.Vertices != 0 && queued.Mesh.Indices != 0 && queued.Part.IndexCount > 0;

	private void SubmitLoose( ReadOnlySpan<System.Numerics.Vector4> planes, bool rigidOnly = false,
		bool prepass = false )
	{
		var queue = CollectionsMarshal.AsSpan( opaque );

		if ( queue.Length == 0 ) return;

		//Own profiler zone for classic draws; see RINTEN_LOOSE_WHY for why a thing took this path
		CoreRenderDevice.ZoneBegin( prepass ? "loose depth" : "loose" );

		var takes = Takes( queue.Length );

		for ( var i = 0; i < queue.Length; i++ )
		{
			ref readonly var queued = ref queue[i];

			//Camera draws only; vertex-moving surfaces skip the pre-pass: costly and depth may not match exactly
			takes[i] = !queued.ShadowOnly &&
				!( prepass && queued.Moves ) &&
				!( rigidOnly && queued.Skinned ) &&
				( planes.IsEmpty || queued.Radius <= 0.0f ||
					CoreSceneRenderer.SphereVisible( planes, queued.Center, Reach( in queued ) ) );
		}

		for ( var i = 0; i < queue.Length; i++ )
		{
			if ( !takes[i] ) continue;

			LooseDraws++;

			var triangles = queue[i].Mesh.Pulled
				? queue[i].Part.PulledVertices / 3
				: queue[i].Part.IndexCount / 3;

			LooseTriangles += triangles;

			if ( queue[i].Moves ) LooseMovingTriangles += triangles;
		}

		SubmitRuns( queue, takes, prepass );

		CoreRenderDevice.ZoneEnd();
	}

	//Classic-path draw and triangle counts this frame, incl. vertex-moving ones that get no distance LOD
	internal static long LooseDraws;
	internal static long LooseTriangles;
	internal static long LooseMovingTriangles;

	//The mark for one draw of a queue, a buffer kept between frames.
	private Span<bool> Takes( int length )
	{
		if ( runTaken.Length < length ) runTaken = new bool[System.Math.Max( length, 64 )];

		return runTaken.AsSpan( 0, length );
	}

	//Draws classic casters into a light's shadow layer, using a frame block copy with the light's matrix
	private void DrawLooseView( in CoreMeshRender.ViewSetup view )
	{
		if ( frameNow is null || opaque.Count == 0 ) return;

		Span<System.Numerics.Vector4> planes = stackalloc System.Numerics.Vector4[6]
		{
			view.Plane0, view.Plane1, view.Plane2, view.Plane3, view.Plane4, view.Plane5,
		};

		var queue = CollectionsMarshal.AsSpan( opaque );
		var first = -1;

		for ( var i = 0; i < queue.Length && first < 0; i++ )
			if ( CastsInto( in queue[i], view.Casters, planes ) ) first = i;

		if ( first < 0 ) return;

		frameNow.AsSpan( 0, lightFrame.Length ).CopyTo( lightFrame );
		MemoryMarshal.Write( MemoryMarshal.AsBytes( lightFrame.AsSpan() ), in view.WorldToClip );

		//The light count below nought says to a vertex that a light is looking, not the camera - see LightLooks in frame.slang.
		lightFrame[19] = -1.0f;

		var put = CoreRenderDevice.PutFrame( lightFrame.AsSpan( 0, CoreSceneRenderer.EffectsAt ) );
		if ( put == 0 ) return;

		var was = CoreRenderDevice.UseFrame( put );

		var takes = Takes( queue.Length );

		for ( var i = 0; i < queue.Length; i++ )
			takes[i] = i >= first && CastsInto( in queue[i], view.Casters, planes );

		//The same runs the camera's pass found, into the light's map - a field of copies casts as one draw there too.
		inLightView = true;
		SubmitRuns( queue, takes, prepass: true );
		inLightView = false;

		CoreRenderDevice.UseFrame( was );
	}

	//Whether a classic draw casts into a light's view: it casts, the view takes what it is, and it stands inside.
	private bool CastsInto( in Queued queued, int casters, ReadOnlySpan<System.Numerics.Vector4> planes )
	{
		if ( queued.NoShadow || queued.Kind.Translucent ) return false;
		if ( casters == 1 && queued.InMotion ) return false;
		if ( casters == 2 && !queued.InMotion ) return false;

		return queued.Radius <= 0.0f || CoreSceneRenderer.SphereVisible( planes, queued.Center, Reach( in queued ) );
	}

	private ulong depthPyramid;
	private int depthBufferSide;
	private int depthBufferDown;
	private readonly byte[] depthPush = new byte[48];

	//Whether the pyramid holds a depth this viewport drew - what the cull may ask.
	private bool depthKnown;

	//The chain this viewport's depth is folded into, made to the picture's shape.
	private ulong EnsurePyramid()
	{
		if ( pyramidPipeline == 0 ) return 0;

		if ( depthPyramid != 0 &&
			( depthBufferSide != CoreDepthPyramid.DepthWide || depthBufferDown != CoreDepthPyramid.DepthTall ) )
		{
			CoreRenderDevice.DestroyPyramid( depthPyramid );
			depthPyramid = 0;
			depthKnown = false;
		}

		if ( depthPyramid == 0 )
		{
			depthBufferSide = CoreDepthPyramid.DepthWide;
			depthBufferDown = CoreDepthPyramid.DepthTall;
			depthPyramid = CoreRenderDevice.CreatePyramid( depthBufferSide, depthBufferDown );
			depthKnown = false;

			if ( depthPyramid == 0 )
				Log.Warning( $"no depth pyramid at {depthBufferSide}x{depthBufferDown}: {CoreRenderDevice.Description}" );
		}

		return depthPyramid;
	}

	//Folds this frame's depth into the pyramid; with ifAppeared the dispatch size comes from the cull
	private void KeepDepth( ulong viewport, bool ifAppeared = false )
	{
		if ( EnsurePyramid() == 0 ) return;

		var args = ifAppeared && meshRender.Ready ? meshRender.WorkBuffer : 0;
		var argsAt = CoreMeshRender.PyramidArgsOffset;

		var depth = CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Depth );
		if ( depth == 0 ) return;

		CoreRenderDevice.ZoneBegin( "depth pyramid" );

		var built = true;

		var levels = CoreDepthPyramid.DepthLevels;
		var (depthWide, depthTall) = size;

		//Where the depth actually is: the world's own picture, or the world's rectangle of the
		//screen's depth buffer where the pre-pass wrote it there - see Swapchain.DepthOnScreen.
		var read = CoreRenderDevice.DepthReadRect( viewport );


		for ( var lod = 0; lod < levels && built; lod += CoreDepthPyramid.LevelsAtOnce )
		{
			var count = System.Math.Min( CoreDepthPyramid.LevelsAtOnce, levels - lod );
			var wide = System.Math.Max( 1, CoreDepthPyramid.DepthWide >> lod );
			var tall = System.Math.Max( 1, CoreDepthPyramid.DepthTall >> lod );
			var sourceWide = lod == 0 ? depthWide : System.Math.Max( 1, CoreDepthPyramid.DepthWide >> ( lod - 1 ) );
			var sourceTall = lod == 0 ? depthTall : System.Math.Max( 1, CoreDepthPyramid.DepthTall >> ( lod - 1 ) );

			BitConverter.TryWriteBytes( depthPush.AsSpan( 0 ), (uint)wide );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 4 ), (uint)tall );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 8 ), (uint)count );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 12 ), lod == 0 ? 1u : 0u );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 16 ), (uint)sourceWide );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 20 ), (uint)sourceTall );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 24 ), (uint)System.Math.Max( 0, lod - 1 ) );

			//Only the first level reads beyond the pyramid, and only it may read part of a bigger picture
			var from = lod == 0 ? read : (X: 0, Y: 0, Wide: sourceWide, Tall: sourceTall);

			BitConverter.TryWriteBytes( depthPush.AsSpan( 28 ), (uint)from.X );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 32 ), (uint)from.Y );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 36 ), (uint)from.Wide );
			BitConverter.TryWriteBytes( depthPush.AsSpan( 40 ), (uint)from.Tall );

			built = CoreRenderDevice.BuildPyramid( pyramidPipeline, depthPyramid, lod, count, lod == 0 ? depth : 0, depthPush,
				args, argsAt + ( ( lod / CoreDepthPyramid.LevelsAtOnce ) * 16 ) );
		}

		CoreRenderDevice.ZoneEnd();

		if ( built ) depthKnown = true;
	}

	//Draws movers and what posed itself anew into the velocity picture, now over then, and says whether it drew,
	//as an idle frame leaves another frame's contents
	private bool DrawVelocities( ulong viewport )
	{
		if ( velocityPipeline == 0 || tables is null ) return false;

		var turn = EngineLoop.LoopFrames;
		var anyPose = false;

		if ( velocityPosedPipeline != 0 )
		{
			foreach ( var slot in tables.PosedSlots )
			{
				if ( tables.EntryAt( slot ) is { PoseMovedTurn: var at } && at == turn ) { anyPose = true; break; }
			}
		}

		var movers = meshRender.Ready && meshRender.AnyMovers();

		if ( tables.Moved.Count == 0 && !anyPose && !movers ) return false;

		CoreRenderDevice.ZoneBegin( "velocity" );

		var drawn = false;

		if ( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Velocity, Color.Transparent ) )
		{
			drawn = true;

			var push = velocityPush;

			foreach ( var slot in tables.Moved )
			{
				var entry = tables.EntryAt( slot );
				if ( entry?.Mesh is null || entry.Mesh.Pulled || PosedVelocity( entry ) ) continue;

				ref var record = ref tables.RecordAt( slot );
				if ( record.Prev == record.World ) continue;

				MemoryMarshal.Write( push.AsSpan(), in record.World );
				MemoryMarshal.Write( push.AsSpan( 64 ), in record.Prev );

				foreach ( var part in entry.Mesh.Meshes )
				{
					if ( part.StandsIn ) continue;

					CoreRenderDevice.DrawIndexed( velocityPipeline, entry.Mesh.Vertices, entry.Mesh.Indices,
						part.IndexCount, push, part.FirstIndex, part.FirstVertex );
				}
			}

			foreach ( var slot in tables.PosedSlots )
			{
				var entry = tables.EntryAt( slot );
				if ( entry is null || !PosedVelocity( entry ) ) continue;

				ref var record = ref tables.RecordAt( slot );
				var poseMoved = entry.PoseMovedTurn == turn;

				if ( !poseMoved && record.Prev == record.World ) continue;

				DrawPosedVelocity( entry, in record, poseMoved );
			}

			if ( movers ) meshRender.DrawMoverVelocity();

			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.ZoneEnd();

		return drawn;
	}

	private readonly byte[] velocityPush = new byte[128];
	private readonly ulong[] velocityBuffers = new ulong[2];

	//Whether a thing's velocity comes from its posed positions: a mesh drawn from them, posed into the range it has now
	private static bool PosedVelocity( CoreSceneTables.Entry entry )
		=> velocityPosedPipeline != 0 && entry.Live && entry.Posed && entry.Mesh is { Pooled: true } &&
			entry.PosedAt >= 0 && entry.SkinnedAt == entry.PosedAt;

	//Each part out of the posed range, moved from the pose before where it posed anew this turn, else from where it stands
	private void DrawPosedVelocity( CoreSceneTables.Entry entry, in CoreSceneTables.InstanceRecord record, bool poseMoved )
	{
		var mesh = entry.Mesh;
		var positions = CoreGeometryPool.Vertices.Side( CoreGeometryPool.PositionSide );
		if ( positions == 0 || mesh.Indices == 0 ) return;

		var was = poseMoved && entry.WasAt >= 0 && CoreSceneTables.Was.Buffer != 0;

		velocityBuffers[0] = positions;
		velocityBuffers[1] = was ? CoreSceneTables.Was.Buffer : positions;

		var words = MemoryMarshal.Cast<byte, float>( velocityPush.AsSpan() );
		Columns( words, 0, in record.World );
		Columns( words, 12, in record.Prev );

		var offsets = MemoryMarshal.Cast<byte, uint>( velocityPush.AsSpan() );

		foreach ( var part in mesh.Meshes )
		{
			var local = part.FirstVertex - mesh.PoolVertexAt;
			if ( part.StandsIn || part.IndexCount <= 0 || local < 0 || local >= mesh.PoolVertexCount ) continue;

			offsets[24] = (uint)( entry.PosedAt + local );
			offsets[25] = (uint)( ( was ? entry.WasAt : entry.PosedAt ) + local );

			var draw = new CoreBufferDraw
			{
				Pipeline = velocityPosedPipeline,
				Indices = mesh.Indices,
				WideIndices = true,
				First = part.FirstIndex,
				Count = part.IndexCount,
			};

			CoreRenderDevice.DrawBuffers( draw, velocityPush, velocityBuffers );
		}
	}

	//An affine row-vector matrix's first three columns, a float4 each - see Affine in velocity.slang
	private static void Columns( Span<float> into, int at, in System.Numerics.Matrix4x4 m )
	{
		into[at + 0] = m.M11; into[at + 1] = m.M21; into[at + 2] = m.M31; into[at + 3] = m.M41;
		into[at + 4] = m.M12; into[at + 5] = m.M22; into[at + 6] = m.M32; into[at + 7] = m.M42;
		into[at + 8] = m.M13; into[at + 9] = m.M23; into[at + 10] = m.M33; into[at + 11] = m.M43;
	}

	//A copy of this frame's block seeing the world through viewProjection rather than the jittered matrix it was
	//drawn through, put beside it for the overlays - see CoreRenderDevice.PutFrame. Nought where it could not be.
	private ulong PutPlainFrame( in System.Numerics.Matrix4x4 viewProjection )
	{
		frameNow.AsSpan( 0, lightFrame.Length ).CopyTo( lightFrame );
		CoreSceneRenderer.SetViewMatrix( lightFrame, viewProjection );

		return CoreRenderDevice.PutFrame( lightFrame.AsSpan( 0, CoreSceneRenderer.EffectsAt ) );
	}

	//The vertical field of view a perspective projection was made with, in radians - nought for a flat one.
	private static float VerticalField( in System.Numerics.Matrix4x4 projection )
		=> projection.M34 == 0.0f || projection.M22 == 0.0f ? 0.0f : 2.0f * MathF.Atan( 1.0f / MathF.Abs( projection.M22 ) );

	//This drawer's upscaler and the history it keeps - see CoreUpscaler.
	private readonly CoreUpscaler upscaler = new();

	//Whether world passes are built for two views; light views stay single-view (WebGL refuses otherwise)
	private bool StereoPass => BothEyes && !inLightView && CoreRender.StereoPipelines;

	//Whether current draws are a light view's, which must use single-view programs inside a stereo frame
	private bool inLightView;

	//Classic pipelines are specialized to what the frame holds (fog, lamps, debug), one per answer
	private CoreSceneRenderer.PipelineKind Wired( in CoreSceneRenderer.PipelineKind kind )
	{
		var wired = wireframe == 0
			? kind with { Specialize = CoreSceneRenderer.ShadeSpecialize() }
			: kind with { Wireframe = true, Through = wireframe >= 2 && !wantNearWins };

		return StereoPass ? wired with { Stereo = true } : wired;
	}

	private bool wantNearWins;

	//Whether this frame's cull tests against depth, and so whether folding the depth is worth it - see KeepDepth
	private bool occluding;

	//Distance slices of still clusters drawn in the depth pre-pass; 0 none, MaxValue all (see DepthTuner)
	internal static uint PrepassSlices { get; private set; } = uint.MaxValue;

	//Draws a queue the classic way, with runs of copies drawn once each (portable instanced draw)
	private void SubmitRuns( ReadOnlySpan<Queued> queue, ReadOnlySpan<bool> takes, bool prepass )
	{
		runOf.Clear();

		foreach ( var members in runMembers ) members.Clear();

		if ( runIndex.Length < queue.Length )
		{
			runIndex = new int[System.Math.Max( queue.Length, 64 )];
			runPipeline = new ulong[runIndex.Length];
		}

		var used = 0;

		for ( var i = 0; i < queue.Length; i++ )
		{
			runIndex[i] = -1;

			if ( !takes[i] || !Runnable( in queue[i] ) ) continue;

			ref readonly var queued = ref queue[i];

			//The pipeline this run would be drawn with, which is also what makes two draws the same kind. A run
			//nobody baked an instanced stage for answers nought here and its draws go one at a time, as before.
			var baked = CoreResources.Pipeline( KindOf( in queued, prepass ) with { Instanced = true } );

			if ( baked == 0 ) continue;

			var key = new RunKey( queued.Mesh.Vertices, queued.Mesh.Indices, queued.Part.FirstIndex,
				queued.Part.IndexCount, queued.Part.FirstVertex, queued.Part.Texture, baked );

			if ( !runOf.TryGetValue( key, out var at ) )
			{
				at = used++;

				if ( at == runMembers.Count ) runMembers.Add( new List<int>() );

				runOf[key] = at;
			}

			runMembers[at].Add( i );
			runIndex[i] = at;
			runPipeline[i] = baked;
		}

		for ( var i = 0; i < queue.Length; i++ )
		{
			if ( !takes[i] ) continue;

			var at = runIndex[i];

			if ( at < 0 )
			{
				Submit( in queue[i], prepass );
				continue;
			}

			var members = runMembers[at];

			//Not the first of its run: it was drawn with the first.
			if ( members.Count > 1 && members[0] != i ) continue;

			if ( members.Count < 2 )
			{
				Submit( in queue[i], prepass );
				continue;
			}

			if ( SubmitRun( queue, members, i, runPipeline[i] ) ) continue;

			//The run would not draw - the frame had no room in its ring, say. Its copies are drawn one at a time here,
			//because the second pass below skips every member but the first: without this the field would be one block.
			foreach ( var member in members ) Submit( in queue[member], prepass );
		}
	}

	//One run, in blocks of as many copies as a ring slot holds. False where the frame would not take
	//it - then the caller draws the copies one at a time, which is what it did before there were runs.
	private bool SubmitRun( ReadOnlySpan<Queued> queue, List<int> members, int first, ulong baked )
	{
		var perBlock = CoreRenderDevice.BlockBytes / CoreSceneRenderer.PushSize;
		if ( baked == 0 || perBlock < 2 ) return false;

		ref readonly var head = ref queue[first];

		var mesh = head.Mesh;
		var part = head.Part;

		System.Span<byte> pass = stackalloc byte[CoreSceneRenderer.PushSize];
		pass.Clear();

		var block = runBlock.AsSpan();

		for ( var at = 0; at < members.Count; at += perBlock )
		{
			var copies = System.Math.Min( perBlock, members.Count - at );

			for ( var c = 0; c < copies; c++ )
			{
				var push = PushOf( queue[members[at + c]].PushAt );
				var into = block.Slice( c * CoreSceneRenderer.PushSize, CoreSceneRenderer.PushSize );

				into.Clear();
				push[..System.Math.Min( push.Length, into.Length )].CopyTo( into );
			}

			if ( !CoreRenderDevice.DrawIndexedInstancedBlock( baked, mesh.Vertices, mesh.Indices,
				part.IndexCount, copies, block[..( copies * CoreSceneRenderer.PushSize )], pass,
				part.FirstIndex, part.FirstVertex, part.Texture ) )
			{
				//None of this block drew: at the first the caller falls back; later, one by one would redraw copies
				return at > 0;
			}
		}

		return true;
	}

	//The pipeline kind a draw would be submitted under - see Submit, which works it out the same way.
	private CoreSceneRenderer.PipelineKind KindOf( in Queued queued, bool prepass )
		=> prepass
			? queued.Kind with { DepthOnly = true, Translucent = false, Target = CoreRender.TargetKind.Depth,
				Stereo = StereoPass }
			: Wired( queued.Kind with { Target = worldTarget, Finishes = worldFinishes, ScreenChain = worldFinishes ? worldChain : null } );

	private void Submit( in Queued queued, bool prepass = false )
	{
		var kind = prepass
			? queued.Kind with { DepthOnly = true, Translucent = false, Target = CoreRender.TargetKind.Depth,
				Stereo = StereoPass }
			: Wired( queued.Kind with { Target = worldTarget, Finishes = worldFinishes, ScreenChain = worldFinishes ? worldChain : null } );

		//A pass carrying the frame's point effects still compiling for this one: the
		//curve alone meanwhile. And one the frame is waiting on, asked for here too.
		if ( !prepass )
		{
			if ( kind.ScreenChain is not null && CoreResources.Pipeline( kind ) == 0 )
			{
				CoreMeshRender.ChainWaiting++;
				kind = kind with { ScreenChain = null };
			}
			else if ( warmChain is not null && !queued.Kind.Translucent &&
				CoreResources.Pipeline( Wired( queued.Kind with { Target = CoreRender.TargetKind.Screen, Finishes = true, ScreenChain = warmChain } ) ) == 0 )
			{
				CoreMeshRender.ChainWaiting++;
			}
		}

		SubmitAs( queued, kind );
	}

	//The same with the pipeline given, for a pass drawing a named thing its own way - see DrawRendererNow
	private void SubmitAs( in Queued queued, in CoreSceneRenderer.PipelineKind kind )
	{
		//Posed ahead, it is drawn still and its posing pipeline is never asked for
		if ( queued.Kind.Morphed && queued.Instances <= 1 && queued.CountFrom == 0 && DrawPosed( in queued, in kind, PushOf( queued.PushAt ) ) ) return;

		var baked = CoreResources.Pipeline( kind );
		if ( baked == 0 ) return;

		var push = PushOf( queued.PushAt );

		if ( queued.Instances > 1 || queued.CountFrom != 0 )
		{
			SubmitCopies( in queued, baked, push );
			return;
		}

		if ( queued.Mesh.Pulled )
		{
			CoreRenderDevice.DrawPulled( baked, queued.Mesh.Vertices, queued.Mesh.Data,
				queued.Part.PulledVertices, push, queued.Part.Texture );
			return;
		}

		if ( queued.Skinned )
		{
			var bones = CoreRenderDevice.Bones( queued.Pose );

			if ( bones != 0 && queued.Morphs is not null && queued.Mesh.MorphDeltas != 0 )
			{
				var weights = CoreRenderDevice.MorphWeights( queued.Morphs );

				if ( weights != 0 )
				{
					CoreRenderDevice.DrawMorphedAt( baked, queued.Mesh.Vertices, queued.Mesh.Skin,
						queued.Mesh.MorphRuns, queued.Mesh.MorphDeltas, queued.Mesh.Indices,
						queued.Part.IndexCount, bones, weights, push,
						queued.Part.FirstIndex, queued.Part.FirstVertex, queued.Part.Texture,
						FirstRun( in queued ) );
					return;
				}
			}

			if ( bones != 0 )
			{
				CoreRenderDevice.DrawSkinnedAt( baked, queued.Mesh.Vertices, queued.Mesh.Skin, queued.Mesh.Indices,
					queued.Part.IndexCount, bones, push,
					queued.Part.FirstIndex, queued.Part.FirstVertex, queued.Part.Texture );
				return;
			}

			CoreRenderDevice.DrawSkinned( baked, queued.Mesh.Vertices, queued.Mesh.Skin, queued.Mesh.Indices,
				queued.Part.IndexCount, queued.Pose, push,
				queued.Part.FirstIndex, queued.Part.FirstVertex, queued.Part.Texture );
			return;
		}

		if ( queued.Morphs is not null && queued.Mesh.MorphDeltas != 0 )
		{
			var weights = CoreRenderDevice.MorphWeights( queued.Morphs );

			if ( weights != 0 )
			{
				CoreRenderDevice.DrawMorphedIndexed( baked, queued.Mesh.Vertices,
					queued.Mesh.MorphRuns, queued.Mesh.MorphDeltas, queued.Mesh.Indices,
					queued.Part.IndexCount, weights, push,
					queued.Part.FirstIndex, queued.Part.FirstVertex, queued.Part.Texture,
					FirstRun( in queued ) );
				return;
			}
		}

		CoreRenderDevice.DrawIndexed( baked, queued.Mesh.Vertices, queued.Mesh.Indices,
			queued.Part.IndexCount, push,
			queued.Part.FirstIndex, queued.Part.FirstVertex, queued.Part.Texture );
	}


	//The portable frame's pose pass - see engine/classic_pose.slang; nought where there is none
	private static ulong posePipeline;
	private static bool poseTried;

	//Morphed models posed once by compute before the view's first pass, so each of its passes - the lamps' faces, the
	//world - draws them as still meshes rather than adding up their shapes and bones in its vertex stage again
	private void PoseAhead()
	{
		if ( !CoreRenderDevice.PortableFrame || !CoreRender.DrawsMorphs ) return;

		var begun = false;

		foreach ( var list in (ReadOnlySpan<List<Queued>>)[opaque, translucent, overlayLayer, afterUiLayer] )
		{
			foreach ( ref readonly var queued in CollectionsMarshal.AsSpan( list ) )
			{
				if ( !queued.Kind.Morphed || queued.Instances > 1 || queued.Mesh.MorphDeltas == 0 || queued.Mesh.MorphVertexCount <= 0 ) continue;

				if ( !poseTried )
				{
					poseTried = true;

					var code = CoreShaderCache.Pass( EngineShaders.ClassicPose, "main", CoreShaderCompiler.Stage.Compute );
					posePipeline = code is null ? 0 : CoreRenderDevice.CreatePoseCompute( code );

					if ( posePipeline == 0 ) Log.Warning( $"the pose pass did not compile, morphed models pose in every pass: {CoreRenderDevice.ShaderError}" );
				}

				if ( posePipeline == 0 ) return;

				if ( !begun )
				{
					CoreRenderDevice.ZoneBegin( "pose" );
					begun = true;
				}

				CoreRenderDevice.PoseAhead( posePipeline, queued.Mesh, queued.Skinned ? queued.Pose : null, queued.Morphs, out _, out _ );
			}
		}

		if ( begun ) CoreRenderDevice.ZoneEnd();
	}

	//A morphed part drawn still out of what PoseAhead posed, with its material's pipeline for a still mesh. False where it was
	//not posed or that pipeline is not there, and it is drawn posing in its vertex stage.
	private static bool DrawPosed( in Queued queued, in CoreSceneRenderer.PipelineKind kind, ReadOnlySpan<byte> push )
	{
		if ( queued.Morphs is null || !CoreRenderDevice.PosedFor( queued.Morphs, out var room, out var first ) ) return false;

		var still = CoreResources.Pipeline( kind with { Skinned = false, Morphed = false } );
		if ( still == 0 ) return false;

		CoreRenderDevice.DrawIndexed( still, room, queued.Mesh.Indices, queued.Part.IndexCount, push,
			queued.Part.FirstIndex, first + queued.Part.FirstVertex, queued.Part.Texture );

		return true;
	}

	//Where a part's vertices start in its own model, which is where its blend shape runs start: the runs are the
	//model's, one per vertex from nought, while the part's first vertex counts from the start of the shared pool.
	private static int FirstRun( in Queued queued )
		=> queued.Mesh.Pooled ? queued.Part.FirstVertex - (int)queued.Mesh.PoolVertexAt : queued.Part.FirstVertex;

	//A part drawn as copies, counted here or by the card from a project buffer (PrepareCounted); rigid, copies
	//told apart by SV_InstanceID
	private static void SubmitCopies( in Queued queued, ulong pipeline, ReadOnlySpan<byte> push )
	{
		var mesh = queued.Mesh;
		var part = queued.Part;

		var draw = new CoreBufferDraw
		{
			Pipeline = pipeline,
			Texture = part.Texture,
			Instances = Math.Max( queued.Instances, 1 ),
		};

		if ( mesh.Pulled )
		{
			draw.Geometry = mesh.Vertices;
			draw.Data = mesh.Data;
			draw.Count = part.PulledVertices;
		}
		else
		{
			draw.Vertices = mesh.Vertices;
			draw.Indices = mesh.Indices;
			draw.WideIndices = true;
			draw.First = part.FirstIndex;
			draw.Count = part.IndexCount;
			draw.BaseVertex = part.FirstVertex;
		}

		if ( queued.CountFrom != 0 )
		{
			//Its arguments were never put, and an unreadable count is not guessed
			if ( queued.ArgsAt < 0 || queued.ArgsBuffer == 0 ) return;

			draw.Arguments = queued.ArgsBuffer;
			draw.ArgumentsOffset = queued.ArgsAt;
			draw.DrawCount = 1;
		}

		CoreRenderDevice.DrawBuffers( draw, push );
	}

	//Arguments of every part whose copies a project buffer counts, put before any pass opens - see CoreInstanceArgs
	private static void PrepareCounted( List<Queued> queue )
	{
		var parts = CollectionsMarshal.AsSpan( queue );

		for ( var i = 0; i < parts.Length; i++ )
		{
			ref var queued = ref parts[i];

			if ( queued.CountFrom == 0 || queued.ArgsAt >= 0 ) continue;

			var pulled = queued.Mesh.Pulled;

			if ( CoreInstanceArgs.Put( !pulled,
				pulled ? queued.Part.PulledVertices : queued.Part.IndexCount,
				pulled ? 0 : queued.Part.FirstIndex,
				pulled ? 0 : queued.Part.FirstVertex,
				queued.CountFrom, queued.CountAt, out var buffer, out var at ) )
			{
				queued.ArgsBuffer = buffer;
				queued.ArgsAt = at;
			}
		}
	}

	//A model's parts queued to be drawn now rather than with the frame, one set for each place it stands - see
	//CoreListDraws.Model. What the draw set for itself is read under the names each part's shader declared.
	internal bool QueueModelNow( CoreSceneRenderer.SceneMesh mesh, ReadOnlySpan<Transform> transforms, RenderAttributes attributes,
		int instances = 0, ulong countFrom = 0, long countAt = 0, int level = -1 )
	{
		named.Clear();

		if ( mesh is null ) return false;

		foreach ( var transform in transforms )
		{
			QueueMesh( mesh, transform, Color.White, ulong.MaxValue, lastEye, inMotion: true, into: named,
				instances: instances, countFrom: countFrom, countAt: countAt, attributes: attributes, level: level );
		}

		return countFrom != 0 && named.Count > 0;
	}

	//The arguments of what QueueModelNow queued, outside every pass.
	internal void PrepareNamed() => PrepareCounted( named );

	//What QueueModelNow queued, into the open target: solid parts, then see-through ones far to near
	internal void SubmitNamed( CoreRender.TargetKind target, int pictureFormat, bool noDepth, bool finishes )
	{
		for ( var pass = 0; pass < 2; pass++ )
		{
			if ( pass == 1 ) FurthestFirst( named, namedOrder );

			for ( var at = 0; at < named.Count; at++ )
			{
				var queued = named[pass == 1 ? namedOrder[at] : at];

				if ( queued.Kind.Translucent != ( pass == 1 ) ) continue;

				SubmitAs( queued, queued.Kind with
				{
					Target = target,
					PictureFormat = pictureFormat,
					NoDepth = noDepth,
					Finishes = finishes,
					ScreenChain = null,
					DepthOnly = false,
				} );
			}
		}

		named.Clear();
	}

	private static readonly List<(int Order, int At, Rinten.Rendering.CommandList List)> curveLists = new();

	//The camera's tonemap curve, and whether the engine curve is all that's at the tonemap stage
	private static bool CurveOf( CameraComponent camera, out float mode )
	{
		mode = 0.0f;

		if ( camera is null ) return true;

		//No post processing is no curve either - see EnablePostProcessing.
		if ( !camera.EnablePostProcessing && !camera.KeepsCurveWithoutPost )
		{
			mode = (float)(int)Tonemapping.TonemappingMode.Linear;
			return true;
		}

		curveLists.Clear();
		camera.PostProcess?.ListsAt( Rinten.Rendering.Stage.Tonemapping, curveLists );
		camera.CommandListsAt( Rinten.Rendering.Stage.Tonemapping, curveLists );

		var alone = true;

		foreach ( var (_, _, list) in curveLists )
		{
			if ( list.OnlyApply( out var material, out var attributes ) &&
				material?.core?.Shader is { } shader &&
				shader.Replace( '\\', '/' ).EndsWith( CorePointChain.CurveShader, StringComparison.OrdinalIgnoreCase ) )
			{
				mode = attributes?.GetFloat( "Mode", 0.0f ) ?? 0.0f;
				continue;
			}

			alone = false;
		}

		return alone;
	}

	//Point effects the world's shading or the portable finish can carry instead of a compose; null if anything else
	//is hung - other than, with passes, what the portable frame draws as passes before its finish (RunPortablePasses)
	private string FoldChain( CameraComponent camera, bool passes )
	{
		foldPoints.Clear();

		if ( camera?.PostProcess is null ) return null;

		foreach ( var stage in passes ? System.Array.Empty<Rinten.Rendering.Stage>() : unfolded )
		{
			foldLists.Clear();
			camera.PostProcess.ListsAt( stage, foldLists );
			camera.CommandListsAt( stage, foldLists );

			foreach ( var (_, _, list) in foldLists )
			{
				if ( !list.IsEmpty ) return null;
			}
		}

		foreach ( var stage in folded )
		{
			foldLists.Clear();
			camera.PostProcess.ListsAt( stage, foldLists );
			camera.CommandListsAt( stage, foldLists );
			foldLists.Sort( static ( a, b ) => a.Order != b.Order ? a.Order.CompareTo( b.Order ) : a.At.CompareTo( b.At ) );

			foreach ( var (order, _, list) in foldLists )
			{
				if ( list.IsEmpty ) continue;

				if ( !list.OnlyApply( out var material, out var attributes ) ||
					material?.core?.Shader is not { } shader || !CorePointChain.IsPoint( shader ) )
				{
					if ( passes ) continue;
					return null;
				}

				foldPoints.Add( new CorePointChain.Point( material, attributes, null, shader, stage, order ) );
			}
		}

		if ( foldPoints.Count == 0 ) return null;

		CorePointChain.WithCurve( foldPoints, frameCurve );

		return CorePointChain.Foldable( CollectionsMarshal.AsSpan( foldPoints ) ) ? CorePointChain.ScreenKeyFor( CollectionsMarshal.AsSpan( foldPoints ) ) : null;
	}

	//Whether anything the portable frame draws as a pass over the world is hung before the compose; a point effect is carried
	private bool PortablePasses( CameraComponent camera )
	{
		if ( camera?.PostProcess is null ) return false;

		foreach ( var stage in portableStages )
		{
			foldLists.Clear();
			camera.PostProcess.ListsAt( stage, foldLists );
			camera.CommandListsAt( stage, foldLists );

			foreach ( var (_, _, list) in foldLists )
			{
				if ( list.IsEmpty ) continue;
				if ( list.OnlyApply( out var material, out _ ) && material?.core?.Shader is { } shader && CorePointChain.IsPoint( shader ) ) continue;

				return true;
			}
		}

		return false;
	}

	//The portable frame's passes over the world drawn aside: the picture the finish reads, and whether it is light
	private (ulong Reads, bool Light) RunPortablePasses( CameraComponent camera, float near, float far )
	{
		var (width, height) = glow.PassSize;

		//The world's pass closed: the passes read what it drew
		CoreRenderDevice.WorldAside( false, out _, out _ );

		CorePostBlit.BeginPortable( new CorePostBlit.Frame( CoreRender.TargetKind.Picture, 1, 0, 0, 0,
			width, height, near, far, 0.0f, viewport ), glow.World, glow.Passes );

		try
		{
			CoreRenderDevice.ZoneBegin( "post pass" );

			foreach ( var stage in portableStages )
			{
				if ( !Hung( camera, stage ) ) continue;

				CorePostBlit.PortableStage( stage );
				RunLists( camera, stage );
			}

			return CorePostBlit.PortableSettled;
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, "a post-process in the portable frame" );
			return CorePostBlit.PortableSettled;
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
			CorePostBlit.End();
		}
	}

	//What the portable frame runs over the world before its finish: the stages before the compose, and the compose's own
	private static readonly Rinten.Rendering.Stage[] portableStages =
	{
		Rinten.Rendering.Stage.AfterTransparent, Rinten.Rendering.Stage.AfterViewmodel,
		Rinten.Rendering.Stage.BeforePostProcess, Rinten.Rendering.Stage.Tonemapping,
	};

	//The stages that must hold nothing for a run to be carried, and the ones it is laid at.
	private static readonly Rinten.Rendering.Stage[] unfolded =
	{
		Rinten.Rendering.Stage.AfterDepthPrepass, Rinten.Rendering.Stage.AfterPostProcess,
	};

	private static readonly Rinten.Rendering.Stage[] folded =
	{
		Rinten.Rendering.Stage.AfterOpaque, Rinten.Rendering.Stage.AfterSkybox, Rinten.Rendering.Stage.AfterTransparent,
		Rinten.Rendering.Stage.AfterViewmodel, Rinten.Rendering.Stage.BeforePostProcess, Rinten.Rendering.Stage.Tonemapping,
	};

	//The curve this frame names - see CurveOf - for the passes that finish the picture themselves.
	private float frameCurve;

	//The point effects the compose is compiled with this frame, kept rather than made.
	private readonly List<CorePointChain.Point> composeChain = new();


	//Where this viewport was looking the frame before, and whether there was one. Per drawer,
	//which is per viewport: two views of one scene are two cameras and each has its own last frame.
	private System.Numerics.Matrix4x4 previousViewProjection;
	private bool hasPrevious;

	//Which turn of the hashed cut-outs this frame is - see SetHashedCut
	private int hashedTurn;

	//Pipelines for viewport furniture (lines, gizmos, billboards, UI); static so previews reuse them
	private static ulong linePipeline;
	private static ulong surfaceGridPipeline;
	private static ulong overlayPipeline;
	private static ulong gizmoLines;
	private static ulong gizmoLinesOverlay;
	private static ulong gizmoTriangles;
	private static ulong gizmoTrianglesOverlay;
	private static ulong billboardPipeline;

	//Two-view builds of the pipelines a headset draws with; 0 where this build has none
	private static ulong gizmoLinesTwo;
	private static ulong gizmoLinesOverlayTwo;
	private static ulong gizmoTrianglesTwo;
	private static ulong gizmoTrianglesOverlayTwo;
	private static ulong billboardTwo;

	//Overlay billboards, screen text and shapes, world panels and head sheets, all built twice for a headset;
	//a screen-space draw into both eyes is one picture, as a mode-nought push says
	private static ulong screenTwo;
	private static ulong uiWorldTwo;
	private static ulong uiSheetTwo;

	//Whether every pipeline a both-eyes pass needs has a two-view build; else draw an eye at a time
	internal static bool StereoReady =>
		gizmoLinesTwo != 0 && gizmoLinesOverlayTwo != 0 &&
		gizmoTrianglesTwo != 0 && gizmoTrianglesOverlayTwo != 0 &&
		billboardTwo != 0 && screenTwo != 0 && uiWorldTwo != 0 && uiSheetTwo != 0;

	//The build of a pipeline this pass wants: the two-view one where both eyes are
	//being drawn at once and there is one, and the ordinary one everywhere else.
	private ulong Pick( ulong one, ulong two ) => BothEyes && two != 0 ? two : one;
	private static ulong screenPipeline;
	private static ulong uiPipeline;

	//The UI shader with depth test on, for a panel in the world: behind a wall when the wall is in front
	private static ulong uiWorldPipeline;

	//The world build without depth test, for a screen panel before a headset's eyes - see DrawHeadsetPanels
	private static ulong uiSheetPipeline;

	//UI vertex stride taken from the struct, so it can't drift from the layout again
	private static readonly int UiVertexStride =
		System.Runtime.InteropServices.Marshal.SizeOf<CoreUiRenderer.UiVertex>();

	//Sky pipelines keyed by shader, target, specialization (night/day/cloud/fog) and stereo
	private static readonly Dictionary<(string Shader, CoreRender.TargetKind Target, int Specialize, bool Stereo), ulong> skyPipelines = new();

	//The sky material this frame draws with - its shader, and its numbers on the device
	//- read off the scene at the top of the frame. See CoreSceneRenderer.SkyMaterialOf.
	private string skyShader = CoreSceneRenderer.SkyShader;
	private ulong skyDevice;

	//This frame's sky where it is a panorama, whose table is made from its picture rather than drawn - see SkyTableSlot
	private CorePanoramaSky.Sky skyPanorama;

	//The sky's light on the card for this drawing's views - see CoreSkyLight
	private readonly CoreSkyLight skyLight = new();

	//The portable frame's bloom and passes, and whether this frame draws the world aside for them - see CorePortableGlow
	private readonly CorePortableGlow glow = new();
	private bool aside;

	//Drawn aside only for its edges to be softened: the world finishes itself, and the finish writes it out as it is
	private bool asideFinished;

	//Last frame's run for the world's shading: where its surfaces had no build of it, the finish carries it from the next frame
	private string lastFoldKey;

	//The sky table's bindless index, one as a flag in the portable frame, nought for none
	private int SkyTableSlot()
	{
		if ( skyPanorama?.Table is { } )
		{
			var table = CorePanoramaSky.TableTexture( skyPanorama );
			CoreRenderDevice.UseSkyTable( table );

			return table == 0 ? 0 : CoreRenderDevice.PortableFrame ? 1 : Indexed( table );
		}

		CoreRenderDevice.UseSkyTable( 0 );

		return SkyTablePipeline( skyShader ) == 0 ? 0
			: CoreRenderDevice.PortableFrame ? 1
			: Indexed( CoreRenderDevice.SkyTableTexture );
	}

	//The same material as its numbers stand on this side - what a fold is read off; see CoreResources.FoldBits.
	private CoreMaterial skyCore;

	//The camera's background colour where a scene has no sky - see shaders/engine/background.slang.
	private static ulong backgroundPipeline;

	private static ulong velocityPipeline;

	//The same for a posed thing, out of its posed positions and the ones before - see PosedVs in velocity.slang
	private static ulong velocityPosedPipeline;

	//The pass that turns the frame's depth into what culls the next one.
	private static ulong pyramidPipeline;

	//Pipelines that fill the sky table, one per sky shader, built around its SkyLight
	private static readonly Dictionary<string, ulong> skyTablePipelines = new();

	//This drawer's casters and sun/lamp shadows; per drawer, as pages hold the world around one camera
	private readonly CoreCasters casters = new();

	private readonly CoreSunPages sunPages;
	private readonly CoreLampMaps lampMaps;

	//The sun's one map in the portable frame, which has neither of those - see RenderTier.

	//The classic draws a light's view takes, handed over once rather than as a delegate a frame - see DrawLooseView
	private readonly CoreMeshRender.LooseView looseView;

	//This frame's block as it went up, and the copy of it a light's view draws the classic draws with.
	private float[] frameNow;
	private readonly float[] lightFrame = new float[CoreSceneRenderer.FrameFloats];

	internal CoreSceneDrawer()
	{
		sunPages = new CoreSunPages( casters );
		lampMaps = new CoreLampMaps( casters );
		looseView = DrawLooseView;
	}

	private static ulong composePipeline;

	//The same pass, drawing into the picture the effects after it read rather than onto the window - see RunStage and
	//Stage. AfterPostProcess. Baked beside the other one and used on a frame where something is hung on that stage.
	private static ulong composeFinalPipeline;

	//All four again with the upscale compiled in, for a frame whose world is drawn smaller than the window - baked
	//the first time one is wanted, because the ordinary frame never wants them. See shaders/engine/compose.slang.
	private static ulong composeCarryPipeline;
	private static ulong composeCarryFinalPipeline;
	private static bool composeCarryTried;

	//Which tonemap curve those two were baked with.
	private static int composeCurve = -1;


	//The picture the compose reads, as a material - see MakePostInputs; window-sized, so rebuilt with it
	private ulong composeInputs;

	//The same, reading the other full-size picture instead of the world's own - see frameIn.
	private ulong composeInputsPost;

	//Which other picture those were built against, or nothing.
	private ulong postBuiltFromPost;

	//Which full-size picture the frame is in now; later passes read from it instead of copying back
	private CoreRender.TargetKind frameIn = CoreRender.TargetKind.Scene;

	//The one shadow push block, written again for every caster in every pass rather than made again. See Submit.

	//World picture handle the materials were built against; a change means they're stale (e.g. resize)
	private ulong postBuiltFrom;

	//The light of a scene with no bake, traced about this view's camera where rays are on - see CoreProbeGrid
	private readonly CoreProbeGrid probeGrid = new();

	//Screen exposure: a fixed number now, no auto-exposure
	private float exposure = Rinten.Rendering.DrawSettings.Exposure;

	//Where the world's draws go this frame: its own picture, or the screen where intermediate passes failed
	private CoreRender.TargetKind worldTarget = CoreRender.TargetKind.Screen;

	//Whether the world's own passes leave the picture finished - the exposure and the curve applied
	//where each pixel is shaded, because no compose runs behind them. See PipelineKind.Finishes.
	private bool worldFinishes = true;

	//The run of point effects the world's own shading carries this frame, or null - see FoldChain.
	private string worldChain;

	//A run the frame could carry but whose passes are still compiling - asked for beside the compose.
	private string warmChain;

	//The last run found with every pass it needs there; a frame that lays it next carries it.
	private string readyChain;

	//This frame's run by its key, where it has one - and the last run the log was told about.
	private string foldKey;
	private string saidChain;

	private readonly List<CorePointChain.Point> foldPoints = new();
	private readonly List<(int Order, int At, Rinten.Rendering.CommandList List)> foldLists = new();

	//The sky onto the screen with a run carried in it, by the run's key.
	private static readonly Dictionary<string, ulong> skyChainPipelines = new( StringComparer.Ordinal );

	//Whether the reason for the world's own picture has been said, and what it was.
	private bool saidReads;
	private bool saidReadsWorld;

	//Wireframe mode this frame (convar and camera, read once); 0 is the normal path
	private int wireframe;

	//Debug view this frame or Normal; camera setting wins over mat_toolsvis when both are set
	private SceneCameraDebugMode debugView;

	private static ulong whiteTexture;

	//A white picture's texture - a material set for a pass that has no material of its own.
	internal static ulong WhiteTexture => whiteTexture;

	//Rasterised text labels by content, kept between frames since the same label repeats every frame
	private static readonly System.Collections.Generic.Dictionary<int, (ulong Texture, Vector2 Size, long Used)> textCache = new();

	//The loop frame ages the text cache by
	private static long textFrame => Rinten.EngineLoop.LoopFrames;

	//The loop frame the old labels were last retired on - once a frame, whoever asks first.
	private static long textRetiredAt = -1;

	//Frames a label must go unused before its texture is freed; well past frames in flight
	private const long TextKeptFor = 30;

	//Engine textures on our device by Texture identity, since UI images are as often generated (atlases,
	//render targets, SVGs) as loaded, with no path to key on
	private static readonly System.Collections.Generic.Dictionary<Texture, ulong> uiTextures = new();

	//The floor's lines, worked out once and drawn by everyone.
	private static LineVertex[] grid;

	//The spacing the cached grid above was built at.
	private static float gridSpacing;

	//And where it was centred, so it is rebuilt when the camera leaves it.
	private static float gridCentreX;
	private static float gridCentreZ;

	//And whether the cells were in it, which a perspective view leaves out.
	private static bool gridCells;

	//World X and Z axis colours as the gizmo hex says (line.slang decodes sRGB itself); opaque on purpose
	private static readonly Color32 AxisX = new( 255, 112, 150, 255 );
	private static readonly Color32 AxisZ = new( 0, 168, 232, 255 );

	//The viewport being drawn and its size, set for one drawing
	private ulong viewport;
	private (int Width, int Height) size;

	//The size of the view being drawn, in its own pixels - what a HUD is laid out in.
	internal (int Width, int Height) ViewSize => size;

	/// <summary>Device-wide resources made once: furniture shaders, icon glyphs, world-to-screen passes.</summary>
	public static bool EnsureResources()
	{
		if ( !CoreNative.Available ) return false;

		if ( linePipeline != 0 ) return true;

		//Registered on first use, so nothing else in the editor need know this exists
		if ( !listening )
		{
			CoreResources.Clearing += ReleaseResources;
			listening = true;
		}

		if ( gizmoLines == 0 )
		{
			Compile( EngineShaders.Gizmo, ( vs, ps ) =>
			{
				gizmoLines = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Lines, blend: true );
				gizmoLinesOverlay = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Lines, blend: true );
				gizmoTriangles = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Triangles, blend: true );
				gizmoTrianglesOverlay = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Triangles, blend: true );
			} );

			CompileStereo( EngineShaders.Gizmo, null, ( vs, ps ) =>
			{
				gizmoLinesTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Lines, blend: true );
				gizmoLinesOverlayTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Lines, blend: true );
				gizmoTrianglesTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Triangles, blend: true );
				gizmoTrianglesOverlayTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Triangles, blend: true );
			} );
		}

		if ( billboardPipeline == 0 )
		{
			Compile( EngineShaders.Billboard, ( vs, ps ) =>
			{
				billboardPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true,
					CoreRender.Topology.Triangles, blend: true );

				//The same shader with screen-space eyes: no depth, always on top - it draws the text.
				screenPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false,
					CoreRender.Topology.Triangles, blend: true );
			} );

			//And the screen's: an overlay billboard draws with it in a headset as anywhere
			CompileStereo( EngineShaders.Billboard, null, ( vs, ps ) =>
			{
				billboardTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true,
					CoreRender.Topology.Triangles, blend: true );

				screenTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false,
					CoreRender.Topology.Triangles, blend: true );
			} );
		}

		if ( uiPipeline == 0 )
		{
			Compile( EngineShaders.Ui, ( vs, ps ) =>
			{
				uiPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Ui, depthTest: false,
					CoreRender.Topology.Triangles, blend: true );
			} );

			//A world panel builds with UI_WORLD, taking its view from the eye (UiPush), so a two-eye pass places it
			//per eye; the screen build reads no frame block, so editor windows bind nothing
			Compile( EngineShaders.Ui, EngineShaders.UiWorld, ( vs, ps ) =>
			{
				//Tested against the world, never written: a panel is a stack of boxes on one plane,
				//and each one writing its depth left the next fighting it for the same pixels.
				uiWorldPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Ui, depthTest: true,
					CoreRender.Topology.Triangles, blend: true, depthWrite: false );

				uiSheetPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Ui, depthTest: false,
					CoreRender.Topology.Triangles, blend: true );
			} );

			CompileStereo( EngineShaders.Ui, EngineShaders.UiWorld, ( vs, ps ) =>
			{
				uiWorldTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Ui, depthTest: true,
					CoreRender.Topology.Triangles, blend: true, depthWrite: false );

				uiSheetTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Ui, depthTest: false,
					CoreRender.Topology.Triangles, blend: true );
			} );

			//The shader always samples; plain boxes sample this and multiply by one.
			Span<byte> white = stackalloc byte[4] { 255, 255, 255, 255 };
			whiteTexture = CoreRenderDevice.CreateTextureRgba( white, 1, 1, srgb: false );
		}

		Compile( EngineShaders.Line, ( vs, ps ) =>
		{
			//The grid respects depth, under the world; selection outlines ignore it, visible through walls
			linePipeline = CoreRenderDevice.CreatePipeline( vs, ps,
				CoreRender.VertexLayoutKind.Line, depthTest: true,
				CoreRender.Topology.Lines, blend: true );

			overlayPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
				CoreRender.VertexLayoutKind.Line, depthTest: false,
				CoreRender.Topology.Lines, blend: true );
		} );

		Compile( EngineShaders.SurfaceGrid, ( vs, ps ) =>
		{
			//The surface's triangles again, depth read not written, both sides, as rooms are worked from inside
			surfaceGridPipeline = CoreRenderDevice.CreatePipeline( vs, ps,
				CoreRender.VertexLayoutKind.Engine, depthTest: true,
				CoreRender.Topology.Triangles, blend: true, depthWrite: false, cullBack: false );
		} );

		MakePostPipelines();
		MakeSkyPipelines();

		//The scene's pipelines are baked on demand. The grid's line pipeline is what is
		//waited for - on a page too, whose export bakes it; see CoreShaderCache.WriteForWeb.
		return linePipeline != 0;
	}

	private static bool listening;

	//Gets both stages of a built-in shader, from the bake cache where present, else compiled via Slang
	private static void Compile( string file, Action<byte[], byte[]> made ) => Compile( file, null, made );

	//A pass built with defines in front of it - one of EngineShaders' variants.
	private static void Compile( string file, string defines, Action<byte[], byte[]> made )
	{
		var vs = CoreShaderCache.Pass( file, "MainVs", CoreShaderCompiler.Stage.Vertex, defines );
		var ps = CoreShaderCache.Pass( file, "MainPs", CoreShaderCompiler.Stage.Pixel, defines );

		if ( vs is not null && ps is not null )
		{
			made( vs, ps );
			return;
		}

		//Passes not meant for the portable frame are skipped silently, not reported as compile failures
		if ( !EngineShaders.InPortableFrame( file ) ) return;

		//Said out loud, once, because the alternative is what happened: a pipeline left at zero, every draw
		//that wanted it skipped without a word, and a feature that looks unimplemented rather than broken.
		Log.Warning( $"{file} did not compile - {( vs is null ? "vertex" : "pixel" )} stage failed. " +
			"Everything drawn with it will be missing." );
	}

	//Two-view build of a pass, browser only; quietly absent in older builds (eyes then drawn one by one)
	private static void CompileStereo( string file, string defines, Action<byte[], byte[]> made )
	{
		if ( !CoreRender.StereoPossible ) return;

		var format = CoreShaderCompiler.Format.GlslStereo;

		var vs = CoreShaderCache.Pass( file, "MainVs", CoreShaderCompiler.Stage.Vertex, defines, format, optional: true );
		var ps = CoreShaderCache.Pass( file, "MainPs", CoreShaderCompiler.Stage.Pixel, defines, format, optional: true );

		if ( vs is not null && ps is not null ) made( vs, ps );
	}

	/// <summary>Lets go of everything held for the device, when the device goes.</summary>
	public static void ReleaseResources()
	{
		CoreRenderDevice.DestroyPipeline( linePipeline );
		CoreRenderDevice.DestroyPipeline( surfaceGridPipeline );
		CoreRenderDevice.DestroyPipeline( overlayPipeline );
		CoreRenderDevice.DestroyPipeline( gizmoLines );
		CoreRenderDevice.DestroyPipeline( gizmoLinesOverlay );
		CoreRenderDevice.DestroyPipeline( gizmoTriangles );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesOverlay );
		CoreRenderDevice.DestroyPipeline( billboardPipeline );
		CoreRenderDevice.DestroyPipeline( screenPipeline );

		//The two-view twins, which are nought on every build that has no such run. DestroyPipeline
		//survives nought, and saying so once is cheaper than five tests - see StereoReady.
		CoreRenderDevice.DestroyPipeline( gizmoLinesTwo );
		CoreRenderDevice.DestroyPipeline( gizmoLinesOverlayTwo );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesTwo );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesOverlayTwo );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesCulled );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesCulledOverlay );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesCulledTwo );
		CoreRenderDevice.DestroyPipeline( gizmoTrianglesCulledOverlayTwo );
		CoreRenderDevice.DestroyPipeline( billboardTwo );
		CoreRenderDevice.DestroyPipeline( screenTwo );
		CoreRenderDevice.DestroyPipeline( uiWorldTwo );
		CoreRenderDevice.DestroyPipeline( uiSheetTwo );
		CoreRenderDevice.DestroyPipeline( uiPipeline );
		CoreRenderDevice.DestroyPipeline( uiWorldPipeline );
		CoreRenderDevice.DestroyPipeline( uiSheetPipeline );
		foreach ( var pipeline in skyPipelines.Values ) CoreRenderDevice.DestroyPipeline( pipeline );
		foreach ( var pipeline in skyTablePipelines.Values ) CoreRenderDevice.DestroyPipeline( pipeline );
		CoreRenderDevice.DestroyPipeline( backgroundPipeline );
		CoreRenderDevice.DestroyPipeline( velocityPipeline );
		CoreRenderDevice.DestroyPipeline( velocityPosedPipeline );
		CoreRenderDevice.DestroyPipeline( pyramidPipeline );
		CoreRenderDevice.DestroyPipeline( composePipeline );
		CoreMeshRender.ReleasePipelines();
		CoreRenderDevice.DestroyPipeline( posePipeline );
		posePipeline = 0;
		poseTried = false;
		CoreFrameGeneration.DestroyPipelines();
		ParticleGpuRun.ForgetPipelines();
		CorePasses.ReleasePipelines();

		foreach ( var made in uiTargetPipelines.Values ) CoreRenderDevice.DestroyPipeline( made );
		uiTargetPipelines.Clear();

		CoreInstanceArgs.Release();
		CoreGpuWork.Release();

		CoreRenderDevice.DestroyTexture( whiteTexture );

		foreach ( var entry in textCache.Values ) CoreRenderDevice.DestroyTexture( entry.Texture );
		foreach ( var spare in textSpare ) CoreRenderDevice.DestroyTexture( spare.Texture );
		foreach ( var uploaded in uiTextures.Values ) CoreRenderDevice.DestroyTexture( uploaded );

		textCache.Clear();
		textSpare.Clear();
		uiTextures.Clear();

		linePipeline = 0;
		surfaceGridPipeline = 0;
		overlayPipeline = 0;
		gizmoLines = 0;
		gizmoLinesOverlay = 0;
		gizmoTriangles = 0;
		gizmoTrianglesOverlay = 0;
		billboardPipeline = 0;
		screenPipeline = 0;
		gizmoLinesTwo = 0;
		gizmoLinesOverlayTwo = 0;
		gizmoTrianglesTwo = 0;
		gizmoTrianglesOverlayTwo = 0;
		gizmoTrianglesCulled = 0;
		gizmoTrianglesCulledOverlay = 0;
		gizmoTrianglesCulledTwo = 0;
		gizmoTrianglesCulledOverlayTwo = 0;
		culledMade = false;
		billboardTwo = 0;
		screenTwo = 0;
		uiWorldTwo = 0;
		uiSheetTwo = 0;
		uiPipeline = 0;
		uiWorldPipeline = 0;
		uiSheetPipeline = 0;
		skyPipelines.Clear();
		skyChainPipelines.Clear();
		skyTablePipelines.Clear();
		CoreSkyLight.ForgetPipelines();
		CoreLight2D.ForgetPipelines();
		CorePortableGlow.ForgetPipelines();
		backgroundPipeline = 0;
		velocityPipeline = 0;
		velocityPosedPipeline = 0;
		pyramidPipeline = 0;
		composePipeline = 0;
		whiteTexture = 0;
	}

	/// <summary>Lets go of the materials naming this viewport's pictures, all this drawing owns.</summary>
	public void Release()
	{
		DropPostInputs();
		skyLight.Release();
		glow.Release();
		upscaler.Release();
		probeGrid.Release();

		for ( var i = 0; i < 2; i++ )
		{
			if ( displayInputs[i] != 0 ) CoreRenderDevice.DestroyTexture( displayInputs[i] );
			displayInputs[i] = 0;
			displayInputFrom[i] = 0;
		}
		meshRender.Release();
		sunPages.Dispose();
		lampMaps.Dispose();

		if ( depthPyramid != 0 ) CoreRenderDevice.DestroyPyramid( depthPyramid );
		depthPyramid = 0;
		depthKnown = false;
	}

	//The last drawn view and its size, for reports that want a picture of it
	internal static (ulong View, int Width, int Height) LastDrawn;

	//The views the cull decides for this frame, the camera first.
	private readonly CoreMeshRender.ViewSetup[] views = new CoreMeshRender.ViewSetup[CoreMeshShaders.MaxViews];
	private static CoreMeshRender.ViewSetup ViewOf( ReadOnlySpan<System.Numerics.Vector4> planes, in System.Numerics.Matrix4x4 worldToClip, Vector3 eye )
	{
		var view = new CoreMeshRender.ViewSetup { WorldToClip = worldToClip, Eye = eye };
		view.SetPlanes( planes );
		return view;
	}

	//The view this drawer's frame is being drawn from - see CameraComponent.RenderView.
	private CameraView eyeView;

	//The headset eye this drawer draws, where it draws one: its place in the world and its four angles,
	//in place of the camera's own place and field - see CoreXr.DrawEyes. Null for every other drawer.
	internal XrEyeView? Eye { get; set; }

	public void Draw( Scene scene, CameraComponent camera, ulong viewport, int width, int height )
	{
		LastDrawn = (viewport, width, height);
		drawingView = viewport;
		if ( !scene.IsValid() || !camera.IsValid() ) return;

		this.viewport = viewport;
		size = (width, height);

		//The frame this thread is inside, for the commands a project records - see Drawing, and CommandList.
		Drawing = this;

		CoreRenderDevice.ZoneBegin( "frame setup" );

		//The components that asked to be called at the render stages, as the scene has them
		//now - see RunLists - and nothing left over from a frame that stopped half way.
		CorePasses.EndFrame();
		CorePostBlit.NewFrame();

		//A new frame is drawn into the world's own picture, wherever the last one ended.
		frameIn = CoreRender.TargetKind.Scene;

		//What the sky is drawn with, and what its table is filled from: the scene's sky material,
		//or the engine's own where the scene names none that could be put on the device.
		var skyMaterial = CoreSceneRenderer.SkyMaterialOf( scene );

		skyDevice = skyMaterial is null ? 0 : CoreResources.Live( skyMaterial );
		skyShader = skyDevice != 0 && !string.IsNullOrWhiteSpace( skyMaterial.core?.Shader )
			? skyMaterial.core.Shader
			: CoreSceneRenderer.SkyShader;

		skyCore = skyDevice == 0 ? null : skyMaterial.core;
		skyPanorama = skyDevice == 0 ? null : CorePanoramaSky.Find( skyMaterial );

		//The project's settings, applied on first draw
		RenderingSettings.EnsureApplied();

		//Before anything reads the shape: views and pyramid must agree
		CoreDepthPyramid.Fit( width, height );

		var gameView = GameViewSource?.Invoke() ?? false;
		var editorOverlays = EditorOverlaysSource?.Invoke() ?? false;

		if ( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene ) != postBuiltFrom )
			MakePostInputs();

		var pixels = size;
		var aspect = pixels.Height > 0 ? pixels.Width / (float)pixels.Height : 1.0f;
		var overlayOnly = camera.OverlayOnly;

		//The camera's view with modifiers, shakes and punches (CameraComponent.RenderView), used below
		eyeView = camera.RenderView;

		//A headset's eye, where there is one: the headset says where it is and how wide it sees, and the camera says
		//everything else - see CoreXr. Both eyes' field at once where both are drawn in one pass - see SecondEye.
		var field = Field;

		if ( field is { } headsetEye )
		{
			eyeView.Position = headsetEye.Position;
			eyeView.Rotation = headsetEye.Rotation;
		}

		var position = eyeView.Position;

		//Kept for passes queuing after the walk - see DrawRendererNow
		lastEye = position;
		var forward = eyeView.Rotation.Forward;
		var up = eyeView.Rotation.Up;

		var view = System.Numerics.Matrix4x4.CreateLookAt(
			new System.Numerics.Vector3( position.x, position.y, position.z ),
			new System.Numerics.Vector3( position.x + forward.x, position.y + forward.y, position.z + forward.z ),
			new System.Numerics.Vector3( up.x, up.y, up.z ) );

		var near = System.MathF.Max( 0.001f, eyeView.ZNear );

		//A headset's eye sees the player's own hands a hand's breadth away, and a camera's near plane - ten
		//centimetres by default - cut them off. Five at most there; closer than that an eye cannot focus anyway.
		if ( field is not null ) near = System.MathF.Min( near, 0.05f );

		var reach = System.MathF.Max( near + 0.001f, eyeView.ZFar );

		//The far plane is flat, so ZFar on the axis is nearer than ZFar at a corner: pushed out until the corners
		//reach it, so what is in reach stays drawn wherever it turns on screen
		var far = field is null && !camera.Orthographic ? reach * CornerStretch( camera.RenderFieldOfView( aspect ), aspect ) : reach;

		var projection = field is { } eyeFov
			? eyeFov.Projection( near, far )
			: ProjectionMatrix( camera.Orthographic, camera.RenderFieldOfView( aspect ),
				camera.OrthographicHeight, aspect, near, far );

		var viewProjection = view * projection;

		//Upscaler and jitter for this frame; the world uses the jittered matrix, the rest the plain one
		var worldPictures = CoreRenderDevice.WorldSize();

		upscaler.Begin( headset: field is not null || overlayOnly,
			(int)worldPictures.x, (int)worldPictures.y, pixels.Width, pixels.Height,
			position, eyeView.Rotation, camera.RenderFieldOfView( aspect ) );

		var drawnProjection = upscaler.Temporal ? projection * upscaler.JitterMatrix : projection;
		var drawnViewProjection = view * drawnProjection;

		//The world's shading size (its pictures' size when upscaled), so screen-position lookups match it
		var upscaledSmaller = worldPictures.x > 0.5f && worldPictures.y > 0.5f &&
			( worldPictures.x < pixels.Width - 0.5f || worldPictures.y < pixels.Height - 0.5f ) &&
			( upscaler.Temporal || upscaler.Kind is CoreUpscale.Kind.Stretch or CoreUpscale.Kind.Fsr1 );

		(int Width, int Height) shaded = upscaledSmaller ? ((int)worldPictures.x, (int)worldPictures.y) : (pixels.Width, pixels.Height);

		//The sun itself: where it points, and how finely its shadow is kept - in this drawer's pages, see CoreSunPages.
		var sunLight = !overlayOnly && scene.IsValid()
			? CoreSceneRenderer.FirstEnabled<DirectionalLight>( scene )
			: null;

		var hasSun = sunLight is not null;

		//And whether it casts: a sun told to cast nothing has its pages neither baked nor read - see CoreSunPages
		var sunCasts = hasSun && sunLight.Shadows;

		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "frame block" );

		var frame = CoreSceneRenderer.BuildFrame( scene, drawnViewProjection, position );

		//A camera excluding the skybox tag keeps the sky's light but shows its own background, as previews do
		var skyShown = !overlayOnly && CoreSceneRenderer.HasSky( frame ) && !camera.RenderExcludeTags.Has( "skybox" );

		//Rotation-only inverse view for direction lookups (the sky); headset eyes write their own
		if ( !overlayOnly && !BothEyes )
		{
			var still = System.Numerics.Matrix4x4.CreateLookAt(
				System.Numerics.Vector3.Zero,
				new System.Numerics.Vector3( forward.x, forward.y, forward.z ),
				new System.Numerics.Vector3( up.x, up.y, up.z ) );

			if ( System.Numerics.Matrix4x4.Invert( still * drawnProjection, out var rayToWorld ) )
				CoreSceneRenderer.SetRayToWorld( frame, rayToWorld );
		}

		//Last frame's matrix with this frame's jitter, so still things read as still in motion vectors
		var previousJittered = ( hasPrevious ? previousViewProjection : viewProjection ) * upscaler.JitterMatrix;
		CoreSceneRenderer.SetPrevious( frame, previousJittered );

		if ( System.Numerics.Matrix4x4.Invert( drawnViewProjection, out var drawnToWorld ) )
		{
			upscaler.ClipToPrevious = drawnToWorld * previousJittered;
			CoreRenderDevice.WorldBack( upscaler.ClipToPrevious );
		}
		CoreSceneRenderer.SetMipBias( frame, upscaler.MipBias );

		//A turn of the cut-out threshold a frame, over as many frames as a history gathers - see foliage.shader
		hashedTurn = ( hashedTurn + 1 ) & 63;
		CoreSceneRenderer.SetHashedCut( frame, upscaler.Temporal ? 1 + hashedTurn : 0 );

		debugView = camera.DebugMode != SceneCameraDebugMode.Normal
			? camera.DebugMode
			: DebugOverlay.ToolsVisualization.mat_toolsvis;

		CoreSceneRenderer.SetDebugView( frame, debugView );

		previousViewProjection = viewProjection;
		hasPrevious = true;

		//Each eye's own place and field over the one both are culled by.
		if ( BothEyes ) WriteEyes( frame, near, far );

		var curveAlone = CurveOf( camera, out frameCurve );

		//No occlusion yet (see LayOcclusion)
		CoreSceneRenderer.SetOcclusion( frame, shaded.Width, shaded.Height, SkyTableSlot() );
		skyLight.Lay( frame );

		//The portable frame's bloom and passes: the world drawn aside and kept past white, its curve left to the finish
		var passes = CoreRenderDevice.PortableFrame && !overlayOnly && !BothEyes && PortablePasses( camera );

		var carry = CoreRenderDevice.PortableFrame && CorePointChain.Refused( lastFoldKey );

		//A page's canvas is multisampled (BrowserSwapchain); elsewhere the portable frame's edges are softened in its finish
		var asideSoftens = CoreRenderDevice.PortableFrame && Rinten.Rendering.DrawSettings.Fxaa && !OperatingSystem.IsBrowser();

		aside = CoreRenderDevice.PortableFrame && !overlayOnly && !BothEyes &&
			glow.Open( camera.PostProcess?.Glow, passes, carry, asideSoftens, exposure );

		//While an export writes down the runs it bakes, the one the portable frame carries beside its passes too
		if ( CorePointChain.Recording && !CoreRenderDevice.PortableFrame ) FoldChain( camera, passes: true );

		//Upload the point effects the world's shading or the finish may carry, whichever way the frame ends up drawn
		foldKey = FoldChain( camera, passes: aside && passes );
		lastFoldKey = foldKey;

		var foldAt = foldKey is null ? -1 : CorePointChain.UploadInto( frame, CollectionsMarshal.AsSpan( foldPoints ) );
		if ( foldAt < 0 ) foldKey = null;

		//Kept past white in eight bits a colour comes back a step off, so a world with nothing over it keeps its curve
		asideFinished = aside && !passes && !carry && !glow.Glows && foldKey is null;

		CoreSceneRenderer.SetFinish( frame, exposure, aside && !asideFinished ? CorePortableGlow.KeepCurve : frameCurve, Math.Max( foldAt, 0 ) );

		//Next frame's exposure, taken here since the compose may not run when shading straight to screen
		exposure = Rinten.Rendering.DrawSettings.Exposure;

		var perspective = viewProjection.M14 != 0.0f || viewProjection.M24 != 0.0f || viewProjection.M34 != 0.0f;

		CoreRenderDevice.ZoneEnd();

		System.Numerics.Matrix4x4.Invert( viewProjection, out var clipToWorld );

		Span<System.Numerics.Vector4> planes = stackalloc System.Numerics.Vector4[6];
		CoreSceneRenderer.FrustumPlanes( viewProjection, planes );

		//The editor's gizmos for this view: the custom objects tools put in the instance's own
		//world, which a game's view leaves out, and the models the last recorded gizmo frame drew.
		var gizmos = GizmoSource?.Invoke();
		var gizmoWorld = gameView ? null : gizmos?.ExistingWorld;

		//Overrides run for every view; this also begins the view's CoreImmediate list, replayed later
		CoreRenderDevice.ZoneBegin( "overrides" );

		//Culled to the 2D light's margin too, or what stands just out of shot never reaches its scene picture
		var margin = CoreLight2D.Shrink( scene, camera );
		var culledBy = margin < 1.0f ? viewProjection * System.Numerics.Matrix4x4.CreateScale( margin, margin, 1.0f ) : viewProjection;

		RunOverrides( scene, gizmoWorld, gizmos?.Draws, camera, new Rect( 0, 0, pixels.Width, pixels.Height ),
			CoreSceneRenderer.FrustumOf( culledBy ), eyeView, viewProjection, eye: Eye is not null );

		CoreRenderDevice.ZoneEnd();

		var immediateModels = overlayOnly ? ReadOnlySpan<CoreImmediate.ModelBatch>.Empty : CoreImmediate.ModelSpan;

		opaque.Clear();
		translucent.Clear();
		overlayLayer.Clear();
		afterUiLayer.Clear();
		pushUsed = 0;

		wireframe = Rinten.Rendering.Wireframe.For( camera.WireframeMode );
		wantNearWins = wireframe >= 2 && Rinten.Rendering.Wireframe.NearWins;

		//Everything of the scene the card knows: brought up to date, and what changed sent where the frame is.
		tables = overlayOnly ? null : CoreSceneTables.Of( scene );

		if ( tables is not null )
		{
			CoreRenderDevice.ZoneBegin( "scene tables" );
			tables.Sync( scene );
			CoreRenderDevice.ZoneEnd();
		}

		//What the camera hides by tag, for the cull and classic draws, once the tables are this frame's
		CoreRenderDevice.ZoneBegin( "hidden" );
		UpdateHidden( camera );
		CoreRenderDevice.ZoneEnd();

		//The classic draws: loose instances, ground, hand-drawn models; everything for a bake or edges
		var everything = wireframe != 0;

		looseErrorScale = perspective ? pixels.Height * 0.5f * MathF.Abs( projection.M22 ) : 0.0f;

		if ( !overlayOnly )
		{
			CoreRenderDevice.ZoneBegin( "gather" );
			Gather( scene, position, everything, immediateModels );
			CoreRenderDevice.ZoneEnd();

			PoseAhead();
		}

		var meshes = !everything && !overlayOnly && tables is not null && CoreMeshRender.EnsurePipelines();

		//Note table changes for the still-world shadow maps
		if ( !overlayOnly )
		{
			casters.Note( tables );

			//What this frame draws by hand and moves (code's models, card geometry) into the movers' maps beside the tables'
			casters.Hand.Clear();

			foreach ( ref readonly var queued in CollectionsMarshal.AsSpan( opaque ) )
				if ( !queued.NoShadow && queued.InMotion && queued.Radius > 0.0f )
					casters.Hand.Add( new System.Numerics.Vector4( queued.Center.x, queued.Center.y, queued.Center.z, queued.Radius ) );

			sunPages.Note();

			//And what this frame draws by hand and holds still - the ground's blocks - which the tables do not hold,
			//into the sun's depth along the light; it is drawn into the maps with the classic draws, see DrawLooseView.
			foreach ( ref readonly var queued in CollectionsMarshal.AsSpan( opaque ) )
				if ( !queued.NoShadow && !queued.InMotion && queued.Radius > 0.0f ) sunPages.Cover( queued.Center, queued.Radius );
		}

		//This frame's block, for the copies a light's view draws the classic draws with.
		frameNow = frame;

		//Plan sun pages and lamp faces now, before the frame block and lights go up
		CoreRenderDevice.ZoneBegin( "shadow plan" );

		var towards = hasSun
			? new Vector3( frame[CoreSceneRenderer.SunAt], frame[CoreSceneRenderer.SunAt + 1], frame[CoreSceneRenderer.SunAt + 2] )
			: Vector3.Up;

		sunPages.Plan( frame, sunCasts && !overlayOnly, towards, sunLight?.ShadowDetail ?? 256.0f, position, reach,
			Rinten.Rendering.DrawSettings.MeshLodPixels );

		lampMaps.Plan( frame, CoreSceneRenderer.LightBlock, CoreSceneRenderer.FrameLights, CoreSceneRenderer.LightsInFrame,
			!overlayOnly, Rinten.Rendering.DrawSettings.MeshLodPixels, CoreMeshShaders.MaxViews - 1 - sunPages.ViewCount,
			//The frustum is one eye's, so a headset passes none, or faces only the other eye sees go undrawn
			position, looseErrorScale, BothEyes ? default : planes, in clipToWorld );

		if ( CoreRenderDevice.PortableFrame ) CoreSceneRenderer.LampFacesIntoBlock( frame );

		CoreRenderDevice.SetupFrame( frame );
		if ( BothEyes ) SetupSecondEye( frame );

		//The decals, which travel with the lights and are read by the same shaders - see CoreDecals.
		//None for a bake or an overlay: what those draw is not surfaces the decals would land on.
		CoreDecals.Gather( overlayOnly ? null : scene?.SceneWorld, position, planes );

		//The lights, read from a buffer with the grid saying which reach where (CoreLights); a bake or a flat view
		//reads every light instead
		CoreLights.Frame( CoreSceneRenderer.LightBlock, CoreSceneRenderer.LightsInFrame, shaded,
			view, projection.M11, projection.M22, near, far,
			grid: !overlayOnly && perspective );

		CoreRenderDevice.ZoneEnd();

		var errorScale = pixels.Height * 0.5f * MathF.Abs( projection.M22 );

		if ( meshes )
		{
			CoreRenderDevice.ZoneBegin( "cull" );

			tables.Upload();
			tables.PlaceRuns();

			var count = 0;
			//Rasterised through the jittered matrix, which the cluster passes read out of here rather than
			//out of the frame block; culled by the plain planes, which a fraction of a pixel does not move.
			views[count] = ViewOf( planes, drawnViewProjection, position );
			views[count].Camera = true;
			views[count].Perspective = perspective;
			views[count].ErrorScale = errorScale;
			views[count].LodPixels = Rinten.Rendering.DrawSettings.MeshLodPixels;
			views[count].Near = near;
			views[count].Far = far;
			views[count].CullPixels = Rinten.Rendering.DrawSettings.CullPixels;
			views[count].HalfHeight = pixels.Height * 0.5f;
			views[count].ScaleX = projection.M11;
			views[count].ScaleY = projection.M22;
			views[count].Fovea = CoreFovea.ForCull( Fovea, Field );
			count++;

			//And the sun's: this frame's bakes of its pages and the zones of its map of what moves, planned before
			//the frame block went up - see CoreSunPages.Plan. Their lists are drawn straight after the cull.
			count = sunPages.AddViews( views, count );

			//Then the lamps' faces - see CoreLampMaps.Plan.
			count = lampMaps.AddViews( views, count );
			meshRender.ShadowTriangles = sunPages.Triangles + lampMaps.Triangles;

			var pyramid = EnsurePyramid();
			var canOcclude = pyramid != 0 && perspective;

			//This view's depth and cull way where settings leave it to the frame, measured on the card (DepthTuner);
			//occlusion is a candidate unless set or ruled out
			var way = depthTuner.Pick( CoreRenderDevice.ReadFrameSummary( viewport )?.GpuMs ?? 0.0f,
				canOcclude && !Rinten.Rendering.DrawSettings.Occlusion ? 4 : 3 );

			occluding = canOcclude && ( Rinten.Rendering.DrawSettings.Occlusion || way == DepthWay.Occlusion );

			//A pyramid not folded this frame is stale, so turning occlusion back on must not cull by it
			if ( !occluding ) depthKnown = false;

			var useDepth = occluding && depthKnown;

			//Pre-pass draws all still clusters when depth is needed first (occlusion, depth stages, paint)
			var depthFirst = occluding || Rinten.Rendering.Wireframe.Clusters > 0 || ( camera.PostProcess is not null &&
				Hung( camera, Rinten.Rendering.Stage.AfterDepthPrepass ) );

			//Else the tuner's pick: nothing, or clusters nearer than the far cut, which cover the screen and hide; far
			//ones hide little and get depth from their shading pass
			PrepassSlices = depthFirst || way == DepthWay.FullPrepass ? uint.MaxValue
				: way == DepthWay.NoPrepass ? 0u : (uint)CoreMeshShaders.FarSlice;

			if ( meshRender.Begin( tables, views.AsSpan( 0, count ), pixels, useDepth ) && pyramid != 0 )
			{
				meshRender.Cull( pyramid );
				meshRender.Skin();
			}

			CoreRenderDevice.ZoneEnd();
		}
		else
		{
			meshes = false;
		}

		CoreRenderDevice.ZoneBegin( "frame plan" );

		var drawn = meshes && meshRender.Ready;

		//Whether nothing goes into the world picture this frame; clusters always count as drawing
		var worldEmpty = !drawn && opaque.Count == 0 && translucent.Count == 0 &&
			!overlayOnly && !skyShown;

		//Sky ambient table (by elevation and sun angle), drawn every frame with a sky, with the sky material
		var skyTable = CoreSceneRenderer.HasSky( frame ) ? SkyTablePipeline( skyShader ) : 0;

		//Draw the sky table once per headset frame, not once per eye; texture cameras still draw their own
		var tableDone = SharingViews && skyTableFrame == EngineLoop.LoopFrames;

		if ( skyTable != 0 && !tableDone &&
			CoreRenderDevice.BeginTarget( CoreRender.TargetKind.SkyTable, default, clearFirst: false ) )
		{
			CoreRenderDevice.ZoneBegin( "sky table" );
			CoreRenderDevice.Draw( skyTable, 3, System.Array.Empty<byte>(), skyDevice );
			CoreRenderDevice.ZoneEnd();

			CoreRenderDevice.EndTarget();

			skyTableFrame = SharingViews ? EngineLoop.LoopFrames : -1;
		}

		//And the sky's light, where its sky changed: capture, reflection levels, irradiance
		skyLight.Draw( frame, skyShader, skyDevice, skyCore );

		//The terrains' rings of relief built since the last frame, before the ground they are drawn over
		TerrainRelief.ExpandAll();

		//Whether anything reads the world picture; if not, the world is shaded straight onto the window
		var worldSize = CoreRenderDevice.WorldSize();

		//Stretching needs the world's own picture: drawn onto the window, the world is window-sized whatever the scale
		var stretching = upscaledSmaller && upscaler.Kind == CoreUpscale.Kind.Stretch;

		var carryingUp = Rinten.Rendering.DrawSettings.Upscaler == 2 &&
			worldSize.x > 1.0f && worldSize.y > 1.0f &&
			( worldSize.x < pixels.Width - 0.5f || worldSize.y < pixels.Height - 0.5f );

		//A temporal upscaler reads the world's picture and hands the compose one of its own - see CoreUpscaler.
		//It is the anti-aliasing too, and the edge filter over it would only soften what it gathered.
		var upscaling = upscaler.Temporal && !worldEmpty;

		//Not in the portable frame, which has no picture of the world for the filter to read - see RenderTier.
		var softening = Rinten.Rendering.DrawSettings.Fxaa && !carryingUp && !upscaling && !worldEmpty &&
			!CoreRenderDevice.PortableFrame;

		//And whatever a project hung between the depth pre-pass and the window. Every one of these stages
		//draws into the world's picture or reads it; AfterUI is the only one that happens past all of this.
		var staged = camera.PostProcess is not null && (
			Hung( camera, Rinten.Rendering.Stage.AfterDepthPrepass ) ||
			Hung( camera, Rinten.Rendering.Stage.AfterOpaque ) ||
			Hung( camera, Rinten.Rendering.Stage.AfterSkybox ) ||
			Hung( camera, Rinten.Rendering.Stage.AfterTransparent ) ||
			Hung( camera, Rinten.Rendering.Stage.AfterViewmodel ) ||
			Hung( camera, Rinten.Rendering.Stage.BeforePostProcess ) ||
			( Hung( camera, Rinten.Rendering.Stage.Tonemapping ) && !curveAlone ) ||
			Hung( camera, Rinten.Rendering.Stage.AfterPostProcess ) );

		//The debug views and the cluster paint are drawn over the world's picture and read what is already in it.
		var inspecting = debugView != DebugOverlay.ToolsVisualization.mat_toolsvis ||
			Rinten.Rendering.Wireframe.Clusters > 0;

		//Translucency needs an offscreen picture: blending onto the window would leak alpha to the compositor
		var blending = translucent.Count > 0;

		//Whether every world pixel gets written (sky or background); else keep an offscreen picture
		var fills = !overlayOnly && ( skyShown || backgroundPipeline != 0 );

		//Whether the world's shading carries the point effects instead of the compose (see FoldChain)
		var folding = foldKey is not null && !aside && !softening && !carryingUp && !stretching && !upscaling && !inspecting &&
			( !blending || CoreRenderDevice.PortableFrame ) &&
			skyShown && !CorePointChain.Refused( foldKey );

		var folds = folding && foldKey == readyChain &&
			SkyChainPipeline( skyShader, foldKey, CoreSceneRenderer.SkySpecialize( skyCore ) ) != 0;

		worldChain = folds ? foldKey : null;
		warmChain = folding && !folds ? foldKey : null;
		CoreMeshRender.ChainWaiting = 0;

		LooseDraws = 0;
		LooseTriangles = 0;
		LooseMovingTriangles = 0;

		if ( worldChain != saidChain )
		{
			saidChain = worldChain;

			if ( worldChain is not null )
				Log.Info( $"the frame's {foldPoints.Count} point effects are carried by the world's own shading - no compose" );
		}

		//The portable frame (RenderTier) shades onto the screen, so it has no world picture to read
		var readsWorld = !CoreRenderDevice.PortableFrame &&
			( softening || carryingUp || stretching || upscaling || ( staged && !folds ) || inspecting || blending || !fills );

		//Log when readsWorld flips, since the extra full-screen pass isn't visible in the picture
		if ( readsWorld != saidReadsWorld || !saidReads )
		{
			saidReads = true;
			saidReadsWorld = readsWorld;

			//Trace: views that come and go would repeat it; chased with the bisect convars
			Log.Trace( readsWorld
				? "the world is drawn into a picture of its own: " +
					( softening ? "edges " : "" ) + ( carryingUp ? "upscale " : "" ) +
					( upscaling ? $"{upscaler.Kind} " : "" ) +
					( staged && !folds ? "an effect " : "" ) +
					( inspecting ? "a debug view " : "" ) + ( blending ? "something translucent " : "" ) +
					( !fills ? "nothing to fill the gaps " : "" )
				: "the world is shaded straight onto the screen" );
		}

		var wantsOffscreen = composePipeline != 0 && composeInputs != 0 && readsWorld;
		worldTarget = wantsOffscreen ? CoreRender.TargetKind.Scene : CoreRender.TargetKind.Screen;

		//So whether the world's passes are the last, applying exposure and curve
		worldFinishes = !wantsOffscreen;

		//The particles stepped on the card, before any pass that draws them - see ParticleGpuRun
		if ( !overlayOnly ) ParticleGpuRun.Simulate( position, new Vector3( forward.x, forward.y, forward.z ) );

		//Whether the adaptive shading rates are worth taking for this world - see VkFrame.Fovea
		CoreRenderDevice.WorldShades( drawn || opaque.Count > 0 || translucent.Count > 0 ||
			CoreImmediate.Instanced.Length > 0 || CoreImmediate.Blocks.Length > 0 );

		//Let the pre-pass write the screen's depth directly when the frame shades onto the window
		CoreRenderDevice.DepthOnScreen( viewport,
			worldFinishes && CoreRenderDevice.DepthOnScreenPossible( viewport ) );

		CoreRenderDevice.ZoneEnd();

		//The shape of the world before its colour: the clusters, then the loose draws beside them, into the depth.
		var clustersDrawn = false;

		//Whether light views drew, after the bin in the depth block; if not, planned bakes were never drawn
		var lightsDrawn = false;

		if ( !overlayOnly && CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Depth ) )
		{
			CoreRenderDevice.ZoneBegin( "depth" );

			//Under a name of its own, so the report can account for it within `depth`
			CoreRenderDevice.ZoneBegin( "prepass" );

			if ( drawn && PrepassSlices > 0 ) meshRender.DrawCamera( appeared: false );
			SubmitLoose( planes, prepass: true );

			CoreRenderDevice.ZoneEnd();

			CoreRenderDevice.EndTarget();

			//Fold depth, retest set-aside clusters, fold again; only when clusters were drawn and occlusion is on
			if ( drawn && occluding ) KeepDepth( viewport );

			if ( drawn && occluding && depthKnown )
			{
				meshRender.Retest( depthPyramid );

				if ( CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Depth, default, clearFirst: false ) )
				{
					meshRender.DrawCamera( appeared: true );
					CoreRenderDevice.EndTarget();
				}

				KeepDepth( viewport, ifAppeared: true );
			}

			//And the camera's clusters sorted by material, for the passes that shade them - after the last
			//cull pass, whichever it was - with the lights' lists filled into their run in the same pass.
			if ( drawn ) meshRender.Bin( depthPyramid );

			//Movers' depth from their own shaders after the bin (DrawMoverDepth), so their shading pass tests early
			if ( drawn && CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Depth, default, clearFirst: false ) )
			{
				meshRender.DrawMoverDepth();
				CoreRenderDevice.EndTarget();
			}

			//Bake sun pages and the movers map from the cull's lists, before the materials that read them
			sunPages.Draw( drawn ? meshRender : null, 1, looseView );

			//And the lamps' faces, from the views after the sun's - see CoreLampMaps.
			lampMaps.Draw( drawn ? meshRender : null, 1 + sunPages.ViewCount, looseView );
			lightsDrawn = true;

			clustersDrawn = drawn;

			//The air into its slice sheet, a march per froxel read in one tap; after the sun maps, before shading
			var air = CoreVolumeFog.Draw( pixels, far );

			//The scene for hardware rays, where they are on: into the block before the passes after the pre-pass trace it
			if ( CoreRays.On )
			{
				CoreRenderDevice.ZoneBegin( "rays build" );
				CoreRays.Build( scene );
				CoreRays.Lay( frame );
				CoreRenderDevice.ZoneEnd();

				//And the light of a scene with no bake through them, about this view's camera
				CoreRenderDevice.ZoneBegin( "probe grid" );
				probeGrid.Trace( scene, position, frame );
				CoreRenderDevice.ZoneEnd();

				CoreRenderDevice.SetupFrame( frame );
			}

			//And the bake's bounce through them, before anything reads it
			CoreRenderDevice.ZoneBegin( "baked bounce" );
			CoreBakedLighting.TraceBounce( scene );
			CoreRenderDevice.ZoneEnd();

			RunStage( camera, Rinten.Rendering.Stage.AfterDepthPrepass, CoreRender.TargetKind.Scene, near, far );

			//An "Occlusion" picture drawn here darkens surfaces (AmbientOcclusion); laid into the block, re-sent,
			//since nothing drawn before reads those lanes
			var laid = false;

			if ( air.Picture > 0 )
			{
				CoreSceneRenderer.LayFogVolumeSheet( frame, air.Picture, CoreVolumeFog.Slices,
					CoreVolumeFog.Laid.Near, CoreVolumeFog.Laid.Far, CoreVolumeFog.Columns );
				laid = true;
			}

			if ( CorePasses.Find( "Occlusion" ) is { Index: > 0 } occlusion )
			{
				CoreSceneRenderer.LayOcclusion( frame, occlusion.Index );
				laid = true;
			}

			//Traced near light where rays drew it, else what the occlusion gathered off the screen
			if ( CorePasses.Find( CoreRayLight.Picture ) is { Index: > 0 } traced )
			{
				CoreSceneRenderer.LayScreenLight( frame, traced.Index, true );
				laid = true;
			}
			else if ( CorePasses.Find( AmbientOcclusion.IndirectPicture ) is { Index: > 0 } indirect )
			{
				CoreSceneRenderer.LayScreenLight( frame, indirect.Index );
				laid = true;
			}

			//And the picture of the depth itself, for the surfaces that read what is behind them - see DepthPicture.
			if ( CorePasses.Find( DepthPicture.PictureName ) is { Index: > 0 } depthPicture )
			{
				CoreSceneRenderer.LayDepthPicture( frame, depthPicture.Index );
				laid = true;
			}

			if ( laid ) CoreRenderDevice.SetupFrame( frame );

			//Emptied again where the edges arbitrate between themselves - see Wireframe.NearWins.
			if ( wantNearWins && CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Depth ) )
				CoreRenderDevice.EndTarget();

			CoreRenderDevice.ZoneEnd();
		}

		//Bakes planned for a frame whose depth never opened go back, or their slots read as drawn - but the portable
		//frame has no depth block, and draws its sun's pages here the classic way, before the world reads them
		CoreRenderDevice.ZoneBegin( "light views" );
		if ( !lightsDrawn )
		{
			var portable = CoreRenderDevice.PortableFrame && !overlayOnly ? looseView : null;

			sunPages.Draw( null, 1, portable );
			lampMaps.Draw( null, 1 + sunPages.ViewCount, portable );
		}

		CoreRenderDevice.ZoneEnd();

		//The clusters' pixels first, a material at a time over the depth of material numbers; then the world's
		//own picture opens over the real depth, keeping what was shaded, for everything drawn the classic way.
		var offscreen = false;

		//The 2D lighting's pictures, out of the sprites already queued, before the world's picture opens - see CoreLight2D.
		//Overlay-only views too: the editor's 2D views are, and their sprites are lit the same over the finished picture
		CoreRenderDevice.ZoneBegin( "light 2d" );
		var light2d = !StereoPass &&
			CoreLight2D.Draw( scene, camera, frame, pixels.Width, pixels.Height );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "world opaque" );


		//Whether the world's picture needs a clear: under a sky every untouched pixel is sky, and normal readers
		//test depth first - see SceneSurface
		var skyCovers = skyShown;

		if ( wantsOffscreen )
		{
			offscreen = CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene, camera.BackgroundColor,
				clearFirst: !skyCovers, discard: skyCovers );

			if ( !offscreen )
			{
				if ( !warnedMaterials ) { warnedMaterials = true; Log.Warning( $"the world's picture would not open: {CoreRenderDevice.Description}" ); }
				worldTarget = CoreRender.TargetKind.Screen;
				worldFinishes = true;
			}
		}

		//Clusters first, a material at a time over their depth, then the classic draws over the same depth
		if ( clustersDrawn && ( offscreen || worldFinishes ) )
			meshRender.DrawMaterials( worldTarget, worldFinishes, worldFinishes ? worldChain : null, warmChain );

		if ( drawn ) meshRender.Debug();

		//Classic draws (SubmitLoose), under their own name in the report
		if ( !overlayOnly )
		{
			CoreRenderDevice.ZoneBegin( "loose" );
			SubmitLoose( planes );
			CoreRenderDevice.ZoneEnd();
		}

		if ( clustersDrawn && offscreen )
			meshRender.DrawClusterView( Rinten.Rendering.Wireframe.Clusters );

		Between( camera, offscreen, near, far, Rinten.Rendering.Stage.AfterOpaque );

		if ( skyShown )
		{
			CoreRenderDevice.ZoneBegin( "sky" );
			DrawSky();
			CoreRenderDevice.ZoneEnd();
		}
		else if ( !overlayOnly && worldFinishes )
		{
			//No sky and nothing to fill what the world left: the clear its own picture would have had
			CoreRenderDevice.ZoneBegin( "background" );
			DrawBackground( camera, aside && !asideFinished ? CorePortableGlow.KeepCurve : frameCurve );
			CoreRenderDevice.ZoneEnd();
		}

		CoreRenderDevice.ZoneEnd();

		//Whether every pass the wanted run needs exists, so the next frame laying it carries it - see FoldChain
		if ( warmChain is not null && CoreMeshRender.ChainWaiting == 0 &&
			SkyChainPipeline( skyShader, warmChain, CoreSceneRenderer.SkySpecialize( skyCore ) ) != 0 )
			readyChain = warmChain;

		Between( camera, offscreen, near, far, Rinten.Rendering.Stage.AfterSkybox );

		//The opaque world kept, so the upscaler can tell what is laid over it next and not trust its history there
		if ( upscaling && offscreen && ( translucent.Count > 0 || CoreImmediate.Instanced.Length > 0 || CoreImmediate.Blocks.Length > 0 ) )
		{
			CoreRenderDevice.EndTarget();
			upscaler.KeepOpaque( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene ) );

			if ( !CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene, camera.BackgroundColor, clearFirst: false ) )
				Log.Warning( $"the world's pass would not reopen for its glass: {CoreRenderDevice.Description}" );
		}

		CoreRenderDevice.ZoneBegin( "world glass" );
		FurthestFirst( translucent, translucentOrder );

		foreach ( var at in translucentOrder )
		{
			var queued = translucent[at];

			if ( !CoreSceneRenderer.SphereVisible( planes, queued.Center, Reach( queued ) ) ) continue;
			Submit( queued );
		}
		CoreRenderDevice.ZoneEnd();

		//Sprite batches go into the world picture after glass so post effects reach them
		instancedInWorld = false;

		if ( !overlayOnly && !StereoPass && ( CoreImmediate.Instanced.Length > 0 || CoreImmediate.Blocks.Length > 0 ) )
		{
			CoreRenderDevice.ZoneBegin( "world sprites" );
			DrawInstanced( worldTarget );
			CoreRenderDevice.ZoneEnd();

			instancedInWorld = true;
		}

		//What of the 2D lighting Lighting2D.Debug asked to see, over the world it lit
		if ( light2d && ( offscreen || worldFinishes ) ) CoreLight2D.DrawDebug( worldTarget );

		//What lies over the world with its own depth (a held weapon), into the same picture so effects reach it
		CoreRenderDevice.ZoneBegin( "world overlay" );
		DrawLayer( overlayLayer );
		CoreRenderDevice.ZoneEnd();

		if ( aside )
		{
			var (reads, light) = passes ? RunPortablePasses( camera, near, far ) : ( 0ul, false );
			glow.Finish( frameCurve, foldKey, reads, light, asideFinished );
		}

		if ( offscreen )
		{
			CoreRenderDevice.EndTarget();

			//Draw velocities only if motion blur or a temporal upscaler will read them
			var moved = ( ( camera.PostProcess?.WantsMotion ?? false ) || upscaling ) && DrawVelocities( viewport );

			//The frame may stay where the chain leaves it once the other picture exists; until then copy back
			var settle = !PostInputsReady();

			//Upscale before the effects so they run at window size; full frame only, 0 when nothing to do
			var upscaled = worldEmpty || CoreRenderDevice.PortableFrame ? 0ul : upscaler.Run(
				CoreRenderDevice.TargetTexture( viewport, frameIn ),
				CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 2 ),
				moved ? CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Velocity ) : 0ul,
				near, far, VerticalField( drawnProjection ), RealTime.Delta, CoreSceneRenderer.ExposureOf( frame ) );

			//Effects to run over it: into the window-size pair first - see TargetKind.Display - and the
			//stages there. With none, the compose reads the upscaler's own picture and nothing is copied.
			var display = upscaled != 0 && HungBeforeCompose( camera ) && IntoDisplay( upscaler.LastTexture, near, far );

			if ( display )
			{
				foreach ( var stage in beforePostStages )
					RunStage( camera, stage, CoreRender.TargetKind.Display, near, far, settle: false, pair: displayPair, holds: true );
			}
			else if ( upscaled == 0 )
			{
				RunStages( camera, CoreRender.TargetKind.Scene, near, far, settle, beforePostStages );
			}

			CoreRenderDevice.ZoneBegin( "compose" );

			var world = CoreRenderDevice.WorldSize();

			var carrying = Rinten.Rendering.DrawSettings.Upscaler == 2 &&
				world.x > 1.0f && world.y > 1.0f &&
				( world.x < pixels.Width - 0.5f || world.y < pixels.Height - 0.5f );

			//Nothing drawn, nothing to soften: one test a frame instead of FXAA finding a flat picture per pixel
			var soften = Rinten.Rendering.DrawSettings.Fxaa && !carrying && !upscaling && !worldEmpty;

			//Every held point effect and the curve go into the compose, which reads every pixel anyway
			if ( display )
				RunStage( camera, Rinten.Rendering.Stage.Tonemapping, CoreRender.TargetKind.Display, near, far,
					settle: false, pair: displayPair, holds: true );
			else if ( upscaled == 0 )
				RunStage( camera, Rinten.Rendering.Stage.Tonemapping, CoreRender.TargetKind.Scene, near, far,
					settle: !PostInputsReady(), holds: true );

			if ( upscaled != 0 )
			{
				carrying = false;
				soften = false;
			}

			composeChain.Clear();
			CorePostBlit.TakeChain( composeChain );

			var split = CorePointChain.WithCurve( composeChain, frameCurve ) + 1;

			//Anything after the compose? If not, it draws onto the window and the finished pair is never made
			var after = Hung( camera, Rinten.Rendering.Stage.AfterPostProcess );

			//No edge filter where an effect moved where the frame is read: the blocks of a pixelation are the point.
			if ( soften && CorePointChain.Moves( CollectionsMarshal.AsSpan( composeChain ) ) ) soften = false;

			var target = after ? CoreRender.TargetKind.Final : CoreRender.TargetKind.Screen;
			var pipeline = CorePointChain.Compose( CollectionsMarshal.AsSpan( composeChain ), split, target, carrying );
			var numbers = pipeline == 0 ? -1 : CorePointChain.Upload( CollectionsMarshal.AsSpan( composeChain ) );

			//A chain the compose could not be compiled with - no compiler here, and never baked - is drawn a pass an
			//effect before it, and the compose is the plain one with the frame's own curve. See ComposeSeparately.
			if ( pipeline == 0 || numbers < 0 )
			{
				ComposeSeparately( composeChain, split, near, far );

				if ( carrying ) EnsureCarryCompose();

				pipeline = carrying && composeCarryPipeline != 0
					? ( after ? composeCarryFinalPipeline : composeCarryPipeline )
					: ( after ? composeFinalPipeline : composePipeline );

				numbers = 0;
			}

			var composeWith = display ? DisplayInput( displayIn )
				: upscaled != 0 ? upscaled
				: frameIn == CoreRender.TargetKind.Post ? composeInputsPost : composeInputs;

			//The picture the compose's point effects read their neighbours from: the one it composes.
			var composeFrame = display ? CoreRenderDevice.TargetTexture( viewport, displayIn )
				: upscaled != 0 ? upscaler.LastTexture
				: CoreRenderDevice.TargetTexture( viewport, frameIn );

			var composePush = CorePostBlit.ComposePush( stackalloc byte[128],
				new Vector2( pixels.Width, pixels.Height ),
				new Vector2( carrying ? world.x : 0.0f, carrying ? world.y : 0.0f ),
				soften, numbers, near, far,
				Indexed( composeFrame ),
				Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 1 ) ),
				Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 2 ) ),
				Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Velocity ) ),
				upscaled != 0 ? upscaler.ComposeSharpening : 0.0f );

			var finishing = after && CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Final, Color.Transparent,
				clearFirst: false, discard: true );

			//The finished picture was asked for but not had: compose onto the window, nothing after
			if ( after && !finishing )
			{
				pipeline = CorePointChain.Compose( CollectionsMarshal.AsSpan( composeChain ), split, CoreRender.TargetKind.Screen, carrying );
				if ( pipeline == 0 ) pipeline = composePipeline;
			}

			if ( pipeline != 0 ) CoreRenderDevice.Draw( pipeline, 3, composePush, composeWith );

			if ( finishing ) CoreRenderDevice.EndTarget();

			CoreRenderDevice.ZoneEnd();

			if ( finishing )
			{
				RunStage( camera, Rinten.Rendering.Stage.AfterPostProcess, CoreRender.TargetKind.Final,
					near, far, settle: false,
					pair: ( CoreRender.TargetKind.Final, CoreRender.TargetKind.FinalSecond ), holds: true );

				//Onto the window from the finished picture the chain left, post-compose point effects on the way
				FinalToScreen( near, far );
			}
			else
			{
				RunStage( camera, Rinten.Rendering.Stage.AfterPostProcess, CoreRender.TargetKind.Screen, near, far );
			}

			CoreFrameGeneration.LetGoUnwanted();

			//The finished world before panels and the interface go over it: what a frame generated ahead of this one is made of
			if ( upscaled != 0 && !editorOverlays && CoreFrameGeneration.Wanted )
			{
				var depth = CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 2 );
				var velocity = moved ? CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Velocity ) : 0ul;

				CoreFrameGeneration.Capture( depth, upscaler.MotionTexture( depth, velocity, near, far ),
					upscaler.RenderSize, upscaler.DisplaySize, upscaler.Jitter, near, far, VerticalField( drawnProjection ),
					RealTime.Delta, upscaler.Reset );
			}
		}

		//Overlays use the unjittered matrix so gizmos don't shake; frame-block readers get a plain copy
		var overlayBlock = upscaler.Temporal && frameNow is not null
			? PutPlainFrame( viewProjection )
			: 0ul;

		var blockBefore = overlayBlock != 0 ? CoreRenderDevice.UseFrame( overlayBlock ) : 0ul;

		CoreRenderDevice.ZoneBegin( "editor overlay" );

		CoreRenderDevice.ZoneBegin( "world panels" );
		DrawWorldPanels( scene, viewProjection );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "overlay text" );
		DrawWorldText( scene, camera, viewProjection );
		CoreRenderDevice.ZoneEnd();

		if ( editorOverlays )
		{
			CoreRenderDevice.ZoneBegin( "overlay grid" );
			if ( !overlayOnly && ( GroundGridSource?.Invoke() ?? true ) ) DrawGrid( camera, viewProjection );

			//The same grid on the faces of meshes being laid out, which the ground grid could not do
			if ( !overlayOnly ) DrawSurfaceGrid( viewProjection, planes );
			CoreRenderDevice.ZoneEnd();

		}

		//What the scene's own code drew with Gizmo.Draw - its debug drawing.
		var overlayDraws = scene?.GizmoDraws;

		CoreRenderDevice.ZoneBegin( "overlay dynamic" );
		DrawDynamicObjects( scene, viewProjection, overlay: false );
		DrawDebug( overlayDraws, camera, viewProjection, overlay: false );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "overlay immediate" );
		DrawImmediate( viewProjection, overlay: false );
		CoreRenderDevice.ZoneEnd();

		if ( GizmoSource is not null )
		{
			lastCameraPosition = eyeView.Position;
			lastCameraSize = camera.SceneCamera?.Size ?? default;
			lastCameraFov = eyeView.FieldOfView;

			var gizmoDraws = gizmos?.Draws;
			MeasureGizmos( gizmoDraws );

			CoreRenderDevice.ZoneBegin( "overlay gizmos" );

			using ( Diagnostics.Performance.Scope( "gizmo world" ) )
			{
				DrawDebug( gizmoDraws, camera, viewProjection, overlay: false );
			}

			if ( editorOverlays )
			{
				using ( Diagnostics.Performance.Scope( "gizmo selection" ) )
				{
					DrawSelection( viewProjection );
				}
			}

			using ( Diagnostics.Performance.Scope( "gizmo over" ) )
			{
				DrawDebug( gizmoDraws, camera, viewProjection, overlay: true );
			}

			CoreRenderDevice.ZoneEnd();

			CoreRenderDevice.ZoneBegin( "overlay screen" );
			DrawDebugScreen( gizmoDraws, viewProjection );
			CoreRenderDevice.ZoneEnd();
		}

		CoreRenderDevice.ZoneBegin( "overlay dynamic late" );
		DrawDynamicObjects( scene, viewProjection, overlay: true );
		DrawDebug( overlayDraws, camera, viewProjection, overlay: true );
		DrawDebugScreen( overlayDraws, viewProjection );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneBegin( "overlay immediate late" );
		DrawImmediate( viewProjection, overlay: true );
		CoreRenderDevice.ZoneEnd();

		CoreRenderDevice.ZoneEnd();

		if ( overlayBlock != 0 ) CoreRenderDevice.UseFrame( blockBefore );

		//A headset has no screen for game panels: each becomes a sheet before the head - see ScreenPanel
		if ( Eye is not null )
		{
			CoreRenderDevice.ZoneBegin( "headset ui" );
			DrawHeadsetPanels( scene, viewProjection );
			CoreRenderDevice.ZoneEnd();
		}

		if ( gameView )
		{
			CoreRenderDevice.ZoneBegin( "game ui" );
			DrawGameUi( scene );
			CoreRenderDevice.ZoneEnd();

			//What is drawn over the interface: onto the window, finished there.
			CoreRenderDevice.ZoneBegin( "after ui" );
			DrawLayer( afterUiLayer, CoreRender.TargetKind.Screen );
			CoreRenderDevice.ZoneEnd();
		}

		//After-UI passes read a copy of the window (with UI) and each draws over the window; no chaining
		var overUi = camera.PostProcess is not null && Hung( camera, Rinten.Rendering.Stage.AfterUI );

		var captured = overUi && CoreRenderDevice.CaptureBackdrop()
			? CoreRenderDevice.BackdropTexture()
			: 0;

		RunStage( camera, Rinten.Rendering.Stage.AfterUI, CoreRender.TargetKind.Screen, near, far,
			reads: captured );

		//The frame's pictures back to the pool - see CorePasses.
		CorePasses.EndFrame();

		//Nothing records commands against this drawer outside its own frame.
		Drawing = null;

		if ( Watched || gameView )
		{
			CoreRenderDevice.ZoneBegin( "report" );

			CoreRenderProfiler.LastViewport = viewport;
			CoreRenderProfiler.LogPeriodically( viewport );

			Rinten.Diagnostics.PerfReport.LastViewportPixels = (long)pixels.Width * pixels.Height;

			if ( CoreRenderProfiler.Enabled ) DrawProfilerOverlay();

			CoreRenderDevice.ZoneEnd();
		}
	}


	//Draws the profiler overlay with the screen text pipeline; lines refresh a few times a second
	private void DrawProfilerOverlay()
	{
		if ( screenPipeline == 0 || whiteTexture == 0 ) return;

		var (width, height) = size;
		if ( width <= 0 || height <= 0 ) return;

		var lines = CoreRenderProfiler.OverlayLines( viewport );
		if ( lines.Length == 0 ) return;

		var ortho = ScreenProjection( width, height );

		var push = LinePush( stackalloc byte[80], ortho );
		var corners = iconCorners;

		//Handed the push block rather than capturing it - see DrawSprites' Flush.
		void Quad( ReadOnlySpan<byte> block, float x, float y, float w, float h, Color32 tint )
		{
			corners[0] = IconVertex( new Vector3( x, y, 0 ), 0, 0, tint );
			corners[1] = IconVertex( new Vector3( x + w, y, 0 ), 1, 0, tint );
			corners[2] = IconVertex( new Vector3( x, y + h, 0 ), 0, 1, tint );
			corners[3] = IconVertex( new Vector3( x, y + h, 0 ), 0, 1, tint );
			corners[4] = IconVertex( new Vector3( x + w, y, 0 ), 1, 0, tint );
			corners[5] = IconVertex( new Vector3( x + w, y + h, 0 ), 1, 1, tint );

			CoreRenderDevice.DrawDynamic( Pick( screenPipeline, screenTwo ),
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( corners ),
				corners.Length, 76, block, whiteTexture );
		}

		const float left = 12.0f;
		const float top = 12.0f;
		const float rowHeight = 16.0f;
		const float barLeft = 460.0f;
		const float barWidth = 160.0f;
		const float barScaleMs = 16.0f;

		//One translucent sheet behind everything, so the numbers read over any scene.
		Quad( push, left - 6, top - 6, barLeft + barWidth + 12 - left + 6, lines.Length * rowHeight + 12,
			new Color32( 0, 0, 0, 150 ) );

		var y = top;

		foreach ( var line in lines )
		{
			if ( line.BarMs >= 0.0f )
			{
				var portion = System.MathF.Min( 1.0f, line.BarMs / barScaleMs );

				//Green within a 60 Hz frame's budget, amber past half of one, red past all of it.
				var tint = line.BarMs > barScaleMs * 0.66f
					? new Color32( 235, 90, 80, 220 )
					: line.BarMs > barScaleMs * 0.33f
						? new Color32( 235, 180, 70, 220 )
						: new Color32( 110, 200, 120, 220 );

				Quad( push, barLeft, y + 3, System.MathF.Max( 1.0f, barWidth * portion ), rowHeight - 6, tint );
			}

			var scope = new TextRendering.Scope( line.Text, Color.White.WithAlpha( 0.92f ), 12,
				"Roboto Mono", line.Depth == 0 ? 700 : 500 );

			var text = RasteriseCached( scope );

			if ( text.Texture != 0 )
			{
				var x = left + ( line.Depth > 1 ? ( line.Depth - 1 ) * 12.0f : 0.0f );
				var white = Color32.White;

				corners[0] = IconVertex( new Vector3( x, y, 0 ), 0, 0, white );
				corners[1] = IconVertex( new Vector3( x + text.Size.x, y, 0 ), 1, 0, white );
				corners[2] = IconVertex( new Vector3( x, y + text.Size.y, 0 ), 0, 1, white );
				corners[3] = IconVertex( new Vector3( x, y + text.Size.y, 0 ), 0, 1, white );
				corners[4] = IconVertex( new Vector3( x + text.Size.x, y, 0 ), 1, 0, white );
				corners[5] = IconVertex( new Vector3( x + text.Size.x, y + text.Size.y, 0 ), 1, 1, white );

				CoreRenderDevice.DrawDynamic( Pick( screenPipeline, screenTwo ),
					System.Runtime.InteropServices.MemoryMarshal.AsBytes( corners ),
					corners.Length, 76, push, text.Texture );
			}

			y += rowHeight;
		}
	}

	//The four passes between the world and the screen, baked once.
	private static void MakePostPipelines()
	{
		//There is nothing between the world and the screen in the portable frame - see RenderTier - so none of
		//these is made, and the world's own passes finish the picture as they do in any frame with no compose.
		if ( CoreRenderDevice.PortableFrame ) return;

		//The compose carries the tonemap curve, a replaceable file, so a compose baked before mount or edit is stale
		if ( composePipeline != 0 && composeCurve == CoreShaderCache.Revision ) return;

		if ( composePipeline != 0 )
		{
			foreach ( var stale in new[] { composePipeline, composeFinalPipeline,
				composeCarryPipeline, composeCarryFinalPipeline } )
			{
				if ( stale != 0 ) CoreRenderDevice.DestroyPipeline( stale );
			}

			composePipeline = 0;
			composeFinalPipeline = 0;
			composeCarryPipeline = 0;
			composeCarryFinalPipeline = 0;
			composeCarryTried = false;
		}

		composeCurve = CoreShaderCache.Revision;

		//Not full-screen: draws the meshes that moved with the world's depth, writing only where they are
		{
			if ( velocityPipeline != 0 ) CoreRenderDevice.DestroyPipeline( velocityPipeline );
			if ( velocityPosedPipeline != 0 ) CoreRenderDevice.DestroyPipeline( velocityPosedPipeline );

			var vs = CoreShaderCache.Pass( EngineShaders.Velocity, "MainVs",
				CoreShaderCompiler.Stage.Vertex );

			var posedVs = CoreShaderCache.Pass( EngineShaders.Velocity, "PosedVs",
				CoreShaderCompiler.Stage.Vertex );

			var ps = CoreShaderCache.Pass( EngineShaders.Velocity, "MainPs",
				CoreShaderCompiler.Stage.Pixel );

			velocityPipeline = vs is null || ps is null
				? 0
				: CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.Engine,
					depthTest: true, CoreRender.Topology.Triangles, blend: false,
					depthWrite: false, target: CoreRender.TargetKind.Velocity );

			velocityPosedPipeline = posedVs is null || ps is null
				? 0
				: CoreRenderDevice.CreatePipeline( posedVs, ps, CoreRender.VertexLayoutKind.None,
					depthTest: true, CoreRender.Topology.Triangles, blend: false,
					depthWrite: false, target: CoreRender.TargetKind.Velocity );

			if ( velocityPipeline == 0 || velocityPosedPipeline == 0 )
				Log.Warning( $"the velocity pass did not compile: {CoreRenderDevice.ShaderError}" );
		}
		if ( pyramidPipeline == 0 )
		{
			var pyramid = CoreShaderCache.Pass( EngineShaders.DepthPyramid, "main",
				CoreShaderCompiler.Stage.Compute );

			//Its own maker: a pyramid level is read and written through set one, not a material
			pyramidPipeline = pyramid is null ? 0 : CoreRenderDevice.CreatePyramidPipeline( pyramid );
			if ( pyramidPipeline == 0 )
				Log.Warning( $"the depth pyramid did not compile: {CoreRenderDevice.ShaderError}" );
		}

		composePipeline = BakePost( EngineShaders.Compose, CoreRender.TargetKind.Screen );

		//The same pass into the finished picture, for frames with something after - see Stage.AfterPostProcess
		composeFinalPipeline = BakePost( EngineShaders.Compose, CoreRender.TargetKind.Final );

		//The compose with the engine's curve, made here not on the first frame, where an uncached compile would hitch
		CorePointChain.WarmUp();
	}

	//A full-screen post pass out of its source: three vertices out of nothing, no depth.
	private static ulong BakePost( string file, CoreRender.TargetKind target, string defines = null )
	{
		var vs = CoreShaderCache.Pass( file, "MainVs", CoreShaderCompiler.Stage.Vertex, defines );
		var ps = CoreShaderCache.Pass( file, "MainPs", CoreShaderCompiler.Stage.Pixel, defines );

		if ( vs is null || ps is null )
		{
			Log.Warning( $"{file} did not compile: {CoreRenderDevice.ShaderError}" );
			return 0;
		}

		//No vertex buffer, no depth: three vertices out of nothing, covering everything.
		return CoreRenderDevice.CreatePipeline( vs, ps,
			CoreRender.VertexLayoutKind.None, depthTest: false,
			CoreRender.Topology.Triangles, blend: false,
			depthWrite: false, target: target );
	}

	//The four composes with the upscale in them, the first time a frame wants one. A bake is a compile
	//on a machine with no cache, so this is a hitch on the frame the render scale drops - once.
	private static void EnsureCarryCompose()
	{
		if ( composeCarryTried ) return;

		composeCarryTried = true;

		composeCarryPipeline = BakePost( EngineShaders.Compose, CoreRender.TargetKind.Screen, EngineShaders.Carry );
		composeCarryFinalPipeline = BakePost( EngineShaders.Compose, CoreRender.TargetKind.Final, EngineShaders.Carry );

		if ( composeCarryPipeline == 0 )
			Log.Warning( $"the upscaling compose did not bake: {CoreRenderDevice.ShaderError}" );
	}

	//Drop the sky's pipelines so the next frame bakes them again - what an edited sky shader,
	//or an edited include under it, needs. See CoreResources.Forget, which is the one caller.
	internal static void ForgetSkyPipelines()
	{
		foreach ( var pipeline in skyPipelines.Values ) CoreRenderDevice.DestroyPipeline( pipeline );
		foreach ( var pipeline in skyChainPipelines.Values ) CoreRenderDevice.DestroyPipeline( pipeline );

		foreach ( var pipeline in skyTablePipelines.Values ) CoreRenderDevice.DestroyPipeline( pipeline );

		skyPipelines.Clear();
		skyChainPipelines.Clear();
		skyTablePipelines.Clear();
		CoreSkyLight.ForgetPipelines();
		CoreLight2D.ForgetPipelines();
	}

	//Only the background pass is made upfront; sky pipelines bake on demand per frame specialization
	private static void MakeSkyPipelines()
	{
		MakeBackgroundPipeline();
	}

	//Pipeline filling the sky table from a sky shader, baked on first ask; unspecialized, it's tiny
	private static ulong SkyTablePipeline( string shader )
	{
		if ( string.IsNullOrWhiteSpace( shader ) ) return 0;

		if ( skyTablePipelines.TryGetValue( shader, out var made ) ) return made;

		var stages = CoreResources.Shader( shader );

		var vertex = stages?[(int)CoreShaderFile.Stage.SkyTableVertex];
		var pixel = stages?[(int)CoreShaderFile.Stage.SkyTablePixel];

		if ( vertex is null || pixel is null )
		{
			//Not cached: a non-sky shader or one not yet loaded from a mounting filesystem answers this way too
			return 0;
		}

		//From here the answer is kept whatever it is: a sky whose table will not bake costs one
		//try and then draws without an ambient of its own, rather than a compile every frame.

		made = CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
			depthTest: false, CoreRender.Topology.Triangles, blend: false, depthWrite: false,
			cullBack: false, target: CoreRender.TargetKind.SkyTable );

		if ( made == 0 )
			Log.Warning( $"the sky's table did not compile for {shader}: {CoreRenderDevice.ShaderError}" );

		skyTablePipelines[shader] = made;

		return made;
	}

	//Pipeline drawing a sky shader into a target, baked once (failures cached); stereo for both eyes
	private static ulong SkyPipeline( string shader, CoreRender.TargetKind target, int specialize, bool stereo )
	{
		var key = (shader, target, specialize, stereo);

		if ( skyPipelines.TryGetValue( key, out var made ) ) return made;

		skyPipelines[key] = 0;

		var stages = CoreResources.Shader( shader, null, stereo );

		if ( stages is null )
		{
			if ( stereo ) CoreSceneRenderer.StereoRefused( $"the sky {shader} has no two-view build" );
			return 0;
		}

		var vertex = stages[(int)CoreShaderFile.Stage.Vertex];
		if ( vertex is null ) return 0;

		//Depth tested but not written (sky is at the far plane), and no normals output
		ulong Bake( byte[] pixel, CoreRender.TargetKind target )
			=> pixel is null
				? 0
				: CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
					depthTest: true, CoreRender.Topology.Triangles, blend: false,
					depthWrite: false, target: target, colourAlone: true, specialize: specialize );

		//The screen's own stage, not the plain one: a sky drawn onto the window is the last pass
		//over those pixels, so the curve is its own business - see CoreShaderFile.Stage.ScreenPixel.
		made = target == CoreRender.TargetKind.Scene
			? Bake( stages[(int)CoreShaderFile.Stage.ScenePixel], CoreRender.TargetKind.Scene )
			: Bake( stages[(int)CoreShaderFile.Stage.ScreenPixel], CoreRender.TargetKind.Screen );

		if ( made == 0 )
			Log.Warning( $"the sky shader {shader} did not bake: {CoreRenderDevice.ShaderError}" );

		skyPipelines[key] = made;

		return made;
	}

	//The sky onto the screen with a run of point effects carried in it - see FoldChain.
	//Nought while it compiles beside the frame, and for good where it would not.
	private static ulong SkyChainPipeline( string shader, string key, int specialize )
	{
		if ( key is null ) return 0;

		var held = shader + "|" + key + "|" + specialize;

		if ( skyChainPipelines.TryGetValue( held, out var made ) ) return made;

		var vertex = CoreResources.Shader( shader )?[(int)CoreShaderFile.Stage.Vertex];
		if ( vertex is null ) return 0;

		var pixel = CoreResources.ScreenChainWords( shader, null, CoreShaderFile.Stage.ScreenPixel, key, out var waiting );

		if ( pixel is null )
		{
			if ( waiting ) return 0;

			CorePointChain.Refuse( key );
		}
		else
		{
			made = CoreRenderDevice.CreatePipeline( vertex, pixel, CoreRender.VertexLayoutKind.None,
				depthTest: true, CoreRender.Topology.Triangles, blend: false,
				depthWrite: false, target: CoreRender.TargetKind.Screen, colourAlone: true,
				specialize: specialize );
		}

		skyChainPipelines[held] = made;

		return made;
	}

	//Background pass for skyless frames: depth-tested, no depth write, reads only the frame block
	private static void MakeBackgroundPipeline()
	{
		if ( backgroundPipeline != 0 ) return;

		var vs = CoreShaderCache.Pass( EngineShaders.Background, "MainVs", CoreShaderCompiler.Stage.Vertex );
		var ps = CoreShaderCache.Pass( EngineShaders.Background, "MainPs", CoreShaderCompiler.Stage.Pixel );

		if ( vs is null || ps is null )
		{
			Log.Warning( $"the background pass did not compile: {CoreRenderDevice.ShaderError}" );
			return;
		}

		backgroundPipeline = CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None,
			depthTest: true, CoreRender.Topology.Triangles, blend: false,
			depthWrite: false, target: CoreRender.TargetKind.Screen );

	}

	//Fills what the world left with the camera's background colour, for a scene with no sky
	//that is being shaded straight onto the window - see shaders/engine/background.slang.
	private void DrawBackground( CameraComponent camera, float curve )
	{
		if ( backgroundPipeline == 0 ) return;

		var colour = camera.IsValid() ? camera.BackgroundColor : Color.Black;

		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 0 ), colour.r );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 4 ), colour.g );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 8 ), colour.b );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 12 ), 1.0f );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 16 ), exposure );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 20 ), curve );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 24 ), 0.0f );
		BitConverter.TryWriteBytes( backgroundPush.AsSpan( 28 ), 0.0f );

		CoreRenderDevice.Draw( backgroundPipeline, 3, backgroundPush );
	}

	//The background pass's push block, kept rather than made every frame.
	private readonly byte[] backgroundPush = new byte[32];

	private void DrawSky()
	{
		//This sky's reachable features, baked into its pipeline (SkySpecialize); read once so both asks agree
		var specialize = CoreSceneRenderer.SkySpecialize( skyCore );

		var pipeline = SkyPipeline( skyShader, worldTarget, specialize, StereoPass );

		//And with the frame's point effects in it, where the world's shading carries
		//them. Not into both eyes at once: that run is only ever built for one view.
		if ( worldTarget != CoreRender.TargetKind.Scene && worldChain is not null && !StereoPass )
		{
			var chained = SkyChainPipeline( skyShader, worldChain, specialize );
			if ( chained != 0 ) pipeline = chained;
		}

		if ( pipeline == 0 ) return;

		//With the sky material's numbers bound, which its shader reads - see SkyBox2D.SkyMaterial
		CoreRenderDevice.Draw( pipeline, 3, System.Array.Empty<byte>(), skyDevice );
	}

	//Whether anything is hung on a stage: a post-process or a camera command list
	private static bool Hung( CameraComponent camera, Rinten.Rendering.Stage stage )
		=> ( camera.PostProcess?.Count( stage ) ?? 0 ) > 0 ||
			camera.CommandListCount( stage ) > 0;

	private readonly List<(int Order, int At, Rinten.Rendering.CommandList List)> stageLists = new();

	//Runs everything hung on a stage: post-process and camera lists sorted by order, then components
	private void RunLists( CameraComponent camera, Rinten.Rendering.Stage stage, bool beforeWindow = false )
	{
		stageLists.Clear();

		camera.PostProcess?.ListsAt( stage, stageLists );
		camera.CommandListsAt( stage, stageLists );

		stageLists.Sort( static ( a, b ) => a.Order != b.Order ? a.Order.CompareTo( b.Order ) : a.At.CompareTo( b.At ) );

		//After the compose, the last pass of the last non-empty list may draw onto the window - see CorePostBlit.Lands
		var last = -1;

		for ( var i = stageLists.Count - 1; beforeWindow && i >= 0; i-- )
		{
			if ( stageLists[i].List.IsEmpty ) continue;

			last = i;
			break;
		}

		for ( var i = 0; i < stageLists.Count; i++ )
		{
			var (order, _, list) = stageLists[i];

			//A HUD is painted at window pixels and would stick to a headset's lens; there a ScreenPanel is a sheet
			if ( Eye is not null && ( list.Flags & Rinten.Rendering.CommandList.Flag.Hud ) != 0 ) continue;

			CorePasses.Unwant();
			CorePostBlit.ListOrder = order;
			CorePostBlit.LastBeforeWindow = i == last;
			list.ExecuteOnRenderThread();
		}

		CorePostBlit.LastBeforeWindow = false;
		CorePasses.Unwant();
	}

	private void RunStage( CameraComponent camera, Rinten.Rendering.Stage stage,
		CoreRender.TargetKind into, float near, float far, bool settle = true,
		(CoreRender.TargetKind First, CoreRender.TargetKind Second)? pair = null,
		ulong reads = 0, bool holds = false )
	{
		if ( camera is null ) return;
		if ( camera.PostProcess is null ) return;

		//Nothing hung on this stage, the usual case, asked before any setup
		if ( !Hung( camera, stage ) ) return;

		var finished = pair is { First: CoreRender.TargetKind.Final };
		var onDisplay = pair is { First: CoreRender.TargetKind.Display };

		OpenPost( stage, into, near, far, pair, reads, holds, onDisplay ? displayIn : null );

		try
		{
			CoreRenderDevice.ZoneBegin( "post pass" );

			RunLists( camera, stage, beforeWindow: finished );

			//Point effects not outliving the stage draw now, before asking where the frame is; after the copy back
			//they landed in the other picture and showed nothing
			if ( !holds ) CorePostBlit.Flush();

			//On the window-size pair the frame stays wherever the chain left it - the compose reads either of the two.
			if ( onDisplay )
			{
				displayIn = CorePostBlit.Settled;
			}
			else if ( into != CoreRender.TargetKind.Screen && !finished )
			{
				//An odd effect count leaves the frame in the other picture; one pass moves it back where untold
				if ( settle && CorePostBlit.Settled == CoreRender.TargetKind.Post )
					CopyBack();

				frameIn = CorePostBlit.Settled;
			}
		}
		catch ( System.Exception e )
		{
			Log.Warning( e, $"a post-process at {stage}" );
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
			CorePostBlit.End();
		}
	}

	//Opens the frame a stage's passes draw in: the size they draw at and the pictures they may read - see RunStage.
	private void OpenPost( Rinten.Rendering.Stage stage, CoreRender.TargetKind into, float near, float far,
		(CoreRender.TargetKind First, CoreRender.TargetKind Second)? pair = null, ulong reads = 0, bool holds = false,
		CoreRender.TargetKind? heldIn = null )
	{
		var (width, height) = size;

		//Finished pictures are window-sized whatever the world's render scale was
		var finished = pair is { First: CoreRender.TargetKind.Final };

		//And so is the world's own pair at the window's size - see TargetKind.Display.
		var windowSized = finished || pair is { First: CoreRender.TargetKind.Display or CoreRender.TargetKind.DisplaySecond };

		if ( into != CoreRender.TargetKind.Screen && !windowSized )
		{
			var world = CoreRenderDevice.WorldSize();

			if ( world.x >= 1.0f && world.y >= 1.0f )
			{
				width = (int)world.x;
				height = (int)world.y;
			}
		}

		//What the frame is read as: the world's picture before the compose, after it the finished one
		var colour = reads != 0
			? reads
			: windowSized
				? CoreRenderDevice.TargetTexture( viewport, pair.Value.First )
				: CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene );

		CorePostBlit.Begin( new CorePostBlit.Frame(
			into,
			Indexed( colour ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 1 ) ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 2 ) ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Velocity ) ),
			width, height, near, far, (float)(int)stage, viewport ),
			heldIn ?? ( windowSized ? pair.Value.First : frameIn ), pair, holds );
	}

	//Fallback when the compose can't compile the chain: draw effects as passes before a plain compose
	private void ComposeSeparately( List<CorePointChain.Point> chain, int split, float near, float far )
	{
		if ( chain.Count <= 1 ) return;

		OpenPost( Rinten.Rendering.Stage.Tonemapping, CoreRender.TargetKind.Scene, near, far );

		try
		{
			for ( var i = 0; i < chain.Count; i++ )
			{
				if ( i == split - 1 ) continue;

				CorePostBlit.Single( chain[i] );
			}

			frameIn = CorePostBlit.Settled;
		}
		finally
		{
			CorePostBlit.End();
		}
	}

	//The window-size pair as the stages before the compose run over it - see TargetKind.Display.
	private static readonly (CoreRender.TargetKind First, CoreRender.TargetKind Second) displayPair =
		( CoreRender.TargetKind.Display, CoreRender.TargetKind.DisplaySecond );

	//Which of the window-size pair the frame is in - see IntoDisplay and RunStage.
	private CoreRender.TargetKind displayIn = CoreRender.TargetKind.Display;

	//Whether anything is hung on the stages run over the frame before the compose.
	private bool HungBeforeCompose( CameraComponent camera )
	{
		if ( camera?.PostProcess is null ) return false;

		foreach ( var stage in beforePostStages )
			if ( Hung( camera, stage ) ) return true;

		return Hung( camera, Rinten.Rendering.Stage.Tonemapping );
	}

	//The upscaler's picture into the window pair for effects, as CopyBack; false and composed without effects where not
	private bool IntoDisplay( ulong upscaledTexture, float near, float far )
	{
		if ( upscaledTexture == 0 ) return false;

		CoreRenderDevice.ReadyToRead( upscaledTexture );

		//Read out of the upscaler's picture, drawn into the pair's first.
		OpenPost( Rinten.Rendering.Stage.BeforePostProcess, CoreRender.TargetKind.Display, near, far,
			( CoreRender.TargetKind.DisplaySecond, CoreRender.TargetKind.Display ), upscaledTexture,
			holds: true, heldIn: CoreRender.TargetKind.DisplaySecond );

		try
		{
			CopyBack();
			displayIn = CorePostBlit.Settled;
			return copyBack is not null;
		}
		finally
		{
			CorePostBlit.End();
		}
	}

	//The compose's material over one of the window-size pair, made once a picture.
	private ulong DisplayInput( CoreRender.TargetKind kind )
	{
		var which = kind == CoreRender.TargetKind.DisplaySecond ? 1 : 0;
		var picture = CoreRenderDevice.TargetTexture( viewport, kind );

		if ( picture != displayInputFrom[which] )
		{
			if ( displayInputs[which] != 0 ) CoreRenderDevice.DestroyTexture( displayInputs[which] );

			displayInputFrom[which] = picture;
			displayInputs[which] = picture == 0 ? 0 : CoreRenderDevice.CreateMaterial( picture, picture, picture, picture, 1, 0, 0, 0 );
		}

		return displayInputs[which];
	}

	private readonly ulong[] displayInputs = new ulong[2];
	private readonly ulong[] displayInputFrom = new ulong[2];

	private void CopyBack()
	{
		copyBack ??= Material.FromShader( "shaders/postprocess/copy.shader" );

		if ( copyBack is null ) return;

		CorePostBlit.Draw( copyBack, null );
	}

	//Held, because a material is a device object and not a value.
	private Material copyBack;

	//Copies the finished picture onto the window when post-compose passes ran offscreen
	private void FinalToScreen( float near, float far )
	{
		//The chain's last pass drew onto the window itself - see CorePostBlit.Lands.
		if ( CorePostBlit.Landed )
		{
			CorePostBlit.Landed = false;
			return;
		}

		var settled = CorePostBlit.Settled;
		var texture = CoreRenderDevice.TargetTexture( viewport, settled );

		if ( texture == 0 ) return;

		var (width, height) = size;

		//The pair is that one picture twice: nothing alternates here, there is one pass and it draws on the window.
		CorePostBlit.Begin( new CorePostBlit.Frame(
			CoreRender.TargetKind.Screen,
			Indexed( texture ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 1 ) ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene, 2 ) ),
			Indexed( CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Velocity ) ),
			width, height, near, far, (float)(int)Rinten.Rendering.Stage.AfterPostProcess, viewport ),
			settled, ( settled, settled ) );

		try
		{
			//Post-compose point effects laid on the way; a plain copy where there are none
			if ( CorePostBlit.Pending ) CorePostBlit.Flush();
			else CopyBack();
		}
		finally
		{
			CorePostBlit.End();
		}
	}

	//A stage inside the world pass: close it so effects can read it, then reopen keeping colour and depth
	private void Between( CameraComponent camera, bool offscreen, float near, float far,
		Rinten.Rendering.Stage stage )
	{
		if ( !offscreen ) return;
		if ( camera?.PostProcess is null ) return;
		if ( !Hung( camera, stage ) ) return;

		CoreRenderDevice.EndTarget();

		RunStage( camera, stage, CoreRender.TargetKind.Scene, near, far );

		//What reflections left for water and glass (the traced chain and the solid world copy), laid before they draw
		//- see ScreenSpaceReflections.SceneCopy
		if ( stage == Rinten.Rendering.Stage.AfterOpaque && frameNow is not null && translucent.Count > 0 &&
			CorePasses.Find( ScreenSpaceReflections.DepthPicture ) is { Index: > 0 } chain )
		{
			OpenPost( stage, CoreRender.TargetKind.Scene, near, far );

			try
			{
				ScreenSpaceReflections.SceneCopy.ExecuteOnRenderThread();
			}
			finally
			{
				CorePostBlit.End();
			}

			if ( CorePasses.Find( ScreenSpaceReflections.ScenePicture ) is { Index: > 0 } solid )
			{
				CoreSceneRenderer.LayScreenTrace( frameNow, chain.Index, solid.Index );
				CoreRenderDevice.SetupFrame( frameNow );
			}
		}

		if ( !CoreRenderDevice.BeginTarget( CoreRender.TargetKind.Scene,
			camera.BackgroundColor, clearFirst: false ) )
		{
			Log.Warning( $"the world's pass would not reopen after {stage}: {CoreRenderDevice.Description}" );
		}
	}

	//Runs every stage between two points in order; a stage is where a project may hang a full-screen pass
	private void RunStages( CameraComponent camera, CoreRender.TargetKind into,
		float near, float far, bool settle, params Rinten.Rendering.Stage[] stages )
	{
		//Nothing reopens the world pass after these, so a point effect is held for the frame's next reader
		foreach ( var stage in stages ) RunStage( camera, stage, into, near, far, settle, holds: true );
	}

	//The pre-post stages as one array for the process, not a params array per call
	private static readonly Rinten.Rendering.Stage[] beforePostStages =
	{
		Rinten.Rendering.Stage.AfterTransparent,
		Rinten.Rendering.Stage.AfterViewmodel,
		Rinten.Rendering.Stage.BeforePostProcess,
	};

	//Where a picture sits in the table a shader indexes.
	internal static int Indexed( ulong texture )
		=> texture == 0 ? 0 : (int)CoreRender.TextureIndex( texture );

	//The material the compose reads the frame through. A material is four textures in one
	//set; the compose reads one of them, and every other picture by its place in the table.
	private void MakePostInputs()
	{
		DropPostInputs();

		var colour = CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene );
		postBuiltFrom = colour;

		//The numbers are nobody's business here - this material exists to carry a picture, not to describe a surface.
		if ( colour != 0 )
			composeInputs = CoreRenderDevice.CreateMaterial( colour, colour, colour, colour, 1, 0, 0, 0 );
	}


	//Whether post-chain passes can read the frame from the other picture; false until it exists
	private bool PostInputsReady()
	{
		var post = CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Post );

		if ( post == 0 ) return false;

		if ( post != postBuiltFromPost ) MakePostInputsPost( post );

		return composeInputsPost != 0;
	}

	//The compose's reader of the frame, built against the other picture.
	private void MakePostInputsPost( ulong post )
	{
		DropPostInputsPost();

		postBuiltFromPost = post;

		composeInputsPost = CoreRenderDevice.CreateMaterial( post, post, post, post, 1, 0, 0, 0 );
	}

	private void DropPostInputsPost()
	{
		if ( composeInputsPost != 0 ) CoreRenderDevice.DestroyTexture( composeInputsPost );

		composeInputsPost = 0;
		postBuiltFromPost = 0;
	}

	private void DropPostInputs()
	{
		//A material is destroyed as a texture; they share handles and it owns none of its images
		if ( composeInputs != 0 ) CoreRenderDevice.DestroyTexture( composeInputs );

		composeInputs = 0;
		postBuiltFrom = 0;


		//The world's pictures were remade, so the other one was too.
		DropPostInputsPost();
	}

	//The game's screen panels, walked after their own layout and redrawn on top of the world - which is what a HUD is.
	private void DrawGameUi( Scene scene )
	{
		var (width, height) = size;

		//Other cameras' boxes each frame; Recycle keeps their vertex lists
		CoreUiRenderer.Recycle( batches );
		CoreUiRenderer.BuildCameraLayers( scene, batches );

		screenBatches.Clear();
		screenBatches.AddRange( batches );

		//Each screen's panels as last walked while its tree held still, else walked again: the kept path
		foreach ( var screen in scene.GetAllComponents<ScreenPanel>() )
		{
			if ( !screen.IsValid() || screen.GetPanel() is not Rinten.UI.RootPanel root ) continue;

			//A sheet in front of a headset is drawn in the eyes - see DrawHeadsetPanels.
			if ( root is GameRootPanel { InHeadset: true } ) continue;

			screenBatches.AddRange( ScreenPanelBatches( root ) );
		}

		DrawPanels( screenBatches, width, height );
	}

	//What DrawGameUi draws this frame: the cameras' boxes, then every screen's kept batches.
	private static readonly System.Collections.Generic.List<CoreUiRenderer.UiBatch> screenBatches = new();

	//A screen's batches, kept while nothing they were made from has moved - see CoreUiRenderer.TryReuseFrame.
	private static System.Collections.Generic.List<CoreUiRenderer.UiBatch> ScreenPanelBatches( Rinten.UI.RootPanel root )
	{
		root.Frame ??= new();

		//Uncut: batches a headset's sheet cut to its edge are not the screen's.
		if ( root.Frame.Cut == default && CoreUiRenderer.TryReuseFrame( root, UploadUiTexture ) )
			return root.Frame.Batches;

		root.Frame.Kept = false;
		CoreUiRenderer.Recycle( root.Frame.Batches );
		CoreUiRenderer.Build( root, root.Frame.Batches, RasteriseCached, UploadUiTexture );
		CoreUiRenderer.KeepFrame( root );
		root.Frame.Cut = default;

		KeepOnDevice( root );

		return root.Frame.Batches;
	}

	//Draws a panel tree into the open frame at this size, through the same UI pass the game uses
	internal static void DrawUi( Rinten.UI.Panel root, int width, int height,
		System.Action<Rect> world = null )
	{
		if ( !EnsureResources() ) return;

		//The editor's own windows hold their batches themselves, so a kept frame is not written over by the next
		//window's walk. Anything else - the game played inside the editor included - goes the ordinary way below.
		if ( root is Rinten.UI.RootPanel { IsEditorUi: true } editor )
		{
			BuildEditorUi( editor, keepable: world is null );

			using ( Diagnostics.Performance.Scope( "ui record" ) )
			{
				//With the world: an editor window is the only kind holding it
				DrawPanels( editor.Frame.Batches, width, height, world );
			}

			return;
		}

		//Handed back before the walk, so batches stay readable as long as anything might look
		CoreUiRenderer.Recycle( windowBatches );

		//Building walks panels into geometry, recording hands it to the device; per-frame rebuilds show in the first,
		//per-panel batches in the second
		using ( Diagnostics.Performance.Scope( "ui build" ) )
		{
			CoreUiRenderer.Build( root, windowBatches, RasteriseCached, UploadUiTexture );
		}

		using ( Diagnostics.Performance.Scope( "ui record" ) )
		{
			DrawPanels( windowBatches, width, height, world );
		}
	}

	//What BuildEditorUi made of a window's panels this frame.
	internal enum UiBuilt
	{
		/// <summary>Walked afresh: the panels moved, or something in them did.</summary>
		Walked,

		/// <summary>Last frame's batches as they stand, over a picture that has new pixels.</summary>
		KeptChanged,

		/// <summary>Last frame's batches and pictures unchanged; the window's picture is already on screen.</summary>
		KeptSame,
	}

	//Builds an editor window's UI batches, or reuses last frame's; never reused while the world is inline
	internal static UiBuilt BuildEditorUi( Rinten.UI.RootPanel editor, bool keepable )
	{
		editor.Frame ??= new();

		using var building = Diagnostics.Performance.Scope( "ui build" );

		if ( keepable && CoreUiRenderer.TryReuseFrame( editor, UploadUiTexture ) )
			return editor.Frame.Changed ? UiBuilt.KeptChanged : UiBuilt.KeptSame;

		//Unkept until the walk finishes: a walk that throws must not leave a half-built list under the old key.
		editor.Frame.Kept = false;
		CoreUiRenderer.Recycle( editor.Frame.Batches );
		CoreUiRenderer.Build( editor, editor.Frame.Batches, RasteriseCached, UploadUiTexture );
		CoreUiRenderer.KeepFrame( editor );

		return UiBuilt.Walked;
	}

	//An editor window's batches, as BuildEditorUi last left them, into the open frame - without the world.
	internal static void DrawEditorUi( Rinten.UI.RootPanel editor, int width, int height )
	{
		if ( editor.Frame is null || !EnsureResources() ) return;

		using ( Diagnostics.Performance.Scope( "ui record" ) )
		{
			DrawPanels( editor.Frame.Batches, width, height );
		}
	}

	//The world, and whatever an editor window paints over it, into the frame of a layer lying over the world's place at
	//origin on the window. What is painted before the world is the window's own, under the layer, and not drawn here.
	internal static void DrawEditorUiOverWorld( Rinten.UI.RootPanel editor, Vector2 origin, int width, int height,
		System.Action<Rect> world )
	{
		if ( editor.Frame is null || !EnsureResources() ) return;

		using ( Diagnostics.Performance.Scope( "ui record" ) )
		{
			DrawPanels( editor.Frame.Batches, width, height, world, origin, fromWorld: true );
		}
	}

	//Draws a still of the world plus the editor UI over it, for screenshots of a layered-world window
	internal static void DrawEditorUiOverPicture( Rinten.UI.RootPanel editor, int width, int height, ulong world )
	{
		if ( editor.Frame is null || world == 0 || !EnsureResources() ) return;

		using ( Diagnostics.Performance.Scope( "ui record" ) )
		{
			DrawPanels( editor.Frame.Batches, width, height, area =>
			{
				CoreUiRenderer.Recycle( pictureBatches );
				CoreUiRenderer.Picture( pictureBatches, area, world );
				DrawPanels( pictureBatches, width, height );
			}, fromWorld: true );
		}
	}

	//The one box the still of the world is drawn with - see DrawEditorUiOverPicture.
	private static readonly System.Collections.Generic.List<CoreUiRenderer.UiBatch> pictureBatches = new();

	//The batches of the game's panels, reused every frame.
	private static readonly System.Collections.Generic.List<CoreUiRenderer.UiBatch> batches = new();

	//And a window's own, apart from the game's: the world is drawn from inside a window's walk, and the
	//game's panels are part of the world. One list for both was cleared under the window's own iteration.
	private static readonly System.Collections.Generic.List<CoreUiRenderer.UiBatch> windowBatches = new();

	//The batches in order; origin is the target's corner on the window (nothing for a window, the layer's place
	//for a layer); from the world on, only what follows its marker draws
	private static void DrawPanels( System.Collections.Generic.List<CoreUiRenderer.UiBatch> batches,
		int width, int height, System.Action<Rect> world = null, Vector2 origin = default, bool fromWorld = false )
	{
		if ( uiPipeline == 0 || whiteTexture == 0 ) return;
		if ( width <= 0 || height <= 0 ) return;

		if ( batches.Count == 0 ) return;

		var ortho = new System.Numerics.Matrix4x4
		{
			M11 = 2.0f / width,
			M22 = 2.0f / height,
			M33 = 1.0f,
			M41 = -1.0f - ( 2.0f * origin.x / width ),
			M42 = -1.0f - ( 2.0f * origin.y / height ),
			M44 = 1.0f,
		};

		//The UI shader also needs target size and window offset (clips are in window pixels) and screen mode
		Span<byte> push = stackalloc byte[UiPushSize];
		UiPush( push, ortho, new System.Numerics.Vector4( width, height, origin.x, origin.y ), inWorld: false );

		CoreRenderDevice.ZoneBegin( "ui panels" );

		var reached = !fromWorld;

		foreach ( var batch in batches )
		{
			//The world where a panel wears it (Panel.WearsWorld); its passes reopen the screen, later panels on top
			if ( batch.Texture == CoreUiRenderer.WorldTexture )
			{
				reached = true;

				CoreRenderDevice.ZoneEnd();
				world?.Invoke( batch.Area );
				CoreRenderDevice.ZoneBegin( "ui panels" );
				continue;
			}

			if ( !reached || batch.Vertices.Count == 0 ) continue;

			var texture = batch.Texture;

			if ( texture == CoreUiRenderer.BackdropTexture )
			{
				//Backdrop is captured here, in draw order, so it includes panels already drawn below this one
				if ( !CoreRenderDevice.CaptureBackdrop() ) continue;

				texture = CoreRenderDevice.BackdropTexture();

				//No copy, nothing to filter: the panel drops out rather than drawing a stale slot
				if ( texture == 0 ) continue;
			}

			DrawUiBatch( uiPipeline, batch, texture != 0 ? texture : whiteTexture, push );
		}

		CoreRenderDevice.ZoneEnd();
	}

	//One panel batch, from its kept device buffer or written into the frame ring - see KeepOnDevice
	private static void DrawUiBatch( ulong pipeline, in CoreUiRenderer.UiBatch batch, ulong texture, ReadOnlySpan<byte> push )
	{
		if ( batch.Buffer != 0 )
		{
			var draw = new CoreBufferDraw
			{
				Pipeline = pipeline,
				Vertices = batch.Buffer,
				First = batch.First,
				Count = batch.Vertices.Count,
				Texture = texture,
			};

			if ( CoreRenderDevice.DrawBuffers( draw, push ) ) return;
		}

		CoreRenderDevice.DrawDynamic( pipeline,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices ) ),
			batch.Vertices.Count, UiVertexStride, push, texture );
	}

	//Uploads an engine texture to our device once via the CPU; fine for still images only
	private static ulong UploadUiTexture( Texture texture )
	{
		if ( texture?.core is null ) return 0;

		return texture.core.Device();
	}

	//The gizmo text cache, lent to the UI - same strings, same textures, one eviction policy.
	private static (ulong Texture, Vector2 Size) RasteriseCached( in TextRendering.Scope scope )
	{
		var key = System.HashCode.Combine( scope );

		RetireOldText();

		if ( textCache.TryGetValue( key, out var cached ) )
		{
			//A cache hit counts as a use, or the sweeper would keep evicting and re-rasterising the label
			cached.Used = textFrame;
			textCache[key] = cached;

			return (cached.Texture, cached.Size);
		}

		//Only the miss: a hit is a dictionary lookup and putting a zone round it would cost more than it measures.
		using ( Diagnostics.Performance.Scope( "ui text" ) )
		{
			var made = RasteriseText( scope );
			textCache[key] = (made.Texture, made.Size, textFrame);

			return made;
		}
	}

	//Screen pixels to clip space for labels, profiler and quads; Vulkan clip Y points down like the screen
	private static System.Numerics.Matrix4x4 ScreenProjection( int width, int height )
		=> new()
		{
			M11 = 2.0f / width,
			M22 = 2.0f / height,
			M33 = 1.0f,
			M41 = -1.0f,
			M42 = -1.0f,
			M44 = 1.0f,
		};

	//One owner's debug drawing that is flat on the screen: shapes in pixels - a box select, the camera speed
	//bar, a picture put on the screen - and the labels, whether at a pixel or over a point in the world.
	private void DrawDebugScreen( DebugDrawList list, System.Numerics.Matrix4x4 viewProjection )
	{
		if ( list is null || screenPipeline == 0 ) return;
		if ( list.Flats.Count == 0 && list.Labels.Count == 0 ) return;

		var (width, height) = size;
		if ( width <= 0 || height <= 0 ) return;

		var push = LinePush( stackalloc byte[80], ScreenProjection( width, height ) );

		DrawFlats( list.Flats, push );
		DrawScreenLabels( list.Labels, viewProjection, width, height, push );
	}

	//Draws screen shapes in runs sharing a pipeline, push and texture, keeping request order
	private void DrawFlats( List<DebugDrawList.Flat> flats, ReadOnlySpan<byte> push )
	{
		if ( flats.Count == 0 || whiteTexture == 0 ) return;

		var pipeline = Pick( screenPipeline, screenTwo );
		var built = 0;
		ulong running = 0;

		foreach ( var flat in flats )
		{
			var picture = flat.Picture is null ? whiteTexture : UploadUiTexture( flat.Picture );
			if ( picture == 0 ) picture = whiteTexture;

			if ( picture != running && built > 0 )
			{
				CoreRenderDevice.DrawDynamic( pipeline,
					System.Runtime.InteropServices.MemoryMarshal.AsBytes( screenScratch.AsSpan( 0, built ) ),
					built, 76, push, running );

				built = 0;
			}

			running = picture;

			if ( screenScratch.Length < built + flat.Count )
				System.Array.Resize( ref screenScratch, Math.Max( 256, ( built + flat.Count ) * 2 ) );

			flat.Span.CopyTo( screenScratch.AsSpan( built ) );
			built += flat.Count;
		}

		if ( built > 0 )
		{
			CoreRenderDevice.DrawDynamic( pipeline,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( screenScratch.AsSpan( 0, built ) ),
				built, 76, push, running );
		}
	}

	//Where the frame's screen shapes are laid end to end - see DrawFlats.
	private Vertex[] screenScratch = System.Array.Empty<Vertex>();

	//Draws screen labels at a pixel or over a world point (skipped behind the eye), from the label cache
	private void DrawScreenLabels( List<DebugDrawList.Label> labels, in System.Numerics.Matrix4x4 viewProjection,
		int width, int height, ReadOnlySpan<byte> push )
	{
		if ( labels.Count == 0 ) return;

		Span<Vertex> corners = stackalloc Vertex[6];
		var white = Color32.White;

		foreach ( var label in labels )
		{
			Vector2 anchor;

			if ( label.Placing == DebugDrawList.Placing.Screen )
			{
				anchor = new Vector2( label.Position.x, label.Position.y );
			}
			else if ( label.Placing == DebugDrawList.Placing.Projected )
			{
				var clip = Clip( viewProjection, label.Position );
				if ( clip.W <= 0.0001f ) continue;

				//The clip volume's y already runs down the screen - see ProjectionMatrix.
				anchor = new Vector2(
					( clip.X / clip.W * 0.5f + 0.5f ) * width,
					( clip.Y / clip.W * 0.5f + 0.5f ) * height ) + label.Offset;
			}
			else
			{
				continue;
			}

			textLabels++;

			var text = RasteriseCached( label.Scope );
			if ( text.Texture == 0 ) continue;

			var (left, top) = Anchored( label.Flags, text.Size );

			//The corners about the anchor, turned if it asks to be.
			var right = new Vector2( 1, 0 );
			var down = new Vector2( 0, 1 );

			if ( label.Angle != 0.0f )
			{
				var radians = label.Angle.DegreeToRadian();
				right = new Vector2( MathF.Cos( radians ), MathF.Sin( radians ) );
				down = new Vector2( -right.y, right.x );
			}

			var topLeft = anchor + ( right * left ) + ( down * top );
			var across = right * text.Size.x;
			var along = down * text.Size.y;

			corners[0] = IconVertex( ToPoint( topLeft ), 0, 0, white );
			corners[1] = IconVertex( ToPoint( topLeft + across ), 1, 0, white );
			corners[2] = IconVertex( ToPoint( topLeft + along ), 0, 1, white );
			corners[3] = corners[2];
			corners[4] = corners[1];
			corners[5] = IconVertex( ToPoint( topLeft + across + along ), 1, 1, white );

			CoreRenderDevice.DrawDynamic( Pick( screenPipeline, screenTwo ),
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( corners ),
				corners.Length, 76, push, text.Texture );
		}

		static Vector3 ToPoint( Vector2 at ) => new( at.x, at.y, 0 );
	}

	//Where a picture this big starts from its anchor: its top-left's offset right and down, per the flags
	private static (float Left, float Top) Anchored( TextFlag flags, Vector2 size )
	{
		var left = flags.Contains( TextFlag.CenterHorizontally ) ? -size.x * 0.5f
			: flags.Contains( TextFlag.Right ) ? -size.x : 0.0f;

		var top = flags.Contains( TextFlag.CenterVertically ) ? -size.y * 0.5f
			: flags.Contains( TextFlag.Bottom ) ? -size.y : 0.0f;

		return (left, top);
	}

	//World labels, turned to the eye or in a plane of their own; a text pixel is Scale metres
	private void DrawWorldLabels( List<DebugDrawList.Label> labels, System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( labels.Count == 0 ) return;

		var pipeline = overlay ? Pick( screenPipeline, screenTwo ) : Pick( billboardPipeline, billboardTwo );
		if ( pipeline == 0 ) return;

		var push = LinePush( stackalloc byte[80], viewProjection, world: true );

		Span<Vertex> corners = stackalloc Vertex[6];
		var white = Color32.White;

		foreach ( var label in labels )
		{
			if ( label.IgnoreDepth != overlay ) continue;
			if ( label.Placing is not ( DebugDrawList.Placing.Facing or DebugDrawList.Placing.Plane ) ) continue;

			textLabels++;

			var text = RasteriseCached( label.Scope );
			if ( text.Texture == 0 ) continue;

			var rotation = label.Placing == DebugDrawList.Placing.Facing ? eyeView.Rotation : label.Rotation;
			var right = rotation.Right;
			var up = rotation.Up;

			var extent = text.Size * label.Scale;
			var (left, top) = Anchored( label.Flags, extent );

			//Down the picture is down the world: v grows as up falls.
			var topLeft = label.Position + ( right * left ) - ( up * top );
			var across = right * extent.x;
			var along = -up * extent.y;

			corners[0] = IconVertex( topLeft, 0, 0, white );
			corners[1] = IconVertex( topLeft + across, 1, 0, white );
			corners[2] = IconVertex( topLeft + along, 0, 1, white );
			corners[3] = corners[2];
			corners[4] = corners[1];
			corners[5] = IconVertex( topLeft + across + along, 1, 1, white );

			CoreRenderDevice.DrawDynamic( pipeline,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( corners ),
				corners.Length, 76, push, text.Texture );
		}
	}

	//Per-frame text stats: labels asked, rasterised and textures released
	internal static long textLabels;
	internal static long textMade;
	internal static long textGone;

	//The keys a sweep lets go of, kept between sweeps
	private static readonly System.Collections.Generic.List<int> textGoing = new();

	private static void RetireOldText()
	{
		if ( textRetiredAt == textFrame ) return;
		textRetiredAt = textFrame;

		if ( textCache.Count == 0 ) return;

		var going = textGoing;
		going.Clear();

		foreach ( var (key, entry) in textCache )
		{
			if ( textFrame - entry.Used <= TextKeptFor ) continue;

			going.Add( key );
		}

		if ( going.Count == 0 ) return;

		foreach ( var key in going )
		{
			textGone++;

			//Kept for a string of the same size to be written into - text that changes is mostly the same size from one to the next
			var gone = textCache[key];

			if ( textSpare.Count < TextSpares ) textSpare.Add( (gone.Texture, (int)gone.Size.x, (int)gone.Size.y) );
			else CoreRenderDevice.DestroyTexture( gone.Texture );
			textCache.Remove( key );
		}
	}

	//Retired strings' textures, written over rather than one made and destroyed a string; on a page each made texture
	//is also a bind group described as text
	private static readonly System.Collections.Generic.List<(ulong Texture, int Width, int Height)> textSpare = new();
	private const int TextSpares = 64;

	//A spare of exactly this size with these pixels written into it; nought where there is none
	private static ulong ReuseText( ReadOnlySpan<byte> pixels, int width, int height )
	{
		for ( var i = 0; i < textSpare.Count; i++ )
		{
			var spare = textSpare[i];
			if ( spare.Width != width || spare.Height != height ) continue;

			textSpare[i] = textSpare[^1];
			textSpare.RemoveAt( textSpare.Count - 1 );

			if ( CoreRenderDevice.UpdateTextureRgba( spare.Texture, pixels, width, height ) ) return spare.Texture;

			CoreRenderDevice.DestroyTexture( spare.Texture );
			return 0;
		}

		return 0;
	}

	//What every string is shaped, drawn and read back in, kept: drawn on the render thread only
	private static readonly TextRendering.TextBlock rasterBlock = new();
	private static Bitmap rasterBitmap;
	private static byte[] rasterPixels = [];

	//Text into pixels by the engine's text machinery, uploaded once; the scope carries the colour
	private static (ulong Texture, Vector2 Size) RasteriseText( in TextRendering.Scope scope )
	{
		var rasterStart = System.Diagnostics.Stopwatch.GetTimestamp();
		var allocatedBefore = Rinten.UI.TextStats.Allocated;

		//Measure with the block that draws it; MeasureText rasterised a second unused texture
		const TextFlag flags = TextFlag.LeftTop | TextFlag.DontClip;
		var block = rasterBlock;
		block.Reuse( scope, flags );
		var measured = block.PictureSize();

		var width = System.Math.Clamp( (int)System.MathF.Ceiling( measured.x ), 1, 1024 );
		var height = System.Math.Clamp( (int)System.MathF.Ceiling( measured.y ), 1, 256 );

		//Into the top left of one bitmap kept for this, grown when a string is bigger than any before it
		if ( rasterBitmap is null || rasterBitmap.Width < width || rasterBitmap.Height < height )
		{
			rasterBitmap?.Dispose();
			rasterBitmap = new Bitmap( System.Math.Max( width, rasterBitmap?.Width ?? 0 ), System.Math.Max( height, rasterBitmap?.Height ?? 0 ) );
		}

		rasterBitmap.ClearArea( width, height );
		rasterBitmap.DrawText( block, new Rect( 0, 0, width, height ) );

		var bytes = width * height * 4;
		if ( rasterPixels.Length < bytes ) rasterPixels = new byte[System.Math.Max( bytes, rasterPixels.Length * 2 )];

		rasterBitmap.CopyArea( width, height, rasterPixels );

		var texture = ReuseText( rasterPixels.AsSpan( 0, bytes ), width, height );
		if ( texture == 0 ) texture = CoreRenderDevice.CreateTextureRgba( rasterPixels.AsSpan( 0, bytes ), width, height, srgb: true );

		Rinten.UI.TextStats.Scope( System.Diagnostics.Stopwatch.GetTimestamp() - rasterStart, (long)width * height * 4 );
		Rinten.UI.TextStats.Garbage( Rinten.UI.TextStats.Allocated - allocatedBefore, label: false );

		return (texture, new Vector2( width, height ));
	}

	//Gizmo sprites as camera-facing quads built here; pixel sizes converted at depth to match picking
	private void DrawSprites( List<DebugDrawList.Sprite> sprites, CameraComponent camera,
		System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( sprites.Count == 0 ) return;

		//One shader; the overlay copy has depth test off, as a handle over the world wants
		var pipeline = overlay ? Pick( screenPipeline, screenTwo ) : Pick( billboardPipeline, billboardTwo );
		if ( pipeline == 0 ) return;

		var (width, height) = size;
		if ( width <= 0 || height <= 0 ) return;

		var right = eyeView.Rotation.Right;
		var up = eyeView.Rotation.Up;

		var push = LinePush( stackalloc byte[80], viewProjection, world: true );

		//One buffer drawn in runs of a shared picture, as mesh tools put thousands of same-texture dots a frame
		var built = 0;
		ulong running = 0;

		//The push block is passed in: it lives on the stack, and a local function cannot hold its caller's span
		void Flush( ReadOnlySpan<byte> block )
		{
			if ( built == 0 || running == 0 ) return;

			CoreRenderDevice.DrawDynamic( pipeline,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( spriteScratch.AsSpan( 0, built ) ),
				built, 76, block, running );

			built = 0;
		}

		foreach ( var sprite in sprites )
		{
			if ( sprite.IgnoreDepth != overlay ) continue;

			//White where no picture is named: a plain square, the mesh tools' dots
			var picture = UploadUiTexture( sprite.Picture );
			if ( picture == 0 ) picture = whiteTexture;
			if ( picture == 0 ) continue;

			var half = HalfExtents( sprite, viewProjection, right, up, width, height );
			if ( half.x <= 0.0f || half.y <= 0.0f ) continue;

			//A different picture ends the run, drawn with its texture in asked order
			if ( picture != running )
			{
				Flush( push );
				running = picture;
			}

			if ( spriteScratch.Length < built + 6 )
				System.Array.Resize( ref spriteScratch, System.Math.Max( 96, ( built + 6 ) * 2 ) );

			var r = right;
			var u = up;

			//Turned about the axis it is seen down, which is the only turn a picture facing the camera can be given.
			if ( sprite.Angle != 0.0f )
			{
				var radians = sprite.Angle.DegreeToRadian();
				var cos = System.MathF.Cos( radians );
				var sin = System.MathF.Sin( radians );

				r = ( right * cos ) + ( up * sin );
				u = ( up * cos ) - ( right * sin );
			}

			r *= half.x;
			u *= half.y;

			var at = sprite.Position;
			var tint = (Color32)sprite.Tint;

			//Two triangles, same winding and v-down as the text: the picture's top is v = 0
			spriteScratch[built + 0] = IconVertex( at - r + u, 0, 0, tint );
			spriteScratch[built + 1] = IconVertex( at + r + u, 1, 0, tint );
			spriteScratch[built + 2] = IconVertex( at - r - u, 0, 1, tint );
			spriteScratch[built + 3] = IconVertex( at - r - u, 0, 1, tint );
			spriteScratch[built + 4] = IconVertex( at + r + u, 1, 0, tint );
			spriteScratch[built + 5] = IconVertex( at + r - u, 1, 1, tint );

			built += 6;
		}

		Flush( push );
	}

	//The quads a frame's sprites are built into, kept between them.
	private Vertex[] spriteScratch = System.Array.Empty<Vertex>();

	//Sprite half-extents in metres; screen-space sizes measured through the projection (works for ortho)
	private static Vector2 HalfExtents( in DebugDrawList.Sprite sprite,
		in System.Numerics.Matrix4x4 viewProjection, Vector3 right, Vector3 up, int width, int height )
	{
		if ( sprite.WorldSpace )
			return new Vector2( sprite.Size.x, sprite.Size.y ) * 0.5f;

		var centre = Clip( viewProjection, sprite.Position );

		//Behind the eye, where a projection says nothing anybody can draw with.
		if ( centre.W <= 0.0001f ) return default;

		//Right and up are square to the view, so a step along either leaves w alone
		var alongRight = Clip( viewProjection, sprite.Position + right ).X - centre.X;
		var alongUp = Clip( viewProjection, sprite.Position + up ).Y - centre.Y;

		//Clip to the screen: the volume is two units across whatever the viewport is wide.
		var pixelsPerMetreX = System.MathF.Abs( alongRight / centre.W ) * 0.5f * width;
		var pixelsPerMetreY = System.MathF.Abs( alongUp / centre.W ) * 0.5f * height;

		if ( pixelsPerMetreX <= 0.0001f || pixelsPerMetreY <= 0.0001f ) return default;

		return new Vector2(
			sprite.Size.x * 0.5f / pixelsPerMetreX,
			sprite.Size.y * 0.5f / pixelsPerMetreY );
	}

	//A point in the world, in the clip volume.
	private static System.Numerics.Vector4 Clip( in System.Numerics.Matrix4x4 viewProjection, Vector3 point )
		=> System.Numerics.Vector4.Transform(
			new System.Numerics.Vector4( point.x, point.y, point.z, 1.0f ), viewProjection );

	//The UI shader's push block: the matrix, the target, the mode.
	private const int UiPushSize = 96;

	//The UI pipeline for a non-screen target, made on first use and kept - see DrawBoxes
	private static readonly Dictionary<(CoreRender.TargetKind Target, int Format), ulong> uiTargetPipelines = new();

	private static ulong UiPipelineFor( CoreRender.TargetKind target, int pictureFormat )
	{
		if ( target == CoreRender.TargetKind.Screen ) return uiPipeline;

		var key = ( target, target == CoreRender.TargetKind.Picture ? pictureFormat : 0 );

		if ( uiTargetPipelines.TryGetValue( key, out var kept ) ) return kept;

		var made = 0ul;

		Compile( EngineShaders.Ui, ( vs, ps ) =>
		{
			made = CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.Ui, depthTest: false,
				CoreRender.Topology.Triangles, blend: true, target: target,
				pictureFormat: target == CoreRender.TargetKind.Picture ? pictureFormat : -1 );
		} );

		//Remembered either way: one that will not bake is asked about once.
		uiTargetPipelines[key] = made;

		return made;
	}

	//Draws UI-style boxes into a command list's target, with the same shader as game panels (HUD)
	internal static void DrawBoxes( ReadOnlySpan<CoreUiRenderer.UiVertex> vertices, ulong texture,
		CoreRender.TargetKind target, int pictureFormat, int width, int height )
	{
		if ( vertices.IsEmpty || width <= 0 || height <= 0 ) return;
		if ( !EnsureResources() || whiteTexture == 0 ) return;

		var pipeline = UiPipelineFor( target, pictureFormat );
		if ( pipeline == 0 ) return;

		var ortho = ScreenProjection( width, height );

		Span<byte> push = stackalloc byte[UiPushSize];
		UiPush( push, ortho, new System.Numerics.Vector4( width, height, 0.0f, 0.0f ), inWorld: false );

		CoreRenderDevice.DrawDynamic( pipeline, MemoryMarshal.AsBytes( vertices ), vertices.Length, UiVertexStride, push,
			texture != 0 ? texture : whiteTexture );
	}

	//A string drawn as a picture, out of the cache the gizmo labels and the panels share.
	internal static (ulong Texture, Vector2 Size) TextPicture( in TextRendering.Scope scope ) => RasteriseCached( scope );

	private static void UiPush( Span<byte> into, in System.Numerics.Matrix4x4 toClip,
		in System.Numerics.Vector4 screen, bool inWorld, bool eyeClip = false )
	{
		System.Runtime.InteropServices.MemoryMarshal.Write( into, in toClip );
		System.Runtime.InteropServices.MemoryMarshal.Write( into[64..], in screen );
		System.Runtime.InteropServices.MemoryMarshal.Write( into[80..],
			new System.Numerics.Vector4( inWorld ? 1.0f : 0.0f, eyeClip ? 1.0f : 0.0f, 0, 0 ) );
	}

	//World panel matrix; in a both-eyes pass it's world-only and the shader applies each eye's view
	private (System.Numerics.Matrix4x4 Matrix, bool EyeClip) PanelMatrix( Transform place, in System.Numerics.Matrix4x4 viewProjection )
	{
		var toWorld = (System.Numerics.Matrix4x4)ScenePanelObject.BuildPanelToWorldMatrix( place );

		return BothEyes || ForceEyePush ? (toWorld, true) : (toWorld * viewProjection, false);
	}

	//The batches of the world panel being drawn; one at a time, so one list.
	private static readonly System.Collections.Generic.List<CoreUiRenderer.UiBatch> worldPanelBatches = new();

	//Draws world panels with the screen UI walk and shader through their own matrix; no backdrop filters
	private void DrawWorldPanels( Scene scene, System.Numerics.Matrix4x4 viewProjection )
	{
		if ( uiWorldPipeline == 0 || whiteTexture == 0 || !scene.IsValid() ) return;

		Span<byte> push = stackalloc byte[UiPushSize];

		foreach ( var component in scene.GetAllComponents<Rinten.WorldPanel>() )
		{
			if ( !component.IsValid() || !component.Enabled ) continue;
			if ( component.GetPanel() is not Rinten.UI.WorldPanel panel || !panel.IsValid ) continue;
			if ( panel.SceneObject is not { } sceneObject || !sceneObject.RenderingEnabled ) continue;

			CoreUiRenderer.Recycle( worldPanelBatches );
			CoreUiRenderer.Build( panel, worldPanelBatches, RasteriseCached, UploadUiTexture );

			if ( worldPanelBatches.Count == 0 ) continue;

			var (matrix, eyeClip) = PanelMatrix( panel.Transform, viewProjection );

			var bounds = panel.PanelBounds;
			UiPush( push, matrix, new System.Numerics.Vector4( bounds.Width, bounds.Height, 0, 0 ), inWorld: true, eyeClip );

			foreach ( var batch in worldPanelBatches )
			{
				if ( batch.Vertices.Count == 0 ) continue;
				if ( batch.Texture == CoreUiRenderer.WorldTexture || batch.Texture == CoreUiRenderer.BackdropTexture ) continue;

				ClipToPanel( batch.Vertices, bounds );

				CoreRenderDevice.DrawDynamic( Pick( uiWorldPipeline, uiWorldTwo ),
					System.Runtime.InteropServices.MemoryMarshal.AsBytes(
						System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices ) ),
					batch.Vertices.Count, UiVertexStride, push,
					batch.Texture != 0 ? batch.Texture : whiteTexture );
			}
		}
	}

	//The game's screen panels that are sheets in a headset, in the order they are drawn.
	private static readonly System.Collections.Generic.List<GameRootPanel> headsetPanels = new();

	//Draws screen panels on headset sheets over the world, lowest layer first, without a depth test
	private void DrawHeadsetPanels( Scene scene, System.Numerics.Matrix4x4 viewProjection )
	{
		if ( uiSheetPipeline == 0 || whiteTexture == 0 || !scene.IsValid() ) return;

		headsetPanels.Clear();

		foreach ( var screen in scene.GetAllComponents<ScreenPanel>() )
		{
			if ( !screen.IsValid() || !screen.Enabled ) continue;
			if ( screen.GetPanel() is not GameRootPanel { InHeadset: true } root || !root.IsValid ) continue;

			headsetPanels.Add( root );
		}

		if ( headsetPanels.Count == 0 ) return;

		headsetPanels.Sort( static ( a, b ) => ( a.ComputedStyle?.ZIndex ?? 0 ).CompareTo( b.ComputedStyle?.ZIndex ?? 0 ) );

		Span<byte> push = stackalloc byte[UiPushSize];

		DropGoneSheets();

		foreach ( var root in headsetPanels )
		{
			var bounds = root.PanelBounds;
			HeadsetBatches( root, bounds );

			var batches = root.Frame.Batches;
			if ( batches.Count == 0 ) continue;

			var (matrix, eyeClip) = PanelMatrix( root.Sheet, viewProjection );

			UiPush( push, matrix, new System.Numerics.Vector4( bounds.Width, bounds.Height, 0, 0 ), inWorld: true, eyeClip );

			CoreRenderDevice.ZoneBegin( "sheet draw" );

			//Out of the sheet's own buffer, written once when it was walked: a kept frame's vertices are the same
			//bytes every frame, and are not written into the frame's ring again
			var pipeline = Pick( uiSheetPipeline, uiSheetTwo );

			foreach ( var batch in batches )
			{
				if ( !KeptDrawn( batch ) ) continue;

				DrawUiBatch( pipeline, batch, batch.Texture != 0 ? batch.Texture : whiteTexture, push );
			}

			CoreRenderDevice.ZoneEnd();
		}
	}

	//A headset sheet's clipped batches, reused from last frame while nothing changed
	private static List<CoreUiRenderer.UiBatch> HeadsetBatches( Rinten.UI.RootPanel root, Rect bounds )
	{
		root.Frame ??= new();

		CoreRenderDevice.ZoneBegin( "sheet kept" );
		var reused = root.Frame.Cut == bounds && CoreUiRenderer.TryReuseFrame( root, UploadUiTexture );
		CoreRenderDevice.ZoneEnd();

		if ( reused ) return root.Frame.Batches;

		CoreRenderDevice.ZoneBegin( "sheet walk" );
		try
		{
			return WalkSheet( root, bounds );
		}
		finally
		{
			CoreRenderDevice.ZoneEnd();
		}
	}

	//A headset sheet walked into batches, cut to its edge and kept - see HeadsetBatches.
	private static List<CoreUiRenderer.UiBatch> WalkSheet( Rinten.UI.RootPanel root, Rect bounds )
	{
		//Unkept until the walk finishes: a walk that throws must not leave a half-built list under the old key.
		root.Frame.Kept = false;
		CoreUiRenderer.Recycle( root.Frame.Batches );
		CoreUiRenderer.Build( root, root.Frame.Batches, RasteriseCached, UploadUiTexture );

		foreach ( var batch in root.Frame.Batches )
			ClipToPanel( batch.Vertices, bounds );

		CoreUiRenderer.KeepFrame( root );
		root.Frame.Cut = bounds;

		KeepOnDevice( root );

		return root.Frame.Batches;
	}

	//Uploads a kept walk's batches into one device buffer, drawn from there while the walk is kept
	private static void KeepOnDevice( Rinten.UI.RootPanel root )
	{
		var frame = root.Frame;

		if ( frame.Vertices != 0 ) CoreRenderDevice.DestroyBuffer( frame.Vertices );
		frame.Vertices = 0;

		var batches = frame.Batches;
		var total = 0;

		foreach ( var batch in batches )
		{
			if ( KeptDrawn( batch ) ) total += batch.Vertices.Count;
		}

		if ( total == 0 ) return;

		//One array for every walk, grown as needed; the device copies it
		var need = total * UiVertexStride;
		if ( sheetBytes.Length < need ) sheetBytes = new byte[need];

		var bytes = sheetBytes.AsSpan( 0, need );
		var at = 0;

		foreach ( var batch in batches )
		{
			if ( !KeptDrawn( batch ) ) continue;

			System.Runtime.InteropServices.MemoryMarshal.AsBytes(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices ) ).CopyTo( bytes[( at * UiVertexStride )..] );

			at += batch.Vertices.Count;
		}

		var buffer = CoreRenderDevice.CreateBuffer( bytes );
		if ( buffer == 0 ) return;

		frame.Vertices = buffer;
		sheetsHolding.Add( root );

		at = 0;

		for ( var i = 0; i < batches.Count; i++ )
		{
			if ( !KeptDrawn( batches[i] ) ) continue;

			batches[i] = batches[i] with { Buffer = buffer, First = at };
			at += batches[i].Vertices.Count;
		}
	}

	//Whether a kept batch is vertices to draw - not the world's place, a backdrop, or nothing.
	private static bool KeptDrawn( in CoreUiRenderer.UiBatch batch ) => batch.Vertices.Count > 0
		&& batch.Texture != CoreUiRenderer.WorldTexture && batch.Texture != CoreUiRenderer.BackdropTexture;

	//Where a sheet's vertices are laid end to end before they go to the device - see KeepOnDevice.
	private static byte[] sheetBytes = [];

	//The sheets holding a buffer of their own - see KeepOnDevice.
	private static readonly HashSet<Rinten.UI.RootPanel> sheetsHolding = new();

	//Lets go of the buffers of sheets that are gone.
	private static void DropGoneSheets()
	{
		if ( sheetsHolding.Count == 0 ) return;

		sheetsHolding.RemoveWhere( static root =>
		{
			if ( root.IsValid ) return false;

			if ( root.Frame is { Vertices: not 0 } frame )
			{
				CoreRenderDevice.DestroyBuffer( frame.Vertices );
				frame.Vertices = 0;
			}

			return true;
		} );
	}

	//Clips boxes to the panel's edge: ancestor clips are narrowed to it, unclipped boxes get it
	private static void ClipToPanel( System.Collections.Generic.List<CoreUiRenderer.UiVertex> vertices, Rect panel )
	{
		var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( vertices );

		for ( var i = 0; i < span.Length; i++ )
		{
			ref var vertex = ref span[i];
			var clip = vertex.Clip;

			if ( clip.z > clip.x || clip.w > clip.y )
			{
				vertex.Clip = new Vector4(
					System.MathF.Max( clip.x, panel.Left ), System.MathF.Max( clip.y, panel.Top ),
					System.MathF.Min( clip.z, panel.Right ), System.MathF.Min( clip.w, panel.Bottom ) );
			}
			else
			{
				vertex.Clip = new Vector4( panel.Left, panel.Top, panel.Right, panel.Bottom );
			}
		}
	}

	//Draws scene text as billboard quads; one text pixel is 0.01 world units at scale one
	private void DrawWorldText( Scene scene, CameraComponent camera,
		System.Numerics.Matrix4x4 viewProjection )
	{
		if ( billboardPipeline == 0 || !scene.IsValid() ) return;

		var push = LinePush( stackalloc byte[80], viewProjection, world: true );
		Span<Vertex> corners = stackalloc Vertex[6];

		foreach ( var renderer in scene.GetAllComponents<TextRenderer>() )
		{
			if ( !renderer.IsValid() || !renderer.Enabled ) continue;
			if ( string.IsNullOrEmpty( renderer.TextScope.Text ) ) continue;

			var text = RasteriseCached( renderer.TextScope );
			if ( text.Texture == 0 ) continue;

			var transform = renderer.WorldTransform;

			//Which way the words face: their own rotation, the camera's, or the camera's with vertical kept
			var rotation = renderer.Billboard switch
			{
				TextRenderer.BillboardMode.Always => eyeView.Rotation,
				TextRenderer.BillboardMode.YOnly => Rotation.LookAt(
					( eyeView.Position - transform.Position ).WithY( 0 ).Normal * -1.0f, Vector3.Up ),
				_ => transform.Rotation,
			};

			var scale = renderer.Scale * TextRenderer.PixelsToWorld * transform.Scale.x;

			var width = text.Size.x * scale;
			var height = text.Size.y * scale;

			//The alignment moves the quad against the point rather than moving the point: the component's
			//position is where the text is anchored, not where its top-left corner happens to land.
			var offsetX = renderer.HorizontalAlignment switch
			{
				TextRenderer.HAlignment.Left => 0.0f,
				TextRenderer.HAlignment.Right => -width,
				_ => -width * 0.5f,
			};

			var offsetY = renderer.VerticalAlignment switch
			{
				TextRenderer.VAlignment.Top => 0.0f,
				TextRenderer.VAlignment.Bottom => height,
				_ => height * 0.5f,
			};

			//Right and up in the text's frame; up is the quad's, so v zero is the top of the words
			var right = rotation.Right;
			var up = rotation.Up;

			var origin = transform.Position + ( right * offsetX ) + ( up * offsetY );

			var tint = Color32.White;

			corners[0] = IconVertex( origin, 0, 0, tint );
			corners[1] = IconVertex( origin + ( right * width ), 1, 0, tint );
			corners[2] = IconVertex( origin - ( up * height ), 0, 1, tint );
			corners[3] = IconVertex( origin - ( up * height ), 0, 1, tint );
			corners[4] = IconVertex( origin + ( right * width ), 1, 0, tint );
			corners[5] = IconVertex( origin + ( right * width ) - ( up * height ), 1, 1, tint );

			CoreRenderDevice.DrawDynamic( Pick( billboardPipeline, billboardTwo ),
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( corners ),
				corners.Length, 76, push, text.Texture );
		}
	}

	private static Vertex IconVertex( Vector3 position, float u, float v, Color32 tint )
		=> new()
		{
			Position = position,
			Color = tint,
			TexCoord0 = new Vector4( u, v, 0, 0 ),
		};

	//Runs hand-drawn scene objects first and collects their draws, isolating exceptions from game code
	private static void RunOverrides( Scene scene, SceneWorld gizmoWorld, DebugDrawList gizmoDraws, CameraComponent camera,
		Rect viewport, Frustum frustum, CameraView view, System.Numerics.Matrix4x4 viewProjection, bool eye )
	{
		CoreImmediate.Begin();

		//The view drawn, not the camera's: in a headset each eye, with its own off-centre projection, so facing and
		//pinned things ask Graphics
		Graphics.BeginView( new Transform( view.Position, view.Rotation ),
			camera.RenderFieldOfView( viewport.Height > 0 ? viewport.Width / viewport.Height : 0.0f ), viewport, frustum,
			viewProjection );

		try
		{
			RunOverrides( scene?.SceneWorld, frustum, eye );

			//Editor furniture lives in the gizmo world; walked second so it lands on top, and not culled
			RunOverrides( gizmoWorld );

			//The models Gizmo.Draw drew - the view's gizmos' and the scene's own.
			AddGizmoModels( gizmoDraws );
			AddGizmoModels( scene?.GizmoDraws );
		}
		finally
		{
			Graphics.IgnoreDepth = false;

			Graphics.EndView();
			CoreImmediate.End();
		}
	}

	//The models a gizmo instance drew, in its own world nothing walks, handed over as model, place, colour,
	//material and pose
	private static void AddGizmoModels( DebugDrawList draws )
	{
		if ( draws is null ) return;

		foreach ( var model in draws.Models )
		{
			if ( !model.IsValid() || !model.RenderingEnabled || model.Model is null ) continue;

			var mesh = model.Model.BoneCount > 0 ? CoreResources.Mesh( model.Model ) : null;
			var pose = mesh is { IsSkinned: true } ? CoreSceneRenderer.PoseBones( mesh, model, model.Transform ) : null;
			if ( pose is not null && CoreSceneRenderer.AtBindPose( pose ) ) pose = null;

			CoreImmediate.Add( new CoreImmediate.ModelBatch( model.Model, model.Transform, model.ColorTint, model.MaterialOverride, Pose: pose ) );
		}
	}

	//Scratch copies of a world's objects, since drawing them may add to the world mid-walk
	private static readonly List<SceneObject> overrideBuffer = new();
	private static readonly List<SceneObject> dynamicBuffer = new();


	private static List<SceneObject> SceneObjectsOf( Scene scene, List<SceneObject> into )
		=> SceneObjectsOf( scene?.SceneWorld, into );

	private static List<SceneObject> SceneObjectsOf( SceneWorld world, List<SceneObject> into )
	{
		into.Clear();

		if ( world is null ) return into;

		lock ( world.InternalSceneObjects )
		{
			into.AddRange( world.InternalSceneObjects );
		}

		return into;
	}

	//The world's self-drawing objects, copied since their game and tool code may add to the world while walked
	//- see SceneWorld.InternalHandDrawn
	private static List<SceneObject> HandDrawnOf( SceneWorld world, List<SceneObject> into )
	{
		into.Clear();

		if ( world is null ) return into;

		lock ( world.InternalSceneObjects )
		{
			into.AddRange( world.InternalHandDrawn );
		}

		InDrawOrder( into );

		return into;
	}

	//Stable insertion sort by DrawOrder; List.Sort is unstable and would make overlapping batches flicker
	private static void InDrawOrder( List<SceneObject> objects )
	{
		for ( var i = 1; i < objects.Count; i++ )
		{
			var one = objects[i];
			var order = OrderOf( one );

			var at = i;
			while ( at > 0 && OrderOf( objects[at - 1] ) > order )
			{
				objects[at] = objects[at - 1];
				at--;
			}

			objects[at] = one;
		}

		static int OrderOf( SceneObject of ) => of is SceneCustomObject custom ? custom.DrawOrder : 0;
	}

	//Runs every self-drawing object in a world, frustum-culled only when it has given bounds
	private static void RunOverrides( SceneWorld world, Frustum? frustum = null, bool eye = false )
	{
		if ( world is null ) return;

		foreach ( var found in HandDrawnOf( world, overrideBuffer ) )
		{
			if ( found is not SceneCustomObject custom ) continue;
			if ( !custom.RenderingEnabled ) continue;

			//Put at a pixel of the screen: a headset's eye has no screen, and a picture stuck to the eye at the same
			//place in both reads as infinitely far off at the edge of the lens. See SceneCustomObject.ScreenAnchored.
			if ( eye && custom.ScreenAnchored ) continue;

			//Off screen is off the list, the same rule everything drawn from the queue gets. An overlay is exempt:
			//it is written over the finished picture and where it stands in the world is not where it lands.
			if ( frustum is { } view && custom.RenderLayer == SceneRenderLayer.Default &&
				custom.BoundsWereGiven && !Sees( view, custom.Bounds ) )
			{
				continue;
			}

			//The layer the object sits on decides whether what it draws is part of the world or written over it.
			Graphics.IgnoreDepth = custom.RenderLayer == SceneRenderLayer.OverlayWithoutDepth;

			//Which layer its draw is: called once per view, so it is told the one it is in
			Graphics.CurrentLayer = custom.Flags.IsTranslucent ? SceneLayerType.Translucent : SceneLayerType.Opaque;

			//Where it stands, for world panels, and cleared view attributes so one object's combo stays its own
			Graphics.ObjectTransform = custom.Transform;
			Graphics.Attributes.Clear();

			custom.RenderInternal();
		}

		Graphics.ObjectTransform = Transform.Zero;
		Graphics.CurrentLayer = SceneLayerType.Unknown;
		Graphics.Attributes.Clear();
	}

	//Generous visibility test using the bounding sphere; only for objects that gave bounds
	private static bool Sees( in Frustum view, in BBox bounds )
	{
		var size = bounds.Size;

		if ( !float.IsFinite( size.x ) || !float.IsFinite( size.y ) || !float.IsFinite( size.z ) )
			return true;

		//A sphere of centre plus half the diagonal, as the queue tests everything
		return view.IsInside( bounds.Center, size.Length * 0.5f, partially: true );
	}

	//Whether this view's instanced draws went into the world's pass, so the overlays do not draw them a second time.
	private bool instancedInWorld;

	//Replays the frame's instanced draws (sprites, tile maps, particles) once their picture is open
	private void DrawInstanced( CoreRender.TargetKind? world = null )
	{
		var draws = CoreImmediate.Instanced;

		for ( var i = 0; i < draws.Length; i++ )
		{
			var draw = draws[i];

			var pipeline = world is null ? draw.Pipeline : Graphics.InstancedPipeline( draw.Kind, world );
			if ( pipeline == 0 ) continue;

			CoreRenderDevice.DrawObjectsIndexed( pipeline, draw.IndexBuffer,
				draw.IndexCount, draw.InstanceCount, draw.WideIndices,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( draw.Push.AsSpan() ),
				0, draw.Buffers.AsSpan( 0, draw.BufferCount ) );
		}

		var runs = CoreImmediate.Blocks;

		for ( var i = 0; i < runs.Length; i++ )
		{
			var run = runs[i];

			var pipeline = world is null ? run.Pipeline : Graphics.InstancedPipeline( run.Kind, world );
			if ( pipeline == 0 ) continue;

			CoreRenderDevice.DrawBlockInstanced( pipeline, run.IndexBuffer, run.IndexCount,
				run.InstanceCount, run.WideIndices, run.Block, run.BlockBytes,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( run.Push.AsSpan() ), run.Texture );
		}
	}

	//Draws the frame's immediate vertices unlit with gizmo or billboard pipelines, after tonemapping
	private void DrawImmediate( System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( !overlay )
		{
			TakeGrabs();
			if ( !instancedInWorld ) DrawInstanced();
			instancedInWorld = false;
		}

		DrawBuffered( overlay );

		var batches = CoreImmediate.BatchSpan;
		if ( batches.Length == 0 ) return;

		//One block for the loop, written again for each batch - the draw copies it.
		Span<byte> push = stackalloc byte[96];

		//For the batches in screen pixels rather than the world.
		var (width, height) = size;
		var ortho = width > 0 && height > 0 ? ScreenProjection( width, height ) : default;

		//Consecutive batches with the same pipeline, transform, tint and projection merge into one draw
		batchPipeline = 0;
		batchCount = 0;
		batchTransform = default;
		batchTint = default;
		batchScreen = false;

		foreach ( var batch in batches )
		{
			if ( batch.IgnoreDepth != overlay ) continue;
			if ( batch.Count <= 0 || batch.Vertices.Length < batch.Count ) continue;

			//A screen batch only ever has the screen to be drawn on.
			if ( batch.Screen && ortho == default ) continue;

			var projection = batch.Screen ? ortho : viewProjection;

			//Lines are drawn by a shader with no texture in it, so a material on one is a tint and nothing
			//more. A picture the draw carried wins over the material's map: the text material has none.
			var texture = batch.Lines ? 0
				: batch.Picture is not null ? UploadUiTexture( batch.Picture )
				: string.IsNullOrWhiteSpace( batch.Material ) ? 0
				: CoreResources.Colour( batch.Material );

			var vertices = batch.Vertices;

			if ( texture != 0 )
			{
				var chosen = overlay ? Pick( screenPipeline, screenTwo ) : Pick( billboardPipeline, billboardTwo );
				if ( chosen == 0 ) continue;

				//The textured shader has no tint, so the batch's goes into this frame's copy of the colours
				if ( batch.Tint != Color.White )
				{
					//Into scratch: the batch's array is the pool's and the next view draws it again
					if ( tintedScratch.Length < batch.Count ) tintedScratch = new Vertex[batch.Count * 2];
					System.Array.Copy( vertices, tintedScratch, batch.Count );
					vertices = tintedScratch;

					for ( var i = 0; i < batch.Count; i++ )
					{
						vertices[i].Color = (Color32)( (Color)vertices[i].Color * batch.Tint );
					}
				}

				//In asked order, so a textured batch between two line batches lands between them
				FlushDynamic();

				CoreRenderDevice.DrawDynamic( chosen,
					System.Runtime.InteropServices.MemoryMarshal.AsBytes( vertices.AsSpan( 0, batch.Count ) ),
					batch.Count, 76,
					//The textured shader is the billboard's: matrix and mode, no tint - see LinePush
					LinePush( push, projection, !batch.Screen, batch.Transform ),
					texture );

				continue;
			}

			var pipeline = batch.Lines
				? ( overlay ? Pick( gizmoLinesOverlay, gizmoLinesOverlayTwo ) : Pick( gizmoLines, gizmoLinesTwo ) )
				: ( overlay ? Pick( gizmoTrianglesOverlay, gizmoTrianglesOverlayTwo ) : Pick( gizmoTriangles, gizmoTrianglesTwo ) );

			if ( pipeline == 0 ) continue;

			//Join on what the push block is made of, not the block: building one is a matrix multiply
			var joins = pipeline == batchPipeline && batch.Transform == batchTransform &&
				batch.Tint == batchTint && batch.Screen == batchScreen &&
				batchCount + batch.Count <= MostBatchedVertices;

			if ( !joins )
			{
				FlushDynamic();

				batchPipeline = pipeline;
				batchTransform = batch.Transform;
				batchTint = batch.Tint;
				batchScreen = batch.Screen;

				DynamicPush( push, batchTransform, batchTint, projection, !batchScreen ).CopyTo( batchPush );
			}

			if ( batchScratch.Length < batchCount + batch.Count )
				System.Array.Resize( ref batchScratch, Math.Max( batchCount + batch.Count, batchScratch.Length * 2 ) );

			vertices.AsSpan( 0, batch.Count ).CopyTo( batchScratch.AsSpan( batchCount ) );
			batchCount += batch.Count;
		}

		FlushDynamic();
	}

	//Takes the frame copies objects asked for (GrabFrameTexture): window picture, else world picture
	private void TakeGrabs()
	{
		var grabs = CoreImmediate.Grabs;
		if ( grabs.Count == 0 ) return;

		var (width, height) = size;
		var texture = CoreRenderDevice.CaptureBackdrop() ? CoreRenderDevice.BackdropTexture() : 0;

		if ( texture == 0 && worldTarget == CoreRender.TargetKind.Scene )
		{
			texture = CoreRenderDevice.TargetTexture( viewport, CoreRender.TargetKind.Scene );

			var world = CoreRenderDevice.WorldSize();

			if ( world.x >= 1.0f && world.y >= 1.0f )
			{
				width = (int)world.x;
				height = (int)world.y;
			}
		}

		for ( var i = 0; i < grabs.Count; i++ ) grabs[i].Into.Point( texture, width, height );
	}

	//Draws from a project's own buffers onto the finished picture - see CoreImmediate.BufferBatch
	private static void DrawBuffered( bool overlay )
	{
		var draws = CoreImmediate.Buffered;

		for ( var i = 0; i < draws.Count; i++ )
		{
			var batch = draws[i];

			if ( batch.IgnoreDepth != overlay ) continue;

			var kind = batch.Kind with
			{
				Target = CoreRender.TargetKind.Screen,
				Finishes = true,
				ScreenChain = null,
				NoDepth = batch.Kind.NoDepth || overlay,
			};

			var pipeline = CoreResources.Pipeline( kind );
			if ( pipeline == 0 ) continue;

			var draw = batch.Draw;
			draw.Pipeline = pipeline;

			CoreRenderDevice.DrawBuffers( draw, batch.Push,
				batch.Buffers is null ? default : batch.Buffers.AsSpan( 0, batch.BufferCount ) );
		}
	}

	private Vertex[] tintedScratch = System.Array.Empty<Vertex>();
	private readonly Vertex[] iconCorners = new Vertex[6];

	//Hand-drawn overlays merged once per frame and drawn per view; static as each eye has its own drawer
	private sealed class DynamicRecord
	{
		internal readonly record struct Batch( ulong Pipeline, Transform Transform, Color Tint, int At, int Count );

		internal Vertex[] Vertices = System.Array.Empty<Vertex>();
		internal int Used;
		internal readonly List<Batch> Batches = new();

		//Which engine frame this was gathered in, or -1 for none.
		internal long Frame = -1;

		//The world's hand-drawn count when gathered; a callback may add one between eyes, cheap to notice
		internal int Seen = -1;
	}

	//The two records, by whether their layer ignores depth.
	private static readonly DynamicRecord[] dynamicRecords = [new(), new()];

	//How many objects a world draws by hand, without copying the list.
	private static int HandDrawnCount( SceneWorld world )
	{
		lock ( world.InternalSceneObjects )
		{
			return world.InternalHandDrawn.Count;
		}
	}

	//Draws hand-drawn objects, merging consecutive ones with the same pipeline and push block
	private void DrawDynamicObjects( Scene scene, System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		var world = scene?.SceneWorld;
		if ( world is null ) return;

		var record = dynamicRecords[overlay ? 1 : 0];

		var kept = SharingViews && record.Frame == EngineLoop.LoopFrames &&
			record.Seen == HandDrawnCount( world );

		if ( !kept ) GatherDynamicObjects( world, overlay, record );

		IssueDynamicObjects( record, viewProjection );
	}

	//Every hand-drawn object of one layer, merged into runs sharing pipeline, transform and tint - see DynamicRecord
	private void GatherDynamicObjects( SceneWorld world, bool overlay, DynamicRecord record )
	{
		record.Batches.Clear();
		record.Used = 0;

		var pipeline = 0ul;
		var transform = default( Transform );
		var tint = default( Color );
		var at = 0;

		//Static, so it captures nothing: a local function that closes over these would allocate a
		//display class every frame, which is the kind of allocation this path has just been cleared of.
		static void Close( DynamicRecord into, ulong pipeline, Transform transform, Color tint, int at )
		{
			if ( into.Used > at && pipeline != 0 )
				into.Batches.Add( new DynamicRecord.Batch( pipeline, transform, tint, at, into.Used - at ) );
		}

		var seen = 0;

		foreach ( var found in HandDrawnOf( world, dynamicBuffer ) )
		{
			seen++;

			if ( found is not SceneDynamicObject dynamic ) continue;
			if ( !dynamic.RenderingEnabled || dynamic.Geometry.Count == 0 ) continue;

			//What a shader would have drawn out of these vertices is not what the vertices are - the grid is
			//four white quads until its shader turns them into lines. Left alone rather than drawn wrong.
			if ( dynamic.ShaderDriven ) continue;

			//Only the layer that says so ignores depth; everything else is part of the world and is hidden by it.
			var ignoresDepth = dynamic.RenderLayer == SceneRenderLayer.OverlayWithoutDepth;
			if ( ignoresDepth != overlay ) continue;

			var lines = dynamic.Primitive is Graphics.PrimitiveType.Lines
				or Graphics.PrimitiveType.LineStrip
				or Graphics.PrimitiveType.LinesWithAdjacency
				or Graphics.PrimitiveType.LineStripWithAdjacency;

			var chosen = lines
				? ( overlay ? Pick( gizmoLinesOverlay, gizmoLinesOverlayTwo ) : Pick( gizmoLines, gizmoLinesTwo ) )
				: ( overlay ? Pick( gizmoTrianglesOverlay, gizmoTrianglesOverlayTwo ) : Pick( gizmoTriangles, gizmoTrianglesTwo ) );

			if ( chosen == 0 ) continue;

			var vertices = (ReadOnlySpan<Vertex>)System.Runtime.InteropServices.CollectionsMarshal.AsSpan( dynamic.Geometry );

			//The line pipeline draws pairs, and a strip handed to it as it is came out as every other
			//segment. See Graphics.DrawInternal, which spells out the immediate strips the same way.
			if ( dynamic.Primitive is Graphics.PrimitiveType.LineStrip )
			{
				vertices = LinePairs( vertices );
				if ( vertices.IsEmpty ) continue;
			}

			//Join on what the push block would be made of: most objects share the identity transform and white tint,
			//and the block is built only at the draw
			var joins = chosen == pipeline && dynamic.Transform == transform &&
				dynamic.ColorTint == tint && record.Used - at + vertices.Length <= MostBatchedVertices;

			if ( !joins )
			{
				Close( record, pipeline, transform, tint, at );

				pipeline = chosen;
				transform = dynamic.Transform;
				tint = dynamic.ColorTint;
				at = record.Used;
			}

			if ( record.Vertices.Length < record.Used + vertices.Length )
			{
				System.Array.Resize( ref record.Vertices,
					Math.Max( record.Used + vertices.Length, Math.Max( record.Vertices.Length * 2, 256 ) ) );
			}

			vertices.CopyTo( record.Vertices.AsSpan( record.Used ) );
			record.Used += vertices.Length;
		}

		Close( record, pipeline, transform, tint, at );

		record.Seen = seen;
		record.Frame = SharingViews ? EngineLoop.LoopFrames : -1;
	}

	//One view's draws out of a gathered record - only the push is its own.
	private void IssueDynamicObjects( DynamicRecord record, System.Numerics.Matrix4x4 viewProjection )
	{
		if ( record.Batches.Count == 0 ) return;

		Span<byte> push = stackalloc byte[96];

		foreach ( var batch in System.Runtime.InteropServices.CollectionsMarshal.AsSpan( record.Batches ) )
		{
			CoreRenderDevice.ZoneBegin( "dynamic draw" );

			CoreRenderDevice.DrawDynamic( batch.Pipeline,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( record.Vertices.AsSpan( batch.At, batch.Count ) ),
				batch.Count, 76, DynamicPush( push, batch.Transform, batch.Tint, viewProjection ) );

			CoreRenderDevice.ZoneEnd();
		}
	}

	//Draws the gathered batch as one draw, in its own profiler zone
	private void FlushDynamic()
	{
		if ( batchCount == 0 || batchPipeline == 0 ) return;

		CoreRenderDevice.ZoneBegin( "dynamic draw" );

		CoreRenderDevice.DrawDynamic( batchPipeline,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( batchScratch.AsSpan( 0, batchCount ) ),
			batchCount, 76, batchPush );

		CoreRenderDevice.ZoneEnd();

		batchCount = 0;
	}

	//The most vertices a merged draw holds, so the device is never asked for one enormous copy
	private const int MostBatchedVertices = 64 * 1024;

	private ulong batchPipeline;
	private Transform batchTransform;
	private Color batchTint;

	//Whether the gathered run is in screen pixels; only DrawImmediate fills it
	private bool batchScreen;
	private int batchCount;
	private Vertex[] batchScratch = System.Array.Empty<Vertex>();
	private readonly byte[] batchPush = new byte[96];

	//A line strip as the pairs it joins, in a reused array valid until the next call
	private ReadOnlySpan<Vertex> LinePairs( ReadOnlySpan<Vertex> strip )
	{
		if ( strip.Length < 2 ) return default;

		var count = ( strip.Length - 1 ) * 2;
		if ( stripScratch.Length < count ) stripScratch = new Vertex[count * 2];

		for ( var i = 0; i < strip.Length - 1; i++ )
		{
			stripScratch[i * 2] = strip[i];
			stripScratch[i * 2 + 1] = strip[i + 1];
		}

		return stripScratch.AsSpan( 0, count );
	}

	private Vertex[] stripScratch = System.Array.Empty<Vertex>();

	//A headset eye drawn beside the other in one engine frame; BuildFrame shares by the same test
	private static bool SharingViews => CoreRender.SharingViews && Rinten.VR.VRSystem.IsRendering;

	//Which engine frame the sky's table was last drawn in, or -1 for none.
	private static long skyTableFrame = -1;

	//Where a batch of vertices stands, and what colour it is multiplied by - the block both these and the
	//gizmos push. Into eighty bytes of the caller's, which the draw copies as it records - see LinePush.
	private Span<byte> DynamicPush( Span<byte> into, Transform transform, Color tint,
		System.Numerics.Matrix4x4 viewProjection, bool world = true, Vector2 wide = default )
	{
		var placed = CoreSceneRenderer.WorldOf( transform );

		//In a both-eyes pass push only the object matrix; the shader applies each eye's view
		var eyes = world && ( BothEyes || ForceEyePush );
		var matrix = eyes ? placed : placed * viewProjection;

		System.Runtime.InteropServices.MemoryMarshal.Write( into, in matrix );

		//The tint stays at sixty-four and the mode goes after it, so a gizmo shader without this lane
		//reads what it always did - see g_mode in gizmo.slang
		System.Runtime.InteropServices.MemoryMarshal.Write( into[64..],
			new System.Numerics.Vector4( tint.r, tint.g, tint.b, tint.a ) );
		System.Runtime.InteropServices.MemoryMarshal.Write( into[80..],
			new System.Numerics.Vector4( eyes ? 1.0f : 0.0f, wide.x > 0.0f ? 1.0f : 0.0f, wide.x, wide.y ) );

		return into[..96];
	}

	/// <summary>Why gizmos are not on screen, in numbers, as every failure on this path is quiet.</summary>
	[ConCmd( "editor_gizmos_status" )]
	public static void GizmoStatus()
	{
		//The hovering chain is measured where the instance actually is - see editor_input_status.
		//It cannot be read from here: nothing is pushed while the drawing is done.

		Log.Info( $"pipelines: lines {gizmoLines}, linesOverlay {gizmoLinesOverlay}, " +
			$"triangles {gizmoTriangles}, trianglesOverlay {gizmoTrianglesOverlay}, screen {screenPipeline}" );

		Log.Info( $"last frame: {lastGizmoBatches} shapes, {lastGizmoVertices} vertices, {lastGizmoDrawn} drawn" );

		//Where the geometry actually landed. Everything above can look right while the vertices sit at the
		//origin, or a million units away, or at no number at all - and all three look the same on screen: empty.
		if ( lastGizmoBatches > 0 )
		{
			Log.Info( GizmoBounds
				? $"gizmo bounds: {lastGizmoMin} .. {lastGizmoMax}, camera {lastCameraPosition}"
				: $"gizmo bounds: not measured (editor_gizmo_bounds 1), camera {lastCameraPosition}" );
		}

		Log.Info( $"camera size {lastCameraSize}, fov {lastCameraFov}" );
	}

	private static int lastGizmoBatches;
	private static int lastGizmoVertices;
	private static int lastGizmoDrawn;

	/// <summary>Whether gizmo bounds are worked out for editor_gizmo_status - see MeasureGizmos.</summary>
	[ConVar( "editor_gizmo_bounds", ConVarFlags.Protected,
		Help = "Work out the world bounds of the frame's gizmo geometry, for editor_gizmo_status; a transform and two compares for every gizmo vertex in the frame" )]
	private static bool GizmoBounds { get; set; }

	private static Vector3 lastGizmoMin;
	private static Vector3 lastGizmoMax;
	private static Vector3 lastCameraPosition;
	private static Vector2 lastCameraSize;
	private static float lastCameraFov;

	//What the gizmo frame held, for editor_gizmos_status .
	private static void MeasureGizmos( DebugDrawList list )
	{
		lastGizmoBatches = list?.Shapes.Count ?? 0;
		lastGizmoVertices = 0;
		lastGizmoDrawn = 0;

		if ( list is null ) return;

		foreach ( var shape in list.Shapes ) lastGizmoVertices += shape.Count;

		//A transform and two compares for every vertex of every gizmo in the frame - the grid alone is thousands
		//- and nothing reads it but the console command that prints it. Off unless somebody has asked for it.
		if ( !GizmoBounds ) return;

		lastGizmoMin = float.MaxValue;
		lastGizmoMax = float.MinValue;

		foreach ( var shape in list.Shapes )
		{
			foreach ( var vertex in shape.Span )
			{
				var at = shape.Transform.PointToWorld( vertex.Position );

				lastGizmoMin = Vector3.Min( lastGizmoMin, at );
				lastGizmoMax = Vector3.Max( lastGizmoMax, at );
			}
		}
	}

	//One owner's world debug drawing (a gizmo instance's or scene overlay), depth-tested or over all: lines,
	//triangles, spheres, sprites, world labels. Screen-flat is DrawDebugScreen
	private void DrawDebug( DebugDrawList list, CameraComponent camera,
		System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( list is null || list.IsEmpty ) return;

		DrawShapes( list.Shapes, viewProjection, overlay );
		DrawRings( list.Rings, viewProjection, overlay );
		DrawSprites( list.Sprites, camera, viewProjection, overlay );
		DrawWorldLabels( list.Labels, viewProjection, overlay );
	}

	//Draws a pass's shapes in request order, one draw per run of same-styled shapes
	private void DrawShapes( List<DebugDrawList.Shape> shapes, System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( shapes.Count == 0 ) return;

		Span<byte> push = stackalloc byte[96];

		var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( shapes );
		var at = 0;

		while ( at < items.Length )
		{
			ref readonly var first = ref items[at];

			if ( first.IgnoreDepth != overlay || first.Count == 0 )
			{
				at++;
				continue;
			}

			//How far the run goes, and how many vertices it holds.
			var end = at + 1;
			var total = first.Count;
			var runs = 1;

			for ( var next = at + 1; next < items.Length; next++ )
			{
				if ( items[next].IgnoreDepth != overlay ) continue;
				if ( !items[next].DrawnLike( first ) ) break;

				total += items[next].Count;
				runs++;
				end = next + 1;
			}

			ReadOnlySpan<Vertex> vertices;

			if ( runs == 1 )
			{
				vertices = first.Span;
			}
			else
			{
				if ( shapeScratch.Length < total )
					shapeScratch = new Vertex[Math.Max( total, shapeScratch.Length * 2 )];

				var written = 0;

				for ( var k = at; k < end; k++ )
				{
					if ( items[k].IgnoreDepth != overlay ) continue;

					items[k].Span.CopyTo( shapeScratch.AsSpan( written ) );
					written += items[k].Count;
				}

				vertices = shapeScratch.AsSpan( 0, written );
			}

			DrawShape( first, vertices, viewProjection, overlay, push );

			at = end;
		}
	}

	//Where runs of shapes are laid end to end - grown and kept.
	private Vertex[] shapeScratch = System.Array.Empty<Vertex>();

	private void DrawShape( in DebugDrawList.Shape shape, ReadOnlySpan<Vertex> vertices,
		System.Numerics.Matrix4x4 viewProjection, bool overlay, Span<byte> push )
	{
		if ( shape.Lines && shape.Thickness > 1.0f )
		{
			DrawWideLines( vertices, shape.Transform, shape.Thickness, viewProjection, overlay, push );
			return;
		}

		var chosen = shape.Lines
			? ( overlay ? Pick( gizmoLinesOverlay, gizmoLinesOverlayTwo ) : Pick( gizmoLines, gizmoLinesTwo ) )
			: 0ul;

		if ( !shape.Lines )
		{
			if ( shape.CullBackfaces ) chosen = CulledTriangles( overlay );

			if ( chosen == 0 )
				chosen = overlay ? Pick( gizmoTrianglesOverlay, gizmoTrianglesOverlayTwo ) : Pick( gizmoTriangles, gizmoTrianglesTwo );
		}

		if ( chosen == 0 ) return;

		lastGizmoDrawn++;

		CoreRenderDevice.DrawDynamic( chosen,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( vertices ),
			vertices.Length, 76, DynamicPush( push, shape.Transform, Color.White, viewProjection ) );
	}

	//Lines thicker than a pixel as two-triangle strips expanded in the vertex stage, constant pixel width
	private void DrawWideLines( ReadOnlySpan<Vertex> lines, in Transform transform, float thickness,
		System.Numerics.Matrix4x4 viewProjection, bool overlay, Span<byte> push )
	{
		var (width, height) = size;
		if ( width <= 0 || height <= 0 ) return;

		var chosen = overlay
			? Pick( gizmoTrianglesOverlay, gizmoTrianglesOverlayTwo )
			: Pick( gizmoTriangles, gizmoTrianglesTwo );

		if ( chosen == 0 ) return;

		var segments = lines.Length / 2;
		if ( segments == 0 ) return;

		if ( wideScratch is null || wideScratch.Length < segments * 6 )
			wideScratch = new Vertex[Math.Max( segments * 6, ( wideScratch?.Length ?? 0 ) * 2 )];

		var into = wideScratch.AsSpan( 0, segments * 6 );

		for ( int i = 0; i < segments; i++ )
		{
			var a = lines[i * 2];
			var b = lines[i * 2 + 1];

			//X: the line's side; y: +1 at the start, -1 at the end, so the shader turns one way; z: width
			var startLeft = Corner( a, b.Position, 1, 1, thickness );
			var startRight = Corner( a, b.Position, -1, 1, thickness );
			var endLeft = Corner( b, a.Position, 1, -1, thickness );
			var endRight = Corner( b, a.Position, -1, -1, thickness );

			var at = i * 6;
			into[at + 0] = startLeft;
			into[at + 1] = startRight;
			into[at + 2] = endLeft;
			into[at + 3] = endLeft;
			into[at + 4] = startRight;
			into[at + 5] = endRight;
		}

		lastGizmoDrawn++;

		CoreRenderDevice.DrawDynamic( chosen,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( into ),
			into.Length, 76, DynamicPush( push, transform, Color.White, viewProjection,
				wide: new Vector2( width, height ) ) );

		static Vertex Corner( in Vertex self, Vector3 other, float side, float end, float thickness )
		{
			var corner = self;
			corner.TexCoord0 = new Vector4( other, 0.0f );
			corner.TexCoord1 = new Vector4( side, end, thickness, 0.0f );
			return corner;
		}
	}

	//Where DrawWideLines builds its strips - grown and kept.
	private static Vertex[] wideScratch;

	//Overlay spheres, outlined by a ring square to the eye, so built per view as world lines
	private void DrawRings( List<DebugDrawList.Ring> rings, System.Numerics.Matrix4x4 viewProjection, bool overlay )
	{
		if ( rings.Count == 0 ) return;

		var sink = new ScratchSink( ringScratch );

		foreach ( var ring in rings )
		{
			if ( ring.IgnoreDepth != overlay ) continue;

			//In the ring's own space: its centre where the transform puts it, its radius grown with the scale.
			sink.Color = ring.Color;
			DebugShapes.SphereRings( ref sink, ring.Transform.PointToWorld( ring.Center ),
				ring.Radius * ring.Transform.UniformScale, ring.Transform.Rotation, eyeView.Position );
		}

		if ( ringScratch.Count == 0 ) return;

		var chosen = overlay ? Pick( gizmoLinesOverlay, gizmoLinesOverlayTwo ) : Pick( gizmoLines, gizmoLinesTwo );

		if ( chosen != 0 )
		{
			CoreRenderDevice.DrawDynamic( chosen,
				System.Runtime.InteropServices.MemoryMarshal.AsBytes( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( ringScratch ) ),
				ringScratch.Count, 76, DynamicPush( stackalloc byte[96], Transform.Zero, Color.White, viewProjection ) );
		}

		ringScratch.Clear();
	}

	//Where a view's spheres are built - kept between views.
	private readonly List<Vertex> ringScratch = new();

	//Builds shapes into a list of vertices, in one colour at a time.
	private struct ScratchSink : IDebugShapeSink
	{
		private readonly List<Vertex> into;
		public Color32 Color;

		public ScratchSink( List<Vertex> into )
		{
			this.into = into;
			Color = Color32.White;
		}

		public void Line( in Vector3 a, in Vector3 b )
		{
			into.Add( new Vertex( a, Color ) );
			into.Add( new Vertex( b, Color ) );
		}

		public void Triangle( in Vector3 a, in Vector3 b, in Vector3 c )
		{
			into.Add( new Vertex( a, Color ) );
			into.Add( new Vertex( b, Color ) );
			into.Add( new Vertex( c, Color ) );
		}
	}

	//Back-face-culled gizmo triangles, made on first use; 0 in a stereo pass with no two-view build
	private ulong CulledTriangles( bool overlay )
	{
		if ( !culledMade )
		{
			culledMade = true;

			Compile( EngineShaders.Gizmo, ( vs, ps ) =>
			{
				gizmoTrianglesCulled = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Triangles, blend: true, cullBack: true );
				gizmoTrianglesCulledOverlay = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Triangles, blend: true, cullBack: true );
			} );

			CompileStereo( EngineShaders.Gizmo, null, ( vs, ps ) =>
			{
				gizmoTrianglesCulledTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: true, CoreRender.Topology.Triangles, blend: true, cullBack: true );
				gizmoTrianglesCulledOverlayTwo = CoreRenderDevice.CreatePipeline( vs, ps,
					CoreRender.VertexLayoutKind.Engine, depthTest: false, CoreRender.Topology.Triangles, blend: true, cullBack: true );
			} );
		}

		var one = overlay ? gizmoTrianglesCulledOverlay : gizmoTrianglesCulled;
		var two = overlay ? gizmoTrianglesCulledOverlayTwo : gizmoTrianglesCulledTwo;

		if ( BothEyes && two == 0 ) return 0;

		return Pick( one, two );
	}

	private static bool culledMade;
	private static ulong gizmoTrianglesCulled;
	private static ulong gizmoTrianglesCulledOverlay;
	private static ulong gizmoTrianglesCulledTwo;
	private static ulong gizmoTrianglesCulledOverlayTwo;

	//Corner ray length over axis depth for a horizontal field; at most 4, for a lens near 180 degrees
	internal static float CornerStretch( float fovDegrees, float aspect )
	{
		var across = System.MathF.Tan( System.Math.Clamp( fovDegrees, 1.0f, 179.0f ) * ( System.MathF.PI / 360.0f ) );
		var up = across / System.MathF.Max( 0.001f, aspect );

		return System.MathF.Min( System.MathF.Sqrt( 1.0f + across * across + up * up ), 4.0f );
	}

	//Camera projection, Y flipped for Vulkan.
	internal static System.Numerics.Matrix4x4 ProjectionMatrix( bool orthographic, float fovDegrees,
		float orthoHeight, float aspect, float near, float far )
	{
		System.Numerics.Matrix4x4 projection;

		if ( orthographic )
		{
			var height = System.MathF.Max( 0.001f, orthoHeight );
			var width = height * System.MathF.Max( 0.001f, aspect );
			projection = System.Numerics.Matrix4x4.CreateOrthographic( width, height, near, far );
		}
		else
		{
			var horizontal = System.Math.Clamp( fovDegrees, 1.0f, 179.0f ) * ( System.MathF.PI / 180.0f );
			var vertical = 2.0f * System.MathF.Atan( System.MathF.Tan( horizontal * 0.5f ) / System.MathF.Max( 0.001f, aspect ) );
			projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView( vertical, aspect, near, far );
		}

		projection.M22 = -projection.M22;
		return projection;
	}

	//-eyepush: overlays always read the view from the frame block, to test that path on a desktop
	private static bool ForceEyePush => forceEyePush ??= Rinten.Utility.CommandLine.HasSwitch( "-eyepush" );

	private static bool? forceEyePush;

	//Line push block (one matrix) written into the caller's stackalloc span to avoid per-pass garbage
	private Span<byte> LinePush( Span<byte> into, System.Numerics.Matrix4x4 viewProjection, bool world = false,
		Transform? standing = null )
	{
		//In a both-eyes pass leave the view out; the vertex stage reads each eye's from the frame block
		var eyes = world && ( BothEyes || ForceEyePush );

		var matrix = standing is { } where
			? ( eyes ? CoreSceneRenderer.WorldOf( where ) : CoreSceneRenderer.WorldOf( where ) * viewProjection )
			: ( eyes ? System.Numerics.Matrix4x4.Identity : viewProjection );

		System.Runtime.InteropServices.MemoryMarshal.Write( into, in matrix );
		System.Runtime.InteropServices.MemoryMarshal.Write( into[64..],
			new System.Numerics.Vector4( eyes ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f ) );

		return into[..80];
	}

	//Ground grid at the editor spacing, centred under the camera, cell doubled as the camera pulls away
	private void DrawGrid( CameraComponent camera, System.Numerics.Matrix4x4 viewProjection )
	{
		if ( linePipeline == 0 ) return;

		//Grid spacing from this viewport's gizmo settings, not Gizmo.Settings (may be unset during a draw)
		var settings = GizmoSource?.Invoke()?.Settings ?? Gizmo.Settings;

		var spacing = settings?.GridSpacing ?? 1.0f;
		if ( spacing <= 0.0001f ) spacing = 1.0f;

		var eye = camera.IsValid() ? camera.WorldPosition : Vector3.Zero;

		//How much world is on screen. For a flat camera that is the height it is set to; otherwise it is
		//how far the eye is from the plane the grid lies on, which is what decides how big a cell comes out.
		var onScreen = camera.IsValid() && camera.Orthographic
			? System.MathF.Max( camera.OrthographicHeight, 0.01f )
			: System.MathF.Max( System.MathF.Abs( eye.y ), 0.01f );

		//Doubled until a cell is a hundredth of the screen; finer lines crowd into a grey wash
		var cell = spacing;
		while ( cell < onScreen * 0.01f ) cell *= 2.0f;

		//Cells only in orthographic views; perspective gets just the two axes, the grid goes on surfaces
		var cells = camera.IsValid() && camera.Orthographic;

		//Snapped to the major lines, so the grid does not crawl under a camera that is moving: the
		//lines stay on their own multiples of the cell and only the patch they are cut from moves.
		var block = cell * 10.0f;
		var centreX = System.MathF.Round( eye.x / block ) * block;
		var centreZ = System.MathF.Round( eye.z / block ) * block;

		if ( grid is null || gridCells != cells || !gridSpacing.AlmostEqual( cell ) ||
			!gridCentreX.AlmostEqual( centreX ) || !gridCentreZ.AlmostEqual( centreZ ) )
		{
			grid = BuildGrid( cell, centreX, centreZ, cells );
			gridSpacing = cell;
			gridCentreX = centreX;
			gridCentreZ = centreZ;
			gridCells = cells;
		}

		CoreRenderDevice.DrawDynamic( linePipeline,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes( grid.AsSpan() ),
			grid.Length, 16, LinePush( stackalloc byte[80], viewProjection, world: true ) );
	}


	//Grid cut into editable mesh faces: their queued triangles redrawn with a per-pixel grid shader
	private void DrawSurfaceGrid( System.Numerics.Matrix4x4 viewProjection,
		System.ReadOnlySpan<System.Numerics.Vector4> planes )
	{
		if ( surfaceGridPipeline == 0 ) return;
		if ( !( SurfaceGridSource?.Invoke() ?? true ) ) return;

		//The viewport's own settings, for the same reason the ground grid asks for them there:
		//Gizmo.Settings is whichever instance is current, and during a draw there need not be one.
		var settings = GizmoSource?.Invoke()?.Settings ?? Gizmo.Settings;

		var cell = settings?.GridSpacing ?? 1.0f;
		if ( cell <= 0.0001f ) cell = 1.0f;

		Span<byte> push = stackalloc byte[128];

		var queue = CollectionsMarshal.AsSpan( opaque );

		for ( var i = 0; i < queue.Length; i++ )
		{
			ref readonly var queued = ref queue[i];

			if ( !queued.EditableMesh || queued.ShadowOnly ) continue;

			//Its own vertices in this pipeline's layout: posed, morphed or buffer-read meshes are skipped
			if ( queued.Skinned || queued.Morphs is not null ) continue;

			var mesh = queued.Mesh;
			if ( mesh is null || mesh.Pulled || mesh.Vertices == 0 || mesh.Indices == 0 ) continue;
			if ( queued.Part.IndexCount <= 0 ) continue;

			if ( !planes.IsEmpty && queued.Radius > 0.0f &&
				!CoreSceneRenderer.SphereVisible( planes, queued.Center, Reach( in queued ) ) ) continue;

			CoreRenderDevice.DrawIndexed( surfaceGridPipeline, mesh.Vertices, mesh.Indices,
				queued.Part.IndexCount,
				SurfaceGridPush( push, viewProjection, in queued.World, cell ),
				queued.Part.FirstIndex, queued.Part.FirstVertex );
		}
	}

	//Surface grid push block (128 bytes); world matrix as three columns to save 16 bytes
	private static Span<byte> SurfaceGridPush( Span<byte> into,
		System.Numerics.Matrix4x4 viewProjection, in System.Numerics.Matrix4x4 world, float cell )
	{
		MemoryMarshal.Write( into, in viewProjection );

		var x = new System.Numerics.Vector4( world.M11, world.M21, world.M31, world.M41 );
		var y = new System.Numerics.Vector4( world.M12, world.M22, world.M32, world.M42 );
		var z = new System.Numerics.Vector4( world.M13, world.M23, world.M33, world.M43 );

		MemoryMarshal.Write( into[64..], in x );
		MemoryMarshal.Write( into[80..], in y );
		MemoryMarshal.Write( into[96..], in z );

		MemoryMarshal.Write( into[112..], in cell );
		MemoryMarshal.Write( into[116..], in SurfaceGridMajorEvery );
		MemoryMarshal.Write( into[120..], in SurfaceGridOpacity );
		MemoryMarshal.Write( into[124..], in SurfaceGridBias );

		return into[..128];
	}

	//How many cells to a stronger line - ten, as the ground grid had.
	private static readonly float SurfaceGridMajorEvery = 10.0f;

	//How strongly the grid is laid over the surface.
	private static readonly float SurfaceGridOpacity = 1.0f;

	//Clip-depth nudge so the grid wins against its surface without showing through walls
	private static readonly float SurfaceGridBias = 1e-6f;


	//Ground grid lines around a centre: fixed count, every tenth stronger, edge faded, axes in colour
	private static LineVertex[] BuildGrid( float cell, float centreX, float centreZ, bool cells )
	{
		const int extent = 120;

		var lines = new System.Collections.Generic.List<LineVertex>( cells ? ( extent * 2 + 1 ) * 4 + 4 : 4 );

		var far = extent * cell;

		//Which multiple of the cell the centre sits on, so that every tenth line being a major counts from the
		//origin of the world rather than from the patch - otherwise the strong lines move about as the camera does.
		var stepX = (int)System.MathF.Round( centreX / cell );
		var stepZ = (int)System.MathF.Round( centreZ / cell );

		void Row( int i, bool alongX )
		{
			var step = ( alongX ? stepX : stepZ ) + i;

			//The axes of the world are drawn separately, on top.
			if ( step == 0 ) return;

			var strength = (byte)( step % 10 == 0 ? 96 : 40 );

			//Fade towards the edge so the grid ends rather than stopping.
			var edge = 1.0f - System.MathF.Abs( i ) / (float)( extent + 1 );
			var alpha = (byte)( strength * System.MathF.Min( 1.0f, edge * 3.0f ) );

			var colour = new Color32( 140, 145, 155, alpha );

			if ( alongX )
			{
				var at = centreX + ( i * cell );
				lines.Add( new LineVertex( new Vector3( at, 0, centreZ - far ), colour ) );
				lines.Add( new LineVertex( new Vector3( at, 0, centreZ + far ), colour ) );
			}
			else
			{
				var at = centreZ + ( i * cell );
				lines.Add( new LineVertex( new Vector3( centreX - far, 0, at ), colour ) );
				lines.Add( new LineVertex( new Vector3( centreX + far, 0, at ), colour ) );
			}
		}

		if ( cells )
		{
			for ( var i = -extent; i <= extent; i++ )
			{
				Row( i, alongX: true );
				Row( i, alongX: false );
			}
		}

		//The axes, only where the patch covers them; X red and Z blue, opaque
		if ( System.MathF.Abs( centreZ ) <= far )
		{
			lines.Add( new LineVertex( new Vector3( centreX - far, 0, 0 ), AxisX ) );
			lines.Add( new LineVertex( new Vector3( centreX + far, 0, 0 ), AxisX ) );
		}

		if ( System.MathF.Abs( centreX ) <= far )
		{
			lines.Add( new LineVertex( new Vector3( 0, 0, centreZ - far ), AxisZ ) );
			lines.Add( new LineVertex( new Vector3( 0, 0, centreZ + far ), AxisZ ) );
		}

		return lines.ToArray();
	}

	private void DrawSelection( System.Numerics.Matrix4x4 viewProjection )
	{
		if ( overlayPipeline == 0 ) return;

		var selected = SelectionSource?.Invoke();
		if ( selected is null ) return;

		var lines = new System.Collections.Generic.List<LineVertex>();
		var colour = new Color32( 255, 170, 60, 220 );

		foreach ( var gameObject in selected )
		{
			if ( !gameObject.IsValid() ) continue;

			var bounds = gameObject.GetBounds();
			if ( bounds.Size.Length <= 0.0001f ) continue;

			AddBox( lines, bounds, colour );
		}

		if ( lines.Count == 0 ) return;

		CoreRenderDevice.DrawDynamic( overlayPipeline,
			System.Runtime.InteropServices.MemoryMarshal.AsBytes(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan( lines ) ),
			lines.Count, 16, LinePush( stackalloc byte[80], viewProjection, world: true ) );
	}

	//The twelve edges of a box.
	private static void AddBox( System.Collections.Generic.List<LineVertex> lines, BBox box, Color32 colour )
	{
		var a = box.Mins;
		var b = box.Maxs;

		Span<Vector3> corners = stackalloc Vector3[8]
		{
			new( a.x, a.y, a.z ), new( b.x, a.y, a.z ), new( b.x, b.y, a.z ), new( a.x, b.y, a.z ),
			new( a.x, a.y, b.z ), new( b.x, a.y, b.z ), new( b.x, b.y, b.z ), new( a.x, b.y, b.z ),
		};

		Span<(int, int)> edges = stackalloc (int, int)[12]
		{
			( 0, 1 ), ( 1, 2 ), ( 2, 3 ), ( 3, 0 ),
			( 4, 5 ), ( 5, 6 ), ( 6, 7 ), ( 7, 4 ),
			( 0, 4 ), ( 1, 5 ), ( 2, 6 ), ( 3, 7 ),
		};

		foreach ( var (from, to) in edges )
		{
			lines.Add( new LineVertex( corners[from], colour ) );
			lines.Add( new LineVertex( corners[to], colour ) );
		}
	}
}
