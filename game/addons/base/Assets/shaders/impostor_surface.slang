//Impostor planes shaded by the model's own surface: the planes ImpostorPlace draws, and per pixel the real triangle the
//sheet's picture names, its corners read out of the pool and run through the surface's Vertex and Shade

#include "impostor_place.slang"

//The material records - flags, cover picture, cutoff, triangle picture - see CoreMaterialTable.Write. A draw is one
//material, so the pictures they name are the same over the draw and are indexed plainly
[[vk::binding(7,2)]] StructuredBuffer<uint> g_visMaterials;

struct ImpostorV2F
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;

	//Instance, the model's first cluster, the parts this plane shades, unused
	nointerpolation uint4 named : TEXCOORD1;

	//The model's sphere in the world, which the sun's shadow is read outside of
	nointerpolation float4 sphere : TEXCOORD2;
};

//Interleaved gradient noise (Jimenez 2014): a share of cover is that share of pixels, spread evenly
float ImpostorThreshold( float2 pixel )
{
	return frac( 52.9829189 * frac( dot( floor( pixel ), float2( 0.06711056, 0.00583715 ) ) ) );
}

[shader("vertex")]
ImpostorV2F MainImpostorVs( uint vid : SV_VertexID )
{
	uint4 named = asuint( g_visNamed[0] );
	uint pair = named.x + ( ( vid >> 7 ) * 2 );
	uint index = g_visVisible[pair];
	uint cluster = g_visVisible[pair + 1] & 0x3FFFFFFFu;

	ClusterVertex v;
	LoadClusterCorner( index, cluster, vid & 127u, v );
	ClusterInstance( index );

	row_major float4x4 modelToWorld = g_modelToWorld;
	float3 position = v.position;
	float3 centre = v.uv1.xyz;

	bool drawn = ImpostorPlace( position, VertexFrameNormal( v.frame ), v.uv1, modelToWorld );

	precise float3 world = mul( float4( position, 1.0 ), modelToWorld ).xyz;

	ImpostorV2F o;
	o.position = mul( float4( world, 1.0 ), EYE_FRAME( worldToClip ) );

	//The planes not drawn past the screen's edge, so the raster drops them before a pixel
	if ( !drawn ) o.position = float4( 2.0, 2.0, 0.5, 1.0 );

	o.uv = v.uv0.xy;

	//Each corner says which of the model's clusters its plane is, and its colour which parts it shades - see ModelBuilder.ImpostorPicture
	uint parts = g_visVertices.Load( ( v.rest * VisVertexBytes ) + EngineVertexColour );
	o.named = uint4( index, cluster - uint( v.uv0.z ), parts, 0u );
	o.sphere = float4( mul( float4( centre, 1.0 ), modelToWorld ).xyz, v.uv1.w * length( modelToWorld[0].xyz ) );

	return o;
}

//The sheet's word under the pixel, nearest texel of the level the plane's footprint wants: the cluster
//in the model plus one, thirteen bits; the triangle in it, seven; its second and third weights, six each
uint ImpostorWord( uint picture, float2 uv )
{
	uint wide, tall, levels;
	g_textures[picture].GetDimensions( 0u, wide, tall, levels );

	float2 size = float2( wide, tall );
	float2 dx = ddx( uv ) * size;
	float2 dy = ddy( uv ) * size;
	float level = clamp( floor( 0.5 * log2( max( max( dot( dx, dx ), dot( dy, dy ) ), 1e-8 ) ) ), 0.0, float( levels - 1u ) );

	uint k = uint( level );
	uint2 extent = max( uint2( wide, tall ) >> k, uint2( 1u, 1u ) );
	int2 texel = int2( min( uint2( uv * float2( extent ) ), extent - 1u ) );

	uint4 b = uint4( round( g_textures[picture].Load( int3( texel, int( k ) ) ) * 255.0 ) );
	return b.x | ( b.y << 8 ) | ( b.z << 16 ) | ( b.w << 24 );
}

//One corner of a cluster's triangle, as the pool holds it
uint ImpostorCorner( VisCluster held, uint triangle, uint k )
{
	uint at = ( ( held.words.y + triangle ) * 3u ) + k;
	uint local = ( g_visClusterTriangles[at >> 2] >> ( ( at & 3u ) * 8u ) ) & 255u;
	return g_visClusterVertices[held.words.x + local];
}

[shader("fragment")]
CLUSTER_OUT MainImpostorPs( ImpostorV2F p )
{
	uint index = p.named.x;
	ClusterInstance( index );

	uint material = asuint( g_visNamed[0] ).y;

	//The page's cover against a fixed pattern, as the old impostor cut it: a texel half covered is half the pixels round it
	uint cover = g_visMaterials[( material * 4u ) + 1u];
	float covered = g_textures[cover].Sample( p.uv ).a;
	if ( covered <= ImpostorThreshold( p.position.xy ) ) discard;

	uint word = ImpostorWord( g_visMaterials[( material * 4u ) + 3u], p.uv );
	if ( ( word >> 19 ) == 0u ) discard;

	VisCluster held = g_visClusters[p.named.y + ( word >> 19 ) - 1u];
	uint triangle = ( word >> 12 ) & 127u;

	//A texel another material's plane shades, or a triangle the cluster does not have
	uint part = held.words.w & 0xFFFFu;
	if ( ( p.named.z & ( 1u << min( part, 31u ) ) ) == 0u || triangle >= ( held.words.z >> 16 ) ) discard;

	float wb = float( ( word >> 6 ) & 63u ) / 63.0;
	float wc = float( word & 63u ) / 63.0;
	float wa = max( 1.0 - wb - wc, 0.0 );
	float3 w = float3( wa, wb, wc ) / max( wa + wb + wc, 1e-6 );

	uint ra = ImpostorCorner( held, triangle, 0u );
	VisVertex a = LoadVisVertex( ra );
	VisVertex b = LoadVisVertex( ImpostorCorner( held, triangle, 1u ) );
	VisVertex c = LoadVisVertex( ImpostorCorner( held, triangle, 2u ) );

	g_clusterRest = ra;

	float3 normal = normalize( ( VertexFrameNormal( a.frame ) * w.x ) + ( VertexFrameNormal( b.frame ) * w.y ) +
		( VertexFrameNormal( c.frame ) * w.z ) );
	float4 ta = VertexFrameTangent( a.frame );
	float3 tangent = ( ta.xyz * w.x ) + ( VertexFrameTangent( b.frame ).xyz * w.y ) + ( VertexFrameTangent( c.frame ).xyz * w.z );
	tangent = normalize( tangent - ( normal * dot( tangent, normal ) ) + float3( 1e-7, 0.0, 0.0 ) );

	VIn i;
	i.position = ( a.position * w.x ) + ( b.position * w.y ) + ( c.position * w.z );
	i.frame = VertexFrameEncode( normal, float4( tangent, ta.w ) );
	i.texCoord0 = ( a.uv0 * w.x ) + ( b.uv0 * w.y ) + ( c.uv0 * w.z );
	i.texCoord1 = ( a.uv1 * w.x ) + ( b.uv1 * w.y ) + ( c.uv1 * w.z );

	//Exactly the corners' colour where they agree: a surface may read bits out of it
	bool same = all( a.colour == b.colour ) && all( a.colour == c.colour );
	i.colour = same ? a.colour : ( a.colour * w.x ) + ( b.colour * w.y ) + ( c.colour * w.z );

	V2F o = Vertex( i );
	o.position = p.position;
	o.depthId = index;

	//The sun's shadow read outside the model's sphere: the planes cast it, and would shade their own crown
	float3 world = mul( float4( i.position, 1.0 ), g_modelToWorld ).xyz;
	float3 l = g_frame[0].sunDirection.xyz;
	g_shadowRest = float4( world + ( l * max( 0.0, ( p.sphere.w * 1.05 ) - dot( world - p.sphere.xyz, l ) ) ), 1.0 );

	bool facing = true;

#ifdef F_BACKFACES
#if F_BACKFACES
	facing = dot( mul( float4( normal, 0.0 ), g_modelToWorld ).xyz, EYE_FRAME( camera ).xyz - world ) >= 0.0;
#endif
#endif

	CLUSTER_RETURN( Shade( o, facing ) );
}
