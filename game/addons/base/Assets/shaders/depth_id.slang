//First V2F interpolants (cutout UV, instance id, stereo eye); implicit locations keep them at 0 and 1
//A surface moving its own corners carries where it stands at rest, w one where the cluster pass filled it in: its shadow
//is cast from rest and read there, or a swaying leaf passes in and out of it - see SunShadow
#ifdef ENGINE_SHADOW_REST
#define SHADOW_REST_FIELD float4 shadowRest : TEXCOORD17;
#define SHADOW_REST_SET( o ) ; o.shadowRest = 0.0
#else
#define SHADOW_REST_FIELD
#define SHADOW_REST_SET( o )
#endif

#ifdef STEREO
#define DEPTH_ID_FIELD float2 depthUv : TEXCOORD14; nointerpolation uint depthId : TEXCOORD15; nointerpolation uint eyeId : TEXCOORD16; SHADOW_REST_FIELD
#define DEPTH_ID_SET( o, uv ) o.depthUv = uv; o.depthId = 0; o.eyeId = ViewOfThisVertex() SHADOW_REST_SET( o )

//Published at a pixel entry, before anything of the surface runs.
#define STEREO_EYE_IN( i ) g_eyeId = i.eyeId
#else
#define DEPTH_ID_FIELD float2 depthUv : TEXCOORD14; nointerpolation uint depthId : TEXCOORD15; SHADOW_REST_FIELD
#define DEPTH_ID_SET( o, uv ) o.depthUv = uv; o.depthId = 0 SHADOW_REST_SET( o )

#define STEREO_EYE_IN( i )
#endif
