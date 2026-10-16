using Rinten.UI;
using System.Collections.Concurrent;

namespace Rinten;

[Expose]
public static partial class TextRendering
{
	//Scope will grow rich text features (spacing, shadows) and replace GetOrCreateTexture eventually

	/// <summary>Create a texture from the scope. The texture will either be a cached version or will be rendered immediately</summary>
	public static Texture GetOrCreateTexture( in Scope scope, Vector2 clip = default, TextFlag flag = TextFlag.LeftTop )
	{
		if ( Application.IsHeadless )
			return Texture.Invalid;

		if ( clip == default ) clip = 8096;

		var tb = GetOrCreateTextBlock( scope, flag, clip );
		tb.MakeReady();
		return tb.Texture;
	}

	//Resolves or creates a fully initialized TextBlock for the given scope. Safe to call
	//from any thread. MakeReady() must still be called on the render thread before drawing.
	internal static TextBlock GetOrCreateTextBlock( in Scope scope, TextFlag flag, Vector2 clip )
	{
		if ( Application.IsHeadless ) return null;

		var hc = new HashCode();
		hc.Add( scope );
		hc.Add( new Vector2( clip ) );
		hc.Add( flag );

		var hash = hc.ToHashCode();

		if ( Dictionary.TryGetValue( hash, out var tb ) )
			return tb;

		//Build a fully initialized candidate before publishing. If another thread wins the race, their instance is returned and ours is discarded.
		var candidate = new TextBlock();
		candidate.Clip = clip;
		candidate.Flags = flag;
		candidate.Initialize( scope );

		//GetOrAdd is race-safe: only one instance wins the slot. Set CacheKey on the winner so MakeReady can re-register if Tick() evicts it.
		var winner = Dictionary.GetOrAdd( hash, candidate );
		winner.CacheKey = hash;
		return winner;
	}

	static ConcurrentDictionary<int, TextBlock> Dictionary = new();

	//The cached block of this text where it has been drawn, without making one - see GetOrCreateTextBlock
	internal static bool TryGetTextBlock( in Scope scope, TextFlag flag, Vector2 clip, out TextBlock block )
	{
		var hc = new HashCode();
		hc.Add( scope );
		hc.Add( new Vector2( clip ) );
		hc.Add( flag );

		return Dictionary.TryGetValue( hc.ToHashCode(), out block );
	}

	[ThreadStatic] static TextBlock scratch;

	//A block on this thread for text measured or drawn once and let go, shaped in the same RichTextKit block every time - see TextBlock.Reuse
	internal static TextBlock Scratch( in Scope scope, TextFlag flags )
	{
		var block = scratch ??= new TextBlock();
		block.Reuse( scope, flags );
		return block;
	}

	//One shaped run of text, kept so the next frame does not shape it again.
	internal sealed class Shaping
	{
		public Topten.RichTextKit.TextBlock Block;
		public RealTimeSince SinceUsed;
	}

	//Text that has been shaped, by everything that decides its shape. See TextBlock.Build for what this is worth and why it can miss safely.
	internal static readonly ConcurrentDictionary<int, Shaping> Shaped = new();

	//How long a shaped run outlives its last use; kept short since resizing creates new keys every frame
	private const float ShapedLifetime = 0.5f;

	//Max shaped runs to keep; above this nothing new is cached until the sweep runs
	private const int ShapedCeiling = 1024;

	//Whether another shaped run is worth keeping.
	internal static bool RoomForShaped => Shaped.Count < ShapedCeiling;

	static RealTimeSince _timeSinceCleanup;

	//The fonts the cached text was set in - see FontManager.Generation.
	static int fontGeneration = -1;

	//Free old, unused textblocks (and their textures)
	internal static void Tick()
	{
		Assert.False( Application.IsHeadless );

		//A face came or went - on a page, one that was still downloading when the text was first set: what was
		//set in a stand-in is set again, or text drawn every frame keeps the stand-in for good
		var fonts = FontManager.Generation;

		if ( fonts != fontGeneration )
		{
			fontGeneration = fonts;
			Shaped.Clear();

			foreach ( var item in Dictionary ) item.Value.Dispose();
			Dictionary.Clear();
		}

		if ( _timeSinceCleanup < 0.5f ) return;
		_timeSinceCleanup = 0;

		//Evict stale shaped runs, then oldest ones until under the ceiling, instead of clearing everything
		foreach ( var pair in Shaped )
		{
			if ( pair.Value.SinceUsed < ShapedLifetime ) continue;

			Shaped.TryRemove( pair.Key, out _ );
		}

		if ( Shaped.Count > ShapedCeiling )
		{
			foreach ( var pair in Shaped.OrderByDescending( x => (float)x.Value.SinceUsed )
				.Take( Shaped.Count - ShapedCeiling ) )
			{
				Shaped.TryRemove( pair.Key, out _ );
			}
		}

		int total = Dictionary.Count;
		int deleted = 0;

		foreach ( var item in Dictionary )
		{
			if ( item.Value.TimeSinceUsed < 1.5f ) continue;

			item.Value.Dispose();
			Dictionary.TryRemove( item );
			deleted++;
		}

		//Log.Info( $"TextManager: {total} ({deleted} deleted)" );
	}

	internal static void Shutdown()
	{
		foreach ( var item in Dictionary )
		{
			item.Value.Dispose();
		}
		Dictionary.Clear();
	}
}
