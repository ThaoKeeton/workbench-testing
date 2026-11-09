using System.Buffers.Binary;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Xunit;
using Xunit.Abstractions;
using static Workbench.Core.Tests.PluginTestingContextTests;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed class TestingExecutionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Actual_virtual_model_saves_validates_asserts_and_resets_instead_of_unconditionally_passing()
    {
        IControlledTestAdapter adapter=new VirtualTaskFormAdapter();
        Assert.Equal("virtual.task-form.v1",adapter.TargetId);
        await using var session=await adapter.OpenAsync(new string('1',32));
        await session.ResetCaseAsync(new string('2',32));
        var title="Actual observed 🧪\r\n\t标题";
        Assert.True((await session.ExecuteAsync(new(TestActionKind.SetText,"task.title",title))).Passed);
        var saved=await session.ExecuteAsync(new(TestActionKind.Click,"task.save",null));
        Assert.True(saved.Passed);Assert.Contains(title,saved.NativePlainTextState,StringComparison.Ordinal);
        Assert.True((await session.ExecuteAsync(new(TestActionKind.AssertText,"task.toast","Saved"))).Passed);
        Assert.True((await session.ExecuteAsync(new(TestActionKind.AssertText,"task.createdCount","1"))).Passed);
        var fail=await session.ExecuteAsync(new(TestActionKind.AssertText,"task.toast","WRONG"));
        Assert.False(fail.Passed);Assert.Equal("AssertionFailed",fail.ErrorCode);
        await session.ResetCaseAsync(new string('3',32));
        await session.ExecuteAsync(new(TestActionKind.Click,"task.save",null));
        Assert.True((await session.ExecuteAsync(new(TestActionKind.AssertText,"task.validation","Title required"))).Passed);
        Assert.True((await session.ExecuteAsync(new(TestActionKind.AssertText,"task.createdCount","0"))).Passed);
        Assert.Equal("TargetUnavailable",(await Assert.ThrowsAsync<PluginTestingException>(()=>session.ExecuteAsync(new(TestActionKind.Click,"arbitrary-selector",null)))).ErrorCode);
    }

    [Fact]
    public async Task Passive_frame_is_exact_bottom_up_BMP_with_visible_SIMULATED_pixels_and_state_dependent_content()
    {
        await using var session=await new VirtualTaskFormAdapter().OpenAsync(new string('1',32));
        await session.ResetCaseAsync(new string('2',32));
        var empty=await session.ExecuteAsync(new(TestActionKind.SetText,"task.title",""));
        var titled=await session.ExecuteAsync(new(TestActionKind.SetText,"task.title","Task 🧪"));
        var frame=titled.PassiveFrame;
        Assert.Equal(172854,frame.Length);Assert.Equal((byte)'B',frame[0]);Assert.Equal((byte)'M',frame[1]);
        Assert.Equal(320,BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(18)));Assert.Equal(180,BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(22)));
        Assert.Equal(24,BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(28)));Assert.Equal(54,BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(10)));
        Assert.Equal(new byte[16],frame[38..54]);Assert.NotEqual(empty.PassiveFrame,frame);
        // Independent 5x7 S glyph at (12,10), scale 2: 01111 / 10000 / 10000 / 01110 / 00001 / 00001 / 11110.
        byte[] Pixel(int x,int y)=>frame[(54+(179-y)*960+x*3)..(54+(179-y)*960+x*3+3)];
        Assert.Equal(new byte[]{255,255,255},Pixel(14,10));
        Assert.NotEqual(new byte[]{255,255,255},Pixel(12,10));
        Assert.Equal(new byte[]{255,255,255},Pixel(12,22));
        var evidencePath=Path.Combine(Path.GetTempPath(),"workbench-testing-task4-20261107","actual-simulated-frame.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);await File.WriteAllBytesAsync(evidencePath,frame);output.WriteLine(evidencePath);
    }
}
