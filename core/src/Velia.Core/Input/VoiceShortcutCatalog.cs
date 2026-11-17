using Velia.Protocol;

namespace Velia.Core.Input;

internal sealed record VoiceShortcutDefinition(
    string Id,
    string Key,
    string Label,
    string Help,
    Func<bool> IsDown);

internal static class VoiceShortcutCatalog
{
    private const int VirtualKeyF8 = 0x77;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeySpace = 0x20;

    public static VoiceShortcutDefinition F8 { get; } = new(
        VoiceShortcutIds.F8,
        "F8",
        "F8",
        "部分笔记本需要同时按住 Fn。",
        () => IsKeyDown(VirtualKeyF8));

    public static VoiceShortcutDefinition CtrlShiftSpace { get; } = new(
        VoiceShortcutIds.CtrlShiftSpace,
        "Ctrl+Shift+Space",
        "Ctrl + Shift + 空格",
        "按住 Ctrl 和 Shift，再按住空格说话；Velia 会拦截最终空格键，不会输入文档或触发 Alt 菜单提示。",
        () => IsKeyDown(VirtualKeyControl) && IsKeyDown(VirtualKeyShift) && IsKeyDown(VirtualKeySpace));

    public static VoiceShortcutDefinition Default => F8;

    public static VoiceShortcutDefinition Get(string? id) => id switch
    {
        VoiceShortcutIds.F8 => F8,
        VoiceShortcutIds.CtrlShiftSpace => CtrlShiftSpace,
        VoiceShortcutIds.CtrlAltSpace => CtrlShiftSpace,
        _ => throw new ArgumentException("不支持的语音快捷键。", nameof(id)),
    };

    public static VoiceShortcutSetting ToSetting(VoiceShortcutDefinition definition) =>
        new(definition.Id, definition.Key, definition.Label, definition.Help);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
