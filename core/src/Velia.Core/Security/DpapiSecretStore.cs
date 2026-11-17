using System.Security.Cryptography;
using System.Text;
using Velia.Protocol;

namespace Velia.Core.Security;

internal sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private const int MaximumSecretCharacters = 16_384;

    public bool IsConfigured(string provider)
    {
        ValidateProvider(provider);
        return File.Exists(GetPath(provider));
    }

    public void Save(string provider, string secret)
    {
        ValidateProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length > MaximumSecretCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        var clearBytes = Encoding.UTF8.GetBytes(secret);
        byte[]? protectedBytes = null;
        var path = GetPath(provider);
        var temporaryPath = path + ".tmp";

        try
        {
            protectedBytes = ProtectedData.Protect(clearBytes, GetEntropy(provider), DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public string? Load(string provider)
    {
        ValidateProvider(provider);
        var path = GetPath(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var clearBytes = ProtectedData.Unprotect(protectedBytes, GetEntropy(provider), DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Remove(string provider)
    {
        ValidateProvider(provider);
        var path = GetPath(provider);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string provider) => Path.Combine(directory, $"{provider}.vault");

    private static byte[] GetEntropy(string provider) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"velia/provider-secret/v1/{provider}"));

    private static void ValidateProvider(string provider)
    {
        if (!ProviderIds.All.Contains(provider))
        {
            throw new ArgumentException("Provider is not allowlisted.", nameof(provider));
        }
    }
}
