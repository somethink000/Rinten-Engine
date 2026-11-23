using System;
using System.IO;
using Rinten.Animation;

using CoreAssets = Rinten.CoreAssets;
using Rinten.CoreAnimation;

namespace EngineTests;

//Ported weapon graphs: idle plus an "Any" state holding the transitions; fire flags reset once read
[TestClass]
public class AnimGraphAnyStateTest
{
	private static string TempPath( string extension )
		=> Path.Combine( Path.GetTempPath(), $"rinten-any-{Guid.NewGuid():N}{extension}" );

	private static string Source( string compiled )
		=> Path.ChangeExtension( compiled, ".anim" ).Replace( '\\', '/' );

	//Root, and a spine a metre up whose turn says which clip is playing.
	private static unsafe CoreAnimRuntime.Skeleton BuildSkeleton( string path )
	{
		var builder = new CoreAssets.ModelBuilder();

		try
		{
			var rest = new[] { Vector3.Zero, new Vector3( 0, 0, 1 ) };
			var names = new[] { "root", "spine" };

			for ( var bone = 0; bone < names.Length; bone++ )
			{
				var matrix = new float[]
				{
					1, 0, 0, 0,
					0, 1, 0, 0,
					0, 0, 1, 0,
					-rest[bone].x, -rest[bone].y, -rest[bone].z, 1,
				};

				fixed ( float* m = matrix ) builder.AddBone( names[bone], bone - 1, m );
			}

			Assert.AreEqual( 1, builder.Save( path ) );
		}
		finally
		{
			builder.Dispose();
		}

		var model = CoreAssets.ModelFile.Load( path );
		var skeleton = CoreAnimRuntime.CreateSkeleton( model );

		model.Dispose();

		Assert.IsNotNull( skeleton );

		return skeleton;
	}

	private static unsafe void BuildClip( string path, Rotation turn )
	{
		var builder = new AnimClipBuilder();

		try
		{
			builder.SetFrameRate( 1.0f );

			builder.AddBone( "root", -1 );
			builder.AddBone( "spine", 0 );

			void Track( int bone, Transform pose )
			{
				var trs = AnimTrs.FromTransform( pose );

				builder.SetFrames( bone, new[] { trs, trs } );
			}

			Track( 0, new Transform( Vector3.Zero, Rotation.Identity, 1.0f ) );
			Track( 1, new Transform( new Vector3( 0, 0, 1 ), turn, 1.0f ) );

			Assert.IsTrue( builder.Save( path ) );
		}
		finally
		{
			builder.Dispose();
		}
	}

	private static float SpineAngle( CoreAnimRuntime.Instance instance )
	{
		instance.ModelBone( 1, out var trs );

		return trs.ToTransform().Rotation.Angle();
	}

	[TestMethod]
	public void AnAnyStateTransitionsFromWhereverTheMachineIs()
	{
		if ( !CoreNative.Available )
		{
			Assert.Inconclusive( $"rintencore is not loaded: {CoreNative.BuildInfo}" );
			return;
		}

		var modelPath = TempPath( ".mdl_c" );
		var idlePath = TempPath( ".anim_c" );
		var firePath = TempPath( ".anim_c" );

		var skeleton = BuildSkeleton( modelPath );
		var instance = CoreAnimRuntime.CreateInstance( skeleton );

		BuildClip( idlePath, Rotation.Identity );
		BuildClip( firePath, Rotation.FromAxis( Vector3.Forward, 90.0f ) );

		try
		{
			var builder = AnimGraph.Builder;

			//A trigger: lowered once the update that read it is done
			var shoot = builder.AddControlParameter( "shoot", AnimGraphBuilder.NodeKind.Bool,
				new AnimGraphBuilder.ControlParameterSettings { Describe = true, AutoReset = true } );

			var machine = builder.StateMachine();

			//Idle first, so it is the state the machine starts in - and with no route of its own, the way a ported one has none.
			var idle = machine.AddState( builder.Clip( Source( idlePath ),
				new AnimGraphBuilder.ClipSettings { Looping = true } ) );

			var fire = machine.AddState( builder.Clip( Source( firePath ),
				new AnimGraphBuilder.ClipSettings { Looping = true } ) );

			var any = machine.AddOffState();

			machine.AddTransition( any, fire,
				machine.CreateTransition( fire, new AnimGraphBuilder.TransitionSettings { Duration = 0.0f } ),
				new AnimGraphBuilder.StateMachineBuilder.TransitionRouteSettings { Condition = shoot } );

			var graph = AnimGraph.FromDefinition( builder.Build( machine ), "any" );
			Assert.IsNotNull( graph );

			var running = new AnimGraphInstance( graph, instance, skeleton, null );
			Assert.IsTrue( running.IsValid );

			//Nothing asked for: idle, which holds the spine straight.
			running.Update( 0.1f );
			Assert.AreEqual( 0.0f, SpineAngle( instance ), 1.0f, "it should start idle" );

			//The route to fire hangs off the Any state, and the machine is in idle - which has no
			//routes at all. Read as an ordinary state, this is unreachable and the spine never moves.
			running.SetBool( "shoot", true );
			running.Update( 0.1f );

			Assert.AreEqual( 90.0f, SpineAngle( instance ), 1.0f,
				"an Any state's transitions apply from whatever state the machine is in" );

			//And the flag is down again on its own: the game set it true once and will never set it false.
			Assert.IsFalse( running.Flag( "shoot" ),
				"a flag is lowered once the update that was told about it has read it" );
		}
		finally
		{

			File.Delete( modelPath );
			File.Delete( idlePath );
			File.Delete( firePath );
		}
	}
}
