using System.Net.Http;
using System.Text.Json.Serialization;
using MCLauncher.Core.Abstractions;
using MCLauncher.Core.Network;
using MCLauncher.Core.Utils;

namespace MCLauncher.Core.Connect;

/// <summary>SakuraFrp API v4 里的一条隧道。</summary>
public sealed class SakuraFrpTunnel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("node")]
    public int Node { get; set; }

    /// <summary>0 正常、2 封禁。</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("status_reason")]
    public string? StatusReason { get; set; }

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("local_ip")]
    public string LocalIp { get; set; } = string.Empty;

    [JsonPropertyName("local_port")]
    public int LocalPort { get; set; }

    /// <summary>远程端口或绑定域名。</summary>
    [JsonPropertyName("remote")]
    public string Remote { get; set; } = string.Empty;
}

/// <summary>SakuraFrp 节点（<c>GET /nodes</c> 返回的字典值）。</summary>
public sealed class SakuraFrpNode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("vip")]
    public int Vip { get; set; }

    [JsonPropertyName("flag")]
    public int Flag { get; set; }

    /// <summary>节点 ID，取自返回字典的键。</summary>
    [JsonIgnore]
    public int Id { get; set; }

    /// <summary>节点是否离线 / 维护中（第 10 位）。</summary>
    [JsonIgnore]
    public bool IsOffline => (Flag & (1 << 9)) != 0;

    /// <summary>是否允许新建隧道（第 3 位）且节点未离线（第 10 位）。</summary>
    [JsonIgnore]
    public bool CanCreate => (Flag & (1 << 2)) != 0 && !IsOffline;

    /// <summary>是否允许穿透「公开游戏服务器」：第 12 位为 1，或节点名里带「多线」。</summary>
    [JsonIgnore]
    public bool PublicGameAllowed => (Flag & 2048) != 0 || Name.Contains("多线", StringComparison.Ordinal);

    /// <summary>海外节点（第 1 位）。</summary>
    [JsonIgnore]
    public bool IsOverseas => (Flag & (1 << 0)) != 0;

    /// <summary>节点是否支持 UDP（第 6 位）；为 0 时面板会标记「禁 UDP」。</summary>
    [JsonIgnore]
    public bool SupportsUdp => (Flag & (1 << 5)) != 0;

    /// <summary>节点是否支持建站（HTTP / HTTPS，第 4 位）。</summary>
    [JsonIgnore]
    public bool WebsiteAllowed => (Flag & (1 << 3)) != 0;

    /// <summary>BETA 测试节点（第 11 位）。</summary>
    [JsonIgnore]
    public bool IsBeta => (Flag & (1 << 10)) != 0;

    /// <summary>该节点是否可以创建指定类型的隧道。</summary>
    public bool SupportsType(string type) => type switch
    {
        SakuraFrpTunnelTypes.Tcp => true,
        SakuraFrpTunnelTypes.Udp => SupportsUdp,
        // WOL 依赖 UDP 发送唤醒包，禁 UDP 的节点同样不能创建。
        SakuraFrpTunnelTypes.Wol => SupportsUdp,
        SakuraFrpTunnelTypes.Http or SakuraFrpTunnelTypes.Https => WebsiteAllowed,
        _ => false,
    };
}

/// <summary>SakuraFrp 隧道类型（API 里的 <c>type</c> 取值）。</summary>
public static class SakuraFrpTunnelTypes
{
    public const string Tcp = "tcp";

    public const string Udp = "udp";

    public const string Http = "http";

    public const string Https = "https";

    public const string Wol = "wol";

    /// <summary>本启动器支持创建的类型。</summary>
    public static readonly IReadOnlyList<string> Creatable = new[] { Tcp, Udp, Http, Https, Wol };

    public static bool IsCreatable(string? type) =>
        type is not null && Creatable.Contains(type.Trim().ToLowerInvariant());

    /// <summary>创建时是否需要本地端口。</summary>
    public static bool NeedsLocalPort(string type) =>
        type is Tcp or Udp or Http or Https;

    /// <summary>创建时是否需要绑定域名。</summary>
    public static bool NeedsRemoteDomain(string type) =>
        type is Http or Https;
}

/// <summary>创建隧道的请求参数。</summary>
public sealed record SakuraFrpTunnelRequest
{
    public required string Name { get; init; }

    public required int NodeId { get; init; }

    /// <summary>隧道类型，取值见 <see cref="SakuraFrpTunnelTypes"/>。</summary>
    public required string Type { get; init; }

    /// <summary>本地端口；WOL 隧道不需要，传 0 即可。</summary>
    public int LocalPort { get; init; }

    public string LocalIp { get; init; } = "127.0.0.1";

    /// <summary>HTTP / HTTPS 隧道绑定的域名，留空由服务端分配。</summary>
    public string? Remote { get; init; }

    public string? Note { get; init; }
}

/// <summary>创建成功后服务端返回的信息。</summary>
public sealed class SakuraFrpCreatedTunnel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("remote")]
    public string? Remote { get; set; }

    [JsonPropertyName("important_warning_for_ai")]
    public string? ImportantWarning { get; set; }
}

/// <summary>
/// SakuraFrp API v4（https://api.natfrp.com/docs/）的最小封装：
/// 用访问密钥（Bearer Token）读写隧道，只覆盖联机需要的部分。
/// </summary>
public sealed class SakuraFrpApi
{
    private const string BaseUrl = "https://api.natfrp.com/v4";

    private readonly HttpDownloader _http;
    private readonly ILogger _log;

    public SakuraFrpApi(HttpDownloader http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>账号下的全部隧道。</summary>
    public async Task<IReadOnlyList<SakuraFrpTunnel>> GetTunnelsAsync(
        string token,
        CancellationToken ct = default)
    {
        var body = await SendAsync(
                () => _http.RequestStringAsync(
                    $"{BaseUrl}/tunnels", HttpMethod.Get, null, null, AuthHeaders(token), ct))
            .ConfigureAwait(false);

        return JsonUtil.Deserialize<List<SakuraFrpTunnel>>(body) ?? new List<SakuraFrpTunnel>();
    }

    /// <summary>节点列表（含在线/是否允许新建等标记）。</summary>
    public async Task<IReadOnlyList<SakuraFrpNode>> GetNodesAsync(
        string token,
        CancellationToken ct = default)
    {
        var body = await SendAsync(
                () => _http.RequestStringAsync(
                    $"{BaseUrl}/nodes", HttpMethod.Get, null, null, AuthHeaders(token), ct))
            .ConfigureAwait(false);

        var map = JsonUtil.Deserialize<Dictionary<string, SakuraFrpNode>>(body);
        if (map is null)
        {
            return Array.Empty<SakuraFrpNode>();
        }

        var result = new List<SakuraFrpNode>(map.Count);
        foreach (var (key, node) in map)
        {
            if (int.TryParse(key, out var id))
            {
                node.Id = id;
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// 创建一条隧道（TCP / UDP / HTTP / HTTPS / WOL），远程端口由服务端分配。
    /// 节点是否支持该类型由调用方先校验。
    /// </summary>
    public async Task<SakuraFrpCreatedTunnel> CreateTunnelAsync(
        string token,
        SakuraFrpTunnelRequest request,
        CancellationToken ct = default)
    {
        var type = request.Type.Trim().ToLowerInvariant();
        if (!SakuraFrpTunnelTypes.IsCreatable(type))
        {
            throw new InvalidOperationException($"不支持的隧道类型：{request.Type}");
        }

        if (SakuraFrpTunnelTypes.NeedsLocalPort(type) && request.LocalPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "本地端口必须在 1-65535 之间");
        }

        var form = new Dictionary<string, string>
        {
            ["name"] = request.Name.Trim(),
            ["type"] = type,
            ["node"] = request.NodeId.ToString(),
        };

        // WOL 隧道不需要本地地址，交给服务端用默认值。
        if (SakuraFrpTunnelTypes.NeedsLocalPort(type))
        {
            form["local_ip"] = string.IsNullOrWhiteSpace(request.LocalIp) ? "127.0.0.1" : request.LocalIp.Trim();
            form["local_port"] = request.LocalPort.ToString();
        }

        if (SakuraFrpTunnelTypes.NeedsRemoteDomain(type) && !string.IsNullOrWhiteSpace(request.Remote))
        {
            form["remote"] = request.Remote.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            form["note"] = request.Note.Trim();
        }

        var body = await SendAsync(
                () => _http.RequestStringAsync(
                    $"{BaseUrl}/tunnels", HttpMethod.Post, FormEncode(form),
                    "application/x-www-form-urlencoded", AuthHeaders(token), ct))
            .ConfigureAwait(false);

        var created = JsonUtil.Deserialize<SakuraFrpCreatedTunnel>(body);
        if (created is null || created.Id <= 0)
        {
            throw new InvalidOperationException("SakuraFrp 没有返回新建隧道的 ID");
        }

        if (!string.IsNullOrWhiteSpace(created.ImportantWarning))
        {
            _log.Warn($"SakuraFrp 创建隧道的提示：{created.ImportantWarning}");
        }

        _log.Info($"SakuraFrp 已创建隧道 {created.Name}（#{created.Id}），远程 {created.Remote}");
        return created;
    }

    /// <summary>删除若干条隧道（服务端一次最多接受 10 条）。</summary>
    public async Task DeleteTunnelsAsync(
        string token,
        IReadOnlyCollection<int> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return;
        }

        if (ids.Count > 10)
        {
            throw new InvalidOperationException("一次最多删除 10 条隧道");
        }

        await SendAsync(
                () => _http.RequestStringAsync(
                    $"{BaseUrl}/tunnel/delete", HttpMethod.Post,
                    FormEncode(new Dictionary<string, string> { ["ids"] = string.Join(",", ids) }),
                    "application/x-www-form-urlencoded", AuthHeaders(token), ct))
            .ConfigureAwait(false);

        _log.Info($"SakuraFrp 已删除隧道：{string.Join(",", ids)}");
    }

    private static Dictionary<string, string> AuthHeaders(string token) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {token.Trim()}",
            ["Accept"] = "application/json",
        };

    private static string FormEncode(IReadOnlyDictionary<string, string> form) =>
        string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static async Task<string> SendAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HttpApiException ex)
        {
            throw new InvalidOperationException(Describe(ex), ex);
        }
    }

    /// <summary>把服务端的 <c>{"code":403,"msg":"无权访问"}</c> 转成可读文案。</summary>
    private static string Describe(HttpApiException ex)
    {
        var message = TryReadError(ex.ResponseBody);
        return message is null
            ? $"SakuraFrp 请求失败（HTTP {(int)ex.StatusCode}）"
            : $"SakuraFrp：{message}（HTTP {(int)ex.StatusCode}）";
    }

    private static string? TryReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var error = JsonUtil.Deserialize<SakuraFrpError>(body);
            return string.IsNullOrWhiteSpace(error?.Msg) ? null : error!.Msg;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed class SakuraFrpError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }
    }
}
