using Rinten.Engine;
using System.Threading;

namespace Rinten;

/// <summary>A generic <see cref="TaskSource"/>.</summary>
public static class GameTask
{

	private static TaskSource source => GlobalContext.Current.TaskSource;

	public static Task Yield() => source.Yield();

	public static Task Delay( int ms ) => source.Delay( ms );

	public static Task Delay( int ms, CancellationToken ct ) => source.Delay( ms, ct );

	public static Task DelaySeconds( float seconds ) => Delay( (int)(seconds * 1000.0f) );

	public static Task DelaySeconds( float seconds, CancellationToken ct ) => Delay( (int)(seconds * 1000.0f), ct );

	public static Task DelayRealtime( int ms ) => source.DelayRealtime( ms );

	public static Task DelayRealtime( int ms, CancellationToken ct ) => source.DelayRealtime( ms, ct );

	public static Task DelayRealtimeSeconds( float seconds ) => DelayRealtime( (int)(seconds * 1000.0f) );

	public static Task DelayRealtimeSeconds( float seconds, CancellationToken ct ) => DelayRealtime( (int)(seconds * 1000.0f), ct );

	public static Task RunInThreadAsync( Action action ) => source.RunInThreadAsync( action );

	public static Task<T> RunInThreadAsync<T>( Func<T> func ) => source.RunInThreadAsync( func );

	public static Task RunInThreadAsync( Func<Task> task ) => source.RunInThreadAsync( task );

	public static Task<T> RunInThreadAsync<T>( Func<Task<T>> task ) => source.RunInThreadAsync( task );

	public static Task CompletedTask => source.CompletedTask;

	public static Task<T> FromResult<T>( T t ) => source.FromResult( t );

	public static Task WhenAll( params Task[] tasks ) => source.WhenAll( tasks );

	public static Task WhenAll( IEnumerable<Task> tasks ) => source.WhenAll( tasks );

	public static Task<T[]> WhenAll<T>( params Task<T>[] tasks ) => source.WhenAll<T>( tasks );

	public static Task<T[]> WhenAll<T>( IEnumerable<Task<T>> tasks ) => source.WhenAll<T>( tasks );

	public static Task<Task> WhenAny( params Task[] tasks ) => source.WhenAny( tasks );

	public static Task<Task> WhenAny( IEnumerable<Task> tasks ) => source.WhenAny( tasks );

	public static Task<Task<T>> WhenAny<T>( params Task<T>[] tasks ) => source.WhenAny<T>( tasks );

	public static Task<Task<T>> WhenAny<T>( IEnumerable<Task<T>> tasks ) => source.WhenAny<T>( tasks );

	public static void WaitAll( params Task[] tasks ) => source.WaitAll( tasks );

	public static void WaitAny( params Task[] tasks ) => source.WaitAny( tasks );

	public static SyncTask MainThread()
	{
		return new SyncTask( SyncContext.MainThread, allowSynchronous: true );
	}

	public static SyncTask MainThread( CancellationToken cancellation )
	{
		return new SyncTask( SyncContext.MainThread, allowSynchronous: true, cancellation: cancellation );
	}

	public static SyncTask WorkerThread()
	{
		TaskSource.EnsureWorkerThreadsStarted();
		return new SyncTask( SyncContext.WorkerThread, allowSynchronous: true );
	}

	public static SyncTask WorkerThread( CancellationToken cancellation )
	{
		TaskSource.EnsureWorkerThreadsStarted();
		return new SyncTask( SyncContext.WorkerThread, allowSynchronous: true, cancellation: cancellation );
	}
}

/// <summary>Provides a way for us to cancel tasks after common async shit is executed.</summary>
public struct TaskSource
{
	private static CancellationTokenSource currentGenerationCts => GlobalContext.Current.CancellationTokenSource;

	internal static CancellationToken Cancellation => currentGenerationCts.Token;

	internal static readonly TaskSource Cancelled = new TaskSource( new CancellationToken( true ) );

	internal TaskSource( int i = 0 )
	{
		_isValid = true;
		_cancellation = Cancellation;

		if ( Cancellation.IsCancellationRequested )
		{
			Log.Warning( $"new TaskSource is already cancelled" );
		}
	}

	internal TaskSource( CancellationToken token )
	{
		if ( token == default )
			token = Cancellation;

		_isValid = true;
		_cancellation = token;
	}

	private static string FormatAction( Delegate action )
	{
		return action.ToSimpleString();
	}

	public static TaskSource Create( CancellationToken token = default )
	{
		return new TaskSource( token );
	}

	/// <summary>Create a token source, which will also be cancelled when sessions end</summary>
	public static CancellationTokenSource CreateLinkedTokenSource()
	{
		return CancellationTokenSource.CreateLinkedTokenSource( Cancellation );
	}

	private readonly CancellationToken _cancellation;
	private bool _isValid;

	public bool IsValid => _isValid && !_cancellation.IsCancellationRequested;

	//Marks this task source as invalid. All associated running tasks will be canceled ASAP.
	internal void Expire()
	{
		_isValid = false;
	}

	internal void CancelIfInvalid()
	{
		if ( !IsValid )
		{
			throw new TaskCanceledException();
		}
	}

	/// <summary>A task that does nothing for given amount of time in milliseconds.</summary>
	public Task Delay( int ms )
	{
		return DelayInternal( ms, _cancellation );
	}

	/// <summary>A task that does nothing for given amount of time in milliseconds.</summary>
	public async Task Delay( int ms, CancellationToken ct )
	{
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource( ct, _cancellation );
		await DelayInternal( ms, linkedCts.Token );
	}

	//Measured actual granularity of Task.Delay on this platform/OS configuration. Anything with more time
	//remaining than this gets a single bulk Task.Delay; the sub-granularity tail is polled per frame.
	internal static readonly int DelayPollingThresholdMs = MeasureTimerGranularityMs();

	private static int MeasureTimerGranularityMs()
	{
		if ( OperatingSystem.IsBrowser() )
			return 16;

		const int Samples = 10;
		var total = 0L;

		for ( var i = 0; i < Samples; i++ )
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			Task.Delay( 1 ).Wait();
			sw.Stop();
			total += sw.ElapsedMilliseconds;
		}

		//Add 1ms to be safe
		return (int)(total / Samples) + 1;
	}

	private async Task DelayInternal( int ms, CancellationToken ct )
	{
		//Capture the calling context so we resume on the same thread (main or worker). Fall
		//back to main thread if called from outside an ExpirableSynchronizationContext.
		var context = SynchronizationContext.Current as Rinten.Tasks.ExpirableSynchronizationContext ?? SyncContext.MainThread;

		var time = Time.NowDouble + ms / 1000.0;

		//For delays longer than the threshold, sleep most of the duration with a single Task.Delay and only poll the remaining sub-threshold window.
		//That sleep is real time: where game time runs faster, the same game time is over sooner
		var scale = ( Game.ActiveScene?.TimeScale ?? 1.0f ) * Scene.HostTimeScale;
		var bulkMs = (int)( ( ms - DelayPollingThresholdMs ) / Math.Max( scale, 1.0f ) );
		if ( bulkMs > 0 )
		{
			await Task.Delay( bulkMs, ct );
			CancelIfInvalid();
		}

		while ( Time.NowDouble < time )
		{
			//Use SyncTask instead of Task.Delay( 1 ) to avoid allocations.
			await new Rinten.Tasks.SyncTask( context, cancellation: ct );
			CancelIfInvalid();
		}

		CancelIfInvalid();
	}

	/// <summary>A task that does nothing for given amount of time in seconds.</summary>
	public Task DelaySeconds( float seconds ) => Delay( (int)(seconds * 1000.0) );

	/// <summary>A task that does nothing for given amount of time in seconds.</summary>
	public Task DelaySeconds( float seconds, CancellationToken ct ) => Delay( (int)(seconds * 1000.0), ct );

	private record struct WorkerThreadAction( Action Action, TaskCompletionSource Tcs )
	{
		public static SendOrPostCallback PostCallback { get; } = state =>
		{
			var item = (WorkerThreadAction)state!;

			try
			{
				item.Action();
				item.Tcs.SetResult();
			}
			catch ( Exception e )
			{
				item.Tcs.SetException( e );
			}
		};

		public override string ToString()
		{
			return FormatAction( Action );
		}
	}

	internal static void EnsureWorkerThreadsStarted()
	{
		if ( !Tasks.WorkerThread.HasStarted ) Tasks.WorkerThread.Start();
	}

	public async Task RunInThreadAsync( Action action )
	{
		var tcs = new TaskCompletionSource();
		SyncContext.WorkerThread.Post( WorkerThreadAction.PostCallback, new WorkerThreadAction( action, tcs ) );

		EnsureWorkerThreadsStarted();

		await tcs.Task;
		CancelIfInvalid();
	}

	private record struct WorkerThreadFunc<T>( Func<T> Func, TaskCompletionSource<T> Tcs )
	{
		public static SendOrPostCallback PostCallback { get; } = state =>
		{
			var item = (WorkerThreadFunc<T>)state!;

			try
			{
				var result = item.Func();
				item.Tcs.SetResult( result );
			}
			catch ( Exception e )
			{
				item.Tcs.SetException( e );
			}
		};

		public override string ToString()
		{
			return FormatAction( Func );
		}
	}

	public async Task<T> RunInThreadAsync<T>( Func<T> func )
	{
		var tcs = new TaskCompletionSource<T>();
		SyncContext.WorkerThread.Post( WorkerThreadFunc<T>.PostCallback, new WorkerThreadFunc<T>( func, tcs ) );

		EnsureWorkerThreadsStarted();

		var result = await tcs.Task;
		CancelIfInvalid();
		return result;
	}

	private record struct WorkerThreadTask( Func<Task> TaskFunc, TaskCompletionSource<Task> Tcs )
	{
		private static Action<Task, object> ContinuationAction { get; } = ( task, state ) =>
		{
			var tcs = (TaskCompletionSource<Task>)state;

			if ( task.IsCanceled )
			{
				tcs.SetCanceled();
			}
			else if ( task.IsFaulted )
			{
				tcs.SetException( task.Exception! );
			}
			else
			{
				Assert.True( task.IsCompletedSuccessfully );
				tcs.SetResult( task );
			}
		};

		public static SendOrPostCallback PostCallback { get; } = state =>
		{
			var item = (WorkerThreadTask)state!;

			try
			{
				var task = item.TaskFunc();
				task.ContinueWith( ContinuationAction, item.Tcs );
			}
			catch ( Exception e )
			{
				item.Tcs.SetException( e );
			}
		};

		public override string ToString()
		{
			return FormatAction( TaskFunc );
		}
	}

	public async Task RunInThreadAsync( Func<Task> task )
	{
		var tcs = new TaskCompletionSource<Task>();
		SyncContext.WorkerThread.Post( WorkerThreadTask.PostCallback, new WorkerThreadTask( task, tcs ) );

		EnsureWorkerThreadsStarted();

		await await tcs.Task;
		CancelIfInvalid();
	}

	private record struct WorkerThreadTask<T>( Func<Task<T>> TaskFunc, TaskCompletionSource<Task<T>> Tcs )
	{
		private static Action<Task<T>, object> ContinuationAction { get; } = ( task, state ) =>
		{
			var tcs = (TaskCompletionSource<Task<T>>)state;

			if ( task.IsCanceled )
			{
				tcs.SetCanceled();
			}
			else if ( task.IsFaulted )
			{
				tcs.SetException( task.Exception! );
			}
			else
			{
				Assert.True( task.IsCompletedSuccessfully );
				tcs.SetResult( task );
			}
		};

		public static SendOrPostCallback PostCallback { get; } = state =>
		{
			var item = (WorkerThreadTask<T>)state!;

			try
			{
				var task = item.TaskFunc();
				task.ContinueWith( ContinuationAction, item.Tcs );
			}
			catch ( Exception e )
			{
				item.Tcs.SetException( e );
			}
		};

		public override string ToString()
		{
			return FormatAction( TaskFunc );
		}
	}

	public async Task<T> RunInThreadAsync<T>( Func<Task<T>> task )
	{
		var tcs = new TaskCompletionSource<Task<T>>();
		SyncContext.WorkerThread.Post( WorkerThreadTask<T>.PostCallback, new WorkerThreadTask<T>( task, tcs ) );

		EnsureWorkerThreadsStarted();

		var result = await await tcs.Task;
		CancelIfInvalid();
		return result;
	}

	public async Task DelayRealtime( int ms )
	{
		await Task.Delay( ms, _cancellation );
		CancelIfInvalid();
	}

	public async Task DelayRealtime( int ms, CancellationToken ct )
	{
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource( ct, _cancellation );
		await Task.Delay( ms, linkedCts.Token );
		CancelIfInvalid();
	}

	public Task DelayRealtimeSeconds( float seconds ) => DelayRealtime( (int)(seconds * 1000.0f) );

	public Task DelayRealtimeSeconds( float seconds, CancellationToken ct ) => DelayRealtime( (int)(seconds * 1000.0f), ct );

	/// <summary>Continues on the main thread.</summary>
	public SyncTask MainThread()
	{
		return GameTask.MainThread( Cancellation );
	}

	/// <summary>Continues on a worker thread.</summary>
	public SyncTask WorkerThread()
	{
		return GameTask.WorkerThread( Cancellation );
	}

	public Task CompletedTask => Task.CompletedTask;

	public Task<T> FromResult<T>( T t ) => Task.FromResult( t );

	public Task FromCanceled( CancellationToken token ) => Task.FromCanceled( token );

	public Task FromException( Exception e ) => Task.FromException( e );

	public async Task WhenAll( params Task[] tasks )
	{
		await Task.WhenAll( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
	}

	public async Task WhenAll( IEnumerable<Task> tasks )
	{
		await Task.WhenAll( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
	}

	public async Task<T[]> WhenAll<T>( params Task<T>[] tasks )
	{
		var result = await Task.WhenAll<T>( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public async Task<T[]> WhenAll<T>( IEnumerable<Task<T>> tasks )
	{
		var result = await Task.WhenAll<T>( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public async Task<Task> WhenAny( params Task[] tasks )
	{
		var result = await Task.WhenAny( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public async Task<Task> WhenAny( IEnumerable<Task> tasks )
	{
		var result = await Task.WhenAny( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public void WaitAny( params Task[] tasks )
	{
		Task.WaitAny( tasks, _cancellation );
	}

	public void WaitAll( params Task[] tasks )
	{
		Task.WaitAll( tasks, _cancellation );
	}

	public async Task<Task<T>> WhenAny<T>( params Task<T>[] tasks )
	{
		var result = await Task.WhenAny<T>( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public async Task<Task<T>> WhenAny<T>( IEnumerable<Task<T>> tasks )
	{
		var result = await Task.WhenAny<T>( tasks ).WaitAsync( _cancellation );
		CancelIfInvalid();
		return result;
	}

	public async Task Yield()
	{
		await Task.Yield();
		CancelIfInvalid();
	}

	/// <summary>Wait until the start of the next frame</summary>
	public async Task Frame()
	{
		await SyncContext.FrameStage.Update.Await();
		CancelIfInvalid();
	}

	/// <summary>Wait until the end of the frame</summary>
	public async Task FrameEnd()
	{
		await SyncContext.FrameStage.PreRender.Await();
		CancelIfInvalid();
	}

	/// <summary>Wait until the next fixed update</summary>
	public async Task FixedUpdate()
	{
		await SyncContext.FrameStage.FixedUpdate.Await();
		CancelIfInvalid();
	}
}
