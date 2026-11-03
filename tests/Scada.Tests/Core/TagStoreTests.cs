using Scada.Core.Demo;
using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class TagStoreTests
{
    [Fact]
    public void Initialize_creates_bad_values_for_every_tag()
    {
        var store = new TagStore();
        store.Initialize(DemoPlant.CreateTagDefinitions());

        var snapshot = store.Snapshot();

        Assert.Equal(8, snapshot.Count);
        Assert.All(snapshot.Values, v => Assert.Equal(TagQuality.Bad, v.Quality));
    }

    [Fact]
    public void Update_replaces_value_and_reports_history()
    {
        var store = new TagStore(historyCapacity: 3);
        store.Initialize(new[]
        {
            new TagDefinition { Id = "level", Name = "液位", Address = "HR:0", DataType = TagDataType.UInt16 }
        });

        store.Update(new TagValue("level", 42, 420, TagQuality.Good, DateTimeOffset.UtcNow));
        store.Update(new TagValue("level", 43, 430, TagQuality.Good, DateTimeOffset.UtcNow));

        Assert.Equal(43, store.Get("level").Value);
        Assert.Equal(2, store.GetHistory("level").Count);

        store.Update(new TagValue("level", 44, 440, TagQuality.Good, DateTimeOffset.UtcNow));
        store.Update(new TagValue("level", 45, 450, TagQuality.Good, DateTimeOffset.UtcNow));

        var history = store.GetHistory("level");
        Assert.Equal(3, history.Count);
        Assert.Equal(45, history[^1].Value);
    }

    [Fact]
    public void Unknown_tag_returns_bad_quality_instead_of_throwing()
    {
        var store = new TagStore();

        var value = store.Get("not-exists");

        Assert.Equal(TagQuality.Bad, value.Quality);
        Assert.Contains("未知", value.Error);
    }
}
