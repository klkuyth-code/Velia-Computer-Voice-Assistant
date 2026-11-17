using Velia.Core.Security;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public void SecretRoundTripsWithoutPlaintextOnDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"velia-secret-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string secret = "test-only-provider-secret-value";
            var store = new DpapiSecretStore(directory);
            store.Save(ProviderIds.Llm, secret);

            Assert.True(store.IsConfigured(ProviderIds.Llm));
            Assert.Equal(secret, store.Load(ProviderIds.Llm));
            var persisted = File.ReadAllBytes(Path.Combine(directory, "llm.vault"));
            Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(persisted), StringComparison.Ordinal);

            store.Remove(ProviderIds.Llm);
            Assert.False(store.IsConfigured(ProviderIds.Llm));
            Assert.Null(store.Load(ProviderIds.Llm));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownProviderIsRejected()
    {
        var store = new DpapiSecretStore(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => store.Save("shell", "not-allowed"));
    }
}
