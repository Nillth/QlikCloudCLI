using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace QlikCloud
{
    public static class Init
    {
        public static void Now()
        {

            AppDomain.CurrentDomain.AssemblyResolve += OnResolveAssembly;
            Console.WriteLine("Init: OnResolveAssembly Configured");
            try
            {
                Serilog.ILogger logger = new Serilog.LoggerConfiguration()
                    .MinimumLevel.ControlledBy(Global.loggingLevelSwitch)
                    .CreateLogger();

            }
            catch { }
            try
            {
                //IdentityModel.Client.AuthorityValidationResult options = IdentityModel.Client.AuthorityValidationResult.SuccessResult;
            }
            catch { }
        }
        // The rest of your application's startup code

        private static Assembly OnResolveAssembly(object sender, ResolveEventArgs args)
        {
            string assemblyName = new AssemblyName(args.Name).Name + ".dll";

            // Use the namespace of the embedded resource. Adjust as necessary.
            string resourceName = "QlikCloud." + assemblyName;

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                byte[] assemblyData = new byte[stream.Length];
                stream.Read(assemblyData, 0, assemblyData.Length);
                return Assembly.Load(assemblyData);
            }
        }
    }
}
