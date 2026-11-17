using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Velia.Core.Conversations;
using Velia.Core.DesktopVisual;
using Velia.Core.Rpc;
using Velia.Core.WindowsControl;
using Velia.Protocol;

namespace Velia.Core.Providers.Live;

internal sealed class GeminiLiveSession : IAsyncDisposable
{
    private const int MaximumReconnectAttempts = 2;
    private const int MaximumInputTranscriptCharacters = 32_768;
    private const int MaximumDirectSequenceCharacters = 320;
    private const int MaximumDirectSequenceBoundaries = 48;
    private const int MaximumDirectSequenceCommands = 8;
    // A Live interaction must release its command lane promptly. Long-running
    // document or research work belongs to the normal mode; Live reports a
    // bounded failure and remains available for the next command.
    private static readonly TimeSpan LiveSemanticTaskTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LiveDelegatedToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TranscriptQuietPeriod = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan TranscriptMaximumWait = TimeSpan.FromMilliseconds(1_200);
    private static readonly TimeSpan DirectWindowsFallbackGracePeriod = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DirectWindowsFallbackTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DirectCommandBurstTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DirectAnnouncementQuietPeriod = TimeSpan.FromMilliseconds(120);
    private static readonly string[] CompoundCommandConnectors =
        ["然后", "接着", "随后", "之后", "下一步", "再"];
    private static readonly string[] DirectCommandActionPrefixes =
    [
        "打开", "启动", "运行", "关闭", "退出", "关掉", "点击", "点开", "点一下", "选择",
        "进入", "访问", "搜索", "切换", "输入", "填写", "键入", "写入", "写到", "添加",
        "插入", "滚动", "滑动", "保存", "返回", "全选", "复制", "粘贴", "重命名", "更名",
        "滑到", "滚到", "向下滑", "向上滑", "往下滑", "往上滑",
        "移动", "放大", "最小化", "最大化",
    ];
    private static readonly string[] DirectCommandBoundaryLeadIns =
        ["请", "帮我", "帮忙", "麻烦", "现在"];
    private static readonly string[] SearchQueryContinuationMarkers =
        ["如何", "怎么", "怎样", "为什么", "是否", "能不能", "可不可以"];
    // Conversational, visual-answer, and status results stay on the native
    // Live voice. Verified computer actions use the local result event instead.
    // This guard retries only the remaining native-voice result paths.
    private static readonly TimeSpan SemanticResultAudioWait = TimeSpan.FromMilliseconds(1_200);
    private static readonly TimeSpan SilentChatRouteWait = TimeSpan.FromMilliseconds(2_200);
    private static readonly TimeSpan LocalTranscriptFusionWait = TimeSpan.FromMilliseconds(450);
    // Gemini can emit a short acknowledgement before the parallel local ASR
    // has classified the utterance as a deterministic computer action. Hold
    // only the first provider audio chunk long enough for that local decision;
    // subsequent audio remains streaming and deterministic execution itself is
    // never delayed by this gate.
    // Local ASR normally resolves deterministic desktop commands in 1-1.5 s.
    // The signal releases this wait immediately for ordinary chat, so this only
    // delays ambiguous first audio that could otherwise race local task TTS.
    private static readonly TimeSpan ProviderFirstAudioArbitrationDelay = TimeSpan.FromMilliseconds(1500);
    // A Live transcript is only useful while it is still adjacent to the
    // captured utterance. Older results are stale provider work and must never
    // become a new desktop/browser command after a later turn has completed.
    private static readonly TimeSpan LocalTranscriptMaximumAge = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LocalTranscriptMaximumFutureSkew = TimeSpan.FromSeconds(2);
    // Local ASR is independent from the Gemini socket. If it heard a complete
    // non-deterministic utterance but Gemini never acknowledges the matching
    // turn, the UI would otherwise remain in a false "listening" state while
    // only local commands continue to work.
    private static readonly TimeSpan DefaultProviderInputLivenessTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultProviderRecoveryResponseTimeout = TimeSpan.FromSeconds(6);
    private readonly string apiKey;
    private readonly ILiveSocketFactory sockets;
    private readonly ILiveEventSink events;
    private readonly IDelegatedTaskExecutor taskExecutor;
    private readonly ILiveSemanticRouter? semanticRouter;
    private readonly ILiveTimingSink? timing;
    private readonly TimeSpan providerInputLivenessTimeout;
    private readonly TimeSpan providerRecoveryResponseTimeout;
    private readonly Stopwatch sessionClock = Stopwatch.StartNew();
    private readonly Stopwatch turnClock = new();
    private readonly Channel<BufferedLiveAudio> audio = Channel.CreateBounded<BufferedLiveAudio>(new BoundedChannelOptions(12)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim reconnectGate = new(1, 1);
    private readonly SemaphoreSlim directCommandLane = new(1, 1);
    private readonly SemaphoreSlim semanticCommandLane = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingLiveToolCall> pendingToolCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> delegatedTaskCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> delegatedTasks = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StableLiveTranscriptBuffer inputTranscript = new(MaximumInputTranscriptCharacters);
    private readonly StableLiveTranscriptBuffer directFallbackTranscript = new(MaximumInputTranscriptCharacters);
    private readonly object directFallbackGate = new();
    private readonly object semanticRouterGate = new();
    private readonly object semanticCommandGate = new();
    private readonly object silentRetryGate = new();
    private readonly object localTranscriptGate = new();
    private TaskCompletionSource connectionReady = NewSignal();
    private ILiveSocket? socket;
    private Task? sendLoop;
    private Task? receiveLoop;
    private long sequence;
    private int bufferedChunks;
    private long audioGeneration;
    private int reconnectAttempts;
    private string? resumptionHandle;
    private Task? directFallbackWatcher;
    private Task? semanticRouterWatcher;
    private readonly Dictionary<string, DirectCommandExecution> directCommandExecutions = new(StringComparer.Ordinal);
    private CancellationTokenSource? activeSemanticBurstCancellation;
    private long activeSemanticStartedAtTimestamp;
    private int queuedSemanticCommands;
    private int delegatedToolCallsThisTurn;
    private long semanticTaskSequence;
    private long latestSemanticTaskSequence;
    private long latestDirectBatchSequence;
    private int suppressModelOutputForTask;
    private int providerTurnActive;
    private int localAsrHandledThisTurn;
    private int localAsrAwaitingProviderTranscript;
    private long providerInputTurnSequence;
    private long providerTurnActivitySequence;
    private long providerInputLivenessProbeSequence;
    private long latestClassifiedTurnSequence;
    private int latestClassifiedTurnAllowsLive;
    private long silentRetrySentTurnSequence;
    private string currentProviderTranscript = "";
    private string? lastKnownWebPlatform;
    private readonly List<LocalTranscriptCandidate> localTranscriptCandidates = [];
    private readonly HashSet<long> locallyHandledProviderTurns = [];
    private readonly HashSet<string> seenLocalUtteranceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TaskCompletionSource> localTranscriptArbitrationSignals = [];
    private DateTimeOffset providerTurnCompletedAtUtc;
    private long semanticResultInterruptionSequence;
    private long semanticResultAwaitingAudioSequence;
    private long semanticResultProviderTurnSequence;
    private int semanticResultAudioObserved;
    private int semanticResultAudioRetryIssued;
    private int reservedModelOutputSuppressed;
    private int recoveredTextTurnActive;
    private bool stopped;
    private bool setupAcknowledged;
    private bool firstOutputAudioObserved;

    public GeminiLiveSession(
        Guid id,
        string model,
        string voiceName,
        string apiKey,
        ILiveSocketFactory sockets,
        ILiveEventSink events,
        IDelegatedTaskExecutor taskExecutor,
        ILiveSemanticRouter? semanticRouter = null,
        ILiveTimingSink? timing = null,
        TimeSpan? providerInputLivenessTimeout = null,
        TimeSpan? providerRecoveryResponseTimeout = null)
    {
        Id = id;
        Model = model;
        VoiceName = voiceName;
        this.apiKey = apiKey;
        this.sockets = sockets;
        this.events = events;
        this.taskExecutor = taskExecutor;
        this.semanticRouter = semanticRouter;
        this.timing = timing;
        this.providerInputLivenessTimeout = providerInputLivenessTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultProviderInputLivenessTimeout;
        this.providerRecoveryResponseTimeout = providerRecoveryResponseTimeout is { } recovery && recovery > TimeSpan.Zero
            ? recovery
            : DefaultProviderRecoveryResponseTimeout;
    }

    public Guid Id { get; }
    public string Model { get; }
    public string VoiceName { get; }
    public string State { get; private set; } = LiveStates.Connecting;
    public int BufferedChunks => Math.Max(0, Volatile.Read(ref bufferedChunks));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(null, cancellationToken).ConfigureAwait(false);
        receiveLoop = ReceiveLoopAsync(lifetime.Token);
        sendLoop = SendLoopAsync(lifetime.Token);
        try
        {
            await setupComplete.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            RecordTiming("session_ready");
        }
        catch (TimeoutException exception)
        {
            throw new LiveProviderException("live_setup_timeout", "Gemini Live 连接超时，请检查网络后重试。", true, exception);
        }
    }

    public bool TryEnqueueAudio(string audioBase64)
    {
        if (stopped || State is LiveStates.Error or LiveStates.Inactive) return false;
        Interlocked.Increment(ref bufferedChunks);
        if (!audio.Writer.TryWrite(new BufferedLiveAudio(
            Volatile.Read(ref audioGeneration), audioBase64)))
        {
            Interlocked.Decrement(ref bufferedChunks);
            return false;
        }
        return true;
    }

    public async Task<bool> SendVideoAsync(string imageBase64, CancellationToken cancellationToken)
    {
        if (stopped || State is LiveStates.Error or LiveStates.Inactive) return false;
        for (var attempt = 0; attempt <= MaximumReconnectAttempts; attempt++)
        {
            ILiveSocket? attemptedSocket = null;
            try
            {
                await WaitForConnectionReadyAsync(cancellationToken).ConfigureAwait(false);
                await reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var current = socket ?? throw new LiveProviderException(
                        "live_connection_lost", "Gemini Live 连接已中断。", true);
                    attemptedSocket = current;
                    await current.SendTextAsync(
                        GeminiLiveProtocol.CreateVideo(imageBase64), cancellationToken).ConfigureAwait(false);
                    return true;
                }
                finally { reconnectGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) when (attempt < MaximumReconnectAttempts && !stopped)
            {
                if (!await TryReconnectAsync(attemptedSocket, cancellationToken).ConfigureAwait(false)) break;
            }
        }
        throw new LiveProviderException("live_video_send_failed", "实时画面发送中断。", true);
    }

    public async Task<bool> SubmitToolResultAsync(
        string callId,
        string toolName,
        bool ok,
        string value,
        CancellationToken cancellationToken)
    {
        if (stopped || !pendingToolCalls.TryGetValue(callId, out var pending) ||
            !string.Equals(pending.Name, toolName, StringComparison.Ordinal) ||
            !pending.TryBeginResponse()) return false;
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        sendCancellation.CancelAfter(SendTimeout);
        try
        {
            await WaitForConnectionReadyAsync(sendCancellation.Token).ConfigureAwait(false);
            await reconnectGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
            try
            {
                var current = socket ?? throw new LiveProviderException(
                    "live_connection_lost", "Gemini Live 连接已中断。", true);
                await current.SendTextAsync(
                    GeminiLiveProtocol.CreateToolResponse(callId, toolName, ok, value),
                    sendCancellation.Token).ConfigureAwait(false);
            }
            finally { reconnectGate.Release(); }
            pending.Complete();
            pendingToolCalls.TryRemove(callId, out _);
            return true;
        }
        catch
        {
            pending.Complete();
            pendingToolCalls.TryRemove(callId, out _);
            throw;
        }
    }

    public Task<LiveSubmitLocalTranscriptResult> SubmitLocalTranscriptAsync(
        string transcript,
        string utteranceId,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTranscript = LiveTranscriptNormalizer.Normalize(transcript);
        var captureAge = DateTimeOffset.UtcNow - capturedAtUtc;
        RecordTiming("local_asr_received", details: new Dictionary<string, object?>
        {
            ["transcriptCharacters"] = normalizedTranscript.Length,
            ["captureLagMs"] = Math.Max(0, (long)captureAge.TotalMilliseconds),
        });
        if (normalizedTranscript.Length == 0 || stopped)
            return Task.FromResult(new LiveSubmitLocalTranscriptResult(Id, false, false, false));
        if (captureAge > LocalTranscriptMaximumAge || captureAge < -LocalTranscriptMaximumFutureSkew)
        {
            RecordTiming("local_asr_dropped_stale", details: new Dictionary<string, object?>
            {
                ["captureLagMs"] = (long)captureAge.TotalMilliseconds,
                ["maximumAgeMs"] = (long)LocalTranscriptMaximumAge.TotalMilliseconds,
            });
            return Task.FromResult(new LiveSubmitLocalTranscriptResult(Id, false, false, false));
        }
        if (!RecordLocalTranscriptCandidate(
                utteranceId,
                normalizedTranscript,
                capturedAtUtc,
                out var assignedProviderTurn))
            return Task.FromResult(new LiveSubmitLocalTranscriptResult(Id, false, false, true));
        var livenessProbeSequence = Interlocked.Increment(ref providerInputLivenessProbeSequence);
        // Every accepted local utterance owns the next direct-result slot. A
        // previous utterance may still finish in the background, but its late
        // announcement must never describe or override this newer request.
        Interlocked.Exchange(ref latestDirectBatchSequence, 0);
        var candidates = SplitLocalCommandCandidates(normalizedTranscript);
        var directCommands = new List<(string Transcript, string Key)>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var allCandidatesAreDirect = true;
        foreach (var candidate in candidates)
        {
            if (!TryExpandDirectCommandCandidate(candidate, out var expanded))
            {
                allCandidatesAreDirect = false;
                break;
            }
            foreach (var command in expanded)
            {
                if (!seenKeys.Add(command.Key)) continue;
                directCommands.Add(command);
            }
        }
        // Never execute only the recognizable half of a compound request. If
        // one clause needs semantic reasoning, route the complete utterance so
        // ordering and user intent stay intact.
        if (!allCandidatesAreDirect || directCommands.Count == 0)
        {
            // Local ASR normally arrives before Gemini emits its acknowledgement.
            // Once it has heard a clear computer-action request, reserve the
            // response lane for the verified task result even when the exact
            // action still needs semantic routing. This prevents a stray
            // native-voice “好的” before the local completion voice.
            if (LooksLikeExplicitTaskFragment(normalizedTranscript))
            {
                Interlocked.Exchange(ref suppressModelOutputForTask, 1);
                RecordTiming("local_action_output_reserved");
            }
            StartProviderInputLivenessWatcher(
                normalizedTranscript,
                assignedProviderTurn,
                livenessProbeSequence);
            SignalLocalTranscriptArbitration(assignedProviderTurn);
            return Task.FromResult(new LiveSubmitLocalTranscriptResult(Id, false, false, false));
        }

        MarkProviderTurnHandledLocally(assignedProviderTurn);
        Interlocked.Exchange(ref localAsrHandledThisTurn, 1);
        if (Volatile.Read(ref providerTurnActive) == 0)
            Interlocked.Exchange(ref localAsrAwaitingProviderTranscript, 1);
        var anyNew = false;
        var anyDuplicate = false;
        var pending = new List<(DirectCommandExecution Execution, int Index)>();
        for (var index = 0; index < directCommands.Count; index++)
        {
            var command = directCommands[index];
            var execution = GetOrStartDeterministicExecution(
                command.Transcript,
                command.Key,
                allowCompletedReuse: Volatile.Read(ref providerTurnActive) != 0,
                localAsrOrigin: true,
                out var duplicate);
            if (duplicate)
            {
                anyDuplicate = true;
                continue;
            }

            anyNew = true;
            pending.Add((execution, index));
        }

        if (pending.Count > 0)
        {
            var taskSequence = Interlocked.Increment(ref semanticTaskSequence);
            Interlocked.Exchange(ref latestSemanticTaskSequence, taskSequence);
            Interlocked.Exchange(ref latestDirectBatchSequence, taskSequence);
            Interlocked.Exchange(ref suppressModelOutputForTask, 1);
            var batch = new DirectCommandBatch(taskSequence, pending.Count);
            RecordTiming("local_command_batch_started", details: new Dictionary<string, object?>
            {
                ["taskSequence"] = taskSequence,
                ["commandCount"] = pending.Count,
            });
            foreach (var item in pending)
            {
                var backgroundId = $"local-command-{utteranceId}-{item.Index}";
                var completion = CompleteQueuedLocalCommandAsync(
                    item.Execution,
                    batch,
                    backgroundId);
                delegatedTasks[backgroundId] = completion;
                _ = completion.ContinueWith(
                    completed => delegatedTasks.TryRemove(backgroundId, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        SignalLocalTranscriptArbitration(assignedProviderTurn);
        // Accepted means the command is durably queued. Verification is
        // published asynchronously so receiving the next utterance never waits
        // for a browser page or desktop operation to finish.
        return Task.FromResult(new LiveSubmitLocalTranscriptResult(Id, true, false, !anyNew && anyDuplicate));
    }

    private async Task CompleteQueuedLocalCommandAsync(
        DirectCommandExecution execution,
        DirectCommandBatch batch,
        string backgroundId)
    {
        var timer = Stopwatch.StartNew();
        PendingDirectAnnouncement? announcement = null;
        try
        {
            var result = await execution.Task.ConfigureAwait(false);
            RecordTiming("local_command_completed", details: new Dictionary<string, object?>
            {
                ["taskSequence"] = batch.TaskSequence,
                ["route"] = GetSafeDirectRouteName(execution.Key),
                ["ok"] = result.Ok,
                ["duplicate"] = false,
                ["executionMs"] = timer.ElapsedMilliseconds,
                ["queueMs"] = execution.QueueDelayMilliseconds,
            });
            if (Volatile.Read(ref delegatedToolCallsThisTurn) == 0 && execution.TryMarkResultInjected())
                announcement = new PendingDirectAnnouncement(batch.TaskSequence, result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            RecordTiming("local_command_failed", details: new Dictionary<string, object?>
            {
                ["commandId"] = backgroundId,
            });
        }
        finally
        {
            if (batch.Complete(announcement)) StartDirectBatchFlush(batch);
        }
    }

    private void StartDirectBatchFlush(DirectCommandBatch batch)
    {
        var backgroundId = $"local-announcement-{batch.TaskSequence}";
        var task = Task.Run(() => FlushDirectBatchAsync(batch), CancellationToken.None);
        delegatedTasks[backgroundId] = task;
        _ = task.ContinueWith(
            completed => delegatedTasks.TryRemove(backgroundId, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task FlushDirectBatchAsync(DirectCommandBatch batch)
    {
        try
        {
            await Task.Delay(DirectAnnouncementQuietPeriod, lifetime.Token).ConfigureAwait(false);
            var announcements = batch.GetAnnouncements();
            if (announcements.Length == 0 || stopped) return;
            if (batch.TaskSequence != Volatile.Read(ref latestDirectBatchSequence))
            {
                RecordTiming("direct_announcement_superseded", details: new Dictionary<string, object?>
                {
                    ["taskSequence"] = batch.TaskSequence,
                    ["count"] = announcements.Length,
                });
                return;
            }

            var result = CombineDirectAnnouncements(announcements);
            RecordTiming("direct_announcements_coalesced", details: new Dictionary<string, object?>
            {
                ["taskSequence"] = batch.TaskSequence,
                ["count"] = announcements.Length,
                ["successCount"] = announcements.Count(static item => item.Result.Ok),
                ["failureCount"] = announcements.Count(static item => !item.Result.Ok),
            });
            await PublishLocalTaskResultAsync(
                batch.TaskSequence,
                result,
                directBatch: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            RecordTiming("direct_announcement_failed");
        }
    }

    private static DelegatedTaskExecutionResult CombineDirectAnnouncements(
        IReadOnlyList<PendingDirectAnnouncement> announcements)
    {
        if (announcements.Count == 1) return announcements[0].Result;
        var successCount = announcements.Count(static item => item.Result.Ok);
        var failureCount = announcements.Count - successCount;
        var lastValue = NormalizeDelegatedTaskResult(
            announcements[^1].Result.Value,
            announcements[^1].Result.Ok);
        var summary = failureCount == 0
            ? $"前面的{announcements.Count}项操作均已完成。最后一项：{lastValue}"
            : $"前面的{announcements.Count}项操作已处理，其中{successCount}项成功、{failureCount}项失败。最后一项：{lastValue}";
        return new DelegatedTaskExecutionResult(failureCount == 0, summary);
    }

    private static string[] SplitLocalCommandCandidates(string transcript)
    {
        var punctuationParts = transcript.Split(
            ['。', '！', '!', '？', '?', '；', ';', '，', ',', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (punctuationParts.Length == 0) return [transcript];

        var commands = new List<string>(punctuationParts.Length);
        foreach (var part in punctuationParts) SplitConnectorBoundaries(part, commands);
        return commands.Count == 0 ? [transcript] : [.. commands];
    }

    private bool TryExpandDirectCommandCandidate(
        string candidate,
        out List<(string Transcript, string Key)> commands)
    {
        var wholeIsDirect = TryGetDirectTaskKey(candidate, out var wholeKey);

        if (candidate.Length > MaximumDirectSequenceCharacters)
        {
            commands = wholeIsDirect ? [(candidate, wholeKey)] : [];
            return wholeIsDirect;
        }

        var boundaries = FindDirectCommandBoundaries(candidate);
        if (boundaries.Length > MaximumDirectSequenceBoundaries)
        {
            commands = wholeIsDirect ? [(candidate, wholeKey)] : [];
            return wholeIsDirect;
        }
        var memo = new Dictionary<int, List<(string Transcript, string Key)>?>();
        var expanded = FindBestDirectCommandPartition(candidate, boundaries, 0, memo);
        if (expanded is not null &&
            expanded.Count is >= 2 and <= MaximumDirectSequenceCommands &&
            IsCredibleDirectCommandPartition(expanded))
        {
            commands = expanded;
            RecordTiming("local_command_sequence_segmented", details: new Dictionary<string, object?>
            {
                ["commandCount"] = commands.Count,
                ["transcriptCharacters"] = candidate.Length,
            });
            return true;
        }

        if (wholeIsDirect && HasUnresolvedCommandBoundary(candidate))
        {
            commands = [];
            return false;
        }

        if (wholeIsDirect)
        {
            commands = [(candidate, wholeKey)];
            return true;
        }

        commands = [];
        return false;
    }

    private bool HasUnresolvedCommandBoundary(string value)
    {
        var normalized = value.Trim();
        var startsAsSearchQuery = normalized.StartsWith("搜索", StringComparison.Ordinal) ||
            normalized.StartsWith("查找", StringComparison.Ordinal) ||
            normalized.StartsWith("查一下", StringComparison.Ordinal);
        if (startsAsSearchQuery && SearchQueryContinuationMarkers.Any(marker =>
                normalized.Contains(marker, StringComparison.Ordinal)))
            return false;

        foreach (var marker in CompoundCommandConnectors.Concat(DirectCommandBoundaryLeadIns))
        {
            var searchAt = 1;
            while (searchAt < normalized.Length)
            {
                var found = normalized.IndexOf(marker, searchAt, StringComparison.Ordinal);
                if (found < 0) break;
                var prefix = normalized[..found].Trim();
                var suffix = normalized[(found + marker.Length)..].Trim();
                if (suffix.Length > 0 && TryGetDirectTaskKey(prefix, out _)) return true;
                searchAt = found + 1;
            }
        }
        return false;
    }

    private static bool IsCredibleDirectCommandPartition(
        List<(string Transcript, string Key)> commands)
    {
        for (var index = 0; index < commands.Count - 1; index++)
        {
            var current = commands[index];
            if (!current.Key.StartsWith("browser:", StringComparison.Ordinal)) continue;
            var text = current.Transcript.Trim();
            if (SearchQueryContinuationMarkers
                .Any(marker => text.EndsWith(marker, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static int[] FindDirectCommandBoundaries(string value)
    {
        var boundaries = new HashSet<int> { 0, value.Length };
        foreach (var prefix in DirectCommandActionPrefixes)
        {
            var searchAt = 1;
            while (searchAt < value.Length)
            {
                var found = value.IndexOf(prefix, searchAt, StringComparison.Ordinal);
                if (found < 0) break;
                boundaries.Add(found);
                searchAt = found + 1;
            }
        }
        foreach (var leadIn in DirectCommandBoundaryLeadIns)
        {
            var searchAt = 1;
            while (searchAt < value.Length)
            {
                var found = value.IndexOf(leadIn, searchAt, StringComparison.Ordinal);
                if (found < 0) break;
                var suffix = value[(found + leadIn.Length)..].TrimStart();
                if (StartsWithDirectCommandAction(suffix)) boundaries.Add(found);
                searchAt = found + 1;
            }
        }
        return [.. boundaries.Order()];
    }

    private List<(string Transcript, string Key)>? FindBestDirectCommandPartition(
        string value,
        IReadOnlyList<int> boundaries,
        int start,
        IDictionary<int, List<(string Transcript, string Key)>?> memo)
    {
        if (start == value.Length) return [];
        if (memo.TryGetValue(start, out var cached)) return cached;

        List<(string Transcript, string Key)>? best = null;
        foreach (var end in boundaries.Where(boundary => boundary > start))
        {
            var segment = value[start..end].Trim();
            if (segment.Length == 0 ||
                (end < value.Length && DirectCommandBoundaryLeadIns.Any(marker =>
                    segment.EndsWith(marker, StringComparison.Ordinal))) ||
                !TryGetDirectTaskKey(segment, out var key)) continue;
            var remainder = FindBestDirectCommandPartition(value, boundaries, end, memo);
            if (remainder is null) continue;
            var candidate = new List<(string Transcript, string Key)>(remainder.Count + 1)
            {
                (segment, key),
            };
            candidate.AddRange(remainder);
            if (best is null || candidate.Count > best.Count ||
                (candidate.Count == best.Count && HasLaterCommandBoundaries(candidate, best)))
                best = candidate;
        }

        memo[start] = best;
        return best;
    }

    private static bool HasLaterCommandBoundaries(
        List<(string Transcript, string Key)> candidate,
        List<(string Transcript, string Key)> current)
    {
        // When ASR removes all pauses, both "打开百度 + 搜索人工智能打开记事本"
        // and "打开百度搜索人工智能 + 打开记事本" can look syntactically valid.
        // Prefer the later first boundary so a search keeps its query and a
        // following complete action begins at the next action verb.
        for (var index = 0; index < candidate.Count; index++)
        {
            var lengthComparison = candidate[index].Transcript.Length.CompareTo(current[index].Transcript.Length);
            if (lengthComparison != 0) return lengthComparison > 0;
        }
        return false;
    }

    private static void SplitConnectorBoundaries(string segment, ICollection<string> commands)
    {
        var start = 0;
        while (TryFindConnectorBoundary(segment, start, out var boundary, out var connectorLength))
        {
            AddCommandCandidate(segment[start..boundary], commands);
            start = boundary + connectorLength;
        }
        AddCommandCandidate(segment[start..], commands);
    }

    private static bool TryFindConnectorBoundary(
        string segment,
        int start,
        out int boundary,
        out int connectorLength)
    {
        boundary = -1;
        connectorLength = 0;
        foreach (var connector in CompoundCommandConnectors)
        {
            var searchAt = Math.Max(start + 1, 1);
            while (searchAt < segment.Length)
            {
                var found = segment.IndexOf(connector, searchAt, StringComparison.Ordinal);
                if (found < 0) break;
                var suffix = segment[(found + connector.Length)..].TrimStart();
                if (StartsWithDirectCommandAction(suffix) && (boundary < 0 || found < boundary))
                {
                    boundary = found;
                    connectorLength = connector.Length;
                    break;
                }
                searchAt = found + connector.Length;
            }
        }
        return boundary >= 0;
    }

    private static bool StartsWithDirectCommandAction(string value)
    {
        var candidate = value;
        var removedConnector = true;
        while (removedConnector)
        {
            removedConnector = false;
            foreach (var connector in CompoundCommandConnectors)
            {
                if (!candidate.StartsWith(connector, StringComparison.Ordinal)) continue;
                candidate = candidate[connector.Length..].TrimStart();
                removedConnector = true;
                break;
            }
        }
        return DirectCommandActionPrefixes.Any(prefix =>
            candidate.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void AddCommandCandidate(string value, ICollection<string> commands)
    {
        var candidate = value.Trim();
        while (candidate.Length > 0)
        {
            var connector = CompoundCommandConnectors.FirstOrDefault(prefix =>
                candidate.StartsWith(prefix, StringComparison.Ordinal) &&
                StartsWithDirectCommandAction(candidate[prefix.Length..].TrimStart()));
            if (connector is null) break;
            candidate = candidate[connector.Length..].TrimStart();
        }
        if (candidate.Length > 0) commands.Add(candidate);
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        CancelAllPendingTools();
        audio.Writer.TryComplete();
        var current = socket;
        if (current is not null)
        {
            using var closeSend = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await current.SendTextAsync(GeminiLiveProtocol.CreateAudioStreamEnd(), closeSend.Token).ConfigureAwait(false); }
            catch (Exception) { }
        }
        lifetime.Cancel();
        await AwaitBackgroundAsync(Task.WhenAll(delegatedTasks.Values), StopDrainTimeout).ConfigureAwait(false);
        await AwaitBackgroundAsync(semanticRouterWatcher, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await AwaitBackgroundAsync(directFallbackWatcher, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await AwaitBackgroundAsync(sendLoop, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await AwaitBackgroundAsync(receiveLoop, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        socket = null;
        State = LiveStates.Inactive;
        await PublishAsync(LiveEventTypes.Closed, state: LiveStates.Inactive).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lifetime.Dispose();
        reconnectGate.Dispose();
        directCommandLane.Dispose();
        semanticCommandLane.Dispose();
    }

    private async Task ConnectAsync(string? handle, CancellationToken cancellationToken)
    {
        var next = sockets.Create();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var endpoint = new Uri(
                "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" +
                $"?key={Uri.EscapeDataString(apiKey)}");
            await next.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            await next.SendTextAsync(GeminiLiveProtocol.CreateSetup(Model, VoiceName, handle), timeout.Token).ConfigureAwait(false);
            socket = next;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw new LiveProviderException("live_connection_timeout", "连接 Gemini Live 超时，请检查能否访问 Google 服务。", true, exception);
        }
        catch (OperationCanceledException)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (LiveProviderException)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not LiveProviderException)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw new LiveProviderException("live_connection_failed", "无法连接 Gemini Live，请检查网络和 API 配置。", true, exception);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in audio.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref bufferedChunks);
                if (chunk.Generation != Volatile.Read(ref audioGeneration)) continue;
                await SendChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            await FailAsync("live_audio_send_failed", "实时音频发送中断，请重新开始 Live。", true).ConfigureAwait(false);
        }
    }

    private async Task SendChunkAsync(BufferedLiveAudio chunk, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= MaximumReconnectAttempts; attempt++)
        {
            if (chunk.Generation != Volatile.Read(ref audioGeneration)) return;
            ILiveSocket? attemptedSocket = null;
            try
            {
                await WaitForConnectionReadyAsync(cancellationToken).ConfigureAwait(false);
                await reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var current = socket ?? throw new LiveProviderException("live_connection_lost", "Gemini Live 连接已中断。", true);
                    attemptedSocket = current;
                    await current.SendTextAsync(GeminiLiveProtocol.CreateAudio(chunk.Base64), cancellationToken).ConfigureAwait(false);
                    return;
                }
                finally { reconnectGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) when (attempt < MaximumReconnectAttempts && !stopped)
            {
                if (!await TryReconnectAsync(attemptedSocket, cancellationToken).ConfigureAwait(false)) break;
            }
        }
        throw new LiveProviderException("live_audio_send_failed", "实时音频发送中断。", true);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var current = socket;
            try
            {
                if (current is null) break;
                var json = await current.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    if (!await TryReconnectAsync(current, cancellationToken).ConfigureAwait(false)) break;
                    continue;
                }
                var message = GeminiLiveProtocol.Parse(json);
                if (HasProviderTurnActivity(message))
                    Interlocked.Increment(ref providerTurnActivitySequence);
                if (message.SetupComplete)
                {
                    var recovered = State == LiveStates.Reconnecting;
                    setupAcknowledged = true;
                    reconnectAttempts = 0;
                    State = LiveStates.Listening;
                    setupComplete.TrySetResult();
                    Volatile.Read(ref connectionReady).TrySetResult();
                    await PublishAsync(LiveEventTypes.Setup, state: State).ConfigureAwait(false);
                    await PublishAsync(LiveEventTypes.Listening, state: State).ConfigureAwait(false);
                    if (recovered) RecordTiming("reconnect_ready");
                }
                if (!string.IsNullOrEmpty(message.ResumptionHandle)) resumptionHandle = message.ResumptionHandle;
                if (message.Interrupted)
                {
                    Interlocked.Exchange(ref providerTurnActive, 0);
                    Interlocked.Exchange(ref recoveredTextTurnActive, 0);
                    var interruptedForSemanticResult =
                        Interlocked.Exchange(ref semanticResultInterruptionSequence, 0) != 0;
                    ClearInputTranscript();
                    if (interruptedForSemanticResult)
                        RecordTiming("semantic_result_interrupted_stale_turn");
                    State = LiveStates.Listening;
                    // A verified local result may interrupt the stale Gemini turn that was
                    // already in flight. That provider acknowledgement is internal cleanup,
                    // not a user barge-in: publishing it would make the desktop cancel the
                    // fresh local completion announcement that has just started playing.
                    if (!interruptedForSemanticResult)
                        await PublishAsync(LiveEventTypes.Interrupted, state: State).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(message.InputTranscript))
                {
                    var normalizedInputTranscript = LiveTranscriptNormalizer.Normalize(message.InputTranscript);
                    if (normalizedInputTranscript.Length == 0) continue;
                    if (!turnClock.IsRunning)
                    {
                        if (Interlocked.Exchange(ref localAsrAwaitingProviderTranscript, 0) == 0)
                            ResetDeterministicTurnState();
                        turnClock.Restart();
                        firstOutputAudioObserved = false;
                        Interlocked.Exchange(ref providerTurnActive, 1);
                        var newProviderTurn = Interlocked.Increment(ref providerInputTurnSequence);
                        CancelPendingToolsBeforeTurn(newProviderTurn);
                        lock (silentRetryGate) currentProviderTranscript = "";
                    }
                    lock (silentRetryGate)
                    {
                        if (currentProviderTranscript.Length + normalizedInputTranscript.Length <= MaximumInputTranscriptCharacters)
                            currentProviderTranscript += normalizedInputTranscript;
                    }
                    inputTranscript.Append(normalizedInputTranscript, message.InputTranscriptFinished);
                    if (semanticRouter is null)
                    {
                        directFallbackTranscript.Append(normalizedInputTranscript, message.InputTranscriptFinished);
                        StartDirectWindowsFallbackWatcher();
                    }
                    else
                    {
                        if (!LooksLikeSpeechOnlyRequest(inputTranscript.CurrentText) &&
                            !LooksLikeVisualQuestionForLive(inputTranscript.CurrentText) &&
                            (taskExecutor.GetActiveTaskSummary() is not null ||
                            LooksLikeExplicitTaskFragment(inputTranscript.CurrentText)))
                            Interlocked.Exchange(ref suppressModelOutputForTask, 1);
                        StartSemanticRouterWatcher();
                    }
                    RecordTiming("input_transcript", details: new Dictionary<string, object?>
                    {
                        ["fragmentCharacters"] = normalizedInputTranscript.Length,
                        ["finished"] = message.InputTranscriptFinished,
                    });
                    await PublishAsync(LiveEventTypes.InputTranscript, transcript: normalizedInputTranscript).ConfigureAwait(false);
                }
                var cancelledThisMessage = message.CancelledToolCallIds.ToHashSet(StringComparer.Ordinal);
                foreach (var callId in message.CancelledToolCallIds)
                {
                    CancelPendingTool(callId);
                }
                foreach (var call in message.ToolCalls)
                {
                    RecordTiming("tool_call", details: new Dictionary<string, object?>
                    {
                        ["tool"] = call.Name,
                    });
                    if (cancelledThisMessage.Contains(call.Id)) continue;
                    if (pendingToolCalls.Count >= 2 ||
                        !pendingToolCalls.TryAdd(
                            call.Id,
                            new PendingLiveToolCall(
                                call.Name,
                                ResolveIncomingToolTurn())))
                    {
                        await SendRejectedToolCallAsync(
                            call.Id,
                            call.Name,
                            "当前已有工具任务正在处理，请稍后重试。",
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (call.Name == GeminiLiveProtocol.DelegateTaskTool)
                    {
                        Interlocked.Exchange(ref delegatedToolCallsThisTurn, 1);
                        StartDelegatedTask(call);
                        continue;
                    }
                    await PublishAsync(
                        LiveEventTypes.ToolCall,
                        toolCall: new LiveToolCall(call.Id, call.Name, call.Input)).ConfigureAwait(false);
                }
                var reservedOutput = ContainsReservedLiveMarker(message.OutputTranscript);
                if (reservedOutput)
                {
                    Interlocked.Exchange(ref reservedModelOutputSuppressed, 1);
                    Interlocked.Exchange(ref suppressModelOutputForTask, 1);
                    RecordTiming("reserved_model_output_suppressed");
                }
                if (!string.IsNullOrEmpty(message.OutputTranscript) &&
                    !reservedOutput &&
                    Volatile.Read(ref suppressModelOutputForTask) == 0)
                    await PublishAsync(LiveEventTypes.OutputTranscript, transcript: message.OutputTranscript).ConfigureAwait(false);
                foreach (var output in message.Audio)
                {
                    if (Volatile.Read(ref suppressModelOutputForTask) != 0) continue;
                    if (Volatile.Read(ref semanticResultAwaitingAudioSequence) != 0)
                        Interlocked.Exchange(ref semanticResultAudioObserved, 1);
                    if (!firstOutputAudioObserved)
                    {
                        firstOutputAudioObserved = true;
                        RecordTiming("first_output_audio");
                        await WaitForLocalTranscriptArbitrationAsync(
                            Volatile.Read(ref providerInputTurnSequence),
                            cancellationToken).ConfigureAwait(false);
                        if (Volatile.Read(ref suppressModelOutputForTask) != 0)
                        {
                            RecordTiming("first_output_audio_suppressed_after_local_arbitration");
                            continue;
                        }
                    }
                    State = LiveStates.Speaking;
                    await PublishAsync(LiveEventTypes.Audio, state: State, audioBase64: output, mimeType: "audio/pcm;rate=24000").ConfigureAwait(false);
                }
                if (message.TurnComplete)
                {
                    var completedRecoveredTextTurn =
                        Interlocked.Exchange(ref recoveredTextTurnActive, 0) != 0;
                    var completedAfterSemanticResultInjection =
                        Interlocked.Exchange(ref semanticResultInterruptionSequence, 0) != 0;
                    var completedAfterReservedOutput =
                        Interlocked.Exchange(ref reservedModelOutputSuppressed, 0) != 0;
                    if (completedAfterSemanticResultInjection ||
                        completedAfterReservedOutput ||
                        Volatile.Read(ref localAsrHandledThisTurn) != 0)
                        Interlocked.Exchange(ref suppressModelOutputForTask, 0);
                    var completedWithoutAudio = !firstOutputAudioObserved;
                    var completedTurnSequence = Volatile.Read(ref providerInputTurnSequence);
                    string completedTranscript;
                    lock (silentRetryGate) completedTranscript = currentProviderTranscript;
                    RecordTiming("turn_complete");
                    Interlocked.Exchange(ref providerTurnActive, 0);
                    lock (localTranscriptGate) providerTurnCompletedAtUtc = DateTimeOffset.UtcNow;
                    PruneLocalTranscriptArbitrationSignals(completedTurnSequence);
                    if (semanticRouter is null || completedRecoveredTextTurn)
                    {
                        ClearInputTranscript();
                    }
                    else
                    {
                        StartSemanticRouterWatcher();
                        Interlocked.Exchange(ref delegatedToolCallsThisTurn, 0);
                        turnClock.Reset();
                        firstOutputAudioObserved = false;
                    }
                    State = LiveStates.Listening;
                    await PublishAsync(LiveEventTypes.TurnComplete, state: State).ConfigureAwait(false);
                    await PublishAsync(LiveEventTypes.Listening, state: State).ConfigureAwait(false);
                    if (semanticRouter is not null && completedWithoutAudio &&
                        !IsProviderTurnHandledLocally(completedTurnSequence) &&
                        completedTurnSequence > 0 && !string.IsNullOrWhiteSpace(completedTranscript))
                        StartSilentChatRetry(completedTurnSequence, completedTranscript);
                }
                if (message.GoAway && !await TryReconnectAsync(current, cancellationToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (LiveProviderException exception) when (!setupAcknowledged)
            {
                await FailAsync(exception.Code, exception.Message, exception.Retryable).ConfigureAwait(false);
                break;
            }
            catch (Exception) when (!stopped)
            {
                if (await TryReconnectAsync(current, cancellationToken).ConfigureAwait(false)) continue;
                await FailAsync("live_connection_failed", "Gemini Live 连接中断，请检查网络后重新开始。", true).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task<bool> TryReconnectAsync(ILiveSocket? failedSocket, CancellationToken cancellationToken)
    {
        if (stopped || cancellationToken.IsCancellationRequested) return false;
        await reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopped) return false;
            if (socket is not null && failedSocket is not null && !ReferenceEquals(socket, failedSocket))
            {
                RecordTiming("reconnect_joined_existing_socket");
                return true;
            }
            if (reconnectAttempts >= MaximumReconnectAttempts) return false;
            reconnectAttempts++;
            State = LiveStates.Reconnecting;
            Interlocked.Increment(ref audioGeneration);
            RecordTiming("reconnect_started", details: new Dictionary<string, object?>
            {
                ["attempt"] = reconnectAttempts,
            });
            await PublishAsync(LiveEventTypes.Reconnecting, state: State, message: $"正在恢复 Live 会话（{reconnectAttempts}/{MaximumReconnectAttempts}）").ConfigureAwait(false);
            CancelAllPendingTools();
            ClearInputTranscript();
            lock (localTranscriptGate)
            {
                localTranscriptCandidates.Clear();
                providerTurnCompletedAtUtc = default;
            }
            setupAcknowledged = false;
            Volatile.Write(ref connectionReady, NewSignal());
            var old = socket;
            socket = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            // Gemini 3.1 Preview can acknowledge a resumed socket while never
            // accepting later microphone turns. Always establish a clean
            // native-audio session after transport loss; a pending local ASR
            // utterance is replayed separately by the liveness watcher.
            resumptionHandle = null;
            ResetFreshProviderSessionState();
            await ConnectAsync(null, cancellationToken).ConfigureAwait(false);
            RecordTiming("reconnect_socket_connected");
            return true;
        }
        catch (Exception)
        {
            RecordTiming("reconnect_failed");
            return false;
        }
        finally
        {
            reconnectGate.Release();
        }
    }

    private async Task FailAsync(string code, string message, bool retryable)
    {
        State = LiveStates.Error;
        setupComplete.TrySetException(new LiveProviderException(code, message, retryable));
        await PublishAsync(LiveEventTypes.Failed, state: State, message: message, retryable: retryable).ConfigureAwait(false);
    }

    private Task PublishAsync(
        string type,
        string? state = null,
        string? audioBase64 = null,
        string? mimeType = null,
        string? transcript = null,
        LiveToolCall? toolCall = null,
        string? message = null,
        bool? retryable = null,
        bool? resultOk = null) => events.PublishAsync(new LiveEvent(
            Id, Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, type,
            state, audioBase64, mimeType, transcript, toolCall, message, retryable, resultOk), CancellationToken.None);

    private void StartSemanticRouterWatcher()
    {
        if (semanticRouter is null || stopped) return;
        lock (semanticRouterGate)
        {
            if (semanticRouterWatcher is { IsCompleted: false }) return;
            semanticRouterWatcher = Task.Run(CaptureSemanticTranscriptAsync, CancellationToken.None);
        }
    }

    private async Task CaptureSemanticTranscriptAsync()
    {
        string? transcript = null;
        try
        {
            transcript = await inputTranscript.TakeWhenStableAsync(
                TranscriptQuietPeriod,
                TranscriptMaximumWait,
                lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lock (semanticRouterGate) semanticRouterWatcher = null;
        }

        if (!string.IsNullOrWhiteSpace(transcript) && !stopped)
            StartSemanticTask(transcript);
        if (inputTranscript.HasText && !stopped)
            StartSemanticRouterWatcher();
    }

    private void StartSemanticTask(string transcript)
    {
        var taskId = $"semantic-{Interlocked.Increment(ref semanticTaskSequence)}";
        var providerTurnSequence = Volatile.Read(ref providerInputTurnSequence);
        var task = Task.Run(
            () => RunSemanticTaskAsync(taskId, providerTurnSequence, transcript),
            CancellationToken.None);
        delegatedTasks[taskId] = task;
        _ = task.ContinueWith(
            _ => delegatedTasks.TryRemove(taskId, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunSemanticTaskAsync(string taskId, long providerTurnSequence, string transcript)
    {
        if (semanticRouter is null || stopped) return;
        try
        {
            transcript = await FuseSemanticTranscriptAsync(
                transcript,
                providerTurnSequence,
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentProviderTurn(providerTurnSequence))
            {
                RecordTiming("semantic_router_skipped_stale_turn");
                return;
            }
            if (IsProviderTurnHandledLocally(providerTurnSequence) &&
                TryGetDirectTaskKey(transcript, out _))
            {
                RecordTiming("semantic_router_skipped_local_asr");
                return;
            }
            if (BrowserNavigationIntentDetector.TryParseExplicitCommand(
                    transcript,
                    GetEffectiveWebPlatform(),
                    out var browserNavigation))
            {
                var browserTaskSequence = long.Parse(
                    taskId.AsSpan("semantic-".Length),
                    System.Globalization.CultureInfo.InvariantCulture);
                Interlocked.Exchange(ref latestSemanticTaskSequence, browserTaskSequence);
                Interlocked.Exchange(ref suppressModelOutputForTask, 1);
                RecordTiming("semantic_browser_fast_path");
                var browserKey = $"browser:{browserNavigation.ExpectedHost}:{browserNavigation.ExpectedPathPrefix}:{browserNavigation.Url}";
                var browserExecution = GetOrStartDeterministicExecution(
                    transcript,
                    browserKey,
                    allowCompletedReuse: true,
                    localAsrOrigin: false,
                    out _);
                var browserResult = await browserExecution.Task.ConfigureAwait(false);
                if (IsCurrentProviderTurn(providerTurnSequence) && browserExecution.TryMarkResultInjected())
                    await PublishLocalTaskResultAsync(
                        browserTaskSequence,
                        browserResult,
                        providerTurnSequence: providerTurnSequence).ConfigureAwait(false);
                return;
            }
            var activeTask = taskExecutor.GetActiveTaskSummary();
            var classification = semanticRouter.ClassifyAsync(transcript, activeTask, lifetime.Token);

            if (IsProviderTurnHandledLocally(providerTurnSequence))
            {
                ObserveFault(classification);
                RecordTiming("semantic_router_skipped_local_asr");
                return;
            }

            if (TryGetDirectTaskKey(transcript, out var deterministicKey))
            {
                var taskSequence = long.Parse(taskId.AsSpan("semantic-".Length), System.Globalization.CultureInfo.InvariantCulture);
                Interlocked.Exchange(ref latestSemanticTaskSequence, taskSequence);
                Interlocked.Exchange(ref suppressModelOutputForTask, 1);
                RecordTiming("semantic_deterministic_fast_path");
                var deterministicExecution = GetOrStartDeterministicExecution(
                    transcript,
                    deterministicKey,
                    allowCompletedReuse: true,
                    localAsrOrigin: false,
                    out _);
                var result = await deterministicExecution.Task.ConfigureAwait(false);
                ObserveFault(classification);
                if (IsCurrentProviderTurn(providerTurnSequence) && deterministicExecution.TryMarkResultInjected())
                    await PublishLocalTaskResultAsync(
                        taskSequence,
                        result,
                        providerTurnSequence: providerTurnSequence).ConfigureAwait(false);
                return;
            }

            var route = NormalizeSemanticRoute(
                await classification.ConfigureAwait(false), transcript, activeTask);
            if (!IsCurrentProviderTurn(providerTurnSequence))
            {
                RecordTiming("semantic_router_skipped_stale_turn");
                return;
            }
            if (IsProviderTurnHandledLocally(providerTurnSequence))
            {
                RecordTiming("semantic_router_skipped_local_asr");
                return;
            }
            RecordTiming("semantic_router_classified", details: new Dictionary<string, object?>
            {
                ["category"] = route.Category.ToString().ToLowerInvariant(),
                ["controlAction"] = route.ControlAction.ToString().ToLowerInvariant(),
                ["responseAuthority"] = route.ResponseAuthority.ToString().ToLowerInvariant(),
                ["confidence"] = route.Confidence,
                ["fallback"] = route.Fallback,
                ["durationMs"] = route.DurationMilliseconds,
            });

            Volatile.Write(ref latestClassifiedTurnAllowsLive,
                route.Category == LiveSemanticCategory.Chat &&
                route.ResponseAuthority == LiveResponseAuthority.Live ? 1 : 0);
            Interlocked.Exchange(ref latestClassifiedTurnSequence, providerTurnSequence);

            if (!route.ShouldDispatch)
            {
                Interlocked.Exchange(ref suppressModelOutputForTask, 0);
                return;
            }
            if (Volatile.Read(ref delegatedToolCallsThisTurn) != 0)
            {
                RecordTiming("semantic_router_skipped_existing_tool_call");
                return;
            }

            var sequence = long.Parse(taskId.AsSpan("semantic-".Length), System.Globalization.CultureInfo.InvariantCulture);
            Interlocked.Exchange(ref latestSemanticTaskSequence, sequence);
            Interlocked.Exchange(ref suppressModelOutputForTask, 1);
            var relationship = ResolveMissionRelationship(route);
            RecordTiming("semantic_task_dispatched", details: new Dictionary<string, object?>
            {
                ["relationship"] = relationship,
            });
            var execution = await ExecuteSemanticCommandAsync(
                transcript,
                relationship,
                lifetime.Token).ConfigureAwait(false);
            if (!IsCurrentProviderTurn(providerTurnSequence))
            {
                RecordTiming("semantic_result_dropped_stale_turn");
                return;
            }
            if (route.Category == LiveSemanticCategory.Action &&
                LooksLikeExplicitTaskFragment(transcript) &&
                !DesktopContextIntentDetector.IsExplicitReadOnlySelectionRequest(transcript))
                await PublishLocalTaskResultAsync(
                    sequence,
                    execution,
                    providerTurnSequence: providerTurnSequence).ConfigureAwait(false);
            else
                await PublishSemanticTaskResultAsync(
                    sequence,
                    providerTurnSequence,
                    execution,
                    route.ControlAction == LiveControlAction.Progress,
                    lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            Interlocked.Exchange(ref suppressModelOutputForTask, 0);
            RecordTiming("semantic_task_failed");
        }
    }

    private void StartProviderInputLivenessWatcher(
        string transcript,
        long assignedProviderTurn,
        long probeSequence)
    {
        if (stopped) return;
        var currentProviderTurn = Volatile.Read(ref providerInputTurnSequence);
        var awaitingNewProviderInput = assignedProviderTurn > currentProviderTurn;
        if (!awaitingNewProviderInput &&
            (assignedProviderTurn != currentProviderTurn || Volatile.Read(ref providerTurnActive) == 0))
            return;
        var providerActivity = Volatile.Read(ref providerTurnActivitySequence);
        var taskId = $"provider-liveness-{probeSequence}";
        var task = Task.Run(
            () => RecoverUnacknowledgedProviderInputAsync(
                transcript,
                assignedProviderTurn,
                awaitingNewProviderInput,
                probeSequence,
                providerActivity),
            CancellationToken.None);
        delegatedTasks[taskId] = task;
        _ = task.ContinueWith(
            completed => delegatedTasks.TryRemove(taskId, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RecoverUnacknowledgedProviderInputAsync(
        string transcript,
        long assignedProviderTurn,
        bool awaitingNewProviderInput,
        long probeSequence,
        long providerActivityAtSubmission)
    {
        try
        {
            await Task.Delay(providerInputLivenessTimeout, lifetime.Token).ConfigureAwait(false);
            var currentProviderTurn = Volatile.Read(ref providerInputTurnSequence);
            var providerAcknowledged = awaitingNewProviderInput
                ? assignedProviderTurn <= currentProviderTurn
                : assignedProviderTurn != currentProviderTurn || Volatile.Read(ref providerTurnActive) == 0;
            if (stopped ||
                probeSequence != Volatile.Read(ref providerInputLivenessProbeSequence) ||
                providerAcknowledged ||
                providerActivityAtSubmission != Volatile.Read(ref providerTurnActivitySequence))
                return;

            RecordTiming("provider_input_liveness_expired", details: new Dictionary<string, object?>
            {
                ["assignedProviderTurn"] = assignedProviderTurn,
                ["awaitingNewProviderInput"] = awaitingNewProviderInput,
            });
            var failedSocket = socket;
            if (!await TryReconnectAsync(failedSocket, lifetime.Token).ConfigureAwait(false))
            {
                await FailAsync(
                    "live_unresponsive",
                    "Live 对话连接已经失去响应，请重新开始 Live。",
                    true).ConfigureAwait(false);
                return;
            }

            await WaitForConnectionReadyAsync(lifetime.Token).ConfigureAwait(false);
            if (stopped || probeSequence != Volatile.Read(ref providerInputLivenessProbeSequence)) return;

            var normalized = transcript.Trim();
            if (normalized.Length > 1_000) normalized = normalized[..1_000];
            PrepareRecoveredTextTurn(normalized);
            var activityBeforeReplay = Volatile.Read(ref providerTurnActivitySequence);
            await SendRealtimeTextAsync(
                $"VELIA_LIVE_RETRY 用户原话：{normalized}。请直接继续本轮回应；如需查看当前画面，请正常调用屏幕工具。",
                lifetime.Token).ConfigureAwait(false);
            RecordTiming("provider_input_replayed_after_fresh_reconnect");

            await Task.Delay(providerRecoveryResponseTimeout, lifetime.Token).ConfigureAwait(false);
            if (stopped ||
                probeSequence != Volatile.Read(ref providerInputLivenessProbeSequence) ||
                activityBeforeReplay != Volatile.Read(ref providerTurnActivitySequence))
                return;

            RecordTiming("provider_recovery_unresponsive");
            await FailAsync(
                "live_unresponsive",
                "Live 对话连接没有恢复，请重新开始 Live。",
                true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            RecordTiming("provider_input_liveness_recovery_failed");
            await FailAsync(
                "live_unresponsive",
                "Live 对话连接恢复失败，请重新开始 Live。",
                true).ConfigureAwait(false);
        }
    }

    private void PrepareRecoveredTextTurn(string transcript)
    {
        ClearInputTranscript();
        ResetDeterministicTurnState();
        inputTranscript.Append(transcript, isFinished: true);
        lock (silentRetryGate) currentProviderTranscript = transcript;
        turnClock.Restart();
        firstOutputAudioObserved = false;
        Interlocked.Exchange(ref providerInputTurnSequence, 1);
        Interlocked.Exchange(ref providerTurnActive, 1);
        Interlocked.Exchange(ref recoveredTextTurnActive, 1);
    }

    private static bool HasProviderTurnActivity(GeminiLiveServerMessage message) =>
        !string.IsNullOrEmpty(message.InputTranscript) ||
        !string.IsNullOrEmpty(message.OutputTranscript) ||
        message.Audio.Count > 0 ||
        message.ToolCalls.Count > 0 ||
        message.CancelledToolCallIds.Count > 0 ||
        message.Interrupted ||
        message.GenerationComplete ||
        message.TurnComplete;

    private void StartSilentChatRetry(long turnSequence, string transcript)
    {
        var taskId = $"silent-retry-{turnSequence}";
        if (delegatedTasks.ContainsKey(taskId)) return;
        RecordTiming("silent_chat_retry_scheduled");
        var task = Task.Run(
            () => RetrySilentChatAsync(turnSequence, transcript),
            CancellationToken.None);
        if (!delegatedTasks.TryAdd(taskId, task)) return;
        _ = task.ContinueWith(
            _ => delegatedTasks.TryRemove(taskId, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RetrySilentChatAsync(long turnSequence, string transcript)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow + SilentChatRouteWait;
            while (Volatile.Read(ref latestClassifiedTurnSequence) < turnSequence &&
                DateTimeOffset.UtcNow < deadline && !stopped)
            {
                await Task.Delay(50, lifetime.Token).ConfigureAwait(false);
            }

            if (stopped || Volatile.Read(ref providerInputTurnSequence) != turnSequence ||
                Volatile.Read(ref latestClassifiedTurnSequence) != turnSequence ||
                Volatile.Read(ref latestClassifiedTurnAllowsLive) == 0 ||
                Volatile.Read(ref providerTurnActive) != 0 ||
                Volatile.Read(ref suppressModelOutputForTask) != 0)
            {
                RecordTiming("silent_chat_retry_skipped");
                return;
            }

            lock (silentRetryGate)
            {
                if (silentRetrySentTurnSequence >= turnSequence) return;
                silentRetrySentTurnSequence = turnSequence;
            }
            var normalized = transcript.Trim();
            if (normalized.Length > 1_000) normalized = normalized[..1_000];
            await SendRealtimeTextAsync(
                $"VELIA_LIVE_RETRY 用户原话：{normalized}。请现在直接用标准普通话回答。",
                lifetime.Token).ConfigureAwait(false);
            RecordTiming("silent_chat_retry_sent");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            RecordTiming("silent_chat_retry_failed");
        }
    }

    private async Task<DelegatedTaskExecutionResult> ExecuteSemanticCommandAsync(
        string transcript,
        string relationship,
        CancellationToken cancellationToken)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        lock (semanticCommandGate)
        {
            queuedSemanticCommands += 1;
            ApplyActiveSemanticBurstDeadline();
        }
        try
        {
            await semanticCommandLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (semanticCommandGate) queuedSemanticCommands = Math.Max(0, queuedSemanticCommands - 1);
            throw;
        }
        var burstCancellation = new CancellationTokenSource();
        try
        {
            lock (semanticCommandGate)
            {
                queuedSemanticCommands = Math.Max(0, queuedSemanticCommands - 1);
                activeSemanticBurstCancellation = burstCancellation;
                activeSemanticStartedAtTimestamp = Stopwatch.GetTimestamp();
                if (queuedSemanticCommands > 0) ApplyActiveSemanticBurstDeadline();
            }
            RecordTiming("semantic_command_lane_entered", details: new Dictionary<string, object?>
            {
                ["queueMs"] = (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds,
                ["burstDeadline"] = queuedSemanticCommands > 0,
            });
            using var executionTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                burstCancellation.Token);
            executionTimeout.CancelAfter(LiveSemanticTaskTimeout);
            try
            {
                return await taskExecutor.ExecuteRoutedTaskAsync(
                    transcript,
                    relationship,
                    executionTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && executionTimeout.IsCancellationRequested)
            {
                var yieldedToFollower = burstCancellation.IsCancellationRequested;
                RecordTiming(yieldedToFollower
                    ? "semantic_task_yielded_to_follower"
                    : "semantic_task_timed_out");
                return new DelegatedTaskExecutionResult(
                    false,
                    yieldedToFollower
                        ? "上一项任务响应过慢，已停止并继续处理后续指令。"
                        : "任务执行超时，已经停止。");
            }
        }
        finally
        {
            lock (semanticCommandGate)
            {
                if (ReferenceEquals(activeSemanticBurstCancellation, burstCancellation))
                {
                    activeSemanticBurstCancellation = null;
                    activeSemanticStartedAtTimestamp = 0;
                }
            }
            burstCancellation.Dispose();
            semanticCommandLane.Release();
        }
    }

    private void ApplyActiveSemanticBurstDeadline()
    {
        if (activeSemanticBurstCancellation is null || activeSemanticStartedAtTimestamp == 0) return;
        var remaining = DirectCommandBurstTimeout -
            Stopwatch.GetElapsedTime(activeSemanticStartedAtTimestamp);
        try
        {
            if (remaining <= TimeSpan.Zero) activeSemanticBurstCancellation.Cancel();
            else activeSemanticBurstCancellation.CancelAfter(remaining);
        }
        catch (ObjectDisposedException) { }
    }

    private LiveSemanticRoute NormalizeSemanticRoute(
        LiveSemanticRoute route,
        string transcript,
        DelegatedTaskSummary? activeTask)
    {
        if (DesktopContextIntentDetector.IsExplicitRequest(transcript))
        {
            return route with
            {
                Category = LiveSemanticCategory.Action,
                ControlAction = LiveControlAction.None,
                TaskUpdateMode = LiveTaskUpdateMode.None,
                ResponseAuthority = LiveResponseAuthority.Reasoner,
                RefersToActiveTask = false,
                CanonicalTask = "",
                Reason = "guarded-explicit-desktop-selection",
            };
        }
        if (LooksLikeScreenDerivedMutation(transcript))
        {
            return route with
            {
                Category = LiveSemanticCategory.Action,
                ControlAction = LiveControlAction.None,
                TaskUpdateMode = LiveTaskUpdateMode.None,
                ResponseAuthority = LiveResponseAuthority.Reasoner,
                RefersToActiveTask = false,
                CanonicalTask = "",
                Reason = "guarded-screen-derived-mutation",
            };
        }
        if (activeTask is not null && route.ControlAction == LiveControlAction.Progress &&
            LooksLikeExplicitTaskFragment(transcript))
        {
            return route with
            {
                Category = LiveSemanticCategory.Control,
                ControlAction = LiveControlAction.Correction,
                TaskUpdateMode = LiveTaskUpdateMode.Replace,
                ResponseAuthority = LiveResponseAuthority.Reasoner,
                RefersToActiveTask = true,
                Reason = "guarded-repeated-action-not-progress",
            };
        }
        if (LooksLikeVisualQuestionForLive(transcript))
        {
            return route with
            {
                Category = LiveSemanticCategory.Chat,
                ControlAction = LiveControlAction.None,
                TaskUpdateMode = LiveTaskUpdateMode.None,
                ResponseAuthority = LiveResponseAuthority.Live,
                RefersToActiveTask = false,
                CanonicalTask = "",
                Reason = "guarded-read-only-live-vision",
            };
        }
        if (LooksLikeSpeechOnlyRequest(transcript))
        {
            return route with
            {
                Category = LiveSemanticCategory.Chat,
                ControlAction = LiveControlAction.None,
                TaskUpdateMode = LiveTaskUpdateMode.None,
                ResponseAuthority = LiveResponseAuthority.Live,
                RefersToActiveTask = false,
                CanonicalTask = "",
                Reason = "guarded-speech-only-request",
            };
        }
        if (route.ControlAction != LiveControlAction.AcceptCompletion) return route;
        var normalized = transcript.Trim().Trim('。', '！', '？', '!', '?', ',', '，', ' ');
        var explicitAcceptance = normalized.Length <= 18 && ContainsAny(
            normalized,
            "通过", "成功了", "完成了", "可以了", "没问题", "正确", "没错", "这样就行", "这样可以");
        if (activeTask is not null && route.RefersToActiveTask && explicitAcceptance) return route;

        if (activeTask is not null && !ContainsAny(normalized, "朗读", "读一下", "念一下") &&
            ContainsAny(normalized, "继续", "接着", "然后", "再"))
        {
            return route with
            {
                Category = LiveSemanticCategory.Control,
                ControlAction = LiveControlAction.Correction,
                TaskUpdateMode = LiveTaskUpdateMode.Append,
                ResponseAuthority = LiveResponseAuthority.Reasoner,
                RefersToActiveTask = true,
                Reason = "guarded-nonacceptance-continuation",
            };
        }

        return route with
        {
            Category = LiveSemanticCategory.Chat,
            ControlAction = LiveControlAction.None,
            TaskUpdateMode = LiveTaskUpdateMode.None,
            ResponseAuthority = LiveResponseAuthority.Live,
            RefersToActiveTask = false,
            CanonicalTask = "",
            Reason = "guarded-false-completion",
        };
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private bool IsCurrentProviderTurn(long providerTurnSequence) =>
        providerTurnSequence > 0 &&
        providerTurnSequence == Volatile.Read(ref providerInputTurnSequence);

    private long ResolveIncomingToolTurn()
    {
        var current = Volatile.Read(ref providerInputTurnSequence);
        return turnClock.IsRunning || Volatile.Read(ref providerTurnActive) != 0
            ? Math.Max(1, current)
            : current + 1;
    }

    private static bool LooksLikeSpeechOnlyRequest(string transcript) => ContainsAny(
        transcript,
        "朗读", "读一下", "读出", "念一下", "念出", "照着说", "重复这句话", "复述这句话");

    private bool LooksLikeVisualQuestionForLive(string transcript)
    {
        var normalized = transcript.Trim();
        if (!ContainsAny(normalized,
            "当前屏幕", "当前画面", "屏幕上", "画面里", "画面中", "记事本里",
            "题目", "这道题", "选项", "正确答案", "你看到", "看一下", "看看"))
            return false;
        if (TryGetDirectTaskKey(normalized, out _)) return false;
        if (ContainsAny(normalized,
            "点击", "点开", "打开", "关闭", "输入", "填写", "滚动", "保存", "下载",
            "移动", "复制", "粘贴", "删除", "重命名", "更名", "写入", "添加", "插入",
            "另起一行", "换行", "在末尾", "写到末尾"))
            return false;
        return ContainsAny(normalized,
            "答案", "选项", "是什么", "是多少", "多少", "为什么", "原因", "解释", "说明",
            "读出", "告诉", "回答", "总结", "内容", "标题", "状态", "编号", "看到");
    }

    private static bool LooksLikeScreenDerivedMutation(string transcript)
    {
        var normalized = transcript.Trim();
        var requestsMutation = ContainsAny(normalized,
            "写入", "写到", "添加", "插入", "另起一行", "换行", "在末尾", "末尾写", "末尾加");
        var derivesContentFromScreen = ContainsAny(normalized,
            "当前屏幕", "当前画面", "当前题目", "当前的题目", "这道题", "题目", "选项",
            "正确答案", "答案", "计算方式", "计算式", "计算过程", "计算结果");
        var namesLocalDestination = ContainsAny(normalized,
            "记事本", "文档", "文本", "正文", "末尾", "另起一行", "换行");
        return requestsMutation && derivesContentFromScreen && namesLocalDestination;
    }

    private async Task PublishSemanticTaskResultAsync(
        long taskSequence,
        long providerTurnSequence,
        DelegatedTaskExecutionResult result,
        bool statusOnly,
        CancellationToken cancellationToken)
    {
        if (taskSequence != Volatile.Read(ref latestSemanticTaskSequence) ||
            !IsCurrentProviderTurn(providerTurnSequence) || stopped) return;
        var value = NormalizeDelegatedTaskResult(result.Value, result.Ok);
        RecordTiming("semantic_task_completed", details: new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
        });
        Interlocked.Exchange(ref semanticResultAwaitingAudioSequence, taskSequence);
        Interlocked.Exchange(ref semanticResultProviderTurnSequence, providerTurnSequence);
        Interlocked.Exchange(ref semanticResultAudioObserved, 0);
        Interlocked.Exchange(ref semanticResultAudioRetryIssued, 0);
        var interruptsActiveTurn = Volatile.Read(ref providerTurnActive) != 0;
        if (interruptsActiveTurn)
            Interlocked.Exchange(ref semanticResultInterruptionSequence, taskSequence);
        else
            Interlocked.Exchange(ref suppressModelOutputForTask, 0);

        try
        {
            await SendRealtimeTextAsync(
                statusOnly
                    ? CreateTaskStatusInstruction(value)
                    : CreateDirectWindowsFallbackInstruction(result.Ok, value),
                cancellationToken).ConfigureAwait(false);
            RecordTiming("semantic_result_sent_immediately", details: new Dictionary<string, object?>
            {
                ["interruptedActiveTurn"] = interruptsActiveTurn,
            });
            _ = RetrySilentSemanticResultAudioAsync(
                taskSequence,
                result.Ok,
                value,
                statusOnly,
                Volatile.Read(ref semanticResultProviderTurnSequence));
        }
        catch
        {
            if (interruptsActiveTurn)
            {
                Interlocked.CompareExchange(ref semanticResultInterruptionSequence, 0, taskSequence);
                Interlocked.Exchange(ref suppressModelOutputForTask, 0);
            }
            throw;
        }
    }

    private async Task PublishLocalTaskResultAsync(
        long taskSequence,
        DelegatedTaskExecutionResult result,
        bool directBatch = false,
        long? providerTurnSequence = null)
    {
        var ownsResult = directBatch
            ? taskSequence == Volatile.Read(ref latestDirectBatchSequence)
            : taskSequence == Volatile.Read(ref latestSemanticTaskSequence);
        if (!ownsResult ||
            providerTurnSequence is { } expectedTurn && !IsCurrentProviderTurn(expectedTurn) ||
            stopped) return;
        var value = NormalizeLocalAnnouncement(result.Value, result.Ok);
        RecordTiming("semantic_task_completed", details: new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["delivery"] = "local-tts",
        });
        // A newer verified local result supersedes any pending Gemini-generated
        // completion retry. Ordinary Live chat remains on the Gemini stream.
        Interlocked.Exchange(ref semanticResultAwaitingAudioSequence, 0);
        Interlocked.Exchange(ref semanticResultAudioObserved, 1);
        Interlocked.Exchange(ref semanticResultAudioRetryIssued, 1);
        var interruptsActiveTurn = Volatile.Read(ref providerTurnActive) != 0;
        if (interruptsActiveTurn)
            Interlocked.Exchange(ref semanticResultInterruptionSequence, taskSequence);

        await PublishAsync(
            LiveEventTypes.LocalResult,
            transcript: value,
            resultOk: result.Ok).ConfigureAwait(false);
        RecordTiming("local_result_published", details: new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["interruptedActiveTurn"] = interruptsActiveTurn,
        });
    }

    private async Task RetrySilentSemanticResultAudioAsync(
        long taskSequence,
        bool resultOk,
        string value,
        bool statusOnly,
        long providerTurnSequence)
    {
        try
        {
            await Task.Delay(SemanticResultAudioWait, lifetime.Token).ConfigureAwait(false);
            if (stopped ||
                taskSequence != Volatile.Read(ref latestSemanticTaskSequence) ||
                taskSequence != Volatile.Read(ref semanticResultAwaitingAudioSequence) ||
                Volatile.Read(ref semanticResultAudioObserved) != 0 ||
                Volatile.Read(ref providerInputTurnSequence) != providerTurnSequence ||
                Volatile.Read(ref providerTurnActive) != 0 ||
                inputTranscript.HasText ||
                Interlocked.CompareExchange(ref semanticResultAudioRetryIssued, 1, 0) != 0)
                return;

            var instruction = statusOnly
                ? CreateTaskStatusInstruction(value)
                : CreateDirectWindowsFallbackInstruction(resultOk, value);
            await SendRealtimeTextAsync(
                $"VELIA_CORE_AUDIO_RETRY {instruction} 请立即输出语音。",
                lifetime.Token).ConfigureAwait(false);
            RecordTiming("semantic_result_audio_retry_sent");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            RecordTiming("semantic_result_audio_retry_failed");
        }
    }

    private static string ResolveMissionRelationship(LiveSemanticRoute route)
    {
        if (route.Category == LiveSemanticCategory.Control)
        {
            return route.ControlAction switch
            {
                LiveControlAction.Stop => "stop",
                LiveControlAction.Progress => "progress",
                LiveControlAction.Correction when route.TaskUpdateMode == LiveTaskUpdateMode.Append => "append",
                LiveControlAction.Correction => "replace",
                LiveControlAction.AcceptCompletion => "progress",
                _ => "auto",
            };
        }
        if (route.RefersToActiveTask)
        {
            return route.TaskUpdateMode == LiveTaskUpdateMode.Append ? "append" : "replace";
        }
        return "auto";
    }

    private bool LooksLikeExplicitTaskFragment(string transcript)
    {
        var normalized = transcript.Trim();
        if (normalized.Length == 0 ||
            normalized.StartsWith("怎么", StringComparison.Ordinal) ||
            normalized.StartsWith("如何", StringComparison.Ordinal) ||
            normalized.StartsWith("为什么", StringComparison.Ordinal) ||
            normalized.StartsWith("能不能", StringComparison.Ordinal) ||
            normalized.StartsWith("是否", StringComparison.Ordinal))
        {
            return false;
        }
        if (DesktopContextIntentDetector.IsExplicitRequest(normalized)) return true;
        if (TryGetDirectTaskKey(normalized, out _)) return true;
        var action = new[]
        {
            "打开", "进入", "访问", "搜索", "切换到", "点击", "点开", "输入", "填写", "滚动", "保存", "下载", "关闭", "重命名",
            "滑动", "滑到", "滚到", "向下滑", "向上滑", "往下滑", "往上滑",
            "更名", "打开浏览器", "搜索网页", "看一下屏幕", "看看屏幕", "读取屏幕",
            "分析屏幕", "识别屏幕", "整理", "创建", "新建", "写入", "写到", "添加", "插入", "另起一行",
            "换行", "在末尾", "末尾写", "末尾加",
        };
        return action.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetDirectTaskKey(string transcript, out string key)
    {
        if (taskExecutor.TryGetDeterministicTaskKey(
                transcript,
                GetEffectiveWebPlatform(),
                out var localKey))
        {
            key = localKey;
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static string GetSafeDirectRouteName(string key)
    {
        if (key.StartsWith("browser-article-to-word:", StringComparison.Ordinal)) return "browser-article-word";
        if (key.StartsWith("browser-action:", StringComparison.Ordinal)) return "browser-element";
        if (key.StartsWith("browser-tab:", StringComparison.Ordinal)) return "browser-tab";
        if (key.StartsWith("browser:", StringComparison.Ordinal)) return "browser-navigation";
        if (key.StartsWith("windows:", StringComparison.Ordinal)) return "windows-control";
        if (key.StartsWith("folder-create:", StringComparison.Ordinal)) return "folder-create";
        if (key.StartsWith("document-create:", StringComparison.Ordinal)) return "document-create";
        if (key.StartsWith("document-edit:", StringComparison.Ordinal)) return "document-edit";
        if (key.StartsWith("desktop-file-move:", StringComparison.Ordinal)) return "desktop-file-move";
        if (key.StartsWith("desktop-file:", StringComparison.Ordinal)) return "desktop-file";
        if (key.StartsWith("desktop:", StringComparison.Ordinal)) return "desktop-action";
        return "unknown";
    }

    private static bool ContainsReservedLiveMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("VELIA_CORE_RESULT", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("VELIA_CORE_STATUS", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("VELIA_CORE_AUDIO_RETRY", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("VELIA_LIVE_RETRY", StringComparison.OrdinalIgnoreCase));

    private bool RecordLocalTranscriptCandidate(
        string utteranceId,
        string transcript,
        DateTimeOffset capturedAtUtc,
        out long assignedSequence)
    {
        var now = DateTimeOffset.UtcNow;
        lock (localTranscriptGate)
        {
            PruneLocalTranscriptCandidates(now);
            if (!seenLocalUtteranceIds.Add(utteranceId))
            {
                assignedSequence = 0;
                return false;
            }
            if (seenLocalUtteranceIds.Count > 512)
            {
                seenLocalUtteranceIds.Clear();
                seenLocalUtteranceIds.Add(utteranceId);
            }

            var currentSequence = Volatile.Read(ref providerInputTurnSequence);
            var providerActive = Volatile.Read(ref providerTurnActive) != 0;
            var capturedBeforeCurrentTurn = providerActive && currentSequence > 1 &&
                providerTurnCompletedAtUtc != default &&
                capturedAtUtc <= providerTurnCompletedAtUtc + TimeSpan.FromSeconds(2);
            var belongsToCompletedTurn = !providerActive && currentSequence > 0 &&
                providerTurnCompletedAtUtc != default &&
                capturedAtUtc <= providerTurnCompletedAtUtc + TimeSpan.FromSeconds(2);
            assignedSequence = capturedBeforeCurrentTurn
                ? currentSequence - 1
                : providerActive || belongsToCompletedTurn
                    ? currentSequence
                    : currentSequence + 1;
            localTranscriptCandidates.Add(new LocalTranscriptCandidate(
                utteranceId,
                transcript,
                capturedAtUtc,
                now,
                assignedSequence));
            while (localTranscriptCandidates.Count > 16) localTranscriptCandidates.RemoveAt(0);
            return true;
        }
    }

    private void MarkProviderTurnHandledLocally(long turnSequence)
    {
        if (turnSequence <= 0) return;
        lock (localTranscriptGate)
        {
            locallyHandledProviderTurns.Add(turnSequence);
            var minimum = Math.Max(1, Volatile.Read(ref providerInputTurnSequence) - 8);
            locallyHandledProviderTurns.RemoveWhere(sequence => sequence < minimum);
        }
        RecordTiming("local_asr_turn_owned", details: new Dictionary<string, object?>
        {
            ["providerTurnSequence"] = turnSequence,
        });
    }

    private bool IsProviderTurnHandledLocally(long turnSequence)
    {
        lock (localTranscriptGate) return locallyHandledProviderTurns.Contains(turnSequence);
    }

    private void SignalLocalTranscriptArbitration(long turnSequence)
    {
        if (turnSequence <= 0) return;
        TaskCompletionSource signal;
        lock (localTranscriptGate)
        {
            if (!localTranscriptArbitrationSignals.TryGetValue(turnSequence, out signal!))
            {
                signal = NewSignal();
                localTranscriptArbitrationSignals[turnSequence] = signal;
            }
        }
        signal.TrySetResult();
    }

    private async Task WaitForLocalTranscriptArbitrationAsync(
        long turnSequence,
        CancellationToken cancellationToken)
    {
        if (turnSequence <= 0)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(400),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        Task signal;
        lock (localTranscriptGate)
        {
            if (!localTranscriptArbitrationSignals.TryGetValue(turnSequence, out var source))
            {
                source = NewSignal();
                localTranscriptArbitrationSignals[turnSequence] = source;
            }
            signal = source.Task;
        }
        var timeout = Task.Delay(ProviderFirstAudioArbitrationDelay, cancellationToken);
        await Task.WhenAny(signal, timeout).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PruneLocalTranscriptArbitrationSignals(long completedTurnSequence)
    {
        lock (localTranscriptGate)
        {
            var oldestKept = Math.Max(1, completedTurnSequence - 1);
            foreach (var key in localTranscriptArbitrationSignals.Keys
                .Where(key => key < oldestKept)
                .ToArray())
                localTranscriptArbitrationSignals.Remove(key);
        }
    }

    private string? GetEffectiveWebPlatform()
    {
        // Mission context describes the previous successful surface. The
        // foreground window is the authority for a new unscoped command: once
        // the user moves to Notepad, "点击编辑" must use desktop UIA rather than
        // a stale browser DOM binding.
        if (taskExecutor.GetActiveInteractionSurface() == ActiveInteractionSurfaceKind.Desktop)
            return null;

        lock (directFallbackGate)
        {
            foreach (var execution in directCommandExecutions.Values
                .Where(static item => !item.Task.IsCompleted)
                .OrderByDescending(static item => item.QueuedAtTimestamp))
            {
                if (TryGetPlatformFromDirectKey(execution.Key, out var platform)) return platform;
            }
        }

        var current = taskExecutor.GetCurrentWebPlatform();
        if (!string.IsNullOrWhiteSpace(current))
        {
            Volatile.Write(ref lastKnownWebPlatform, current);
            return current;
        }

        lock (directFallbackGate)
        {
            foreach (var execution in directCommandExecutions.Values
                .OrderByDescending(static item => item.QueuedAtTimestamp))
            {
                if (TryGetPlatformFromDirectKey(execution.Key, out var platform)) return platform;
            }
        }
        return Volatile.Read(ref lastKnownWebPlatform);
    }

    private static bool TryGetPlatformFromDirectKey(string key, out string platform)
    {
        const string browserPrefix = "browser:";
        const string actionPrefix = "browser-action:";
        string value;
        if (key.StartsWith(browserPrefix, StringComparison.Ordinal))
        {
            value = key[browserPrefix.Length..].Split(':', 2)[0];
        }
        else if (key.StartsWith(actionPrefix, StringComparison.Ordinal))
        {
            value = key[actionPrefix.Length..].Split(':', 2)[0];
        }
        else
        {
            platform = string.Empty;
            return false;
        }

        platform = value.ToLowerInvariant() switch
        {
            var host when host.Contains("douyin") => "抖音",
            var host when host.Contains("bilibili") => "哔哩哔哩",
            var host when host.Contains("xiaohongshu") => "小红书",
            var host when host.Contains("zhihu") => "知乎",
            var host when host.Contains("weibo") => "微博",
            var host when host.Contains("youtube") => "YouTube",
            var host when host.Contains("baidu") => "百度",
            var host when host.Contains("bing") => "必应",
            var host when host.Contains("google") => "谷歌",
            _ => value,
        };
        return !string.IsNullOrWhiteSpace(platform);
    }

    private async Task<string> FuseSemanticTranscriptAsync(
        string providerTranscript,
        long turnSequence,
        CancellationToken cancellationToken)
    {
        if (TryGetLocalTranscriptCandidate(turnSequence, out var localTranscript))
            return SelectSemanticTranscript(providerTranscript, localTranscript);
        if (!ShouldAwaitLocalTranscript(providerTranscript)) return providerTranscript;

        var deadline = DateTimeOffset.UtcNow + LocalTranscriptFusionWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            if (TryGetLocalTranscriptCandidate(turnSequence, out localTranscript))
                return SelectSemanticTranscript(providerTranscript, localTranscript);
        }
        return providerTranscript;
    }

    private bool TryGetLocalTranscriptCandidate(long turnSequence, out string transcript)
    {
        lock (localTranscriptGate)
        {
            PruneLocalTranscriptCandidates(DateTimeOffset.UtcNow);
            var candidate = localTranscriptCandidates
                .Where(item => item.TurnSequence == turnSequence)
                .OrderByDescending(static item => item.CapturedAtUtc)
                .FirstOrDefault();
            if (candidate is not null)
            {
                localTranscriptCandidates.Remove(candidate);
                transcript = candidate.Transcript;
                return true;
            }
        }
        transcript = string.Empty;
        return false;
    }

    private void PruneLocalTranscriptCandidates(DateTimeOffset now)
    {
        localTranscriptCandidates.RemoveAll(candidate =>
            now < candidate.CapturedAtUtc - LocalTranscriptMaximumFutureSkew ||
            now - candidate.CapturedAtUtc > LocalTranscriptMaximumAge);
    }

    private string SelectSemanticTranscript(string providerTranscript, string localTranscript)
    {
        string selected;
        string source;
        var providerIsDeterministic = TryGetDirectTaskKey(providerTranscript, out var providerKey);
        var localIsDeterministic = TryGetDirectTaskKey(localTranscript, out var localKey);
        if (localIsDeterministic &&
            (!providerIsDeterministic || !StringComparer.Ordinal.Equals(localKey, providerKey)))
        {
            selected = localTranscript;
            source = "local-asr-deterministic";
        }
        else if (providerIsDeterministic && !localIsDeterministic)
        {
            selected = providerTranscript;
            source = "gemini-deterministic";
        }
        else if (LooksLikeExplicitTaskFragment(localTranscript) ||
            LooksLikeForeignLanguageDrift(providerTranscript, localTranscript))
        {
            selected = localTranscript;
            source = "local-asr";
        }
        else
        {
            selected = providerTranscript;
            source = "gemini";
        }
        RecordTiming("semantic_transcript_fused", details: new Dictionary<string, object?>
        {
            ["source"] = source,
            ["providerCharacters"] = providerTranscript.Length,
            ["localCharacters"] = localTranscript.Length,
        });
        return selected;
    }

    private static bool LooksLikeForeignLanguageDrift(
        string providerTranscript,
        string localTranscript)
    {
        var localHanCharacters = localTranscript.Count(IsHanCharacter);
        if (localHanCharacters == 0 || providerTranscript.Any(IsHanCharacter)) return false;
        var providerHasForeignLetters = providerTranscript.Count(char.IsAsciiLetter) >= 2 ||
            providerTranscript.Any(static character => character is >= '\uac00' and <= '\ud7af');
        return providerHasForeignLetters &&
            !string.Equals(providerTranscript.Trim(), localTranscript.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHanCharacter(char character) =>
        character is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff';

    private bool ShouldAwaitLocalTranscript(string transcript)
    {
        if (LooksLikeSpeechOnlyRequest(transcript) || LooksLikeVisualQuestionForLive(transcript)) return false;
        return ContainsAny(
            transcript,
            "打开", "关闭", "最小化", "恢复", "最大化", "切换", "进入", "访问",
            "点击", "点开", "选择", "输入", "填写", "保存", "搜索", "搜一下", "返回",
            "滚动", "复制", "粘贴", "重命名", "创建", "新建", "写入", "发送",
            "浏览器", "网页", "页面", "主页", "相册", "作品", "创作者中心",
            "抖音", "微博", "哔哩哔哩", "B站", "小红书", "知乎", "YouTube",
            "微信", "QQ", "钉钉", "飞书", "WPS", "Word", "Excel", "记事本");
    }

    private void StartDelegatedTask(GeminiLiveToolCall call)
    {
        if (stopped)
        {
            pendingToolCalls.TryRemove(call.Id, out _);
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(LiveDelegatedToolTimeout);
        if (!delegatedTaskCancellations.TryAdd(call.Id, cancellation))
        {
            cancellation.Dispose();
            pendingToolCalls.TryRemove(call.Id, out _);
            return;
        }

        var task = Task.Run(
            () => RunDelegatedTaskAsync(call, cancellation),
            CancellationToken.None);
        delegatedTasks[call.Id] = task;
        _ = task.ContinueWith(
            completedTask => delegatedTasks.TryRemove(call.Id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunDelegatedTaskAsync(
        GeminiLiveToolCall call,
        CancellationTokenSource cancellation)
    {
        var providerTurnSequence = pendingToolCalls.TryGetValue(call.Id, out var pendingAtStart)
            ? pendingAtStart.ProviderTurnSequence
            : Volatile.Read(ref providerInputTurnSequence);
        try
        {
            DelegatedTaskExecutionResult result;
            try
            {
                var transcript = await inputTranscript.TakeWhenStableAsync(
                    TranscriptQuietPeriod,
                    TranscriptMaximumWait,
                    cancellation.Token).ConfigureAwait(false);
                if (transcript is null)
                {
                    result = new DelegatedTaskExecutionResult(
                        false,
                        "本轮没有稳定识别到完整用户指令，未执行任何操作。");
                }
                else
                {
                    var authorizedTask = SelectAuthorizedTask(transcript, call.Input);
                    if (TryGetDirectFallbackExecution(transcript, call.Input, out var fallbackExecution))
                    {
                        RecordTiming("delegated_task_reused_local_result");
                        result = await fallbackExecution.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    else if (TryGetDirectTaskKey(transcript, out var directTaskKey))
                    {
                        var directExecution = GetOrStartDeterministicExecution(
                            authorizedTask,
                            directTaskKey,
                            allowCompletedReuse: true,
                            localAsrOrigin: false,
                            out _);
                        result = await directExecution.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await taskExecutor.ExecuteDelegatedTaskAsync(
                            authorizedTask,
                            cancellation.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                result = new DelegatedTaskExecutionResult(false, "任务执行已取消或超时。");
            }
            catch (Exception)
            {
                result = new DelegatedTaskExecutionResult(false, "任务执行失败，请稍后重试。");
            }

            if (Volatile.Read(ref providerInputTurnSequence) > providerTurnSequence)
            {
                CancelPendingTool(call.Id);
                RecordTiming("delegated_result_dropped_stale_turn");
                return;
            }
            var value = NormalizeDelegatedTaskResult(result.Value, result.Ok);
            await SubmitToolResultAsync(
                call.Id,
                call.Name,
                result.Ok,
                value,
                lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) when (stopped || cancellation.IsCancellationRequested)
        {
            // The session or provider cancelled the call; no response is required.
        }
        catch (Exception)
        {
            await FailAsync(
                "live_tool_response_failed",
                "Live 任务结果回传失败，请重新开始 Live。",
                true).ConfigureAwait(false);
        }
        finally
        {
            if (delegatedTaskCancellations.TryRemove(call.Id, out var stored)) stored.Dispose();
        }
    }

    private static string NormalizeDelegatedTaskResult(string value, bool ok)
    {
        var fallback = ok ? "任务已完成。" : "任务未能完成，请稍后重试。";
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!ok)
            normalized += " 不得要求用户再次授权、回复已就绪或确认前台窗口；只说明本次未完成及现有原因。";
        return normalized.Length <= GeminiLiveService.MaximumToolResultCharacters
            ? normalized
            : normalized[..GeminiLiveService.MaximumToolResultCharacters];
    }

    private static string NormalizeLocalAnnouncement(string value, bool ok)
    {
        var fallback = ok ? "任务已完成。" : "任务未能完成，请稍后重试。";
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        const string internalFailurePolicy =
            " 不得要求用户再次授权、回复已就绪或确认前台窗口；只说明本次未完成及现有原因。";
        if (normalized.EndsWith(internalFailurePolicy, StringComparison.Ordinal))
            normalized = normalized[..^internalFailurePolicy.Length].TrimEnd();
        return normalized.Length <= 4_000 ? normalized : normalized[..4_000];
    }

    private static string SelectAuthorizedTask(string transcript, string suggestedTask)
    {
        var actual = transcript.Trim();
        var suggested = suggestedTask.Trim();
        if (WindowsControlIntentDetector.TryParseExplicitCommand(actual, out var actualCommand) &&
            WindowsControlIntentDetector.TryParseExplicitCommand(suggested, out var suggestedCommand) &&
            actualCommand == suggestedCommand)
        {
            return suggested;
        }
        return actual;
    }

    private void StartDirectWindowsFallbackWatcher()
    {
        lock (directFallbackGate)
        {
            if (stopped || directFallbackWatcher is { IsCompleted: false }) return;
            directFallbackWatcher = Task.Run(RunDirectWindowsFallbackAsync, CancellationToken.None);
        }
    }

    private async Task RunDirectWindowsFallbackAsync()
    {
        try
        {
            var transcript = await directFallbackTranscript.TakeWhenStableAsync(
                TranscriptQuietPeriod,
                TranscriptMaximumWait,
                lifetime.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcript) ||
                !TryGetDirectTaskKey(transcript, out var key)) return;
            var providerTurnSequence = Volatile.Read(ref providerInputTurnSequence);
            RecordTiming("deterministic_command_detected");
            var taskSequence = Interlocked.Increment(ref semanticTaskSequence);
            Interlocked.Exchange(ref latestSemanticTaskSequence, taskSequence);

            await Task.Delay(DirectWindowsFallbackGracePeriod, lifetime.Token).ConfigureAwait(false);
            if (Volatile.Read(ref delegatedToolCallsThisTurn) != 0) return;

            var execution = GetOrStartDeterministicExecution(
                transcript,
                key,
                allowCompletedReuse: true,
                localAsrOrigin: false,
                out _);
            var result = await execution.Task.ConfigureAwait(false);
            RecordTiming("deterministic_command_completed", details: new Dictionary<string, object?>
            {
                ["ok"] = result.Ok,
            });
            await Task.Delay(DirectWindowsFallbackGracePeriod, lifetime.Token).ConfigureAwait(false);
            if (Volatile.Read(ref delegatedToolCallsThisTurn) != 0) return;

            if (IsCurrentProviderTurn(providerTurnSequence) && execution.TryMarkResultInjected())
                await PublishLocalTaskResultAsync(
                    taskSequence,
                    result,
                    providerTurnSequence: providerTurnSequence).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!stopped)
        {
            // The normal Live response remains available if the deterministic fallback cannot run.
        }
        finally
        {
            lock (directFallbackGate)
            {
                directFallbackWatcher = null;
            }
        }
    }

    private async Task<DelegatedTaskExecutionResult> ExecuteQueuedDirectCommandAsync(
        DirectCommandExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            await directCommandLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            execution.MarkCompleted();
            throw;
        }
        execution.MarkStarted(DirectCommandBurstTimeout);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            execution.BurstCancellationToken);
        try
        {
            execution.QueueDelayMilliseconds =
                (long)Stopwatch.GetElapsedTime(execution.QueuedAtTimestamp).TotalMilliseconds;
            RecordTiming("direct_command_lane_entered", details: new Dictionary<string, object?>
            {
                ["route"] = GetSafeDirectRouteName(execution.Key),
                ["queueMs"] = execution.QueueDelayMilliseconds,
                ["burstDeadline"] = execution.HasFollower,
            });
            return await ExecuteDirectWindowsFallbackAsync(
                execution.Transcript,
                execution.WebPlatform,
                executionCancellation.Token,
                execution.BurstCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            execution.MarkCompleted();
            directCommandLane.Release();
        }
    }

    private async Task<DelegatedTaskExecutionResult> ExecuteDirectWindowsFallbackAsync(
        string transcript,
        string? queuedWebPlatform,
        CancellationToken cancellationToken,
        CancellationToken burstCancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DirectWindowsFallbackTimeout);
        try
        {
            var contextualPlatform = queuedWebPlatform ?? taskExecutor.GetCurrentWebPlatform();
            if (DeterministicLocalTaskIntentDetector.TryParse(
                    transcript,
                    contextualPlatform,
                    out var intent) && intent.IsBrowser)
                return await taskExecutor.ExecuteRoutedTaskAsync(
                    transcript,
                    "auto",
                    timeout.Token).ConfigureAwait(false);
            return await taskExecutor.ExecuteDeterministicWindowsTaskAsync(
                transcript,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new DelegatedTaskExecutionResult(
                false,
                burstCancellationToken.IsCancellationRequested
                    ? "上一项操作响应过慢，已停止并继续处理后续指令。"
                    : "任务执行已取消或超时。");
        }
        catch (Exception)
        {
            return new DelegatedTaskExecutionResult(false, "任务执行失败，请稍后重试。");
        }
    }

    private DirectCommandExecution GetOrStartDeterministicExecution(
        string transcript,
        string key,
        bool allowCompletedReuse,
        bool localAsrOrigin,
        out bool duplicate)
    {
        lock (directFallbackGate)
        {
            if (directCommandExecutions.TryGetValue(key, out var existing) &&
                ((!existing.Task.IsCompleted && (!localAsrOrigin || !existing.LocalAsrOrigin)) ||
                 (allowCompletedReuse && existing.CompletedRecently(TimeSpan.FromSeconds(2)))))
            {
                duplicate = true;
                return existing;
            }

            foreach (var pending in directCommandExecutions.Values
                .Where(static item => !item.Task.IsCompleted))
                pending.MarkFollowerQueued(DirectCommandBurstTimeout);

            var webPlatform = TryGetPlatformFromDirectKey(key, out var platform) ? platform : null;
            if (!string.IsNullOrWhiteSpace(webPlatform))
                Volatile.Write(ref lastKnownWebPlatform, webPlatform);
            var execution = new DirectCommandExecution(
                key,
                transcript.Trim(),
                Stopwatch.GetTimestamp(),
                webPlatform,
                localAsrOrigin);
            execution.Task = ExecuteQueuedDirectCommandAsync(execution, lifetime.Token);
            directCommandExecutions[key] = execution;
            duplicate = false;
            return execution;
        }
    }

    private void ResetDeterministicTurnState()
    {
        Interlocked.Exchange(ref localAsrHandledThisTurn, 0);
        Interlocked.Exchange(ref localAsrAwaitingProviderTranscript, 0);
        lock (directFallbackGate)
        {
            foreach (var key in directCommandExecutions
                .Where(static pair => pair.Value.Task.IsCompleted)
                .Select(static pair => pair.Key)
                .ToArray())
                directCommandExecutions.Remove(key);
        }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private bool TryGetDirectFallbackExecution(
        string transcript,
        string suggestedTask,
        out Task<DelegatedTaskExecutionResult> execution)
    {
        lock (directFallbackGate)
        {
            if (TryFindDirectExecution(transcript, out var matched) ||
                TryFindDirectExecution(suggestedTask, out matched))
            {
                execution = matched.Task;
                return true;
            }
        }
        execution = null!;
        return false;
    }

    private bool TryFindDirectExecution(string transcript, out DirectCommandExecution execution)
    {
        if (TryGetDirectTaskKey(transcript, out var key) &&
            directCommandExecutions.TryGetValue(key, out var matched) &&
            (!matched.Task.IsCompleted || matched.CompletedRecently(TimeSpan.FromSeconds(2))))
        {
            execution = matched;
            return true;
        }
        execution = null!;
        return false;
    }

    private static string CreateDirectWindowsFallbackInstruction(bool ok, string value) =>
        $"VELIA_CORE_RESULT 执行状态：{(ok ? "成功" : "失败")}。本机验证结果：{value} " +
        "只用一句自然简体中文播报该结果；不要再次调用工具，不要询问确认，不要提及内部标记。";

    private static string CreateTaskStatusInstruction(string value) =>
        $"VELIA_CORE_STATUS 后台任务当前状态：{value} " +
        "只如实播报当前状态；没有明确的本机验证完成证据时，严禁声称已经修改电脑。";

    private async Task SendRealtimeTextAsync(string text, CancellationToken cancellationToken)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        sendCancellation.CancelAfter(SendTimeout);
        await WaitForConnectionReadyAsync(sendCancellation.Token).ConfigureAwait(false);
        await reconnectGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
        try
        {
            var current = socket ?? throw new LiveProviderException(
                "live_connection_lost", "Gemini Live 连接已中断。", true);
            await current.SendTextAsync(
                GeminiLiveProtocol.CreateRealtimeText(text),
                sendCancellation.Token).ConfigureAwait(false);
        }
        finally { reconnectGate.Release(); }
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task SendRejectedToolCallAsync(
        string callId,
        string toolName,
        string message,
        CancellationToken cancellationToken)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        sendCancellation.CancelAfter(SendTimeout);
        await WaitForConnectionReadyAsync(sendCancellation.Token).ConfigureAwait(false);
        await reconnectGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
        try
        {
            if (socket is { } current)
            {
                await current.SendTextAsync(
                    GeminiLiveProtocol.CreateToolResponse(callId, toolName, false, message),
                    sendCancellation.Token).ConfigureAwait(false);
            }
        }
        finally { reconnectGate.Release(); }
    }

    private async Task WaitForConnectionReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var signal = Volatile.Read(ref connectionReady);
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(signal, Volatile.Read(ref connectionReady))) return;
        }
    }

    private void ClearInputTranscript()
    {
        inputTranscript.Clear();
        directFallbackTranscript.Clear();
        Interlocked.Exchange(ref delegatedToolCallsThisTurn, 0);
        Interlocked.Exchange(ref suppressModelOutputForTask, 0);
        turnClock.Reset();
        firstOutputAudioObserved = false;
    }

    private void ResetFreshProviderSessionState()
    {
        Interlocked.Exchange(ref providerTurnActive, 0);
        Interlocked.Exchange(ref recoveredTextTurnActive, 0);
        Interlocked.Exchange(ref providerInputTurnSequence, 0);
        Interlocked.Exchange(ref latestClassifiedTurnSequence, 0);
        Interlocked.Exchange(ref latestClassifiedTurnAllowsLive, 0);
        Interlocked.Exchange(ref silentRetrySentTurnSequence, 0);
        lock (silentRetryGate) currentProviderTranscript = "";
        lock (localTranscriptGate) locallyHandledProviderTurns.Clear();
        lock (localTranscriptGate) localTranscriptArbitrationSignals.Clear();
        ResetDeterministicTurnState();
    }

    private void CancelPendingTool(string callId)
    {
        if (pendingToolCalls.TryGetValue(callId, out var pending) && pending.TryCancel())
            pendingToolCalls.TryRemove(callId, out _);
        if (delegatedTaskCancellations.TryGetValue(callId, out var cancellation)) TryCancel(cancellation);
    }

    private void CancelPendingToolsBeforeTurn(long providerTurnSequence)
    {
        foreach (var pair in pendingToolCalls)
        {
            if (pair.Value.ProviderTurnSequence >= providerTurnSequence) continue;
            CancelPendingTool(pair.Key);
        }
    }

    private void CancelAllPendingTools()
    {
        foreach (var callId in pendingToolCalls.Keys) CancelPendingTool(callId);
        foreach (var cancellation in delegatedTaskCancellations.Values) TryCancel(cancellation);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record BufferedLiveAudio(long Generation, string Base64);

    private sealed class DirectCommandExecution(
        string key,
        string transcript,
        long queuedAtTimestamp,
        string? webPlatform,
        bool localAsrOrigin) : IDisposable
    {
        private readonly CancellationTokenSource burstCancellation = new();
        private int resultInjected;
        private int hasFollower;
        private long startedAtTimestamp;
        private long completedAtTimestamp;

        public string Key { get; } = key;
        public string Transcript { get; } = transcript;
        public long QueuedAtTimestamp { get; } = queuedAtTimestamp;
        public string? WebPlatform { get; } = webPlatform;
        public bool LocalAsrOrigin { get; } = localAsrOrigin;
        public long QueueDelayMilliseconds { get; set; }
        public Task<DelegatedTaskExecutionResult> Task { get; set; } = null!;
        public bool HasFollower => Volatile.Read(ref hasFollower) != 0;
        public CancellationToken BurstCancellationToken => burstCancellation.Token;

        public bool TryMarkResultInjected() =>
            Interlocked.CompareExchange(ref resultInjected, 1, 0) == 0;

        public void MarkStarted(TimeSpan burstTimeout)
        {
            Interlocked.CompareExchange(ref startedAtTimestamp, Stopwatch.GetTimestamp(), 0);
            ApplyBurstDeadlineIfNeeded(burstTimeout);
        }

        public void MarkFollowerQueued(TimeSpan burstTimeout)
        {
            Interlocked.Exchange(ref hasFollower, 1);
            ApplyBurstDeadlineIfNeeded(burstTimeout);
        }

        public void MarkCompleted()
        {
            Interlocked.Exchange(ref completedAtTimestamp, Stopwatch.GetTimestamp());
            Dispose();
        }

        public void Dispose() => burstCancellation.Dispose();

        public bool CompletedRecently(TimeSpan maximumAge)
        {
            var timestamp = Volatile.Read(ref completedAtTimestamp);
            return timestamp != 0 && Stopwatch.GetElapsedTime(timestamp) <= maximumAge;
        }

        private void ApplyBurstDeadlineIfNeeded(TimeSpan burstTimeout)
        {
            var startedAt = Volatile.Read(ref startedAtTimestamp);
            if (startedAt == 0 || Volatile.Read(ref hasFollower) == 0) return;
            var remaining = burstTimeout - Stopwatch.GetElapsedTime(startedAt);
            try
            {
                if (remaining <= TimeSpan.Zero) burstCancellation.Cancel();
                else burstCancellation.CancelAfter(remaining);
            }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed record PendingDirectAnnouncement(
        long TaskSequence,
        DelegatedTaskExecutionResult Result);

    private sealed class DirectCommandBatch(long taskSequence, int commandCount)
    {
        private readonly object gate = new();
        private readonly List<PendingDirectAnnouncement> announcements = [];
        private int remaining = commandCount;
        private bool flushScheduled;

        public long TaskSequence { get; } = taskSequence;

        public bool Complete(PendingDirectAnnouncement? announcement)
        {
            lock (gate)
            {
                if (announcement is not null) announcements.Add(announcement);
                remaining = Math.Max(0, remaining - 1);
                if (remaining != 0 || flushScheduled) return false;
                flushScheduled = true;
                return true;
            }
        }

        public PendingDirectAnnouncement[] GetAnnouncements()
        {
            lock (gate) return [.. announcements];
        }
    }

    private void RecordTiming(
        string eventName,
        IReadOnlyDictionary<string, object?>? details = null) =>
        timing?.Record(
            Id,
            eventName,
            sessionClock.ElapsedMilliseconds,
            turnClock.IsRunning ? turnClock.ElapsedMilliseconds : null,
            details);

    private static async Task AwaitBackgroundAsync(Task? task, TimeSpan timeout)
    {
        if (task is null) return;
        try { await task.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private sealed class PendingLiveToolCall(string name, long providerTurnSequence = 0)
    {
        private int state;
        public string Name { get; } = name;
        public long ProviderTurnSequence { get; } = providerTurnSequence;
        public bool TryBeginResponse() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
        public bool TryCancel() => Interlocked.CompareExchange(ref state, 2, 0) == 0;
        public void Complete() => Interlocked.Exchange(ref state, 2);
    }

    private sealed record LocalTranscriptCandidate(
        string UtteranceId,
        string Transcript,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset ReceivedAtUtc,
        long TurnSequence);

}
