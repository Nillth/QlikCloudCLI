using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Reflection;

namespace QlikCloudCLI.Runtime
{
    /// <summary>
    /// .NET Framework does not apply a library's .dll.config binding redirects.
    /// Resolve references made by our packaged assemblies against their bundled
    /// compatible dependencies, without changing powershell.exe.config or the GAC.
    /// </summary>
    public sealed class ModuleAssemblyResolver : IModuleAssemblyInitializer
    {
        private static readonly object Gate = new object();
        private static readonly string DirectoryPath = Path.GetDirectoryName(typeof(ModuleAssemblyResolver).Assembly.Location);
        private static readonly Dictionary<string, string> Libraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;

        public void OnImport()
        {
            lock (Gate)
            {
                if (initialized) return;
                foreach (var path in Directory.GetFiles(DirectoryPath, "*.dll"))
                    Libraries[Path.GetFileNameWithoutExtension(path)] = path;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                // Assemblies remain loaded after Remove-Module. Keep one resolver
                // for their lifetime, including references held by returned models.
                initialized = true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            // Reject an identified caller outside this package. PowerShell 5.1's
            // reflection binder can omit RequestingAssembly; in that case accept
            // only exact reference identities declared by already-loaded package
            // assemblies, never an arbitrary request for a matching file name.
            var requesting = args.RequestingAssembly;
            if (requesting != null && !IsModuleAssembly(requesting)) return null;

            var requested = new AssemblyName(args.Name);
            if (!Libraries.TryGetValue(requested.Name, out var path)) return null;
            if (requesting == null && !AppDomain.CurrentDomain.GetAssemblies()
                .Where(IsModuleAssembly).Any(assembly => assembly.GetReferencedAssemblies()
                    .Any(reference => string.Equals(reference.FullName, requested.FullName, StringComparison.OrdinalIgnoreCase))))
                return null;
            var available = AssemblyName.GetAssemblyName(path);
            if (!string.Equals(requested.Name, available.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requested.CultureName ?? "", available.CultureName ?? "", StringComparison.OrdinalIgnoreCase) ||
                !(requested.GetPublicKeyToken() ?? new byte[0]).SequenceEqual(available.GetPublicKeyToken() ?? new byte[0]) ||
                (requested.Version != null && available.Version < requested.Version))
                return null;

            return Assembly.LoadFrom(path);
        }

        private static bool IsModuleAssembly(Assembly assembly)
        {
            if (assembly.IsDynamic) return false;
            // Add-Type and Assembly.Load(byte[]) assemblies can have no location.
            // .NET Framework rejects GetDirectoryName("") rather than returning null.
            try
            {
                var location = assembly.Location;
                return !string.IsNullOrEmpty(location) &&
                    string.Equals(Path.GetDirectoryName(location), DirectoryPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (NotSupportedException) { return false; }
        }
    }
}
