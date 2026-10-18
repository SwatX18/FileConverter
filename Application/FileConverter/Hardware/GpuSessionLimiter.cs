// <copyright file="GpuSessionLimiter.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.Hardware
{
    using System;
    using System.Threading;

    /// <summary>
    /// Consumer GPUs limit the number of concurrent encode sessions (NVENC on GeForce: 8 with recent drivers).
    /// Hardware encodes go through this limiter, independently of the global simultaneous-conversion limit.
    /// </summary>
    public static class GpuSessionLimiter
    {
        public const int DefaultMaximumSessions = 4;

        private static readonly object InitializationLock = new object();
        private static SemaphoreSlim semaphore;

        /// <summary>
        /// Sets the maximum number of concurrent GPU sessions. Only effective before the first <see cref="Acquire"/>.
        /// </summary>
        public static void Configure(int maximumSessions)
        {
            lock (InitializationLock)
            {
                if (semaphore == null)
                {
                    int count = maximumSessions > 0 ? maximumSessions : DefaultMaximumSessions;
                    semaphore = new SemaphoreSlim(count, count);
                }
            }
        }

        /// <summary>
        /// Waits for a GPU session slot. Returns null if <paramref name="isCancelled"/> becomes true while waiting.
        /// Dispose the result to release the slot.
        /// </summary>
        public static IDisposable Acquire(Func<bool> isCancelled)
        {
            GpuSessionLimiter.Configure(DefaultMaximumSessions);

            while (!semaphore.Wait(200))
            {
                if (isCancelled != null && isCancelled())
                {
                    return null;
                }
            }

            return new Releaser();
        }

        private sealed class Releaser : IDisposable
        {
            private int released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref this.released, 1) == 0)
                {
                    semaphore.Release();
                }
            }
        }
    }
}
