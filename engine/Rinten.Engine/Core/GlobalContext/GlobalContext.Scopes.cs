namespace Rinten.Engine;

internal partial class GlobalContext
{
	/// <summary>Should rarely have to get called, game scope is implicit. Will need to be called if we're in the menu scope, and have to call something in the game scope.</summary>
	public static IDisposable GameScope() => new GlobalContextScope( Game, clearAsyncContext: true );

	/// <summary>Should only be called at a really high level, when doing menu stuff</summary>
	public static IDisposable MenuScope() => new GlobalContextScope( Menu );

	/// <summary>Makes this the current context until disposed.</summary>
	public override IDisposable Enter() => new GlobalContextScope( this );

	/// <summary>Whether this is the menu's context.</summary>
	public override bool IsMenu => ReferenceEquals( this, Menu );


	public struct GlobalContextScope : IDisposable
	{
		GlobalContext previous;
		public GlobalContextScope( GlobalContext context, bool clearAsyncContext = false )
		{
			previous = Current;

			if ( clearAsyncContext )
			{
				_current.Value = context;
			}
			else
			{
				Current = context;
			}
		}

		public void Dispose()
		{
			Current = previous;
		}
	}
}
