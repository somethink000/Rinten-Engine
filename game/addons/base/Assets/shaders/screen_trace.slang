//Screen-space ray trace through a nearest-depth mip chain, refined on full depth at the end. Depth is a camera's, reversed:
//it falls with distance, so nearer is greater - see NearDepth in frame.slang

//Where a ray met the depth: whether it did, where on the screen, and how much to believe it - less at the edge
//of the screen and at the end of the ray, where a reflection that stopped dead would give the technique away.
struct ScreenHit
{
	bool found;
	float2 uv;
	float fade;
};

//The nearest depth under one texel of a level of the chain - read at the texel's middle, where the filtering sampler hands the texel back as it is.
float ScreenTraceNearest( uint chain, float2 cell, float level, float2 size )
{
	float2 levelSize = max( floor( size / exp2( level ) ), 1.0 );
	float2 uv = ( clamp( cell, 0.0, levelSize - 1.0 ) + 0.5 ) / levelSize;

	return g_textures[NonUniformResourceIndex( chain )].SampleLevel( uv, level ).r;
}

//Where on the screen and in the world a depth as written is.
float3 ScreenTracePlace( float2 uv, float depth, row_major float4x4 clipToWorld )
{
	float4 world = mul( float4( ( uv * 2.0 ) - 1.0, depth, 1.0 ), clipToWorld );
	return world.xyz / max( world.w, 0.000001 );
}

//The depth at a texel of the full-size depth - or, with none, of the chain's finest level, which is the best there is.
float ScreenTraceDepth( uint fine, uint chain, float2 texel, float2 fineSize )
{
	float2 uv = ( clamp( texel, 0.0, fineSize - 1.0 ) + 0.5 ) / fineSize;

	return fine != 0u
		? g_textures[NonUniformResourceIndex( fine )].SampleLevel( uv, 0.0 ).r
		: g_textures[NonUniformResourceIndex( chain )].SampleLevel( uv, 0.0 ).r;
}

//Whether the surface at a texel faces the ray - nought normals, nothing to ask. A ray meets the front
//of what it hits; one that found the back of something, or the surface it left, has found nothing.
bool ScreenTraceFaces( uint normals, float2 texel, float2 fineSize, float3 ray )
{
	if ( normals == 0u ) return true;

	float2 uv = ( clamp( texel, 0.0, fineSize - 1.0 ) + 0.5 ) / fineSize;
	float3 normal = ( g_textures[NonUniformResourceIndex( normals )].SampleLevel( uv, 0.0 ).rgb * 2.0 ) - 1.0;

	return dot( normal, ray ) < 0.0;
}

//Steps the ray across one finest chain texel per depth texel; true and t where it hits a surface
bool ScreenTraceFine( uint chain, uint fine, uint normals, float3 o, float3 d, float2 size, float2 fineSize,
	inout float t, float exit, float3 ray, row_major float4x4 clipToWorld, float3 camera )
{
	float2 scale = fineSize / size;

	//A texel of the chain is two of the depth's across: a ray crosses at most three of them in one, and a fourth guards the rounding at an edge.
	for ( int j = 0; j < 4 && t < exit; j++ )
	{
		float2 at = ( o.xy + ( d.xy * t ) ) * scale;
		float2 texel = floor( at );

		float2 edge = ( texel + float2( d.x > 0.0 ? 1.0 : 0.0, d.y > 0.0 ? 1.0 : 0.0 ) ) / scale;
		float2 crossing = float2(
			abs( d.x ) > 1e-6 ? ( edge.x - o.x ) / d.x : 1e9,
			abs( d.y ) > 1e-6 ? ( edge.y - o.y ) / d.y : 1e9 );
		float leaves = min( exit, max( min( crossing.x, crossing.y ), t + 1e-5 ) );

		float surface = ScreenTraceDepth( fine, chain, texel, fineSize );
		float zIn = o.z + ( d.z * t );
		float zOut = o.z + ( d.z * leaves );

		if ( !IsFarDepth( surface ) && min( zIn, zOut ) <= surface )
		{
			//Reached from in front, inside this texel: there.
			if ( zIn >= surface )
			{
				if ( ScreenTraceFaces( normals, texel, fineSize, ray ) )
				{
					t = d.z < 0.0 ? t + ( ( surface - zIn ) / d.z ) : t;
					return true;
				}
			}
			else
			{
				//Behind it already as it came in: the surface it met, if only just behind it - a ray
				//steeper than one texel of the depth's - and otherwise something it passes behind.
				float2 uv = at / fineSize;
				float rayFar = length( ScreenTracePlace( uv, zIn, clipToWorld ) - camera );
				float behind = rayFar - length( ScreenTracePlace( uv, surface, clipToWorld ) - camera );

				if ( behind < 0.05 + ( 0.02 * rayFar ) && ScreenTraceFaces( normals, texel, fineSize, ray ) )
					return true;
			}
		}

		t = leaves + ( 0.01 / max( length( d.xy * scale ), 1e-4 ) );
	}

	return false;
}

//`ray` from `from` through the chain `chain`, as far as fifty metres or the edge of the screen, in at most `steps` reads of it. `fine` is the depth
//at its own size and `normals` the world's normals, for the last word on where it met something - nought for either the frame does not have to hand.
ScreenHit ScreenTrace( uint chain, uint fine, uint normals, float3 from, float3 ray, row_major float4x4 worldToClip,
	row_major float4x4 clipToWorld, float3 camera, int steps )
{
	ScreenHit hit;
	hit.found = false;
	hit.uv = float2( 0.0, 0.0 );
	hit.fade = 0.0;

	float4 start = mul( float4( from, 1.0 ), worldToClip );
	float4 along = mul( float4( ray, 0.0 ), worldToClip );
	if ( start.w <= 0.0 ) return hit;

	//As far as fifty metres, or short of the eye where it comes back towards it.
	float reach = 50.0;
	if ( along.w < 0.0 ) reach = min( reach, ( start.w * 0.9 ) / -along.w );

	float4 end = start + ( along * reach );

	//In the finest level's texels across and down, and the depth as written - the chain's own size, asked of it, and how many levels it has.
	uint wide, tall, levels;
	g_textures[NonUniformResourceIndex( chain )].GetDimensions( 0u, wide, tall, levels );
	float2 size = float2( float( wide ), float( tall ) );

	//And the depth's own, which is what a texel of it is measured in.
	float2 fineSize = size;
	if ( fine != 0u )
	{
		uint fineWide, fineTall, fineLevels;
		g_textures[NonUniformResourceIndex( fine )].GetDimensions( 0u, fineWide, fineTall, fineLevels );
		fineSize = float2( float( fineWide ), float( fineTall ) );
	}

	float3 o = float3( ( ( start.xy / start.w ) * 0.5 + 0.5 ) * size, start.z / start.w );
	float3 e = float3( ( ( end.xy / end.w ) * 0.5 + 0.5 ) * size, end.z / end.w );
	float3 d = e - o;

	//Where it leaves the screen.
	float2 leave = float2(
		d.x > 0.0 ? ( size.x - o.x ) / d.x : d.x < 0.0 ? -o.x / d.x : 1e9,
		d.y > 0.0 ? ( size.y - o.y ) / d.y : d.y < 0.0 ? -o.y / d.y : 1e9 );
	float last = min( 1.0, min( leave.x, leave.y ) );

	//A texel of the depth out first, past the surface's own: the last word is the depth's, so nothing coarser needs stepping over.
	float pixels = max( length( d.xy ), 1e-4 );
	float t = ( size.x / fineSize.x ) / pixels;
	float nudge = 0.01 / pixels;

	int top = max( int( levels ) - 1, 0 );
	int level = 0;

	for ( int k = 0; k < steps && t < last; k++ )
	{
		float3 p = o + ( d * t );
		float cellSize = exp2( float( level ) );
		float2 cell = floor( p.xy / cellSize );

		//Where the ray leaves this cell.
		float2 edge = ( cell + float2( d.x > 0.0 ? 1.0 : 0.0, d.y > 0.0 ? 1.0 : 0.0 ) ) * cellSize;
		float2 crossing = float2(
			abs( d.x ) > 1e-6 ? ( edge.x - o.x ) / d.x : 1e9,
			abs( d.y ) > 1e-6 ? ( edge.y - o.y ) / d.y : 1e9 );
		float exit = min( min( crossing.x, crossing.y ), last );

		float nearest = ScreenTraceNearest( chain, cell, float( level ), size );

		if ( p.z > nearest )
		{
			//In front of everything in the cell here. Going deeper, it may reach the nearest before it leaves: then there, a level finer.
			float reaches = d.z < 0.0 ? ( nearest - o.z ) / d.z : 1e9;

			if ( reaches < exit )
			{
				t = max( t, reaches );

				if ( level > 0 )
				{
					level--;
					continue;
				}

				//At the finest the depth itself says where, if anywhere, in this texel - see ScreenTraceFine.
				if ( ScreenTraceFine( chain, fine, normals, o, d, size, fineSize, t, exit, ray, clipToWorld, camera ) )
				{
					hit.found = true;
					break;
				}

				//Across it clear: up a level again, as from any cell it passes.
				t = exit + nudge;
				level = min( level + 1, top );
			}
			else
			{
				t = exit + nudge;
				level = min( level + 1, top );
			}
		}
		else if ( level > 0 )
		{
			level--;
		}
		else
		{
			//At the finest, behind the nearest in the texel - which is the nearest of four, and the ray may be in front of the one under it.
			if ( ScreenTraceFine( chain, fine, normals, o, d, size, fineSize, t, exit, ray, clipToWorld, camera ) )
			{
				hit.found = true;
				break;
			}

			//Across it, in front or behind: up a level, which comes straight back down where the ray is still
			//behind the nearest there - a ray leaving a floor at a glance was walked a texel at a time across it.
			t = exit + nudge;
			level = min( level + 1, top );
		}
	}

	//Out of steps while grazing a surface: count it as a hit if close and facing the ray
	if ( !hit.found && t < last )
	{
		float3 p = o + ( d * t );
		float2 uv = p.xy / size;
		float2 texel = floor( uv * fineSize );
		float surface = ScreenTraceDepth( fine, chain, texel, fineSize );
		float rayFar = length( ScreenTracePlace( uv, p.z, clipToWorld ) - camera );
		float gap = abs( rayFar - length( ScreenTracePlace( uv, surface, clipToWorld ) - camera ) );

		hit.found = !IsFarDepth( surface ) && gap < 0.25 + ( 0.05 * rayFar ) &&
			ScreenTraceFaces( normals, texel, fineSize, ray );
	}

	if ( !hit.found ) return hit;

	hit.uv = ( o.xy + ( d.xy * t ) ) / size;

	float2 border = min( hit.uv, 1.0 - hit.uv );
	hit.fade = saturate( min( border.x, border.y ) / 0.08 ) * saturate( ( 1.0 - ( t / last ) ) / 0.2 );

	return hit;
}
