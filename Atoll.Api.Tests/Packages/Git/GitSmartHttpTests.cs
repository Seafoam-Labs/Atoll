using Atoll.Api.Services.Git;
using Xunit;

namespace Atoll.Api.Tests.Packages.Git;

public class GitSmartHttpTests
{
    [Fact]
    public void IsSupportedService_UploadPackService_IsAccepted()
    {
        Assert.True(GitSmartHttp.IsSupportedService(GitSmartHttp.UploadPackService));
    }

    [Fact]
    public void IsSupportedService_ReceivePackService_IsRejected()
    {
        Assert.False(GitSmartHttp.IsSupportedService("git-receive-pack"),
            "serving receive-pack would expose an unauthenticated push target");
    }

    [Fact]
    public void IsSupportedService_NullEmptyOrWrongCasing_IsRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.False(GitSmartHttp.IsSupportedService(null));
            Assert.False(GitSmartHttp.IsSupportedService(""));
            Assert.False(GitSmartHttp.IsSupportedService("GIT-UPLOAD-PACK"),
                "service names are compared ordinally");
        });
    }
}
