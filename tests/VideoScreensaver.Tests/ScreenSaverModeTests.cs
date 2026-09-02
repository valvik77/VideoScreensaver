using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public class ScreenSaverModeTests
{
    [TestMethod]
    public void Parse_WithoutArguments_UsesConfigurationMode()
    {
        var mode = ScreenSaverMode.Parse([]);

        Assert.AreEqual(ScreenSaverModeKind.Configure, mode.Kind);
    }

    [TestMethod]
    [DataRow("/s")]
    [DataRow("-S")]
    public void Parse_RunArgument_UsesRunMode(string argument)
    {
        var mode = ScreenSaverMode.Parse([argument]);

        Assert.AreEqual(ScreenSaverModeKind.Run, mode.Kind);
    }

    [TestMethod]
    [DataRow("/p:12345")]
    [DataRow("-p:12345")]
    public void Parse_InlinePreviewHandle_UsesPreviewMode(string argument)
    {
        var mode = ScreenSaverMode.Parse([argument]);

        Assert.AreEqual(ScreenSaverModeKind.Preview, mode.Kind);
        Assert.AreEqual((nint)12345, mode.PreviewHandle);
    }

    [TestMethod]
    public void Parse_SeparatedPreviewHandle_UsesPreviewMode()
    {
        var mode = ScreenSaverMode.Parse(["/p", "67890"]);

        Assert.AreEqual(ScreenSaverModeKind.Preview, mode.Kind);
        Assert.AreEqual((nint)67890, mode.PreviewHandle);
    }

    [TestMethod]
    public void Parse_UnknownArgument_UsesConfigurationMode()
    {
        var mode = ScreenSaverMode.Parse(["/unknown"]);

        Assert.AreEqual(ScreenSaverModeKind.Configure, mode.Kind);
    }
}
