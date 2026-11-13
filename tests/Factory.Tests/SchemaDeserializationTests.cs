using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Contracts;
using Factory.Engine.Llm;

namespace Factory.Tests;

public class SchemaDeserializationTests
{
    [Fact]
    public void TaskGraph_parses_valid_output()
    {
        const string json = """
        {"subtasks":[{"order":1,"title":"Scaffold","description":"Create project","targetFilesToModify":[],"targetFilesToCreate":["a.cs"],"contextFilesToRead":["b.cs"],"verificationCommand":"dotnet test"}]}
        """;

        var graph = StructuredOutputParser.Parse<TaskGraphResponse>(json);

        var subtask = Assert.Single(graph.Subtasks);
        Assert.Equal("Scaffold", subtask.Title);
        Assert.Equal(["a.cs"], subtask.TargetFilesToCreate);
        Assert.Equal("dotnet test", subtask.VerificationCommand);
    }

    [Fact]
    public void FilePatchSet_parses_valid_output()
    {
        const string json = """
        {"summaryOfChanges":"Added it","operations":[{"relativeFilePath":"src/a.cs","action":"CREATE","fileContent":"class A { string s = \"}{\"; }"}]}
        """;

        var patch = StructuredOutputParser.Parse<FilePatchSetResponse>(json);

        Assert.Equal("class A { string s = \"}{\"; }", Assert.Single(patch.Operations).FileContent);
    }

    [Fact]
    public void ReviewVerdict_parses_valid_output()
    {
        var verdict = StructuredOutputParser.Parse<ReviewVerdictResponse>("""
        {"passed":false,"confidenceScore":85,"blockingIssues":["No null check"],"suggestions":[]}
        """);

        Assert.False(verdict.Passed);
        Assert.Equal(85, verdict.ConfidenceScore);
        Assert.Equal(["No null check"], verdict.BlockingIssues);
    }

    [Fact]
    public void RefinedSpec_parses_valid_output()
    {
        var spec = StructuredOutputParser.Parse<RefinedSpecResponse>("""
        {"implementationInstructions":"Do it","methodSignatures":["int F()"],"interfaces":[],"testCases":["F_returns_1"],"edgeCaseChecks":["empty"]}
        """);

        Assert.Equal("Do it", spec.ImplementationInstructions);
        Assert.Equal(["int F()"], spec.MethodSignatures);
    }

    [Theory]
    [InlineData("""{"passed":true,"confidenceScore":85,"blockingIssues":[]}""")]             // Missing suggestions.
    [InlineData("""{"passed":true,"confidenceScore":85,"blockingIssues":null,"suggestions":[]}""")]
    [InlineData("""{"passed":"maybe","confidenceScore":85,"blockingIssues":[],"suggestions":[]}""")]
    [InlineData("""not json at all""")]
    [InlineData("""{"passed":true""")]
    public void Output_that_breaks_the_schema_is_rejected(string content) =>
        Assert.ThrowsAny<JsonException>(() => StructuredOutputParser.Parse<ReviewVerdictResponse>(content));

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Here is the JSON:\n{\"a\":1}\nHope that helps!", "{\"a\":1}")]
    [InlineData("<think>maybe {\"wrong\":true}</think>{\"a\":1}", "{\"a\":1}")]
    [InlineData("{\"a\":\"}{\"}", "{\"a\":\"}{\"}")]
    [InlineData("{\"a\":{\"b\":\"\\\"}\"}} trailing", "{\"a\":{\"b\":\"\\\"}\"}}")]
    public void Chatter_around_the_json_is_tolerated(string content, string expected) =>
        Assert.Equal(expected, StructuredOutputParser.ExtractJsonObject(content));

    [Fact]
    public void Think_text_inside_the_json_answer_is_kept_but_a_leading_think_block_is_removed()
    {
        var kept = StructuredOutputParser.Parse<FileContentProbe>("""<think>plan</think>{"fileContent":"a <think>b</think> c"}""");
        Assert.Equal("a <think>b</think> c", kept.FileContent);

        var unterminated = StructuredOutputParser.Parse<FileContentProbe>("""<think>one</think> <think>two</think> {"fileContent":"x <think>open"}""");
        Assert.Equal("x <think>open", unterminated.FileContent);

        Assert.Null(StructuredOutputParser.ExtractJsonObject("""<think>still thinking {"a":1}"""));
    }

    private sealed record FileContentProbe(string FileContent);

    public static TheoryData<Type, LlmSchema> SchemaPairs => new()
    {
        { typeof(TaskGraphResponse), LlmSchemas.TaskGraph },
        { typeof(RefinedSpecResponse), LlmSchemas.RefinedSpec },
        { typeof(FilePatchSetResponse), LlmSchemas.FilePatchSet },
        { typeof(ReviewVerdictResponse), LlmSchemas.ReviewVerdict }
    };

    [Theory]
    [MemberData(nameof(SchemaPairs))]
    public void Schemas_match_their_records_and_are_strict(Type type, LlmSchema schema) =>
        AssertObjectMatches(type, JsonNode.Parse(schema.Json)!.AsObject());

    private static void AssertObjectMatches(Type type, JsonObject schema)
    {
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());

        var properties = schema["properties"]!.AsObject();
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var expected = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.PropertyType);

        Assert.Equal(expected.Keys.ToHashSet(), properties.Select(p => p.Key).ToHashSet());
        Assert.Equal(expected.Keys.ToHashSet(), required);

        foreach (var (name, propertyType) in expected)
        {
            if (!propertyType.IsGenericType) continue;
            var elementType = propertyType.GetGenericArguments()[0];
            if (elementType != typeof(string))
                AssertObjectMatches(elementType, properties[name]!["items"]!.AsObject());
        }
    }

    [Fact]
    public void For_returns_the_schema_for_each_response_type()
    {
        Assert.Same(LlmSchemas.TaskGraph, LlmSchemas.For<TaskGraphResponse>());
        Assert.Same(LlmSchemas.RefinedSpec, LlmSchemas.For<RefinedSpecResponse>());
        Assert.Same(LlmSchemas.FilePatchSet, LlmSchemas.For<FilePatchSetResponse>());
        Assert.Same(LlmSchemas.ReviewVerdict, LlmSchemas.For<ReviewVerdictResponse>());
    }
}
