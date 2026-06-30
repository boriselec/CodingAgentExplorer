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
    // Port 5000: Dashboard (HTTP)
    options.ListenLocalhost(5000);
});

// Services
builder.Services.AddSingleton<RequestStore>();
builder.Services.AddSingleton<HookEventStore>();
builder.Services.AddSingleton<McpProxyConfig>();
builder.Services.AddSingleton<McpRequestStore>();
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
