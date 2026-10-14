using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using QlikCloud.Clients;

namespace QlikCloud.AcceptanceFixtures
{
    // Keep this fixture in C# 5 syntax so Windows PowerShell 5.1 can compile it
    // with Add-Type and its built-in .NET Framework compiler. No SDK build is needed.
    public sealed class RecordedRequest
    {
        public string Method { get; set; }
        public string Uri { get; set; }
        public string ContentType { get; set; }
        public byte[] Body { get; set; }
        public string Text { get { return Encoding.UTF8.GetString(Body); } }
    }

    // This handler has no network fallback. An unexpected request fails the test immediately.
    public sealed class MockTransport : HttpMessageHandler
    {
        private sealed class Reply
        {
            internal int Status;
            internal byte[] Bytes;
            internal string ContentType;
            internal bool Pending;
        }

        private readonly ConcurrentQueue<Reply> replies = new ConcurrentQueue<Reply>();
        private readonly ConcurrentQueue<RecordedRequest> requests = new ConcurrentQueue<RecordedRequest>();
        private int cancelled;
        private int disposed;
        public int Count { get { return requests.Count; } }
        public int CancelledCount { get { return cancelled; } }
        public int DisposedCount { get { return disposed; } }
        public RecordedRequest[] Requests { get { return requests.ToArray(); } }

        public void QueueJson(int status, string json)
        {
            QueueBytes(status, Encoding.UTF8.GetBytes(json), "application/json");
        }

        public void QueueBytes(int status, byte[] bytes, string contentType)
        {
            replies.Enqueue(new Reply { Status = status, Bytes = bytes, ContentType = contentType });
        }

        public void QueuePending()
        {
            replies.Enqueue(new Reply { Pending = true });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? new byte[0] : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            requests.Enqueue(new RecordedRequest
            {
                Method = request.Method.Method,
                Uri = request.RequestUri.AbsoluteUri,
                ContentType = request.Content == null || request.Content.Headers.ContentType == null ? null : request.Content.Headers.ContentType.ToString(),
                Body = body
            });
            Reply reply;
            if (!replies.TryDequeue(out reply)) throw new InvalidOperationException("Unexpected mock HTTP request: " + request.Method + " " + request.RequestUri);
            if (reply.Pending)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            }
            var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new ByteArrayContent(reply.Bytes), RequestMessage = request };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(reply.ContentType);
            response.Headers.Add("X-Qlik-Acceptance", "local-mock");
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref disposed);
            base.Dispose(disposing);
        }
    }

    public sealed class FixtureClient : RestClient<FixtureClient>
    {
        public FixtureClient(HttpClient httpClient) : base("https://qlikcloud-acceptance.invalid/", NullLogger.Instance)
        {
            Config.httpClient.Dispose();
            Config.httpClient = httpClient;
            base.Initialize();
        }

        public override bool Authenticate() { return true; }
    }
}
