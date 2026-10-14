using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QlikCloud.Utilities;

public static class Info
{
    private static Version _version;
    private static Process _process;
    private static Assembly _executingAssembly;
    private static string _userAgent;
    private static Assembly _currentAssembly;
    private static string _osInfo;
    private static string _os;
    private static string _osVersion;
    private static string _netVersion;
    private static string _userAgentExtra;
    internal static string _client;

    public static string UserAgentExtra
    {
        get => _userAgentExtra;
        set
        {
            if (ValidateUserAgent(value))
            {
                _userAgentExtra = value;
            }
            else
            {
                throw new ArgumentException("Invalid user agent extra");
            }
        }
    }

    public static Version GetVersion
    {
        get
        {
            if (_version == default) _version = typeof(Global).Assembly.GetName().Version;
            return _version;
        }
    }

    private static bool ValidateUserAgent(string userAgent)
    {
        // Regular expression to match a valid product token (RFC 7231 token definition)
        var productRegex = new Regex(@"[!#$%&'*+.^_`|~A-Za-z0-9-]+(?:/[!#$%&'*+.^_`|~A-Za-z0-9-]+)?",
            RegexOptions.Compiled);
        // Regular expression to match a valid comment (RFC 7231 comment definition)
        var commentRegex = new Regex(@"\((?:[\x21\x23-\x27\x2a-\x5b\x5d-\x7e\x20]*)\)", RegexOptions.Compiled);
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        var currentIndex = 0;
        while (currentIndex < userAgent.Length)
        {
            // Try to match a product token first
            var productMatch = productRegex.Match(userAgent, currentIndex);
            if (productMatch.Success && productMatch.Index == currentIndex)
            {
                currentIndex += productMatch.Length;
            }
            else
            {
                // Try to match a comment
                var commentMatch = commentRegex.Match(userAgent, currentIndex);
                if (commentMatch.Success && commentMatch.Index == currentIndex)
                    currentIndex += commentMatch.Length;
                else
                    return false; // Neither a valid product token nor a valid comment
            }

            // Skip any spaces between tokens
            while (currentIndex < userAgent.Length && char.IsWhiteSpace(userAgent[currentIndex])) currentIndex++;
        }

        return true;
    }
    private static string _userAgentProcess;
    private static string _userAgentProduct;
    private static string _userAgentComment;

    public static string GetUserAgent(string client = default)
    {
        if (_userAgentProcess != default)
        {
            return string.Join(" ", new[] { _userAgentProcess, _userAgentProduct, client, _userAgentComment, _userAgentExtra }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        _process ??= Process.GetCurrentProcess();
        _executingAssembly ??= Assembly.GetExecutingAssembly();
        _currentAssembly ??= typeof(Global).Assembly;

        var currentAssemblyName = _currentAssembly.GetName().Name;
        var currentAssemblyVersion = _currentAssembly.GetName().Version;
        var currentAssemblyVersionString =
            $"{currentAssemblyVersion.Major}.{currentAssemblyVersion.Minor}.{currentAssemblyVersion.Build}";
        var callingAssembly = Assembly.GetCallingAssembly();
        var callingAssemblyName = callingAssembly.GetName().Name;
        var callingAssemblyVersion = callingAssembly.GetName().Version;
        var callingAssemblyVersionString =
            $"{callingAssemblyVersion.Major}.{callingAssemblyVersion.Minor}.{callingAssemblyVersion.Build}";
        var processName = _process.ProcessName;
        var processVersion = FileVersionInfo.GetVersionInfo(_process.MainModule?.FileName ?? string.Empty);
        var processVersionString =
            $"{processVersion.FileMajorPart}.{processVersion.FileMinorPart}.{processVersion.FileBuildPart}";

        if (processName != callingAssemblyName)
        {
            _userAgentProcess = $"{processName}/{processVersionString}";
            _userAgentProduct = $"({GetOSInfo()})";
            _userAgentComment = $"{currentAssemblyName}/{currentAssemblyVersionString} {callingAssemblyName}/{callingAssemblyVersionString}";
        }
        else
        {
            _userAgentProcess = $"{callingAssemblyName}/{callingAssemblyVersionString}";
            _userAgentProduct = $"({GetOSInfo()})";
            _userAgentComment = $"{currentAssemblyName}/{currentAssemblyVersionString}";
        }
        _userAgent = string.Join(" ", new[] { _userAgentProcess, _userAgentProduct, client, _userAgentComment, _userAgentExtra }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return _userAgent;
    }

    private static string GetOSInfo()
    {
        if (_osInfo != default)
            return _osInfo;
        var os = GetOS();
        var osVersion = GetOSVersion();
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLower();
        var netVersion = GetNetVersion();
        _osInfo = $"{os}/{osVersion}-{architecture} Net/{netVersion}";
        return _osInfo;
    }

    private static string GetOS()
    {
        if (_os != default)
            return _os;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _os = "Win";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            _os = "Mac";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var distro = GetLinuxDistribution();
            _os = $"Linux-{distro}";
        }
        return string.IsNullOrEmpty(_os) ? "unknown" : _os;
    }

    private static string GetLinuxDistribution()
    {
        try
        {
            // First, try to read from /etc/os-release
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                var idLine = lines.FirstOrDefault(l => l.StartsWith("ID="));
                if (idLine != null) return idLine.Substring(3).Trim('"');
            }

            // If /etc/os-release doesn't exist or doesn't contain ID, try lsb_release command
            using (var process = new Process())
            {
                process.StartInfo.FileName = "lsb_release";
                process.StartInfo.Arguments = "-i -s";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(output)) return output.ToLower();
            }
        }
        catch
        {
            // If any exception occurs, we'll just return "unknown"
        }

        return "unknown";
    }

    public static string GetVersionString(string version)
    {
        var pattern = @"(\d+(?:\.\d+)+)";
        var match = Regex.Match(version, pattern);
        if (match.Success) return match.Groups[1].Value;
        return "0.0.0";
    }

    private static string GetNetVersion()
    {
        if (_netVersion != default)
            return _netVersion;

        if (Version.TryParse(GetVersionString(RuntimeInformation.FrameworkDescription), out var result))
            _netVersion = $"{result.Major}.{result.Minor}";
        return _netVersion;
    }

    private static string GetOSVersion()
    {
        if (_osVersion != default)
            return _osVersion;
        if (Version.TryParse(GetVersionString(RuntimeInformation.OSDescription), out var result))
            _osVersion = $"{result.Major}.{result.Minor}.{result.Build}";
        return _osVersion;
    }
}