using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velia.Core.Providers.Chat;

/// <summary>
/// Streaming Chat Completions adapter for providers that emit data-only SSE,
/// including DeepSeek's current OpenAI-compatible endpoint.
/// </summary>
internal sealed class OpenAiCompatibleChatProvider : IStreamingChatProvider
{
    private const int MaximumMessages = 256;
    private const int MaximumMessageCharacters = 1_048_576;
    private const int MaximumEventCharacters = 1_048_576;
    private const int MaximumErrorCharacters = 65_536;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient;
    private readonly Func<string?> apiKeyProvider;
    private readonly ChatProviderOptions options;
    private readonly Uri endpoint;

    public OpenAiCompatibleChatProvider(
        HttpClient httpClient,
        Func<string?> apiKeyProvider,
        ChatProviderOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        this.options = ValidateOptions(options);
        endpoint = new Uri(EnsureTrailingSlash(this.options.BaseUri), "chat/completions");
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var apiKey = apiKeyProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 16_384)
        {
            throw new ChatProviderException("The chat provider API key is not configured.", false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new ByteArrayContent(BuildPayload(request));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderException("The chat provider request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new ChatProviderException("The chat provider could not be reached.", true, null, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadLimitedErrorAsync(response, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
                throw new ChatProviderException(
                    $"The chat provider returned HTTP {status}: {body}",
                    retryable,
                    status);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 16_384, leaveOpen: false);
            var data = new StringBuilder();
            string? finishReason = null;
            ChatUsage? usage = null;
            var completed = false;

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (data.Length > 0)
                    {
                        var finalEventData = data.ToString();
                        data.Clear();
                        if (finalEventData == "[DONE]")
                        {
                            completed = true;
                            yield return new ChatStreamEvent(
                                ChatStreamEventKind.Completed,
                                FinishReason: finishReason ?? "stop",
                                Usage: usage);
                        }
                        else
                        {
                            var finalChunk = ParseChunk(finalEventData);
                            finishReason = finalChunk.FinishReason ?? finishReason;
                            usage = finalChunk.Usage ?? usage;
                            foreach (var streamEvent in finalChunk.Events)
                            {
                                yield return streamEvent;
                            }
                        }
                    }

                    break;
                }

                if (line.Length == 0)
                {
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var eventData = data.ToString();
                    data.Clear();
                    if (eventData == "[DONE]")
                    {
                        completed = true;
                        yield return new ChatStreamEvent(
                            ChatStreamEventKind.Completed,
                            FinishReason: finishReason ?? "stop",
                            Usage: usage);
                        break;
                    }

                    var chunk = ParseChunk(eventData);
                    finishReason = chunk.FinishReason ?? finishReason;
                    usage = chunk.Usage ?? usage;
                    foreach (var streamEvent in chunk.Events)
                    {
                        yield return streamEvent;
                    }

                    continue;
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
                if (data.Length > MaximumEventCharacters)
                {
                    throw new ChatProviderException("A chat stream event exceeded the size limit.", false);
                }
            }

            if (!completed)
            {
                throw new ChatProviderException("The chat provider stream ended before the [DONE] marker.", true);
            }
        }
    }

    private byte[] BuildPayload(ChatCompletionRequest request)
    {
        var messages = request.Messages.Select(static chatMessage => new ChatPayloadMessage
        {
            Role = RoleName(chatMessage.Role),
            Content = chatMessage.ToolCalls is { Count: > 0 } && chatMessage.Content.Length == 0
                ? null
                : chatMessage.Content,
            ToolCallId = chatMessage.ToolCallId,
            ToolCalls = chatMessage.ToolCalls?.Select(static call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.Arguments },
            }),
        });
        var tools = request.Tools?.Select(static tool => new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.Parameters,
                strict = tool.Strict ? true : (bool?)null,
            },
        });
        var payload = new
        {
            model = options.Model,
            messages,
            stream = true,
            stream_options = new { include_usage = true },
            thinking = new { type = options.ThinkingEnabled ? "enabled" : "disabled" },
            reasoning_effort = options.ThinkingEnabled ? options.ReasoningEffort : null,
            max_tokens = options.MaximumOutputTokens,
            tools = request.Tools is { Count: > 0 } ? tools : null,
            tool_choice = request.Tools is { Count: > 0 } ? "auto" : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    private static ParsedChunk ParseChunk(string eventData)
    {
        try
        {
            using var document = JsonDocument.Parse(eventData);
            var root = document.RootElement;
            var events = new List<ChatStreamEvent>();
            string? finishReason = null;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                }

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    AddTextEvent(delta, "reasoning_content", ChatStreamEventKind.ReasoningDelta, events);
                    AddTextEvent(delta, "content", ChatStreamEventKind.ContentDelta, events);
                    AddToolCallEvents(delta, events);
                }
            }

            return new ParsedChunk(events, finishReason, ParseUsage(root));
        }
        catch (JsonException exception)
        {
            throw new ChatProviderException("The chat provider returned malformed stream JSON.", false, null, exception);
        }
    }

    private static void AddTextEvent(
        JsonElement delta,
        string propertyName,
        ChatStreamEventKind kind,
        List<ChatStreamEvent> events)
    {
        if (delta.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
        {
            events.Add(new ChatStreamEvent(kind, text));
        }
    }

    private static void AddToolCallEvents(JsonElement delta, List<ChatStreamEvent> events)
    {
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : 0;
            var id = ReadOptionalString(call, "id");
            string? name = null;
            string? arguments = null;
            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = ReadOptionalString(function, "name");
                arguments = ReadOptionalString(function, "arguments");
            }

            if (id is not null || name is not null || arguments is not null)
            {
                events.Add(new ChatStreamEvent(
                    ChatStreamEventKind.ToolCallDelta,
                    ToolCall: new ChatToolCallDelta(index, id, name, arguments)));
            }
        }
    }

    private static ChatUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ChatUsage(
            ReadInt32(usage, "prompt_tokens"),
            ReadInt32(usage, "completion_tokens"),
            ReadInt32(usage, "total_tokens"));
    }

    private static int ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static ChatProviderOptions ValidateOptions(ChatProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.BaseUri.IsAbsoluteUri || options.BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The provider base URI must use HTTPS.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        if (options.Model.Length > 256)
        {
            throw new ArgumentException("The provider model name is too long.", nameof(options));
        }

        if (options.ReasoningEffort is not ("high" or "max"))
        {
            throw new ArgumentException("Reasoning effort must be high or max.", nameof(options));
        }

        if (options.MaximumOutputTokens is < 1 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum output tokens are invalid.");
        }

        return options;
    }

    private static void ValidateRequest(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count is < 1 or > MaximumMessages)
        {
            throw new ArgumentException("The chat request has an invalid message count.", nameof(request));
        }

        long totalCharacters = 0;
        foreach (var message in request.Messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content) &&
                !(message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 }))
            {
                throw new ArgumentException("A chat message has no content.", nameof(request));
            }
            totalCharacters += message.Content.Length;
            if (totalCharacters > MaximumMessageCharacters)
            {
                throw new ArgumentException("The chat request exceeds the character limit.", nameof(request));
            }

            if (message.Role == ChatRole.Tool && string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                throw new ArgumentException("Tool messages require a tool call identifier.", nameof(request));
            }

            if (message.Role != ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
            {
                throw new ArgumentException("Only assistant messages may contain tool calls.", nameof(request));
            }

            foreach (var call in message.ToolCalls ?? [])
            {
                if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 256 ||
                    string.IsNullOrWhiteSpace(call.Name) || call.Name.Length > 64 ||
                    call.Arguments.Length > MaximumMessageCharacters)
                {
                    throw new ArgumentException("An assistant tool call is invalid.", nameof(request));
                }
            }
        }

        foreach (var tool in request.Tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 64)
            {
                throw new ArgumentException("A tool name is invalid.", nameof(request));
            }
        }
    }

    private static Uri EnsureTrailingSlash(Uri baseUri) =>
        baseUri.AbsoluteUri[^1] == '/'
            ? baseUri
            : new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);

    private static async Task<string> ReadLimitedErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (output.Length < MaximumErrorCharacters)
        {
            var remaining = Math.Min(buffer.Length, MaximumErrorCharacters - output.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Append(buffer, 0, read);
        }

        return output.ToString().Trim();
    }

    private sealed record ParsedChunk(
        IReadOnlyList<ChatStreamEvent> Events,
        string? FinishReason,
        ChatUsage? Usage);

    private sealed record ChatPayloadMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_calls")]
        public object? ToolCalls { get; init; }
    }
}
