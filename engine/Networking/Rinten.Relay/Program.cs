using Rinten.Relay;

var options = new RelayServer.Options();

for ( var i = 0; i < args.Length; i++ )
{
	var value = i + 1 < args.Length ? args[i + 1] : null;

	switch ( args[i] )
	{
		case "--port" when int.TryParse( value, out var port ):
			options.Port = port;
			i++;
			break;

		case "--max-hosts" when int.TryParse( value, out var most ):
			options.MostHosts = most;
			i++;
			break;

		case "--client-rate" when int.TryParse( value, out var rate ):
			options.ClientRate = rate;
			i++;
			break;

		case "--host-rate" when int.TryParse( value, out var rate ):
			options.HostRate = rate;
			i++;
			break;

		default:
			Console.WriteLine( "rinten-relay [--port 27020] [--max-hosts 2000] [--client-rate bytes/s] [--host-rate bytes/s]" );
			return args[i] is "-h" or "--help" ? 0 : 1;
	}
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += ( _, e ) =>
{
	e.Cancel = true;
	stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += ( _, _ ) => stop.Cancel();

try
{
	new RelayServer( options ).Run( stop.Token );
}
catch ( InvalidOperationException e )
{
	Console.Error.WriteLine( e.Message );
	return 1;
}

return 0;
