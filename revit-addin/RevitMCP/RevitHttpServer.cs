using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP;

/// <summary>
/// Embedded HttpListener that receives JSON requests from the MCP Node server
/// and dispatches them on Revit's UI thread via ExternalEvent.
/// </summary>
public class RevitHttpServer
{
    private const string Prefix = "http://localhost:6543/";
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RevitApiTimeout = TimeSpan.FromSeconds(120);
    private const long MaxRequestBytes = 2 * 1024 * 1024;

    private readonly HttpListener _listener = new();
    private Thread? _thread;
    private bool _running;

    // The ExternalEvent mechanism lets us marshal calls onto Revit's main thread.
    private readonly RevitEventHandler _eventHandler;
    private readonly ExternalEvent _externalEvent;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public RevitHttpServer(UIControlledApplication uiApp)
    {
        _eventHandler = new RevitEventHandler();
        _externalEvent = ExternalEvent.Create(_eventHandler);
    }

    public void Start()
    {
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _running = true;
        _thread = new Thread(Listen) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_listener.IsListening)
            _listener.Stop();
    }

    private void Listen()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }

            // Handle each request on a thread-pool thread
            ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        try
        {
            var path = req.Url?.AbsolutePath ?? "";

            if (path == "/healthz")
            {
                WriteJson(res, 200, new { ok = true, service = "RevitMCP" });
                return;
            }

            if (req.HttpMethod != "POST")
            {
                WriteJson(res, 405, new { error = "Only POST requests are supported." });
                return;
            }

            if (!path.StartsWith("/api/"))
            {
                WriteJson(res, 404, new { error = "Not found" });
                return;
            }

            if (req.ContentLength64 > MaxRequestBytes)
            {
                WriteJson(res, 413, new { error = "Request body is too large." });
                return;
            }

            // Read body
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            JObject payload;
            try
            {
                payload = string.IsNullOrWhiteSpace(body)
                    ? new JObject()
                    : JObject.Parse(body);
            }
            catch (JsonReaderException ex)
            {
                WriteJson(res, 400, new { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            var action = path["/api/".Length..].TrimEnd('/');
            if (string.IsNullOrWhiteSpace(action))
            {
                WriteJson(res, 400, new { error = "Missing Revit action in /api/<action>." });
                return;
            }

            if (!_requestGate.Wait(QueueTimeout))
            {
                WriteJson(res, 429, new { error = "RevitMCP is busy. Try again in a few seconds." });
                return;
            }

            try
            {
                // Dispatch on Revit thread and wait for result.
                var request = _eventHandler.SetRequest(action, payload);
                _externalEvent.Raise();

                if (!RevitEventHandler.WaitForResult(request, RevitApiTimeout))
                {
                    WriteJson(res, 504, new { error = $"Revit API timeout after {RevitApiTimeout.TotalSeconds:0} s" });
                    return;
                }

                var (result, error) = RevitEventHandler.GetResult(request);
                if (error != null)
                    WriteJson(res, 500, new { error });
                else
                    WriteJson(res, 200, result!);
            }
            finally
            {
                _requestGate.Release();
            }
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, new { error = ex.Message });
        }
    }

    private static void WriteJson(HttpListenerResponse res, int status, object data)
    {
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }
}
