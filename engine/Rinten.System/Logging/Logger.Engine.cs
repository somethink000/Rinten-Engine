using NLog;

namespace Rinten.Diagnostics;

//The engine's log: NLog's targets, the console's filters and listeners
public partial class Logger
{
	private NLog.Logger _log;

	partial void Created()
	{
		Logging.InitializeConfig();
		_log = NLog.LogManager.GetLogger( Name );
		Logging.RegisterLogger( Name );
	}

	partial void Write( LogLevel level, Exception exception, FormattableString message, string name )
	{
		var nlogLevel = level switch
		{
			LogLevel.Trace => NLog.LogLevel.Trace,
			LogLevel.Warn => NLog.LogLevel.Warn,
			LogLevel.Error => NLog.LogLevel.Error,
			_ => NLog.LogLevel.Info,
		};

		WriteToTargets( nlogLevel, exception, message, name );
	}

	internal void WriteToTargets( NLog.LogLevel nlogLevel, Exception ex, FormattableString message, string name = null )
	{
		name ??= Name;

		LogLevel level = nlogLevel.Ordinal switch
		{
			0 => LogLevel.Trace,
			1 => LogLevel.Trace,
			2 => LogLevel.Info,
			3 => LogLevel.Warn,
			4 => LogLevel.Error,
			5 => LogLevel.Error,
			_ => LogLevel.Info,
		};

		if ( !Logging.ShouldLog( name, level ) )
			return;

		var defaultMessage = message.ToString();
		var arguments = new List<object>();

		var htmlMessage = (string)WrapObject( message, arguments );

		//Only show the first line in the console, but inspecting that line will show the rest

		var firstLineBreakIndex = htmlMessage.IndexOf( "\n", StringComparison.Ordinal );
		if ( firstLineBreakIndex > -1 )
		{
			htmlMessage = htmlMessage.Substring( 0, firstLineBreakIndex ).TrimEnd();
		}

		var logEvent = LogEventInfo.Create( nlogLevel, name, defaultMessage );

		if ( ex != null )
		{
			logEvent.Exception = ex;
		}
		else
		{
			var stackTrace = new StackTrace( 0, true );
			logEvent.SetStackTrace( stackTrace, 0 );
		}

		_log.Log( logEvent );

		string stacktrace = null;

		if ( logEvent.Exception != null )
		{
			stacktrace = GameLog.WriteExceptionDetails( logEvent.Exception );
		}

		if ( stacktrace == null && logEvent.StackTrace != null )
		{
			stacktrace = logEvent.StackTrace.ToString();
		}

		var e = new LogEvent
		{
			Level = level,
			Logger = logEvent.LoggerName,
			Message = defaultMessage,
			Exception = ex,
			HtmlMessage = htmlMessage,
			Stack = stacktrace,
			Time = DateTime.Now,
			Arguments = arguments.ToArray()
		};

		Logging.Write( e );
	}
}
