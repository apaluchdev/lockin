using System.Text;
using LockIn.Core.Lcu;

namespace LockIn.Core.Tests.Lcu;

public class LcuEventParserTests
{
    [Fact]
    public void Parses_an_update_event()
    {
        var frame = """[8,"OnJsonApiEvent_lol-gameflow_v1_gameflow-phase",{"data":"ChampSelect","eventType":"Update","uri":"/lol-gameflow/v1/gameflow-phase"}]""";

        Assert.True(LcuEventParser.TryParse(Encoding.UTF8.GetBytes(frame), out var e));

        Assert.Equal(LcuEndpoints.GameflowPhase, e!.Uri);
        Assert.Equal("Update", e.EventType);
        Assert.Equal("ChampSelect", e.DataAs<string>());
    }

    [Fact]
    public void Delete_events_have_no_data()
    {
        var frame = """[8,"OnJsonApiEvent_lol-champ-select_v1_session",{"data":null,"eventType":"Delete","uri":"/lol-champ-select/v1/session"}]""";

        Assert.True(LcuEventParser.TryParse(Encoding.UTF8.GetBytes(frame), out var e));

        Assert.True(e!.IsDelete);
        Assert.Null(e.DataAs<object>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[0,\"welcome\",1,\"x\"]")]
    [InlineData("[8,\"OnJsonApiEvent\",\"no payload\"]")]
    public void Ignores_other_frames(string frame)
    {
        Assert.False(LcuEventParser.TryParse(Encoding.UTF8.GetBytes(frame), out _));
    }

    [Fact]
    public void Builds_wamp_event_names()
    {
        Assert.Equal("OnJsonApiEvent_lol-champ-select_v1_session", LcuEndpoints.EventName(LcuEndpoints.ChampSelectSession));
    }
}
