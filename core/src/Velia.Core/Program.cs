using System.Text;
using Velia.Core;
using Velia.Core.Conversations;
using Velia.Core.Providers;
using Velia.Core.Providers.Chat;
using Velia.Core.Providers.Asr;
using Velia.Core.Providers.Vision;
using Velia.Core.Providers.Live;
using Velia.Core.Documents;
using Velia.Core.DesktopContext;
using Velia.Core.DesktopVisual;
using Velia.Core.Input;
using Velia.Core.Rpc;
using Velia.Core.Security;
using Velia.Core.WindowsControl;
using Velia.Core.Web;
using Velia.Protocol;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (!OperatingSystem.IsWindows())
{
    await Console.Error.WriteLineAsync("Velia Core v1 supports Windows only.");
    return 2;
}

var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var storageRoot = Path.Combine(appData, "Velia");
#if DEBUG
var testStorageRoot = Environment.GetEnvironmentVariable("VELIA_TEST_DATA_DIR");
if (!string.IsNullOrWhiteSpace(testStorageRoot))
{
    storageRoot = Path.GetFullPath(testStorageRoot);
}
#endif
var secrets = new DpapiSecretStore(Path.Combine(storageRoot, "secrets"));
using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(90),
};
var chatProviders = new ChatProviderFactory(httpClient, secrets);
var providerConfiguration = new ProviderConfigurationService(secrets, chatProviders);
var firecrawl = new FirecrawlService(httpClient, secrets);
var installedApplications = new WindowsInstalledApplicationCatalog();
// Build the bounded local app index during Core startup instead of charging
// its one-time registry/shortcut scan to the user's first spoken sentence.
installedApplications.WarmUp();
var asrProvider = new VolcengineFlashAsrProvider(httpClient, () => secrets.Load(ProviderIds.Asr));
var asr = new AsrService(
    secrets,
    asrProvider,
    new ContextualAsrPostProcessor(installedApplications),
    new WindowsAsrRecognitionContextProvider(installedApplications));
var visionProvider = new GeminiVisionProvider(httpClient, () => secrets.Load(ProviderIds.Vision));
var vision = new VisionService(secrets, visionProvider);
var documents = new DocumentService(secrets, chatProviders);
using var messageWriter = new LineMessageWriter(Console.Out);
var voiceShortcutSettings = new VoiceShortcutSettings(Path.Combine(storageRoot, "settings.json"));
using var shortcutSuppressor = new WindowsShortcutKeySuppressor(
    () => voiceShortcutSettings.Current.Id == VoiceShortcutIds.CtrlShiftSpace);
shortcutSuppressor.Start();
using var pushToTalk = new WindowsPushToTalkMonitor(
    messageWriter,
    voiceShortcutSettings,
    shortcutSuppressor.ReadShortcutEdge,
    shortcutSuppressor.ReadLongConversationEdge);
pushToTalk.Start();
using var timers = new WindowsTimerController(messageWriter, new WindowsTimerAlert());
var windowsControl = new WindowsControlService(
    new WindowsApplicationController(installedApplications),
    new WindowsVolumeController(),
    timers);
var desktopFiles = new DesktopFileOpenService();
var desktopFolders = new DesktopFolderWorkflowService(desktopFiles);
var desktopNamedFileMoves = new DesktopNamedFileMoveService();
var desktopVisual = new WindowsDesktopAutomationService();
var desktopDocuments = new DesktopDocumentWorkflowService(desktopFiles);
var desktopTextDocuments = new DesktopTextDocumentWorkflowService(desktopVisual);
using var wordAutomation = new WindowsWordAutomationService();
var desktopWordDocuments = new DesktopWordDocumentWorkflowService(wordAutomation, desktopFiles);
using var excelAutomation = new WindowsExcelAutomationService();
var desktopExcelWorkbooks = new DesktopExcelWorkbookWorkflowService(excelAutomation, desktopFiles);
var conversationHistory = new DpapiConversationHistoryStore(Path.Combine(storageRoot, "history"));
using var conversations = new ConversationService(
    chatProviders,
    providerConfiguration,
    messageWriter,
    windowsControlService: windowsControl,
    desktopFileOpenService: desktopFiles,
    firecrawlService: firecrawl,
    conversationHistoryStore: conversationHistory,
    deterministicDesktopActionService: desktopVisual,
    desktopDocumentWorkflowService: desktopDocuments,
    desktopTextDocumentWorkflowService: desktopTextDocuments,
    desktopWordDocumentWorkflowService: desktopWordDocuments,
    desktopExcelWorkbookWorkflowService: desktopExcelWorkbooks,
    desktopNamedFileMoveService: desktopNamedFileMoves,
    installedApplicationCatalog: installedApplications,
    desktopFolderWorkflowService: desktopFolders);
var liveSemanticRouter = new DeepSeekLiveSemanticRouter(httpClient, secrets);
await using var live = new GeminiLiveService(
    secrets,
    new ClientWebSocketFactory(),
    messageWriter,
    conversations,
    liveSemanticRouter,
    new FileLiveTimingSink(Path.Combine(storageRoot, "logs", "live-timing.jsonl")));
var desktopContext = new DesktopContextService(new WindowsDesktopContextBackend());
var dispatcher = new CoreRpcDispatcher(
    secrets,
    providerConfiguration,
    conversations,
    asr,
    vision,
    documents,
    voiceShortcutSettings,
    live,
    desktopContext,
    firecrawl,
    desktopVisual);
var server = new LineRpcServer(Console.In, messageWriter, dispatcher);

await server.RunAsync(CancellationToken.None);
return 0;
