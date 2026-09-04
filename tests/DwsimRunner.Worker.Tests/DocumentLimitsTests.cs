// FND-0102 (ISK-198) — construction caps, enforced in the worker's own parse.
//
// The API's DocumentValidator already capped `objects` and total bytes, and that is why the
// worker copy is not redundant: POST /flowsheets/pfd reaches FlowsheetBuilder WITHOUT running
// that validator at all, and `connections`/`reactions` were unbounded on every route. A cap that
// depends on an external validator having run is the same unverifiable-owner assumption
// FND-0104 was filed about.
//
// TIER NOTE: not built in CI (Worker needs DWSIM at compile time). See WatchdogTests.

using System.Text.Json;
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class DocumentLimitsTests
{
    private static JsonElement Doc(int objects, int connections, int reactions)
    {
        var o = string.Join(",", Enumerable.Range(0, objects)
            .Select(i => $$"""{"tag":"S{{i}}","kind":"materialStream"}"""));
        var c = string.Join(",", Enumerable.Range(0, connections)
            .Select(i => $$"""{"from":"S{{i}}","to":"U1","port":"Inlet"}"""));
        var r = string.Join(",", Enumerable.Range(0, reactions)
            .Select(i => $$"""{"tag":"R{{i}}","type":"conversion","stoichiometry":{},"baseCompound":"Methane"}"""));
        return JsonDocument.Parse(
            $$"""{"schemaVersion":1,"compounds":["Methane"],"propertyPackage":"PR","objects":[{{o}}],"connections":[{{c}}],"reactions":[{{r}}]}""")
            .RootElement.Clone();
    }

    [Theory]
    [InlineData(501, 0, 0, "objects")]
    [InlineData(1, 1001, 0, "connections")]
    [InlineData(1, 0, 201, "reactions")]
    public void An_oversized_document_is_refused_before_any_engine_object_is_created(
        int objects, int connections, int reactions, string what)
    {
        var ex = Assert.Throws<WorkerInputException>(() => FlowsheetBuilder.ParseDocument(Doc(objects, connections, reactions)));
        Assert.Equal("DOCUMENT_TOO_LARGE", ex.Code);
        Assert.Contains(what, ex.Message);
    }

    [Fact]
    public void The_largest_document_in_the_corpus_still_parses()
    {
        // 47 objects / 48 connections / 4 reactions — measured across every flowsheet document in
        // the eval corpus. A cap that refuses this is a cap set too low.
        var doc = FlowsheetBuilder.ParseDocument(Doc(47, 48, 4));
        Assert.Equal(47, doc.Objects.Count);
    }

    [Fact]
    public void The_caps_are_configuration()
    {
        var previous = Environment.GetEnvironmentVariable("MAX_DOCUMENT_OBJECTS");
        try
        {
            Environment.SetEnvironmentVariable("MAX_DOCUMENT_OBJECTS", "10");
            Assert.Throws<WorkerInputException>(() => FlowsheetBuilder.ParseDocument(Doc(11, 0, 0)));
        }
        finally { Environment.SetEnvironmentVariable("MAX_DOCUMENT_OBJECTS", previous); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("0")]
    public void A_malformed_cap_falls_back_to_the_default_rather_than_off(string? configured)
    {
        var previous = Environment.GetEnvironmentVariable("MAX_DOCUMENT_OBJECTS");
        try
        {
            Environment.SetEnvironmentVariable("MAX_DOCUMENT_OBJECTS", configured);
            Assert.Equal(500, FlowsheetBuilder.MaxObjects);
        }
        finally { Environment.SetEnvironmentVariable("MAX_DOCUMENT_OBJECTS", previous); }
    }
}
