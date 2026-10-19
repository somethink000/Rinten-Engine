namespace Rinten;

using Rinten.Hashing;
using Rinten.Rendering;
using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class SceneSpriteSystem : GameObjectSystem<SceneSpriteSystem>
{
	private readonly record struct SystemOffset( IBatchedParticleSpriteRenderer System, int Offset, int ParticleCount );

	private readonly record struct ParticleResult( Guid Id, ulong Group, IBatchedParticleSpriteRenderer System, int Offset, int Count, int SplotCount, BBox Bounds );

	//Carries the data needed to configure a new SpriteBatchSceneObject from the original component state.
	private readonly record struct RenderGroupConfig( InstanceGroupFlags Flags, RenderOptions RenderOptions, IReadOnlySet<uint> Tags, Material Material, int SortOrder );

	//Unique per-material ids for group keys (hashes could collide); weak and thread-safe
	private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Material, object> materialNumbers = new();

	private int materialNumbersGiven;

	private int NumberOf( Material material )
		=> material is null ? 0
			: (int)materialNumbers.GetValue( material, _ => Interlocked.Increment( ref materialNumbersGiven ) );

	Dictionary<ulong, SpriteBatchSceneObject> RenderGroups = [];

	public SceneSpriteSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 1, UpdateSprites, "UpdateSprites" ); //We want to upload after particles update
	}

	public override void Dispose()
	{
		if ( _sharedSprites != null )
		{
			ArrayPool<SpriteBatchSceneObject.SpriteData>.Shared.Return( _sharedSprites );
			_sharedSprites = null;
		}
		base.Dispose();
	}

	private Guid[] _activeParticleEmitters = [];
	private ParticleResult[] _particleProcessingResults = [];
	private HashSet<Guid> _registeredSpriteRenderers = new();
	private SpriteBatchSceneObject.SpriteData[] _sharedSprites;
	private readonly List<SystemOffset> _systemOffsets = [];
	private readonly List<SpriteRenderer> _allSprites = new();
	private readonly HashSet<Guid> _activeParticleIds = new();
	private readonly HashSet<Guid> _currentEnabledSprites = new();
	private readonly List<Guid> _spritesToRemove = new();
	private readonly List<Guid> _keysToRemoveScratch = new();

	internal unsafe void UpdateParticleSprites()
	{
		var spriteRenderers = Scene.GetAllComponents<IBatchedParticleSpriteRenderer>();

		//Calculate total size needed and ensure shared block is large enough
		int totalParticles = 0;
		foreach ( var renderer in spriteRenderers )
		{
			var particleRenderer = (ParticleRenderer)renderer;
			totalParticles += particleRenderer.ParticleEffect.Particles.Count;
		}

		//Clean up if there are no particles
		if ( totalParticles == 0 )
		{
			foreach ( var rg in RenderGroups )
			{
				_keysToRemoveScratch.Clear();
				_keysToRemoveScratch.AddRange( rg.Value.SpriteGroups.Keys );
				foreach ( var key in _keysToRemoveScratch )
				{
					rg.Value.UnregisterSpriteGroup( key );
				}
			}
			return;
		}

		//Here we allocate one big chunk of memory for all particle systems, each writting at a separate offset
		if ( _sharedSprites == null || _sharedSprites.Length < totalParticles )
		{
			if ( _sharedSprites != null ) ArrayPool<SpriteBatchSceneObject.SpriteData>.Shared.Return( _sharedSprites );
			_sharedSprites = ArrayPool<SpriteBatchSceneObject.SpriteData>.Shared.Rent( Math.Max( totalParticles, 4096 ) );
		}

		//Calculate write offsets for each particle system
		_systemOffsets.Clear();
		int currentOffset = 0;

		foreach ( var particleSystem in spriteRenderers )
		{
			particleSystem.RenderTexture?.MarkUsed( ushort.MaxValue );

			var particleRenderer = (ParticleRenderer)particleSystem;
			int particleCount = particleRenderer.ParticleEffect.Particles.Count;
			_systemOffsets.Add( new( particleSystem, currentOffset, particleCount ) );
			currentOffset += particleCount;
			if ( particleSystem is ParticleSpriteRenderer psr )
			{
				psr.AdvanceFrame();
			}
		}

		int systemCount = _systemOffsets.Count;

		if ( _particleProcessingResults.Length < systemCount )
		{
			_particleProcessingResults = new ParticleResult[systemCount];
		}
		else
		{
			Array.Clear( _particleProcessingResults );
		}

		if ( _activeParticleEmitters.Length < systemCount )
		{
			_activeParticleEmitters = new Guid[systemCount];
		}
		else
		{
			Array.Clear( _activeParticleEmitters );
		}

		//Write particles for the GPU, going parallel only when the total particle count is worth a fork
		var shared = 0;

		for ( var i = 0; i < _systemOffsets.Count; i++ ) shared += _systemOffsets[i].ParticleCount;

		void ProcessSystem( int i )
		{
			var systemInfo = _systemOffsets[i];
			if ( systemInfo.ParticleCount == 0 ) return;

			var particleRenderer = (ParticleRenderer)systemInfo.System;
			var particleSystemID = particleRenderer.Id;
			_activeParticleEmitters[i] = particleSystemID;

			var rendergroup = GetRenderGroupKey( systemInfo.System, (GameTags)particleRenderer.Tags, particleRenderer.RenderOptions );

			//This is a very hot codepath, beware! Create span from the managed array starting at the correct offset with the correct length
			var destSpan = _sharedSprites.AsSpan( systemInfo.Offset, systemInfo.ParticleCount );

			//Call the common ProcessParticlesDirectly interface method
			var result = systemInfo.System.ProcessParticlesDirectly( destSpan );

			if ( result.SpriteCount == 0 ) return;

			_particleProcessingResults[i] = new ParticleResult( particleSystemID, rendergroup, systemInfo.System, systemInfo.Offset, result.SpriteCount, result.SplotCount, result.Bounds );
		}

		if ( shared >= ParallelParticles && _systemOffsets.Count > 1 && !Rinten.Utility.Parallel.InOrder )
		{
			Rinten.Jobs.JobSystem.For( _systemOffsets.Count, processSystem ??= ProcessSystem );
		}
		else
		{
			for ( var i = 0; i < _systemOffsets.Count; i++ ) ProcessSystem( i );
		}

		//Cleanup inactive particle emitters
		_activeParticleIds.Clear();
		for ( int i = 0; i < systemCount; i++ )
		{
			var id = _activeParticleEmitters[i];
			if ( id != Guid.Empty ) _activeParticleIds.Add( id );
		}
		foreach ( var rg in RenderGroups )
		{
			RemoveInactiveSpriteGroups( rg.Value );
		}

		//Register buffers to corresponding render groups
		for ( int i = 0; i < systemCount; i++ )
		{
			var (id, rendergroup, system, offset, count, splotCount, bounds) = _particleProcessingResults[i];
			if ( count == 0 )
				continue;

			foreach ( var rg in RenderGroups )
			{
				rg.Value.UnregisterSpriteGroup( id );
			}

			//Create render group if needed
			if ( !RenderGroups.ContainsKey( rendergroup ) )
			{
				var particleRenderer = (ParticleRenderer)system;
				CreateRenderGroup( rendergroup, BuildConfig( system, (GameTags)particleRenderer.Tags, particleRenderer.RenderOptions ) );
			}

			//Register in correct render group using shared block with offset and precomputed splot count
			RenderGroups[rendergroup].RegisterSprite( id, _sharedSprites, offset, count, splotCount, bounds );
		}

		//Final cleanup for systems that no longer exist
		foreach ( var rg in RenderGroups )
		{
			RemoveInactiveSpriteGroups( rg.Value );
		}
	}

	private void RemoveInactiveSpriteGroups( SpriteBatchSceneObject renderGroup )
	{
		_keysToRemoveScratch.Clear();

		foreach ( var id in renderGroup.SpriteGroups.Keys )
		{
			if ( !_activeParticleIds.Contains( id ) )
				_keysToRemoveScratch.Add( id );
		}

		foreach ( var key in _keysToRemoveScratch )
		{
			renderGroup.UnregisterSpriteGroup( key );
		}
	}

	internal void UpdateSpriteRenderers()
	{
		if ( Application.IsHeadless )
			return;

		_allSprites.Clear();
		Scene.GetAll<SpriteRenderer>( _allSprites );
		_currentEnabledSprites.Clear();

		foreach ( var sprite in _allSprites )
		{
			if ( sprite.Enabled && sprite.GameObject.Active )
			{
				sprite.Texture?.MarkUsed( ushort.MaxValue );

				//This is used to clean up inactive sprites
				_currentEnabledSprites.Add( sprite.Id );

				if ( _registeredSpriteRenderers.Contains( sprite.Id ) )
				{
					UpdateSprite( sprite.Id, sprite );
				}
				else
				{
					RegisterSprite( sprite.Id, sprite );
					_registeredSpriteRenderers.Add( sprite.Id );
				}
			}
		}

		//Frames are advanced by each renderer's own OnUpdate, where the events they reach can be run straight away on the main thread.

		//Registered sprites who are not enabled
		_spritesToRemove.Clear();
		foreach ( var spriteId in _registeredSpriteRenderers )
		{
			if ( !_currentEnabledSprites.Contains( spriteId ) )
				_spritesToRemove.Add( spriteId );
		}
		foreach ( var spriteId in _spritesToRemove )
		{
			UnregisterSprite( spriteId );
			_registeredSpriteRenderers.Remove( spriteId );
		}
	}

	private Action<int> processSystem;

	//How many particles are worth sharing out - an emitter's work is measured in them. See SpriteBatchSceneObject.ParallelSprites.
	const int ParallelParticles = 512;

	private void UpdateSprites()
	{
		using var _ = PerformanceStats.Timings.Render.Scope();

		if ( Application.IsHeadless )
			return;

		UpdateSpriteRenderers();
		UpdateParticleSprites();

		foreach ( var rg in RenderGroups )
		{
			rg.Value.UploadOnHost();
		}
	}

	private ulong GetRenderGroupKey( ISpriteRenderGroup component, GameTags tags, RenderOptions renderOptions )
	{
		var flags = InstanceGroupFlags.None;

		//Non-opaque and sorted needs transparency
		if ( !component.Opaque && component.IsSorted )
		{
			flags |= InstanceGroupFlags.Transparent;
		}

		//Shadows
		if ( component.Shadows && !component.Additive )
		{
			flags |= InstanceGroupFlags.CastShadow;
		}

		if ( component.Additive )
		{
			flags |= InstanceGroupFlags.Additive;
		}

		if ( component.Opaque )
		{
			flags |= InstanceGroupFlags.Opaque;
		}

		byte renderLayerFlags = (byte)(
			(renderOptions.Game ? 1 : 0) |
			(renderOptions.Overlay ? 2 : 0) |
			(renderOptions.AfterUI ? 8 : 0)
		);

		//Which material its sprites are drawn with, as its number - a batch is one draw and a draw is one material, so two sprites wearing
		//different ones cannot share a batch. Nought is the engine's own sprite shader, which is what nearly every sprite in a scene has.
		var material = NumberOf( component.MaterialOverride );

		//And where it is drawn among the others: the order is between draws, and a batch is one, so two sprites of different orders cannot share one.
		var order = component.SortOrder;

		var tokens = tags.GetTokens();
		//Single buffer: [4 bytes flags][1 byte renderLayer][4 bytes material][4 bytes order][4*N bytes tokens] - bounded to 269 bytes by the guard.
		Span<byte> buf = tokens.Count <= 64 ? stackalloc byte[13 + tokens.Count * 4] : new byte[13 + tokens.Count * 4];
		MemoryMarshal.Write( buf, in flags );
		buf[4] = renderLayerFlags;
		MemoryMarshal.Write( buf[5..], in material );
		MemoryMarshal.Write( buf[9..], in order );
		var tokenSlice = MemoryMarshal.Cast<byte, uint>( buf[13..] );
		ITagSet.Copy( tokens ).CopyTo( tokenSlice );
		MemoryExtensions.Sort( tokenSlice );

		return XxHash3.HashToUInt64( buf );
	}

	private static RenderGroupConfig BuildConfig( ISpriteRenderGroup component, GameTags tags, RenderOptions renderOptions )
	{
		var flags = InstanceGroupFlags.None;
		if ( !component.Opaque && component.IsSorted ) flags |= InstanceGroupFlags.Transparent;
		if ( component.Shadows && !component.Additive ) flags |= InstanceGroupFlags.CastShadow;
		if ( component.Additive ) flags |= InstanceGroupFlags.Additive;
		if ( component.Opaque ) flags |= InstanceGroupFlags.Opaque;

		return new RenderGroupConfig( flags, renderOptions.Clone(), tags.GetTokens().ToFrozenSet(), component.MaterialOverride, component.SortOrder );
	}

	//Find the component's current render group - might be outdated if component has changed. Returns null if not present in any
	private ulong? FindCurrentRenderGroup( Guid componentId )
	{
		foreach ( var rg in RenderGroups )
		{
			if ( rg.Value.ContainsSprite( componentId ) )
			{
				return rg.Key;
			}
		}

		return null;
	}

	private bool IsPresentInRenderGroup( Guid componentId, ulong renderGroup )
	{
		return RenderGroups[renderGroup].ContainsSprite( componentId );
	}

	private void InsertInRenderGroup( Guid componentId, SpriteRenderer component, ulong renderGroup )
	{
		Assert.True( RenderGroups.ContainsKey( renderGroup ) );
		RenderGroups[renderGroup].RegisterSprite( componentId, component );
	}

	private void RemoveFromRenderGroup( Guid componentId, ulong renderGroup )
	{
		Assert.True( IsPresentInRenderGroup( componentId, renderGroup ) );
		RenderGroups[renderGroup].UnregisterSprite( componentId );
	}

	private SpriteBatchSceneObject CreateRenderGroup( ulong key, RenderGroupConfig config )
	{
		var renderGroupObject = new SpriteBatchSceneObject( Scene );
		renderGroupObject.Flags.CastShadows = (config.Flags & InstanceGroupFlags.CastShadow) != 0;
		renderGroupObject.Flags.ExcludeGameLayer = (config.Flags & InstanceGroupFlags.CastOnlyShadow) != 0;
		renderGroupObject.Sorted = (config.Flags & InstanceGroupFlags.Transparent) != 0;
		renderGroupObject.Additive = (config.Flags & InstanceGroupFlags.Additive) != 0;
		renderGroupObject.Opaque = (config.Flags & InstanceGroupFlags.Opaque) != 0;
		renderGroupObject.Material = config.Material;
		renderGroupObject.DrawOrder = config.SortOrder;
		renderGroupObject.Tags.SetFrom( new TagSet( config.Tags.Select( StringToken.GetValue ) ) );
		config.RenderOptions.Apply( renderGroupObject );

		RenderGroups.Add( key, renderGroupObject );
		return renderGroupObject;
	}

	internal void RegisterSprite( Guid componentId, SpriteRenderer component )
	{
		var key = GetRenderGroupKey( component, component.Tags as GameTags, component.RenderOptions );
		if ( !RenderGroups.ContainsKey( key ) )
			CreateRenderGroup( key, BuildConfig( component, component.Tags as GameTags, component.RenderOptions ) );

		InsertInRenderGroup( componentId, component, key );
	}

	internal void UpdateSprite( Guid componentId, SpriteRenderer component )
	{
		//If found in old renderGroup, we unregister it and register it in the new one
		if ( FindCurrentRenderGroup( componentId ) is ulong oldRenderGroup )
		{
			var newRenderGroup = GetRenderGroupKey( component, (GameTags)component.Tags, component.RenderOptions );
			if ( !oldRenderGroup.Equals( newRenderGroup ) )
			{
				RemoveFromRenderGroup( componentId, oldRenderGroup );
				RegisterSprite( componentId, component );
			}
			else
			{
				//Same render group, just update the sprite data
				RenderGroups[oldRenderGroup].UpdateSprite( componentId, component );
			}
		}
		else
		{
			//Sprite not found in any render group, register it
			RegisterSprite( componentId, component );
		}
	}

	internal void UnregisterSprite( Guid componentId )
	{
		if ( FindCurrentRenderGroup( componentId ) is ulong rg )
		{
			RenderGroups[rg].UnregisterSprite( componentId );
		}
	}

	[Flags]
	internal enum InstanceGroupFlags
	{
		None = 0,
		CastShadow = 1 << 0,
		CastOnlyShadow = 1 << 1,
		Transparent = 1 << 2,
		Additive = 1 << 3,
		Opaque = 1 << 4
	}
}
