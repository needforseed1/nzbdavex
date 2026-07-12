using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class WardenSettingsTests
{
    [Fact]
    public void DisabledRemoteSourceIsNeverDue()
    {
        var source = new WardenSourceInfo
        {
            Enabled = false,
            Kind = "remote",
            Url = "https://example.invalid/warden.ndjson.gz",
            RefreshHours = 1,
            LastChecked = 0,
        };

        Assert.False(WardenRemoteSourceService.IsDue(source, now: 10_000));
    }

    [Fact]
    public void BackupHeaderAndBackbonesAreCanonical()
    {
        Assert.Equal(
            WardenStore.BuildExportHeader(deterministic: true, updatedAt: 1),
            WardenStore.BuildExportHeader(deterministic: true, updatedAt: 999));
        Assert.Equal(
            "{\"warden\":1,\"updated\":123}",
            WardenStore.BuildExportHeader(deterministic: false, updatedAt: 123));
        Assert.Equal(
            ["a.example", "b.example"],
            WardenStore.SplitBackbones("b.example,a.example,b.example"));
    }
}
