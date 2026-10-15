using System;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QlikCloud.Clients;

namespace QlikCloud.Common
{
    /// <summary>A reusable client connection; disposing it closes its Engine sessions and HTTP transport.</summary>
    public sealed class Connection : IDisposable
    {
        private int disposed;
        public IRestClient QCSClient { get; }
        public ILogger Logger { get; }
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public Connection(IRestClient client, ILogger logger = null)
        {
            QCSClient = client ?? throw new ArgumentNullException(nameof(client));
            Logger = logger ?? client.Logger ?? NullLogger.Instance;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { Qlik.Engine.PowerShell.EngineSessionRegistry.CloseConnection(this); }
            finally { QCSClient.Config?.httpClient?.Dispose(); }
        }

        public override string ToString()
        {
            var address = QCSClient.Config?.httpClient?.BaseAddress;
            return address == null ? "Qlik Cloud connection" : "Qlik Cloud connection: " + address.GetLeftPart(UriPartial.Authority);
        }
    }

    /// <summary>Defaults belong to a PowerShell runspace, never to the whole process.</summary>
    public static class ConnectionManager
    {
        private static readonly ConditionalWeakTable<Runspace, RunspaceConnection> Connections = new ConditionalWeakTable<Runspace, RunspaceConnection>();

        public static Connection GetConnection()
        {
            var runspace = Runspace.DefaultRunspace;
            return runspace != null && Connections.TryGetValue(runspace, out var state) ? state.Current : null;
        }

        public static void SetConnection(Connection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (connection.IsDisposed) throw new ObjectDisposedException(nameof(connection));
            var runspace = Runspace.DefaultRunspace ?? throw new InvalidOperationException("A default connection requires an active PowerShell runspace.");
            Connections.GetValue(runspace, key => new RunspaceConnection(key)).Current = connection;
        }

        public static Connection ClearConnection(Connection expected = null)
        {
            var runspace = Runspace.DefaultRunspace;
            if (runspace == null || !Connections.TryGetValue(runspace, out var state)) return null;
            if (expected == null) return Interlocked.Exchange(ref state.Current, null);
            var previous = Interlocked.CompareExchange(ref state.Current, null, expected);
            return ReferenceEquals(previous, expected) ? previous : null;
        }

        public static void EnsureConnection()
        {
            var connection = GetConnection();
            if (connection == null || connection.IsDisposed)
                throw new InvalidOperationException("No active Qlik Cloud connection. Run Connect-QlikCloud or supply -Connection.");
        }

        private sealed class RunspaceConnection
        {
            public Connection Current;
            public RunspaceConnection(Runspace runspace) => runspace.StateChanged += OnStateChanged;

            private void OnStateChanged(object sender, RunspaceStateEventArgs args)
            {
                if (args.RunspaceStateInfo.State != RunspaceState.Closed && args.RunspaceStateInfo.State != RunspaceState.Broken) return;
                Interlocked.Exchange(ref Current, null)?.Dispose();
                ((Runspace)sender).StateChanged -= OnStateChanged;
            }
        }
    }
}
