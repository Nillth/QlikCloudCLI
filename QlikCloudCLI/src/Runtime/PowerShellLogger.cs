using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace QlikCloud.Common
{
    /// <summary>Never calls PowerShell from an SDK continuation or engine event thread.</summary>
    public sealed class PowerShellLogger : ILogger
    {
        private const int MaximumPendingMessages = 256;
        private readonly ConcurrentQueue<LogMessage> messages = new ConcurrentQueue<LogMessage>();

        public IDisposable BeginScope<TState>(TState state) => EmptyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // SDK logs can include token responses, request headers, request bodies and credentials.
            // Render structured metadata ourselves instead of invoking a formatter on those objects.
            var fields = state as IEnumerable<KeyValuePair<string, object>>;
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string template = null;
            if (fields != null)
                foreach (var item in fields)
                    if (item.Key == "{OriginalFormat}") template = item.Value as string;
                    else values[item.Key.TrimStart('@', '$')] = item.Value;
            var message = template == null ? "Qlik Cloud diagnostic event (details omitted)." : Regex.Replace(template,
                @"\{[@$]?([^}:]+)(?::[^}]+)?\}", match => SafeValue(match.Groups[1].Value, values));
            messages.Enqueue(new LogMessage(logLevel, message));
            while (messages.Count > MaximumPendingMessages) messages.TryDequeue(out _);
        }

        private static string SafeValue(string name, IDictionary<string, object> values)
        {
            if (!values.TryGetValue(name, out var value) || value == null) return "(none)";
            var key = name.ToLowerInvariant();
            if (key.Contains("token") || key.Contains("secret") || key.Contains("password") || key.Contains("authorization") || key.Contains("apikey") ||
                key.Contains("header") || key.Contains("content") || key.Contains("body") || key.Contains("option") || key.Contains("result") || key.Contains("error"))
                return "[details omitted]";
            if (value is Uri address) return SafeAddress(address);
            if (value is string text)
            {
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    return SafeAddress(uri);
                return text.Length > 256 ? "[details omitted]" : text.Replace('\r', ' ').Replace('\n', ' ');
            }
            return value.GetType().IsPrimitive || value.GetType().IsEnum
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : "[details omitted]";
        }

        private static string SafeAddress(Uri uri) => uri.IsAbsoluteUri
            ? uri.Scheme + "://" + uri.Authority + uri.AbsolutePath : "[relative URI]";

        internal bool TryRead(out LogMessage message) => messages.TryDequeue(out message);

        internal sealed class LogMessage
        {
            public LogLevel Level { get; }
            public string Text { get; }
            public LogMessage(LogLevel level, string text) { Level = level; Text = text; }
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new EmptyScope();
            public void Dispose() { }
        }
    }
}
