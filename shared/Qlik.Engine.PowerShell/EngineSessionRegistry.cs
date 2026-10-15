using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Qlik.Engine.PowerShell
{
    public static class EngineSessionRegistry
    {
        private static readonly object Sync = new object();
        private static readonly ConditionalWeakTable<object, Entry> Entries = new ConditionalWeakTable<object, Entry>();
        private static readonly ConditionalWeakTable<object, OwnerState> Owners = new ConditionalWeakTable<object, OwnerState>();
        private static readonly ConditionalWeakTable<object, ResourceBag> Resources = new ConditionalWeakTable<object, ResourceBag>();
        private static readonly HashSet<Entry> Active = new HashSet<Entry>();
        private static readonly HashSet<PendingOpen> Opening = new HashSet<PendingOpen>();
        private static readonly Dictionary<Guid, Runspace> Runspaces = new Dictionary<Guid, Runspace>();
        private static readonly HashSet<Guid> ClosedRunspaces = new HashSet<Guid>();

        private sealed class PendingOpen : IDisposable
        {
            internal object Owner;
            internal Guid Runspace;
            internal Func<Task> Cancel;
            public void Dispose() { lock (Sync) Opening.Remove(this); }
        }

        private sealed class OwnerState { internal bool Closed; }
        private sealed class ResourceBag
        {
            internal readonly List<IDisposable> Items = new List<IDisposable>();
            internal bool Closed;
        }
        private sealed class Entry
        {
            internal object App, Owner, Session;
            internal Guid Runspace;
            internal IEngineProvider Provider;
            internal readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            internal readonly CancellationTokenSource Stopped = new CancellationTokenSource();
            internal readonly List<object> Children = new List<object>();
            internal bool Closed;
            internal Task Closing;
        }

        internal static IDisposable BeginOpen(object owner, Guid runspaceId, Func<Task> cancel)
        {
            lock (Sync)
            {
                TrackRunspace(runspaceId);
                if (ClosedRunspaces.Contains(runspaceId) || Owners.GetValue(owner, ignored => new OwnerState()).Closed)
                    throw new ObjectDisposedException("Engine connection or runspace");
                var pending = new PendingOpen { Owner = owner, Runspace = runspaceId, Cancel = cancel };
                Opening.Add(pending);
                return pending;
            }
        }

        private static void TrackRunspace(Guid id)
        {
            Runspace current = Runspace.DefaultRunspace;
            if (current == null || current.InstanceId != id || Runspaces.ContainsKey(id)) return;
            if (current.RunspaceStateInfo.State == RunspaceState.Closing || current.RunspaceStateInfo.State == RunspaceState.Closed || current.RunspaceStateInfo.State == RunspaceState.Broken)
            {
                ClosedRunspaces.Add(id);
                return;
            }
            Runspaces.Add(id, current);
            current.StateChanged += OnRunspaceStateChanged;
        }

        private static void OnRunspaceStateChanged(object sender, RunspaceStateEventArgs args)
        {
            if (args.RunspaceStateInfo.State != RunspaceState.Closing && args.RunspaceStateInfo.State != RunspaceState.Closed && args.RunspaceStateInfo.State != RunspaceState.Broken) return;
            var runspace = (Runspace)sender;
            Entry[] entries;
            PendingOpen[] opening;
            lock (Sync)
            {
                ClosedRunspaces.Add(runspace.InstanceId);
                entries = Active.Where(entry => entry.Runspace == runspace.InstanceId).ToArray();
                opening = Opening.Where(entry => entry.Runspace == runspace.InstanceId).ToArray();
                Runspaces.Remove(runspace.InstanceId);
                runspace.StateChanged -= OnRunspaceStateChanged;
            }
            // The runspace event cannot report a terminating cmdlet error. Observe
            // eventual cleanup even if an SDK transport takes longer to dispose.
            Task cleanup = Task.WhenAll(entries.Select(CloseEntry).Concat(opening.Select(entry => entry.Cancel())));
            Observe(cleanup);
            try { cleanup.Wait(TimeSpan.FromSeconds(10)); } catch { }
        }

        public static void AttachResource(object app, IDisposable resource)
        {
            if (app == null || resource == null) throw new ArgumentNullException();
            bool reject;
            lock (Sync)
            {
                ResourceBag bag = Resources.GetValue(app, ignored => new ResourceBag());
                reject = bag.Closed;
                if (!reject && !bag.Items.Any(item => ReferenceEquals(item, resource))) bag.Items.Add(resource);
            }
            if (reject) { SafeDispose(resource); throw new ObjectDisposedException("Engine app"); }
        }

        public static void Register(object app, object owner, Guid runspaceId)
        {
            EngineRuntime.EnsureLoaded();
            Register(app, owner, runspaceId, new EngineSdkProvider());
        }

        public static void Register(object app, object owner, Guid runspaceId, IEngineProvider provider)
        {
            if (app == null || owner == null || provider == null) throw new ArgumentNullException();
            if (runspaceId == Guid.Empty) throw new EngineOperationException("An Engine app must belong to a PowerShell runspace.");
            object session = provider.GetSession(app);
            if (session == null || provider.IsClosed(app, session)) throw new EngineOperationException("The Engine app is already closed.");
            lock (Sync)
            {
                TrackRunspace(runspaceId);
                if (ClosedRunspaces.Contains(runspaceId)) throw new ObjectDisposedException("Engine runspace");
                if (Owners.GetValue(owner, ignored => new OwnerState()).Closed) throw new ObjectDisposedException("Engine connection");
                Entry existing;
                if (Entries.TryGetValue(app, out existing))
                {
                    if (existing.Closed || !ReferenceEquals(existing.Owner, owner) || existing.Runspace != runspaceId || !ReferenceEquals(existing.Session, session))
                        throw new EngineOperationException("The Engine app already has a different owner or has been closed.");
                    return;
                }
                if (Active.Any(entry => ReferenceEquals(entry.Session, session)))
                    throw new EngineOperationException("Each opened Engine app must have an isolated session.");
                var created = new Entry { App = app, Owner = owner, Session = session, Runspace = runspaceId, Provider = provider };
                Entries.Add(app, created);
                Active.Add(created);
            }
        }

        public static Task<EngineReadResult> ReadAsync(object app, Guid runspaceId, EngineReadRequest request)
        {
            Entry entry = Require(app, runspaceId, false);
            return ReadCoreAsync(entry, request);
        }

        private static async Task<EngineReadResult> ReadCoreAsync(Entry entry, EngineReadRequest request)
        {
            await entry.Gate.WaitAsync(entry.Stopped.Token).ConfigureAwait(false);
            try
            {
                RequireOpen(entry);
                EngineReadResult result;
                Task<EngineReadResult> reading = entry.Provider.ReadAsync(entry.App, request);
                using (var finished = CancellationTokenSource.CreateLinkedTokenSource(entry.Stopped.Token))
                {
                    Task closingSignal = Task.Delay(Timeout.Infinite, finished.Token);
                    Task completed = await Task.WhenAny(reading, closingSignal).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, reading))
                    {
                        Observe(DisposeLateReadAsync(entry, reading));
                        throw new ObjectDisposedException("Engine app");
                    }
                    finished.Cancel();
                    result = await reading.ConfigureAwait(false);
                }
                bool closed;
                lock (Sync)
                {
                    closed = entry.Closed;
                    if (!closed && result.NativeChild != null) entry.Children.Add(result.NativeChild);
                }
                if (closed)
                {
                    if (result.NativeChild != null) await SafeDestroyAsync(entry, result.NativeChild).ConfigureAwait(false);
                    throw new ObjectDisposedException("Engine app");
                }
                return result;
            }
            finally { entry.Gate.Release(); }
        }

        public static Task CloseAsync(object app, Guid runspaceId)
        {
            return CloseEntry(Require(app, runspaceId, true));
        }

        public static void CloseConnection(object owner)
        {
            if (owner == null) return;
            Entry[] entries;
            PendingOpen[] opening;
            lock (Sync)
            {
                Owners.GetValue(owner, ignored => new OwnerState()).Closed = true;
                entries = Active.Where(entry => ReferenceEquals(entry.Owner, owner)).ToArray();
                opening = Opening.Where(entry => ReferenceEquals(entry.Owner, owner)).ToArray();
            }
            Task cleanup = Task.WhenAll(entries.Select(CloseEntry).Concat(opening.Select(entry => entry.Cancel())));
            Observe(cleanup);
            try
            {
                if (!cleanup.Wait(TimeSpan.FromSeconds(10)))
                    throw new EngineOperationException("Closing the connection's Engine sessions exceeded 10 seconds. The sessions cannot be reused; transport cleanup is still running.");
            }
            catch (AggregateException)
            {
                throw new EngineOperationException("An Engine transport failed while closing the connection. The sessions cannot be reused.");
            }
        }

        internal static void Abandon(object app, Guid runspaceId)
        {
            try { Observe(CloseAsync(app, runspaceId)); } catch { }
        }

        internal static void DisposeUnregistered(object app)
        {
            if (app == null) return;
            Entry entry;
            lock (Sync) Entries.TryGetValue(app, out entry);
            if (entry != null) { Observe(CloseEntry(entry)); return; }
            try
            {
                var provider = new EngineSdkProvider();
                provider.DisposeApp(app, provider.GetSession(app));
            }
            catch { SafeDispose(app as IDisposable); }
            finally { DisposeResources(app); }
        }

        private static Entry Require(object app, Guid runspace, bool allowClosed)
        {
            if (app == null) throw new ArgumentNullException("app");
            Entry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(app, out entry)) throw new EngineOperationException("The app was not opened by this module. Use Open-QSEApp from the same module.");
                if (entry.Runspace != runspace) throw new EngineOperationException("The Engine app belongs to a different PowerShell runspace.");
                if (!allowClosed && entry.Closed) throw new ObjectDisposedException("Engine app");
            }
            return entry;
        }

        private static void RequireOpen(Entry entry)
        {
            lock (Sync) if (entry.Closed) throw new ObjectDisposedException("Engine app");
            if (!ReferenceEquals(entry.Provider.GetSession(entry.App), entry.Session) || entry.Provider.IsClosed(entry.App, entry.Session))
            {
                Observe(CloseEntry(entry));
                throw new EngineOperationException("The Engine session has closed or changed. Open the app again explicitly.");
            }
        }

        private static Task CloseEntry(Entry entry)
        {
            lock (Sync)
            {
                if (entry.Closing != null) return entry.Closing;
                entry.Closed = true;
                entry.Stopped.Cancel();
                object[] children = entry.Children.ToArray();
                entry.Children.Clear();
                // Closing the dedicated socket releases all server session objects,
                // even when an in-flight RPC never returns or a child was disposed.
                entry.Closing = Task.Run(() =>
                {
                    try { entry.Provider.DisposeApp(entry.App, entry.Session); }
                    finally
                    {
                        try
                        {
                            foreach (object child in children) SafeDispose(child as IDisposable);
                            DisposeResources(entry.App);
                        }
                        finally { lock (Sync) Active.Remove(entry); }
                    }
                });
                return entry.Closing;
            }
        }

        private static async Task DisposeLateReadAsync(Entry entry, Task<EngineReadResult> reading)
        {
            try
            {
                EngineReadResult result = await reading.ConfigureAwait(false);
                if (result.NativeChild != null) await SafeDestroyAsync(entry, result.NativeChild).ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task SafeDestroyAsync(Entry entry, object child)
        {
            try { await entry.Provider.DestroyChildAsync(entry.App, child).ConfigureAwait(false); }
            catch { SafeDispose(child as IDisposable); }
        }

        private static void DisposeResources(object app)
        {
            IDisposable[] resources;
            lock (Sync)
            {
                ResourceBag bag = Resources.GetValue(app, ignored => new ResourceBag());
                if (bag.Closed) return;
                bag.Closed = true;
                resources = bag.Items.ToArray();
                bag.Items.Clear();
            }
            foreach (IDisposable resource in resources) SafeDispose(resource);
        }

        private static void SafeDispose(IDisposable value) { try { if (value != null) value.Dispose(); } catch { } }
        internal static void Observe(Task task)
        {
            task.ContinueWith(completed => { var ignored = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
