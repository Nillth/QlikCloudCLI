using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IdentityModel.OidcClient.Browser;
using QlikCloud.DataFiles;
using QlikCloud.LineageGraphs;

namespace QlikCloud
{
    public class Browser : IBrowser
    {
        private int _port;

        public Browser(int port)
        {
            _port = port;
        }

        public async Task<BrowserResult> InvokeAsync(BrowserOptions options,
            CancellationToken cancellationToken = new CancellationToken())
        {
            using (var listener = LoopbackHttpListener.CreateAsync(_port,"callback"))
            {
                try
                {
                    // Create a task that completes upon receiving a context
                    var getContextTask = listener.Listener.GetContextAsync();
                    OpenBrowser(options.StartUrl);

                    // Wait for either the getContextTask to complete or the cancellationToken to be triggered
                    if (await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cancellationToken)) ==
                        getContextTask)
                    {
                        // Proceed only if getContextTask completed before the cancellation token was triggered
                        var context = await getContextTask; // getContextTask is already completed here
                        var code = context.Request.QueryString["code"] ?? string.Empty;
                        var error = context.Request.QueryString["error"]?? string.Empty;
                        var rawUrl = context.Request.RawUrl;
                        await listener.SendResponse(String.IsNullOrEmpty(error), context);
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            var errordescription = context.Request.QueryString["error_description"] ?? "No error description provided";
                            return new BrowserResult
                                { ResultType = BrowserResultType.UnknownError, Error = errordescription };
                        }

                        return new BrowserResult { Response = rawUrl, ResultType = BrowserResultType.Success };
                    }
                    else
                    {
                        // The operation was canceled
                        cancellationToken.ThrowIfCancellationRequested();
                        return new BrowserResult
                        {
                            ResultType = BrowserResultType.Timeout, Error = "Operation cancelled."
                        }; // Fallback, in case ThrowIfCancellationRequested doesn't throw
                    }
                }
                catch (OperationCanceledException ex)
                {
                    return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = ex.Message };
                }
                catch (Exception ex)
                {
                    return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
                }
            }
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
    }
}