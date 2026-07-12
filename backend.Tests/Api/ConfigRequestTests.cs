using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.GetConfig;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Api;

public class ConfigRequestTests
{
    [Fact]
    public void GetConfigMasksWebdavPasswordWithoutMutatingStoredItem()
    {
        var stored = new List<ConfigItem>
        {
            new() { ConfigName = "webdav.pass", ConfigValue = "stored-hash" },
            new() { ConfigName = "rclone.pass", ConfigValue = "stored-plaintext" },
            new() { ConfigName = "webdav.user", ConfigValue = "admin" },
        };

        var responseItems = GetConfigController.MaskSensitiveValues(stored);

        Assert.Equal("", responseItems.Single(x => x.ConfigName == "webdav.pass").ConfigValue);
        Assert.Equal("", responseItems.Single(x => x.ConfigName == "rclone.pass").ConfigValue);
        Assert.Equal("admin", responseItems.Single(x => x.ConfigName == "webdav.user").ConfigValue);
        Assert.Equal("stored-hash", stored[0].ConfigValue);
        Assert.Equal("stored-plaintext", stored[1].ConfigValue);
    }

    [Fact]
    public void BlankWebdavPasswordUpdateIsIgnored()
    {
        var context = FormContext(
            ("webdav.pass", "   "),
            ("rclone.pass", ""),
            ("webdav.user", "new-user"));

        var request = new UpdateConfigRequest(context);

        Assert.DoesNotContain(request.ConfigItems, x => x.ConfigName == "webdav.pass");
        Assert.DoesNotContain(request.ConfigItems, x => x.ConfigName == "rclone.pass");
        Assert.Contains(request.ConfigItems,
            x => x.ConfigName == "webdav.user" && x.ConfigValue == "new-user");
    }

    [Fact]
    public void NonBlankWebdavPasswordUpdateIsHashed()
    {
        var context = FormContext(("webdav.pass", "new-password"));

        var request = new UpdateConfigRequest(context);

        var password = Assert.Single(request.ConfigItems);
        Assert.Equal("webdav.pass", password.ConfigName);
        Assert.NotEqual("new-password", password.ConfigValue);
        Assert.True(PasswordUtil.Verify(password.ConfigValue, "new-password"));
    }

    private static DefaultHttpContext FormContext(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(values.ToDictionary(
            x => x.Key,
            x => new StringValues(x.Value)));
        return context;
    }
}
