﻿using JetBrains.ReSharper.FeaturesTestFramework.Intentions;
using JetBrains.ReSharper.Plugins.Unity.Feature.Services.QuickFixes;
using NUnit.Framework;

namespace JetBrains.ReSharper.Plugins.Unity.Tests.Intentions.QuickFixes
{
    [TestUnity]
    public class ConvertCoalesingToConditionalQuickFixAvailabilityTest : QuickFixAvailabilityTestBase
    {
        protected override string RelativeTestDataPath => @"Intentions\QuickFixes\ConvertCoalescingToConditional\Availability";

        [Test] public void Test01() { DoNamedTest(); }
        [Test] public void Test02() { DoNamedTest(); }
        [Test] public void Test03() { DoNamedTest(); }
        [Test] public void Test04() { DoNamedTest(); }
    }

    [TestUnity]
    public class ConvertCoalesingToConditionalQuickFixTests : QuickFixTestBase<ConvertCoalesingToConditionalQuickFix>
    {
        protected override string RelativeTestDataPath => @"Intentions\QuickFixes\ConvertCoalescingToConditional";
        protected override bool AllowHighlightingOverlap => true;

        [Test] public void Test01() { DoNamedTest(); }
        [Test] public void Test02() { DoNamedTest(); }
        [Test] public void Test03() { DoNamedTest(); }
        [Test] public void Test04() { DoNamedTest(); }
    }
}
