using Atoll.Api.Services.Git;
using Xunit;

namespace Atoll.Api.Tests.Packages.Git;

public class GitSmartHttpTests
{
    [Fact]
    public void IsSupportedService_accepts_only_git_upload_pack()
    {
        Assert.True(GitSmartHttp.IsSupportedService(GitSmartHttp.UploadPackService));
    }

    [Fact]
    public void IsSupportedService_rejects_the_push_service()
    {
        Assert.False(GitSmartHttp.IsSupportedService("git-receive-pack"),
            "serving receive-pack would expose an unauthenticated push target");
    }

    [Fact]
    public void IsSupportedService_rejects_missing_and_mismatched_casing()
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
