
namespace Rinten
{
	public enum LogLevel
	{
		Trace,
		Info,
		Warn,
		Error
	}

	public struct LogEvent
	{
		[Title( "Log Level" )]
		[Category( "Meta Data" )]
		[ReadOnly]
		public LogLevel Level { get; set; }

		[Category( "Meta Data" )]
		[ReadOnly]
		public string Logger { get; set; }

		[ReadOnly]
		public string Message { get; set; }

		[ReadOnly]
		[SkipHotload]
		public Exception Exception { get; set; }

		[ReadOnly]
		public string HtmlMessage { get; set; }

		[Title( "Stack Trace" )]
		[ReadOnly]
		[Category( "Stack Trace" )]
		public string Stack { get; set; }

		[Category( "Meta Data" )]
		[ReadOnly]
		public DateTime Time { get; set; }

		[ReadOnly]
		[SkipHotload]
		public object[] Arguments { get; set; }

		[ReadOnly]
		public int Repeats { get; set; }

		[ReadOnly]
		public bool IsDiagnostic { get; set; }

		//A copy for sinks that keep events: reference arguments held weakly, so a stored event keeps nothing alive
		internal readonly LogEvent WithWeakArguments()
		{
			if ( Arguments is not { Length: > 0 } arguments )
				return this;

			var weak = new object[arguments.Length];
			for ( int i = 0; i < arguments.Length; i++ )
			{
				var argument = arguments[i];
				weak[i] = argument is null || argument is string || argument.GetType().IsValueType ? argument : new WeakReference( argument );
			}

			var copy = this;
			copy.Arguments = weak;
			return copy;
		}

		//The argument at index, unwrapped if WithWeakArguments made it weak; null if out of range or collected
		internal readonly object GetArgument( int index )
		{
			if ( Arguments is null || index < 0 || index >= Arguments.Length )
				return null;

			return Arguments[index] is WeakReference weak ? weak.Target : Arguments[index];
		}
	}
}
