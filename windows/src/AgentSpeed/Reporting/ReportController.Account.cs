using System.Security.Cryptography;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Reporting;

/// <summary>
/// The user's side (DESIGN 11): turning reporting on and off, the service, the consent sheet and registration, sections,
/// pausing, the username, the API key, forgetting and resetting this device, and the discovery refresh.
/// </summary>
internal sealed partial class ReportController
{
    /// <summary>The consent sheet opened or closed (the app shows or hides the dialog).</summary>
    public event EventHandler? ConsentChanged;

    /// <summary>
    /// <c>report.enabled</c>: on opens the consent sheet unless this service is confirmed and registered; off sends one leaving
    /// report and keeps the registration.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == Preferences.Enabled && !(enabled && Consent is null && Identity?.IsRegistered != true))
        {
            return;
        }

        if (enabled)
        {
            if (Problem is ReportProblem.Gone or ReportProblem.Rejected)
            {
                Problem = null;
            }

            if (Identity is { IsRegistered: true, Pin: not null })
            {
                UpdatePreferences(p => p with { Enabled = true });
                LoadSecrets();
                RefreshDiscoveryIfDue(force: false);
                if (NeedsReview)
                {
                    BeginConsent(review: true);
                }
            }
            else
            {
                BeginConsent(review: false);
            }
        }
        else
        {
            Post(ReportEvent.Stop);
            UpdatePreferences(p => p with { Enabled = false });
            if (Consent is not null)
            {
                CancelConsent();
            }

            EndSetup();
        }

        Notify();
    }

    public void SelectPreset(ReportPreset preset)
    {
        if (preset != Preferences.Preset)
        {
            SwitchService(p => p with { Preset = preset });
        }
    }

    /// <summary>The custom URL, committed (Return or focus loss) and valid.</summary>
    public void CommitCustomUrl(string text)
    {
        if (ReportServiceUrl.Parse(text) is not { } service
            || (service.Text == SelectedService?.Text && Preferences.Preset == ReportPreset.Custom))
        {
            return;
        }

        SwitchService(p => p with { Preset = ReportPreset.Custom, CustomUrl = service.Text });
    }

    /// <summary>Switching services pauses the old one (its identity stays) and opens the consent sheet for the new one.</summary>
    private void SwitchService(Func<ReportPreferences, ReportPreferences> change)
    {
        var wasEnabled = Preferences.Enabled;
        if (_run is not null || _stream is not null)
        {
            Post(ReportEvent.Stop);
        }

        EndRun();
        if (Consent is not null)
        {
            CancelConsent();
        }

        DiscardUnregisteredIdentity();
        ClearSetupMessages();
        _consentWanted = false;
        UpdatePreferences(change);
        ServiceState = ReportServiceState.Unknown;
        Problem = null;
        _strikes = 0;
        LastDeliveryAt = null;
        Rank = null;
        RetryReason = null;
        RateLimitedUntil = null;
        LoadSecrets();
        if (Identity is not { } identity)
        {
            if (wasEnabled)
            {
                BeginConsent(review: false);
            }
            else
            {
                CheckService();
            }
        }
        else if (wasEnabled && identity is { IsRegistered: true, Pin: not null })
        {
            RefreshDiscoveryIfDue(force: false);
        }
        else if (wasEnabled)
        {
            BeginConsent(review: false);
        }

        Notify();
    }

    public void SetSection(ReportSection section, bool on)
    {
        UpdatePreferences(p => p.WithSection(section, on));
        ClearRejection();
        FollowPerfWatch();
        Notify();
    }

    /// <summary>Pauses for <paramref name="duration"/>, or until the user resumes when null.</summary>
    public void Pause(TimeSpan? duration)
    {
        var until = duration is { } d ? DateTimeOffset.UtcNow + d : DateTimeOffset.MaxValue;
        var wasPaused = IsPaused;
        UpdatePreferences(p => p with { PausedUntil = until });
        if (!wasPaused)
        {
            Post(ReportEvent.Pause);
        }

        SchedulePauseEnd();
        Notify();
    }

    public void Resume()
    {
        UpdatePreferences(p => p with { PausedUntil = null });
        Cancel(ref _pauseTimer);
        Post(ReportEvent.Resume);
        Notify();
    }

    private void SchedulePauseEnd()
    {
        Cancel(ref _pauseTimer);
        if (Preferences.PausedUntil is not { } until || until == DateTimeOffset.MaxValue)
        {
            return;
        }

        var left = until - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            Resume();
            return;
        }

        _pauseTimer = After(left, Resume);
    }

    private void ClearRejection()
    {
        if (Problem == ReportProblem.Rejected)
        {
            Problem = null;
        }

        _strikes = 0;
    }

    /// <summary>Fetches the selected service's document for the pane (<c>report.service.checking</c>).</summary>
    public void CheckService(Action<ReportDiscovery>? then = null, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (!NetworkEnabled || SelectedService is not { } service)
        {
            return;
        }

        ReportLog.Write($"discovery of {service.Host} for {caller}");

        ServiceState = ReportServiceState.Checking;
        Notify();
        _ = FetchAsync();

        async Task FetchAsync()
        {
            var outcome = await _api.DiscoverAsync(service).ConfigureAwait(true);
            if (!Equals(SelectedService, service))
            {
                return;
            }

            ApplyDiscovery(outcome, service);
            if (ServiceState is { Kind: ReportServiceStateKind.Ready, Document: { } document })
            {
                then?.Invoke(document);
            }

            Notify();
        }
    }

    /// <summary>At launch while on, every 24 hours, after <c>version_unsupported</c> and <c>payload_too_large</c> (REPORTING 3.4).</summary>
    private void RefreshDiscoveryIfDue(bool force)
    {
        if (!Preferences.Enabled || !NetworkEnabled || Identity is not { IsRegistered: true } identity)
        {
            return;
        }

        if (!force && identity.DiscoveryFetchedAt is { } fetched && DateTimeOffset.UtcNow - fetched < RefreshInterval)
        {
            ScheduleRefresh(RefreshInterval - (DateTimeOffset.UtcNow - fetched));
            return;
        }

        CheckService();
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        Cancel(ref _refreshTimer);
        _refreshTimer = After(delay < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, () =>
        {
            RefreshDiscoveryIfDue(force: false);
            ContinueSetup();
        });
    }

    private void ApplyDiscovery(ReportDiscoveryOutcome outcome, ReportServiceUrl service)
    {
        switch (outcome)
        {
            case ReportDiscoveryOutcome.Document fetched:
                ServiceState = new ReportServiceState(ReportServiceStateKind.Ready, fetched.Discovery);
                if (Identity is not null)
                {
                    UpdateIdentity(service.Origin, i => i with { Discovery = fetched.Discovery, DiscoveryFetchedAt = DateTimeOffset.UtcNow });
                }

                if (!fetched.Discovery.SupportsThisClient && Identity?.IsRegistered == true)
                {
                    Problem = ReportProblem.Upgrade;
                }

                if (FollowEffectiveTransport())
                {
                    // The document added or dropped the WebSocket capability: the run starts over on the other transport.
                    Tick();
                }
                else
                {
                    _run?.Scheduler.Update(fetched.Discovery.Limits);
                    StreamDiscovery(fetched.Discovery);
                }

                ScheduleRefresh(RefreshInterval);
                ConfirmInvitedIfCovered(fetched.Discovery, service);
                break;
            case ReportDiscoveryOutcome.NotSupported or ReportDiscoveryOutcome.Invalid:
                ServiceState = new ReportServiceState(outcome is ReportDiscoveryOutcome.Invalid ? ReportServiceStateKind.Invalid : ReportServiceStateKind.NotSupported);
                StreamDiscovery(null);
                if (IsSettingUp)
                {
                    ScheduleRefresh(RefreshInterval);
                }

                break;
            case ReportDiscoveryOutcome.Gone:
                ServiceState = new ReportServiceState(ReportServiceStateKind.Gone);
                if (Identity?.IsRegistered == true || IsSettingUp)
                {
                    Problem = ReportProblem.Gone;
                    UpdatePreferences(p => p with { Enabled = false });
                    EndRun();
                    EndSetup();
                }

                break;
            case ReportDiscoveryOutcome.Failed failed:
                ServiceState = new ReportServiceState(ReportServiceStateKind.Failed, Failure: failed.Failure);
                StreamDiscovery(null);
                ScheduleRefresh(TimeSpan.FromHours(1));
                break;
        }
    }

    /// <summary>Opens the consent sheet once the service's document is known; nothing else is requested.</summary>
    public void BeginConsent(bool review)
    {
        if (!NetworkEnabled || SelectedService is not { } service)
        {
            return;
        }

        _consentWanted = true;
        void Open(ReportDiscovery document)
        {
            if (!Equals(SelectedService, service) || !_consentWanted)
            {
                return;
            }

            _consentWanted = false;
            var identity = Identity;
            Consent = new ReportConsentDraft(service, document, identity?.DeviceId ?? ReportCanonical.NewUuid())
            {
                Username = identity?.Username ?? NextUsername(document.UsernameRules),
                Sections = Preferences.Sections.ToHashSet(),
                IsReview = review && identity?.IsRegistered == true,
            };
            ConsentChanged?.Invoke(this, EventArgs.Empty);
            Notify();
        }

        if (ServiceState is { Kind: ReportServiceStateKind.Ready, Document: { } ready } && !review)
        {
            Open(ready);
        }
        else
        {
            CheckService(Open);
        }
    }

    /// <summary>The consent sheet's edits (username, API key, section switches) as the user types.</summary>
    public void EditConsent(Func<ReportConsentDraft, ReportConsentDraft> change)
    {
        if (Consent is { IsStarting: false } draft)
        {
            Consent = change(draft);
            Notify();
        }
    }

    public void CancelConsent()
    {
        if (Consent is not { } draft)
        {
            return;
        }

        Consent = null;
        Registering = false;
        if (_draftSigner is not null && Preferences.Identity(draft.Service)?.IsRegistered != true)
        {
            _keyStore.DeleteAll(draft.DeviceId);
        }

        _draftSigner = null;
        _consentWanted = false;
        // A cancelled review keeps reporting paused on report.service.changed (REPORTING 3.3); a cancelled first consent —
        // after the invitation too — turns reporting off and leaves nothing behind.
        if (Identity?.IsRegistered != true)
        {
            UpdatePreferences(p => p with { Enabled = false });
            EndSetup();
        }

        ConsentChanged?.Invoke(this, EventArgs.Empty);
        Notify();
    }

    /// <summary><c>report.start</c>: confirms a review, or creates the key (first time only) and registers (REPORTING 4.1).</summary>
    public void StartReporting(bool transfer = false)
    {
        if (Consent is not { IsStarting: false } draft)
        {
            return;
        }

        if (draft.IsReview)
        {
            Confirm(draft, null);
            return;
        }

        var rules = draft.Discovery.UsernameRules;
        var username = ReportUsername.Normalized(draft.Username);
        draft = draft with
        {
            Username = username,
            UsernameError = ReportUsername.Validate(username, rules) is { } problem ? UsernameKey(problem, username, rules) : null,
            ApiKeyError = ApiKeyProblem(draft.ApiKey, draft.Discovery.ApiKeyMode),
            Failure = null,
            OffersTransfer = false,
        };
        if (draft.UsernameError is not null || draft.ApiKeyError is not null || !NetworkEnabled)
        {
            Consent = draft;
            Notify();
            return;
        }

        IReportSigner key;
        try
        {
            key = _draftSigner ?? _keyStore.CreateKey(draft.DeviceId);
            _draftSigner = key;
            if (draft.ApiKey.Length > 0)
            {
                _keyStore.SaveApiKey(draft.ApiKey, draft.DeviceId);
            }
        }
        catch (CryptographicException)
        {
            Consent = draft with { Failure = ReportFailures.Key(ReportFailure.Key) };
            Notify();
            return;
        }

        Consent = draft with { IsStarting = true };
        Registering = true;
        Notify();
        var body = ReportDocuments.Registration(draft.DeviceId, draft.Username, key.PublicKey, Client, transfer);
        var builder = new ReportRequestBuilder(Client.UserAgent, draft.DeviceId, draft.ApiKey.Length == 0 ? null : draft.ApiKey);
        var url = draft.Discovery.Api.Endpoint(ReportEndpoint.Devices);
        var timestamp = UnixMs / 1000 + _clockCorrection;
        _ = RegisterAsync();

        async Task RegisterAsync()
        {
            var attempt = await Perform(builder, "POST", url, body, key, timestamp).ConfigureAwait(true);
            FinishConsent(attempt, draft.DeviceId);
        }
    }

    private void FinishConsent(Attempt attempt, string deviceId)
    {
        Registering = false;
        if (Consent is not { } draft || draft.DeviceId != deviceId)
        {
            Notify();
            return;
        }

        draft = draft with { IsStarting = false };
        if (attempt.SigningFailed)
        {
            draft = draft with { Failure = ReportFailures.Key(ReportFailure.Key) };
        }
        else if (attempt.Reply is not { } reply)
        {
            draft = draft with { Failure = ReportFailures.Key(attempt.Result.Error?.Failure ?? ReportFailure.Unreachable) };
        }
        else if (reply.Result is "created" or "exists")
        {
            Confirm(draft, reply);
            return;
        }
        else
        {
            var rules = draft.Discovery.UsernameRules;
            draft = reply.Code switch
            {
                "username_taken" => draft with
                {
                    UsernameError = "report.username.taken",
                    OffersTransfer = draft.Discovery.ApiKeyMode != ReportApiKeyMode.None && draft.ApiKey.Length > 0,
                },
                "username_invalid" => draft with
                {
                    UsernameError = reply.Reason == "reserved"
                        ? "report.username.invalid.reserved"
                        : UsernameKey(ParseProblem(reply.Reason), draft.Username, rules),
                },
                "api_key_required" or "api_key_invalid" => draft with { ApiKeyError = "report.status.apiKey" },
                "gone" => draft with { Failure = "report.status.gone" },
                _ => draft with { Failure = ReportFailures.Key(ReportFailure.Unavailable) },
            };
            if (reply.Code == "gone")
            {
                ServiceState = new ReportServiceState(ReportServiceStateKind.Gone);
            }
        }

        Consent = draft;
        Notify();
    }

    /// <summary>Stores what the user confirmed and turns reporting on.</summary>
    private void Confirm(ReportConsentDraft draft, ReportReply? reply)
    {
        var now = DateTimeOffset.UtcNow;
        var identity = Preferences.Identity(draft.Service) ?? new ReportIdentity
        {
            Service = draft.Service,
            DeviceId = draft.DeviceId,
            KeyCreatedAt = now,
            LastUsedAt = now,
        };
        if (reply is not null)
        {
            identity = identity with
            {
                Username = reply.Username ?? draft.Username,
                ProfileUrl = reply.ProfileUrl ?? identity.ProfileUrl,
                HasApiKey = draft.ApiKey.Length > 0,
                KeyCreatedAt = now,
            };
        }

        identity = identity with
        {
            Pin = ReportConsentPin.Of(draft.Discovery),
            Discovery = draft.Discovery,
            DiscoveryFetchedAt = now,
            LastUsedAt = now,
        };
        IReadOnlyList<string> dropped = [];
        UpdatePreferences(p => p.Upsert(identity, out dropped) with
        {
            Sections = [.. ReportSections.All.Where(draft.Sections.Contains)],
            Enabled = true,
            // Turning reporting on in Settings answers the dashboard invitation too.
            Invite = p.Invite ?? ReportInviteAnswer.Accepted,
        });
        foreach (var deviceId in dropped)
        {
            _keyStore.DeleteAll(deviceId);
        }

        Consent = null;
        _draftSigner = null;
        Problem = null;
        ClearSetupMessages();
        _strikes = 0;
        ServiceState = new ReportServiceState(ReportServiceStateKind.Ready, draft.Discovery);
        EndRun();
        LoadSecrets();
        ConsentChanged?.Invoke(this, EventArgs.Empty);
        Tick();
        Notify();
    }

    /// <summary>
    /// The pane's username field, committed: renames when registered (REPORTING 4.4); otherwise keeps the name for the next
    /// registration (DESIGN 11, Username).
    /// </summary>
    public void CommitUsername(string text)
    {
        var name = ReportUsername.Normalized(text);
        if (Identity is not { IsRegistered: true, Discovery.UsernameRules: { } rules } identity)
        {
            StoreNextUsername(name);
            return;
        }

        if (string.Equals(name, identity.Username, StringComparison.Ordinal))
        {
            return;
        }

        if (ReportUsername.Validate(name, rules) is { } problem)
        {
            RenameState = ReportRenameKind.Failed;
            RenameError = UsernameKey(problem, name, rules);
            Notify();
            return;
        }

        ClearRejection();
        _ = Rename(name);
    }

    /// <summary>Not registered yet: the name waits locally for the next registration — no request — and a waiting setup continues with it.</summary>
    private void StoreNextUsername(string name)
    {
        var rules = UsernameRules;
        if (ReportUsername.Validate(name, rules) is { } problem)
        {
            RenameState = ReportRenameKind.Failed;
            RenameError = UsernameKey(problem, name, rules);
            Notify();
            return;
        }

        RenameState = ReportRenameKind.Idle;
        RenameError = null;
        string? typed = name == DefaultUsername(rules) ? null : name;
        UpdatePreferences(p => p with { Username = typed });
        if (IsSettingUp)
        {
            if (Problem is ReportProblem.UsernameTaken or ReportProblem.UsernameRejected)
            {
                Problem = null;
            }

            if (InviteGap is ReportInviteGap.Username)
            {
                InviteGap = null;
            }

            ContinueSetup();
        }

        Notify();
    }

    private async Task Rename(string name)
    {
        if (Identity is not { Discovery: { } document } identity || _signer is not { } key)
        {
            return;
        }

        RenameState = ReportRenameKind.Working;
        RenameError = null;
        Notify();
        var builder = new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey);
        var url = document.Api.Endpoint(ReportEndpoint.Device(identity.DeviceId));
        var timestamp = UnixMs / 1000 + _clockCorrection;
        var origin = identity.Id;
        var attempt = await Perform(builder, "PATCH", url, ReportDocuments.Rename(name), key, timestamp).ConfigureAwait(true);
        FinishRename(attempt, name, origin);
    }

    private void FinishRename(Attempt attempt, string name, string origin)
    {
        if (attempt.SigningFailed || attempt.Reply is not { } reply)
        {
            RenameState = ReportRenameKind.Failed;
            RenameError = ReportFailures.Key(attempt.SigningFailed ? ReportFailure.Key : attempt.Result.Error?.Failure ?? ReportFailure.Unreachable);
            Notify();
            return;
        }

        if (reply.Result is "updated" or "unchanged")
        {
            UpdateIdentity(origin, i => i with { Username = reply.Username ?? name, ProfileUrl = reply.ProfileUrl ?? i.ProfileUrl });
            if (Problem == ReportProblem.UsernameTaken)
            {
                Problem = null;
            }

            RenameState = ReportRenameKind.Updated;
            After(TimeSpan.FromSeconds(3), () =>
            {
                if (RenameState == ReportRenameKind.Updated)
                {
                    RenameState = ReportRenameKind.Idle;
                    Notify();
                }
            });
            Notify();
            return;
        }

        var rules = Identity?.Discovery?.UsernameRules ?? ReportUsernameRules.Standard;
        RenameState = ReportRenameKind.Failed;
        RenameError = reply.Code switch
        {
            "username_taken" => "report.username.taken",
            "username_invalid" when reply.Reason == "reserved" => "report.username.invalid.reserved",
            "username_invalid" => UsernameKey(ParseProblem(reply.Reason), name, rules),
            _ => ReportFailures.Key(ReportFailure.Unavailable),
        };
        Notify();
    }

    private static ReportUsernameProblem ParseProblem(string? reason) => reason switch
    {
        "length" => ReportUsernameProblem.Length,
        "spaces" => ReportUsernameProblem.Spaces,
        "start" => ReportUsernameProblem.Start,
        _ => ReportUsernameProblem.Characters,
    };

    /// <summary>The localization key for a username problem (DESIGN 11, Username).</summary>
    public static string UsernameKey(ReportUsernameProblem problem, string name, ReportUsernameRules rules) => problem switch
    {
        ReportUsernameProblem.Length => "report.username.invalid.length",
        ReportUsernameProblem.Characters => rules.Charset == ReportUsernameCharset.Ascii ? "report.username.invalid.ascii" : "report.username.invalid.characters",
        ReportUsernameProblem.Start => "report.username.invalid.start",
        _ => !rules.Spaces && name.Contains(' ', StringComparison.Ordinal) ? "report.username.invalid.noSpaces" : "report.username.invalid.spaces",
    };

    /// <summary><c>unknown_device</c> (register again) and <c>signature_invalid</c> (re-check this key) of REPORTING 8.3.</summary>
    private void ReRegister(string? username = null, bool transfer = false)
    {
        if (Identity is not { Discovery: { } document } identity || _signer is not { } key || (username ?? identity.Username) is not { } name)
        {
            return;
        }

        Registering = true;
        Notify();
        var body = ReportDocuments.Registration(identity.DeviceId, name, key.PublicKey, Client, transfer);
        var builder = new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey);
        var url = document.Api.Endpoint(ReportEndpoint.Devices);
        var timestamp = UnixMs / 1000 + _clockCorrection;
        var origin = identity.Id;
        _ = RegisterAsync();

        async Task RegisterAsync()
        {
            var attempt = await Perform(builder, "POST", url, body, key, timestamp).ConfigureAwait(true);
            FinishReRegister(attempt, name, origin);
        }
    }

    /// <summary><c>report.username.transfer</c>: registers again with <c>"transfer": true</c> (REPORTING 4.7), the only path that sends it.</summary>
    public void TransferUsername()
    {
        if (Consent is { OffersTransfer: true })
        {
            StartReporting(transfer: true);
        }
        else if (IsSettingUp && Problem == ReportProblem.UsernameTaken && _apiKey is not null && Identity?.Discovery is { } document)
        {
            Problem = null;
            ReRegister(NextUsername(document.UsernameRules), transfer: true);
        }
    }

    private void FinishReRegister(Attempt attempt, string name, string origin)
    {
        Registering = false;
        // Only a device that is not registered yet waits on this answer; its passing failures are retried hourly.
        var settingUp = Preferences.Identities.FirstOrDefault(i => i.Id == origin)?.IsRegistered == false;
        if (attempt.SigningFailed)
        {
            if (settingUp)
            {
                RegistrationFailure = ReportFailure.Key;
            }

            Notify();
            return;
        }

        if (attempt.Reply is not { } reply)
        {
            if (settingUp)
            {
                RegistrationFailure = attempt.Result.Error?.Failure ?? ReportFailure.Unreachable;
                ScheduleSetupRetry();
            }

            Notify();
            return;
        }

        switch (reply.Result ?? reply.Code)
        {
            case "created" or "exists":
                UpdateIdentity(origin, i => i with { Username = reply.Username ?? name, ProfileUrl = reply.ProfileUrl ?? i.ProfileUrl });
                RegistrationFailure = null;
                if (Problem is ReportProblem.UsernameTaken or ReportProblem.UsernameRejected or ReportProblem.KeyMismatch or ReportProblem.ApiKey)
                {
                    Problem = null;
                }

                LoadSecrets();
                Tick();
                break;
            case "username_taken":
                Problem = ReportProblem.UsernameTaken;
                break;
            case "username_invalid":
                Problem = ReportProblem.UsernameRejected;
                break;
            case "device_conflict":
                Problem = ReportProblem.KeyMismatch;
                break;
            case "api_key_required" or "api_key_invalid":
                Problem = ReportProblem.ApiKey;
                break;
            case "forbidden":
                Problem = ReportProblem.Forbidden;
                break;
            case "gone":
                Problem = ReportProblem.Gone;
                UpdatePreferences(p => p with { Enabled = false });
                EndRun();
                break;
            default:
                if (settingUp)
                {
                    RegistrationFailure = ReportFailure.Unavailable;
                    ScheduleSetupRetry();
                }

                break;
        }

        Notify();
    }

    /// <summary>8–256 printable ASCII characters without spaces (REPORTING 5.6); null when acceptable.</summary>
    public static string? ApiKeyProblem(string key, ReportApiKeyMode mode)
    {
        if (key.Length == 0)
        {
            return mode == ReportApiKeyMode.Required ? "report.apiKey.required" : null;
        }

        return key.Length is >= 8 and <= 256 && key.All(c => c is >= '!' and <= '~') ? null : "report.status.apiKey";
    }

    public bool SetApiKey(string key)
    {
        if (Identity is not { Discovery.ApiKeyMode: var mode } identity || key.Length == 0 || ApiKeyProblem(key, mode) is not null)
        {
            return false;
        }

        try
        {
            _keyStore.SaveApiKey(key, identity.DeviceId);
        }
        catch (CryptographicException)
        {
            return false;
        }

        _apiKey = key;
        UpdateIdentity(identity.Id, i => i with { HasApiKey = true });
        if (Problem == ReportProblem.ApiKey)
        {
            Problem = null;
        }

        ClearRejection();
        ContinueSetup();
        Notify();
        return true;
    }

    public void ClearApiKey()
    {
        if (Identity is not { } identity)
        {
            return;
        }

        _keyStore.DeleteApiKey(identity.DeviceId);
        _apiKey = null;
        UpdateIdentity(identity.Id, i => i with { HasApiKey = false });
        Notify();
    }

    /// <summary><c>report.forget</c> (REPORTING 4.6) for the selected identity or another one.</summary>
    public void ForgetDevice(string? origin = null)
    {
        var targetId = origin ?? Identity?.Id;
        var target = Preferences.Identities.FirstOrDefault(i => i.Id == targetId);
        var selected = target is not null && target.Id == Identity?.Id;
        var key = target is null ? null : selected ? _signer : _keyStore.LoadKey(target.DeviceId);
        if (target is not { Discovery: { } document } || !NetworkEnabled || key is null)
        {
            ForgetState = ReportForgetKind.Failed;
            ForgetFailure = NetworkEnabled ? ReportFailure.Key : ReportFailure.Unreachable;
            Notify();
            return;
        }

        if (selected)
        {
            EndRun();
        }

        ForgetState = ReportForgetKind.Working;
        ForgetFailure = null;
        Notify();
        var targetKey = target.HasApiKey ? selected ? _apiKey : _keyStore.LoadApiKey(target.DeviceId) : null;
        var builder = new ReportRequestBuilder(Client.UserAgent, target.DeviceId, targetKey);
        var url = document.Api.Endpoint(ReportEndpoint.Device(target.DeviceId));
        var timestamp = UnixMs / 1000 + _clockCorrection;
        _ = ForgetAsync();

        async Task ForgetAsync()
        {
            var attempt = await Perform(builder, "DELETE", url, [], key, timestamp).ConfigureAwait(true);
            FinishForget(attempt, target.Id);
        }
    }

    private void FinishForget(Attempt attempt, string origin)
    {
        if (attempt.Reply is { } reply && (reply.Result == "deleted" || reply.Status == 404))
        {
            ForgetLocally(origin);
            ForgetState = ReportForgetKind.Done;
            After(TimeSpan.FromSeconds(6), () =>
            {
                if (ForgetState == ReportForgetKind.Done)
                {
                    ForgetState = ReportForgetKind.Idle;
                    Notify();
                }
            });
        }
        else
        {
            ForgetState = ReportForgetKind.Failed;
            ForgetFailure = attempt.SigningFailed ? ReportFailure.Key
                : attempt.Reply is not null ? ReportFailure.Unavailable
                : attempt.Result.Error?.Failure ?? ReportFailure.Unreachable;
        }

        Notify();
    }

    /// <summary>Deletes the key and the device id here only (<c>report.forget.localOnly</c>).</summary>
    public void ForgetLocally(string? origin = null)
    {
        var targetId = origin ?? Identity?.Id;
        if (Preferences.Identities.FirstOrDefault(i => i.Id == targetId) is not { } target)
        {
            return;
        }

        var selected = target.Id == Identity?.Id;
        if (selected)
        {
            EndRun();
        }

        _keyStore.DeleteAll(target.DeviceId);
        UpdatePreferences(p =>
        {
            var next = p.Removing(target.Id);
            return selected ? next with { Enabled = false } : next;
        });
        if (selected)
        {
            Problem = null;
            LastDeliveryAt = null;
            Rank = null;
            LoadSecrets();
        }

        if (ForgetState == ReportForgetKind.Failed)
        {
            ForgetState = ReportForgetKind.Idle;
        }

        Notify();
    }

    public void DismissForgetResult()
    {
        ForgetState = ReportForgetKind.Idle;
        ForgetFailure = null;
        Notify();
    }

    /// <summary><c>report.reset</c> after a key mismatch: a new key and device id, registered again with the same name.</summary>
    public void ResetDevice()
    {
        if (Identity is not { } old || (old.Username ?? Consent?.Username ?? NextUsername(UsernameRules)) is not { } name)
        {
            return;
        }

        EndRun();
        _keyStore.DeleteAll(old.DeviceId);
        var deviceId = ReportCanonical.NewUuid();
        IReportSigner key;
        try
        {
            key = _keyStore.CreateKey(deviceId);
        }
        catch (CryptographicException)
        {
            return;
        }

        var fresh = old with { DeviceId = deviceId, Username = null, PendingKey = false, HasApiKey = false, KeyCreatedAt = DateTimeOffset.UtcNow };
        if (_apiKey is { } apiKey)
        {
            try
            {
                _keyStore.SaveApiKey(apiKey, deviceId);
                fresh = fresh with { HasApiKey = true };
            }
            catch (CryptographicException)
            {
            }
        }

        UpdatePreferences(p => p.Upsert(fresh, out _));
        _signer = key;
        _pendingSigner = null;
        Problem = null;
        ReRegister(name);
    }

    /// <summary>Rotates a key older than 365 days (REPORTING 4.5); the new key stays pending until the service confirms it.</summary>
    public void RotateKeyIfDue(bool force = false)
    {
        if (_rotating || Identity is not { IsRegistered: true, Discovery: { } document } identity || _signer is not { } key
            || (!force && DateTimeOffset.UtcNow - identity.KeyCreatedAt <= RotationAge))
        {
            return;
        }

        IReportSigner pending;
        if (_pendingSigner is { } existing)
        {
            pending = existing;
        }
        else
        {
            try
            {
                pending = _keyStore.CreateKey(identity.DeviceId, pending: true);
            }
            catch (CryptographicException)
            {
                return;
            }

            _pendingSigner = pending;
            UpdateIdentity(identity.Id, i => i with { PendingKey = true });
        }

        _rotating = true;
        var builder = new ReportRequestBuilder(Client.UserAgent, identity.DeviceId, _apiKey);
        var url = document.Api.Endpoint(ReportEndpoint.Key(identity.DeviceId));
        var timestamp = UnixMs / 1000 + _clockCorrection;
        _ = RotateAsync();

        async Task RotateAsync()
        {
            var attempt = await Perform(builder, "PUT", url, ReportDocuments.Rotation(pending.PublicKey), key, timestamp, newKey: pending)
                .ConfigureAwait(true);
            FinishRotation(attempt);
        }
    }

    private void FinishRotation(Attempt attempt)
    {
        _rotating = false;
        if (attempt.Reply is not { } reply)
        {
            return;
        }

        if (reply.Result is "rotated" or "unchanged")
        {
            PromotePendingKey();
        }
        else if (reply.Code == "device_conflict" && Identity is { } identity)
        {
            _keyStore.DeletePending(identity.DeviceId);
            _pendingSigner = null;
            UpdateIdentity(identity.Id, i => i with { PendingKey = false });
        }

        Notify();
    }

    private void PromotePendingKey()
    {
        if (Identity is not { } identity || _pendingSigner is not { } pending || !_keyStore.PromotePending(identity.DeviceId))
        {
            return;
        }

        _signer = pending;
        _pendingSigner = null;
        UpdateIdentity(identity.Id, i => i with { PendingKey = false, KeyCreatedAt = DateTimeOffset.UtcNow });
    }
}
