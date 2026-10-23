//One clutter patch placed on the card: each ground triangle's counted copies, kept by the paint under them,
//each into its own instance slot - see TerrainClutter.Placed

FEATURES
{
}

PARAMETERS
{
	//Which picture holds the terrain's numbers, for where its paint mask is; nought for none.
	float4 g_vClutter < Attribute( "Clutter" ); >;
}

#include "postprocess.slang"

//The portable frame has no compute and no instance table: its clutter is placed by the processor.
#ifdef PORTABLE
[numthreads( 64, 1, 1 )]
void MainCs( uint3 id : SV_DispatchThreadID )
{
}
#else
#define g_flParams ( g_vClutter.x )

#include "engine/terrain_layers.slang"

//The copies' record, the model's sphere, the run, the rules, six a triangle - see TerrainClutter.Placed
[[vk::binding( 12, 2 )]] StructuredBuffer<uint4> g_patch;

//The scene's instance table, a record of 256 bytes a slot - see CoreSceneTables.InstanceRecord.
[[vk::binding( 13, 2 )]] RWByteAddressBuffer g_instances;

static const uint TemplateAt = 0u;
static const uint BoundAt = 16u;
static const uint RunAt = 17u;
static const uint RulesAt = 18u;
static const uint TrianglesAt = 23u;
static const uint TriangleWords = 6u;

static const uint RecordBytes = 256u;
static const uint InstanceDrawnBit = 1u;

static const uint RuleYaw = 1u;
static const uint RuleAlign = 2u;

//The next number from nought up to one out of a run of bits - the same run as TerrainClutter.Unit.
float Unit( inout uint bits )
{
	bits ^= bits << 13;
	bits ^= bits >> 17;
	bits ^= bits << 5;

	return float( bits >> 8 ) * ( 1.0 / 16777216.0 );
}

//Quaternions as System.Numerics has them: x, y, z, then w.
float4 Turn( float4 a, float4 b )
{
	return float4(
		( a.w * b.x ) + ( a.x * b.w ) + ( a.y * b.z ) - ( a.z * b.y ),
		( a.w * b.y ) - ( a.x * b.z ) + ( a.y * b.w ) + ( a.z * b.x ),
		( a.w * b.z ) + ( a.x * b.y ) - ( a.y * b.x ) + ( a.z * b.w ),
		( a.w * b.w ) - ( a.x * b.x ) - ( a.y * b.y ) - ( a.z * b.z ) );
}

//The rows of a turn's matrix, a point times them - as Matrix4x4.CreateFromQuaternion.
float3x3 RowsOf( float4 q )
{
	float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
	float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
	float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;

	return float3x3(
		1.0 - ( 2.0 * ( yy + zz ) ), 2.0 * ( xy + wz ), 2.0 * ( xz - wy ),
		2.0 * ( xy - wz ), 1.0 - ( 2.0 * ( zz + xx ) ), 2.0 * ( yz + wx ),
		2.0 * ( xz + wy ), 2.0 * ( yz - wx ), 1.0 - ( 2.0 * ( yy + xx ) ) );
}

//How much of the layers asked for four of a paint's carry, a corner's share of it.
float Grows( uint layers, uint weights, uint wanted )
{
	float sum = 0.0;

	[unroll]
	for ( uint s = 0u; s < 4u; s++ )
	{
		uint layer = ( layers >> ( s * 8u ) ) & 0xFFu;

		if ( layer < 32u && ( wanted & ( 1u << layer ) ) != 0u ) sum += float( ( weights >> ( s * 8u ) ) & 0xFFu ) / 255.0;
	}

	return sum;
}

void Store( uint slot, uint4 words, uint at )
{
	g_instances.Store4( ( slot * RecordBytes ) + ( at * 16u ), words );
}

[numthreads( 64, 1, 1 )]
void MainCs( uint3 id : SV_DispatchThreadID )
{
	uint4 run = g_patch[RunAt];

	if ( id.x >= run.z ) return;

	uint head = run.x;
	uint at = TrianglesAt + ( id.x * TriangleWords );

	uint4 t0 = g_patch[at];
	uint4 t1 = g_patch[at + 1u];
	uint4 t2 = g_patch[at + 2u];
	uint4 t3 = g_patch[at + 3u];
	uint4 t4 = g_patch[at + 4u];
	uint4 t5 = g_patch[at + 5u];

	float3 a = asfloat( t0.xyz );
	float3 b = asfloat( t1.xyz );
	float3 c = asfloat( t2.xyz );
	float3 facing = asfloat( t3.xyz );

	uint keep = t0.w;
	uint first = t1.w;
	uint count = t2.w;
	uint bits = t3.w;

	uint4 rules = g_patch[RulesAt];
	uint wanted = rules.x;
	float scaleLeast = asfloat( rules.z );
	float scaleMost = asfloat( rules.w );
	float sink = asfloat( g_patch[RulesAt + 1u].x );
	float3 framePlace = asfloat( g_patch[RulesAt + 2u].xyz );
	float4 frameTurn = asfloat( g_patch[RulesAt + 3u] );
	float3 frameScale = asfloat( g_patch[RulesAt + 4u].xyz );

	float4 bound = asfloat( g_patch[BoundAt] );
	uint4 drawn = g_patch[TemplateAt + 9u];

	//Stood square to the ground: the turn that takes up to where the triangle faces.
	float4 tilt = float4( 0.0, 0.0, 0.0, 1.0 );

	if ( ( rules.y & RuleAlign ) != 0u )
	{
		float3 axis = cross( float3( 0.0, 1.0, 0.0 ), facing );
		float across = length( axis );

		if ( across > 1e-6 )
		{
			float angle = acos( clamp( facing.y, -1.0, 1.0 ) );
			tilt = float4( ( axis / across ) * sin( angle * 0.5 ), cos( angle * 0.5 ) );
		}
	}

	MaskPlace mask = MaskPlaceOfParams();

	//No numbers picture is no mask to read: the corners' paint alone.
	bool masked = wanted != 0u && g_vClutter.x > 0.5;

	for ( uint n = 0u; n < count; n++ )
	{
		float u = Unit( bits );
		float v = Unit( bits );

		if ( u + v > 1.0 ) { u = 1.0 - u; v = 1.0 - v; }

		float w = 1.0 - u - v;
		float chance = Unit( bits );
		float sized = Unit( bits );
		float turned = Unit( bits );

		float3 point = ( a * w ) + ( b * u ) + ( c * v );

		bool kept = n >= 32u || ( ( keep >> n ) & 1u ) != 0u;

		if ( kept && wanted != 0u )
		{
			float grows = 0.0;
			float cover = 0.0;

			if ( masked )
			{
				Painted p = (Painted)0;
				cover = MaskInto( mask, point, facing.y, p );

				[unroll]
				for ( uint m = 8u; m < PaintedMost; m++ )
					if ( p.weight[m] > 0.0 && p.layer[m] < 32u && ( wanted & ( 1u << p.layer[m] ) ) != 0u ) grows += p.weight[m];
			}

			float share = 1.0 - cover;

			grows += ( Grows( t4.x, t4.y, wanted ) * w ) * share;
			grows += ( Grows( t4.z, t4.w, wanted ) * u ) * share;
			grows += ( Grows( t5.x, t5.y, wanted ) * v ) * share;

			kept = chance < grows;
		}

		uint slot = head + first + n;

		if ( !kept )
		{
			Store( slot, uint4( 0u, 0u, 0u, 0u ), 8u );
			Store( slot, uint4( drawn.x, drawn.y, 0u, drawn.w ), 9u );
			continue;
		}

		//Its own turn and size, as TerrainClutter.Work stands one up, then the terrain's - Transform.ToWorld.
		float scale = scaleLeast + ( ( scaleMost - scaleLeast ) * sized );
		float4 turn = float4( 0.0, 0.0, 0.0, 1.0 );

		if ( ( rules.y & RuleYaw ) != 0u )
		{
			float halfway = turned * 3.14159265;
			turn = float4( 0.0, sin( halfway ), 0.0, cos( halfway ) );
		}

		turn = Turn( tilt, turn );

		float3 local = point - float3( 0.0, sink, 0.0 );
		float3 place = mul( local * frameScale, RowsOf( frameTurn ) ) + framePlace;
		float4 whole = Turn( frameTurn, turn );
		float3 sizes = frameScale * scale;

		float3x3 rows = RowsOf( whole );
		float3 centre = place + mul( bound.xyz * sizes, rows );
		float radius = bound.w * max( abs( sizes.x ), max( abs( sizes.y ), abs( sizes.z ) ) );

		uint4 r0 = asuint( float4( rows[0] * sizes.x, 0.0 ) );
		uint4 r1 = asuint( float4( rows[1] * sizes.y, 0.0 ) );
		uint4 r2 = asuint( float4( rows[2] * sizes.z, 0.0 ) );
		uint4 r3 = asuint( float4( place, 1.0 ) );

		Store( slot, r0, 0u );
		Store( slot, r1, 1u );
		Store( slot, r2, 2u );
		Store( slot, r3, 3u );
		Store( slot, r0, 4u );
		Store( slot, r1, 5u );
		Store( slot, r2, 6u );
		Store( slot, r3, 7u );
		Store( slot, asuint( float4( centre, radius ) ), 8u );
		Store( slot, uint4( drawn.x, drawn.y, drawn.z | InstanceDrawnBit, drawn.w ), 9u );

		[unroll]
		for ( uint k = 10u; k < 16u; k++ ) Store( slot, g_patch[TemplateAt + k], k );
	}
}
#endif
