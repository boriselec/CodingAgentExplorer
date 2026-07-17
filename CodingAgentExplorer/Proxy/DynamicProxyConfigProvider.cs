using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using CodingAgentExplorer.Services;

namespace CodingAgentExplorer.Proxy;

public class DynamicProxyConfigProvider(McpProxyConfig mcpConfig) : IProxyConfigProvider
{
    private static readonly ForwarderRequestConfig LongTimeout = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(10),
        AllowResponseBuffering = false
    };

    public IProxyConfig GetConfig()
    {
        var routes = new List<RouteConfig>
        {
            new()
            {
                RouteId = "anthropic-route",
                ClusterId = "anthropic-cluster",
                Match = new RouteMatch
                {
                    Hosts = ["localhost:8888", "127.0.0.1:8888"],
                    Path = "{**catch-all}"
                }
            },
            new()
            {
                RouteId = "llama-route",
                ClusterId = "llama-cluster",
                Match = new RouteMatch
                {
                    Hosts = ["localhost:8889", "127.0.0.1:8889"],
                    Path = "{**catch-all}"
                }
            }
        };

        var clusters = new List<ClusterConfig>
        {
            new()
            {
                ClusterId = "anthropic-cluster",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["dest"] = new() { Address = "https://api.anthropic.com" }
                },
                HttpRequest = LongTimeout
            },
            new()
            {
                ClusterId = "llama-cluster",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["dest"] = new() { Address = "http://192.168.1.128:8080" }
                },
                HttpRequest = LongTimeout
            }
        };

        return new InMemoryProxyConfig(routes, clusters, mcpConfig.GetChangeToken());
    }
}

internal sealed class InMemoryProxyConfig(
    IReadOnlyList<RouteConfig> routes,
    IReadOnlyList<ClusterConfig> clusters,
    IChangeToken changeToken) : IProxyConfig
{
    public IReadOnlyList<RouteConfig> Routes { get; } = routes;
    public IReadOnlyList<ClusterConfig> Clusters { get; } = clusters;
    public IChangeToken ChangeToken { get; } = changeToken;
}
