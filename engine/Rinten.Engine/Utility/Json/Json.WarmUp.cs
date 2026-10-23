using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Rinten.Internal;

namespace Rinten;

partial class Json
{
	internal static CancellationTokenSource WarmUpCts;

	//Try to do any reflection / code gen immediately, so we don't do anything too slow during gameplay.
	internal static void PopulateReflectionCache( TypeLibrary typeLibrary )
	{
		WarmUpCts?.Cancel();
		WarmUpCts = new CancellationTokenSource();
		var token = WarmUpCts.Token;

		var jsonOptions = options;

		var sw = Stopwatch.StartNew();

		//Force the JsonSerializerOptions to become immutable JsonTypeInfos get cached

		_ = JsonSerializer.Deserialize<string>( "null", jsonOptions );

		if ( OperatingSystem.IsBrowser() )
			return;

		//Try to create as many JsonTypeInfos as possible This can be a background task, its expensive and we dont need to be populated immediately
		_ = Task.Run( () =>
		{
			//What each assembly's types are checked against - see DescribingReaches.
			var reaches = new Dictionary<Assembly, JitReach>();

			try
			{
				WarmUp( typeLibrary, jsonOptions, reaches, token );
			}
			finally
			{
				foreach ( var reach in reaches.Values )
					reach?.Dispose();
			}
		}, token );

		Log.Trace( $"Took {sw.Elapsed.TotalMilliseconds:F2}ms populating reflection cache" );
	}

	private static void WarmUp( TypeLibrary typeLibrary, JsonSerializerOptions jsonOptions, Dictionary<Assembly, JitReach> reaches, CancellationToken token )
	{
		foreach ( var typeDesc in typeLibrary.GetTypes() )
		{
			if ( token.IsCancellationRequested ) return;

			//ref structs & ptrs are invalid for serialization
			if ( typeDesc.TargetType.IsByRef || typeDesc.TargetType.IsPointer || typeDesc.TargetType.IsByRefLike )
				continue;

			//as are generics (with no target type)
			if ( typeDesc.TargetType.IsGenericType )
				continue;

			//State machines and closures are never serialized
			if ( CompilerMade( typeDesc.TargetType ) )
				continue;

			//Nor the one to load an assembly. A type whose description names one that nothing has loaded yet is described on the day
			//something serializes it: Project, whose compilers are its own properties, had a warm-up load the compiler into every game.
			if ( DescribingReaches( typeDesc.TargetType, reaches ) )
				continue;

			try
			{
				_ = jsonOptions.GetTypeInfo( typeDesc.TargetType );
			}
			catch
			{
				// Ignore not supported
			}
		}
	}

	//Whether describing this type - its own members and those of every type it derives
	//from - would load an assembly nothing has loaded. See JitReach.DescribingReaches.
	private static bool DescribingReaches( Type type, Dictionary<Assembly, JitReach> reaches )
	{
		for ( var each = type; each is not null && each != typeof( object ); each = each.BaseType )
		{
			if ( !reaches.TryGetValue( each.Assembly, out var reach ) )
				reaches[each.Assembly] = reach = JitReach.For( each.Assembly );

			try
			{
				if ( reach?.DescribingReaches( each ) == true ) return true;
			}
			catch ( Exception e ) when ( e is BadImageFormatException or ArgumentException or InvalidOperationException )
			{
				//Metadata that will not say: described as it always was.
			}
		}

		return false;
	}
}

