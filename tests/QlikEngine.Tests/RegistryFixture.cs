using System;
using System.Threading;
using System.Threading.Tasks;
using Qlik.Engine.PowerShell;

namespace Qlik.Engine.Tests
{
    // These objects deliberately compare equal. Registry ownership must use
    // reference identity, not application IDs or a provider's Equals method.
    public sealed class FixtureApp
    {
        public object Session = new object();
        public bool Closed;
        public override bool Equals(object value) { return value is FixtureApp; }
        public override int GetHashCode() { return 1; }
    }

    public sealed class FixtureResource : IDisposable
    {
        public int Disposals;
        public void Dispose() { Interlocked.Increment(ref Disposals); }
    }

    public sealed class FixtureProvider : IEngineProvider
    {
        private readonly SemaphoreSlim permits = new SemaphoreSlim(0);
        private int active;
        public bool HoldReads;
        public int MaximumActive;
        public int Disposals;
        public int DestroyedChildren;
        public int Active { get { return Volatile.Read(ref active); } }
        public object GetSession(object app) { return ((FixtureApp)app).Session; }
        public bool IsClosed(object app, object session) { return ((FixtureApp)app).Closed; }
        public void ReleaseRead() { permits.Release(); }
        public async Task<EngineReadResult> ReadAsync(object app, EngineReadRequest request)
        {
            int count = Interlocked.Increment(ref active);
            MaximumActive = Math.Max(MaximumActive, count);
            try
            {
                if (HoldReads) await permits.WaitAsync().ConfigureAwait(false);
                object child = request.AsSdkObject ? new FixtureResource() : null;
                return new EngineReadResult { Value = child ?? "fixture", NativeChild = child };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task DestroyChildAsync(object app, object child)
        {
            Interlocked.Increment(ref DestroyedChildren);
            ((IDisposable)child).Dispose();
            return Task.FromResult(true);
        }
        public void DisposeApp(object app, object session)
        {
            ((FixtureApp)app).Closed = true;
            Interlocked.Increment(ref Disposals);
        }
    }
}
