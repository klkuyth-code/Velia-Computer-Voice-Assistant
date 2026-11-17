using Velia.Core.Conversations;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopContextIntentDetectorTests
{
    [Theory]
    [InlineData("看一下我选中的话是什么意思")]
    [InlineData("解释我刚选中的句子")]
    [InlineData("告诉我高亮文字表达了什么")]
    public void RecognizesNaturalExplicitTextSelectionQuestions(string request)
    {
        Assert.True(DesktopContextIntentDetector.IsExplicitRequest(request));
        Assert.True(DesktopContextIntentDetector.IsExplicitReadOnlySelectionRequest(request));
    }

    [Theory]
    [InlineData("把我选中的文字替换为新文字")]
    [InlineData("把我选中的文件复制到选中的文件夹")]
    [InlineData("把选中的文件移动到选中的文件夹")]
    [InlineData("把选中的文件重命名为报告")]
    public void DoesNotTreatSelectionMutationsAsReadOnlyQuestions(string request) =>
        Assert.False(DesktopContextIntentDetector.IsExplicitReadOnlySelectionRequest(request));

    [Theory]
    [InlineData("请读取我记事本里面选中的3行内容。")]
    [InlineData("准确读出我选中的三行内容")]
    [InlineData("请读取我选取的三段文字。")]
    [InlineData("读一下我框起来的两行")]
    [InlineData("查看当前高亮的内容")]
    [InlineData("念出我圈出来的文字")]
    [InlineData("复制刚刚选择的文本")]
    [InlineData("查看当前选区")]
    [InlineData("总结这段高亮文字")]
    [InlineData("列出资源管理器里选定的两个文件")]
    [InlineData("告诉我剪贴板里是什么")]
    [InlineData("把我选中的文字替换为新文字")]
    public void AcceptsExplicitNaturalSelectionAndClipboardRequests(string request)
    {
        Assert.True(DesktopContextIntentDetector.IsExplicitRequest(request));
    }

    [Theory]
    [InlineData("如何在记事本选中三行内容")]
    [InlineData("不要读取我选中的文字")]
    [InlineData("请别查看我高亮的内容")]
    [InlineData("不用读取我框选的文字")]
    [InlineData("告诉我怎样读取选中文字")]
    [InlineData("读取我准备选取的文件")]
    [InlineData("选取一个方案，然后告诉我天气")]
    [InlineData("介绍一下今天的安排")]
    [InlineData("打开记事本")]
    public void RejectsInstructionsWithoutExplicitContextAccessConsent(string request)
    {
        Assert.False(DesktopContextIntentDetector.IsExplicitRequest(request));
    }

    [Theory]
    [InlineData("把我选取的三段文字替换为新文字")]
    [InlineData("修改当前选区")]
    [InlineData("删除我圈出来的两行")]
    public void AcceptsExplicitSelectionMutationRequests(string request)
    {
        Assert.True(DesktopContextIntentDetector.IsExplicitSelectionMutationRequest(request));
    }

    [Theory]
    [InlineData("读取我选取的文字")]
    [InlineData("不要修改我选中的文字")]
    [InlineData("告诉我怎样替换选中文字")]
    [InlineData("替换剪贴板里的文字")]
    public void RejectsSelectionMutationWithoutExplicitConsent(string request)
    {
        Assert.False(DesktopContextIntentDetector.IsExplicitSelectionMutationRequest(request));
    }

    [Theory]
    [InlineData("总结我当前选中的 PDF 文档")]
    [InlineData("分析选中的DOCX文档")]
    [InlineData("读取当前选中文件的正文")]
    public void AcceptsExplicitSelectedDocumentContentRequests(string request)
    {
        Assert.True(DesktopContextIntentDetector.IsExplicitDocumentContentRequest(request));
    }

    [Theory]
    [InlineData("告诉我当前选中的文件名")]
    [InlineData("列出资源管理器里选定的两个文件")]
    [InlineData("不要总结我选中的 PDF 文档")]
    public void RejectsMetadataOnlyOrDeniedDocumentContentRequests(string request)
    {
        Assert.False(DesktopContextIntentDetector.IsExplicitDocumentContentRequest(request));
    }

    [Theory]
    [InlineData("把我选中的文件复制到选中的文件夹", DesktopContextFileOperations.CopyToSelectedFolder)]
    [InlineData("将所选文件拷贝到所选文件夹", DesktopContextFileOperations.CopyToSelectedFolder)]
    [InlineData("把选中的文件移动到选中的文件夹", DesktopContextFileOperations.MoveToSelectedFolder)]
    [InlineData("把我选取的文档挪到选中的文件夹", DesktopContextFileOperations.MoveToSelectedFolder)]
    [InlineData("把选中的文件重命名为报告", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("把选中的文件更名为报告", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("给我选取的文件改名", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("把我选中的文档换个名字", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("把所选文件的名字改成报告", DesktopContextFileOperations.RenameSelectedFile)]
    public void AcceptsOnlyTheMatchingExplicitSelectedFileOperation(string request, string action)
    {
        Assert.True(DesktopContextIntentDetector.IsExplicitSelectedFileOperationRequest(request, action));
    }

    [Theory]
    [InlineData("把我选中的文件复制到选中的文件夹", DesktopContextFileOperations.MoveToSelectedFolder)]
    [InlineData("不要移动我选中的文件", DesktopContextFileOperations.MoveToSelectedFolder)]
    [InlineData("告诉我怎样重命名选中的文件", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("不要更名我选中的文件", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("别给选中的文件换名字", DesktopContextFileOperations.RenameSelectedFile)]
    [InlineData("复制选中文件的文件名", DesktopContextFileOperations.CopyToSelectedFolder)]
    [InlineData("移动报告文件", DesktopContextFileOperations.MoveToSelectedFolder)]
    public void RejectsMismatchedDeniedOrUnselectedFileOperations(string request, string action)
    {
        Assert.False(DesktopContextIntentDetector.IsExplicitSelectedFileOperationRequest(request, action));
    }
}
