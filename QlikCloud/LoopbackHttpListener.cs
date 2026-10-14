using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace QlikCloud
{
    internal class LoopbackHttpListener : IDisposable
    {
        private ILogger _logger = Global.LoggingInstance<LoopbackHttpListener>();
        const int DefaultTimeout = 60 * 5; // 5 mins (in seconds)

        TaskCompletionSource<string> _source = new TaskCompletionSource<string>();
        string _url;

        public string Url => _url;
        private HttpListener _httpListener;
        public HttpListener Listener => _httpListener;

        // Private constructor to prevent direct instantiation
        private LoopbackHttpListener(int port, string path)
        {
            path = path?.TrimStart('/') ?? string.Empty; // Ensure path is correctly formatted
            _url = $"http://127.0.0.1:{port}/{path}/";
            _logger.Verbose(_url);
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(_url);
        }

        // Static factory method to create and start the listener asynchronously
        public static LoopbackHttpListener CreateAsync(int port = 0, string path = null)
        {
            if (port == 0)
            {
                port = Global.GetRandomUnusedPort();
            }
            var listener = new LoopbackHttpListener(port, path);
            listener.StartAsync();
            return listener;
        }

        private void StartAsync()
        {
            _logger.Verbose("Starting HttpListener");
            _httpListener.Start();
            return;
        }

        public void Dispose()
        {
            _logger.Verbose("Stopping HttpListener");
            _httpListener?.Stop();
            _httpListener?.Close();
        }
        internal async Task SendResponse(bool success, HttpListenerContext ctx)
        {
            try
            {
                string responseMessage =
                    $"<html><head></head><body onload=\"waitFiveSec()\"><h1>Authentication Successfull</h1><h2> You may now close this tab</h2><script>\r\n  setTimeout(function() {{\r\n      window.close()\r\n  }}, 5000);\r\n</script></body></html>";
                var statuscode = (int)HttpStatusCode.OK;
                if (success == false)
                {
                    var errordescription = ctx.Request.QueryString["error_description"]?? "No error description provided";
                    responseMessage =
                        $"<html><head><script>function close_window() {{\r\nclose();\r\n}}</script></head><body><h1>Authentication Failed Please try again</h1><p>{errordescription}</p><h2>You may now close this tab</h2><a href=\"javascript:close_window();\">close</a></body>";
                    statuscode = (int)HttpStatusCode.Unauthorized;

                }
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseMessage);
                ctx.Response.StatusCode = statuscode;
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = buffer.Length;
                await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
                await ctx.Response.OutputStream.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex.ToString());
            }
            _source.TrySetResult(success.ToString());
        }
    }

}