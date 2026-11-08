using LockIn.Core.Lcu;

namespace LockIn.Core.Tests.Lcu;

public class LcuLockfileTests
{
    [Fact]
    public void Parses_a_valid_lockfile()
    {
        Assert.True(LcuLockfile.TryParse("LeagueClient:22540:54321:s3cr3t-T0ken_x:https\r\n", out var lockfile));

        Assert.Equal("LeagueClient", lockfile.ProcessName);
        Assert.Equal(22540, lockfile.ProcessId);
        Assert.Equal(54321, lockfile.Port);
        Assert.Equal("s3cr3t-T0ken_x", lockfile.Password);
        Assert.Equal(new Uri("https://127.0.0.1:54321/"), lockfile.BaseAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LeagueClient:22540:54321:https")]
    [InlineData("LeagueClient:pid:54321:pw:https")]
    [InlineData("LeagueClient:22540:70000:pw:https")]
    [InlineData("LeagueClient:22540:54321::https")]
    [InlineData("LeagueClient:22540:54321:pw:ftp")]
    public void Rejects_malformed_content(string? content)
    {
        Assert.False(LcuLockfile.TryParse(content, out _));
    }

    [Fact]
    public void ToString_does_not_leak_the_password()
    {
        Assert.True(LcuLockfile.TryParse("LeagueClient:1:2:hunter2:https", out var lockfile));

        Assert.DoesNotContain("hunter2", lockfile.ToString());
    }
}
