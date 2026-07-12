using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;

namespace NzbWebDAV.Tests.Api;

public class AddFileSettingsTests
{
    [Theory]
    [InlineData("movies")]
    [InlineData("tv-4k")]
    [InlineData("manual_upload")]
    public void CategoryAcceptsSafeSingleSegment(string category)
    {
        AddFileController.ValidateCategory(category);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("nested/category")]
    [InlineData("nested\\category")]
    public void CategoryRejectsUnsafePathValues(string category)
    {
        Assert.Throws<BadHttpRequestException>(() => AddFileController.ValidateCategory(category));
    }
}
