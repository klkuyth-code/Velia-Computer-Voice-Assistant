using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Velia.Core.Conversations;
using Velia.Core.DesktopVisual;
using Velia.Core.DesktopContext;
using Velia.Core.Documents;
using Velia.Core.Providers;
using Velia.Core.Providers.Chat;
using Velia.Core.Rpc;
using Velia.Core.Security;
using Velia.Core.Tasks;
using Velia.Core.WindowsControl;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ConversationServiceTests
{
    [Fact]
    public async Task SafeVoiceChatPublishesContentBeforeProviderCompletionWithoutTools()
    {
        var provider = new GatedStreamingProvider("你好，我在这里。");
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var submitted = await service.SubmitAsync("你好，最近怎么样", "voice", "auto", CancellationToken.None);
        var content = await sink.WaitForTypeAsync(CoreEventTypes.Content);

        Assert.Equal(submitted.OperationId, content.OperationId);
        Assert.Equal("你好，我在这里。", content.Delta);
        Assert.Empty(Assert.Single(provider.Requests).Tools ?? []);
        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Completed);

        provider.ReleaseCompletion.TrySetResult();
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync()).Type);
        Assert.Equal("你好，我在这里。", string.Concat(sink.Events
            .Where(static item => item.Type == CoreEventTypes.Content)
            .Select(static item => item.Delta)));
    }

    [Fact]
    public async Task ExternalStateVoiceRequestStaysBufferedAndKeepsConversationTools()
    {
        var provider = new GatedStreamingProvider("需要先确认外部状态。");
        var sink = new RecordingEventSink();
        using var service = CreateService(
            provider,
            sink,
            enableDeterministicFastPath: false);

        await service.SubmitAsync("请详细分析今天的天气", "voice", "auto", CancellationToken.None);
        await provider.DeltaProduced.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Content);
        Assert.NotEmpty(Assert.Single(provider.Requests).Tools ?? []);

        provider.ReleaseCompletion.TrySetResult();
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync()).Type);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta == "需要先确认外部状态。");
    }

    [Fact]
    public async Task DelegatedLiveTaskUsesTheSameConversationPipelineAndReturnsItsFinalAnswer()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(
                    ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "call_live_1",
                        "browser",
                        "{\"method\":\"open\",\"params\":{\"url\":\"https://example.com/search?q=Velia\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "任务执行完成"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var delegated = service.ExecuteDelegatedTaskAsync("打开浏览器并搜索 Velia", CancellationToken.None);
        var toolEvent = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        var submitted = service.SubmitToolResult(new SubmitToolResultParams(
            toolEvent.OperationId,
            toolEvent.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"opened\":true}").RootElement.Clone()));
        var result = await delegated;

        Assert.True(submitted.Accepted);
        Assert.True(result.Ok);
        Assert.Equal("任务执行完成", result.Value);
        Assert.All(sink.Events, static item => Assert.Equal("live", item.Origin));
        Assert.Equal("live", sink.Events.Single(static item =>
            item.Type == CoreEventTypes.Mission && item.Mission?.State == MissionLifecycleStates.Running).Mission?.Origin);
        Assert.Equal(
            ["browser", "desktop_context", "document_context", "screen_vision", "desktop_visual", "windows_control", "web_research", "mission_control"],
            provider.Requests[0].Tools!.Select(static tool => tool.Name));
    }

    [Fact]
    public async Task StreamsOrderedEventsWithoutExposingReasoningText()
    {
        var provider = new ScriptedProvider(
        [
            new ChatStreamEvent(ChatStreamEventKind.ReasoningDelta, "private reasoning"),
            new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "你"),
            new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "好"),
            new ChatStreamEvent(
                ChatStreamEventKind.Completed,
                FinishReason: "stop",
                Usage: new ChatUsage(3, 2, 5)),
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("测试");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal(operation.OperationId, terminal.OperationId);
        Assert.All(sink.Events, static item => Assert.Equal("text", item.Origin));
        Assert.Equal(
            ["accepted", "mission", "state", "reasoning", "state", "content", "content", "mission", "completed"],
            sink.Events.Select(static item => item.Type).ToArray());
        Assert.Equal(Enumerable.Range(1, sink.Events.Length).Select(static value => (long)value),
            sink.Events.Select(static item => item.Sequence));
        Assert.Null(sink.Events.Single(static item => item.Type == "reasoning").Delta);
        Assert.Equal("你好", string.Concat(sink.Events.Where(static item => item.Type == "content").Select(static item => item.Delta)));
        Assert.Equal(5, terminal.Usage?.TotalTokens);
    }

    [Fact]
    public async Task CancelsOnlyTheMatchingActiveOperation()
    {
        var provider = new WaitingProvider();
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);
        var operation = service.Submit("等待");
        await sink.WaitForTypeAsync("accepted");

        Assert.False(service.Cancel(Guid.NewGuid()).Accepted);
        Assert.True(service.Cancel(operation.OperationId).Accepted);
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("cancelled", terminal.Type);
        Assert.False(service.Cancel(operation.OperationId).Accepted);
    }

    [Fact]
    public async Task ExecutesABoundedToolRoundAndContinuesWithTheVerifiedResult()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "call_1", "browser", "{\"method\":\"getPage\",\"params\":{}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "页面标题是测试页"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("读取当前页面标题");
        var toolEvent = await sink.WaitForTypeAsync("tool");
        var result = service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            toolEvent.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"page\":{\"title\":\"测试页\"}}").RootElement.Clone()));
        var terminal = await sink.WaitForTerminalAsync();

        Assert.True(result.Accepted);
        Assert.Equal("completed", terminal.Type);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(["browser", "desktop_context", "document_context", "screen_vision", "desktop_visual", "windows_control", "web_research", "mission_control"], provider.Requests[0].Tools!.Select(static tool => tool.Name));
        var continuation = provider.Requests[1].Messages;
        Assert.Contains(continuation, static message => message.Role == ChatRole.Assistant && message.ToolCalls?.Single().Id == "call_1");
        Assert.Contains(continuation, static message => message.Role == ChatRole.Tool && message.ToolCallId == "call_1" && message.Content.Contains("title"));
        Assert.Equal("页面标题是测试页", string.Concat(sink.Events.Where(static item => item.Type == "content").Select(static item => item.Delta)));
    }

    [Fact]
    public async Task RejectsAnUnknownToolBeforeWaitingForDesktopExecution()
    {
        var provider = new ScriptedProvider(
        [
            new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                ToolCall: new ChatToolCallDelta(0, "call_2", "run_shell", "{}")),
            new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("运行命令");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("failed", terminal.Type);
        Assert.Equal("unknown_tool", terminal.Error?.Code);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task ExecutesDesktopContextOnlyForAnExplicitSelectionRequest()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "context_1",
                        "desktop_context",
                        "{\"method\":\"capture\",\"mode\":\"selection\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "你选中了测试文字。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("读取我选中的文字");
        var toolEvent = await sink.WaitForTypeAsync("tool");
        Assert.Equal("desktop_context", toolEvent.ToolCall?.Name);
        var accepted = service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            toolEvent.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"kind\":\"selectedText\",\"text\":\"测试文字\"}").RootElement.Clone()));
        await sink.WaitForTerminalAsync();

        Assert.True(accepted.Accepted);
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "context_1" && message.Content.Contains("selectedText"));
    }

    [Fact]
    public async Task AcceptsAQualifiedSelectionPhraseFromVoiceTranscription()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "voice_context_1",
                        "desktop_context",
                        "{\"method\":\"capture\",\"mode\":\"selection\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已读取三行内容。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("请读取我选取的三段文字。");
        var toolEvent = await sink.WaitForTypeAsync("tool");
        Assert.Equal("desktop_context", toolEvent.ToolCall?.Name);
    }

    [Fact]
    public async Task DeniesDesktopContextWhenTheUserDidNotRequestIt()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "context_denied",
                        "desktop_context",
                        "{\"method\":\"capture\",\"mode\":\"clipboard\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有读取本机内容。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("介绍一下今天的安排");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "context_denied");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task DeniesDesktopVisualWhenTheUserDidNotRequestDesktopOperation()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_denied", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"确定按钮\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有操作桌面。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("告诉我今天星期几");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "visual_denied");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task ReadingTheCurrentWindowDoesNotAuthorizeAVisualClick()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_read_only", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"确定按钮\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有点击。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("帮我看看当前窗口显示了什么");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.Content.Contains("tool_consent_denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowsFreshReadOnlyScreenVisionForAnExplicitCurrentScreenQuestion()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "screen_read", "screen_vision", "{\"question\":\"读出当前屏幕上的三行文字\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "屏幕上显示三行测试文字。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var operation = service.Submit("请读出当前屏幕上的三行文字");
        var tool = await sink.WaitForToolAsync("screen_read");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"analysis\":\"第一行\\n第二行\\n第三行\",\"evidence\":\"freshCurrentScreen\"}").RootElement.Clone())).Accepted);

        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Fact]
    public async Task ReadsOneSelectedDocumentThroughTheExistingContextWithoutExposingAPath()
    {
        const string contextId = "2196ea21-1692-46a8-b87b-92c608429ead";
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "capture_document", "desktop_context", "{\"method\":\"capture\",\"mode\":\"selection\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "read_document", "document_context", $"{{\"contextId\":\"{contextId}\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "文档总结完成。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var operation = service.Submit("总结我当前选中的 PDF 文档");
        var capture = await sink.WaitForToolAsync("capture_document");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            capture.ToolCall!.Id!,
            true,
            JsonDocument.Parse($"{{\"contextId\":\"{contextId}\",\"kind\":\"selectedFiles\",\"files\":[{{\"name\":\"报告.pdf\"}}]}}").RootElement.Clone())).Accepted);
        var document = await sink.WaitForToolAsync("read_document");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            document.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"name\":\"报告.pdf\",\"text\":\"正文\",\"evidence\":\"selectedDocumentLocalExtraction\"}").RootElement.Clone())).Accepted);

        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
        Assert.DoesNotContain(provider.Requests.SelectMany(static request => request.Messages), static message =>
            message.Content.Contains("C:\\\\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectedFileMetadataDoesNotAuthorizeReadingDocumentContents()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "document_overreach", "document_context",
                        "{\"contextId\":\"2196ea21-1692-46a8-b87b-92c608429ead\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有读取文档正文。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        service.Submit("告诉我当前选中的文件有哪些");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Tool);
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.ToolCallId == "document_overreach" &&
            message.Content.Contains("tool_consent_denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitScreenVisionDenialNeverCapturesTheScreen()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "screen_denied", "screen_vision",
                        "{\"question\":\"分析当前屏幕\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有读取屏幕。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        service.Submit("不要分析当前屏幕，只回答今天星期几");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Tool);
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.ToolCallId == "screen_denied" &&
            message.Content.Contains("tool_consent_denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptsABoundedDesktopVisualBatchForOneExplicitCommand()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_batch", "desktop_visual",
                        "{\"actions\":[{\"action\":\"click\",\"target\":\"文件\"},{\"action\":\"click\",\"target\":\"保存\"}]}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "连续动作已完成。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var operation = service.Submit("请在当前窗口点击文件，然后点击保存");
        var tool = await sink.WaitForToolAsync("visual_batch");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"actionCount\":2}").RootElement.Clone())).Accepted);

        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Fact]
    public async Task GroundsALowRiskFollowUpAgainstTheBoundMissionInsteadOfOnlyTheLatestUtterance()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "mission_menu", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"文件菜单\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "文件菜单已展开。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "mission_follow_up", "desktop_visual",
                        "{\"actions\":[{\"action\":\"click\",\"target\":\"文件菜单\"},{\"action\":\"click\",\"target\":\"保存\"}]}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "保存动作已完成。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var first = service.Submit("请在当前窗口的记事本中点击文件菜单");
        var firstTool = await sink.WaitForToolAsync("mission_menu");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            first.OperationId,
            firstTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);
        await sink.WaitForTerminalAsync(1);

        var second = service.Submit("然后点击保存");
        var followUpTool = await sink.WaitForToolAsync("mission_follow_up");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            second.OperationId,
            followUpTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"actionCount\":2}").RootElement.Clone())).Accepted);
        await sink.WaitForTerminalAsync(2);

        Assert.Equal(first.Mission.MissionId, second.Mission.MissionId);
        Assert.DoesNotContain(provider.Requests[3].Messages, static message =>
            message.ToolCallId == "mission_follow_up" &&
            message.Content.Contains("desktop_visual_action_not_grounded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAnUngroundedExtraDesktopActionInABatch()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_overreach", "desktop_visual",
                        "{\"actions\":[{\"action\":\"click\",\"target\":\"文件\"},{\"action\":\"click\",\"target\":\"保存\"}]}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "只执行用户明确要求的动作。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        service.Submit("点击文件");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Tool);
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.ToolCallId == "visual_overreach" &&
            message.Content.Contains("desktop_visual_action_not_grounded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowsDesktopVisualForAnExplicitCurrentWindowAction()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_allowed", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"确定按钮\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已经验证点击成功。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("请在当前窗口中点击确定按钮");
        var tool = await sink.WaitForToolAsync("visual_allowed");
        var accepted = service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"evidence\":\"确认页面已打开\"}").RootElement.Clone()));
        var terminal = await sink.WaitForTerminalAsync();

        Assert.True(accepted.Accepted);
        Assert.Equal("completed", terminal.Type);
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "visual_allowed" && message.Content.Contains("verified", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("根据当前的题目，在末尾另起一行，写入正确的答案和计算方式")]
    [InlineData("根据当前的，在末尾另起一行，然后写入的答案和计算方式")]
    public async Task AllowsScreenDerivedTextOnlyAfterFreshVisionEvidenceAndVerifiedWrite(string request)
    {
        const string generatedAnswer = "答案：A，198元；200×1.1×0.9=198";
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "screen_for_answer", "screen_vision",
                        "{\"question\":\"读取当前记事本里的题目和选项并计算正确答案\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "write_derived_answer", "desktop_visual",
                        JsonSerializer.Serialize(new
                        {
                            action = "type",
                            target = "文档末尾",
                            text = generatedAnswer,
                        }))),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "正确答案和计算式已写入并经过本机验证。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(
            provider,
            sink,
            enableDeterministicFastPath: false);

        var operation = service.Submit(request);
        var screen = await sink.WaitForToolAsync("screen_for_answer");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            screen.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"analysis\":\"A，198元；200×1.1×0.9=198\",\"evidence\":\"freshCurrentScreen\"}")
                .RootElement.Clone())).Accepted);
        var write = await sink.WaitForToolAsync("write_derived_answer");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            write.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"state\":\"typed\"}")
                .RootElement.Clone())).Accepted);
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("completed", terminal.Type);
        Assert.DoesNotContain(provider.Requests.SelectMany(static request => request.Messages), static message =>
            message.Content.Contains("desktop_visual_action_not_grounded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("请根据当前题目，在末尾另起一行，输入正确的答案。")]
    [InlineData("根据当前的题目，在当前文档末尾写入正确答案和计算方式")]
    public async Task ScreenDerivedDocumentWriteUsesVerifiedCompositeFastPath(string request)
    {
        var provider = new ScriptedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var operation = service.Submit(request);
        var tool = await sink.WaitForTypeAsync("tool");

        Assert.Equal("screen_document_write", tool.ToolCall?.Name);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
            Assert.Equal(request, arguments.RootElement.GetProperty("request").GetString());
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"verification\":\"uiaTextAppended\"}")
                .RootElement.Clone())).Accepted);

        var terminal = await sink.WaitForTerminalAsync();
        Assert.Equal("completed", terminal.Type);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains(sink.Events, static item =>
            item.Type == "content" && item.Delta == "正确答案已经写入当前文档末尾。");
    }

    [Fact]
    public async Task ScreenDerivedDocumentWriteDoesNotReportSuccessWithoutNativeVerification()
    {
        var provider = new ScriptedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var operation = service.Submit("请根据当前题目，在末尾另起一行，输入正确的答案。");
        var tool = await sink.WaitForTypeAsync("tool");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            false,
            Error: new RpcError("desktop_visual_append_unverified", "未验证", true))).Accepted);

        var terminal = await sink.WaitForTerminalAsync();
        Assert.Equal("failed", terminal.Type);
        Assert.Equal("desktop_visual_append_unverified", terminal.Error?.Code);
        Assert.Equal("未验证", terminal.Error?.Message);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "content");
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RejectsScreenDerivedWriteWithoutFreshVisionEvidenceAndNeverClaimsCompletion()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "write_without_screen", "desktop_visual",
                        "{\"action\":\"type\",\"target\":\"文档末尾\",\"text\":\"答案：A，198元\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已经写入。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        using var service = CreateService(
            provider,
            sink,
            enableDeterministicFastPath: false);

        service.Submit("根据当前记事本里的题目，在文档末尾输入正确答案和计算式");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("failed", terminal.Type);
        Assert.Equal("local_mutation_unverified", terminal.Error?.Code);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Content.Contains("desktop_visual_action_not_grounded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("点击文件")]
    [InlineData("然后点击文件")]
    [InlineData("请帮我点击文件")]
    public async Task AllowsAnExplicitDirectDesktopActionWithoutRepeatingCurrentWindow(string request)
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_direct", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"文件菜单\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "文件菜单已展开。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit(request);
        var tool = await sink.WaitForToolAsync("visual_direct");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true,\"state\":\"menuExpanded\"}").RootElement.Clone())).Accepted);

        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Theory]
    [InlineData("不要点击文件")]
    [InlineData("为什么点击文件失败了")]
    [InlineData("点击文件是什么意思")]
    public async Task DoesNotTreatNegationOrAQuestionAsDirectDesktopAuthorization(string request)
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_direct_denied", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"文件菜单\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行桌面操作。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit(request);
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.Content.Contains("tool_consent_denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptsAnAsrTruncatedCurrentWindowPhrase()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "visual_asr", "desktop_visual",
                        "{\"action\":\"click\",\"target\":\"编辑菜单\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已完成。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("前窗口点击记事本的编辑功能");
        var tool = await sink.WaitForToolAsync("visual_asr");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);
        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Theory]
    [InlineData("{\"action\":\"click\",\"target\":\"删除账号按钮\"}")]
    [InlineData("{\"action\":\"click\",\"target\":\"立即付款按钮\"}")]
    [InlineData("{\"action\":\"type\",\"target\":\"普通输入框\",\"text\":\"sk-abcdefghijklmnop\"}")]
    public async Task RejectsSensitiveDesktopVisualActionsBeforePublishingThem(string arguments)
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "visual_sensitive", "desktop_visual", arguments)),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行敏感操作。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("请在当前窗口中点击对应按钮");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "visual_sensitive");
        Assert.Contains("desktop_visual_sensitive_action", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task AllowsVerifiedReplacementOnlyAfterAnExplicitSelectionMutationRequest()
    {
        const string contextId = "2196ea21-1692-46a8-b87b-92c608429ead";
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "capture_replace", "desktop_context",
                        "{\"method\":\"capture\",\"mode\":\"selection\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0, "replace_1", "desktop_context",
                        $"{{\"method\":\"replaceSelection\",\"contextId\":\"{contextId}\",\"text\":\"新文字\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已验证替换成功。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("把我选取的文字替换为新文字");
        var capture = await sink.WaitForToolAsync("capture_replace");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            capture.ToolCall!.Id!,
            true,
            JsonDocument.Parse($"{{\"contextId\":\"{contextId}\",\"kind\":\"selectedText\",\"text\":\"原文字\"}}").RootElement.Clone())).Accepted);
        var replace = await sink.WaitForToolAsync("replace_1");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            replace.ToolCall!.Id!,
            true,
            JsonDocument.Parse($"{{\"contextId\":\"{contextId}\",\"replaced\":true,\"verified\":true}}").RootElement.Clone())).Accepted);
        await sink.WaitForTerminalAsync();

        Assert.Equal("completed", sink.Events.Last().Type);
        Assert.Equal(2, sink.Events.Count(static item => item.Type == "tool"));
    }

    [Fact]
    public async Task DeniesSelectionReplacementWithoutExplicitMutationIntent()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "replace_denied",
                        "desktop_context",
                        "{\"method\":\"replaceSelection\",\"contextId\":\"2196ea21-1692-46a8-b87b-92c608429ead\",\"text\":\"新文字\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有修改选中文字。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("读取我选中的文字");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "replace_denied");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task AllowsTheExplicitlyRequestedSelectedFileOperation()
    {
        const string contextId = "2196ea21-1692-46a8-b87b-92c608429ead";
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "move_files",
                        "desktop_context",
                        $"{{\"method\":\"operateSelectedFiles\",\"contextId\":\"{contextId}\",\"action\":\"moveToSelectedFolder\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "移动结果已经验证。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("把我选中的文件移动到选中的文件夹");
        var tool = await sink.WaitForToolAsync("move_files");
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse($"{{\"operationId\":\"{operation.OperationId}\",\"contextId\":\"{contextId}\",\"action\":\"moveToSelectedFolder\",\"verified\":true,\"files\":[]}}").RootElement.Clone())).Accepted);
        await sink.WaitForTerminalAsync();

        Assert.Equal("completed", sink.Events.Last().Type);
    }

    [Fact]
    public async Task DeniesAFileOperationDifferentFromTheUsersExplicitRequest()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "wrong_file_action",
                        "desktop_context",
                        "{\"method\":\"operateSelectedFiles\",\"contextId\":\"2196ea21-1692-46a8-b87b-92c608429ead\",\"action\":\"moveToSelectedFolder\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行移动。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("把我选中的文件复制到选中的文件夹");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "wrong_file_action");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task ReturnsMalformedBrowserArgumentsToTheModelWithoutExecutingThem()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "我先尝试操作。"),
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "call_bad", "browser", "{\"method\":\"act\",\"params\":")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "call_retry", "browser", "{\"method\":\"getPage\",\"params\":{}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已完成"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var operation = service.Submit("读取页面");
        var toolEvent = await sink.WaitForTypeAsync("tool");
        Assert.Equal("call_retry", toolEvent.ToolCall?.Id);
        var result = service.SubmitToolResult(new SubmitToolResultParams(
            operation.OperationId,
            toolEvent.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"page\":{\"title\":\"测试页\"}}").RootElement.Clone()));
        var terminal = await sink.WaitForTerminalAsync();

        Assert.True(result.Accepted);
        Assert.Equal("completed", terminal.Type);
        Assert.Equal(3, provider.Requests.Count);
        var retryRequest = provider.Requests[1].Messages;
        var validationResult = Assert.Single(retryRequest, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "call_bad");
        Assert.Contains("invalid_tool_arguments", validationResult.Content, StringComparison.Ordinal);
        Assert.Contains("method", validationResult.Content, StringComparison.Ordinal);
        Assert.Single(sink.Events, static item => item.Type == "tool");
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool" && item.ToolCall?.Id == "call_bad");
        Assert.Equal("已完成", string.Concat(sink.Events
            .Where(static item => item.Type == "content")
            .Select(static item => item.Delta)));
    }

    [Fact]
    public async Task ReturnsUnsupportedBrowserMethodsToTheModelWithoutDesktopExecution()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "call_rejected", "browser", "{\"method\":\"executeScript\",\"params\":{}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "无法执行该方法"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("执行浏览器动作");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("completed", terminal.Type);
        Assert.Equal(2, provider.Requests.Count);
        var validationResult = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "call_rejected");
        Assert.Contains("browser_method_rejected", validationResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task DeniesBrowserMutationWhenTheUserDidNotExplicitlyRequestBrowserWork()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "call_open", "browser", "{\"method\":\"open\",\"params\":{\"url\":\"https://example.com\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行浏览器动作。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("帮我处理一下这件事");
        await sink.WaitForTerminalAsync();

        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "call_open");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
    }

    [Fact]
    public async Task ExecutesAllowlistedWindowsControlDirectlyInsideCore()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "open_notepad", "windows_control", "{\"action\":\"openApplication\",\"applicationId\":\"notepad\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "记事本已经打开。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var windows = new FakeWindowsControlService();
        var service = CreateService(provider, sink, windows);

        service.Submit("打开记事本");
        await sink.WaitForTerminalAsync();

        var command = Assert.Single(windows.Commands);
        Assert.Equal(WindowsControlActions.OpenApplication, command.Action);
        Assert.Equal(WindowsApplicationIds.Notepad, command.ApplicationId);
        Assert.Contains(sink.Events, static item => item.Type == "tool" && item.ToolCall?.Name == "windows_control");
        Assert.Empty(provider.Requests);
        Assert.Equal("测试应用已经打开。", string.Concat(sink.Events
            .Where(static item => item.Type == CoreEventTypes.Content)
            .Select(static item => item.Delta)));
    }

    [Fact]
    public async Task LiveDeterministicWindowsCommandBypassesMissionAndModelPipelines()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var windows = new FakeWindowsControlService();
        var service = CreateService(provider, sink, windows);

        var result = await service.ExecuteDeterministicWindowsTaskAsync(
            "打开记事本。打开记事本。Hi, Bella。你好，喂。",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("测试应用已经打开。", result.Value);
        var command = Assert.Single(windows.Commands);
        Assert.Equal(WindowsControlActions.OpenApplication, command.Action);
        Assert.Equal(WindowsApplicationIds.Notepad, command.ApplicationId);
        Assert.Empty(provider.Requests);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task RoutedLiveHomepageCommandUsesTheBrowserBridgeAndVerifiesTheFinalHost()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var windows = new FakeWindowsControlService();
        using var service = CreateService(provider, sink, windows);

        var execution = service.ExecuteRoutedTaskAsync(
            "用谷歌浏览器打开百度首页",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);

        Assert.Equal("browser", tool.ToolCall?.Name);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("open", arguments.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                "https://www.baidu.com/",
                arguments.RootElement.GetProperty("params").GetProperty("url").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"finalPage":{"ok":true,"page":{"url":"https://www.baidu.com/","title":"百度一下"}}}
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal("已打开百度首页。", result.Value);
        Assert.Empty(windows.Commands);
        Assert.Empty(provider.Requests);
        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Fact]
    public async Task RoutedLiveBackCommandUsesVerifiedBrowserHistoryWithoutModelPlanning()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var execution = service.ExecuteRoutedTaskAsync(
            "返回上一页",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("act", arguments.RootElement.GetProperty("method").GetString());
            var action = arguments.RootElement.GetProperty("params").GetProperty("actions")[0];
            Assert.Equal("go_back", action.GetProperty("type").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"go_back","ok":true}],"finalPage":{"page":{"url":"https://www.baidu.com/"}}}
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal("已返回上一页。", result.Value);
        Assert.Empty(provider.Requests);
        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Fact]
    public async Task RoutedLiveScrollRequiresRealPositionEvidence()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var execution = service.ExecuteRoutedTaskAsync(
            "滑动页面到底部",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"scroll","ok":true,"result":{"ok":true,"action":{"type":"scroll","position":"bottom","beforeScrollTop":0,"afterScrollTop":3900,"scrollHeight":4900,"viewportHeight":1000,"moved":true,"targetReached":true}}}],"finalPage":{"page":{"url":"https://www.google.com/search?q=ai","scrollTop":3900,"scrollHeight":4900,"viewportHeight":1000}}}
                """).RootElement.Clone())).Accepted);

        var result = await execution;
        Assert.True(result.Ok);
        Assert.Equal("已滑动到页面底部。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task PaginationClickUsesAccessibleMetadataAndRequiresNavigation()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var search = service.ExecuteRoutedTaskAsync(
            "用谷歌浏览器搜索人工智能",
            "auto",
            CancellationToken.None);
        var searchTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            searchTool.OperationId,
            searchTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.google.com/search?q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD","title":"人工智能 - Google 搜索"}}
                """).RootElement.Clone())).Accepted);
        Assert.True((await search).Ok);

        var pagination = service.ExecuteRoutedTaskAsync("点击第3页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.google.com/search?q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD","elements":[
                  {"ref":"v333333333333","tag":"a","text":"Page 3 3","ariaLabel":"Page 3","href":"https://www.google.com/search?q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD&start=20"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var clickTool = await sink.WaitForToolOrdinalAsync(3);
        using (var arguments = JsonDocument.Parse(clickTool.ToolCall!.ArgumentsFragment!))
        {
            var action = arguments.RootElement.GetProperty("params").GetProperty("actions")[0];
            Assert.Equal("navigate", action.GetProperty("type").GetString());
            Assert.Contains("start=20", action.GetProperty("url").GetString(), StringComparison.Ordinal);
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            clickTool.OperationId,
            clickTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true}],"finalPage":{"page":{"url":"https://www.google.com/search?q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD&start=20"}}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“第3页”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task PaginationAcceptsReconciledTabWhenTrackingParametersChange()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("点击第6页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.google.com/search?q=ai&start=20&ved=old","elements":[
                  {"ref":"v666666666666","tag":"a","text":"6","ariaLabel":"Page 6","href":"https://www.google.com/search?q=ai&start=50&ved=target"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true}],"finalPage":{"ok":true,"verification":"navigationCommitted","page":{"url":"https://www.google.com/search?q=ai&start=20&ved=stale"}},"verifiedTab":{"id":7,"active":true,"url":"https://www.google.com/search?q=ai&start=50&ved=actual"}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“第6页”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task PaginationAcceptsCommittedStepUrlWhenReconciliationIsDeferred()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("点击第3页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.google.com/search?q=ai","elements":[
                  {"ref":"v333333333333","tag":"a","text":"3","ariaLabel":"Page 3","href":"https://www.google.com/search?q=ai&start=20&ved=target"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true,"fromUrl":"https://www.google.com/search?q=ai","toUrl":"https://www.google.com/search?q=ai&start=20&ved=actual"}],"reconciliationDeferred":true}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“第3页”。", result.Value);
    }

    [Fact]
    public async Task PaginationAcceptsSamePathCanonicalRedirectAfterCommittedNavigate()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("点击第3页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://example.com/search?q=ai&page=2","elements":[
                  {"ref":"v333333333333","tag":"a","text":"3","ariaLabel":"Page 3","href":"https://example.com/search?q=ai&page=3"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true,"fromUrl":"https://example.com/search?q=ai","toUrl":"https://example.com/search?q=ai"}],"verifiedTab":{"id":7,"active":true,"url":"https://example.com/search?q=ai"}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“第3页”。", result.Value);
    }

    [Fact]
    public async Task PaginationRejectsReconciledTabForTheWrongPage()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("点击第6页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://example.com/search?page=2","elements":[
                  {"ref":"v666666666666","tag":"a","text":"6","ariaLabel":"Page 6","href":"https://example.com/search?page=6"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true}],"verifiedTab":{"id":7,"active":true,"url":"https://example.com/search?page=5"}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        var terminal = await sink.WaitForTerminalAsync();
        Assert.False(result.Ok);
        Assert.Equal("failed", terminal.Type);
        Assert.Equal("browser_pagination_unverified", terminal.Error?.Code);
    }

    [Fact]
    public async Task RepeatedPaginationCommandSucceedsWhenTargetPageIsAlreadyActive()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("点击第6页", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://example.com/search?page=6","elements":[
                  {"ref":"v666666666666","tag":"a","text":"6","ariaLabel":"Page 6","href":"https://example.com/search?page=6"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true}],"verifiedTab":{"id":7,"active":true,"url":"https://example.com/search?page=6"}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“第6页”。", result.Value);
    }

    [Fact]
    public async Task NaturalNextPageCommandUsesStandardPaginationRelationship()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var pagination = service.ExecuteRoutedTaskAsync("下一个页面", "auto", CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://example.com/search?page=2","elements":[
                  {"ref":"v333333333333","tag":"a","text":"Next","ariaLabel":"Next","rel":"next","href":"https://example.com/search?page=3"}
                ]}}
                """).RootElement.Clone())).Accepted);
        var actionTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            actionTool.OperationId,
            actionTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"navigate","ok":true}],"verifiedTab":{"id":7,"active":true,"url":"https://example.com/search?page=3"}}
                """).RootElement.Clone())).Accepted);

        var result = await pagination;
        Assert.True(result.Ok);
        Assert.Equal("已点击“下一页”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RoutedLiveNamedTabCloseCarriesExplicitSingleTabAuthorization()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var execution = service.ExecuteRoutedTaskAsync(
            "关闭人工智能这个标签页",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("act", arguments.RootElement.GetProperty("method").GetString());
            var parameters = arguments.RootElement.GetProperty("params");
            Assert.True(parameters.GetProperty("userExplicitTabClose").GetBoolean());
            var action = parameters.GetProperty("actions")[0];
            Assert.Equal("close_tab", action.GetProperty("type").GetString());
            Assert.Equal("人工智能", action.GetProperty("titleContains").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"close_tab","ok":true,"tabId":17}],"closedTabId":17,"closedTabTitle":"人工智能_百度搜索"}
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal("已关闭“人工智能”标签页。", result.Value);
        Assert.Empty(provider.Requests);
        Assert.Equal("completed", (await sink.WaitForTerminalAsync()).Type);
    }

    [Fact]
    public async Task HomepageCommandNeverReportsSuccessForTheWrongBrowserHost()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        service.Submit("打开百度首页");
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://example.com/"}}
                """).RootElement.Clone())).Accepted);

        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("failed", terminal.Type);
        Assert.Equal("browser_navigation_unverified", terminal.Error?.Code);
        Assert.DoesNotContain(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta?.Contains("已打开", StringComparison.Ordinal) == true);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task KnownSiteSearchUsesAStableUrlAndReturnsAnExplicitVerifiedAcknowledgement()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var execution = service.ExecuteRoutedTaskAsync(
            "打开抖音，搜索罗永浩",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal(
                "https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9",
                arguments.RootElement.GetProperty("params").GetProperty("url").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9"}}
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal("已在抖音搜索“罗永浩”。", result.Value);
        Assert.Null(service.GetActiveTaskSummary());
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task NaturalSearchFollowUpReusesTheCompletedPlatformContextWithoutModelPlanning()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var open = service.ExecuteRoutedTaskAsync("打开抖音", "auto", CancellationToken.None);
        var openTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            openTool.OperationId,
            openTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.douyin.com/"}}
                """).RootElement.Clone())).Accepted);
        Assert.True((await open).Ok);
        Assert.Equal("抖音", service.GetCurrentWebPlatform());

        var search = service.ExecuteRoutedTaskAsync("搜索罗永浩", "auto", CancellationToken.None);
        var searchTool = await sink.WaitForToolOrdinalAsync(2);
        using (var arguments = JsonDocument.Parse(searchTool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("open", arguments.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                "https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9",
                arguments.RootElement.GetProperty("params").GetProperty("url").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            searchTool.OperationId,
            searchTool.ToolCall.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9"}}
                """).RootElement.Clone())).Accepted);

        var result = await search;

        Assert.True(result.Ok);
        Assert.Equal("已在抖音搜索“罗永浩”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task KnownSiteSearchRejectsALoginRedirectInsteadOfClaimingSearchSucceeded()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var execution = service.ExecuteRoutedTaskAsync(
            "打开抖音，搜索罗永浩",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"verification":"navigationCommitted","page":{"url":"https://www.douyin.com/passport/login"}}
                """).RootElement.Clone())).Accepted);

        var result = await execution;
        var terminal = await sink.WaitForTerminalAsync();

        Assert.False(result.Ok);
        Assert.Equal("failed", terminal.Type);
        Assert.Equal("browser_navigation_unverified", terminal.Error?.Code);
        Assert.DoesNotContain(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta?.Contains("已在抖音搜索", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NaturalProfileFollowUpUsesBoundWebContextAndVerifiedDomClick()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var search = service.ExecuteRoutedTaskAsync(
            "在微博搜索范冰冰",
            "auto",
            CancellationToken.None);
        var openTool = await sink.WaitForToolOrdinalAsync(1);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            openTool.OperationId,
            openTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://s.weibo.com/weibo?q=%E8%8C%83%E5%86%B0%E5%86%B0","title":"微博搜索","visibleText":"范冰冰"}}
                """).RootElement.Clone())).Accepted);
        Assert.True((await search).Ok);

        var profile = service.ExecuteRoutedTaskAsync(
            "进入她的个人主页",
            "auto",
            CancellationToken.None);
        var pageTool = await sink.WaitForToolOrdinalAsync(2);
        Assert.Equal("browser", pageTool.ToolCall?.Name);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            pageTool.OperationId,
            pageTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"page":{"url":"https://s.weibo.com/weibo?q=%E8%8C%83%E5%86%B0%E5%86%B0","title":"微博搜索","visibleText":"范冰冰","elements":[
                  {"ref":"v111111111111","tag":"a","text":"范冰冰","href":"https://weibo.com/u/123"},
                  {"ref":"v222222222222","tag":"a","text":"其他用户","href":"https://weibo.com/u/456"}
                ]}}
                """).RootElement.Clone())).Accepted);

        var clickTool = await sink.WaitForToolOrdinalAsync(3);
        using (var arguments = JsonDocument.Parse(clickTool.ToolCall!.ArgumentsFragment!))
        {
            var action = arguments.RootElement.GetProperty("params").GetProperty("actions")[0];
            Assert.Equal("click", action.GetProperty("type").GetString());
            Assert.Equal("v111111111111", action.GetProperty("ref").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            clickTool.OperationId,
            clickTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"steps":[{"type":"click","ok":true}],"finalPage":{"ok":true,"page":{"url":"https://weibo.com/u/123","title":"范冰冰的个人主页","visibleText":"范冰冰 粉丝 作品"}}}
                """).RootElement.Clone())).Accepted);

        var result = await profile;

        Assert.True(result.Ok);
        Assert.Equal("已点击“个人主页”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LiveDesktopFileCommandUsesTheVerifiedLocalFileService()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopFiles = new FakeDesktopFileOpenService();
        var service = CreateService(
            provider,
            sink,
            desktopFiles: desktopFiles);

        var result = await service.ExecuteDeterministicWindowsTaskAsync(
            "帮我打开桌面的 GPT导演决策",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("已打开桌面文件“GPT导演决策.txt”。", result.Value);
        Assert.Equal("GPT导演决策", Assert.Single(desktopFiles.Commands).RequestedName);
        Assert.Empty(provider.Requests);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task LiveNamedDesktopFileMoveUsesTheVerifiedLocalFastPath()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var moves = new FakeDesktopNamedFileMoveService();
        using var service = CreateService(
            provider,
            sink,
            desktopNamedFileMoves: moves);

        var result = await service.ExecuteDeterministicWindowsTaskAsync(
            "把桌面的移动验收.txt移动到资料文件夹",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("已把“移动验收.txt”移动到桌面文件夹“资料”。", result.Value);
        var command = Assert.Single(moves.Commands);
        Assert.Equal("移动验收.txt", command.SourceName);
        Assert.Equal("资料", command.DestinationFolderName);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task FolderCreationUsesTheSameVerifiedFastPathForVoiceAndLive()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var folders = new FakeDesktopFolderWorkflowService();
        using var service = CreateService(
            provider,
            sink,
            desktopFolders: folders);

        service.Submit("在桌面建个叫客户文档的文件夹");
        await sink.WaitForTerminalAsync(1);
        var live = await service.ExecuteDeterministicWindowsTaskAsync(
            "新建文件夹叫八月资料",
            CancellationToken.None);

        Assert.True(live.Ok);
        Assert.Equal("已在桌面创建文件夹“八月资料”。", live.Value);
        Assert.Collection(
            folders.Commands,
            command => Assert.Equal("客户文档", command.FolderName),
            command => Assert.Equal("八月资料", command.FolderName));
        Assert.Empty(provider.Requests);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Tool && item.ToolCall?.Name == "local_folder_create");
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content &&
            item.Delta == "已在桌面创建文件夹“客户文档”。");
    }

    [Fact]
    public async Task TextDocumentCreationUsesTheSameVerifiedFastPathForVoiceAndLive()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        using var service = CreateService(
            provider,
            sink,
            desktopDocuments: documents);

        service.Submit("在桌面创建一个文本文档，取名叫做小鹿乱撞");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在桌面创建一个完档。命名为删除验收。");
        await sink.WaitForTerminalAsync(2);
        var live = await service.ExecuteDeterministicWindowsTaskAsync(
            "新建文本文件，改名为Live测试",
            CancellationToken.None);

        Assert.True(live.Ok);
        Assert.Equal("已在桌面创建并打开“Live测试.txt”。", live.Value);
        Assert.Collection(
            documents.Commands,
            command => Assert.Equal("小鹿乱撞.txt", command.FileName),
            command => Assert.Equal("删除验收.txt", command.FileName),
            command => Assert.Equal("Live测试.txt", command.FileName));
        Assert.Empty(provider.Requests);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Tool && item.ToolCall?.Name == "local_document_create");
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content &&
            item.Delta == "已在桌面创建并打开“小鹿乱撞.txt”。");
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content &&
            item.Delta == "已在桌面创建并打开“删除验收.txt”。");
    }

    [Fact]
    public async Task BoundTextDocumentEditingBypassesTheModelAndUsesTheSameVoicePipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var edits = new FakeDesktopTextDocumentWorkflowService();
        using var service = CreateService(
            provider, sink, desktopDocuments: documents, desktopTextDocuments: edits);

        service.Submit("创建一个名为小鹿乱撞的文本文档");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在当前文档末尾输入：秋天适合早睡早起");
        await sink.WaitForTerminalAsync(2);

        var edit = Assert.Single(edits.Commands);
        Assert.Equal(DesktopTextDocumentEditActions.Append, edit.Command.Action);
        Assert.Equal("秋天适合早睡早起", edit.Command.Text);
        Assert.Equal("小鹿乱撞.txt", edit.FileName);
        Assert.Empty(provider.Requests);
        Assert.Contains(sink.Events, static item => item.Type == CoreEventTypes.Content &&
            item.Delta == "已写入并保存“小鹿乱撞.txt”。");
    }

    [Fact]
    public async Task NaturalDocumentContinuationUsesTheVerifiedWorkspaceAcrossVoiceAndLive()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var edits = new FakeDesktopTextDocumentWorkflowService();
        using var service = CreateService(
            provider,
            sink,
            desktopDocuments: documents,
            desktopTextDocuments: edits);

        service.Submit("创建一个名为自然续写验收的文本文档");
        await sink.WaitForTerminalAsync(1);
        service.Submit("写入：今天你吃饭了吗？你过得怎么样？");
        await sink.WaitForTerminalAsync(2);
        var live = await service.ExecuteDeterministicWindowsTaskAsync(
            "把连续测试成功写进文档",
            CancellationToken.None);

        Assert.True(live.Ok);
        Assert.Collection(
            edits.Commands,
            item => Assert.Equal("今天你吃饭了吗？你过得怎么样", item.Command.Text),
            item => Assert.Equal("连续测试成功", item.Command.Text));
        Assert.All(edits.Commands, item => Assert.Equal("自然续写验收.txt", item.FileName));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task NaturalInputDoesNotLeakFromAStaleDocumentBindingIntoAnotherApplication()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var edits = new FakeDesktopTextDocumentWorkflowService();
        var desktop = new FakeDeterministicDesktopActionService();
        var foreground = new MutableForegroundDocumentKindProvider();
        using var service = CreateService(
            provider,
            sink,
            desktopActions: desktop,
            desktopDocuments: documents,
            desktopTextDocuments: edits,
            foregroundDocuments: foreground);

        service.Submit("创建一个名为旧文档的文本文档");
        await sink.WaitForTerminalAsync(1);
        foreground.Kind = ForegroundDocumentKind.Other;
        service.Submit("输入：这是当前应用的文字");
        await sink.WaitForTerminalAsync(2);

        Assert.Empty(edits.Commands);
        var input = Assert.Single(desktop.Commands);
        Assert.Equal(DeterministicDesktopActionTypes.TypeText, input.Action);
        Assert.Equal("这是当前应用的文字", input.Text);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task TextDeletionClearAndCleanupUseTheSharedDeterministicPipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var edits = new FakeDesktopTextDocumentWorkflowService();
        using var service = CreateService(
            provider, sink, desktopDocuments: documents, desktopTextDocuments: edits);

        service.Submit("创建一个名为清理验收的文本文档");
        await sink.WaitForTerminalAsync(1);
        service.Submit("删除当前文档的广告内容");
        await sink.WaitForTerminalAsync(2);
        service.Submit("删除当前选中的文字");
        await sink.WaitForTerminalAsync(3);
        service.Submit("清空当前文档");
        await sink.WaitForTerminalAsync(4);
        service.Submit("删除作者署名和广告，只保留正文");
        await sink.WaitForTerminalAsync(5);

        Assert.Collection(
            edits.Commands,
            item =>
            {
                Assert.Equal(DesktopTextDocumentEditActions.Delete, item.Command.Action);
                Assert.Equal("广告内容", item.Command.Match);
            },
            item => Assert.Equal(DesktopTextDocumentEditActions.DeleteSelection, item.Command.Action),
            item => Assert.Equal(DesktopTextDocumentEditActions.Clear, item.Command.Action),
            item => Assert.Equal(DesktopTextDocumentEditActions.Clean, item.Command.Action));
        Assert.All(edits.Commands, item => Assert.Equal("清理验收.txt", item.FileName));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ForegroundDocumentKindOverridesAStaleBindingAcrossTextWordAndExcel()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var text = new FakeDesktopTextDocumentWorkflowService { CurrentFileName = "删除验收.txt" };
        var word = new FakeDesktopWordDocumentWorkflowService { CurrentFileName = "文字删除验收.docx" };
        var excel = new FakeDesktopExcelWorkbookWorkflowService { CurrentFileName = "表格文字删除验收.xlsx" };
        var foreground = new MutableForegroundDocumentKindProvider();
        using var service = CreateService(
            provider,
            sink,
            desktopDocuments: documents,
            desktopTextDocuments: text,
            desktopWordDocuments: word,
            desktopExcelWorkbooks: excel,
            foregroundDocuments: foreground);

        service.Submit("创建一个名为旧绑定的文本文档");
        await sink.WaitForTerminalAsync(1);

        foreground.Kind = ForegroundDocumentKind.Word;
        service.Submit("删除当前选中的文字");
        await sink.WaitForTerminalAsync(2);

        foreground.Kind = ForegroundDocumentKind.Excel;
        service.Submit("清空当前文档");
        await sink.WaitForTerminalAsync(3);

        foreground.Kind = ForegroundDocumentKind.Other;
        service.Submit("清空当前文档");
        await sink.WaitForTerminalAsync(4);

        Assert.Empty(text.Commands);
        var wordEdit = Assert.Single(word.Edits);
        Assert.Equal("文字删除验收.docx", wordEdit.FileName);
        Assert.Equal(DesktopTextDocumentEditActions.DeleteSelection, wordEdit.Command.Action);
        var excelEdit = Assert.Single(excel.Edits);
        Assert.Equal("表格文字删除验收.xlsx", excelEdit.FileName);
        Assert.Equal(DesktopExcelWorkbookEditActions.ClearSheet, excelEdit.Command.Action);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Failed && item.Error?.Code == "document_binding_invalid");
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RecentlyVerifiedTextDocumentSurvivesATransientSameFormatBindingMiss()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var documents = new FakeDesktopDocumentWorkflowService();
        var text = new FakeDesktopTextDocumentWorkflowService();
        var foreground = new MutableForegroundDocumentKindProvider { Kind = ForegroundDocumentKind.Text };
        using var service = CreateService(
            provider,
            sink,
            desktopDocuments: documents,
            desktopTextDocuments: text,
            foregroundDocuments: foreground);

        service.Submit("创建一个名为连续编辑验收的文本文档");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在当前文档末尾输入：连续写入成功");
        await sink.WaitForTerminalAsync(2);
        service.Submit("保存并关闭当前文档");
        await sink.WaitForTerminalAsync(3);

        Assert.Collection(
            text.Commands,
            item =>
            {
                Assert.Equal("连续编辑验收.txt", item.FileName);
                Assert.Equal(DesktopTextDocumentEditActions.Append, item.Command.Action);
            },
            item =>
            {
                Assert.Equal("连续编辑验收.txt", item.FileName);
                Assert.Equal(DesktopTextDocumentEditActions.Close, item.Command.Action);
            });
        Assert.Equal(2, text.CurrentBindingAttempts);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task OpeningAnExistingTextFileRebindsItsVerifiedNameForTheNextEdit()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopFiles = new FakeDesktopFileOpenService();
        var edits = new FakeDesktopTextDocumentWorkflowService();
        using var service = CreateService(
            provider,
            sink,
            desktopFiles: desktopFiles,
            desktopTextDocuments: edits);

        service.Submit("打开桌面上的GPT导演决策");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在当前文档末尾输入：重新绑定成功");
        await sink.WaitForTerminalAsync(2);

        Assert.Equal("GPT导演决策.txt", Assert.Single(edits.Commands).FileName);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task WordCreationAndEditingShareTheDeterministicVoiceAndLivePipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService();
        using var service = CreateService(
            provider, sink, desktopWordDocuments: word);

        service.Submit("创建一个名为秋天养生的Word文档");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在当前文档末尾输入：秋季宜早睡早起");
        await sink.WaitForTerminalAsync(2);
        service.Submit("删除当前文档的早睡");
        await sink.WaitForTerminalAsync(3);
        service.Submit("保存并关闭当前 Word 文档");
        await sink.WaitForTerminalAsync(4);
        var liveCreate = await service.ExecuteDeterministicWindowsTaskAsync(
            "创建一个名为Live工作记录的Word文档",
            CancellationToken.None);
        var liveEdit = await service.ExecuteDeterministicWindowsTaskAsync(
            "在当前文档末尾输入：Live模式共用成功",
            CancellationToken.None);
        var liveClose = await service.ExecuteDeterministicWindowsTaskAsync(
            "存好后关掉这个Wod文档",
            CancellationToken.None);

        Assert.True(liveCreate.Ok);
        Assert.True(liveEdit.Ok);
        Assert.True(liveClose.Ok);
        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Failed);
        Assert.Collection(
            word.Created,
            command => Assert.Equal("秋天养生.docx", command.FileName),
            command => Assert.Equal("Live工作记录.docx", command.FileName));
        Assert.Collection(
            word.Edits,
            edit =>
            {
                Assert.Equal("秋天养生.docx", edit.FileName);
                Assert.Equal("秋季宜早睡早起", edit.Command.Text);
            },
            edit =>
            {
                Assert.Equal("秋天养生.docx", edit.FileName);
                Assert.Equal(DesktopTextDocumentEditActions.Delete, edit.Command.Action);
                Assert.Equal("早睡", edit.Command.Match);
            },
            edit =>
            {
                Assert.Equal("秋天养生.docx", edit.FileName);
                Assert.Equal(DesktopTextDocumentEditActions.Close, edit.Command.Action);
            },
            edit =>
            {
                Assert.Equal("Live工作记录.docx", edit.FileName);
                Assert.Equal("Live模式共用成功", edit.Command.Text);
            },
            edit =>
            {
                Assert.Equal("Live工作记录.docx", edit.FileName);
                Assert.Equal(DesktopTextDocumentEditActions.Close, edit.Command.Action);
            });
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExistingWordDocumentOpenAndEditUseTheManagedWordPipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopFiles = new FakeDesktopFileOpenService();
        var word = new FakeDesktopWordDocumentWorkflowService { ExistingFileName = "秋天养生.docx" };
        using var service = CreateService(
            provider,
            sink,
            desktopFiles: desktopFiles,
            desktopWordDocuments: word);

        service.Submit("打开桌面上的秋天养生");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在当前文档末尾输入：已有文档编辑成功");
        await sink.WaitForTerminalAsync(2);

        Assert.Equal("秋天养生", Assert.Single(word.Opened).RequestedName);
        var edit = Assert.Single(word.Edits);
        Assert.Equal("秋天养生.docx", edit.FileName);
        Assert.Equal("已有文档编辑成功", edit.Command.Text);
        Assert.Empty(desktopFiles.Commands);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task DocumentEditReacquiresTheCurrentWordDocumentWhenHistoryHasNoBinding()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService { CurrentFileName = "手动打开.docx" };
        using var service = CreateService(provider, sink, desktopWordDocuments: word);

        var result = await service.ExecuteRoutedTaskAsync("清空当前文档", "auto", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, word.CurrentBindingAttempts);
        var edit = Assert.Single(word.Edits);
        Assert.Equal("手动打开.docx", edit.FileName);
        Assert.Equal(DesktopTextDocumentEditActions.Clear, edit.Command.Action);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task VoiceArticleTransferUsesTheLatestWordWorkspaceWhileBrowserIsActive()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService();
        var missions = new MissionCoordinator();
        missions.ObserveVerifiedDocument("秋天养生.docx");
        missions.ObserveVerifiedWorkspaceAction("打开谷歌浏览器");
        using var service = CreateService(
            provider,
            sink,
            desktopWordDocuments: word,
            missionCoordinator: missions);

        var submitted = await service.SubmitAsync(
            "把当前网页正文写入刚才的 Word 文档",
            "voice",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.Equal(submitted.OperationId, tool.OperationId);
        Assert.Equal($"browser_get_article_{submitted.OperationId:N}", tool.ToolCall?.Id);
        using (var arguments = JsonDocument.Parse(tool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("getArticle", arguments.RootElement.GetProperty("method").GetString());
            Assert.Equal(JsonValueKind.Object, arguments.RootElement.GetProperty("params").ValueKind);
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            submitted.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {
                  "ok":true,
                  "article":{
                    "title":"秋季养生指南",
                    "url":"https://example.com/autumn",
                    "text":"作者：示例作者\n\n秋季天气逐渐转凉，日常作息应当规律，并根据气温变化及时增减衣物，早晚外出时尤其需要留意温差。\n\n饮食宜清淡多样，保证蔬菜、水果、蛋白质和充足饮水，同时保持适量运动和充足睡眠。",
                    "paragraphCount":3,
                    "source":"article"
                  }
                }
                """).RootElement.Clone())).Accepted);

        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal(CoreEventTypes.Completed, terminal.Type);
        var edit = Assert.Single(word.Edits);
        Assert.Equal("秋天养生.docx", edit.FileName);
        Assert.Equal(DesktopTextDocumentEditActions.Append, edit.Command.Action);
        Assert.StartsWith("秋季养生指南\n\n秋季天气", edit.Command.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("作者", edit.Command.Text, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content &&
            item.Delta == "已提取当前网页正文，清理后写入并保存到“秋天养生.docx”。");
        Assert.Equal(WorkspaceResourceKind.Document, missions.GetActiveWorkspace()?.Kind);
    }

    [Fact]
    public async Task ReopensTheLastVerifiedWordDocumentAfterItsActiveBindingWasClosed()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService { ExistingFileName = "最近文档.docx" };
        using var service = CreateService(provider, sink, desktopWordDocuments: word);

        await service.SubmitAsync(
            "在桌面创建一个 Word 文档，命名为最近文档",
            "voice",
            "auto",
            CancellationToken.None);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(1)).Type);

        await service.SubmitAsync(
            "保存并关闭当前 Word 文档",
            "voice",
            "auto",
            CancellationToken.None);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(2)).Type);

        await service.SubmitAsync(
            "重新打开刚才的 Word 文档",
            "voice",
            "auto",
            CancellationToken.None);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(3)).Type);

        Assert.Equal("最近文档.docx", Assert.Single(word.Opened).RequestedName);
        Assert.Equal(DesktopTextDocumentEditActions.Close, Assert.Single(word.Edits).Command.Action);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LiveArticleTransferUsesTheSameVerifiedFastPathWithoutModelPlanning()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService();
        var missions = new MissionCoordinator();
        missions.ObserveVerifiedDocument("工作记录.docx");
        using var service = CreateService(
            provider,
            sink,
            desktopWordDocuments: word,
            missionCoordinator: missions);

        var execution = service.ExecuteRoutedTaskAsync(
            "把这篇文章整理到当前文档里",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {
                  "ok":true,
                  "article":{
                    "title":"办公效率",
                    "url":"https://example.com/work",
                    "text":"第一段介绍如何整理每日任务，并且把重要事项放在清晰可见的位置，避免遗漏当天必须完成的工作。\n\n第二段介绍如何减少重复操作，让日常工作保持稳定和连贯，并在下班前检查保存结果。",
                    "paragraphCount":2,
                    "source":"main"
                  }
                }
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal("已提取当前网页正文，清理后写入并保存到“工作记录.docx”。", result.Value);
        Assert.Single(word.Edits);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ArticleTransferReacquiresTheCurrentEditableWordDocumentWhenHistoryHasNoBinding()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService { CurrentFileName = "手动打开.docx" };
        using var service = CreateService(provider, sink, desktopWordDocuments: word);

        var execution = service.ExecuteRoutedTaskAsync(
            "把当前网页正文写入刚才的 Word 文档",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {
                  "ok":true,
                  "article":{
                    "title":"当前文章",
                    "url":"https://example.com/current",
                    "text":"第一段正文足够完整，用于验证当前手动打开的 Word 文档可以被安全重新绑定并写入。\n\n第二段正文继续提供足够内容，确保文章提取和本地长度校验均能顺利通过。",
                    "paragraphCount":2,
                    "source":"article"
                  }
                }
                """).RootElement.Clone())).Accepted);

        var result = await execution;

        Assert.True(result.Ok);
        Assert.Equal(1, word.CurrentBindingAttempts);
        Assert.Equal("手动打开.docx", Assert.Single(word.Edits).FileName);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ArticleTransferFailsBeforeBrowserReadWhenNoWordDocumentIsBound()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService();
        using var service = CreateService(provider, sink, desktopWordDocuments: word);

        var result = await service.ExecuteRoutedTaskAsync(
            "把当前网页正文写入刚才的 Word 文档",
            "auto",
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("没有找到最近绑定或正在打开的桌面 Word 文档", result.Value, StringComparison.Ordinal);
        Assert.Empty(word.Edits);
        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Tool);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ArticleTransferNeverWritesOrReportsSuccessWhenBrowserToolFails()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var word = new FakeDesktopWordDocumentWorkflowService();
        var missions = new MissionCoordinator();
        missions.ObserveVerifiedDocument("工作记录.docx");
        using var service = CreateService(
            provider,
            sink,
            desktopWordDocuments: word,
            missionCoordinator: missions);

        var execution = service.ExecuteRoutedTaskAsync(
            "提取当前网页正文并保存到刚才的文档",
            "auto",
            CancellationToken.None);
        var tool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            tool.OperationId,
            tool.ToolCall!.Id!,
            false,
            Error: new RpcError("browser_bridge_disconnected", "浏览器桥未连接。", true))).Accepted);

        var result = await execution;

        Assert.False(result.Ok);
        Assert.Empty(word.Edits);
        Assert.DoesNotContain(sink.Events, static item => item.Type == CoreEventTypes.Completed);
        Assert.DoesNotContain(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta!.Contains("已提取", StringComparison.Ordinal));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExcelCreationAndCellEditingShareTheDeterministicVoiceAndLivePipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var excel = new FakeDesktopExcelWorkbookWorkflowService();
        using var service = CreateService(
            provider, sink, desktopExcelWorkbooks: excel);

        service.Submit("创建一个名为销售记录的Excel表格");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在 A1 输入：姓名");
        await sink.WaitForTerminalAsync(2);
        service.Submit("删除当前文档的广告内容");
        await sink.WaitForTerminalAsync(3);
        var liveCreate = await service.ExecuteDeterministicWindowsTaskAsync(
            "创建一个名为Live销售记录的Excel表格",
            CancellationToken.None);
        var liveEdit = await service.ExecuteDeterministicWindowsTaskAsync(
            "把 B2 改成 198",
            CancellationToken.None);
        var liveClear = await service.ExecuteDeterministicWindowsTaskAsync(
            "清空 A1 到 C3 区域",
            CancellationToken.None);

        Assert.True(liveCreate.Ok);
        Assert.True(liveEdit.Ok);
        Assert.True(liveClear.Ok);
        Assert.Collection(
            excel.Created,
            command => Assert.Equal("销售记录.xlsx", command.FileName),
            command => Assert.Equal("Live销售记录.xlsx", command.FileName));
        Assert.Collection(
            excel.Edits,
            edit =>
            {
                Assert.Equal("销售记录.xlsx", edit.FileName);
                Assert.Equal("A1", edit.Command.CellReference);
                Assert.Equal("姓名", edit.Command.Value);
            },
            edit =>
            {
                Assert.Equal("销售记录.xlsx", edit.FileName);
                Assert.Equal(DesktopExcelWorkbookEditActions.DeleteText, edit.Command.Action);
                Assert.Equal("广告内容", edit.Command.Value);
            },
            edit =>
            {
                Assert.Equal("Live销售记录.xlsx", edit.FileName);
                Assert.Equal("B2", edit.Command.CellReference);
                Assert.Equal("198", edit.Command.Value);
            },
            edit =>
            {
                Assert.Equal("Live销售记录.xlsx", edit.FileName);
                Assert.Equal(DesktopExcelWorkbookEditActions.ClearRange, edit.Command.Action);
                Assert.Equal("A1:C3", edit.Command.CellReference);
            });
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExistingExcelWorkbookOpenAndEditUseTheManagedSpreadsheetPipeline()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopFiles = new FakeDesktopFileOpenService();
        var excel = new FakeDesktopExcelWorkbookWorkflowService { ExistingFileName = "销售记录.xlsx" };
        using var service = CreateService(
            provider,
            sink,
            desktopFiles: desktopFiles,
            desktopExcelWorkbooks: excel);

        service.Submit("打开桌面上的销售记录");
        await sink.WaitForTerminalAsync(1);
        service.Submit("清空 C3");
        await sink.WaitForTerminalAsync(2);

        Assert.Equal("销售记录", Assert.Single(excel.Opened).RequestedName);
        var edit = Assert.Single(excel.Edits);
        Assert.Equal("销售记录.xlsx", edit.FileName);
        Assert.Equal("C3", edit.Command.CellReference);
        Assert.Empty(desktopFiles.Commands);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExcelEditReacquiresTheCurrentWorkbookWhenHistoryHasNoBinding()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var excel = new FakeDesktopExcelWorkbookWorkflowService { CurrentFileName = "手动表格.xlsx" };
        using var service = CreateService(provider, sink, desktopExcelWorkbooks: excel);

        var result = await service.ExecuteRoutedTaskAsync("清空 A1 到 C3 区域", "auto", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, excel.CurrentBindingAttempts);
        var edit = Assert.Single(excel.Edits);
        Assert.Equal("手动表格.xlsx", edit.FileName);
        Assert.Equal(DesktopExcelWorkbookEditActions.ClearRange, edit.Command.Action);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExcelSaveAndCloseUsesTheLastVerifiedWorkbookAfterMissionWorkspaceExpires()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var excel = new FakeDesktopExcelWorkbookWorkflowService();
        var missions = new MissionCoordinator();
        using var service = CreateService(
            provider,
            sink,
            desktopExcelWorkbooks: excel,
            missionCoordinator: missions);

        service.Submit("创建一个名为持久绑定验收的Excel表格");
        await sink.WaitForTerminalAsync(1);
        service.Submit("在 A1 输入：已验证");
        await sink.WaitForTerminalAsync(2);
        missions.InvalidateActiveWorkspace(WorkspaceResourceKind.Document);

        service.Submit("保存并关闭当前表格");
        await sink.WaitForTerminalAsync(3);

        Assert.Collection(
            excel.Edits,
            edit => Assert.Equal(DesktopExcelWorkbookEditActions.SetCell, edit.Command.Action),
            edit =>
            {
                Assert.Equal(DesktopExcelWorkbookEditActions.Close, edit.Command.Action);
                Assert.Equal("持久绑定验收.xlsx", edit.FileName);
            });
        Assert.DoesNotContain(sink.Events, static item =>
            item.Type == CoreEventTypes.Failed && item.Error?.Code == "spreadsheet_binding_invalid");
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task StructuredDesktopActionsBypassTheModelAndContinueLocally()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopActions = new FakeDeterministicDesktopActionService();
        var service = CreateService(
            provider,
            sink,
            desktopActions: desktopActions);

        service.Submit("点击当前窗口的编辑菜单");
        await sink.WaitForTerminalAsync(1);
        service.Submit("选择全选");
        await sink.WaitForTerminalAsync(2);
        service.Submit("复制一下");
        await sink.WaitForTerminalAsync(3);
        service.Submit("打开文件菜单");
        await sink.WaitForTerminalAsync(4);

        Assert.Collection(
            desktopActions.Commands,
            command =>
            {
                Assert.Equal(DeterministicDesktopActionTypes.ClickControl, command.Action);
                Assert.Equal("编辑菜单", command.Target);
            },
            command => Assert.Equal(DeterministicDesktopActionTypes.SelectAll, command.Action),
            command => Assert.Equal(DeterministicDesktopActionTypes.CopySelection, command.Action),
            command =>
            {
                Assert.Equal(DeterministicDesktopActionTypes.ClickControl, command.Action);
                Assert.Equal("文件菜单", command.Target);
            });
        Assert.Empty(provider.Requests);
        Assert.Equal(4, sink.Events.Count(static item => item.Type == CoreEventTypes.Completed));
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Tool && item.ToolCall?.Name == "local_desktop_action");
    }

    [Fact]
    public async Task ExplicitDesktopTextEntryAndSubmitBypassTheModelInBothModes()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopActions = new FakeDeterministicDesktopActionService();
        using var service = CreateService(
            provider,
            sink,
            desktopActions: desktopActions);

        service.Submit("在微信输入：你好，明天下午三点见");
        await sink.WaitForTerminalAsync(1);
        var liveSubmit = await service.ExecuteDeterministicWindowsTaskAsync(
            "发送这条消息",
            CancellationToken.None);

        Assert.True(liveSubmit.Ok);
        Assert.Collection(
            desktopActions.Commands,
            command =>
            {
                Assert.Equal(DeterministicDesktopActionTypes.TypeText, command.Action);
                Assert.Equal("消息输入框", command.Target);
                Assert.Equal("你好，明天下午三点见", command.Text);
            },
            command => Assert.Equal(DeterministicDesktopActionTypes.SubmitText, command.Action));
        Assert.Empty(provider.Requests);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta == "已输入文字，尚未发送。");
    }

    [Fact]
    public async Task MissingStructuredControlFallsBackToOneDirectVisualLocation()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var service = CreateService(
            provider,
            sink,
            desktopActions: new MissingDeterministicDesktopActionService());

        service.Submit("点击当前窗口的搜索按钮");
        var visual = await sink.WaitForToolOrdinalAsync(2);

        Assert.Equal("desktop_visual", visual.ToolCall?.Name);
        using (var arguments = JsonDocument.Parse(visual.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("click", arguments.RootElement.GetProperty("action").GetString());
            Assert.Equal("搜索按钮", arguments.RootElement.GetProperty("target").GetString());
            Assert.True(arguments.RootElement.GetProperty("preferVisual").GetBoolean());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            visual.OperationId,
            visual.ToolCall.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);

        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync()).Type);
        Assert.Contains(sink.Events, static item =>
            item.Type == CoreEventTypes.Content && item.Delta == "已点击“搜索按钮”。");
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ClosedMessagingUiUsesVisualTypingAndAConsumedSubmitAuthorization()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopActions = new VisualFallbackDesktopActionService();
        using var service = CreateService(
            provider,
            sink,
            desktopActions: desktopActions);

        service.Submit("在微信输入：你好，明天下午三点见");
        var typeVisual = await sink.WaitForToolOrdinalAsync(2);
        using (var arguments = JsonDocument.Parse(typeVisual.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("type", arguments.RootElement.GetProperty("action").GetString());
            Assert.Equal("消息输入框", arguments.RootElement.GetProperty("target").GetString());
            Assert.Equal("你好，明天下午三点见", arguments.RootElement.GetProperty("text").GetString());
            Assert.Equal(3, arguments.RootElement.EnumerateObject().Count());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            typeVisual.OperationId,
            typeVisual.ToolCall.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(1)).Type);
        Assert.True(desktopActions.VisualDraftObserved);

        service.Submit("发送这条消息");
        var submitVisual = await sink.WaitForToolOrdinalAsync(4);
        using (var arguments = JsonDocument.Parse(submitVisual.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("click", arguments.RootElement.GetProperty("action").GetString());
            Assert.Equal("发送按钮", arguments.RootElement.GetProperty("target").GetString());
            Assert.True(arguments.RootElement.GetProperty("preferVisual").GetBoolean());
            Assert.Equal(3, arguments.RootElement.EnumerateObject().Count());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            submitVisual.OperationId,
            submitVisual.ToolCall.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(2)).Type);
        Assert.Equal(1, desktopActions.SubmitAuthorizationsConsumed);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task FailedVisualSubmitCannotReuseTheConsumedDraftAuthorization()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopActions = new VisualFallbackDesktopActionService();
        using var service = CreateService(
            provider,
            sink,
            desktopActions: desktopActions);

        service.Submit("在微信输入：这条消息只能发送一次");
        var typeVisual = await sink.WaitForToolOrdinalAsync(2);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            typeVisual.OperationId,
            typeVisual.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);
        Assert.Equal(CoreEventTypes.Completed, (await sink.WaitForTerminalAsync(1)).Type);

        service.Submit("发送这条消息");
        var submitVisual = await sink.WaitForToolOrdinalAsync(4);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            submitVisual.OperationId,
            submitVisual.ToolCall!.Id!,
            false,
            Error: new RpcError("desktop_visual_action_unverified", "发送结果无法确认。", true))).Accepted);
        Assert.Equal(CoreEventTypes.Failed, (await sink.WaitForTerminalAsync(2)).Type);
        Assert.False(desktopActions.VisualDraftObserved);
        Assert.Equal(1, desktopActions.SubmitAuthorizationsConsumed);

        service.Submit("发送这条消息");
        var secondSubmit = await sink.WaitForTerminalAsync(3);

        Assert.Equal(CoreEventTypes.Failed, secondSubmit.Type);
        Assert.Equal("desktop_message_draft_missing", secondSubmit.Error?.Code);
        Assert.Equal(5, sink.Events.Count(static item => item.Type == CoreEventTypes.Tool));
        Assert.Equal(1, desktopActions.SubmitAuthorizationsConsumed);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LiveMissingStructuredControlUsesTheSameVisualFallback()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var service = CreateService(
            provider,
            sink,
            desktopActions: new MissingDeterministicDesktopActionService());

        var execution = service.ExecuteDeterministicWindowsTaskAsync(
            "点击当前窗口的搜索按钮",
            CancellationToken.None);
        var visual = await sink.WaitForToolOrdinalAsync(2);
        Assert.Equal("desktop_visual", visual.ToolCall?.Name);
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            visual.OperationId,
            visual.ToolCall!.Id!,
            true,
            JsonDocument.Parse("{\"executed\":true,\"verified\":true}").RootElement.Clone())).Accepted);

        var result = await execution;
        Assert.True(result.Ok);
        Assert.Equal("已点击“搜索按钮”。", result.Value);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task LiveStructuredDesktopActionUsesTheVerifiedLocalService()
    {
        var provider = new SequencedProvider([]);
        var sink = new RecordingEventSink();
        var desktopActions = new FakeDeterministicDesktopActionService();
        var service = CreateService(
            provider,
            sink,
            desktopActions: desktopActions);

        var result = await service.ExecuteDeterministicWindowsTaskAsync(
            "然后点击文件菜单",
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("已打开“文件菜单”。", result.Value);
        Assert.Equal("文件菜单", Assert.Single(desktopActions.Commands).Target);
        Assert.Empty(provider.Requests);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task DeniesWindowsControlWhenTheModelChangesTheRequestedApplication()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "wrong_app", "windows_control", "{\"action\":\"openApplication\",\"applicationId\":\"calculator\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var windows = new FakeWindowsControlService();
        var service = CreateService(provider, sink, windows, enableDeterministicFastPath: false);

        service.Submit("打开记事本");
        await sink.WaitForTerminalAsync();

        Assert.Empty(windows.Commands);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.Content.Contains("tool_consent_denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAPathInjectedIntoWindowsControlArguments()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "path_injection", "windows_control", "{\"action\":\"openApplication\",\"applicationId\":\"notepad\",\"path\":\"C:\\\\evil.exe\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有执行。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var windows = new FakeWindowsControlService();
        var service = CreateService(provider, sink, windows, enableDeterministicFastPath: false);

        service.Submit("打开记事本");
        await sink.WaitForTerminalAsync();

        Assert.Empty(windows.Commands);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.Content.Contains("invalid_windows_control", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitsForUserAndResumesTheSameMissionOnTheNextTurn()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "wait_1", "mission_control", "{\"action\":\"await_input\",\"message\":\"请告诉我文件名\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "请告诉我文件名。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已使用报告.docx继续。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var first = service.Submit("创建一个文档，但我还没说文件名");
        await sink.WaitForTerminalAsync(1);
        var awaiting = sink.Events.Last(static item => item.Type == "mission").Mission!;
        Assert.Equal("awaitingInput", awaiting.State);

        var second = await service.SubmitAsync("文件名是报告.docx", "voice", "auto", CancellationToken.None);
        await sink.WaitForTerminalAsync(2);

        Assert.Equal(first.Mission.MissionId, second.Mission.MissionId);
        Assert.Equal(2, second.Mission.Revision);
        Assert.Equal("completed", sink.Events.Last(static item => item.Type == "mission").Mission?.State);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool" && item.ToolCall?.Name == "mission_control");
    }

    [Fact]
    public async Task DoesNotPauseForAResolvableForegroundWindowConfirmation()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(
                        0,
                        "foreground_wait",
                        "mission_control",
                        "{\"action\":\"await_input\",\"message\":\"请确认记事本窗口处于前台，然后回复已就绪\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "我会直接检查当前前台窗口。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("请在当前窗口中点击编辑菜单");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("failed", terminal.Type);
        Assert.Equal("local_mutation_unverified", terminal.Error?.Code);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "mission" && item.Mission?.State == "awaitingInput");
        Assert.Contains(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.Content.Contains("mission_control_auto_resolvable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AwaitInputSuppressesOtherToolsRequestedInTheSameModelTurn()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(0, "browser_1", "browser", "{\"method\":\"open\",\"params\":{\"url\":\"https://example.com\"}}")),
                new ChatStreamEvent(ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(1, "wait_1", "mission_control", "{\"action\":\"await_input\",\"message\":\"请确认目标网站\"}")),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "tool_calls"),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "请确认目标网站。"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        service.Submit("打开网页，但目标还不确定");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        var browserResult = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "browser_1");
        Assert.Contains("mission_waiting_for_user", browserResult.Content, StringComparison.Ordinal);
        Assert.Equal("awaitingInput", sink.Events.Last(static item => item.Type == "mission").Mission?.State);
    }

    [Fact]
    public async Task ExplicitCorrectionCancelsOldWorkAndStartsTheRevisedMission()
    {
        var provider = new CorrectableProvider();
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var first = service.Submit("打开网页并整理内容");
        await sink.WaitForTypeAsync("accepted");
        var corrected = await service.SubmitAsync("改成只读取当前页面", "voice", "auto", CancellationToken.None);
        await sink.WaitForTerminalAsync(2);

        Assert.Equal(first.Mission.MissionId, corrected.Mission.MissionId);
        Assert.Equal(2, corrected.Mission.Revision);
        Assert.Contains(sink.Events, item => item.OperationId == first.OperationId && item.Type == "cancelled");
        Assert.Contains(sink.Events, item => item.OperationId == corrected.OperationId && item.Type == "completed");
    }

    [Fact]
    public async Task OrdinarySecondCommandWaitsForTheRunningTaskInsteadOfCancellingIt()
    {
        var provider = new QueueAwareProvider();
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var first = service.Submit("整理当前网页内容");
        await provider.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondTask = service.SubmitAsync(
            "打开微信",
            "voice",
            "auto",
            CancellationToken.None);
        await Task.Delay(50);

        Assert.False(secondTask.IsCompleted);
        Assert.Equal(1, provider.CallCount);

        provider.ReleaseFirst.TrySetResult();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        await sink.WaitForTerminalAsync(2);

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, provider.CallCount);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "cancelled");
    }

    [Fact]
    public async Task LiveExplicitBrowserCommandPreemptsAStaleModelTask()
    {
        var provider = new WaitingProvider();
        var sink = new RecordingEventSink();
        using var service = CreateService(provider, sink);

        var stale = service.Submit("整理当前网页内容");
        await sink.WaitForTypeAsync("accepted");

        var next = service.ExecuteRoutedTaskAsync(
            "进入我的抖音个人主页",
            "auto",
            CancellationToken.None);
        var browserTool = await sink.WaitForTypeAsync(CoreEventTypes.Tool);

        Assert.Equal("browser", browserTool.ToolCall?.Name);
        using (var arguments = JsonDocument.Parse(browserTool.ToolCall!.ArgumentsFragment!))
        {
            Assert.Equal("open", arguments.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                "https://www.douyin.com/user/self?from_tab_name=main",
                arguments.RootElement.GetProperty("params").GetProperty("url").GetString());
        }
        Assert.True(service.SubmitToolResult(new SubmitToolResultParams(
            browserTool.OperationId,
            browserTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""
                {"ok":true,"finalPage":{"ok":true,"page":{"url":"https://www.douyin.com/user/self?from_tab_name=main"}}}
                """).RootElement.Clone())).Accepted);

        var result = await next.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Ok);
        Assert.Contains(sink.Events, item => item.OperationId == stale.OperationId && item.Type == "cancelled");
        Assert.DoesNotContain(sink.Events, item => item.OperationId == stale.OperationId && item.Type == CoreEventTypes.Tool);
    }

    [Fact]
    public async Task ProgressAndStopCommandsTargetOnlyTheActiveMission()
    {
        var provider = new WaitingProvider();
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink);

        var started = service.Submit("读取当前网页并总结");
        await sink.WaitForTypeAsync("accepted");
        var progress = await service.SubmitAsync("进度", "voice", "auto", CancellationToken.None);
        Assert.Equal(started.OperationId, progress.OperationId);
        Assert.Equal("running", progress.Mission.State);

        var stopped = await service.SubmitAsync("停止", "voice", "auto", CancellationToken.None);
        var terminal = await sink.WaitForTerminalAsync();
        Assert.Equal(started.OperationId, stopped.OperationId);
        Assert.Equal("cancelled", stopped.Mission.State);
        Assert.Equal("cancelled", terminal.Type);
    }

    [Fact]
    public async Task PersistsACompletedConversationAndCanContinueItAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"velia-conversation-history-test-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiConversationHistoryStore(directory);
            var firstProvider = new ScriptedProvider(
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "第一次回答"),
                new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
            ]);
            var firstSink = new RecordingEventSink();
            using (var first = CreateService(firstProvider, firstSink, historyStore: store))
            {
                first.Submit("第一次问题");
                await firstSink.WaitForTerminalAsync();
                Assert.Single(first.ListHistory().Conversations);
            }

            var saved = store.List().Single();
            var secondProvider = new SequencedProvider(
            [
                [
                    new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "第二次回答"),
                    new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop"),
                ],
            ]);
            var secondSink = new RecordingEventSink();
            using var second = CreateService(secondProvider, secondSink, historyStore: store);
            var opened = second.OpenHistory(saved.ConversationId);
            Assert.Equal("第一次问题", opened.Messages[0].Text);

            second.Submit("继续回答");
            await secondSink.WaitForTerminalAsync();

            var request = Assert.Single(secondProvider.Requests);
            Assert.Contains(request.Messages, static item => item.Role == ChatRole.User && item.Content == "第一次问题");
            Assert.Contains(request.Messages, static item => item.Role == ChatRole.Assistant && item.Content == "第一次回答");
            Assert.Contains(request.Messages, static item => item.Role == ChatRole.User && item.Content == "继续回答");
            Assert.Equal(4, store.Get(saved.ConversationId)?.Messages.Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ConversationService CreateService(
        IStreamingChatProvider provider,
        ICoreEventSink sink,
        IWindowsControlService? windowsControl = null,
        IDesktopFileOpenService? desktopFiles = null,
        IConversationHistoryStore? historyStore = null,
        bool enableDeterministicFastPath = true,
        IDeterministicDesktopActionService? desktopActions = null,
        IDesktopDocumentWorkflowService? desktopDocuments = null,
        IDesktopTextDocumentWorkflowService? desktopTextDocuments = null,
        IDesktopWordDocumentWorkflowService? desktopWordDocuments = null,
        IDesktopExcelWorkbookWorkflowService? desktopExcelWorkbooks = null,
        MissionCoordinator? missionCoordinator = null,
        IForegroundDocumentKindProvider? foregroundDocuments = null,
        IDesktopNamedFileMoveService? desktopNamedFileMoves = null,
        IDesktopFolderWorkflowService? desktopFolders = null)
    {
        var secrets = new MemorySecretStore();
        secrets.Save(ProviderIds.Llm, "test-key");
        var factory = new SingleProviderFactory(provider);
        var configuration = new ProviderConfigurationService(secrets, factory);
        return new ConversationService(
            factory,
            configuration,
            sink,
            missionCoordinator: missionCoordinator,
            windowsControlService: windowsControl,
            desktopFileOpenService: desktopFiles,
            conversationHistoryStore: historyStore,
            enableDeterministicFastPath: enableDeterministicFastPath,
            deterministicDesktopActionService: desktopActions,
            desktopDocumentWorkflowService: desktopDocuments,
            desktopTextDocumentWorkflowService: desktopTextDocuments,
            desktopWordDocumentWorkflowService: desktopWordDocuments,
            desktopExcelWorkbookWorkflowService: desktopExcelWorkbooks,
            foregroundDocumentKindProvider: foregroundDocuments ?? new MutableForegroundDocumentKindProvider(),
            desktopNamedFileMoveService: desktopNamedFileMoves,
            desktopFolderWorkflowService: desktopFolders);
    }

    private sealed class FakeWindowsControlService : IWindowsControlService
    {
        public List<WindowsControlCommand> Commands { get; } = [];

        public Task<WindowsControlResult> ExecuteAsync(WindowsControlCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new WindowsControlResult(
                command.Action,
                true,
                "alreadyOpen",
                command.ApplicationId,
                "测试应用"));
        }
    }

    private sealed class FakeDesktopFileOpenService : IDesktopFileOpenService
    {
        public List<DesktopFileOpenCommand> Commands { get; } = [];

        public Task<DesktopFileOpenResult> OpenAsync(
            DesktopFileOpenCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new DesktopFileOpenResult(true, "opened", "GPT导演决策.txt"));
        }
    }

    private sealed class FakeDesktopNamedFileMoveService : IDesktopNamedFileMoveService
    {
        public List<DesktopNamedFileMoveCommand> Commands { get; } = [];

        public Task<DesktopNamedFileMoveResult> ExecuteAsync(
            DesktopNamedFileMoveCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new DesktopNamedFileMoveResult(
                true,
                command.SourceName,
                command.DestinationFolderName,
                command.SourceName));
        }
    }

    private sealed class FakeDesktopFolderWorkflowService : IDesktopFolderWorkflowService
    {
        public List<DesktopFolderCreateCommand> Commands { get; } = [];

        public Task<DesktopFolderCreateResult> CreateAsync(
            DesktopFolderCreateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            var name = command.FolderName ?? "新建文件夹";
            return Task.FromResult(new DesktopFolderCreateResult(
                true,
                command.OpenAfterCreate,
                command.OpenAfterCreate ? "createdAndOpened" : "created",
                name,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name)));
        }
    }

    private sealed class FakeDesktopDocumentWorkflowService : IDesktopDocumentWorkflowService
    {
        public List<DesktopDocumentCreateCommand> Commands { get; } = [];

        public Task<DesktopDocumentCreateResult> CreateTextDocumentAsync(
            DesktopDocumentCreateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new DesktopDocumentCreateResult(
                true,
                true,
                "createdAndOpened",
                command.FileName,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), command.FileName)));
        }
    }

    private sealed class FakeDesktopTextDocumentWorkflowService : IDesktopTextDocumentWorkflowService
    {
        public List<(DesktopTextDocumentEditCommand Command, string FileName)> Commands { get; } = [];
        public string? CurrentFileName { get; init; }
        public int CurrentBindingAttempts { get; private set; }

        public Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
        {
            CurrentBindingAttempts++;
            return Task.FromResult(CurrentFileName);
        }

        public Task<DesktopTextDocumentEditResult> ExecuteAsync(
            DesktopTextDocumentEditCommand command, string fileName, CancellationToken cancellationToken)
        {
            Commands.Add((command, fileName));
            return Task.FromResult(new DesktopTextDocumentEditResult(
                true, command.Action, fileName, command.Text?.Length ?? 0, command.Action == "close"));
        }
    }

    private sealed class MutableForegroundDocumentKindProvider : IForegroundDocumentKindProvider
    {
        public ForegroundDocumentKind Kind { get; set; } = ForegroundDocumentKind.Unknown;

        public ForegroundDocumentKind GetCurrent() => Kind;
    }

    private sealed class FakeDesktopWordDocumentWorkflowService : IDesktopWordDocumentWorkflowService
    {
        public List<DesktopWordDocumentCreateCommand> Created { get; } = [];
        public List<DesktopFileOpenCommand> Opened { get; } = [];
        public List<(DesktopTextDocumentEditCommand Command, string FileName)> Edits { get; } = [];
        public string? ExistingFileName { get; init; }
        public string? CurrentFileName { get; init; }
        public int CurrentBindingAttempts { get; private set; }

        public Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
        {
            CurrentBindingAttempts++;
            return Task.FromResult(CurrentFileName);
        }

        public Task<DesktopFileOpenResult?> TryOpenExistingAsync(
            DesktopFileOpenCommand command,
            CancellationToken cancellationToken)
        {
            Opened.Add(command);
            return Task.FromResult<DesktopFileOpenResult?>(ExistingFileName is null
                ? null
                : new DesktopFileOpenResult(true, "opened", ExistingFileName));
        }

        public Task<DesktopWordDocumentCreateResult> CreateAsync(
            DesktopWordDocumentCreateCommand command, CancellationToken cancellationToken)
        {
            Created.Add(command);
            return Task.FromResult(new DesktopWordDocumentCreateResult(
                true, true, "createdAndOpened", command.FileName, Path.Combine("C:\\Desktop", command.FileName)));
        }

        public Task<DesktopTextDocumentEditResult> ExecuteAsync(
            DesktopTextDocumentEditCommand command, string fileName, CancellationToken cancellationToken)
        {
            Edits.Add((command, fileName));
            return Task.FromResult(new DesktopTextDocumentEditResult(
                true, command.Action, fileName, command.Text?.Length ?? 0, command.Action == "close"));
        }
    }

    private sealed class FakeDesktopExcelWorkbookWorkflowService : IDesktopExcelWorkbookWorkflowService
    {
        public List<DesktopExcelWorkbookCreateCommand> Created { get; } = [];
        public List<DesktopFileOpenCommand> Opened { get; } = [];
        public List<(DesktopExcelCellCommand Command, string FileName)> Edits { get; } = [];
        public string? ExistingFileName { get; init; }
        public string? CurrentFileName { get; init; }
        public int CurrentBindingAttempts { get; private set; }

        public Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
        {
            CurrentBindingAttempts++;
            return Task.FromResult(CurrentFileName);
        }

        public Task<DesktopFileOpenResult?> TryOpenExistingAsync(
            DesktopFileOpenCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(command);
            return Task.FromResult<DesktopFileOpenResult?>(ExistingFileName is null
                ? null
                : new DesktopFileOpenResult(true, "opened", ExistingFileName));
        }

        public Task<DesktopExcelWorkbookCreateResult> CreateAsync(
            DesktopExcelWorkbookCreateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Created.Add(command);
            return Task.FromResult(new DesktopExcelWorkbookCreateResult(
                true, true, "createdAndOpened", command.FileName, Path.Combine("C:\\Desktop", command.FileName)));
        }

        public Task<DesktopExcelCellResult> ExecuteAsync(
            DesktopExcelCellCommand command,
            string fileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Edits.Add((command, fileName));
            return Task.FromResult(new DesktopExcelCellResult(
                true,
                command.Action,
                fileName,
                command.CellReference,
                command.Value,
                command.Action == DesktopExcelWorkbookEditActions.Close));
        }
    }

    private sealed class FakeDeterministicDesktopActionService : IDeterministicDesktopActionService
    {
        public List<DeterministicDesktopActionCommand> Commands { get; } = [];

        public Task<DeterministicDesktopActionResult> ExecuteAsync(
            DeterministicDesktopActionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new DeterministicDesktopActionResult(
                command.Action,
                true,
                command.Action == DeterministicDesktopActionTypes.CopySelection
                    ? "clipboardUpdated"
                    : command.Action == DeterministicDesktopActionTypes.TypeText
                        ? "messageDraftVerified"
                    : command.Action == DeterministicDesktopActionTypes.SubmitText
                        ? "messageSubmittedVerified"
                    : "menuExpanded",
                command.Target,
                "测试窗口"));
        }
    }

    private sealed class MissingDeterministicDesktopActionService : IDeterministicDesktopActionService
    {
        public Task<DeterministicDesktopActionResult> ExecuteAsync(
            DeterministicDesktopActionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DesktopVisualException(
                "desktop_visual_target_not_found",
                "没有匹配的 UIA 控件。",
                true);
        }
    }

    private sealed class VisualFallbackDesktopActionService : IDeterministicDesktopActionService
    {
        public bool VisualDraftObserved { get; private set; }
        public int SubmitAuthorizationsConsumed { get; private set; }

        public Task<DeterministicDesktopActionResult> ExecuteAsync(
            DeterministicDesktopActionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw command.Action switch
            {
                DeterministicDesktopActionTypes.TypeText => new DesktopVisualException(
                    "desktop_text_input_not_found",
                    "微信没有公开输入框。",
                    true),
                DeterministicDesktopActionTypes.SubmitText when VisualDraftObserved => new DesktopVisualException(
                    "desktop_message_submit_requires_visual",
                    "微信需要视觉发送。",
                    true),
                _ => new DesktopVisualException(
                    "desktop_message_draft_missing",
                    "当前没有消息草稿。"),
            };
        }

        public void ObserveVerifiedVisualTextEntry(DeterministicDesktopActionCommand command)
        {
            Assert.Equal(DeterministicDesktopActionTypes.TypeText, command.Action);
            VisualDraftObserved = true;
        }

        public void ConsumeVisualTextSubmissionAuthorization(DeterministicDesktopActionCommand command)
        {
            Assert.True(VisualDraftObserved);
            Assert.Equal(DeterministicDesktopActionTypes.SubmitText, command.Action);
            VisualDraftObserved = false;
            SubmitAuthorizationsConsumed++;
        }
    }

    private sealed class SingleProviderFactory(IStreamingChatProvider provider) : IChatProviderFactory
    {
        public IStreamingChatProvider Create(
            string profileId,
            int maximumOutputTokens = 4096,
            bool thinkingEnabled = true) => provider;
    }

    private sealed class ScriptedProvider(IReadOnlyList<ChatStreamEvent> events) : IStreamingChatProvider
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            foreach (var item in events)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class GatedStreamingProvider(string content) : IStreamingChatProvider
    {
        public List<ChatCompletionRequest> Requests { get; } = [];
        public TaskCompletionSource DeltaProduced { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            DeltaProduced.TrySetResult();
            yield return new ChatStreamEvent(ChatStreamEventKind.ContentDelta, content);
            await ReleaseCompletion.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop");
        }
    }

    private sealed class SequencedProvider(IReadOnlyList<IReadOnlyList<ChatStreamEvent>> rounds) : IStreamingChatProvider
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            foreach (var item in rounds[index])
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class WaitingProvider : IStreamingChatProvider
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class CorrectableProvider : IStreamingChatProvider
    {
        private int calls;

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已按纠正后的要求执行。" );
            yield return new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop");
        }
    }

    private sealed class QueueAwareProvider : IStreamingChatProvider
    {
        private int calls;
        public int CallCount => Volatile.Read(ref calls);
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent(
                ChatStreamEventKind.ContentDelta,
                call == 1 ? "第一项任务完成。" : "第二项任务完成。");
            yield return new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop");
        }
    }


    private sealed class RecordingEventSink : ICoreEventSink
    {
        private readonly ConcurrentQueue<CoreEvent> events = new();
        private readonly TaskCompletionSource<CoreEvent> terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CoreEvent>> typeWaiters = new(StringComparer.Ordinal);

        public CoreEvent[] Events => events.ToArray();

        public Task PublishAsync(CoreEvent coreEvent, CancellationToken cancellationToken)
        {
            events.Enqueue(coreEvent);
            if (coreEvent.Type is "completed" or "failed" or "cancelled")
            {
                terminal.TrySetResult(coreEvent);
            }
            if (typeWaiters.TryRemove(coreEvent.Type, out var waiter))
            {
                waiter.TrySetResult(coreEvent);
            }
            return Task.CompletedTask;
        }

        public Task<CoreEvent> WaitForTerminalAsync() => terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<CoreEvent> WaitForTerminalAsync(int ordinal)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var terminals = events.Where(static item => item.Type is "completed" or "failed" or "cancelled").ToArray();
                if (terminals.Length >= ordinal) return terminals[ordinal - 1];
                await Task.Delay(10);
            }
            throw new TimeoutException($"Terminal event {ordinal} was not published.");
        }

        public Task<CoreEvent> WaitForTypeAsync(string type)
        {
            var existing = events.FirstOrDefault(item => item.Type == type);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var waiter = new TaskCompletionSource<CoreEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            typeWaiters[type] = waiter;
            return waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task<CoreEvent> WaitForToolAsync(string callId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var existing = events.FirstOrDefault(item => item.Type == "tool" && item.ToolCall?.Id == callId);
                if (existing is not null) return existing;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Tool event {callId} was not published.");
        }

        public async Task<CoreEvent> WaitForToolOrdinalAsync(int ordinal)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var tools = events.Where(static item => item.Type == CoreEventTypes.Tool).ToArray();
                if (tools.Length >= ordinal) return tools[ordinal - 1];
                await Task.Delay(10);
            }
            var observed = string.Join(", ", events.Select(item =>
                $"{item.Type}:{item.ToolCall?.Id}:{item.Error?.Code}"));
            throw new TimeoutException($"Tool event {ordinal} was not published. Observed: {observed}");
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public bool IsConfigured(string provider) => values.ContainsKey(provider);

        public void Save(string provider, string secret) => values[provider] = secret;

        public string? Load(string provider) => values.GetValueOrDefault(provider);

        public void Remove(string provider) => values.Remove(provider);
    }
}
