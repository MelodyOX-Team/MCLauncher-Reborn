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

    /// <summary>是否允许新建隧道（第 3 位）且节点未离线（第 10 位）。</summary>
    [JsonIgnore]
    public bool CanCreate => (Flag & (1 << 2)) != 0 && (Flag & (1 << 9)) == 0;

    /// <summary>
    /// 是否允许穿透「公开游戏服务器」：第 12 位为 1，或节点名里带「多线」。
    /// 官方规则要求公开发布的游戏服务器只能走这类节点。
    /// </summary>
    [JsonIgnore]
    public bool PublicGameAllowed => (Flag & 2048) != 0 || Name.Contains("多线", StringComparison.Ordinal);
}

/// <summary>创建隧道的请求参数（目前只做 TCP，Minecraft 联机够用）。</summary>
public sealed record SakuraFrpTunnelRequest
{
    public required string Name { get; init; }

    public required int NodeId { get; init; }

    public required int LocalPort { get; init; }

    public string LocalIp { get; init; } = "127.0.0.1";

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

    /// <summary>创建一条 TCP 隧道，远程端口由服务端分配。</summary>
    public async Task<SakuraFrpCreatedTunnel> CreateTcpTunnelAsync(
        string token,
        SakuraFrpTunnelRequest request,
        CancellationToken ct = default)
    {
        if (request.LocalPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "本地端口必须在 1-65535 之间");
        }

        var form = new Dictionary<string, string>
        {
            ["name"] = request.Name.Trim(),
            ["type"] = "tcp",
            ["node"] = request.NodeId.ToString(),
            ["local_ip"] = string.IsNullOrWhiteSpace(request.LocalIp) ? "127.0.0.1" : request.LocalIp.Trim(),
            ["local_port"] = request.LocalPort.ToString(),
        };

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
