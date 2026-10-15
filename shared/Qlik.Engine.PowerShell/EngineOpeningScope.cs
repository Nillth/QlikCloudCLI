using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Qlik.Engine.PowerShell
{
    // A cmdlet invocation owns resources before an IApp is available. Adapters
    // subsequently attach the same resources to the successfully opened app.
    internal sealed class EngineOpeningScope
    {
        private readonly object sync = new object();
        private readonly List<IDisposable> resources = new List<IDisposable>();
        private bool cancelled, transferred;
        private Task closing;

        internal void Track(IDisposable resource)
        {
            if (resource == null) return;
            lock (sync)
            {
                if (!cancelled && !transferred)
                {
                    if (!resources.Any(value => ReferenceEquals(value, resource))) resources.Add(resource);
                    return;
                }
            }
            SafeDispose(resource);
            throw new OperationCanceledException();
        }

        internal void ThrowIfCancelled() { lock (sync) if (cancelled) throw new OperationCanceledException(); }

        internal bool Transfer()
        {
            lock (sync)
            {
                if (cancelled) return false;
                transferred = true;
                resources.Clear();
                return true;
            }
        }

        internal Task CancelAsync()
        {
            lock (sync)
            {
                if (closing != null) return closing;
                cancelled = true;
                IDisposable[] owned = resources.ToArray();
                resources.Clear();
                closing = Task.Run(() => { foreach (IDisposable resource in owned) SafeDispose(resource); });
                return closing;
            }
        }

        private static void SafeDispose(IDisposable resource) { try { resource.Dispose(); } catch { } }
    }
}
