using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class KnowledgeGraphExtractionTests
{
    private static readonly string[] Types = ["organization", "person", "geo", "event"];

    [Fact]
    public void Parse_KeepsAllowedTypes_DropsUnknownTypes_AndUnmatchedRelations()
    {
        const string json = """
            ```json
            {
              "entities": [
                {"chunkId":"c1","name":"Acme","type":"organization","description":"Co"},
                {"chunkId":"c1","name":"Widget","type":"product","description":"Nope"},
                {"chunkId":"c2","name":"Ada","type":"Person","description":"Founder"}
              ],
              "relations": [
                {"chunkId":"c1","source":"Acme","sourceType":"organization","target":"Ada","targetType":"person","description":"founded by","weight":12,"evidence":"Ada founded Acme"},
                {"chunkId":"c1","source":"Acme","sourceType":"organization","target":"Widget","targetType":"product","description":"makes","weight":3,"evidence":"makes widgets"}
              ]
            }
            ```
            """;

        var parsed = KnowledgeGraphExtractionParser.Parse(json, Types, new HashSet<string> { "c1", "c2" });

        Assert.Equal(2, parsed.Entities.Count);
        Assert.DoesNotContain(parsed.Entities, entity => entity.Type == "product");
        var relation = Assert.Single(parsed.Relations);
        Assert.Equal("acme", relation.SourceNormalizedName);
        Assert.Equal("ada", relation.TargetNormalizedName);
        Assert.Equal(10, relation.Weight);
    }

    [Fact]
    public void Merge_CombinesSameTypeAndName_AndKeepsDifferentTypes()
    {
        var extraction = new KnowledgeGraphExtraction(
            [
                new ExtractedKnowledgeEntity("c1", "Acme", "acme", "organization", "Short"),
                new ExtractedKnowledgeEntity("c2", "ACME", "acme", "organization", "A longer company description"),
                new ExtractedKnowledgeEntity("c3", "Acme", "acme", "person", "A person")
            ],
            [
                new ExtractedKnowledgeRelation("c1", "Acme", "acme", "organization", "Missing", "missing", "person", "employs", 4, "nope"),
                new ExtractedKnowledgeRelation("c2", "ACME", "acme", "organization", "Acme", "acme", "person", "same name", 3, "yes")
            ]);

        var graph = KnowledgeGraphMerger.Build(extraction);

        Assert.Equal(2, graph.Entities.Count);
        var company = Assert.Single(graph.Entities, entity => entity.EntityType == "organization");
        Assert.Equal(["c1", "c2"], company.ChunkIds);
        Assert.Equal("A longer company description", company.Description);
        Assert.Single(graph.Entities, entity => entity.EntityType == "person");
        var relation = Assert.Single(graph.Relations);
        Assert.Equal("person", relation.TargetType);
        Assert.Equal("same name", relation.Description);
    }
}
