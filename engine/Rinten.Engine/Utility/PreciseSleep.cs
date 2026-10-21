using System.Diagnostics;
using System.Threading;

namespace Rinten;

//Waits until a stopwatch time: asleep for as much of it as this machine's timer can be trusted with, spinning the sliver after
internal static class PreciseSleep
{
	//Milliseconds spun rather than slept: enough to absorb a sleep that comes back early, too few to matter against a frame
	private const double SpinTailMs = 0.25;

	//What a sleep overshoots on this machine, learnt slowly so one long sleep does not move it far
	internal static double SlackMs { get; private set; } = 1.0;

	internal static void Until( long timestamp )
	{
		while ( true )
		{
			var remaining = ( timestamp - Stopwatch.GetTimestamp() ) * 1000.0 / Stopwatch.Frequency;
			if ( remaining <= 0.0 ) return;

			if ( remaining > SlackMs + SpinTailMs )
			{
				var ask = Math.Max( 1, (int)( remaining - SlackMs - SpinTailMs ) );
				var began = Stopwatch.GetTimestamp();

				Thread.Sleep( ask );

				var overshoot = ( ( Stopwatch.GetTimestamp() - began ) * 1000.0 / Stopwatch.Frequency ) - ask;
				SlackMs = Math.Clamp( SlackMs + ( ( overshoot - SlackMs ) * 0.125 ), 0.05, 4.0 );
				continue;
			}

			//SpinWait rather than an empty loop: it tells the processor what this is, and the runtime can suspend at it
			Thread.SpinWait( 40 );
		}
	}
}
