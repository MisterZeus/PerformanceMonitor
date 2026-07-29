using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PerformanceMonitor.Analysis;
using PerformanceMonitorLite.Analysis;
using PerformanceMonitorLite.Database;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Tests the InferenceEngine and RelationshipGraph against seeded scenarios.
/// Validates that stories are built with correct paths and severity ordering.
/// </summary>
public class InferenceEngineTests : IClassFixture<SharedDuckDbFixture>
{
    private readonly DuckDbInitializer _duckDb;

    public InferenceEngineTests(SharedDuckDbFixture fixture)
    {
        fixture.ResetData();
        _duckDb = fixture.DuckDb;
    }

    /* ── MemoryStarved scenario ── */

    [Fact]
    public async Task MemoryStarved_ProducesStories()
    {
        var stories = await BuildStoriesAsync(s => s.SeedMemoryStarvedServerAsync());

        Assert.NotEmpty(stories);
        Assert.All(stories, s => Assert.False(s.IsAbsolution));
    }

    [Fact]
    public async Task MemoryStarved_HighestSeverityStoryFirst()
    {
        var stories = await BuildStoriesAsync(s => s.SeedMemoryStarvedServerAsync());

        // PAGEIOLATCH_SH should be the highest severity entry point
        Assert.Equal("PAGEIOLATCH_SH", stories[0].RootFactKey);
    }

    [Fact]
    public async Task MemoryStarved_StoriesHaveStablePaths()
    {
        var stories = await BuildStoriesAsync(s => s.SeedMemoryStarvedServerAsync());

        foreach (var story in stories)
        {
            Assert.NotEmpty(story.StoryPath);
            Assert.NotEmpty(story.StoryPathHash);
            Assert.Equal(16, story.StoryPathHash.Length); // 16 hex chars
        }
    }

    [Fact]
    public async Task MemoryStarved_NoFactUsedTwice()
    {
        var stories = await BuildStoriesAsync(s => s.SeedMemoryStarvedServerAsync());

        var allFactKeys = stories.SelectMany(s => s.Path).ToList();
        var distinctKeys = allFactKeys.Distinct().ToList();

        Assert.Equal(distinctKeys.Count, allFactKeys.Count);
    }

    /* ── BadParallelism scenario ── */

    [Fact]
    public async Task BadParallelism_CxPacketLeadsStory()
    {
        var stories = await BuildStoriesAsync(s => s.SeedBadParallelismServerAsync());

        // CXPACKET has highest severity (1.7 with amplifiers)
        Assert.Equal("CXPACKET", stories[0].RootFactKey);
    }

    [Fact]
    public async Task BadParallelism_CxPacketTraversesToSos()
    {
        var stories = await BuildStoriesAsync(s => s.SeedBadParallelismServerAsync());

        var cxStory = stories.First(s => s.RootFactKey == "CXPACKET");

        // CXPACKET → SOS_SCHEDULER_YIELD (edge: CPU starvation from parallelism)
        Assert.Contains("SOS_SCHEDULER_YIELD", cxStory.Path);
        Assert.True(cxStory.Path.Count >= 2, "Should traverse at least one edge");
    }

    [Fact]
    public async Task BadParallelism_StoryPathShowsTraversal()
    {
        var stories = await BuildStoriesAsync(s => s.SeedBadParallelismServerAsync());

        var cxStory = stories.First(s => s.RootFactKey == "CXPACKET");

        Assert.Contains("→", cxStory.StoryPath); // Multi-node path
    }

    /* ── CleanServer scenario ── */

    [Fact]
    public async Task CleanServer_ProducesAbsolution()
    {
        var stories = await BuildStoriesAsync(s => s.SeedCleanServerAsync());

        // All waits below 0.5 severity → should produce absolution
        Assert.Single(stories);
        Assert.True(stories[0].IsAbsolution);
        Assert.Equal("absolution", stories[0].Category);
    }

    /* ── Unit tests: graph edge evaluation ── */

    [Fact]
    public void Graph_NoEdgesForUnknownFact()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>();

        var edges = graph.GetActiveEdges("UNKNOWN_THING", facts);
        Assert.Empty(edges);
    }

    [Fact]
    public void Graph_CxPacketEdgeFires_WhenSosIsHigh()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>
        {
            ["SOS_SCHEDULER_YIELD"] = new() { Key = "SOS_SCHEDULER_YIELD", Value = 0.50, Severity = 0.67 }
        };

        var edges = graph.GetActiveEdges("CXPACKET", facts);
        Assert.Contains(edges, e => e.Destination == "SOS_SCHEDULER_YIELD");
    }

    [Fact]
    public void Graph_CxPacketEdgeDoesNotFire_WhenSosIsLow()
    {
        var graph = new RelationshipGraph();
        var facts = new Dictionary<string, Fact>
        {
            ["SOS_SCHEDULER_YIELD"] = new() { Key = "SOS_SCHEDULER_YIELD", Value = 0.10, Severity = 0.13 }
        };

        var edges = graph.GetActiveEdges("CXPACKET", facts);
        Assert.DoesNotContain(edges, e => e.Destination == "SOS_SCHEDULER_YIELD");
    }

    /* ── Helper ── */

    private async Task<List<AnalysisStory>> BuildStoriesAsync(Func<TestDataSeeder, Task> seedAction)
    {
        using var seeder = new TestDataSeeder(_duckDb);
        await seedAction(seeder);

        var collector = new DuckDbFactCollector(_duckDb);
        var context = TestDataSeeder.CreateTestContext();
        var facts = await collector.CollectFactsAsync(context);

        var scorer = new FactScorer();
        scorer.ScoreAll(facts);

        var graph = new RelationshipGraph();
        var engine = new InferenceEngine(graph);
        return engine.BuildStories(facts);
    }
}
