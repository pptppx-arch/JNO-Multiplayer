namespace Assets.Scripts.Threading
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading.Tasks;

    /// <summary>
    /// Bridges TCP/UDP continuations to the Juno/Unity game thread.
    /// Only MultiplayerTelemetryRuntime.Update() may call Pump().
    /// </summary>
    public static class MultiplayerThread
    {
        private interface IWorkItem
        {
            void Execute();
        }

        private sealed class ActionWorkItem : IWorkItem
        {
            private readonly Action _action;

            public ActionWorkItem(Action action)
            {
                _action = action;
            }

            public void Execute()
            {
                _action();
            }
        }

        private sealed class FuncWorkItem<T> : IWorkItem
        {
            private readonly Func<T> _function;
            private readonly TaskCompletionSource<T> _completion;

            public Task<T> Task => _completion.Task;

            public FuncWorkItem(Func<T> function)
            {
                _function = function;
                _completion = new TaskCompletionSource<T>();
            }

            public void Execute()
            {
                try
                {
                    _completion.TrySetResult(_function());
                }
                catch (Exception ex)
                {
                    _completion.TrySetException(ex);
                }
            }
        }

        private static readonly ConcurrentQueue<IWorkItem> _pending =
            new ConcurrentQueue<IWorkItem>();

        /// <summary>
        /// Queues fire-and-forget work that must run on the game thread.
        /// </summary>
        public static void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _pending.Enqueue(new ActionWorkItem(action));
        }

        /// <summary>
        /// Queues game-thread work and returns a task completed when Pump executes it.
        /// </summary>
        public static Task<T> Enqueue<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));

            var item = new FuncWorkItem<T>(function);
            _pending.Enqueue(item);
            return item.Task;
        }

        /// <summary>
        /// Executes a bounded amount of queued work. Call only from the Juno/Unity game thread.
        /// </summary>
        public static int Pump(int maximumItemsPerFrame = 32)
        {
            if (maximumItemsPerFrame <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumItemsPerFrame));
            }

            int processed = 0;
            while (processed < maximumItemsPerFrame
                && _pending.TryDequeue(out IWorkItem item))
            {
                try
                {
                    item.Execute();
                }
                catch (Exception ex)
                {
                    // Exceptions from Enqueue(Func<T>) are returned through that task.
                    // This branch handles Post(Action) work.
                    Mod.LogError($"[MultiplayerThread] Queued game work failed: {ex.Message}");
                }

                processed++;
            }

            return processed;
        }
    }
}
