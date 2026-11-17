namespace Velia.Core.Security;

internal interface ISecretStore
{
    bool IsConfigured(string provider);

    void Save(string provider, string secret);

    string? Load(string provider);

    void Remove(string provider);
}
