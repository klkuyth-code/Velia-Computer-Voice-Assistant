using System.Text.Json;
using Velia.Core.Input;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class VoiceShortcutSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"velia-shortcut-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultsToF8AndPersistsAnAllowlistedAlternative()
    {
        var path = Path.Combine(root, "settings.json");
        var settings = new VoiceShortcutSettings(path);

        Assert.Equal(VoiceShortcutIds.F8, settings.Get().Id);
        var changed = settings.Set(VoiceShortcutIds.CtrlShiftSpace);

        Assert.Equal("Ctrl+Shift+Space", changed.Key);
        Assert.Equal(VoiceShortcutIds.CtrlShiftSpace, new VoiceShortcutSettings(path).Get().Id);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(VoiceShortcutIds.CtrlShiftSpace, document.RootElement.GetProperty("voiceShortcutId").GetString());
    }

    [Fact]
    public void MigratesTheLegacyCtrlAltSpaceSettingWithoutKeepingAlt()
    {
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{\"voiceShortcutId\":\"ctrlAltSpace\"}");

        var migrated = new VoiceShortcutSettings(path).Get();

        Assert.Equal(VoiceShortcutIds.CtrlShiftSpace, migrated.Id);
        Assert.Equal("Ctrl+Shift+Space", migrated.Key);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(VoiceShortcutIds.CtrlShiftSpace, document.RootElement.GetProperty("voiceShortcutId").GetString());
    }

    [Fact]
    public void RejectsUnknownShortcutsAndRecoversFromMalformedStorage()
    {
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{not-json");
        var settings = new VoiceShortcutSettings(path);

        Assert.Equal(VoiceShortcutIds.F8, settings.Get().Id);
        Assert.Throws<ArgumentException>(() => settings.Set("shell"));
        Assert.Equal(VoiceShortcutIds.F8, settings.Get().Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
