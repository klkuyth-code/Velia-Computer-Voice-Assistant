namespace Velia.Core.WindowsControl;

using Velia.Core.DesktopVisual;
using Velia.Core.DesktopContext;
using Velia.Core.Documents;

internal enum DeterministicLocalTaskKind
{
    BrowserTab,
    BrowserNavigation,
    BrowserElementAction,
    BrowserArticleToWord,
    WindowsControl,
    DesktopFolderCreate,
    DesktopDocumentCreate,
    DesktopWordDocumentCreate,
    DesktopExcelWorkbookCreate,
    DesktopExcelCellEdit,
    DesktopDocumentEdit,
    DesktopFileOpen,
    DesktopNamedFileMove,
    DesktopAction,
}

internal sealed record DeterministicLocalTaskIntent(
    DeterministicLocalTaskKind Kind,
    string CanonicalKey,
    BrowserTabCommand? BrowserTab = null,
    BrowserNavigationCommand? BrowserNavigation = null,
    BrowserElementActionCommand? BrowserElementAction = null,
    BrowserArticleToWordCommand? BrowserArticleToWord = null,
    WindowsControlCommand? WindowsControl = null,
    DesktopFolderCreateCommand? DesktopFolderCreate = null,
    DesktopDocumentCreateCommand? DesktopDocumentCreate = null,
    DesktopWordDocumentCreateCommand? DesktopWordDocumentCreate = null,
    DesktopExcelWorkbookCreateCommand? DesktopExcelWorkbookCreate = null,
    DesktopExcelCellCommand? DesktopExcelCellEdit = null,
    DesktopTextDocumentEditCommand? DesktopDocumentEdit = null,
    DesktopFileOpenCommand? DesktopFileOpen = null,
    DesktopNamedFileMoveCommand? DesktopNamedFileMove = null,
    DeterministicDesktopActionCommand? DesktopAction = null)
{
    public bool IsBrowser => Kind is
        DeterministicLocalTaskKind.BrowserTab or
        DeterministicLocalTaskKind.BrowserNavigation or
        DeterministicLocalTaskKind.BrowserElementAction or
        DeterministicLocalTaskKind.BrowserArticleToWord;
}

internal static class DeterministicLocalTaskIntentDetector
{
    public static bool TryParse(
        string request,
        string? currentWebPlatform,
        out DeterministicLocalTaskIntent intent,
        IInstalledApplicationCatalog? installedApplications = null,
        bool allowImplicitTextDocumentTarget = false,
        bool foregroundBrowser = false)
    {
        if (BrowserArticleToWordIntentDetector.TryParseExplicitCommand(request, out var articleToWord))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.BrowserArticleToWord,
                BrowserArticleToWordIntentDetector.GetCanonicalKey(),
                BrowserArticleToWord: articleToWord);
            return true;
        }
        if (BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var browserTab) &&
            BrowserTabCommandIntentDetector.IsContextuallyAllowed(request, browserTab, currentWebPlatform, foregroundBrowser))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.BrowserTab,
                BrowserTabCommandIntentDetector.GetCanonicalKey(browserTab),
                BrowserTab: browserTab);
            return true;
        }
        if (BrowserNavigationIntentDetector.TryParseExplicitCommand(
                request,
                currentWebPlatform,
                out var browserNavigation))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.BrowserNavigation,
                $"browser:{browserNavigation.ExpectedHost}:{browserNavigation.ExpectedPathPrefix}:{browserNavigation.Url}",
                BrowserNavigation: browserNavigation);
            return true;
        }
        // Explicit Windows applications and desktop files take precedence over
        // a previously bound web platform. Otherwise "打开记事本" can be
        // mistaken for clicking a page element merely because the last task
        // happened in Chrome.
        if (DesktopExcelCellIntentDetector.TryParseExplicitCommand(request, out var excelEdit))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopExcelCellEdit,
                DesktopExcelCellIntentDetector.GetCanonicalKey(excelEdit),
                DesktopExcelCellEdit: excelEdit);
            return true;
        }
        // Resolve creation before editing. File names can legitimately contain
        // edit verbs such as “删除”, “清空” or “保存”; those words belong to the
        // requested name and must not capture the whole utterance as an edit.
        if (DesktopFolderCreationIntentDetector.TryParseExplicitCommand(request, out var folderCreate))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopFolderCreate,
                DesktopFolderCreationIntentDetector.GetCanonicalKey(folderCreate),
                DesktopFolderCreate: folderCreate);
            return true;
        }
        if (DesktopWordDocumentCreationIntentDetector.TryParseExplicitCommand(request, out var wordCreate))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopWordDocumentCreate,
                DesktopWordDocumentCreationIntentDetector.GetCanonicalKey(wordCreate),
                DesktopWordDocumentCreate: wordCreate);
            return true;
        }
        if (DesktopExcelWorkbookCreationIntentDetector.TryParseExplicitCommand(request, out var excelCreate))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopExcelWorkbookCreate,
                DesktopExcelWorkbookCreationIntentDetector.GetCanonicalKey(excelCreate),
                DesktopExcelWorkbookCreate: excelCreate);
            return true;
        }
        if (DesktopDocumentCreationIntentDetector.TryParseExplicitCommand(request, out var documentCreate))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopDocumentCreate,
                DesktopDocumentCreationIntentDetector.GetCanonicalKey(documentCreate),
                DesktopDocumentCreate: documentCreate);
            return true;
        }
        if (DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out var documentEdit))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopDocumentEdit,
                DesktopTextDocumentEditIntentDetector.GetCanonicalKey(documentEdit),
                DesktopDocumentEdit: documentEdit);
            return true;
        }
        if (allowImplicitTextDocumentTarget &&
            DesktopTextDocumentEditIntentDetector.TryParseContextualCommand(request, out var contextualDocumentEdit))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopDocumentEdit,
                DesktopTextDocumentEditIntentDetector.GetCanonicalKey(contextualDocumentEdit),
                DesktopDocumentEdit: contextualDocumentEdit);
            return true;
        }
        if (DesktopNamedFileMoveIntentDetector.TryParseExplicitCommand(request, out var namedFileMove))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopNamedFileMove,
                DesktopNamedFileMoveIntentDetector.GetCanonicalKey(namedFileMove),
                DesktopNamedFileMove: namedFileMove);
            return true;
        }
        // A real, uniquely resolved installed application wins over the broad
        // “打开桌面的……” file grammar. Concrete file extensions and explicit
        // file/document/folder nouns remain under the desktop-file route.
        if (installedApplications is not null &&
            WindowsControlIntentDetector.TryParseInstalledApplicationCommand(
                request,
                installedApplications,
                out var installedApplication))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.WindowsControl,
                $"windows:{installedApplication.Action}:{installedApplication.ApplicationId}",
                WindowsControl: installedApplication);
            return true;
        }
        // A named desktop file can contain application words such as Word,
        // Excel, WPS or PPT. The concrete file target must win over launching
        // the corresponding application shell.
        if (DesktopFileOpenIntentDetector.TryParseExplicitCommand(request, out var desktopFile))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopFileOpen,
                DesktopFileOpenIntentDetector.GetCanonicalKey(desktopFile),
                DesktopFileOpen: desktopFile);
            return true;
        }
        if (WindowsControlIntentDetector.TryParseExplicitCommand(request, installedApplications, out var windows))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.WindowsControl,
                $"windows:{windows.Action}:{windows.ApplicationId}:{windows.Value}:{windows.DurationSeconds}",
                WindowsControl: windows);
            return true;
        }
        if (BrowserElementActionIntentDetector.TryParseExplicitCommand(request, out var browserElement) &&
            (!string.IsNullOrWhiteSpace(currentWebPlatform) ||
             (BrowserElementActionIntentDetector.HasExplicitBrowserScope(request) &&
              !BrowserElementActionIntentDetector.RequiresBoundBrowserContext(request))))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.BrowserElementAction,
                $"browser-action:{currentWebPlatform}:{browserElement.Target}:{browserElement.Ordinal}",
                BrowserElementAction: browserElement);
            return true;
        }
        if (DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out var desktopAction))
        {
            intent = new DeterministicLocalTaskIntent(
                DeterministicLocalTaskKind.DesktopAction,
                DeterministicDesktopActionIntentDetector.GetCanonicalKey(desktopAction),
                DesktopAction: desktopAction);
            return true;
        }
        intent = null!;
        return false;
    }

    public static bool TryGetCanonicalKey(
        string request,
        out string key,
        string? currentWebPlatform = null,
        IInstalledApplicationCatalog? installedApplications = null,
        bool allowImplicitTextDocumentTarget = false,
        bool foregroundBrowser = false)
    {
        if (TryParse(
                request,
                currentWebPlatform,
                out var intent,
                installedApplications,
                allowImplicitTextDocumentTarget,
                foregroundBrowser))
        {
            key = intent.CanonicalKey;
            return true;
        }
        key = string.Empty;
        return false;
    }
}
