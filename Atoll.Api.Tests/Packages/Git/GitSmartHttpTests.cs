using Atoll.Api.Services.Git;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Git;

public class GitSmartHttpTests
{
    [Test]
    public void IsSupportedService_accepts_only_git_upload_pack()
    {
        Assert.That(GitSmartHttp.IsSupportedService(GitSmartHttp.UploadPackService), Is.True);
    }

    [Test]
    public void IsSupportedService_rejects_the_push_service()
    {
        Assert.That(GitSmartHttp.IsSupportedService("git-receive-pack"), Is.False,
            "serving receive-pack would expose an unauthenticated push target");
    }

    [Test]
    public void IsSupportedService_rejects_missing_and_mismatched_casing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitSmartHttp.IsSupportedService(null), Is.False);
            Assert.That(GitSmartHttp.IsSupportedService(""), Is.False);
            Assert.That(GitSmartHttp.IsSupportedService("GIT-UPLOAD-PACK"), Is.False,
                "service names are compared ordinally");
        });
    }
}
