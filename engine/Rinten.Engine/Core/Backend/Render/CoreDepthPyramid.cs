namespace Rinten;

//The depth pyramid: a viewport's depth folded down level by level, each the farthest of the four below
//it, which is what a cull may test a box against and be certain - see shaders/engine/mesh_cull.slang.
internal static class CoreDepthPyramid
{
	[ConVar( "r_occlusion_grain", ConVarFlags.Protected, Min = 64, Max = 2048,
		Help = "How many patches across the depth the cull pass tests against, a power of two." )]
	internal static int Grain
	{
		get => grain;
		set
		{
			var wanted = System.Math.Clamp( value, 64, 2048 );
			var side = 64;

			while ( side < wanted ) side <<= 1;

			grain = side;
		}
	}

	private static int grain = 1024;

	//How many levels one dispatch writes - the core's PyramidLevelsAtOnce.
	internal const int LevelsAtOnce = 6;

	//One viewport's pyramid in patches: the first level's texels the depth fills, and the power of two each way it is
	//kept in, the rest far, so every level halves the one below exactly and spans the whole screen. Each drawer keeps its
	//own: views of other sizes in the same frame would otherwise rebuild each other's and cull by the wrong one's shape
	internal readonly record struct Shape( int Wide, int Tall, int ImageWide, int ImageTall )
	{
		//Level count down to one texel; each holds the farthest of the four below it
		internal int Levels
		{
			get
			{
				var levels = 1;

				for ( int w = Wide, h = Tall; w > 1 || h > 1; w = System.Math.Max( 1, w >> 1 ), h = System.Math.Max( 1, h >> 1 ) )
					levels++;

				return levels;
			}
		}

		//The groups of the pyramid's two launches - a group a 32x32 tile of the first level each writes - packed across in the low half and down
		//in the high. What the cull's finish pass writes as the second build's arguments, so the two agree on the shape without a readback.
		internal (uint First, uint Second) DispatchGroups()
		{
			var shape = this;

			uint Packed( int lod )
			{
				var wide = (uint)System.Math.Max( 1, shape.Wide >> lod );
				var tall = (uint)System.Math.Max( 1, shape.Tall >> lod );

				return ( ( wide + 31 ) / 32 ) | ( ( ( tall + 31 ) / 32 ) << 16 );
			}

			return ( Packed( 0 ), Levels > LevelsAtOnce ? Packed( LevelsAtOnce ) : 0u );
		}
	}

	//The pyramid for a depth this size: half the depth each way up to the grain, at most 8x8 depth texels a level-0 texel,
	//kept in the next power of two - an odd level would lose its last row or column, and the cull read a strip beside the box
	internal static Shape Fit( int width, int height )
	{
		var wide = Filled( width );
		var tall = Filled( height );

		return new Shape( Kept( wide ), Kept( tall ), wide, tall );
	}

	private static int Filled( int depth )
	{
		depth = System.Math.Max( depth, 1 );
		return System.Math.Max( System.Math.Clamp( ( depth + 1 ) / 2, ( depth + 7 ) / 8, grain ), ( depth + 7 ) / 8 );
	}

	private static int Kept( int filled )
	{
		var side = 1;
		while ( side < filled ) side <<= 1;
		return side;
	}
}
