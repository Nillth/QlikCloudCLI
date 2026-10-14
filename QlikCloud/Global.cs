using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using QlikCloud.CspOrigins;
using Serilog;
using Serilog.Core;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using System.Reflection;
using System.Linq;
using System.ComponentModel;
using System.Text.RegularExpressions;
using QlikCloud.Utilities;

namespace QlikCloud
{
    public static class Global
    {
        private static LoggingLevelSwitch _loggingLevelSwitch = new LoggingLevelSwitch()
        {
            #if DEBUG
                MinimumLevel = Serilog.Events.LogEventLevel.Debug
            #else
                MinimumLevel = Serilog.Events.LogEventLevel.Information
            #endif
        };

        internal static LoggingLevelSwitch loggingLevelSwitch
        {
            get => _loggingLevelSwitch;
        }
        public static Serilog.Events.LogEventLevel LogLevel
        {
            get => _loggingLevelSwitch.MinimumLevel;
            set => _loggingLevelSwitch.MinimumLevel = value;
        }

        public static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch
            {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
                    throw;
                }
            }
        }

        internal static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        //private const string OutputTemplateConsole = "[{Timestamp:HH:mm:ss} {Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}";
        private const string OutputTemplateConsole = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        public static Serilog.ILogger LoggingInstance(string type)
        {
            var loggerConfiguration = new LoggerConfiguration()
                 .Destructure.With(new SensitiveDataDestructuringPolicy(new[] { "IdentityToken", "RefreshToken", "AccessToken", "access_token", "Authorization", "ClientId", "ClientSecret" }, new[] { "Raw" })) // Specify sensitive properties
                 .MinimumLevel.ControlledBy(loggingLevelSwitch)
                 .WriteTo.Console(outputTemplate: OutputTemplateConsole);
            return loggerConfiguration.CreateLogger().ForContext("SourceContext", type);
        }
        public static Serilog.ILogger LoggingInstance<T>(string logfile = default)
        {
            var loggerConfiguration = new LoggerConfiguration()
                .Destructure.With(new SensitiveDataDestructuringPolicy(new[] { "IdentityToken", "RefreshToken", "AccessToken", "access_token", "Authorization", "ClientId", "ClientSecret" }, new[] { "Raw" })) // Specify sensitive properties
                .MinimumLevel.ControlledBy(loggingLevelSwitch)
                .WriteTo.Console(outputTemplate: OutputTemplateConsole);
            return loggerConfiguration.CreateLogger().ForContext<T>();
        }
        public static Microsoft.Extensions.Logging.ILogger MELoggingInstance<T>(string logfile = default)
        {
            var loggerConfiguration = new LoggerConfiguration()
                .Destructure.With(new SensitiveDataDestructuringPolicy(new[] { "IdentityToken", "RefreshToken", "AccessToken", "access_token", "Authorization", "ClientId", "ClientSecret" }, new[] { "Raw" }))
                .MinimumLevel.ControlledBy(loggingLevelSwitch)
                .WriteTo.Console(outputTemplate: OutputTemplateConsole);

            var serilogLogger = loggerConfiguration.CreateLogger().ForContext<T>();

            // Create a factory that uses Serilog
            var loggerFactory = new SerilogLoggerFactory(serilogLogger);

            // Create and return a Microsoft.Extensions.Logging.ILogger
            return loggerFactory.CreateLogger(typeof(T).FullName);
        }
        public static LoggerConfiguration GetLoggerConfiguration()
        {
            var loggerConfiguration = new LoggerConfiguration()
                .Destructure.With(new SensitiveDataDestructuringPolicy(new[] { "IdentityToken", "RefreshToken", "AccessToken", "access_token", "Authorization", "ClientId", "ClientSecret" }, new[] { "Raw" })) // Specify sensitive properties
                .MinimumLevel.ControlledBy(loggingLevelSwitch)
                .WriteTo.Console(outputTemplate: OutputTemplateConsole);
            return loggerConfiguration;
        }

    }

    public class OAuthCredentials
    {
        public static OAuthCredentials CreateInstance(string clientId, string clientSecret = null)
        {
            return new OAuthCredentials(clientId, clientSecret);
        }

        public string clientSecret;
        public string clientId;
        private OAuthCredentials(string clientId, string clientSecret = null)
        {
            this.clientSecret = clientSecret;
            this.clientId = clientId;
        }
    }
    public enum CloudRegions
    {
        AP,
        EU,
        SG,
        US
    }
}
