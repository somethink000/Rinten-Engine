//Where an impostor plane's corner goes, for impostor.shader and impostor_surface.slang: near, a box of the six baked
//sides, each facing the eye left where it was baked; far, one side, the nearest the eye's view, turned to face it

//Pixels across on the screen below which the model is the one turned side rather than the box
static const float ImpostorBillboardPixels = 48.0;

//A side edge-on to the eye is a smear of its picture; one turned further than this from the eye is left out of the box
static const float ImpostorBoxFacing = 0.4;

//The corner moved for this view, and whether its plane is drawn at all - false drops it before the raster
bool ImpostorPlace( inout float3 position, float3 normal, float4 sphere, row_major float4x4 modelToWorld )
{
	float3 centre = sphere.xyz;
	float3 centreWorld = mul( float4( centre, 1.0 ), modelToWorld ).xyz;

	//The eye from the centre in the model's space; nothing was baked from below, so below the horizon is the horizon
	float3 toEyeWorld = EYE_FRAME( camera ).xyz - centreWorld;
	float3 toEye = float3( dot( modelToWorld[0].xyz, toEyeWorld ), dot( modelToWorld[1].xyz, toEyeWorld ),
		dot( modelToWorld[2].xyz, toEyeWorld ) );
	toEye.y = max( toEye.y, 0.0 );

	//The sphere's size on the screen: its radius through the projection's y column, over the depth
	row_major float4x4 toClip = EYE_FRAME( worldToClip );
	float depth = max( mul( float4( centreWorld, 1.0 ), toClip ).w, 1e-4 );
	float tall = length( float3( toClip[0][1], toClip[1][1], toClip[2][1] ) );
	float pixels = sphere.w * length( modelToWorld[0].xyz ) * tall * EYE_FRAME( settings ).z / depth;

	if ( pixels >= ImpostorBillboardPixels )
		return dot( normal, normalize( toEye + float3( 0.0, 1e-20, 0.0 ) ) ) > ImpostorBoxFacing;

	//Every corner asks about the same centre, so exactly one plane is drawn
	float3 along = abs( toEye );
	float3 chosen = along.y >= along.x && along.y >= along.z ? float3( 0.0, 1.0, 0.0 )
		: along.x >= along.z ? float3( toEye.x < 0.0 ? -1.0 : 1.0, 0.0, 0.0 )
		: float3( 0.0, 0.0, toEye.z < 0.0 ? -1.0 : 1.0 );

	//A side turned about the upright through the centre until it faces the eye: the eye's way times the side's conjugate, in x and z
	if ( chosen.y == 0.0 )
	{
		float2 h = normalize( toEye.xz + float2( 1e-20, 0.0 ) );
		float2 q = float2( ( h.x * chosen.x ) + ( h.y * chosen.z ), ( h.y * chosen.x ) - ( h.x * chosen.z ) );
		float2 p = position.xz - centre.xz;

		position.xz = centre.xz + float2( ( q.x * p.x ) - ( q.y * p.y ), ( q.x * p.y ) + ( q.y * p.x ) );
	}

	return dot( normal, chosen ) >= 0.5;
}
