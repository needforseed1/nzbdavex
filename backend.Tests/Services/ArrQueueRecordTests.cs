using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Tests.Services;

public class ArrQueueRecordTests
{
    [Fact]
    public void StatusMessageMatchingIsCaseInsensitive()
    {
        var record = new ArrQueueRecord
        {
            StatusMessages =
            [
                new ArrQueueStatusMessage { Messages = ["No files found are eligible for import"] },
            ],
        };

        Assert.True(record.HasStatusMessage("NO FILES FOUND"));
    }
}
