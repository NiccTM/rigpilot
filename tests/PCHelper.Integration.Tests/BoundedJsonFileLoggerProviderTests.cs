using System.Text.Json;
using Microsoft.Extensions.Logging;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class BoundedJsonFileLoggerProviderTests
{
    [Fact]
    public void WritesStructuredJsonLogRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pchelper-log-test", Guid.NewGuid().ToString("N"));
        try
        {
            using (BoundedJsonFileLoggerProvider provider = new(directory))
            {
                ILogger logger = provider.CreateLogger("PCHelper.Test");
                logger.Log(
                    LogLevel.Information,
                    new EventId(42, "Smoke"),
                    "value=7",
                    null,
                    static (state, _) => state);
            }

            string file = Assert.Single(Directory.GetFiles(directory, "service-*.jsonl"));
            using JsonDocument record = JsonDocument.Parse(Assert.Single(File.ReadAllLines(file)));
            Assert.Equal("Information", record.RootElement.GetProperty("level").GetString());
            Assert.Equal("PCHelper.Test", record.RootElement.GetProperty("category").GetString());
            Assert.Equal(42, record.RootElement.GetProperty("eventId").GetInt32());
            Assert.Contains("value=7", record.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
