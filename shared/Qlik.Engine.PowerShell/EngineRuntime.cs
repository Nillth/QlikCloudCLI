using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Qlik.Engine.PowerShell
{
    public sealed class EngineRuntimeException : InvalidOperationException
    {
        public EngineRuntimeException(string message) : base(message) { }
    }

    public static class EngineRuntime
    {
        private static readonly object Sync = new object();
        private static readonly string[] Names = { "Qlik.Sense.JsonRpc", "Qlik.Engine", "Qlik.Sense.Client", "Qlik.Engine.Extensions" };
        private static readonly Version SdkVersion = new Version(16, 11, 0, 0);

        public static void EnsureLoaded()
        {
            lock (Sync)
            {
                bool desktop = typeof(object).Assembly.GetName().Name == "mscorlib";
                string directory = Path.Combine(Path.GetDirectoryName(typeof(EngineRuntime).Assembly.Location), "Engine", desktop ? "Desktop" : "Core");
                foreach (string name in Names)
                {
                    string path = Path.Combine(directory, name + ".dll");
                    if (!File.Exists(path)) throw new EngineRuntimeException("Engine support files are missing. Reinstall the complete module package before opening an Engine app.");
                    AssemblyName identity;
                    try { identity = AssemblyName.GetAssemblyName(path); }
                    catch { throw new EngineRuntimeException("An Engine support file is invalid. Reinstall the complete module package."); }
                    if (identity.Name != name || identity.Version != SdkVersion)
                        throw new EngineRuntimeException("The module's Engine support files do not match the required SDK version 16.11.0. Reinstall the complete package.");
                }
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => Names.Contains(assembly.GetName().Name))) ValidateLoaded(assembly, desktop);
                foreach (string name in Names)
                {
                    Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == name);
                    if (assembly == null)
                    {
                        try { assembly = Assembly.LoadFrom(Path.Combine(directory, name + ".dll")); }
                        catch { throw new EngineRuntimeException("Engine support could not be loaded. Reinstall the complete module and restart PowerShell."); }
                    }
                    ValidateLoaded(assembly, desktop);
                }
            }
        }

        private static void ValidateLoaded(Assembly assembly, bool desktop)
        {
            string framework = assembly.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
                .Select(attribute => attribute.ConstructorArguments.Count == 1 ? attribute.ConstructorArguments[0].Value as string : null).FirstOrDefault();
            string expected = desktop ? ".NETFramework," : ".NETCoreApp,";
            if (assembly.GetName().Version != SdkVersion || framework == null || !framework.StartsWith(expected, StringComparison.Ordinal))
                throw new EngineRuntimeException("An incompatible Qlik Engine SDK is already loaded. Restart PowerShell and use the complete module package for this PowerShell edition.");
        }
    }
}
