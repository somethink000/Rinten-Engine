//Kept as a file rather than as text inside the engine's C#, so it is compiled the way every other shader is - includes
//and all - and a project may put its own file of this name in its assets to replace it. See CoreShaderCache.Pass.

//Bloom for the portable frame in pixel passes: kept, halved, carried back up, laid over the world; and the finish
//onto the screen of a frame drawn aside without it - see CorePortableGlow

#include "tonemap.slang"

struct V2F
{
	float4 position : SV_Position;
};

[[vk::push_constant]]
cbuffer Push
{
	float4 g_size;   //The written picture's 1/width, 1/height; the filtered read's 1/width, 1/height
	float4 g_glow;   //Threshold, falloff, mode, the glow's share of each level's total
	float4 g_tint;   //Strength times tint
	float4 g_finish; //Exposure, curve, one where the world read is light as it is rather than kept, one to soften edges
};

//What the pass reads; the up pass and the compose read a second picture beside it. A compose carrying a run of
//point effects reads them from the frame's block, whose file declares the two at the same places
#ifdef ENGINE_SCREEN_CHAIN
#include "frame.slang"
#else
[[vk::binding(0,0)]] Sampler2D g_color;
[[vk::binding(1,0)]] Sampler2D g_normal;
#endif

[shader("vertex")]
V2F MainVs( uint id : SV_VertexID )
{
	float2 corner = float2( ( id << 1 ) & 2, id & 2 );

	V2F o;
	o.position = float4( ( corner * 2.0 ) - 1.0, 0.0, 1.0 );
	return o;
}

//From the fragment's place, not the vertex's: GL flips the vertex and keeps rows bottom first, and the two cancel
float2 Uv( V2F i ) { return i.position.xy * g_size.xy; }

//The materials sample with repeat; held half a texel inside so a blur never wraps to the far edge
float3 Read( Sampler2D picture, float2 uv )
{
	float2 inside = g_size.zw * 0.5;
	return picture.SampleLevel( clamp( uv, inside, 1.0 - inside ), 0.0 ).rgb;
}

//The world as FinishToScreen wrote it - x / (1 + x) after exposure - back to light past white
float3 Decode( float3 kept )
{
	float3 c = min( kept, 254.0 / 255.0 );
	return c / ( 1.0 - c );
}

//What the world picture holds as the light it was shaded in: kept, or a pass's picture of light as it is
float3 Lit( float3 stored )
{
	return g_finish.z > 0.5 ? stored : Decode( stored ) / max( g_finish.x, 1e-4 );
}

float Brightest( float3 colour ) { return max( colour.r, max( colour.g, colour.b ) ); }

//Soft knee half the threshold wide, as bloom.shader keeps it
float Keep( float brightness, float threshold )
{
	float knee = max( threshold * 0.5, 1e-4 );

	float soft = clamp( brightness - threshold + knee, 0.0, 2.0 * knee );
	soft = ( soft * soft ) / ( 4.0 * knee );

	return max( soft, brightness - threshold );
}

//One texel of the world in the light it was shaded in, and its Karis weight
float3 Shaded( float2 uv, inout float weight )
{
	float3 colour = Lit( Read( g_color, uv ) );
	float w = 1.0 / ( 1.0 + Brightest( colour ) );

	weight += w;
	return colour * w;
}

//Four texel centres of the world under a half-size texel, Karis-weighted so one firefly can't dominate
[shader("fragment")]
float4 BrightPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float2 step = g_size.zw * 0.5;
	float weight = 0.0;

	float3 sum = Shaded( uv + float2( -step.x, -step.y ), weight ) +
		Shaded( uv + float2( step.x, -step.y ), weight ) +
		Shaded( uv + float2( -step.x, step.y ), weight ) +
		Shaded( uv + float2( step.x, step.y ), weight );

	float3 colour = sum / max( weight, 1e-4 );
	float brightness = Brightest( colour );

	return float4( colour * ( Keep( brightness, g_glow.x ) / max( brightness, 1e-4 ) ), 1.0 );
}

//A halving over four by four texels: the centre's two by two and four more at the corners (Bjorge's dual filter)
[shader("fragment")]
float4 DownPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float2 t = g_size.zw;

	float3 sum = Read( g_color, uv ) * 4.0 +
		Read( g_color, uv + float2( -t.x, -t.y ) ) +
		Read( g_color, uv + float2( t.x, -t.y ) ) +
		Read( g_color, uv + float2( -t.x, t.y ) ) +
		Read( g_color, uv + float2( t.x, t.y ) );

	return float4( sum * 0.125, 1.0 );
}

//This level plus the coarser one carried up by the falloff; four taps half a texel out make a 3x3 tent
[shader("fragment")]
float4 UpPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float2 t = g_size.zw * 0.5;

	float3 tent = ( Read( g_normal, uv + float2( -t.x, -t.y ) ) + Read( g_normal, uv + float2( t.x, -t.y ) ) +
		Read( g_normal, uv + float2( -t.x, t.y ) ) + Read( g_normal, uv + float2( t.x, t.y ) ) ) * 0.25;

	return float4( g_color.SampleLevel( uv, 0.0 ).rgb + ( tent * g_glow.y ), 1.0 );
}

float Luma( float3 colour ) { return dot( colour, float3( 0.299, 0.587, 0.114 ) ); }

//The world read with its edges softened where g_finish.w asks: compose.slang's FXAA, which the portable frame has no compose for
float3 World( float2 uv, float3 middle )
{
	if ( g_finish.w < 0.5 ) return middle;

	float2 texel = g_size.xy;

	float nw = Luma( g_color.SampleLevel( uv + ( float2( -1.0, -1.0 ) * texel ), 0.0 ).rgb );
	float ne = Luma( g_color.SampleLevel( uv + ( float2( 1.0, -1.0 ) * texel ), 0.0 ).rgb );
	float sw = Luma( g_color.SampleLevel( uv + ( float2( -1.0, 1.0 ) * texel ), 0.0 ).rgb );
	float se = Luma( g_color.SampleLevel( uv + ( float2( 1.0, 1.0 ) * texel ), 0.0 ).rgb );
	float m = Luma( middle );

	float low = min( m, min( min( nw, ne ), min( sw, se ) ) );
	float high = max( m, max( max( nw, ne ), max( sw, se ) ) );

	//Flat, or a step too dim to show
	if ( high - low < max( 1.0 / 24.0, high * 0.125 ) ) return middle;

	float2 direction = float2( -( ( nw + ne ) - ( sw + se ) ), ( nw + sw ) - ( ne + se ) );
	float reduce = max( ( nw + ne + sw + se ) * 0.25 * 0.125, 1.0 / 128.0 );
	float scale = 1.0 / ( min( abs( direction.x ), abs( direction.y ) ) + reduce );

	direction = clamp( direction * scale, -8.0, 8.0 ) * texel;

	float3 near = 0.5 * ( g_color.SampleLevel( uv + ( direction * ( ( 1.0 / 3.0 ) - 0.5 ) ), 0.0 ).rgb +
		g_color.SampleLevel( uv + ( direction * ( ( 2.0 / 3.0 ) - 0.5 ) ), 0.0 ).rgb );

	float3 wide = ( near * 0.5 ) + ( 0.25 * ( g_color.SampleLevel( uv + ( direction * -0.5 ), 0.0 ).rgb +
		g_color.SampleLevel( uv + ( direction * 0.5 ), 0.0 ).rgb ) );

	float found = Luma( wide );

	return found < low || found > high ? near : wide;
}

//Light onto the screen through the run of point effects, or exposure and the curve; the world's own alpha is what
//a see-through window shows the desktop by
float4 Finished( float3 colour, float alpha, float2 uv )
{
#ifdef ENGINE_SCREEN_CHAIN
	return float4( EngineScreenChain( colour, uv ), alpha );
#else
	return float4( EngineTonemap( colour * max( g_finish.x, 1e-4 ), g_finish.y ), alpha );
#endif
}

//The world with its glow onto the screen
[shader("fragment")]
float4 ComposePs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );

	float4 world = g_color.SampleLevel( uv, 0.0 );
	float3 colour = Lit( World( uv, world.rgb ) );
	float3 glow = Read( g_normal, uv ) * g_tint.rgb * g_glow.w;

	//Added; screened, which is src (1 - dst) + dst; or the glow alone - as bloom.shader's Apply
	if ( g_glow.z < 1.5 ) colour += glow;
	else if ( g_glow.z < 2.5 ) colour += glow * ( 1.0 - colour );
	else colour = glow;

	return Finished( colour, world.a, uv );
}

//The world alone onto the screen, for a frame drawn aside for its passes and with no glow
[shader("fragment")]
float4 FinishPs( V2F i ) : SV_Target
{
	float2 uv = Uv( i );
	float4 world = g_color.SampleLevel( uv, 0.0 );

	return Finished( Lit( World( uv, world.rgb ) ), world.a, uv );
}
