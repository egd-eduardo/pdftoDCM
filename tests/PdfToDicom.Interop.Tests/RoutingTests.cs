using PdfToDicom.Interop.Routing;
using Xunit;

namespace PdfToDicom.Interop.Tests;

public sealed class RoutingTests
{
    private static Dictionary<string, string> Report(string type = "ORU^R01", string kind = "pdf") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["messageType"] = type,
            ["documentKind"] = kind
        };

    private static RoutingRule Rule(
        string name,
        Dictionary<string, string>? when = null,
        string[]? destinations = null,
        string profile = "example-oru-r01",
        bool stop = false,
        bool enabled = true) =>
        new()
        {
            Name = name,
            Enabled = enabled,
            When = when ?? [],
            Destinations = destinations ?? ["archive"],
            ProfileId = profile,
            StopOnMatch = stop
        };

    [Fact]
    public void ARuleWithNoConditionsMatchesEverything()
    {
        var engine = new RoutingEngine([Rule("catch all")]);

        RoutingDecision decision = Assert.Single(engine.Route(Report()));

        Assert.Equal("archive", decision.DestinationId);
        Assert.Equal("catch all", decision.RuleName);
    }

    [Fact]
    public void EveryConditionHasToHold()
    {
        var engine = new RoutingEngine(
        [
            Rule("reports as pdf", new() { ["messageType"] = "ORU^R01", ["documentKind"] = "pdf" })
        ]);

        Assert.Single(engine.Route(Report()));
        Assert.Empty(engine.Route(Report(kind: "image")));
        Assert.Empty(engine.Route(Report(type: "ADT^A04")));
    }

    [Fact]
    public void AStarMeansAnyValueThatIsActuallyThere()
    {
        var engine = new RoutingEngine([Rule("anything with an accession",
            new() { ["accessionNumber"] = "*" })]);

        Dictionary<string, string> with = Report();
        with["accessionNumber"] = "ACC001";

        Assert.Single(engine.Route(with));
        Assert.Empty(engine.Route(Report()));
    }

    [Fact]
    public void ACommaSeparatedListMeansAnyOfThem()
    {
        var engine = new RoutingEngine([Rule("two kinds",
            new() { ["documentKind"] = "pdf, image" })]);

        Assert.Single(engine.Route(Report(kind: "pdf")));
        Assert.Single(engine.Route(Report(kind: "image")));
        Assert.Empty(engine.Route(Report(kind: "video")));
    }

    [Fact]
    public void MatchingIgnoresCaseBecausePeopleTypeTheseByHand()
    {
        var engine = new RoutingEngine([Rule("reports", new() { ["messageType"] = "oru^r01" })]);

        Assert.Single(engine.Route(Report(type: "ORU^R01")));
    }

    [Fact]
    public void OneMessageCanGoToSeveralSystems()
    {
        var engine = new RoutingEngine(
        [
            Rule("to the archive", destinations: ["archive"]),
            Rule("to the record system", destinations: ["records"])
        ]);

        IReadOnlyList<RoutingDecision> decisions = engine.Route(Report());

        Assert.Equal(["archive", "records"], decisions.Select(d => d.DestinationId));
    }

    [Fact]
    public void ADestinationNamedTwiceIsStillSentToOnce()
    {
        // Two overlapping rules are easy to write by accident. Without this the result is
        // the same report arriving twice at one system, which is a duplicate in a patient's
        // record rather than a tidiness problem.
        var engine = new RoutingEngine(
        [
            Rule("reports", destinations: ["archive", "records"]),
            Rule("everything", destinations: ["archive"])
        ]);

        IReadOnlyList<RoutingDecision> decisions = engine.Route(Report());

        Assert.Equal(["archive", "records"], decisions.Select(d => d.DestinationId));
        Assert.Equal("reports", decisions.First(d => d.DestinationId == "archive").RuleName);
    }

    [Fact]
    public void ALaterRuleStillReachesTheDestinationsTheEarlierOneDidNotClaim()
    {
        var engine = new RoutingEngine(
        [
            Rule("first", destinations: ["archive"]),
            Rule("second", destinations: ["archive", "records"])
        ]);

        Assert.Equal(["archive", "records"], engine.Route(Report()).Select(d => d.DestinationId));
    }

    [Fact]
    public void ARuleCanBeTheLastWord()
    {
        var engine = new RoutingEngine(
        [
            Rule("only this one", destinations: ["archive"], stop: true),
            Rule("never reached", destinations: ["records"])
        ]);

        Assert.Equal(["archive"], engine.Route(Report()).Select(d => d.DestinationId));
    }

    [Fact]
    public void ADisabledRuleDoesNothing()
    {
        var engine = new RoutingEngine([Rule("off", enabled: false)]);

        Assert.Empty(engine.Route(Report()));
    }

    [Fact]
    public void EachDecisionCarriesTheProfileThatBuildsIt()
    {
        var engine = new RoutingEngine(
        [
            Rule("archive", destinations: ["archive"], profile: "oru-for-archive"),
            Rule("records", destinations: ["records"], profile: "oru-for-records")
        ]);

        IReadOnlyList<RoutingDecision> decisions = engine.Route(Report());

        Assert.Equal("oru-for-archive", decisions[0].ProfileId);
        Assert.Equal("oru-for-records", decisions[1].ProfileId);
    }

    [Fact]
    public void RulesThatPointAtNothingAreReportedBeforeTheyAreUsed()
    {
        var engine = new RoutingEngine(
        [
            Rule("good", destinations: ["archive"], profile: "known"),
            Rule("bad destination", destinations: ["nowhere"], profile: "known"),
            Rule("bad profile", destinations: ["archive"], profile: "missing"),
            Rule("no destinations", destinations: []),
            Rule("no profile", destinations: ["archive"], profile: "")
        ]);

        IReadOnlyList<string> problems = engine.Check(["archive"], ["known"]);

        Assert.Contains(problems, p => p.Contains("'nowhere'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'missing'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("names no destination", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("names no profile", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains("'good'", StringComparison.Ordinal));
    }

    [Fact]
    public void RulesThatAreSoundRaiseNothing()
    {
        var engine = new RoutingEngine([Rule("fine", destinations: ["archive"], profile: "known")]);

        Assert.Empty(engine.Check(["archive"], ["known"]));
    }
}
