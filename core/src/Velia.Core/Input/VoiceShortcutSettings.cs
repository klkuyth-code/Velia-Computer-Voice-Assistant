using System.Text.Json;
using Velia.Protocol;

namespace Velia.Core.Input;

internal sealed class VoiceShortcutSettings
{
    private readonly object gate = new();
    private readonly string path;
    private VoiceShortcutDefinition current;

    public VoiceShortcutSettings(string path)
    {
        this.path = Path.GetFullPath(path);
        current = Load(this.path, out var migrated);
        if (migrated)
        {
            try { Persist(current.Id); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public VoiceShortcutDefinition Current
    {
        get { lock (gate) return current; }
    }

    public VoiceShortcutSetting Get() => VoiceShortcutCatalog.ToSetting(Current);

    public VoiceShortcutSetting Set(string shortcutId)
    {
        var next = VoiceShortcutCatalog.Get(shortcutId);
        lock (gate)
        {
            Persist(next.Id);
            current = next;
        }
        return VoiceShortcutCatalog.ToSetting(next);
    }

    private static VoiceShortcutDefinition Load(string path, out bool migrated)
    {
        migrated = false;
        try
        {
            if (!File.Exists(path)) return VoiceShortcutCatalog.Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetRawText().Length > 1_024 ||
                !root.TryGetProperty("voiceShortcutId", out var id) || id.ValueKind != JsonValueKind.String)
            {
                return VoiceShortcutCatalog.Default;
            }
            var shortcutId = id.GetString();
            migrated = StringComparer.Ordinal.Equals(shortcutId, VoiceShortcutIds.CtrlAltSpace);
            return VoiceShortcutCatalog.Get(shortcutId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return VoiceShortcutCatalog.Default;
        }
    }

    private void Persist(string id)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("快捷键配置目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { voiceShortcutId = id }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
