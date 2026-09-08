using System.Net.Http;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Primary handler for typed model HttpClients. Disables automatic system proxy
/// discovery (PAC/WPAD lookups add latency before every new connection to the
/// self-hosted gateway/vLLM) and pins connection-pool parameters explicitly.
/// </summary>
internal static class ModelHttpClientHandler
{
    public static SocketsHttpHandler Create()
    {
        return new SocketsHttpHandler
        {
            // 关闭系统代理自动探测（DefaultProxy/PAC），直连自建网关与 vLLM。
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(30),   // 显式：新连接建立超时兜底
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2), // 池内空闲回收
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),    // 周期刷新，避免无限复用
            MaxConnectionsPerServer = 32                // 显式上限，防单端点连接风暴
        };
    }
}
