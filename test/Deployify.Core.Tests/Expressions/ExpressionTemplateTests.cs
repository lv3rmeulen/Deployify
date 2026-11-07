using System.Linq;
using Deployify.Core.Expressions;
using Shouldly;
using Xunit;

namespace Deployify.Core.Tests.Expressions;

public sealed class ExpressionTemplateTests
{
    [Fact]
    public void Parse_PlainText_IsOneLiteral()
    {
        var t = ExpressionTemplate.Parse("Production");

        t.HasReferences.ShouldBeFalse();
        t.Segments.ShouldHaveSingleItem().ShouldBe(new LiteralSegment("Production"));
    }

    [Fact]
    public void Parse_Empty_HasNoSegments()
    {
        ExpressionTemplate.Parse("").Segments.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_SingleReference_SplitsResourceAndPath()
    {
        var t = ExpressionTemplate.Parse("{pg.bindings.tcp.host}");

        var r = t.Segments.ShouldHaveSingleItem().ShouldBeOfType<ReferenceSegment>();
        r.Resource.ShouldBe("pg");
        r.Path.ShouldBe(new[] { "bindings", "tcp", "host" });
        r.Property.ShouldBe("bindings");
        r.Placeholder.ShouldBe("{pg.bindings.tcp.host}");
    }

    [Fact]
    public void Parse_MixedTemplate_KeepsOrder()
    {
        var t = ExpressionTemplate.Parse("Host={pg.bindings.tcp.host};Password={pg-password.value}");

        t.Segments.Count.ShouldBe(4);
        t.Segments[0].ShouldBe(new LiteralSegment("Host="));
        t.Segments[1].ShouldBe(new ReferenceSegment("pg", ["bindings", "tcp", "host"]));
        t.Segments[2].ShouldBe(new LiteralSegment(";Password="));
        t.Segments[3].ShouldBe(new ReferenceSegment("pg-password", ["value"]));
        t.References.Select(r => r.Resource).ShouldBe(new[] { "pg", "pg-password" });
    }

    [Fact]
    public void Parse_AdjacentReferences()
    {
        var t = ExpressionTemplate.Parse("{a.value}{b.value}");

        t.Segments.Count.ShouldBe(2);
        t.Segments.ShouldAllBe(s => s is ReferenceSegment);
    }

    [Theory]
    [InlineData("{\"retries\":3}")]
    [InlineData("{{ .Values.image }}")]
    [InlineData("{ not a ref }")]
    [InlineData("{noproperty}")]
    [InlineData("{trailing.}")]
    [InlineData("unclosed {pg.bindings")]
    [InlineData("}{")]
    public void Parse_BracesThatAreNotReferences_StayLiteral(string input)
    {
        var t = ExpressionTemplate.Parse(input);

        t.HasReferences.ShouldBeFalse();
        string.Concat(t.Segments.Cast<LiteralSegment>().Select(s => s.Text)).ShouldBe(input);
    }

    [Fact]
    public void Parse_ReferenceInsideOtherBraces_IsFound()
    {
        var t = ExpressionTemplate.Parse("{\"url\":\"{api.bindings.http.url}\"}");

        t.References.ShouldHaveSingleItem().Placeholder.ShouldBe("{api.bindings.http.url}");
    }

    [Fact]
    public void ReferenceSegment_EqualityIsByValue()
    {
        var a = new ReferenceSegment("pg", ["bindings", "tcp", "port"]);
        var b = new ReferenceSegment("pg", new[] { "bindings", "tcp", "port" }.ToList());

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    // ── Escaping ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DoubledReference_IsLiteral()
    {
        var t = ExpressionTemplate.Parse("{{api.bindings.http.url}}");

        t.HasReferences.ShouldBeFalse();
        t.Segments.ShouldHaveSingleItem().ShouldBe(new LiteralSegment("{api.bindings.http.url}"));
    }

    [Fact]
    public void Parse_EscapedAndRealReferences_MixAndMergeLiterals()
    {
        var t = ExpressionTemplate.Parse("literal {{api.value}} real {api.value}");

        t.Segments.Count.ShouldBe(2);
        t.Segments[0].ShouldBe(new LiteralSegment("literal {api.value} real "));
        t.Segments[1].ShouldBe(new ReferenceSegment("api", new[] { "value" }));
    }

    [Theory]
    [InlineData("{{ .Values.image }}")]
    [InlineData("{{.Values.image}}")]
    [InlineData("{\"a\":{\"b\":1}}")]
    [InlineData("{{range .Items}}{{.}}{{end}}")]
    public void Parse_DoubleBracesThatAreNotReferences_AreUnchanged(string input)
    {
        var t = ExpressionTemplate.Parse(input);

        t.HasReferences.ShouldBeFalse();
        t.Segments.ShouldHaveSingleItem().ShouldBe(new LiteralSegment(input));
    }

    [Fact]
    public void Parse_UnbalancedDoubleBrace_FallsBackToReference()
    {
        var t = ExpressionTemplate.Parse("{{api.value}");

        t.Segments.Count.ShouldBe(2);
        t.Segments[0].ShouldBe(new LiteralSegment("{"));
        t.Segments[1].ShouldBe(new ReferenceSegment("api", new[] { "value" }));
    }

    [Fact]
    public void Parse_TripleBraces_EscapeInsideLiteralBraces()
    {
        // "{" + escaped "{{a.b}}" + "}" → literal "{{a.b}}"
        var t = ExpressionTemplate.Parse("{{{a.b}}}");

        t.HasReferences.ShouldBeFalse();
        t.Segments.ShouldHaveSingleItem().ShouldBe(new LiteralSegment("{{a.b}}"));
    }
}
