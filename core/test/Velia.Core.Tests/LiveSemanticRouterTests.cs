using System.Net;
using System.Text;
using System.Text.Json;
using Velia.Core.Conversations;
using Velia.Core.Providers.Live;
using Velia.Core.Security;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class LiveSemanticRouterTests
{
    [Fact]
    public async Task UsesDeepSeekV4FlashJsonRoutingAndParsesAnAction()
    {
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = JsonSerializer.Serialize(new
                        {
                            category = "action",
                            control_action = "none",
                            task_update_mode = "none",
                            response_authority = "reasoner",
                            confidence = 0.98,
                            refers_to_active_task = false,
                            canonical_task = "",
                            reason = "desktop operation",
                        }),
                    },
                },
            },
        });
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler);
        var secrets = new MemorySecrets();
        secrets.Save(ProviderIds.Llm, "private-deepseek-key");
        var router = new DeepSeekLiveSemanticRouter(client, secrets);

        var route = await router.ClassifyAsync("点击当前窗口的文件菜单", null, CancellationToken.None);

        Assert.Equal(LiveSemanticCategory.Action, route.Category);
        Assert.Equal(LiveResponseAuthority.Reasoner, route.ResponseAuthority);
        Assert.True(route.ShouldDispatch);
        Assert.False(route.Fallback);
        Assert.Equal("Bearer", handler.Request?.Headers.Authorization?.Scheme);
        Assert.Equal("private-deepseek-key", handler.Request?.Headers.Authorization?.Parameter);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(ProviderProfileIds.DeepSeekV4Flash, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("你好，请介绍一下你自己", "Chat", false)]
    [InlineData("打开记事本", "Action", true)]
    [InlineData("请详细分析当前屏幕", "Action", true)]
    [InlineData("根据当前的题目，在末尾另起一行，写入正确的答案和计算方式", "Action", true)]
    public async Task MissingKeyUsesTheSameSafeLocalFallback(
        string transcript,
        string category,
        bool dispatch)
    {
        using var client = new HttpClient(new RecordingHandler("{}"));
        var router = new DeepSeekLiveSemanticRouter(client, new MemorySecrets());

        var route = await router.ClassifyAsync(transcript, null, CancellationToken.None);

        Assert.Equal(category, route.Category.ToString());
        Assert.Equal(dispatch, route.ShouldDispatch);
        Assert.True(route.Fallback);
    }

    [Fact]
    public async Task RouterFailureTreatsAContinuationAsAnActiveTaskCorrection()
    {
        using var client = new HttpClient(new RecordingHandler("{}", HttpStatusCode.ServiceUnavailable));
        var secrets = new MemorySecrets();
        secrets.Save(ProviderIds.Llm, "key");
        var router = new DeepSeekLiveSemanticRouter(client, secrets);
        var active = new DelegatedTaskSummary("编辑当前文档", "正在编辑", "Running", "记事本");

        var route = await router.ClassifyAsync("然后点击文件", active, CancellationToken.None);

        Assert.Equal(LiveSemanticCategory.Control, route.Category);
        Assert.Equal(LiveControlAction.Correction, route.ControlAction);
        Assert.Equal(LiveTaskUpdateMode.Append, route.TaskUpdateMode);
        Assert.True(route.RefersToActiveTask);
    }

    [Fact]
    public async Task AFailedResultStatementCanNeverBeAcceptedAsCompletion()
    {
        var content = JsonSerializer.Serialize(new
        {
            category = "control",
            control_action = "accept_completion",
            task_update_mode = "none",
            response_authority = "live",
            confidence = 0.95,
            refers_to_active_task = true,
            canonical_task = "",
            reason = "user feedback",
        });
        var response = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });
        using var client = new HttpClient(new RecordingHandler(response));
        var secrets = new MemorySecrets();
        secrets.Save(ProviderIds.Llm, "key");
        var router = new DeepSeekLiveSemanticRouter(client, secrets);
        var active = new DelegatedTaskSummary("打开记事本", "任务结果已返回", "Completed", "记事本");

        var route = await router.ClassifyAsync("这次还是没有打开", active, CancellationToken.None);

        Assert.Equal(LiveControlAction.Correction, route.ControlAction);
        Assert.Equal(LiveTaskUpdateMode.Replace, route.TaskUpdateMode);
        Assert.Equal(LiveResponseAuthority.Reasoner, route.ResponseAuthority);
        Assert.True(route.ShouldDispatch);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = [];
        public bool IsConfigured(string provider) => values.ContainsKey(provider);
        public void Save(string provider, string secret) => values[provider] = secret;
        public string? Load(string provider) => values.GetValueOrDefault(provider);
        public void Remove(string provider) => values.Remove(provider);
    }

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
