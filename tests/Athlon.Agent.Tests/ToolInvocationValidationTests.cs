using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ToolInvocationValidationTests
{
    [Fact]
    public void ToolSchema_EmitsNestedItemsAndConstraints()
    {
        var schema = ToolSchema.Object()
            .String("mode", "Mode", required: true, defaultValue: "safe", enumValues: ["safe", "fast"], pattern: "^[a-z]+$", minLength: 2, maxLength: 8)
            .Integer("count", "Count", defaultValue: 3, minimum: 1, maximum: 5)
            .Array(
                "items",
                "Nested items",
                items: ToolSchema.Object()
                    .String("id", "Id", required: true, minLength: 1)
                    .Build(),
                minItems: 1,
                maxItems: 2)
            .Build();

        using var document = JsonDocument.Parse(schema.ToCanonicalJson());
        var properties = document.RootElement.GetProperty("properties");
        Assert.Equal("safe", properties.GetProperty("mode").GetProperty("default").GetString());
        Assert.Equal(2, properties.GetProperty("mode").GetProperty("enum").GetArrayLength());
        Assert.Equal(1, properties.GetProperty("count").GetProperty("minimum").GetInt32());
        Assert.Equal("object", properties.GetProperty("items").GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ToolRouter_InvalidNestedArguments_ReturnsStructuredErrorWithoutExecution()
    {
        var tool = new CountingTool(
            ToolSchema.Object()
                .Array(
                    "todos",
                    "Todos",
                    required: true,
                    items: ToolSchema.Object()
                        .String("status", "Status", required: true, enumValues: ["pending", "completed"])
                        .Build(),
                    minItems: 1)
                .Build());
        var router = new ToolRouter([tool]);
        var arguments = ToolCallArgumentsParser.ParseJson("""{"todos":[{"status":"invalid"}]}""");

        var result = await router.InvokeAsync(new ToolInvocation("counting", arguments));

        Assert.False(result.Succeeded);
        Assert.Equal(0, tool.InvocationCount);
        var error = JsonSerializer.Deserialize<ToolInvocationError>(result.Error!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(error);
        Assert.Equal("schema.enum", error!.Code);
        Assert.Equal("$.todos[0].status", error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Expected));
        Assert.False(string.IsNullOrWhiteSpace(error.Actual));
        Assert.False(string.IsNullOrWhiteSpace(error.Remediation));
        Assert.Contains("status", error.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one of the allowed values", error.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_TypeMismatch_RemediationIncludesPropertyExample()
    {
        var schema = ToolSchema.Object()
            .Integer("start_line", "start", required: true)
            .Build();
        var args = ToolCallArguments.Parse("""{"start_line":"1"}""");

        var error = ToolInvocationValidator.Validate(schema, args);

        Assert.NotNull(error);
        Assert.Equal("schema.type_mismatch", error!.Code);
        Assert.Equal("$.start_line", error.Path);
        Assert.Contains("start_line", error.Remediation, StringComparison.Ordinal);
        Assert.Contains("\"start_line\": 1", error.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolRouter_AskWithoutDecision_RemainsPendingAndDoesNotExecute()
    {
        var tool = new CountingTool(
            ToolSchema.Object().Build(),
            ToolInvocationPolicy.Ask);
        var result = await new ToolRouter([tool]).InvokeAsync(
            new ToolInvocation("counting", ToolCallArguments.Empty));

        Assert.False(result.Succeeded);
        Assert.Equal(0, tool.InvocationCount);
        Assert.Contains("policy.approval_required", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolRouter_AskExecutesOnlyAfterExplicitApproval()
    {
        var tool = new CountingTool(
            ToolSchema.Object().Build(),
            ToolInvocationPolicy.Ask);
        var result = await new ToolRouter([tool]).InvokeAsync(
            new ToolInvocation(
                "counting",
                ToolCallArguments.Empty,
                ApprovalDecision: ToolApprovalDecision.Approved));

        Assert.True(result.Succeeded);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task ToolRouter_DenyCannotBeOverriddenByApproval()
    {
        var tool = new CountingTool(
            ToolSchema.Object().Build(),
            ToolInvocationPolicy.Deny);
        var result = await new ToolRouter([tool]).InvokeAsync(
            new ToolInvocation(
                "counting",
                ToolCallArguments.Empty,
                ApprovalDecision: ToolApprovalDecision.Approved));

        Assert.False(result.Succeeded);
        Assert.Equal(0, tool.InvocationCount);
        Assert.Contains("policy.denied", result.Error, StringComparison.Ordinal);
    }

    private sealed class CountingTool(
        ToolJsonSchema schema,
        ToolInvocationPolicy policy = ToolInvocationPolicy.Allow) : IAgentTool
    {
        public int InvocationCount { get; private set; }

        public ToolDefinition Definition { get; } = new(
            "counting",
            "Counting test tool",
            schema,
            InvocationPolicy: policy);

        public Task<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(ToolResult.Success("executed"));
        }
    }
}
