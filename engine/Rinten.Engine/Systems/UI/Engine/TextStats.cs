using System.Diagnostics;

namespace Rinten.UI;

//What turning text into pictures costs, counted where it happens: the label path (TextBlock, one picture a label) and the scope
//cache (gizmo text, TextRenderer, Graphics.DrawText - one picture a distinct string). Cumulative; read two snapshots and subtract.
internal static class TextStats
{
	internal struct Snapshot
	{
		//label path
		public long Restyles, RestyleTicks;          //UpdateStyles that rebuilt the block
		public long MeasureMisses, MeasureTicks;     //Measure() that ran the line breaker
		public long SizeHits, PictureHits, PictureHitTicks; //answered by TextPictures instead
		public long SizeReleases;                    //SizeFinalized threw the picture away for a new box
		public long Rebuilds, LayoutTicks, PaintTicks, UploadTicks;
		public long NewTextures, ReusedTextures, UploadBytes;
		public long LiveTextures, LiveBytes;         //label pictures held right now (incl. the kept spare)
		public long MaxRebuildsInFrame, MaxTicksInFrame, FramesWithRebuild;
		//scope cache path
		public long ScopeRasters, ScopeTicks, ScopeBytes;
		//managed bytes each path allocated - the garbage text makes
		public long LabelGarbage, ScopeGarbage;
		public long Frames;                          //FrameProfiler frames seen by the counter
	}

	static readonly object Gate = new();
	static Snapshot s;
	static int frame = -1;
	static long frameRebuilds, frameTicks;
	static int firstFrame = -1, lastFrame = -1;

	public static double Ms( long ticks ) => ticks * 1000.0 / Stopwatch.Frequency;

	/// <summary>What this thread has allocated so far - read before and after a piece of text work, see LabelGarbage.</summary>
	public static long Allocated => System.GC.GetAllocatedBytesForCurrentThread();

	public static void Garbage( long bytes, bool label )
	{
		lock ( Gate )
		{
			if ( label ) s.LabelGarbage += bytes;
			else s.ScopeGarbage += bytes;
		}
	}

	static void Frame( long ticks, bool rebuild )
	{
		var f = (int)Rinten.EngineLoop.LoopFrames;
		if ( firstFrame < 0 ) firstFrame = f;
		lastFrame = f;

		if ( f != frame )
		{
			frame = f;
			frameRebuilds = 0;
			frameTicks = 0;
		}

		if ( rebuild )
		{
			if ( frameRebuilds == 0 ) s.FramesWithRebuild++;
			frameRebuilds++;
		}

		frameTicks += ticks;
		if ( frameRebuilds > s.MaxRebuildsInFrame ) s.MaxRebuildsInFrame = frameRebuilds;
		if ( frameTicks > s.MaxTicksInFrame ) s.MaxTicksInFrame = frameTicks;
	}

	public static void Restyle( long ticks ) { lock ( Gate ) { s.Restyles++; s.RestyleTicks += ticks; Frame( ticks, false ); } }
	public static void MeasureMiss( long ticks ) { lock ( Gate ) { s.MeasureMisses++; s.MeasureTicks += ticks; Frame( ticks, false ); } }
	public static void SizeHit() { lock ( Gate ) s.SizeHits++; }
	public static void PictureHit( long ticks ) { lock ( Gate ) { s.PictureHits++; s.PictureHitTicks += ticks; Frame( ticks, false ); } }
	public static void SizeRelease() { lock ( Gate ) s.SizeReleases++; }

	public static void Rebuild( long layout, long paint, long upload, long bytes, bool reused )
	{
		lock ( Gate )
		{
			s.Rebuilds++;
			s.LayoutTicks += layout;
			s.PaintTicks += paint;
			s.UploadTicks += upload;
			s.UploadBytes += bytes;
			if ( reused ) s.ReusedTextures++;
			else { s.NewTextures++; s.LiveTextures++; s.LiveBytes += bytes; }
			Frame( layout + paint + upload, true );
		}
	}

	public static void Freed( long bytes ) { lock ( Gate ) { s.LiveTextures--; s.LiveBytes -= bytes; } }

	public static void Scope( long ticks, long bytes )
	{
		lock ( Gate ) { s.ScopeRasters++; s.ScopeTicks += ticks; s.ScopeBytes += bytes; Frame( ticks, false ); }
	}

	/// <summary>The counts so far; with reset, the per-frame maxima start again.</summary>
	public static Snapshot Take( bool resetMaxima = false )
	{
		lock ( Gate )
		{
			var copy = s;
			copy.Frames = firstFrame < 0 ? 0 : (int)Rinten.EngineLoop.LoopFrames - firstFrame;

			if ( resetMaxima )
			{
				s.MaxRebuildsInFrame = 0;
				s.MaxTicksInFrame = 0;
			}

			return copy;
		}
	}

	public static string Describe( in Snapshot a )
	{
		return $"label path: restyles {a.Restyles} ({Ms( a.RestyleTicks ):F2} ms), measure misses {a.MeasureMisses} ({Ms( a.MeasureTicks ):F2} ms), " +
			$"shared sizes {a.SizeHits}, shared pictures {a.PictureHits} ({Ms( a.PictureHitTicks ):F2} ms), size releases {a.SizeReleases}, rebuilds {a.Rebuilds} (layout {Ms( a.LayoutTicks ):F2} + paint {Ms( a.PaintTicks ):F2} + upload {Ms( a.UploadTicks ):F2} ms), " +
			$"new {a.NewTextures} / reused {a.ReusedTextures}, uploaded {a.UploadBytes / 1024.0:F0} KB, " +
			$"live {a.LiveTextures} pictures {a.LiveBytes / 1048576.0:F2} MB, frames with a rebuild {a.FramesWithRebuild}, " +
			$"worst frame {a.MaxRebuildsInFrame} rebuilds / {Ms( a.MaxTicksInFrame ):F2} ms of text; " +
			$"scope cache: rasters {a.ScopeRasters} ({Ms( a.ScopeTicks ):F2} ms, {a.ScopeBytes / 1024.0:F0} KB); " +
			$"garbage: label path {a.LabelGarbage / 1024.0:F0} KB, scope cache {a.ScopeGarbage / 1024.0:F0} KB";
	}
}
