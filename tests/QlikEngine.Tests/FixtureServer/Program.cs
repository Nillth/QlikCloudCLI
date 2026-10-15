using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

// A deliberately small, local-only Engine protocol fixture. Tests use the real
// Qlik SDK; this server records requests and models independent app sessions.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
var app = builder.Build();
var requests = new ConcurrentQueue<JsonObject>();
var sessions = new ConcurrentDictionary<int, FixtureSession>();
var nextSession = 0;
app.UseWebSockets();
app.MapGet("/__fixture/state", () => Results.Json(new {
    requests = requests.ToArray(),
    sessions = sessions.Values.OrderBy(s => s.Id).Select(s => new {
        id = s.Id, path = s.Path, open = s.Open, appId = s.AppId,
        objects = s.Objects.Count, noData = s.NoData, sessionHeader = s.SessionHeader
    }).ToArray()
}));
app.MapFallback(async context => {
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 404; return; }
    var session = new FixtureSession(Interlocked.Increment(ref nextSession), context.Request.Path + context.Request.QueryString) {
        SessionHeader = string.Join(";", context.Request.Headers.Where(h => h.Key.Contains("session", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key + "=" + h.Value))
    };
    sessions[session.Id] = session;
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var closed = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    using var sendGate = new SemaphoreSlim(1, 1);
    var pendingReplies = new List<Task>();
    async Task SendReply(JsonObject reply) {
        await sendGate.WaitAsync(closed.Token);
        try { await Send(socket, reply, closed.Token); }
        finally { sendGate.Release(); }
    }
    try {
        await SendReply(new JsonObject {
            ["jsonrpc"] = "2.0", ["method"] = "OnConnected",
            ["params"] = new JsonObject { ["qSessionState"] = "SESSION_CREATED" }
        });
        var buffer = new byte[65536];
        while (socket.State == WebSocketState.Open) {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do {
                part = await socket.ReceiveAsync(buffer, closed.Token);
                if (part.MessageType == WebSocketMessageType.Close) {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture close", CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            var request = JsonNode.Parse(message.ToArray())!.AsObject();
            var record = request.DeepClone().AsObject();
            record["fixtureSession"] = session.Id;
            requests.Enqueue(record);
            if (request["id"] == null) continue;
            var method = request["method"]!.GetValue<string>();
            JsonObject response;
            try {
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = session.Reply(request) };
            } catch (FixtureError error) {
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message, ["parameter"] = "fixture" } };
            }
            if ((method == "GetScript" && (session.AppId == "slow-app" || session.AppId == "cancel-app")) ||
                (method == "OpenDoc" && session.AppId == "slow-open")) {
                // Keep receiving while withholding this response, so a client
                // cancellation/close is observed immediately by the fixture.
                pendingReplies.Add(Task.Run(async () => {
                    await Task.Delay(TimeSpan.FromSeconds(15), closed.Token);
                    await SendReply(response);
                }, closed.Token));
            } else { await SendReply(response); }
        }
    } catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) {
        // A client cancelling or disposing a session is expected fixture input.
    } finally {
        session.Open = false;
        session.Objects.Clear();
        closed.Cancel();
        try { await Task.WhenAll(pendingReplies); } catch (OperationCanceledException) { }
    }
});
await app.StartAsync();
Console.WriteLine("FIXTURE_URL=" + app.Urls.Single());
await app.WaitForShutdownAsync();

static Task Send(WebSocket socket, JsonObject response, CancellationToken token) =>
    socket.SendAsync(Encoding.UTF8.GetBytes(response.ToJsonString()), WebSocketMessageType.Text, true, token);

sealed class FixtureError(int code, string message) : Exception(message) { public int Code { get; } = code; }

sealed class FixtureSession(int id, string path) {
    public int Id { get; } = id;
    public string Path { get; } = path;
    public string SessionHeader { get; set; } = "";
    public bool Open { get; set; } = true;
    public string? AppId { get; private set; }
    public bool NoData { get; private set; }
    public ConcurrentDictionary<int, JsonObject> Objects { get; } = new();
    private int nextHandle = 10;

    public JsonObject Reply(JsonObject request) {
        var method = request["method"]!.GetValue<string>();
        var handle = request["handle"]?.GetValue<int>() ?? -1;
        var parameters = request["params"];
        JsonNode? Param(int index, string name) => parameters is JsonArray array
            ? (index < array.Count ? array[index] : null) : parameters?[name];
        string? Text(int index, string name) => Param(index, name)?.GetValue<string>();
        switch (method) {
            case "QTProduct": return Obj("qReturn", "Engine");
            case "GetAuthenticatedUser": return Obj("qReturn", "UserDirectory=FIXTURE; UserId=test");
            case "GetProductVersion": return Obj("qReturn", "Fixture Engine");
            case "EngineVersion": return Obj("qVersion", new JsonObject { ["qComponentVersion"] = "34.0.0" });
            case "OpenDoc":
                AppId = Text(0, "qDocName");
                if (AppId == "missing-app") throw new FixtureError(1003, "Fixture app not found");
                NoData = Param(4, "qNoData")?.GetValue<bool>() ?? false;
                return Obj("qReturn", Reference(1, "Doc", AppId));
            case "GetActiveDoc": return Obj("qReturn", Reference(1, "Doc", AppId));
            case "GetAppLayout": return Obj("qLayout", new JsonObject {
                ["qTitle"] = "Fixture " + AppId, ["qFileName"] = AppId,
                ["qLastReloadTime"] = "2026-01-01T00:00:00Z", ["qHasScript"] = true, ["qHasData"] = !NoData
            });
            case "GetScript": return Obj("qScript", "// " + AppId + "\r\nLOAD * INLINE [Sales\n42];");
            case "CheckScriptSyntax": return Obj("qErrors", new JsonArray(new JsonObject {
                ["qErrNo"] = 1, ["qLineInScript"] = 3, ["qTextPos"] = 2, ["qTabIx"] = 0, ["qErrString"] = "Fixture syntax diagnostic"
            }));
            case "CheckExpression": return new JsonObject {
                ["qErrorMsg"] = Text(0, "qExpr") == "bad(" ? "Fixture invalid expression" : "",
                ["qBadFieldNames"] = new JsonArray(), ["qDangerousFieldNames"] = new JsonArray()
            };
            case "EvaluateEx":
                if (Text(0, "qExpression") == "FixtureError") throw new FixtureError(7001, "Fixture evaluation failed");
                return Obj("qValue", new JsonObject { ["qText"] = "42", ["qNumber"] = 42.0, ["qIsNumeric"] = true });
            case "GetTablesAndKeys": return new JsonObject {
                ["qtr"] = new JsonArray(new JsonObject { ["qName"] = "SalesTable", ["qNoOfRows"] = 3,
                    ["qFields"] = new JsonArray(new JsonObject { ["qName"] = "Sales", ["qCardinal"] = 3 }) }),
                ["qk"] = new JsonArray()
            };
            case "GetField": return Obj("qReturn", Reference(2, "Field", Text(0, "qFieldName")));
            case "GetFieldDescription": return Obj("qReturn", new JsonObject { ["qName"] = "Sales", ["qCardinal"] = 3, ["qTags"] = new JsonArray("$numeric") });
            case "GetCardinal": return Obj("qReturn", 3);
            case "GetAndMode": return Obj("qReturn", false);
            case "GetNxProperties": return Obj("qProperties", new JsonObject { ["qOneAndOnlyOne"] = false });
            case "CreateSessionObject":
                var definition = Param(0, "qProp")?.DeepClone().AsObject() ?? new JsonObject();
                var objectHandle = Interlocked.Increment(ref nextHandle);
                var objectId = "fixture-object-" + objectHandle;
                definition["qInfo"] ??= new JsonObject();
                definition["qInfo"]!["qId"] = objectId;
                Objects[objectHandle] = definition;
                return Obj("qReturn", Reference(objectHandle, "GenericObject", objectId, definition["qInfo"]?["qType"]?.GetValue<string>()));
            case "GetLayout":
                if (!Objects.TryGetValue(handle, out var properties)) throw new FixtureError(1002, "Fixture object is closed");
                return Obj("qLayout", Layout(properties));
            case "GetProperties":
                return Obj("qProp", Objects.TryGetValue(handle, out var props) ? props.DeepClone() : new JsonObject());
            case "DestroySessionObject":
                var targetId = Text(0, "qId");
                foreach (var pair in Objects.Where(p => p.Value["qInfo"]?["qId"]?.GetValue<string>() == targetId)) Objects.TryRemove(pair.Key, out _);
                return Obj("qSuccess", true);
            case "GetDocList": return Obj("qDocList", new JsonArray());
            case "AbortRequest": case "CancelRequest": case "ClearAll": return new JsonObject();
            default: throw new FixtureError(-32601, "Fixture does not implement " + method);
        }
    }

    private static JsonObject Layout(JsonObject properties) {
        var result = new JsonObject { ["qInfo"] = properties["qInfo"]?.DeepClone(),
            ["qMeta"] = new JsonObject { ["title"] = "Fixture list" } };
        foreach (var property in properties.Where(p => p.Key.EndsWith("ListDef", StringComparison.Ordinal))) {
            var name = property.Key[..^3];
            var type = property.Value?["qType"]?.GetValue<string>() ?? name;
            var item = new JsonObject { ["qInfo"] = new JsonObject { ["qId"] = "fixture-" + type, ["qType"] = type },
                ["qMeta"] = new JsonObject { ["title"] = "Fixture " + type, ["description"] = "Fixture description" },
                ["qData"] = new JsonObject { ["title"] = "Fixture " + type }, ["qName"] = "Sales", ["qCardinal"] = 3,
                ["qDefinition"] = "42", ["qTags"] = new JsonArray("fixture") };
            result[name] = new JsonObject { ["qItems"] = new JsonArray(item) };
        }
        return result;
    }
    private static JsonObject Reference(int handle, string type, string? id, string? genericType = null) => new() {
        ["qHandle"] = handle, ["qType"] = type, ["qGenericId"] = id, ["qGenericType"] = genericType ?? type
    };
    private static JsonObject Obj(string key, JsonNode? value) => new() { [key] = value };
}
