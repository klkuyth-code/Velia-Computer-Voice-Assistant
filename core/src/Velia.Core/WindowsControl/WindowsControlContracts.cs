namespace Velia.Core.WindowsControl;

internal static class WindowsControlActions
{
    public const string OpenApplication = "openApplication";
    public const string CloseApplication = "closeApplication";
    public const string MinimizeApplication = "minimizeApplication";
    public const string RestoreApplication = "restoreApplication";
    public const string MaximizeApplication = "maximizeApplication";
    public const string SetVolume = "setVolume";
    public const string AdjustVolume = "adjustVolume";
    public const string Mute = "mute";
    public const string Unmute = "unmute";
    public const string StartTimer = "startTimer";
    public const string CancelTimer = "cancelTimer";
}

internal static class WindowsApplicationIds
{
    public const string Notepad = "notepad";
    public const string Calculator = "calculator";
    public const string FileExplorer = "fileExplorer";
    public const string Paint = "paint";
    public const string SnippingTool = "snippingTool";
    public const string Chrome = "chrome";
    public const string Edge = "edge";
    public const string WeChat = "wechat";
    public const string Word = "word";
    public const string Excel = "excel";
    public const string PowerPoint = "powerPoint";
    public const string MicrosoftWord = "microsoftWord";
    public const string MicrosoftExcel = "microsoftExcel";
    public const string MicrosoftPowerPoint = "microsoftPowerPoint";
    public const string Qq = "qq";
    public const string WeCom = "weCom";
    public const string DingTalk = "dingTalk";
    public const string Feishu = "feishu";
    public const string TencentMeeting = "tencentMeeting";
    public const string WpsWriter = "wpsWriter";
    public const string WpsSpreadsheets = "wpsSpreadsheets";
    public const string WpsPresentation = "wpsPresentation";
    public const string VsCode = "vsCode";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Notepad, Calculator, FileExplorer, Paint, SnippingTool,
        Chrome, Edge, WeChat, Word, Excel, PowerPoint,
        MicrosoftWord, MicrosoftExcel, MicrosoftPowerPoint,
        Qq, WeCom, DingTalk, Feishu, TencentMeeting,
        WpsWriter, WpsSpreadsheets, WpsPresentation, VsCode,
    };
}

internal sealed record WindowsControlCommand(
    string Action,
    string? ApplicationId = null,
    int? Value = null,
    int? DurationSeconds = null);

internal sealed record WindowsControlResult(
    string Action,
    bool Verified,
    string State,
    string? ApplicationId = null,
    string? ApplicationName = null,
    int? VolumePercent = null,
    bool? Muted = null,
    Guid? TimerId = null,
    int? DurationSeconds = null,
    DateTimeOffset? DueAtUtc = null);

internal sealed record DesktopFileOpenCommand(string RequestedName);

internal sealed record DesktopFileOpenResult(
    bool Verified,
    string State,
    string FileName,
    bool IsFolder = false);

internal sealed class WindowsControlException(
    string code,
    string message,
    bool retryable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

internal interface IWindowsControlService
{
    Task<WindowsControlResult> ExecuteAsync(WindowsControlCommand command, CancellationToken cancellationToken);
}

internal interface IDesktopFileOpenService
{
    Task<DesktopFileOpenResult> OpenAsync(
        DesktopFileOpenCommand command,
        CancellationToken cancellationToken);
}

internal interface IWindowsApplicationController
{
    Task<WindowsControlResult> OpenAsync(string applicationId, CancellationToken cancellationToken);
    Task<WindowsControlResult> CloseAsync(string applicationId, CancellationToken cancellationToken);
    Task<WindowsControlResult> MinimizeAsync(string applicationId, CancellationToken cancellationToken);
    Task<WindowsControlResult> RestoreAsync(string applicationId, CancellationToken cancellationToken);
    Task<WindowsControlResult> MaximizeAsync(string applicationId, CancellationToken cancellationToken);
}

internal interface IWindowsVolumeController
{
    WindowsControlResult Set(int percentage);
    WindowsControlResult Adjust(int delta);
    WindowsControlResult SetMuted(bool muted);
}

internal interface IWindowsTimerController : IDisposable
{
    WindowsControlResult Start(int durationSeconds);
    WindowsControlResult Cancel();
}

internal interface IWindowsTimerAlert
{
    void Play();
}
