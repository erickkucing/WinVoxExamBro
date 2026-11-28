using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ExamBro.Tests.Windows.Fixtures;

/// <summary>
/// In-process Kestrel HTTP server on an ephemeral loopback port simulating the projectCBT contract
/// (workspace, finished route, violation recording, answer saving, redirects, downloads, and chaos drops).
/// </summary>
public sealed class FakeCbtServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; }
    public ConcurrentQueue<string> RequestedPaths { get; } = new();
    public ConcurrentQueue<string> RecordedViolations { get; } = new();
    public ConcurrentQueue<string> RecordedAnswers { get; } = new();
    public volatile string? LastUserAgent;
    public volatile bool DropConnections;
    public volatile bool Return500;
    public int SlowDelayMs { get; set; } = 1500;

    private FakeCbtServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeCbtServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        FakeCbtServer? instance = null;

        app.Use(async (ctx, next) =>
        {
            if (instance != null)
            {
                instance.RequestedPaths.Enqueue(ctx.Request.Path + ctx.Request.QueryString);
                instance.LastUserAgent = ctx.Request.Headers.UserAgent.ToString();
                if (instance.DropConnections)
                {
                    ctx.Abort();
                    return;
                }

                if (instance.Return500)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync("chaos 500");
                    return;
                }
            }

            await next();
        });

        app.MapGet("/login", () => Results.Content(
            """
            <!doctype html><html><head><meta charset="utf-8"><title>CBT Login</title></head>
            <body>
              <h1>Login Ujian</h1>
              <a id="start-link" href="/exam/workspace/e2e-session-1">Masuk Ujian</a>
            </body></html>
            """,
            "text/html"));

        app.MapGet("/exam/workspace/{id}", (string id) => Results.Content(
            $$"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>Workspace {{id}}</title></head>
            <body>
              <div id="cbt-anti-cheat-root" data-session-uuid="{{id}}" data-max-strikes="3" data-is-exambrowser="0">
                <div id="anti-cheat-fullscreen-gate" class="flex" style="display:flex">Fullscreen Gate</div>
                <textarea id="essay-box"></textarea>
                <a id="ext-link" href="/redirect-external">External Redirect</a>
                <a id="dl-link" href="/download">Download File</a>
                <a id="finish-link" href="/exam/finished/{{id}}">Selesai</a>
              </div>
              <script>
                window.__cbtReportViolation = window.__cbtReportViolation || function(type, meta) {
                  return fetch('/api/exam/sessions/{{id}}/violation', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ violation_type: type, metadata: meta })
                  });
                };
              </script>
            </body>
            </html>
            """,
            "text/html"));

        app.MapGet("/exam/finished/{id}", (string id) => Results.Content(
            $"<!doctype html><html><body><h1 id=\"finished-banner\">Ujian {id} Selesai</h1></body></html>",
            "text/html"));

        static async Task<string> ReadRequestBody(HttpRequest req)
        {
            using var reader = new StreamReader(req.Body);
            return await reader.ReadToEndAsync();
        }

        app.MapPost("/api/exam/sessions/{id}/violation", async (HttpRequest req) =>
        {
            string body = await ReadRequestBody(req);
            instance?.RecordedViolations.Enqueue(body);
            return Results.Json(new { action = "recorded", strikes = instance?.RecordedViolations.Count ?? 1, is_locked = false });
        });

        app.MapPost("/violation", async (HttpRequest req) =>
        {
            string body = await ReadRequestBody(req);
            instance?.RecordedViolations.Enqueue(body);
            return Results.Json(new { action = "recorded", strikes = instance?.RecordedViolations.Count ?? 1, is_locked = false });
        });

        app.MapPost("/api/exam/sessions/{id}/answer", async (HttpRequest req) =>
        {
            string body = await ReadRequestBody(req);
            instance?.RecordedAnswers.Enqueue(body);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/slow", async () =>
        {
            await Task.Delay(instance?.SlowDelayMs ?? 1500);
            return Results.Content("<html><body>Slow OK</body></html>", "text/html");
        });

        app.MapGet("/404", () => Results.NotFound("Not Found"));

        app.MapGet("/redirect-external", () => Results.Redirect("https://example.com/"));

        app.MapGet("/download", (HttpResponse resp) =>
        {
            resp.Headers.ContentDisposition = "attachment; filename=\"leak.txt\"";
            return Results.Text("forbidden download", "text/plain");
        });

        await app.StartAsync();
        string baseUrl = app.Urls.First().TrimEnd('/');
        instance = new FakeCbtServer(app, baseUrl);
        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
