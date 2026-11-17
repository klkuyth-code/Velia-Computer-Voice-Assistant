using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Velia.Protocol;

namespace Velia.Core.Conversations;

internal sealed record ConversationHistoryOptions(bool Enabled, int RetentionDays);

internal sealed record ConversationHistoryEntry(string Role, string Text, DateTimeOffset AtUtc);

internal sealed record StoredConversation(
    Guid ConversationId,
    string Title,
    string Preview,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ConversationHistoryEntry> Messages);

internal interface IConversationHistoryStore
{
    ConversationHistoryOptions GetOptions();
    ConversationHistoryOptions UpdateOptions(bool enabled, int retentionDays);
    IReadOnlyList<StoredConversation> List();
    StoredConversation? Get(Guid conversationId);
    Guid? Save(Guid? conversationId, IReadOnlyList<ConversationHistoryEntry> messages);
    bool Delete(Guid conversationId);
    int Clear();
}

internal sealed class DisabledConversationHistoryStore : IConversationHistoryStore
{
    public ConversationHistoryOptions GetOptions() => new(false, ConversationHistoryRetention.DefaultDays);
    public ConversationHistoryOptions UpdateOptions(bool enabled, int retentionDays) => GetOptions();
    public IReadOnlyList<StoredConversation> List() => [];
    public StoredConversation? Get(Guid conversationId) => null;
    public Guid? Save(Guid? conversationId, IReadOnlyList<ConversationHistoryEntry> messages) => null;
    public bool Delete(Guid conversationId) => false;
    public int Clear() => 0;
}

internal sealed class DpapiConversationHistoryStore : IConversationHistoryStore
{
    private const int MaximumVaultBytes = 8 * 1024 * 1024;
    private const int MaximumStoredConversations = 100;
    private const int MaximumMessagesPerConversation = 24;
    private const int MaximumMessageCharacters = 32_768;
    private const int MaximumTotalCharacters = 2_000_000;
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("velia/conversation-history/v1"));
    private readonly object gate = new();
    private readonly string directory;
    private readonly string settingsPath;
    private readonly string vaultPath;
    private readonly TimeProvider clock;

    public DpapiConversationHistoryStore(string directory, TimeProvider? timeProvider = null)
    {
        this.directory = Path.GetFullPath(directory);
        settingsPath = Path.Combine(this.directory, "settings.json");
        vaultPath = Path.Combine(this.directory, "history.vault");
        clock = timeProvider ?? TimeProvider.System;
    }

    public ConversationHistoryOptions GetOptions()
    {
        lock (gate) return ReadOptions();
    }

    public ConversationHistoryOptions UpdateOptions(bool enabled, int retentionDays)
    {
        ValidateRetention(retentionDays);
        lock (gate)
        {
            var options = new ConversationHistoryOptions(enabled, retentionDays);
            WriteOptions(options);
            if (!enabled)
            {
                TryDelete(vaultPath);
                TryDelete(vaultPath + ".tmp");
            }
            else
            {
                var state = ReadState();
                if (Prune(state, options)) WriteState(state);
            }
            return options;
        }
    }

    public IReadOnlyList<StoredConversation> List()
    {
        lock (gate)
        {
            var options = ReadOptions();
            if (!options.Enabled) return [];
            var state = ReadState();
            if (Prune(state, options)) WriteState(state);
            return state.Conversations
                .OrderByDescending(static item => item.UpdatedAtUtc)
                .Select(ToStoredConversation)
                .ToArray();
        }
    }

    public StoredConversation? Get(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return null;
        lock (gate)
        {
            var options = ReadOptions();
            if (!options.Enabled) return null;
            var state = ReadState();
            if (Prune(state, options)) WriteState(state);
            var item = state.Conversations.SingleOrDefault(value => value.ConversationId == conversationId);
            return item is null ? null : ToStoredConversation(item);
        }
    }

    public Guid? Save(Guid? conversationId, IReadOnlyList<ConversationHistoryEntry> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (gate)
        {
            var options = ReadOptions();
            if (!options.Enabled) return null;
            var sanitized = SanitizeMessages(messages);
            if (sanitized.Count == 0) return conversationId;

            var state = ReadState();
            Prune(state, options);
            var id = conversationId.GetValueOrDefault() == Guid.Empty
                ? Guid.NewGuid()
                : conversationId!.Value;
            var existing = state.Conversations.SingleOrDefault(item => item.ConversationId == id);
            var createdAtUtc = existing?.CreatedAtUtc ?? sanitized[0].AtUtc;
            var updatedAtUtc = sanitized[^1].AtUtc;
            var replacement = new ConversationDocument(
                id,
                BuildTitle(sanitized),
                BuildPreview(sanitized),
                createdAtUtc,
                updatedAtUtc,
                sanitized.Select(static item => new MessageDocument(item.Role, item.Text, item.AtUtc)).ToList());
            if (existing is null) state.Conversations.Add(replacement);
            else state.Conversations[state.Conversations.IndexOf(existing)] = replacement;
            Prune(state, options);
            WriteState(state);
            return id;
        }
    }

    public bool Delete(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return false;
        lock (gate)
        {
            var state = ReadState();
            var removed = state.Conversations.RemoveAll(item => item.ConversationId == conversationId) > 0;
            if (removed) WriteState(state);
            return removed;
        }
    }

    public int Clear()
    {
        lock (gate)
        {
            var count = ReadState().Conversations.Count;
            TryDelete(vaultPath);
            TryDelete(vaultPath + ".tmp");
            return count;
        }
    }

    private ConversationHistoryOptions ReadOptions()
    {
        if (!File.Exists(settingsPath))
            return new ConversationHistoryOptions(true, ConversationHistoryRetention.DefaultDays);
        try
        {
            if (new FileInfo(settingsPath).Length > 4096) throw new JsonException();
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(settingsPath), JsonDefaults.Strict);
            if (document?.Schema != "velia-conversation-history-settings/v1" ||
                !ConversationHistoryRetention.AllowedDays.Contains(document.RetentionDays))
                throw new JsonException();
            return new ConversationHistoryOptions(document.Enabled, document.RetentionDays);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDelete(settingsPath);
            return new ConversationHistoryOptions(true, ConversationHistoryRetention.DefaultDays);
        }
    }

    private void WriteOptions(ConversationHistoryOptions options)
    {
        Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new SettingsDocument("velia-conversation-history-settings/v1", options.Enabled, options.RetentionDays),
                    JsonDefaults.Strict));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private VaultDocument ReadState()
    {
        if (!File.Exists(vaultPath)) return EmptyState();
        byte[]? protectedBytes = null;
        byte[]? clearBytes = null;
        try
        {
            var length = new FileInfo(vaultPath).Length;
            if (length is <= 0 or > MaximumVaultBytes) throw new JsonException();
            protectedBytes = File.ReadAllBytes(vaultPath);
            clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var state = JsonSerializer.Deserialize<VaultDocument>(clearBytes, JsonDefaults.Strict);
            ValidateState(state);
            return state!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            TryDelete(vaultPath);
            TryDelete(vaultPath + ".tmp");
            return EmptyState();
        }
        finally
        {
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private void WriteState(VaultDocument state)
    {
        if (state.Conversations.Count == 0)
        {
            TryDelete(vaultPath);
            TryDelete(vaultPath + ".tmp");
            return;
        }

        Directory.CreateDirectory(directory);
        byte[]? clearBytes = null;
        byte[]? protectedBytes = null;
        var temporaryPath = vaultPath + ".tmp";
        try
        {
            clearBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Strict);
            if (clearBytes.Length > MaximumVaultBytes) throw new InvalidOperationException("Conversation history exceeds the encrypted vault limit.");
            protectedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, vaultPath, overwrite: true);
        }
        finally
        {
            if (clearBytes is not null) CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            TryDelete(temporaryPath);
        }
    }

    private bool Prune(VaultDocument state, ConversationHistoryOptions options)
    {
        var changed = false;
        var cutoff = clock.GetUtcNow().AddDays(-options.RetentionDays);
        changed |= state.Conversations.RemoveAll(item => item.UpdatedAtUtc < cutoff) > 0;
        state.Conversations.Sort(static (left, right) => right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc));
        while (state.Conversations.Count > MaximumStoredConversations ||
               state.Conversations.Sum(static item => item.Messages.Sum(static message => message.Text.Length)) > MaximumTotalCharacters)
        {
            state.Conversations.RemoveAt(state.Conversations.Count - 1);
            changed = true;
        }
        return changed;
    }

    private static List<ConversationHistoryEntry> SanitizeMessages(IReadOnlyList<ConversationHistoryEntry> messages)
    {
        var start = Math.Max(0, messages.Count - MaximumMessagesPerConversation);
        var sanitized = new List<ConversationHistoryEntry>(messages.Count - start);
        for (var index = start; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!ConversationHistoryRoles.All.Contains(message.Role) || string.IsNullOrWhiteSpace(message.Text)) continue;
            var text = ConversationHistoryRedactor.Redact(message.Text.Trim());
            if (text.Length > MaximumMessageCharacters) text = text[..MaximumMessageCharacters];
            sanitized.Add(message with { Text = text });
        }
        return sanitized;
    }

    private static void ValidateState(VaultDocument? state)
    {
        if (state?.Schema != "velia-conversation-history-vault/v1" ||
            state.Conversations.Count > MaximumStoredConversations)
            throw new JsonException();
        var ids = new HashSet<Guid>();
        var totalCharacters = 0;
        foreach (var conversation in state.Conversations)
        {
            if (conversation.ConversationId == Guid.Empty || !ids.Add(conversation.ConversationId) ||
                conversation.Title.Length is < 1 or > 80 || conversation.Preview.Length > 160 ||
                conversation.Messages.Count is < 1 or > MaximumMessagesPerConversation ||
                conversation.CreatedAtUtc > conversation.UpdatedAtUtc)
                throw new JsonException();
            foreach (var message in conversation.Messages)
            {
                if (!ConversationHistoryRoles.All.Contains(message.Role) ||
                    message.Text.Length is < 1 or > MaximumMessageCharacters ||
                    message.AtUtc < conversation.CreatedAtUtc || message.AtUtc > conversation.UpdatedAtUtc)
                    throw new JsonException();
                totalCharacters += message.Text.Length;
                if (totalCharacters > MaximumTotalCharacters) throw new JsonException();
            }
        }
    }

    private static StoredConversation ToStoredConversation(ConversationDocument value) => new(
        value.ConversationId,
        value.Title,
        value.Preview,
        value.CreatedAtUtc,
        value.UpdatedAtUtc,
        value.Messages.Select(static item => new ConversationHistoryEntry(item.Role, item.Text, item.AtUtc)).ToArray());

    private static string BuildTitle(IReadOnlyList<ConversationHistoryEntry> messages) =>
        Abbreviate(messages.FirstOrDefault(static item => item.Role == ConversationHistoryRoles.User)?.Text ?? "新对话", 40);

    private static string BuildPreview(IReadOnlyList<ConversationHistoryEntry> messages) =>
        Abbreviate(messages.LastOrDefault(static item => item.Role == ConversationHistoryRoles.Assistant)?.Text ?? messages[^1].Text, 90);

    private static string Abbreviate(string value, int maximum)
    {
        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        return normalized.Length <= maximum ? normalized : $"{normalized[..(maximum - 1)]}…";
    }

    private static VaultDocument EmptyState() => new("velia-conversation-history-vault/v1", []);

    private static void ValidateRetention(int retentionDays)
    {
        if (!ConversationHistoryRetention.AllowedDays.Contains(retentionDays))
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record SettingsDocument(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("retentionDays")] int RetentionDays);

    private sealed record VaultDocument(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("conversations")] List<ConversationDocument> Conversations);

    private sealed record ConversationDocument(
        [property: JsonPropertyName("conversationId")] Guid ConversationId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("preview")] string Preview,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
        [property: JsonPropertyName("messages")] List<MessageDocument> Messages);

    private sealed record MessageDocument(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);
}

internal static class ConversationHistoryRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex LabelledSecret = new(
        @"(?i)(api[ _-]?key|access[ _-]?token|authorization|secret|token|密钥|令牌)(\s*[:=：]\s*)(bearer\s+)?([^\s,;，；]{8,})",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex KnownToken = new(
        @"(?i)\b(sk-[a-z0-9_-]{12,}|fc-[a-z0-9_-]{12,}|AIza[a-z0-9_-]{20,})\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var labelled = LabelledSecret.Replace(value, "$1$2[敏感信息已隐藏]");
        return KnownToken.Replace(labelled, "[敏感信息已隐藏]");
    }
}
