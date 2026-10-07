using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>
/// Main-thread helpers. Engine objects (AssetSystem, Model, SceneModel, widgets) may only be touched on the
/// editor main thread - doing so from a pool thread is a native crash, not an exception. Every async flow
/// awaits <see cref="SwitchToMainThread"/> before touching them (pattern from humanoid-retargeter).
/// </summary>
public static class EngineThread
{
	/// <summary>Awaitable hop to the editor main thread (completes synchronously when already there).</summary>
	public static MainThreadAwaitable SwitchToMainThread() => default;

	public readonly struct MainThreadAwaitable : INotifyCompletion
	{
		public MainThreadAwaitable GetAwaiter() => this;
		public bool IsCompleted => ThreadSafe.IsMainThread;
		public void OnCompleted( Action continuation ) => MainThread.Queue( continuation );
		public void GetResult() { }
	}

	/// <summary>Waits on a timer, then resumes on the main thread.</summary>
	public static async Task DelayOnMain( int milliseconds )
	{
		await Task.Delay( milliseconds );
		await SwitchToMainThread();
	}

	/// <summary>An <see cref="IProgress{T}"/> whose callback always runs on the main thread (the editor has no
	/// SynchronizationContext, so <see cref="Progress{T}"/> would call back on the worker).</summary>
	public sealed class MainThreadProgress<T> : IProgress<T>
	{
		readonly Action<T> _report;
		public MainThreadProgress( Action<T> report ) => _report = report;
		public void Report( T value )
		{
			if ( ThreadSafe.IsMainThread ) _report( value );
			else MainThread.Queue( () => _report( value ) );
		}
	}

	public static T Try<T>( Func<T> getter )
	{
		try { return getter(); }
		catch { return default; }
	}
}
