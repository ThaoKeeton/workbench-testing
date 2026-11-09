using System.Text;
using Workbench.Sdk;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class OwnedFixedPlansTests
{
    [Theory]
    [InlineData(OwnedScenario.Positive, "Owned pilot task", "task.toast", "Saved", "1")]
    [InlineData(OwnedScenario.EmptyTitle, "", "task.validation", "Title required", "0")]
    [InlineData(OwnedScenario.AssertionFailure, "Owned pilot task", "task.toast", "DELIBERATELY WRONG TOAST", "1")]
    public void Catalog_freezes_only_the_four_owned_actions(OwnedScenario scenario, string title, string element, string text, string count)
    {
        var plan = FixedOwnedPlans.Create(scenario);
        Assert.Equal("owned.win32.task-form.v1", plan.TargetId);
        Assert.Equal((ushort)1, plan.Protocol);
        Assert.Equal(new[] { "step-1", "step-2", "step-3", "step-4" }, plan.Steps.Select(s => s.Id));
        Assert.Equal(new TestAction(TestActionKind.SetText, "task.title", title), plan.Steps[0].Action);
        Assert.Equal(new TestAction(TestActionKind.Click, "task.save", null), plan.Steps[1].Action);
        Assert.Equal(new TestAction(TestActionKind.AssertText, element, text), plan.Steps[2].Action);
        Assert.Equal(new TestAction(TestActionKind.AssertText, "task.createdCount", count), plan.Steps[3].Action);
        Assert.Equal(plan.Bytes.ToArray(), FixedOwnedPlans.ParseFrozen(plan.Bytes).Bytes.ToArray());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(plan.Bytes.Span)), plan.Sha256);
    }

    [Fact]
    public void Frozen_input_preserves_original_bytes_and_rejects_any_catalog_or_schema_change()
    {
        const string input = "{\"schema\":1,\"targetId\":\"owned.win32.task-form.v1\",\"protocol\":1,\"scenario\":1,\"steps\":[{\"id\":\"step-1\",\"kind\":0,\"elementId\":\"task.title\",\"value\":\"Owned pilot task\"},{\"id\":\"step-2\",\"kind\":1,\"elementId\":\"task.save\",\"value\":null},{\"id\":\"step-3\",\"kind\":2,\"elementId\":\"task.toast\",\"value\":\"Saved\"},{\"id\":\"step-4\",\"kind\":2,\"elementId\":\"task.createdCount\",\"value\":\"1\"}]}";
        var bytes = Encoding.UTF8.GetBytes(input);
        var parsed = FixedOwnedPlans.ParseFrozen(bytes);
        bytes[0] = 0;
        Assert.Equal(Encoding.UTF8.GetBytes(input), parsed.Bytes.ToArray());
        foreach (var malformed in new[] {
            input.Replace("Owned pilot task", "changed", StringComparison.Ordinal),
            input.Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal),
            input.Replace("\"schema\":1", "\"unknown\":1,\"schema\":1", StringComparison.Ordinal),
            input.Replace("\"kind\":0", "\"kind\":3", StringComparison.Ordinal),
            input.Replace("\"id\":\"step-1\"", "\"kind\":0,\"id\":\"step-1\"", StringComparison.Ordinal),
            input + "{}", input.Replace("Owned pilot task", "\\ud800", StringComparison.Ordinal)
        }) Assert.Equal("InvalidPlan", Assert.Throws<OwnedLabException>(() => FixedOwnedPlans.ParseFrozen(Encoding.UTF8.GetBytes(malformed))).ErrorCode);
        Assert.Throws<OwnedLabException>(() => FixedOwnedPlans.Create((OwnedScenario)0));
    }
}
