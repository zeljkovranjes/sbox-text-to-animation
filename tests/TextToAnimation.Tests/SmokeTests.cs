using System.Collections.Generic;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Rig;
using Xunit;

namespace TextToAnimation.Tests;

public class SmokeTests
{
    [Fact]
    public void SkeletonBuilds()
    {
        var s = Skeleton.Create(new List<BoneDefinition> { new("root", null, XForm.Identity), new("child", "root", XForm.Identity) });
        Assert.Equal(2, s.Count);
    }
}
