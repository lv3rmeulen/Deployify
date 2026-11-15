using System;
using Deployify.Target.Nomad.Api;
using Shouldly;
using Xunit;

namespace Deployify.Target.Nomad.Tests;

public sealed class NomadDurationTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("10s", 10_000_000_000L)]
    [InlineData("1500ms", 1_500_000_000L)]
    [InlineData("1m30s", 90_000_000_000L)]
    [InlineData("2h", 7_200_000_000_000L)]
    [InlineData("1.5s", 1_500_000_000L)]
    [InlineData("250us", 250_000L)]
    [InlineData("250µs", 250_000L)]
    [InlineData("42ns", 42L)]
    [InlineData(" 30s ", 30_000_000_000L)]
    [InlineData("-5s", -5_000_000_000L)]
    public void Parse_ReadsGoDurations(string text, long nanoseconds) => NomadDuration.Parse(text).ShouldBe(nanoseconds);

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("s")]
    [InlineData("10 s")]
    [InlineData("10sec")]
    [InlineData("1d")]
    public void Parse_RejectsWhatGoWouldReject(string text)
        => Should.Throw<FormatException>(() => NomadDuration.Parse(text)).Message.ShouldContain("not a duration");
}
