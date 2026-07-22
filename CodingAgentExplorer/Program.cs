using CodingAgentExplorer.Hubs;
using CodingAgentExplorer.Models;
using CodingAgentExplorer.Proxy;
using CodingAgentExplorer.Services;
using Microsoft.AspNetCore.SignalR;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

const string DashboardPort5000 = "*:5000";

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel endpoints (localhost only, never exposed externally)
builder.WebHost.ConfigureKestrel(options =>
{
    // Port 8888: Claude API proxy (HTTP)
    options.ListenLocalhost(8888);
    // Port 8889: llama.cpp (OpenAI-compatible) proxy (HTTP)
    options.ListenLocalhost(8889);
    // Port 5000: Dashboard (HTTP)
    options.ListenLocalhost(5000);
});

// Services
builder.Services.AddSingleton<RequestStore>();
builder.Services.AddSingleton<HookEventStore>();
builder.Services.AddSingleton<McpProxyConfig>();
builder.Services.AddSingleton<McpRequestStore>();
builder.Services.AddSingleton<TailscaleExitNodeGuard>();
builder.Services.AddSingleton<ITransformProvider, CaptureTransformProvider>();
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// YARP with dynamic config (Claude proxy on :8888)
builder.Services.AddReverseProxy();
builder.Services.AddSingleton<IProxyConfigProvider, DynamicProxyConfigProvider>();

var app = builder.Build();

// Serve static files only on dashboard ports
app.UseWhen(
    ctx => ctx.Connection.LocalPort is 5000,
    branch => branch.UseStaticFiles());

// Gate the Claude proxy flow (port 8888): only forward upstream when Tailscale is
// currently routing traffic through an exit node. Requests are blocked (403) otherwise
// and never reach YARP. The llama proxy (8889) and dashboard (5000) are unaffected.
app.UseWhen(
    ctx => ctx.Connection.LocalPort is 8888,
    branch => branch.Use(async (ctx, next) =>
    {
        var guard = ctx.RequestServices.GetRequiredService<TailscaleExitNodeGuard>();
        var result = await guard.CheckAsync(ctx.RequestAborted);
        if (!result.Allowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    type = "tailscale_exit_node_required",
                    message = $"Request blocked by CodingAgentExplorer: {result.Reason}"
                }
            });
            return;
        }

        await next();
    }));

// Dashboard endpoints (port 5000 only)
app.MapHub<DashboardHub>("/hub").RequireHost(DashboardPort5000);

app.MapPost("/api/hook-event", async (
    HttpContext ctx,
    HookEventStore hookStore,
    IHubContext<DashboardHub> hub) =>
{
    var hookEvent = await ctx.Request.ReadFromJsonAsync<HookEvent>();
    if (hookEvent is null) return Results.BadRequest();

    hookEvent.Timestamp = DateTime.UtcNow;     // server clock for timeline accuracy
    hookEvent.ExitCode = 0;
    hookEvent.Stdout = $"Hook '{hookEvent.HookEventName}' captured";
    hookEvent.Stderr = "";

    hookStore.Add(hookEvent);
    await hub.Clients.All.SendAsync("NewHookEvent", hookEvent);

    return Results.Ok(new {
        exitCode = hookEvent.ExitCode,
        stdout   = hookEvent.Stdout,
        stderr   = hookEvent.Stderr
    });
})
.RequireHost(DashboardPort5000);   // NOT on :8888 (YARP proxy port)

// MCP destination config endpoints
app.MapGet("/api/mcp-destination", (McpProxyConfig mcpConfig) =>
    Results.Ok(new { destinationUrl = mcpConfig.DestinationUrl }))
.RequireHost(DashboardPort5000);

app.MapPost("/api/mcp-destination", async (
    HttpContext ctx,
    McpProxyConfig mcpConfig,
    McpRequestStore mcpStore,
    IHubContext<DashboardHub> hub) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<McpDestinationRequest>();
    mcpConfig.SetDestination(body?.Url);
    mcpStore.Clear();
    await hub.Clients.All.SendAsync("McpConfigChanged", new { destinationUrl = mcpConfig.DestinationUrl });
    await hub.Clients.All.SendAsync("McpCleared");
    return Results.Ok();
})
.RequireHost(DashboardPort5000);

app.MapFallbackToFile("index.html").RequireHost(DashboardPort5000);

// YARP reverse proxy (port 8888 only, via Hosts match in appsettings.json)
app.MapReverseProxy();

await app.RunAsync();
