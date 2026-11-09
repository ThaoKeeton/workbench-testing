using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Sources;
using Xunit;
using static Workbench.Core.Tests.PluginTestingContextTests;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed class TestingAuthoringTests
{
    [Fact]
    public async Task Immediate_flush_joins_held_autosave_then_loops_for_final_accepted_input_using_actual_new_CAS()
    {
        using var disk = new TestDirectory();
        using var context = Registration(new("default","Default",disk.User));
        var testing=Workspace(context); var set=await Seed(testing);
        var draft=new CaseDraftSession(testing,set);
        using var held=HoldLease(disk.User);
        draft.CaptureName("First accepted text");
        var flushing=draft.FlushAsync();
        draft.CaptureName("Final accepted text 🧪\r\n\t");
        Assert.Equal("Final accepted text 🧪\r\n\t",draft.View.Content.Name);
        Assert.True(draft.View.IsDirty); Assert.False(flushing.IsCompleted);
        try
        {
            held.Dispose();
            var saved=await flushing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Final accepted text 🧪\r\n\t",saved.Name);
            Assert.Equal(saved.Name,(await testing.ReadCaseSetAsync(set.Id)).Name);
            Assert.Equal(saved.Revision,draft.View.BaseRevision);
            Assert.False(draft.View.IsDirty);Assert.Null(draft.View.ErrorCode);
        }
        finally { held.Dispose(); await Settle(context,draft); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instruction_or_expected_edits_permanently_clear_only_the_affected_mapping_while_metadata_preserves_it(bool expected)
    {
        using var disk=new TestDirectory(); using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        var item=set.Cases[0];var step=item.Steps[0];
        draft.CaptureCaseMetadata(item.Id,"Changed title","Setup");
        var metadata=await draft.FlushAsync();
        Assert.Equal(step.Action,metadata.Cases[0].Steps[0].Action);
        using var held=HoldLease(disk.User);
        draft.CaptureStepText(item.Id,step.Id,expected ? step.Instruction : "Changed instruction",expected ? "Changed expectation" : step.Expected);
        draft.CaptureStepText(item.Id,step.Id,step.Instruction,step.Expected);
        Assert.Null(draft.View.Content.Cases[0].Steps[0].Action);
        try
        {
            held.Dispose();var saved=await draft.FlushAsync();
            Assert.Null(saved.Cases[0].Steps[0].Action);
            Assert.Equal(set.Cases[0].Steps[1].Action,saved.Cases[0].Steps[1].Action);
            Assert.Equal(step.Traces,saved.Cases[0].Steps[0].Traces);
            var error=await Assert.ThrowsAsync<PluginTestingException>(()=>testing.CreateRunAsync(saved.Id,saved.Revision,[item.Id]));
            Assert.Equal("ManualStepUnsupported",error.ErrorCode);
        }
        finally { held.Dispose(); await Settle(context,draft); }
    }

    [Fact]
    public async Task Overcap_changed_text_then_restored_original_stays_manual_without_persisting_invalid_intermediates()
    {
        using var disk=new TestDirectory();using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        var item=set.Cases[0];var step=item.Steps[0];
        draft.CaptureStepText(item.Id,step.Id,new string('x',4097),step.Expected);
        Assert.Equal("StoreLimitExceeded",(await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync())).ErrorCode);
        draft.CaptureStepText(item.Id,step.Id,step.Instruction,step.Expected);
        var restored=await draft.FlushAsync();
        Assert.Equal(step.Instruction,restored.Cases[0].Steps[0].Instruction);Assert.Null(restored.Cases[0].Steps[0].Action);
        await Settle(context,draft);
    }

    [Fact]
    public async Task Actual_write_failure_retains_same_draft_and_all_accepted_text_then_conditional_retry_saves_it()
    {
        using var disk=new TestDirectory(); using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        var path=CasePath(disk.User,set.Id);var original=await File.ReadAllBytesAsync(path);
        using(var obstruction=WriteObstruction.Create(path))
        {
            draft.CaptureName("Dirty final name");
            draft.CaptureCaseMetadata(set.Cases[0].Id,"Dirty title 🧪","Dirty prerequisites");
            var error=await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync());
            Assert.Equal("StoreIoFailure",error.ErrorCode);
            Assert.True(draft.View.IsDirty);Assert.Equal(set.Revision,draft.View.BaseRevision);
            Assert.Equal("Dirty title 🧪",draft.View.Content.Cases[0].Title);
            Assert.Equal("Dirty prerequisites",draft.View.Content.Cases[0].Prerequisites);
            Assert.Equal("StoreIoFailure",draft.View.ErrorCode);
            Assert.Equal(original,await File.ReadAllBytesAsync(path));
        }
        var saved=await draft.RetryFlushAsync();
        Assert.Equal("Dirty final name",saved.Name);Assert.Equal("Dirty title 🧪",saved.Cases[0].Title);
        Assert.False(draft.View.IsDirty);await Settle(context,draft);
    }

    [Fact]
    public async Task Stale_CAS_retains_same_dirty_model_and_copy_as_new_preserves_it_before_original_reload()
    {
        using var disk=new TestDirectory();using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        var store=new ProjectTestStore(disk.User,"default");
        var external=await store.UpdateCaseSetAsync(Owner,set.Id,new("Externally changed",set.Cases),set.Revision);
        draft.CaptureName(new string('名',200));
        draft.CaptureStepText(set.Cases[0].Id,set.Cases[0].Steps[0].Id,"Dirty instruction 🧪\r\n","Dirty expected");
        var error=await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync());
        Assert.Equal("RevisionConflict",error.ErrorCode);Assert.Equal(set.Revision,draft.View.BaseRevision);
        var dirty=draft.View.Content;
        var result=await draft.PreserveAsNewAndReloadAsync();
        Assert.True(result.ReloadedOriginal);Assert.NotEqual(set.Id,result.Preserved.Id);
        Assert.Equal(dirty.Name,result.Preserved.Name);Assert.Equal(dirty.Cases[0].Title,result.Preserved.Cases[0].Title);
        Assert.Equal("Dirty instruction 🧪\r\n",result.Preserved.Cases[0].Steps[0].Instruction);
        Assert.Null(result.Preserved.Cases[0].Steps[0].Action);
        Assert.Equal(dirty.Cases[0].Traces,result.Preserved.Cases[0].Traces);
        Assert.NotEqual(set.Cases[0].Id,result.Preserved.Cases[0].Id);
        Assert.Equal(external.Revision,draft.View.BaseRevision);Assert.Equal("Externally changed",draft.View.Content.Name);
        Assert.False(draft.View.IsDirty);Assert.Equal(2,(await testing.ListCaseSetsAsync()).Count);
        await Settle(context,draft);
    }

    [Fact]
    public async Task Invalid_accepted_input_is_kept_until_corrected_and_cannot_be_mistaken_for_saved()
    {
        using var disk=new TestDirectory();using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        draft.CaptureName(new string('x',201));
        var error=await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync());
        Assert.Equal("StoreLimitExceeded",error.ErrorCode);Assert.Equal(201,draft.View.Content.Name.Length);Assert.True(draft.View.IsDirty);
        draft.CaptureName("Corrected");var saved=await draft.FlushAsync();
        Assert.Equal("Corrected",saved.Name);Assert.False(draft.View.IsDirty);await Settle(context,draft);
    }

    [Fact]
    public async Task Conditional_conflict_copy_does_not_reload_over_newer_input_accepted_while_copy_IO_is_pending()
    {
        using var disk=new TestDirectory();using var context=Registration(new("default","Default",disk.User));
        var testing=Workspace(context);var set=await Seed(testing);var draft=new CaseDraftSession(testing,set);
        await testing.UpdateCaseSetAsync(set.Id,new("External",set.Cases),set.Revision);
        draft.CaptureName("First dirty draft");await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync());
        using var held=HoldLease(disk.User);var preserving=draft.PreserveAsNewAndReloadAsync();draft.CaptureName("Newer accepted draft");
        try
        {
            held.Dispose();var result=await preserving;
            Assert.False(result.ReloadedOriginal);Assert.Equal("First dirty draft",result.Preserved.Name);
            Assert.Equal("Newer accepted draft",draft.View.Content.Name);Assert.True(draft.View.IsDirty);
            Assert.Equal("RevisionConflict",(await Assert.ThrowsAsync<PluginTestingException>(()=>draft.FlushAsync())).ErrorCode);
            var next=await draft.PreserveAsNewAndReloadAsync();Assert.True(next.ReloadedOriginal);Assert.Equal("Newer accepted draft",next.Preserved.Name);
        }
        finally { held.Dispose();await Settle(context,draft); }
    }

    internal static async Task<TestCaseSetSnapshot> Seed(IProjectTestWorkspace testing)
    {
        var source=await testing.PublishSourceAsync(new RequirementSourceProvider().Parse(SyntheticTestingExamples.Read(TestSourceKind.Requirements)));
        return await testing.CreateCaseSetAsync(new RuleTestCaseGenerator().Generate([source]));
    }
    private static async Task Settle(Workbench.Core.PluginRegistrationContext context,CaseDraftSession draft)
    { _=await Record.ExceptionAsync(()=>draft.FlushAsync());context.Close(PluginStopReason.PluginDisabled);await context.DrainProjectWorkAsync(); }

    internal sealed class WriteObstruction : IDisposable
    {
        private readonly string _directory;private readonly UnixFileMode _mode;private readonly FileStream? _held;
        private WriteObstruction(string path)
        {
            _directory=Path.GetDirectoryName(path)!;
            if(OperatingSystem.IsWindows()) _held=new(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            else { _mode=File.GetUnixFileMode(_directory);File.SetUnixFileMode(_directory,UnixFileMode.UserRead|UnixFileMode.UserExecute); }
        }
        internal static WriteObstruction Create(string path)=>new(path);
        public void Dispose() { _held?.Dispose();if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory,_mode); }
    }
}
