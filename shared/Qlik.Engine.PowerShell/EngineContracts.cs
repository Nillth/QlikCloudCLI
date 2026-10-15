using System;
using System.Threading.Tasks;

namespace Qlik.Engine.PowerShell
{
    // Only module-authored, credential-free messages use this exception type.
    public sealed class EngineOperationException : InvalidOperationException
    {
        public EngineOperationException(string message) : base(message) { }
    }

    // SDK-neutral contracts keep command discovery independent of Engine DLL loading.
    public sealed class EngineReadRequest
    {
        public string Operation { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string Expression { get; set; }
        public bool AsSdkObject { get; set; }
    }

    public sealed class EngineReadResult
    {
        public object Value { get; set; }
        public bool Enumerate { get; set; }
        public object NativeChild { get; set; }
    }

    public interface IEngineProvider
    {
        object GetSession(object app);
        bool IsClosed(object app, object session);
        Task<EngineReadResult> ReadAsync(object app, EngineReadRequest request);
        Task DestroyChildAsync(object app, object child);
        void DisposeApp(object app, object session);
    }
}
