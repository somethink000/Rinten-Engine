//Engine cluster pass for every surface: runs its Vertex and Shade over a vertex pulled from the pool

[shader("vertex")]
V2F MainClusterVs( uint vid : SV_VertexID )
{
	ClusterVertex v;
	LoadClusterVertex( vid, v );

	//The draw's numbers before the body runs: the body reads them.
	ClusterInstance( v.instance );

	//And where the corner was before anything posed it, for a body that asks.
	g_clusterRest = v.rest;

	VIn i;
	i.position = v.position;
	i.colour = v.colour;
	i.frame = v.frame;
	i.texCoord0 = v.uv0;
	i.texCoord1 = v.uv1;

	V2F o = Vertex( i );

	//Use the pre-pass depth unless the surface moves vertices; then this pass writes its own depth
#if !defined( ENGINE_MOVES_VERTICES ) || ENGINE_MOVES_VERTICES == 0
	o.position = v.clip;
#endif

	//The instance, for the pixel stage to take its numbers from.
	o.depthId = v.instance;

#ifdef ENGINE_SHADOW_REST
	o.shadowRest = float4( mul( float4( v.position, 1.0 ), g_modelToWorld ).xyz, 1.0 );
#endif

	return o;
}

//The depth-only build writes no colour: Shade runs for its cut-out alone - see Stage.ClusterDepthPixel
#ifdef DEPTH_ONLY
#define CLUSTER_OUT void
#define CLUSTER_RETURN( shaded ) shaded
#else
#define CLUSTER_OUT Out
#define CLUSTER_RETURN( shaded ) return shaded
#endif

[shader("fragment")]
CLUSTER_OUT MainClusterPs( V2F i, bool front : SV_IsFrontFace )
{
	ClusterInstance( i.depthId );

#ifdef ENGINE_SHADOW_REST
	g_shadowRest = i.shadowRest;
#endif

	//Which side of the sheet, only where the material draws both - see F_BACKFACES in complex.shader. A surface that has no such feature is
	//always looked at from the front. Nested, because a preprocessor asked about a name nobody defined says so even on the far side of an &&.
	bool facing = true;

#ifdef F_BACKFACES
#if F_BACKFACES
	facing = front;
#endif
#endif

	CLUSTER_RETURN( Shade( i, facing ) );
}

#ifdef VELOCITY
//A surface that moves its own corners (wind) has motion its matrix does not tell: its Vertex is run twice,
//now and a frame back - the instance's previous matrix and scene clock - and the two compared on the screen
struct VelocityV2F
{
	float4 position : SV_Position;
	float4 now : TEXCOORD0;
	float4 before : TEXCOORD1;
};

[shader("vertex")]
VelocityV2F MainClusterVelocityVs( uint vid : SV_VertexID )
{
	ClusterVertex v;
	LoadClusterVertex( vid, v );

	ClusterInstance( v.instance );
	g_clusterRest = v.rest;

	VIn i;
	i.position = v.position;
	i.colour = v.colour;
	i.frame = v.frame;
	i.texCoord0 = v.uv0;
	i.texCoord1 = v.uv1;

	V2F now = Vertex( i );

	VisInstance instance = g_visInstances[v.instance];
	g_modelToWorld = float4x4( instance.prev0, instance.prev1, instance.prev2, instance.prev3 );
	g_frameBefore = true;

	V2F was = Vertex( i );

	VelocityV2F o;
	o.position = now.position;
	o.now = now.position;
	o.before = mul( float4( was.world, 1.0 ), EYE_FRAME( previousWorldToClip ) );
	return o;
}

[shader("fragment")]
float4 MainClusterVelocityPs( VelocityV2F i ) : SV_Target
{
	if ( i.now.w <= 0.0001 || i.before.w <= 0.0001 ) return float4( 0.0, 0.0, 0.0, 0.0 );

	float2 here = ( ( i.now.xy / i.now.w ) * 0.5 ) + 0.5;
	float2 there = ( ( i.before.xy / i.before.w ) * 0.5 ) + 0.5;

	//As velocity.slang writes it: screen delta, view depth, written
	return float4( here - there, i.now.w, 1.0 );
}
#endif
