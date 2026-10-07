using WinRebuilder.Windows;
using Xunit;

namespace WinRebuilder.Windows.Tests;

public sealed class WindowsOnlyTests
{
    [Fact]
    public void ProgramDataRootIsStable()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.EndsWith("WinRebuilder", WindowsPaths.Root, StringComparison.Ordinal);
    }
}
