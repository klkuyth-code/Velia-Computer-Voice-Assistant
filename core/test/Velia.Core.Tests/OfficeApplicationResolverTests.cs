using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class OfficeApplicationResolverTests
{
    [Theory]
    [InlineData("WPS.Docx.6", "Wps")]
    [InlineData("ET.Xlsx.6", "Wps")]
    [InlineData("KWPP.Application", "Wps")]
    [InlineData("Applications\\et.exe", "Wps")]
    [InlineData("Word.Document.12", "Microsoft")]
    [InlineData("Excel.Sheet.12", "Microsoft")]
    [InlineData("PowerPoint.Show.12", "Microsoft")]
    [InlineData("Applications\\WINWORD.EXE", "Microsoft")]
    public void ClassifiesKnownOfficeFileAssociations(string progId, string expected)
    {
        Assert.Equal(expected, OfficeApplicationResolver.ClassifyProgId(progId)?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown.Document")]
    public void DoesNotGuessUnknownOfficeFileAssociations(string? progId)
    {
        Assert.Null(OfficeApplicationResolver.ClassifyProgId(progId));
    }

    [Fact]
    public void GenericWordUsesTheUsersWpsAssociationFirst()
    {
        Assert.True(OfficeApplicationResolver.TryGetFamily(WindowsApplicationIds.Word, out var family));

        var candidates = OfficeApplicationResolver.OrderConcreteApplicationIds(family, "WPS.Docx.6");

        Assert.Equal(
            [WindowsApplicationIds.WpsWriter, WindowsApplicationIds.MicrosoftWord],
            candidates);
    }

    [Fact]
    public void GenericExcelUsesTheUsersMicrosoftAssociationFirst()
    {
        Assert.True(OfficeApplicationResolver.TryGetFamily(WindowsApplicationIds.Excel, out var family));

        var candidates = OfficeApplicationResolver.OrderConcreteApplicationIds(family, "Excel.Sheet.12");

        Assert.Equal(
            [WindowsApplicationIds.MicrosoftExcel, WindowsApplicationIds.WpsSpreadsheets],
            candidates);
    }

    [Theory]
    [InlineData(WindowsApplicationIds.Word, WindowsApplicationIds.WpsWriter, WindowsApplicationIds.MicrosoftWord)]
    [InlineData(WindowsApplicationIds.Excel, WindowsApplicationIds.WpsSpreadsheets, WindowsApplicationIds.MicrosoftExcel)]
    [InlineData(WindowsApplicationIds.PowerPoint, WindowsApplicationIds.WpsPresentation, WindowsApplicationIds.MicrosoftPowerPoint)]
    public void UnknownAssociationUsesTheEditableWpsFallbackBeforeMicrosoft(
        string genericId,
        string expectedFirst,
        string expectedSecond)
    {
        Assert.True(OfficeApplicationResolver.TryGetFamily(genericId, out var family));

        var candidates = OfficeApplicationResolver.OrderConcreteApplicationIds(family, null);

        Assert.Equal([expectedFirst, expectedSecond], candidates);
    }

}
