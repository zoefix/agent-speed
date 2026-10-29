using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Reporting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Reporting;

/// <summary>The next report as the preview shows it (DESIGN 11, Consent sheet and Preview window).</summary>
/// <param name="NextIn">Seconds until the next report is due; null while quiet, paused or off, and over WebSocket.</param>
/// <param name="OnChange">WebSocket: sent as soon as the data changes (<c>report.preview.onChange</c> instead of the countdown).</param>
/// <param name="Assumed">Built before the service's document is known: only the body is meaningful.</param>
internal sealed record ReportPreview(
    ReportJson Body,
    string RequestLine,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    int? NextIn,
    bool OnChange,
    bool Assumed)
{
    /// <summary>What <c>report.preview.copy</c> copies: the pretty-printed body.</summary>
    public string CopyText => Body.ToPrettyString();
}

/// <summary>
/// The live preview: the real body of the next report — the open window of the run, or, before a run exists, the window
/// since the preview was opened.
/// </summary>
internal sealed partial class ReportController
{
    /// <summary>Starts the preview's own window when no run is open.</summary>
    public void OpenPreview()
    {
        _previewOpenings++;
        if (_previewWindow is null)
        {
            _previewWindow = new ReportWindow(LastSample);
            _previewStartedAt = MonotonicMs;
        }
    }

    public void ClosePreview()
    {
        _previewOpenings = Math.Max(0, _previewOpenings - 1);
        if (_previewOpenings == 0)
        {
            _previewWindow = null;
        }
    }

    /// <summary>
    /// The next report for the consent sheet's <paramref name="draft"/>, or for the selected service: its identity and
    /// document, or, before the document is known (the dashboard invitation), the protocol's defaults.
    /// </summary>
    public ReportPreview? Preview(ReportConsentDraft? draft = null)
    {
        var now = MonotonicMs;
        var sample = LastSample;
        ReportDiscovery document;
        string deviceId;
        string username;
        IEnumerable<ReportSection> sections = Preferences.Sections;
        var assumed = false;
        if (draft is not null)
        {
            document = draft.Discovery;
            deviceId = draft.DeviceId;
            username = ReportUsername.Normalized(draft.Username);
            sections = draft.Sections;
        }
        else if (Discovery is { } stored)
        {
            document = stored;
            deviceId = Identity?.DeviceId ?? _previewDeviceId;
            username = FieldUsername;
        }
        else if (SelectedService is { } service && ReportDiscovery.Assumed(service, ServiceName ?? service.Host) is { } guess)
        {
            document = guess;
            deviceId = _previewDeviceId;
            username = FieldUsername;
            assumed = true;
        }
        else
        {
            return null;
        }

        var webSocket = ReportTransports.Effective(draft is not null ? Preferences.TransportOf(draft.Service) : Transport, document)
            == ReportTransportKind.WebSocket;
        var http = draft is null && !webSocket ? _run : null;
        var stream = draft is null && webSocket ? _stream : null;
        var window = http?.Window ?? stream?.Window ?? _previewWindow ?? new ReportWindow(sample);
        var start = http is not null ? http.Scheduler.WindowStart ?? now
            : stream is not null ? stream.Scheduler.WindowStart ?? now
            : _previewStartedAt ?? now;
        var seq = (http?.Scheduler.Seq ?? stream?.Scheduler.Seq ?? 0) + 1;
        var working = sample.IsWorking || sample.Live.Output > 0;
        var activity = !working && Mode == ReportMode.Idle ? ReportActivity.Idle : ReportActivity.Active;
        var envelope = new ReportEnvelope(Client, deviceId, username, http?.Id ?? stream?.Id ?? _previewRunId, seq, UnixMs + _clockCorrection * 1000,
            Math.Max(0, now - start), activity, http?.Title.Level ?? stream?.Title.Level ?? 0);
        var options = Options(document, sections);
        if (webSocket)
        {
            var limits = stream?.Scheduler.Limits ?? document.WebSocket ?? ReportStreamSettings.Default;
            long? age = stream?.Scheduler.WindowObserved is { } observed ? Math.Max(0, now - observed) : null;
            options = options with { Stream = new ReportStreamBody(age, limits.MaxMessageBytes) };
        }

        // The statistics rows are the selected service's (METRICS 11.10); a consent draft has watched nothing yet.
        var content = window.Content(sample);
        if (draft is null)
        {
            content = content with { Perf = _perf.Content(sample) };
        }

        var (body, data) = ReportBody.Fitted(envelope, content, sample, options);
        var timestamp = UnixMs / 1000 + _clockCorrection;
        var hasApiKey = draft is not null ? draft.ApiKey.Length > 0 : _apiKey is not null;
        var request = webSocket
            ? ReportPreviewRequest.WebSocket(ReportWebSocketProtocol.Url(document.Api), Client.UserAgent, deviceId, timestamp, hasApiKey)
            : ReportPreviewRequest.Http(document.Api.Endpoint(ReportEndpoint.Reports), Client.UserAgent, deviceId, timestamp, data.Length, hasApiKey);
        int? nextIn = null;
        if (http is not null && !IsPaused && http.Scheduler.NextDueAt is { } due)
        {
            nextIn = (int)Math.Max(0, (due - now + 999) / 1000);
        }

        // Like the countdown, the WebSocket note only shows while reports actually go out as the data changes.
        var onChange = stream is { Scheduler: { Available: true, Stop: null, Mode: not ReportMode.Quiet } } && !IsPaused;
        return new ReportPreview(body, request.RequestLine, request.Headers, nextIn, onChange, assumed);
    }

    /// <summary>The preset's name and what the invitation says (DESIGN 11, Dashboard invitation): nothing is fetched.</summary>
    public (string Name, string Domain, string Username) InvitationText =>
        (ReportPreferences.CodexRadarName, PresetDomain ?? SelectedService?.Authority ?? string.Empty, FieldUsername);
}
