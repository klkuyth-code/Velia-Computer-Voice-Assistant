using Velia.Core.DesktopVisual;
using Velia.Core.Documents;

namespace Velia.Core.Conversations;

/// <summary>
/// One local, model-free snapshot shared by text, ordinary voice and Live
/// routing. It describes recent verified state; it never grants new authority.
/// </summary>
internal sealed record LocalCommandContext(
    string? WebPlatform,
    ActiveInteractionSurfaceKind Surface,
    ForegroundDocumentKind ForegroundDocument,
    string? DocumentFileName,
    string? Application,
    string? Window,
    string? LastVerifiedTarget)
{
    public bool AllowsImplicitTextDocumentTarget => ForegroundDocument switch
    {
        ForegroundDocumentKind.Text or ForegroundDocumentKind.Word => true,
        ForegroundDocumentKind.Excel or ForegroundDocumentKind.Other => false,
        _ => DocumentFileName?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) == true ||
             DocumentFileName?.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) == true,
    };
}
