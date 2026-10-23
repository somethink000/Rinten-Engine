using System.Numerics;

namespace Rinten.CoreAnimation;

//Jiggle bones: damped world-space springs on the finished pose, stepped at a fixed rate
internal static class CoreJiggle
{
	//One jiggle bone, with its bone found in the skeleton.
	internal sealed class Bound
	{
		public int Bone = -1;

		/// <summary>Where the tip rests, in the bone's own space.</summary>
		public Vector3 Tip;

		/// <summary>How long the swinging part is in the model's space, or nought to measure the tip.</summary>
		public float Length;

		/// <summary>Swings a second, as angular frequency.</summary>
		public float Omega;

		public float Damping;
		public float Gravity;
		public float WorldMotion;

		/// <summary>In radians.</summary>
		public float MaxAngle;

		public float Weight;
		public float Radius;
	}

	/// <summary>A sphere on a bone, in metres, in the bone's own frame without its scale.</summary>
	internal sealed class Collider
	{
		public int Bone = -1;
		public Vector3 Offset;
		public float Radius;
	}

	//A model's jiggle bones, parents first, and what they are kept out of.
	internal sealed class Set
	{
		public static readonly Set Empty = new();

		public Bound[] Bones = System.Array.Empty<Bound>();
		public Collider[] Colliders = System.Array.Empty<Collider>();

		public bool IsEmpty => Bones.Length == 0;
	}

	//Where one posed thing's tips are and how fast they are going - its own, because two characters of the same model swing differently.
	internal sealed class State
	{
		/// <summary>The set these were made for; another set starts them again.</summary>
		public Set For;

		public Vector3[] Tip = System.Array.Empty<Vector3>();
		public Vector3[] Velocity = System.Array.Empty<Vector3>();
		public Vector3[] Target = System.Array.Empty<Vector3>();

		public Transform LastWorld;
		public bool Live;

		public void Reset( Set set )
		{
			For = set;
			Tip = new Vector3[set.Bones.Length];
			Velocity = new Vector3[set.Bones.Length];
			Target = new Vector3[set.Bones.Length];
			Live = false;
		}
	}

	//The step the springs are worked out at.
	private const float Step = 1.0f / 120.0f;

	//The most simulated time a frame is given. A longer frame is a stall or a jump, and catching up on it would only fling everything.
	private const int MostSteps = 8;

	//A frame longer than this starts the springs again rather than stepping them.
	private const float Stall = 0.25f;

	//Further than this in one frame is somewhere else, not somewhere moved to.
	private const float Teleport = 2.0f;

	private const float Gravity = 9.81f;

	/// <summary>Swing a pose's jiggle bones, and put the model-space pose right after.</summary>
	public static void Apply( CoreAnimRuntime.Instance instance, Set set, State state, in Transform world, float delta )
	{
		if ( instance is null || set is null || set.IsEmpty || state is null ) return;

		var local = instance.Local;
		var skeleton = instance.Skeleton;

		if ( state.For != set ) state.Reset( set );

		var restart = !state.Live || delta > Stall || delta < 0.0f;

		//Simulated at most this much: see MostSteps.
		var simulated = MathF.Min( MathF.Max( delta, 0.0f ), Step * MostSteps );
		var steps = simulated > 0.0f ? System.Math.Clamp( (int)MathF.Ceiling( simulated / Step ), 1, MostSteps ) : 0;
		var h = steps > 0 ? simulated / steps : 0.0f;

		var worldTurn = world.Rotation;
		var lastTurn = state.LastWorld.Rotation;

		for ( var i = 0; i < set.Bones.Length; i++ )
		{
			var jiggle = set.Bones[i];
			var bone = jiggle.Bone;

			if ( bone < 0 || bone >= local.Length ) continue;

			//Physics or a game is holding it, and has the last word.
			if ( instance.OverrideCount > 0 && instance.Overridden[bone] ) continue;

			var parent = skeleton.Bones[bone].Parent;

			//Walked from the local pose rather than read off the model pose: a jiggle bone above this one has just been turned.
			var above = parent >= 0 ? CoreAnimRuntime.ModelOf( local, skeleton, parent ) : AnimTrs.Identity;
			var mine = AnimTrs.Then( local[bone], above );

			var tipAt = AnimTrs.Identity;
			tipAt.Position = jiggle.Tip;

			var along = AnimTrs.Then( tipAt, mine ).Position - mine.Position;

			if ( jiggle.Length > 0.0f )
				along = AnimTrs.NormaliseVec( along ) * jiggle.Length;

			if ( along.Length < 1e-6f ) continue;

			var root = world.PointToWorld( mine.Position );
			var target = world.PointToWorld( mine.Position + along );
			var reach = ( target - root ).Length;

			if ( reach < 1e-6f ) continue;

			var tip = state.Tip[i];
			var velocity = state.Velocity[i];
			var lastTarget = state.Target[i];

			if ( restart || ( target - lastTarget ).Length > Teleport )
			{
				tip = target;
				velocity = Vector3.Zero;
				lastTarget = target;
			}
			else if ( steps > 0 )
			{
				//Carried along with the body by as much as it is told not to lag: where the tip would be had it been nailed to the model.
				if ( jiggle.WorldMotion < 1.0f )
				{
					var carried = world.PointToWorld( state.LastWorld.PointToLocal( tip ) );
					var turned = worldTurn * ( lastTurn.Inverse * velocity );
					var lag = System.Math.Clamp( jiggle.WorldMotion, 0.0f, 1.0f );

					tip = Vector3.Lerp( carried, tip, lag );
					velocity = Vector3.Lerp( turned, velocity, lag );
				}

				//The spring pulls toward where the pose puts the tip, and is damped against how fast that is moving rather than against
				//standing still - otherwise a body walking at a steady pace would drag its tips behind it like something through water.
				var moving = ( target - lastTarget ) / simulated;
				var k = jiggle.Omega * jiggle.Omega;
				var c = 2.0f * jiggle.Damping * jiggle.Omega;
				var pull = Vector3.Down * ( Gravity * jiggle.Gravity );

				for ( var s = 0; s < steps; s++ )
				{
					var goal = Vector3.Lerp( lastTarget, target, (float)( s + 1 ) / steps );
					var push = ( goal - tip ) * k - ( velocity - moving ) * c + pull;

					velocity += push * h;
					tip += velocity * h;
				}
			}

			//Where the pose points it, and where it is allowed to go from there.
			var rest = AnimTrs.NormaliseVec( target - root );
			var pointing = AnimTrs.NormaliseVec( tip - root );

			pointing = Within( rest, pointing, jiggle.MaxAngle );
			pointing = Outside( instance, set, world, root, pointing, reach, jiggle.Radius );

			tip = root + pointing * reach;

			//Nothing goes along the bone: it does not stretch.
			velocity -= pointing * Vector3.Dot( velocity, pointing );

			state.Tip[i] = tip;
			state.Velocity[i] = velocity;
			state.Target[i] = target;

			//Turned in the model's space from where the pose points it to where the tip is.
			var from = along;
			var to = worldTurn.Inverse * pointing;

			var turn = AnimTrs.Between( from, to );

			if ( jiggle.Weight < 1.0f )
				turn = Quaternion.Slerp( Quaternion.Identity, turn, System.Math.Clamp( jiggle.Weight, 0.0f, 1.0f ) );

			var swung = AnimTrs.NormaliseQuat( turn * AnimTrs.NormaliseQuat( mine.Rotation ) );

			local[bone].Rotation = AnimTrs.NormaliseQuat(
				Quaternion.Inverse( AnimTrs.NormaliseQuat( above.Rotation ) ) * swung );
		}

		state.LastWorld = world;
		state.Live = true;

		Recompose( instance );
	}

	//A direction held inside a cone round another.
	private static Vector3 Within( Vector3 rest, Vector3 pointing, float limit )
	{
		if ( limit >= MathF.PI ) return pointing;

		var cosine = System.Math.Clamp( Vector3.Dot( rest, pointing ), -1.0f, 1.0f );
		var angle = MathF.Acos( cosine );

		if ( angle <= limit ) return pointing;

		//Turned back toward where it rests until it is on the edge of the cone.
		var axis = Vector3.Cross( rest, pointing );

		if ( axis.Length < 1e-6f ) axis = AnimTrs.Perpendicular( rest );

		var back = AnimTrs.FromAxisAngle( axis, limit );

		return AnimTrs.NormaliseVec( (Rotation)back * rest );
	}

	//A direction turned until a tip that far along it is out of every collider.
	private static Vector3 Outside( CoreAnimRuntime.Instance instance, Set set, in Transform world,
		Vector3 root, Vector3 pointing, float reach, float radius )
	{
		if ( set.Colliders.Length == 0 ) return pointing;

		var local = instance.Local;
		var skeleton = instance.Skeleton;
		var scale = world.Scale.x;

		foreach ( var collider in set.Colliders )
		{
			if ( collider.Bone < 0 || collider.Bone >= local.Length ) continue;

			//Offset in the bone's own turn and without its scale, in metres - the same as the editor places it.
			var on = CoreAnimRuntime.ModelOf( local, skeleton, collider.Bone );
			var centre = world.PointToWorld( on.Position + (Rotation)on.Rotation * collider.Offset );

			var tip = root + pointing * reach;
			var away = tip - centre;
			var clear = ( collider.Radius + radius ) * scale;

			if ( away.Length >= clear ) continue;

			var pushed = centre + AnimTrs.NormaliseVec( away ) * clear;

			pointing = AnimTrs.NormaliseVec( pushed - root );
		}

		return pointing;
	}

	//The model-space pose from the local one, parents first. Not CoreAnimRuntime.ComposeNow: that moves this frame's pose into last
	//frame's first, and this frame's pose is already there - doing it twice would tell whatever reads the difference that nothing moved.
	private static void Recompose( CoreAnimRuntime.Instance instance )
	{
		var skeleton = instance.Skeleton;
		++instance.Stamp;

		foreach ( var bone in skeleton.Order )
		{
			var parent = skeleton.Bones[bone].Parent;

			instance.Model[bone] = parent >= 0
				? AnimTrs.Then( instance.Local[bone], instance.Model[parent] )
				: instance.Local[bone];
		}
	}
}
