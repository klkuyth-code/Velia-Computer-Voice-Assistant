using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velia.Core.Conversations;
using Velia.Core.Security;
using Velia.Core.WindowsControl;
using Velia.Protocol;

namespace Velia.Core.Providers.Live;

internal enum LiveSemanticCategory
{
    Chat,
    Reasoning,
    Action,
    Control,
}

internal enum LiveControlAction
{
    None,
    Stop,
    Progress,
    Correction,
    AcceptCompletion,
}

internal enum LiveTaskUpdateMode
{
    None,
    Append,
    Replace,
}

internal enum LiveResponseAuthority
{
    Live,
    Reasoner,
    ReasonerIfEffect,
}

internal sealed record LiveSemanticRoute(
    LiveSemanticCategory Category,
    LiveControlAction ControlAction,
    LiveTaskUpdateMode TaskUpdateMode,
    LiveResponseAuthority ResponseAuthority,
    double Confidence,
    bool RefersToActiveTask,
    string CanonicalTask,
    string Reason,
    bool Fallback,
    long DurationMilliseconds)
{
    public bool ShouldDispatch =>
        Category != LiveSemanticCategory.Chat &&
        ControlAction != LiveControlAction.AcceptCompletion &&
        !(Category != LiveSemanticCategory.Control && ResponseAuthority == LiveResponseAuthority.Live);
}

internal interface ILiveSemanticRouter
{
    Task<LiveSemanticRoute> ClassifyAsync(
        string transcript,
        DelegatedTaskSummary? activeTask,
        CancellationToken cancellationToken);
}

internal sealed class DeepSeekLiveSemanticRouter(HttpClient httpClient, ISecretStore secrets) : ILiveSemanticRouter
{
    private const string RouterModel = ProviderProfileIds.DeepSeekV4Flash;
    private const int MaximumResponseCharacters = 65_536;
    private static readonly Uri Endpoint = new("https://api.deepseek.com/v1/chat/completions", UriKind.Absolute);
    private static readonly TimeSpan RouterTimeout = TimeSpan.FromMilliseconds(3_500);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string SystemPrompt = """
        You are the semantic router for a Windows desktop voice assistant. Classify only the latest real user transcript.
        Return exactly one JSON object and no markdown.

        category:
        - chat: greetings, casual conversation, stable general knowledge, simple opinions, capability questions, or any read-only question that Gemini Live can answer from its current video, including explicitly asking to look at the current screen, read a visible question, choose an answer, explain visible content, or summarize the current画面 without operating the computer. Looking at a visible question and answering it aloud is chat.
        - reasoning: deep analysis, long-form writing, code, multi-step logic, or comprehensive formatting without changing external state.
        - action: operating a browser, screen, app, file, clipboard or system setting; fetching external webpage/weather/news/price state; reading a precise Windows text/file selection explicitly named by the user; or performing precise document extraction that cannot be answered from the current Live video. A general read-only question about content already visible in the current Live video is chat, but an explicit selected/highlighted-text request is action because it requires the local selection tool. Reading or solving a visible question and then writing, adding, inserting, or appending the derived answer into a document is always action.
        - control: stop, progress, correction, addition, redirection, or acceptance concerning the active task.

        control_action is none, stop, progress, correction, or accept_completion.
        task_update_mode is none, append, or replace. Use append for compatible extra requirements and replace for contradictions or target changes.
        response_authority is live, reasoner, or reasoner_if_effect. Chat and all read-only current-video questions use live. Operations that change external state, deep reasoning not grounded in the current video, and precise document extraction use reasoner.
        accept_completion requires an unambiguous statement that the result is correct. Any phrase saying it failed, did not open, is wrong, or asks why it failed is correction, never completion.
        Questions such as “are you doing it”, “how is it going”, or “what is the progress” are control/progress when a task is active.
        If an active task exists, preserve still-valid requirements in canonical_task without inventing actions. Otherwise canonical_task must be empty.

        Required keys:
        category, control_action, task_update_mode, response_authority, confidence, refers_to_active_task, canonical_task, reason.
        """;

    public async Task<LiveSemanticRoute> ClassifyAsync(
        string transcript,
        DelegatedTaskSummary? activeTask,
        CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var normalized = transcript.Trim();
        if (normalized.Length == 0)
            return Fallback(normalized, activeTask, "empty-transcript", started);
        if (TryClassifyObviousLiveChat(normalized, activeTask, started, out var localRoute))
            return localRoute;

        var apiKey = secrets.Load(ProviderIds.Llm)?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Fallback(normalized, activeTask, "missing-deepseek-key", started);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RouterTimeout);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new ByteArrayContent(BuildPayload(normalized, activeTask, includeThinking: attempt == 0));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                var content = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return Parse(content, normalized, activeTask, started);
                if (attempt == 0 && (int)response.StatusCode is 400 or 404 or 422) continue;
                return Fallback(normalized, activeTask, $"http-{(int)response.StatusCode}", started);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback(normalized, activeTask, "router-timeout", started);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Fallback(normalized, activeTask, "router-error", started);
        }

        return Fallback(normalized, activeTask, "router-no-result", started);
    }

    private static bool TryClassifyObviousLiveChat(
        string transcript,
        DelegatedTaskSummary? activeTask,
        long started,
        out LiveSemanticRoute route)
    {
        route = null!;
        if (activeTask is not null || transcript.Length > 600) return false;
        if (ContainsAny(transcript,
                "打开", "启动", "运行", "关闭", "退出", "关掉", "点击", "点开", "选择",
                "输入", "填写", "写入", "写到", "添加", "插入", "保存", "删除", "清空",
                "修改", "替换", "复制", "粘贴", "移动", "重命名", "发送", "下载", "上传",
                "搜索", "查找", "滚动", "滑动", "返回上一页", "标签页", "浏览器", "文件",
                "文件夹", "文档", "表格", "单元格", "剪贴板", "选中文字", "选中的",
                "音量", "静音", "计时器", "倒计时", "提醒我", "网址", "链接", "新闻",
                "天气", "股价", "价格", "汇率", "最新", "登录", "账号", "密码", "验证码",
                "支付", "购买", "发布", "深入分析", "详细分析", "写一份", "代码"))
            return false;
        route = new LiveSemanticRoute(
            LiveSemanticCategory.Chat,
            LiveControlAction.None,
            LiveTaskUpdateMode.None,
            LiveResponseAuthority.Live,
            1,
            false,
            "",
            "local:obvious-live-chat",
            true,
            Environment.TickCount64 - started);
        return true;
    }

    private static byte[] BuildPayload(
        string transcript,
        DelegatedTaskSummary? activeTask,
        bool includeThinking)
    {
        var task = activeTask is null
            ? new { active = false, goal = "", phase = "", status = "", target_application = "" }
            : new
            {
                active = true,
                goal = Limit(activeTask.Goal, 500),
                phase = Limit(activeTask.Phase, 100),
                status = Limit(activeTask.Status, 40),
                target_application = Limit(activeTask.TargetApplication ?? "", 120),
            };
        var payload = new
        {
            model = RouterModel,
            temperature = 0,
            max_tokens = 450,
            response_format = new { type = "json_object" },
            thinking = includeThinking ? new { type = "disabled" } : null,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new { active_task = task, user_input = transcript }),
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    private static LiveSemanticRoute Parse(
        string responseBody,
        string transcript,
        DelegatedTaskSummary? activeTask,
        long started)
    {
        try
        {
            using var envelope = JsonDocument.Parse(responseBody);
            var content = envelope.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content))
                return Fallback(transcript, activeTask, "empty-router-response", started);
            if (content.StartsWith("```", StringComparison.Ordinal))
            {
                content = content.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("```", "", StringComparison.Ordinal).Trim();
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!TryCategory(ReadString(root, "category"), out var category))
                return Fallback(transcript, activeTask, "invalid-router-category", started);
            var control = ParseControl(ReadString(root, "control_action"), category);
            var update = ParseUpdate(ReadString(root, "task_update_mode"), control);
            var authority = ParseAuthority(ReadString(root, "response_authority"), category);
            if (control == LiveControlAction.AcceptCompletion && IsFailedResultCorrection(transcript))
            {
                category = LiveSemanticCategory.Control;
                control = LiveControlAction.Correction;
                update = LiveTaskUpdateMode.Replace;
                authority = LiveResponseAuthority.Reasoner;
            }
            var confidence = root.TryGetProperty("confidence", out var confidenceElement) &&
                confidenceElement.TryGetDouble(out var parsedConfidence)
                ? Math.Clamp(parsedConfidence, 0, 1)
                : 0.8;
            return new LiveSemanticRoute(
                category,
                control,
                update,
                authority,
                confidence,
                ReadBoolean(root, "refers_to_active_task"),
                Limit(ReadString(root, "canonical_task"), 500),
                Limit(ReadString(root, "reason"), 200),
                false,
                Environment.TickCount64 - started);
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(transcript))
        {
            return Fallback(transcript, activeTask, "invalid-router-json", started);
        }
    }

    private static LiveSemanticRoute Fallback(
        string transcript,
        DelegatedTaskSummary? activeTask,
        string reason,
        long started)
    {
        var normalized = transcript.Trim();
        if (activeTask is not null)
        {
            if (ContainsAny(normalized, "停止", "停下", "取消", "别做了"))
                return Route(LiveSemanticCategory.Control, LiveControlAction.Stop, LiveTaskUpdateMode.None, reason, started);
            if (ContainsAny(normalized, "进度", "做到哪", "到哪一步", "还要多久"))
                return Route(LiveSemanticCategory.Control, LiveControlAction.Progress, LiveTaskUpdateMode.None, reason, started);
            return Route(
                LiveSemanticCategory.Control,
                LiveControlAction.Correction,
                ContainsAny(normalized, "然后", "接着", "补充", "另外", "再")
                    ? LiveTaskUpdateMode.Append
                    : LiveTaskUpdateMode.Replace,
                reason,
                started,
                refersToActiveTask: true);
        }

        if (DeterministicLocalTaskIntentDetector.TryGetCanonicalKey(normalized, out _) ||
            ContainsAny(normalized,
                "打开", "关闭", "点击", "点开", "输入", "填写", "滚动", "搜索", "浏览器",
                "文件", "剪贴板", "音量", "计时器", "下载", "保存", "当前屏幕", "看屏幕",
                "读取屏幕", "分析屏幕", "识别屏幕", "写入", "写到", "添加", "插入",
                "另起一行", "换行", "在末尾", "末尾写", "末尾加"))
        {
            return Route(LiveSemanticCategory.Action, LiveControlAction.None, LiveTaskUpdateMode.None, reason, started);
        }
        if (ContainsAny(normalized, "深入分析", "详细分析", "写一份", "编写", "代码", "计算", "推理"))
            return Route(LiveSemanticCategory.Reasoning, LiveControlAction.None, LiveTaskUpdateMode.None, reason, started);
        return new LiveSemanticRoute(
            LiveSemanticCategory.Chat,
            LiveControlAction.None,
            LiveTaskUpdateMode.None,
            LiveResponseAuthority.Live,
            0,
            false,
            "",
            $"fallback:{reason}",
            true,
            Environment.TickCount64 - started);
    }

    private static LiveSemanticRoute Route(
        LiveSemanticCategory category,
        LiveControlAction control,
        LiveTaskUpdateMode update,
        string reason,
        long started,
        bool refersToActiveTask = false) => new(
            category,
            control,
            update,
            LiveResponseAuthority.Reasoner,
            0,
            refersToActiveTask,
            "",
            $"fallback:{reason}",
            true,
            Environment.TickCount64 - started);

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4_096, leaveOpen: false);
        var buffer = new char[4_096];
        var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return result.ToString();
            result.Append(buffer, 0, read);
            if (result.Length > MaximumResponseCharacters)
                throw new InvalidDataException("The semantic router response is too large.");
        }
    }

    private static bool TryCategory(string value, out LiveSemanticCategory category) =>
        Enum.TryParse(value, true, out category) && Enum.IsDefined(category);

    private static LiveControlAction ParseControl(string value, LiveSemanticCategory category) =>
        Enum.TryParse<LiveControlAction>(RemoveUnderscores(value), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : category == LiveSemanticCategory.Control ? LiveControlAction.Correction : LiveControlAction.None;

    private static LiveTaskUpdateMode ParseUpdate(string value, LiveControlAction control) =>
        Enum.TryParse<LiveTaskUpdateMode>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : control == LiveControlAction.Correction ? LiveTaskUpdateMode.Replace : LiveTaskUpdateMode.None;

    private static LiveResponseAuthority ParseAuthority(string value, LiveSemanticCategory category) =>
        Enum.TryParse<LiveResponseAuthority>(RemoveUnderscores(value), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : category == LiveSemanticCategory.Chat ? LiveResponseAuthority.Live : LiveResponseAuthority.Reasoner;

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static bool ReadBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string RemoveUnderscores(string value) => value.Replace("_", "", StringComparison.Ordinal);

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsFailedResultCorrection(string value) => ContainsAny(
        value,
        "没打开", "没有打开", "打不开", "未打开", "没成功", "没有成功", "失败", "不对",
        "错误", "弄错", "做错", "为什么没", "怎么没", "还是不行", "还是没有");

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
