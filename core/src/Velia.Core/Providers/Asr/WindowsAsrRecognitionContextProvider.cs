using Velia.Core.WindowsControl;

namespace Velia.Core.Providers.Asr;

/// <summary>
/// Supplies only non-sensitive control vocabulary and installed application
/// display names to ASR. Personal filenames, window titles and document text
/// deliberately stay local and are never uploaded as recognition context.
/// </summary>
internal sealed class WindowsAsrRecognitionContextProvider(
    IInstalledApplicationCatalog installedApplications) : IAsrRecognitionContextProvider
{
    private static readonly string[] ControlVocabulary =
    [
        "Velia", "停止", "停下", "取消任务", "打开", "关闭", "最小化", "最大化",
        "恢复窗口", "切换到", "点击", "输入", "写入", "保存", "返回上一页",
        "关闭标签页", "记事本", "计算器", "谷歌浏览器", "微信", "Word", "Excel",
    ];

    public AsrRecognitionContext GetCurrent()
    {
        var hotwords = ControlVocabulary
            .Concat(installedApplications.GetHotwords(180))
            .Where(static value => value.Length is >= 2 and <= 40)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
        return new AsrRecognitionContext(hotwords);
    }
}
