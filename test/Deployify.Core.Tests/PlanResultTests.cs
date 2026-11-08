using Deployify.Core.Models;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests;

public sealed class PlanResultTests
{
    [Fact]
    public void FromDiff_SortsEachKindIntoItsList()
    {
        var plan = PlanResult.FromDiff(
        [
            new DiffEntry { ServiceName = "new", Kind = DiffKind.Added },
            new DiffEntry { ServiceName = "changed", Kind = DiffKind.Changed },
            new DiffEntry { ServiceName = "gone", Kind = DiffKind.Removed },
            new DiffEntry { ServiceName = "same", Kind = DiffKind.Unchanged },
            new DiffEntry { ServiceName = "unsure", Kind = DiffKind.Unknown, Reason = "kubectl diff failed" }
        ]);

        plan.ToCreate.ShouldBe(["new"]);
        plan.ToUpdate.ShouldBe(["changed"]);
        plan.ToDelete.ShouldBe(["gone"]);
        plan.Unchanged.ShouldBe(["same"]);
        plan.Unknown.ShouldBe([new PlanUnknown("unsure", "kubectl diff failed")]);
    }

    [Fact]
    public void IsUpToDate_OnlyWhenNothingChangesAndNothingIsUnknown()
    {
        new PlanResult { Unchanged = ["api"] }.IsUpToDate.ShouldBeTrue();
        new PlanResult { Unknown = [new PlanUnknown("api", "x")] }.IsUpToDate.ShouldBeFalse();
        new PlanResult { ToDelete = ["old"] }.IsUpToDate.ShouldBeFalse();
    }

    [Fact]
    public void FromDiff_UnknownWithoutReason_GetsADefault()
        => PlanResult.FromDiff([new DiffEntry { ServiceName = "api", Kind = DiffKind.Unknown }])
            .Unknown[0].Reason.ShouldNotBeNullOrWhiteSpace();
}
