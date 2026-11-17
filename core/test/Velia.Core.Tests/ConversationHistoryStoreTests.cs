using System.Text;
using Velia.Core.Conversations;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ConversationHistoryStoreTests
{
    [Fact]
    public void RoundTripsEncryptedHistoryWithoutPlaintextOnDisk()
    {
        WithStore((directory, store) =>
        {
            const string sensitive = "api_key: sk-test-only-secret-token-123456";
            var now = DateTimeOffset.UtcNow;
            var id = store.Save(null,
            [
                new ConversationHistoryEntry(ConversationHistoryRoles.User, sensitive, now),
                new ConversationHistoryEntry(ConversationHistoryRoles.Assistant, "测试回答", now.AddSeconds(1)),
            ]);

            Assert.NotNull(id);
            var persisted = File.ReadAllBytes(Path.Combine(directory, "history.vault"));
            Assert.DoesNotContain(sensitive, Encoding.UTF8.GetString(persisted), StringComparison.Ordinal);
            var loaded = store.Get(id!.Value);
            Assert.NotNull(loaded);
            Assert.Contains("[敏感信息已隐藏]", loaded.Messages[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-test-only", loaded.Messages[0].Text, StringComparison.Ordinal);
            Assert.Single(store.List());
        });
    }

    [Fact]
    public void DisablingHistoryDeletesTheVaultAndStartsEmptyWhenReenabled()
    {
        WithStore((directory, store) =>
        {
            var now = DateTimeOffset.UtcNow;
            store.Save(null,
            [
                new ConversationHistoryEntry(ConversationHistoryRoles.User, "问题", now),
                new ConversationHistoryEntry(ConversationHistoryRoles.Assistant, "回答", now.AddSeconds(1)),
            ]);
            Assert.True(File.Exists(Path.Combine(directory, "history.vault")));

            var disabled = store.UpdateOptions(false, 30);
            Assert.False(disabled.Enabled);
            Assert.False(File.Exists(Path.Combine(directory, "history.vault")));
            Assert.Empty(store.List());

            store.UpdateOptions(true, 30);
            Assert.Empty(store.List());
        });
    }

    [Fact]
    public void RetentionPrunesExpiredConversations()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        WithStore((_, store) =>
        {
            var old = clock.GetUtcNow().AddDays(-31);
            store.Save(null,
            [
                new ConversationHistoryEntry(ConversationHistoryRoles.User, "旧问题", old),
                new ConversationHistoryEntry(ConversationHistoryRoles.Assistant, "旧回答", old.AddSeconds(1)),
            ]);
            Assert.Empty(store.List());
        }, clock);
    }

    [Fact]
    public void CorruptVaultIsRemovedWithoutCrashingHistoryReads()
    {
        WithStore((directory, store) =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "history.vault"), [1, 2, 3, 4]);

            Assert.Empty(store.List());
            Assert.False(File.Exists(Path.Combine(directory, "history.vault")));
        });
    }

    private static void WithStore(
        Action<string, DpapiConversationHistoryStore> test,
        TimeProvider? timeProvider = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"velia-history-test-{Guid.NewGuid():N}");
        try
        {
            test(directory, new DpapiConversationHistoryStore(directory, timeProvider));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
