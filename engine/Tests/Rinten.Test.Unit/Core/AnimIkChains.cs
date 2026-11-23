using System;
using Rinten.CoreAnimation;

namespace UnitTests.Core;

//IK chains name their three bones and bend direction instead of walking up from the end bone
[TestClass]
public class AnimIkChainTests
{
	//Root, thigh, a twist bone, calf, foot - a leg two units long with a joint half way. The twist bone sits between
	//thigh and calf on purpose: it is the shape that used to be solved wrongly, and the one a chain exists to name past.
	static (CoreAnimRuntime.Skeleton Skeleton, CoreAnimRuntime.Instance Instance) Leg()
	{
		var places = new[]
		{
			Vector3.Zero,                    //root
			Vector3.Zero,                    //thigh
			new Vector3( 0, 0, -0.5f ),      //thigh twist
			new Vector3( 0, 0, -0.5f ),      //calf
			new Vector3( 0, 0, -1.0f ),      //foot
		};

		var names = new[] { "root", "thigh", "thigh_twist", "calf", "foot" };
		var parents = new[] { -1, 0, 1, 2, 3 };

		var skeleton = new CoreAnimRuntime.Skeleton
		{
			Bones = new CoreAnimRuntime.Skeleton.BoneEntry[names.Length],
			Order = new int[names.Length],
		};

		for ( var bone = 0; bone < names.Length; bone++ )
		{
			skeleton.Bones[bone] = new CoreAnimRuntime.Skeleton.BoneEntry
			{
				Name = names[bone],
				Key = names[bone],
				Parent = parents[bone],
				Rest = AnimTrs.Identity with { Position = places[bone] },
			};

			skeleton.Order[bone] = bone;
			skeleton.ByName[names[bone]] = bone;
		}

		var pose = new AnimTrs[names.Length];
		for ( var bone = 0; bone < names.Length; bone++ ) pose[bone] = skeleton.Bones[bone].Rest;

		var instance = new CoreAnimRuntime.Instance { Skeleton = skeleton, Local = pose };

		instance.Poses.Add( pose );
		instance.PoseUsed.Add( true );

		return (skeleton, instance);
	}

	static Vector3 Where( CoreAnimRuntime.Instance instance, int bone )
		=> CoreAnimRuntime.ModelOf( instance.Pose( 0 ), instance.Skeleton, bone ).Position;

	static AnimTrs Target( Vector3 position )
		=> AnimTrs.Identity with { Position = position };

	//The joints a chain names are the ones that turn, whatever is parented between them.
	[TestMethod]
	public void AChainTurnsTheJointsItNames()
	{
		var (_, instance) = Leg();

		var twistWas = instance.Pose( 0 )[2];

		Assert.IsTrue( CoreAnimRuntime.PoseTwoBoneIk( instance, 0, 1, 3, 4,
			Target( new Vector3( 1.0f, 0.0f, -1.0f ) ), 1.0f, 0.0f, false, default ) );

		var foot = Where( instance, 4 );

		Assert.AreEqual( 1.0f, foot.x, 0.01f, "the foot should land on the target" );
		Assert.AreEqual( -1.0f, foot.z, 0.01f, "the foot should land on the target" );

		//The twist bone is not one of the three, so it is left exactly as it was and carried along by the thigh above it.
		var twist = instance.Pose( 0 )[2];

		Assert.AreEqual( twistWas.Position.z, twist.Position.z, 0.0001f, "a bone the chain does not name keeps its pose" );
		Assert.AreEqual( 1.0f, twist.Rotation.W, 0.0001f, "a bone the chain does not name keeps its pose" );

		//And it has come along rather than stayed behind: it sits between the thigh and the calf wherever the two of them ended up.
		var thigh = Where( instance, 1 );
		var calf = Where( instance, 3 );
		var carried = Where( instance, 2 );

		Assert.AreEqual( ( thigh - calf ).Length, ( thigh - carried ).Length * 2.0f, 0.01f,
			"the twist bone should still sit half way down the thigh" );
	}

	//Walking the limb up from the end bone finds the twist bone, which is why naming the joints is worth doing.
	[TestMethod]
	public void WalkingUpFromTheEndTurnsWhateverIsThere()
	{
		var (_, instance) = Leg();

		//Foot, calf, twist - the thigh never moves, because as far as the walk is concerned the limb starts at the twist bone.
		Assert.IsTrue( CoreAnimRuntime.PoseTwoBoneIk( instance, 0, 4,
			Target( new Vector3( 0.5f, 0.0f, -1.0f ) ), 1.0f, 0.0f ) );

		Assert.AreEqual( 1.0f, instance.Pose( 0 )[1].Rotation.W, 0.0001f, "the thigh is above the limb that was walked" );
		Assert.AreNotEqual( 1.0f, instance.Pose( 0 )[2].Rotation.W, 0.0001f, "the twist bone was turned instead" );
	}

	//Which way the knee goes is the chain's answer, not the pose's.
	[TestMethod]
	public void TheKneeDirectionDecidesWhichWayTheLimbBends()
	{
		foreach ( var side in new[] { 1.0f, -1.0f } )
		{
			var (_, instance) = Leg();

			//Straight down and short, so the limb has to fold and the fold has somewhere to go.
			Assert.IsTrue( CoreAnimRuntime.PoseTwoBoneIk( instance, 0, 1, 3, 4,
				Target( new Vector3( 0.0f, 0.0f, -1.5f ) ), 1.0f, 0.0f,
				true, new Vector3( side, 0.0f, 0.0f ) ) );

			var knee = Where( instance, 3 );

			Assert.IsTrue( knee.x * side > 0.2f,
				$"the knee should be pushed to the side the chain asked for - got {knee}" );
		}
	}

	//A target past the end of the limb straightens it rather than stretching it, and the named joints are still the ones doing the straightening.
	[TestMethod]
	public void AnUnreachableTargetStraightensTheChain()
	{
		var (_, instance) = Leg();

		Assert.IsTrue( CoreAnimRuntime.PoseTwoBoneIk( instance, 0, 1, 3, 4,
			Target( new Vector3( 8.0f, 0.0f, 0.0f ) ), 1.0f, 0.0f, false, default ) );

		var thigh = Where( instance, 1 );
		var foot = Where( instance, 4 );

		Assert.AreEqual( 2.0f, ( foot - thigh ).Length, 0.02f, "the limb should not stretch" );
		Assert.IsTrue( foot.x > 1.9f, $"it should point at what it cannot reach - got {foot}" );
	}

	//An IK blended all the way out leaves the pose exactly as it found it.
	[TestMethod]
	public void AWeightOfNothingSolvesNothing()
	{
		var (_, instance) = Leg();

		Assert.IsFalse( CoreAnimRuntime.PoseTwoBoneIk( instance, 0, 1, 3, 4,
			Target( new Vector3( 1.0f, 0.0f, -1.0f ) ), 0.0f, 0.0f, false, default ) );

		Assert.AreEqual( -2.0f, Where( instance, 4 ).z, 0.0001f, "nothing should have moved" );
	}

	//A chain of more joints than a limb reaches its target too, which is the whole point of not making an author name exactly three bones.
	[TestMethod]
	public void ALongerChainStillReaches()
	{
		var (_, instance) = Leg();

		//Thigh down to foot, every joint in between free to turn.
		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( instance, 0, [1, 2, 3, 4],
			Target( new Vector3( 1.0f, 0.0f, -1.0f ) ), 1.0f, 0.0f, false, default ) );

		var foot = Where( instance, 4 );

		Assert.AreEqual( 1.0f, foot.x, 0.01f, "the foot should land on the target" );
		Assert.AreEqual( -1.0f, foot.z, 0.01f, "the foot should land on the target" );
	}

	//Every segment keeps the length it started with, however many there are.
	[TestMethod]
	public void ALongerChainDoesNotStretch()
	{
		var (_, instance) = Leg();

		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( instance, 0, [1, 2, 3, 4],
			Target( new Vector3( 0.6f, 0.4f, -0.9f ) ), 1.0f, 0.0f, false, default ) );

		Assert.AreEqual( 0.5f, ( Where( instance, 2 ) - Where( instance, 1 ) ).Length, 0.01f, "thigh to twist" );
		Assert.AreEqual( 0.5f, ( Where( instance, 3 ) - Where( instance, 2 ) ).Length, 0.01f, "twist to calf" );
		Assert.AreEqual( 1.0f, ( Where( instance, 4 ) - Where( instance, 3 ) ).Length, 0.01f, "calf to foot" );
	}

	//Out of range, a long chain straightens and points rather than tearing itself apart.
	[TestMethod]
	public void AnUnreachableTargetStraightensALongerChain()
	{
		var (_, instance) = Leg();

		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( instance, 0, [1, 2, 3, 4],
			Target( new Vector3( 9.0f, 0.0f, 0.0f ) ), 1.0f, 0.0f, false, default ) );

		var thigh = Where( instance, 1 );
		var foot = Where( instance, 4 );

		Assert.AreEqual( 2.0f, ( foot - thigh ).Length, 0.02f, "the chain should not stretch" );
		Assert.IsTrue( foot.x > 1.9f, $"it should point at what it cannot reach - got {foot}" );
	}

	//The bend direction decides which side a long chain folds to, the same as a limb.
	[TestMethod]
	public void TheKneeDirectionDecidesWhichWayALongerChainBends()
	{
		var (_, forward) = Leg();
		var (_, back) = Leg();

		var target = Target( new Vector3( 0.0f, 0.0f, -1.0f ) );

		//The knee has to name a side the chain can actually fold to. The chain runs along -Z, the same way the target lies,
		//so a knee pointing along +-Z has no sideways component to give and cannot decide between left and right.
		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( forward, 0, [1, 2, 3, 4], target, 1.0f, 0.0f, true, Vector3.Right ) );
		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( back, 0, [1, 2, 3, 4], target, 1.0f, 0.0f, true, -Vector3.Right ) );

		Assert.IsTrue( Where( forward, 3 ).x > 0.05f, $"forward should fold the joints one way - got {Where( forward, 3 )} (twist {Where( forward, 2 )})" );
		Assert.IsTrue( Where( back, 3 ).x < -0.05f, $"back should fold them the other - got {Where( back, 3 )} (twist {Where( back, 2 )})" );
	}

	//A chain of one segment has nothing to bend, so it aims instead.
	[TestMethod]
	public void ASingleSegmentChainPoints()
	{
		var (_, instance) = Leg();

		Assert.IsTrue( CoreAnimRuntime.PoseIkChain( instance, 0, [3, 4],
			Target( new Vector3( 1.0f, 0.0f, -1.0f ) ), 1.0f, 0.0f, false, default ) );

		var calf = Where( instance, 3 );
		var foot = Where( instance, 4 );

		Assert.AreEqual( 1.0f, ( foot - calf ).Length, 0.01f, "the segment should keep its length" );
		Assert.IsTrue( foot.x > 0.5f, $"the foot should have swung towards the target - got {foot}" );
	}
}
