using Velia.Protocol;

namespace Velia.Core.Conversations;

internal static class DesktopContextIntentDetector
{
    private static readonly string[] AccessActions =
    [
        "读", "看", "念", "复述", "说出", "识别", "获取", "分析", "解释", "总结", "提取", "列出", "告诉", "显示", "处理",
        "修改", "替换", "改写", "重写", "写回", "删除", "复制", "拷贝", "备份", "移动", "移到", "挪到", "放到",
        "重命名", "更名", "改名", "换名", "换个名字", "是什么", "有哪些", "多少",
    ];

    private static readonly string[] MutationActions =
    [
        "修改", "替换", "改成", "改为", "改写", "重写", "写回", "覆盖", "删除",
    ];

    private static readonly string[] Denials =
    [
        "不要", "无需", "不用", "不许", "禁止", "别给", "别把", "别将", "取消读取", "停止读取", "不想让你",
        "别读", "别看", "别查看", "别获取", "别识别", "别分析", "别总结", "别提取", "别复制",
        "别拷贝", "别备份", "别移动", "别挪", "别放到", "别重命名", "别更名", "别改名",
        "别换名", "别换名字",
        "别修改", "别替换", "别改写", "别重写", "别写回", "别覆盖", "别删除",
    ];

    private static readonly string[] InstructionalQueries = ["如何", "怎么", "怎样", "什么方法"];

    private static readonly string[] ProspectiveSelectionReferences =
    [
        "准备选", "打算选", "想要选", "我要选", "之后选", "稍后选", "待会选",
    ];

    private static readonly string[] ClipboardReferences = ["剪贴板", "我复制的", "刚复制的"];

    private static readonly string[] DirectSelectionReferences =
    [
        "选区", "所选文字", "所选内容", "所选文件", "当前选中", "已选文字", "已选内容", "已选文件",
        "所选文件夹", "选中文件", "选中文件夹", "高亮文字", "高亮内容", "高亮文本", "蓝色选区", "蓝色高亮",
        "我选中的", "我刚选中的", "刚才选中的",
    ];

    private static readonly string[] SelectionMarkers =
    [
        "选中", "选取", "选定", "选择", "框选", "框起", "圈选", "圈出", "圈起", "划选", "高亮",
    ];

    private static readonly string[] SelectionObjects =
    [
        "字", "文字", "文本", "内容", "话", "词", "词语", "句子", "行", "段", "段落", "句", "部分", "区域", "代码",
        "文件", "文件夹", "文档", "项目", "单元格", "表格",
    ];

    private static readonly string[] FileSelectionObjects = ["文件", "文件夹", "文档", "项目"];
    private static readonly string[] CopyActions = ["复制", "拷贝", "备份"];
    private static readonly string[] MoveActions = ["移动", "移到", "挪到", "放到"];
    private static readonly string[] RenameActions =
    [
        "重命名", "更名", "改名", "换名", "换个名字", "换名字",
        "名称改为", "名称改成", "名字改为", "名字改成", "文件名改为", "文件名改成",
    ];
    private static readonly string[] FileNameOnlyCopyReferences = ["文件名", "复制名称", "拷贝名称"];
    private static readonly string[] DocumentContentActions =
    [
        "读取", "读出", "查看内容", "看看内容", "分析", "总结", "解释", "提取正文", "回答文档", "审阅", "做题",
    ];
    private static readonly string[] DocumentReferences =
    [
        "文档", "文件内容", "正文", "PDF", "DOCX", "TXT", "Markdown", "CSV", "日志文件",
    ];

    public static bool IsExplicitRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (IsBlocked(request) || !ContainsAny(request, AccessActions)) return false;
        if (ContainsAny(request, ClipboardReferences) || ContainsAny(request, DirectSelectionReferences)) return true;
        return ContainsQualifiedSelectionReference(request);
    }

    public static bool IsExplicitSelectionMutationRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return !IsBlocked(request) &&
            ContainsAny(request, MutationActions) &&
            ContainsSelectionReference(request);
    }

    public static bool IsExplicitReadOnlySelectionRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return IsExplicitRequest(request) &&
            !ContainsAny(request, MutationActions) &&
            !ContainsAny(request, CopyActions) &&
            !ContainsAny(request, MoveActions) &&
            !ContainsAny(request, RenameActions);
    }

    public static bool IsExplicitDocumentContentRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return !IsBlocked(request) &&
            ContainsSelectionReference(request) &&
            ContainsAny(request, DocumentReferences) &&
            ContainsAny(request, DocumentContentActions);
    }

    public static bool IsExplicitSelectedFileOperationRequest(string request, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        if (IsBlocked(request) || !ContainsSelectionReference(request) || !ContainsAny(request, FileSelectionObjects))
            return false;
        return action switch
        {
            DesktopContextFileOperations.CopyToSelectedFolder =>
                ContainsAny(request, CopyActions) && !ContainsAny(request, FileNameOnlyCopyReferences),
            DesktopContextFileOperations.MoveToSelectedFolder => ContainsAny(request, MoveActions),
            DesktopContextFileOperations.RenameSelectedFile => ContainsAny(request, RenameActions),
            _ => false,
        };
    }

    private static bool IsBlocked(string request) =>
        ContainsAny(request, Denials) ||
        ContainsAny(request, InstructionalQueries) ||
        ContainsAny(request, ProspectiveSelectionReferences);

    private static bool ContainsSelectionReference(string request) =>
        ContainsAny(request, DirectSelectionReferences) || ContainsQualifiedSelectionReference(request);

    private static bool ContainsQualifiedSelectionReference(string request)
    {
        const int ContextRadius = 24;
        foreach (var marker in SelectionMarkers)
        {
            var offset = 0;
            while (offset < request.Length)
            {
                var index = request.IndexOf(marker, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var start = Math.Max(0, index - ContextRadius);
                var end = Math.Min(request.Length, index + marker.Length + ContextRadius);
                var surroundingText = request.AsSpan(start, end - start);
                foreach (var item in SelectionObjects)
                {
                    if (surroundingText.Contains(item, StringComparison.OrdinalIgnoreCase)) return true;
                }
                offset = index + marker.Length;
            }
        }
        return false;
    }

    private static bool ContainsAny(string request, IEnumerable<string> markers) =>
        markers.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
