//A ModelDeformer stack, evaluated in the model's rest space after blend shapes and before bones - see mesh_skin.slang.
//Thirteen float4 a volume, packed by ModelDeformer.Pack; y is up, so squash and stretch work along y

static const uint DeformShapeSphere = 0;
static const uint DeformShapeBox = 1;
static const uint DeformShapeCapsule = 2;
static const uint DeformShapeInfinite = 1000;

static const uint DeformInflate = 0;
static const uint DeformSquashStretch = 1;
static const uint DeformTransform = 2;
static const uint DeformSuck = 3;
static const uint DeformProject = 4;

static const uint DeformNormalAccurate = 0;
static const uint DeformNormalSmooth = 1;
static const uint DeformNormalPreserve = 2;

static const uint DeformVolumeWords = 13;

static const float3x3 DeformIdentity = float3x3( 1, 0, 0, 0, 1, 0, 0, 0, 1 );

struct DeformVolume
{
	float3x4 modelToVolume;
	float3x4 volumeToModel;
	float4 sizeRadius;
	float4 amountFalloff;
	float4 weightOperation;
	float4 centerShape;
	float3x4 operation;
};

DeformVolume LoadDeformVolume( StructuredBuffer<float4> words, uint at )
{
	DeformVolume v;
	v.modelToVolume = float3x4( words[at], words[at + 1], words[at + 2] );
	v.volumeToModel = float3x4( words[at + 3], words[at + 4], words[at + 5] );
	v.sizeRadius = words[at + 6];
	v.amountFalloff = words[at + 7];
	v.weightOperation = words[at + 8];
	v.centerShape = words[at + 9];
	v.operation = float3x4( words[at + 10], words[at + 11], words[at + 12] );
	return v;
}

float3x3 Outer( float3 a, float3 b ) { return float3x3( a.x * b, a.y * b, a.z * b ); }

//Negative inside, with its gradient
float DeformDistance( DeformVolume v, float3 p, out float3 gradient )
{
	float3 size = v.sizeRadius.xyz;
	uint shape = uint( v.centerShape.w );

	if ( shape == DeformShapeInfinite )
	{
		gradient = 0;
		return -1e20;
	}

	if ( shape == DeformShapeSphere || shape == DeformShapeCapsule )
	{
		float3 q = p;
		if ( shape == DeformShapeCapsule ) q -= size * clamp( dot( p, size ) / max( dot( size, size ), 1e-8 ), -1.0, 1.0 );

		float distance = length( q );
		gradient = q / max( distance, 1e-6 );
		return distance - v.sizeRadius.w;
	}

	float3 q = abs( p ) - size;
	float3 outside = max( q, 0.0 );
	float d = length( outside );

	if ( d > 0.0 ) gradient = sign( p ) * outside / d;
	else gradient = q.x >= q.y && q.x >= q.z ? float3( sign( p.x ), 0, 0 ) : ( q.y >= q.z ? float3( 0, sign( p.y ), 0 ) : float3( 0, 0, sign( p.z ) ) );

	return d + min( max( q.x, max( q.y, q.z ) ), 0.0 );
}

//Onto the surface, radially for boxes and spheres and from the nearest axis point for capsules, with its Jacobian
float3 DeformProjectToSurface( DeformVolume v, float3 p, out float3x3 jacobian )
{
	float3 size = v.sizeRadius.xyz;
	uint shape = uint( v.centerShape.w );

	if ( shape == DeformShapeBox )
	{
		float3 relative = abs( p ) / size;
		float extent = max( relative.x, max( relative.y, relative.z ) );

		if ( extent < 1e-6 )
		{
			jacobian = 0;
			return float3( size.x, 0, 0 );
		}

		float3 gradient = relative.x >= relative.y && relative.x >= relative.z ? float3( sign( p.x ) / size.x, 0, 0 ) :
			( relative.y >= relative.z ? float3( 0, sign( p.y ) / size.y, 0 ) : float3( 0, 0, sign( p.z ) / size.z ) );
		jacobian = DeformIdentity / extent - Outer( p, gradient ) / ( extent * extent );
		return p / extent;
	}

	float3 axisPoint = 0;
	float3x3 axisDerivative = 0;
	float lengthSquared = dot( size, size );

	if ( shape == DeformShapeCapsule && lengthSquared > 1e-8 )
	{
		float projection = dot( p, size ) / lengthSquared;
		axisPoint = size * clamp( projection, -1.0, 1.0 );
		if ( abs( projection ) < 1.0 ) axisDerivative = Outer( size, size ) / lengthSquared;
	}

	float3 radial = p - axisPoint;
	float radialLength = length( radial );

	if ( radialLength < 1e-6 )
	{
		float3 direction = float3( 1, 0, 0 );

		if ( shape == DeformShapeCapsule && lengthSquared > 1e-8 )
		{
			float3 axis = size * rsqrt( lengthSquared );
			direction = normalize( cross( axis, abs( axis.y ) < 0.9 ? float3( 0, 1, 0 ) : float3( 1, 0, 0 ) ) );
		}

		jacobian = axisDerivative;
		return axisPoint + direction * v.sizeRadius.w;
	}

	float3 direction = radial / radialLength;
	float3x3 radialDerivative = DeformIdentity - Outer( direction, direction );
	jacobian = axisDerivative + ( v.sizeRadius.w / radialLength ) * mul( radialDerivative, DeformIdentity - axisDerivative );
	return axisPoint + direction * v.sizeRadius.w;
}

//A finite shape keeps what it moves inside itself: an escaping result is clipped, with its derivative
void DeformConstrain( DeformVolume v, float3 p, inout float3 delta, inout float3x3 derivative )
{
	uint shape = uint( v.centerShape.w );
	if ( shape == DeformShapeInfinite ) return;

	float3 candidate = p + delta;
	float3 gradient;
	if ( DeformDistance( v, candidate, gradient ) <= 0.0 ) return;

	float3x3 jacobian;
	float3 constrained;

	if ( shape == DeformShapeBox )
	{
		constrained = clamp( candidate, -v.sizeRadius.xyz, v.sizeRadius.xyz );
		float3 free = float3( abs( candidate.x ) < v.sizeRadius.x, abs( candidate.y ) < v.sizeRadius.y, abs( candidate.z ) < v.sizeRadius.z );
		jacobian = float3x3( free.x, 0, 0, 0, free.y, 0, 0, 0, free.z );
	}
	else
	{
		constrained = DeformProjectToSurface( v, candidate, jacobian );
	}

	derivative = mul( jacobian, DeformIdentity + derivative ) - DeformIdentity;
	delta = constrained - p;
}

//What the volume moves a point by in its own space, and how that changes across it
void DeformEvaluate( DeformVolume v, float3 local, out float3 delta, out float3x3 derivative, out float distance )
{
	delta = 0;
	derivative = 0;

	float3 p = local - v.centerShape.xyz;
	float3 size = v.sizeRadius.xyz;
	uint shape = uint( v.centerShape.w );
	uint operation = uint( v.weightOperation.y );
	float radius = v.sizeRadius.w;

	float3 gradient;
	distance = DeformDistance( v, p, gradient );
	if ( distance >= 0.0 || ( operation == DeformProject && shape == DeformShapeInfinite ) ) return;

	//A full-strength core inside a smooth band, as wide as the shape's falloff share of its size
	float falloffRadius = shape == DeformShapeInfinite ? 0.0 : ( shape == DeformShapeBox ? min( size.x, min( size.y, size.z ) ) : radius );
	float fadeWidth = falloffRadius * v.amountFalloff.w;
	float3 influenceGradient = 0;
	float weight = v.weightOperation.x;
	float influence = weight;

	if ( operation != DeformProject && shape != DeformShapeInfinite && fadeWidth > 0.0 )
	{
		float t = saturate( -distance / max( fadeWidth, 1e-6 ) );
		influence = weight * t * t * ( 3.0 - 2.0 * t );
		influenceGradient = -weight * 6.0 * t * ( 1.0 - t ) / max( fadeWidth, 1e-6 ) * gradient;
	}

	if ( operation == DeformInflate )
	{
		float inflation = v.amountFalloff.y;
		float3 radial = p;
		derivative = DeformIdentity * inflation;

		if ( shape == DeformShapeCapsule )
		{
			float lengthSquared = dot( size, size );
			float projection = dot( p, size ) / max( lengthSquared, 1e-8 );
			radial -= size * clamp( projection, -1.0, 1.0 );
			if ( abs( projection ) < 1.0 && lengthSquared > 1e-8 ) derivative -= mul( derivative, Outer( size, size ) / lengthSquared );
		}

		delta = radial * inflation;

		//Eases towards the room left inside rather than clamping to it, so the shading stays continuous
		if ( shape != DeformShapeInfinite )
		{
			float clearance = -distance;
			float lengthSquared = dot( delta, delta );
			float inverseLength = rsqrt( clearance * clearance + lengthSquared );
			float scale = clearance * inverseLength;
			float3x3 along = Outer( delta, delta ) * ( inverseLength * inverseLength );
			derivative = scale * mul( DeformIdentity - along, derivative )
				- Outer( delta, gradient ) * ( lengthSquared * inverseLength * inverseLength * inverseLength );
			delta *= scale;
		}
	}
	else if ( operation == DeformSquashStretch )
	{
		float stretch = clamp( v.amountFalloff.x, 0.0, 2.0 );
		float width = rsqrt( max( 0.01, stretch ) );
		float3 scaleDelta = float3( width - 1.0, stretch - 1.0, width - 1.0 );
		delta = p * scaleDelta;
		derivative = float3x3( scaleDelta.x, 0, 0, 0, scaleDelta.y, 0, 0, 0, scaleDelta.z );
	}
	else if ( operation == DeformTransform )
	{
		delta = mul( v.operation, float4( p, 1.0 ) ) - p;
		derivative = (float3x3)v.operation - DeformIdentity;
	}
	else if ( operation == DeformSuck )
	{
		delta = -p;
		derivative = -DeformIdentity;
	}
	else if ( operation == DeformProject )
	{
		//No edge fade: at Value 1 everything inside reaches the surface
		delta = DeformProjectToSurface( v, p, derivative ) - p;
		derivative -= DeformIdentity;
	}

	derivative = derivative * influence + Outer( delta, influenceGradient );
	delta *= influence;
	DeformConstrain( v, p, delta, derivative );
}

float3 DeformSample( DeformVolume v, float3 local )
{
	float3 delta;
	float3x3 derivative;
	float distance;
	DeformEvaluate( v, local, delta, derivative, distance );
	return delta;
}

//The frame carried through a Jacobian: the normal by its cofactors, which keeps it oriented through folds
void DeformFrame( float3x3 jacobian, inout float3 normal, inout float4 tangent )
{
	float3x3 cofactor = float3x3( cross( jacobian[1], jacobian[2] ), cross( jacobian[2], jacobian[0] ), cross( jacobian[0], jacobian[1] ) );
	float3 turned = mul( cofactor, normal );
	if ( dot( turned, turned ) <= 1e-8 ) return;

	normal = normalize( turned );
	float3 along = mul( jacobian, tangent.xyz );
	along -= normal * dot( normal, along );

	if ( dot( along, along ) > 1e-8 ) tangent.xyz = normalize( along );
	else tangent.xyz = normalize( cross( abs( normal.y ) < 0.9 ? float3( 0, 1, 0 ) : float3( 1, 0, 0 ), normal ) );
}

//Each volume in turn, in priority order: the next one masks and shapes at where the last one left the point
void DeformApply( StructuredBuffer<float4> words, uint at, uint count, inout float3 position, inout float3 normal, inout float4 tangent )
{
	for ( uint i = 0; i < count; ++i )
	{
		DeformVolume v = LoadDeformVolume( words, at + ( i * DeformVolumeWords ) );
		float3x3 toModel = (float3x3)v.volumeToModel;
		float3 local = mul( v.modelToVolume, float4( position, 1.0 ) );
		uint normalMode = uint( v.weightOperation.z );

		float3 delta;
		float3x3 derivative;
		float distance;
		DeformEvaluate( v, local, delta, derivative, distance );

		if ( normalMode != DeformNormalPreserve )
		{
			float h = v.weightOperation.w;

			//Smooth reads the change across a wider neighbourhood than the analytic derivative
			if ( normalMode == DeformNormalSmooth && distance < h )
			{
				float3 dx = DeformSample( v, local + float3( h, 0, 0 ) ) - DeformSample( v, local - float3( h, 0, 0 ) );
				float3 dy = DeformSample( v, local + float3( 0, h, 0 ) ) - DeformSample( v, local - float3( 0, h, 0 ) );
				float3 dz = DeformSample( v, local + float3( 0, 0, h ) ) - DeformSample( v, local - float3( 0, 0, h ) );
				derivative = transpose( float3x3( dx, dy, dz ) ) / ( 2.0 * h );
			}

			DeformFrame( DeformIdentity + mul( toModel, mul( derivative, (float3x3)v.modelToVolume ) ), normal, tangent );
		}

		position += mul( toModel, delta );
	}
}

//Moves a vertex as the stack moves the anchors of the bones it is weighted to, so it follows without being reshaped
float3 DeformRigid( StructuredBuffer<float4> words, uint at, uint count, uint anchors, uint bones, uint4 links, float4 weights,
	float3 position )
{
	float3 anchor = 0;
	float total = 0;

	//A slot with no weight often names bone 255
	for ( uint i = 0; i < 4; ++i )
	{
		if ( weights[i] <= 0.0 ) continue;
		anchor += weights[i] * words[anchors + min( links[i], bones - 1 )].xyz;
		total += weights[i];
	}

	if ( total <= 0.0 ) return position;
	anchor /= total;

	float3 moved = anchor;
	float3 normal = float3( 0, 1, 0 );
	float4 tangent = float4( 1, 0, 0, 1 );
	DeformApply( words, at, count, moved, normal, tangent );

	return position + moved - anchor;
}
