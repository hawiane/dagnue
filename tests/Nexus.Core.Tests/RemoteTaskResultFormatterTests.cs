using Nexus.Core.Codex;

namespace Nexus.Core.Tests;

public sealed class RemoteTaskResultFormatterTests
{
    [Fact]
    public void FileResult_HidesRuntimePathAndCitation_ButKeepsObservation()
    {
        const string response = """
            I'll retry the Excel export first. If the workbook runtime is still unavailable, I'll use the CSV fallback and place it under results. Created the report: ::codex-file-citation{path="C:\Users\Admin\AppData\Local\NEXUS\remote-tasks\123\results\attendance.csv" purpose="output" artifact_kind="workbook"}

            It contains all 31 dates: 14 days with biometric logs, including 12 complete records and two missing OUT punches.
            """;

        var result = RemoteTaskResultFormatter.Format(response, hasResultFiles: true);

        Assert.Equal("The requested file is ready. It contains all 31 dates: 14 days with biometric logs, including 12 complete records and two missing OUT punches.", result);
        Assert.DoesNotContain("C:\\", result);
        Assert.DoesNotContain("codex-file-citation", result);
        Assert.DoesNotContain("runtime", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileResult_UsesProfessionalDefault_WhenOnlyInternalNarrationRemains()
    {
        var result = RemoteTaskResultFormatter.Format("I will upload it under ./results.", hasResultFiles: true);

        Assert.Equal("The requested file is ready.", result);
    }

    [Fact]
    public void TextResult_IsLimitedToTwoSentences()
    {
        var result = RemoteTaskResultFormatter.Format("42 employees found. Two records need review. Extra internal detail.", hasResultFiles: false);

        Assert.Equal("42 employees found. Two records need review.", result);
    }

    [Fact]
    public void Error_HidesLocalWindowsPath()
    {
        var result = RemoteTaskResultFormatter.FormatError("Access to C:\\Users\\Admin\\secret.txt is denied.");

        Assert.DoesNotContain("Users", result);
        Assert.DoesNotContain("C:\\", result);
        Assert.Contains("is denied", result);
    }
}
