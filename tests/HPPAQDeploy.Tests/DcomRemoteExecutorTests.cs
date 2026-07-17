using HPPAQDeploy.Infrastructure.Remote;

namespace HPPAQDeploy.Tests;

public sealed class DcomRemoteExecutorTests
{
    [Fact]
    public void BuildDirectFallbackCommand_PreservesQuotedPaths()
    {
        const string commandLine =
            "\"C:\\Temp\\HPIA\\HPImageAssistant.exe\" /ReportFolder:\"C:\\Temp\\HPIA Reports\"";
        const string exitCodeFile = "C:\\Temp\\HPIA\\exitcode.txt";

        var actual = DcomRemoteExecutor.BuildDirectFallbackCommand(commandLine, exitCodeFile);

        Assert.Equal(
            "cmd.exe /d /s /c \"\"C:\\Temp\\HPIA\\HPImageAssistant.exe\" " +
            "/ReportFolder:\"C:\\Temp\\HPIA Reports\" & echo %ERRORLEVEL% > " +
            "\"C:\\Temp\\HPIA\\exitcode.txt\"\"",
            actual);
    }

    [Fact]
    public void BuildWindowsCommandLine_QuotesWhitespaceAndEmbeddedQuotes()
    {
        var actual = DcomRemoteExecutor.BuildWindowsCommandLine(
            "tool.exe",
            "plain",
            "two words",
            "pa&ss\"word");

        Assert.Equal("tool.exe plain \"two words\" \"pa&ss\\\"word\"", actual);
    }

    [Fact]
    public void BuildNetUseBatchLine_DoesNotExposeCredentialToCmdExpansion()
    {
        const string password = "p%a&ss!\"word^";

        var actual = DcomRemoteExecutor.BuildNetUseBatchLine(
            @"\\server\repository",
            @"DOMAIN\operator",
            password);

        Assert.Contains("-EncodedCommand", actual, StringComparison.Ordinal);
        Assert.DoesNotContain(password, actual, StringComparison.Ordinal);
    }
}
