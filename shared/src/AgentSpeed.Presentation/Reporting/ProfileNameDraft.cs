namespace AgentSpeed.Presentation.Reporting;

public sealed class ProfileNameDraft
{
    private string _server = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public bool IsDirty { get; private set; }
    public bool IsEditing { get; private set; }

    public void Edit(string text) { Text = text; IsDirty = text != _server; }
    public void SetEditing(bool editing)
    {
        IsEditing = editing;
        if (!editing && !IsDirty) Text = _server;
    }
    public void Receive(string name)
    {
        if (!IsDirty && !IsEditing) Text = name;
        _server = name;
    }
    public void Commit(string submittedText, string acceptedName)
    {
        _server = acceptedName;
        if (Text == submittedText) { Text = acceptedName; IsDirty = false; }
    }
    public void Restore() { Text = _server; IsDirty = false; }
    public void Reset(string name) { _server = Text = name; IsDirty = IsEditing = false; }
}
