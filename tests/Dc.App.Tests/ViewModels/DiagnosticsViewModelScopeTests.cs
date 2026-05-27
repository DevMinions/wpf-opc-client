using Dc.App.ViewModels;

namespace Dc.App.Tests.ViewModels;

public class DiagnosticsViewModelScopeTests
{
    [Theory]
    [InlineData(null, "t1", true)]
    [InlineData("t1", "t1", true)]
    [InlineData("t1", "t2", false)]
    public void MatchesScope_FiltersByTaskId(string? scope, string taskId, bool expected)
    {
        Assert.Equal(expected, DiagnosticsViewModel.MatchesScope(scope, taskId));
    }
}
