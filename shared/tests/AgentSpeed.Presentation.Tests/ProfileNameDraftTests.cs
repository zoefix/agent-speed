using AgentSpeed.Presentation.Reporting;

namespace AgentSpeed.Presentation.Tests;

public sealed class ProfileNameDraftTests
{
    [Fact]
    public void InitialProfileRefreshReplacesOnlyAnUntouchedName()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.Receive("shared");
        Assert.Equal("shared", draft.Text);
        Assert.False(draft.IsDirty);
    }
    [Fact]
    public void RefreshNeverOverwritesTypingEvenBeforeFocusMoves()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.SetEditing(true); draft.Edit("my draft"); draft.Receive("remote rename");
        Assert.Equal("my draft", draft.Text);
        draft.SetEditing(false);
        Assert.Equal("my draft", draft.Text);
        Assert.True(draft.IsDirty);
    }
    [Fact]
    public void AFocusedUntouchedNameUpdatesOnlyAfterEditingEnds()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.SetEditing(true); draft.Receive("shared");
        Assert.Equal("device", draft.Text);
        draft.SetEditing(false);
        Assert.Equal("shared", draft.Text);
        Assert.False(draft.IsDirty);
    }
    [Fact]
    public void ADelayedSaveDoesNotEraseTheNextEdit()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.Edit("first"); draft.Edit("second"); draft.Commit("first", "first accepted");
        Assert.Equal("second", draft.Text); Assert.True(draft.IsDirty);
        draft.Restore(); Assert.Equal("first accepted", draft.Text);
    }
    [Fact]
    public void ASuccessfulSaveUsesTheAcceptedNameAndClearsDirtyState()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.Edit("new"); draft.Commit("new", "new");
        Assert.Equal("new", draft.Text); Assert.False(draft.IsDirty);
    }
    [Fact]
    public void ChangingTheServiceResetsAnOldDraft()
    {
        var draft = new ProfileNameDraft(); draft.Reset("device");
        draft.SetEditing(true); draft.Edit("private draft"); draft.Reset("other service");
        Assert.Equal("other service", draft.Text); Assert.False(draft.IsDirty); Assert.False(draft.IsEditing);
    }
}
