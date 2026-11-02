using System.Diagnostics;
using System.Net;
using MCLauncher.Core.Network;
using MCLauncher.Core.Services;
using MCLauncher.Core.Tests.Support;

namespace MCLauncher.Core.Tests;

/// <summary>启动公告与版本检查的解析与失败降级。</summary>
public sealed class LauncherInfoServiceTests
{
    private const string NoticeJson = """
    {"id":"n1","title":"维护公告","message":"今晚 23:00 维护，请提前退出游戏","url":"https://example.com/notice"}
    """;

    private const string ReleaseJson = """
    {"tag_name":"v6.1.0","name":"6.1.0 更新","body":"改进启动速度\n修复若干问题",
     "html_url":"https://example.com/release","published_at":"2026-10-20T10:00:00Z"}
    """;

    private const string ReleaseWithAssetsJson = """
    {"tag_name":"v6.5.31","name":"6.5.31 测试版","body":"",
     "html_url":"https://example.com/release",
     "assets":[
       {"name":"notes.txt","browser_download_url":"https://example.com/notes.txt","size":12},
       {"name":"MCLauncher-Reborn.exe","browser_download_url":"https://example.com/app.exe","size":614912,
        "digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
     ]}
    """;

    private static LauncherInfoService Create(string content, out TestLog log, out HttpDownloader downloader)
    {
        log = new TestLog();
        downloader = new HttpDownloader(log, new FakeHttpHandler(content));
        return new LauncherInfoService(downloader, log);
    }

    [Fact]
    public async Task 解析公告字段()
    {
        var service = Create(NoticeJson, out _, out var downloader);
        using (downloader)
        {
            var notice = await service.FetchNoticeAsync();

            Assert.NotNull(notice);
            Assert.Equal("n1", notice!.Id);
            Assert.Equal("维护公告", notice.Title);
            Assert.Equal("今晚 23:00 维护，请提前退出游戏", notice.Message);
            Assert.Equal("https://example.com/notice", notice.Url);
        }
    }

    [Fact]
    public async Task 公告可以标记为停用()
    {
        var service = Create("""{"enabled":false,"id":"n1","title":"x"}""", out _, out var downloader);
        using (downloader)
        {
            Assert.Null(await service.FetchNoticeAsync());
        }
    }

    [Fact]
    public async Task 解析更新版本()
    {
        var service = Create(ReleaseJson, out _, out var downloader);
        using (downloader)
        {
            var update = await service.CheckUpdateAsync();

            Assert.NotNull(update);
            Assert.Equal("v6.1.0", update!.Version);
            Assert.Equal("6.1.0 更新", update.Title);
            Assert.Equal("https://example.com/release", update.Url);
            Assert.NotNull(update.PublishedAt);
        }
    }

    [Fact]
    public async Task 解析更新附件与摘要()
    {
        var service = Create(ReleaseWithAssetsJson, out _, out var downloader);
        using (downloader)
        {
            var update = await service.CheckUpdateAsync();

            Assert.NotNull(update);
            Assert.Equal(2, update!.Assets.Count);

            var asset = update.ExecutableAsset;
            Assert.NotNull(asset);
            Assert.Equal("MCLauncher-Reborn.exe", asset!.Name);
            Assert.Equal(614912, asset.Size);
            Assert.Equal(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                asset.Sha256);
        }
    }

    [Fact]
    public async Task 没有exe附件时不能自更新()
    {
        var service = Create("""{"tag_name":"v6.5.31","assets":[{"name":"app.zip","browser_download_url":"https://example.com/app.zip"}]}""", out _, out var downloader);
        using (downloader)
        {
            var update = await service.CheckUpdateAsync();

            Assert.NotNull(update);
            Assert.Null(update!.ExecutableAsset);
        }
    }

    [Fact]
    public async Task 无效内容不会抛异常()
    {
        var service = Create("not json at all", out var log, out var downloader);
        using (downloader)
        {
            Assert.Null(await service.FetchNoticeAsync());
            Assert.Null(await service.CheckUpdateAsync());
            Assert.True(log.Contains("获取启动器公告失败"));
            Assert.True(log.Contains("检查启动器更新失败"));
        }
    }

    [Theory]
    [InlineData("v6.1.0", "6.0.0", true)]
    [InlineData("6.0.0", "6.0.0", false)]
    [InlineData("6.0.0.1", "6.0.0", true)]
    [InlineData("v5.9.9", "6.0.0", false)]
    [InlineData("v6.1.0-beta", "6.0.0", true)]
    [InlineData("not-a-version", "6.0.0", false)]
    public void 版本号比较(string latest, string current, bool expected)
    {
        Assert.Equal(expected, LauncherInfoService.IsNewerVersion(latest, current));
    }

    private const string ReleasesAtom = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <title>6.5.31 测试版</title>
        <link rel="alternate" type="text/html" href="https://github.com/HotWaterFlask/MCLauncher-Reborn/releases/tag/v6.5.31"/>
        <content type="html">&lt;p&gt;用于测试更新提示&lt;/p&gt;</content>
        <updated>2026-10-29T13:35:54Z</updated>
      </entry>
    </feed>
    """;

    [Fact]
    public async Task REST被限流时退回Atom源()
    {
        var log = new TestLog();
        using var downloader = new HttpDownloader(log, new AtomFallbackHandler(ReleasesAtom));
        var service = new LauncherInfoService(downloader, log);

        var update = await service.CheckUpdateAsync();

        Assert.NotNull(update);
        Assert.Equal("v6.5.31", update!.Version);
        Assert.Equal("6.5.31 测试版", update.Title);
        Assert.Equal("用于测试更新提示", update.Notes);
        Assert.Empty(update.Assets);
        Assert.Contains("改读 releases.atom", string.Join(" ", log.Lines));
    }

    [Fact]
    public async Task 公告在官方源卡住时立刻改用镜像()
    {
        var log = new TestLog();
        using var downloader = new HttpDownloader(log, new HangingOfficialHandler(NoticeJson));
        var service = new LauncherInfoService(downloader, log);

        var started = Stopwatch.StartNew();
        var notice = await service.FetchNoticeAsync();
        started.Stop();

        Assert.NotNull(notice);
        Assert.Equal("维护公告", notice!.Title);
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(3),
            $"公告应与镜像并行竞速、立刻返回，实际耗时 {started.Elapsed}");
    }

    /// <summary>API 一律 403（模拟限流），其它地址正常返回 Atom。</summary>
    private sealed class AtomFallbackHandler : HttpMessageHandler
    {
        private readonly string _atom;

        public AtomFallbackHandler(string atom) => _atom = atom;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Contains("api.github.com") == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_atom),
            });
        }
    }

    /// <summary>官方 raw 一直挂起（模拟被墙），站点/镜像正常返回内容。</summary>
    private sealed class HangingOfficialHandler : HttpMessageHandler
    {
        private readonly string _content;

        public HangingOfficialHandler(string content) => _content = content;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Contains("raw.githubusercontent.com") == true)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_content),
            };
        }
    }
}
