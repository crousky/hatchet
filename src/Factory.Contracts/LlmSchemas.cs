using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Contracts;

/// <summary>A named strict JSON Schema, sent as <c>response_format.json_schema</c>.</summary>
public sealed record LlmSchema(string Name, string Json);

public static class LlmSchemas
{
    /// <summary>Options that parse model output; missing required properties and nulls are errors.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static readonly LlmSchema TaskGraph = new("TaskGraph", """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["subtasks"],
      "properties": {
        "subtasks": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["order", "title", "description", "targetFilesToModify", "targetFilesToCreate", "contextFilesToRead", "verificationCommand"],
            "properties": {
              "order": { "type": "integer" },
              "title": { "type": "string" },
              "description": { "type": "string" },
              "targetFilesToModify": { "type": "array", "items": { "type": "string" } },
              "targetFilesToCreate": { "type": "array", "items": { "type": "string" } },
              "contextFilesToRead": { "type": "array", "items": { "type": "string" } },
              "verificationCommand": { "type": "string" }
            }
          }
        }
      }
    }
    """);

    public static readonly LlmSchema RefinedSpec = new("RefinedSpec", """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["implementationInstructions", "methodSignatures", "interfaces", "testCases", "edgeCaseChecks"],
      "properties": {
        "implementationInstructions": { "type": "string" },
        "methodSignatures": { "type": "array", "items": { "type": "string" } },
        "interfaces": { "type": "array", "items": { "type": "string" } },
        "testCases": { "type": "array", "items": { "type": "string" } },
        "edgeCaseChecks": { "type": "array", "items": { "type": "string" } }
      }
    }
    """);

    public static readonly LlmSchema FilePatchSet = new("FilePatchSet", """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["summaryOfChanges", "operations"],
      "properties": {
        "summaryOfChanges": { "type": "string" },
        "operations": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["relativeFilePath", "action", "fileContent"],
            "properties": {
              "relativeFilePath": { "type": "string" },
              "action": { "type": "string", "enum": ["CREATE", "OVERWRITE", "DELETE"] },
              "fileContent": { "type": "string" }
            }
          }
        }
      }
    }
    """);

    public static readonly LlmSchema ReviewVerdict = new("ReviewVerdict", """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["passed", "confidenceScore", "blockingIssues", "suggestions"],
      "properties": {
        "passed": { "type": "boolean" },
        "confidenceScore": { "type": "integer", "minimum": 0, "maximum": 100 },
        "blockingIssues": { "type": "array", "items": { "type": "string" } },
        "suggestions": { "type": "array", "items": { "type": "string" } }
      }
    }
    """);

    /// <summary>The schema each role must answer with.</summary>
    public static LlmSchema For(AgentRole role) => role switch
    {
        AgentRole.TaskPlanner => TaskGraph,
        AgentRole.ContextSpecifier => RefinedSpec,
        AgentRole.StaticReviewer => ReviewVerdict,
        _ => FilePatchSet
    };

    public static LlmSchema For<T>() =>
        typeof(T) == typeof(TaskGraphResponse) ? TaskGraph :
        typeof(T) == typeof(RefinedSpecResponse) ? RefinedSpec :
        typeof(T) == typeof(FilePatchSetResponse) ? FilePatchSet :
        typeof(T) == typeof(ReviewVerdictResponse) ? ReviewVerdict :
        throw new ArgumentException($"No LLM schema registered for {typeof(T).Name}");
}
