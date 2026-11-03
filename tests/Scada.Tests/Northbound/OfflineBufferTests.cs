using Scada.Northbound;

namespace Scada.Tests.Northbound;

public class OfflineBufferTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"scada-outbox-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Enqueue_and_TakeAll_roundtrip()
    {
        var path = TempPath();
        try
        {
            var buffer = new OfflineBuffer(path, 100);
            buffer.Enqueue("topic/a", "{\"v\":1}");
            buffer.Enqueue("topic/b", "{\"v\":2}");

            Assert.Equal(2, buffer.Count);
            var items = buffer.TakeAll();
            Assert.Equal("topic/a", items[0].Topic);
            Assert.Equal("{\"v\":2}", items[1].Payload);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Clear_removes_pending_messages()
    {
        var path = TempPath();
        try
        {
            var buffer = new OfflineBuffer(path, 100);
            buffer.Enqueue("t", "p");
            buffer.Clear();

            Assert.Equal(0, buffer.Count);
            Assert.Empty(buffer.TakeAll());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Max_items_keeps_latest_messages()
    {
        var path = TempPath();
        try
        {
            var buffer = new OfflineBuffer(path, 10);
            for (var i = 0; i < 20; i++)
                buffer.Enqueue("t", i.ToString());

            var items = buffer.TakeAll();
            Assert.Equal(10, items.Count);
            Assert.Equal("10", items[0].Payload);
            Assert.Equal("19", items[^1].Payload);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_line_is_skipped()
    {
        var path = TempPath();
        try
        {
            var buffer = new OfflineBuffer(path, 100);
            buffer.Enqueue("ok", "1");
            File.AppendAllText(path, "{not-json}\n");
            buffer.Enqueue("ok2", "2");

            Assert.Equal(2, buffer.TakeAll().Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
