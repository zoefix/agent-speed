import AgentSpeedCore
import Foundation

/// The user's side (DESIGN 11): turning reporting on and off, the service, the consent sheet and registration,
/// sections, pausing, the username, the API key, forgetting and resetting this device, and the discovery refresh.
/// The dashboard invitation's setup is in `ReportController+Setup`.
extension ReportController {
    // MARK: - Master switch, service, sections, pause

    /// `report.enabled`: on opens the consent sheet unless this service is confirmed and registered; off sends one
    /// leaving report and keeps the registration.
    func setEnabled(_ enabled: Bool) {
        guard enabled != preferences.enabled || (enabled && consent == nil && identity?.isRegistered != true) else { return }
        if enabled {
            if problem == .gone || problem == .rejected { problem = nil }
            if let identity, identity.isRegistered, identity.pin != nil {
                updatePreferences { $0.enabled = true }
                loadSecrets()
                refreshDiscoveryIfDue(force: false)
                if needsReview { beginConsent(review: true) }
            } else {
                beginConsent(review: false)
            }
        } else {
            post(.stop)
            updatePreferences { $0.enabled = false }
            updatePerfWatch()
            if consent != nil { cancelConsent() }
            endSetup()
        }
    }

    func selectPreset(_ preset: ReportPreset) {
        guard preset != preferences.preset else { return }
        switchService { $0.preset = preset }
    }

    /// The custom URL, committed (Return or focus loss) and valid.
    func commitCustomURL(_ text: String) {
        guard case .success(let service) = ReportServiceURL.parse(text) else { return }
        guard service.string != selectedService?.string || preferences.preset != .custom else { return }
        switchService {
            $0.preset = .custom
            $0.customURL = service.string
        }
    }

    /// Switching services pauses the old one (its identity stays) and opens the consent sheet for the new one.
    private func switchService(_ change: (inout ReportPreferences) -> Void) {
        profile.reset()
        let wasEnabled = preferences.enabled
        if run != nil { post(.stop) }
        endRun()
        if consent != nil { cancelConsent() }
        discardUnregisteredIdentity()
        clearSetupMessages()
        consentWanted = false
        updatePreferences(change)
        serviceState = .unknown
        problem = nil
        strikes = 0
        lastDeliveryAt = nil
        rank = nil
        retryReason = nil
        rateLimitedUntil = nil
        loadSecrets()
        updatePerfWatch()
        guard let identity else {
            if wasEnabled { beginConsent(review: false) } else { checkService() }
            return
        }
        if wasEnabled, identity.isRegistered, identity.pin != nil {
            refreshDiscoveryIfDue(force: false)
        } else if wasEnabled {
            beginConsent(review: false)
        }
    }

    func setSection(_ section: ReportSection, on: Bool) {
        updatePreferences { preferences in
            if on { preferences.sections.insert(section) } else { preferences.sections.remove(section) }
        }
        clearRejection()
        // The models switch carries the performance score: off is a break (METRICS 11.10).
        updatePerfWatch()
    }

    func pause(for duration: TimeInterval?) {
        let until = duration.map { Date().addingTimeInterval($0) } ?? .distantFuture
        let wasPaused = preferences.isPaused
        updatePreferences { $0.pausedUntil = until }
        if !wasPaused { post(.pause) }
        schedulePauseEnd()
    }

    func resume() {
        updatePreferences { $0.pausedUntil = nil }
        pauseTimer?.invalidate()
        pauseTimer = nil
        post(.resume)
    }

    func schedulePauseEnd() {
        pauseTimer?.invalidate()
        pauseTimer = nil
        guard let until = preferences.pausedUntil else { return }
        guard until != .distantFuture else { return }
        guard until > Date() else {
            resume()
            return
        }
        let timer = Timer(fire: until, interval: 0, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.resume() }
        }
        RunLoop.main.add(timer, forMode: .common)
        pauseTimer = timer
    }

    func clearRejection() {
        if problem == .rejected { problem = nil }
        strikes = 0
    }

    // MARK: - Discovery

    /// Fetches the selected service's document for the pane (`report.service.checking`).
    func checkService(then: (@MainActor (ReportDiscovery) -> Void)? = nil) {
        guard settings.networkEnabled, let service = selectedService else { return }
        serviceState = .checking
        let api = self.api
        refreshTask?.cancel()
        refreshTask = Task { [weak self] in
            let outcome = await api.discover(service)
            guard let self, !Task.isCancelled, self.selectedService == service else { return }
            self.applyDiscovery(outcome, service: service)
            if case .ready(let document) = self.serviceState { then?(document) }
        }
    }

    /// At launch while on, every 24 hours, after `version_unsupported` and `payload_too_large` (REPORTING 3.4).
    func refreshDiscoveryIfDue(force: Bool) {
        guard preferences.enabled, settings.networkEnabled, let identity, identity.isRegistered else { return }
        if !force, let fetched = identity.discoveryFetchedAt, Date().timeIntervalSince(fetched) < Self.refreshInterval {
            scheduleRefresh(after: Self.refreshInterval - Date().timeIntervalSince(fetched))
            return
        }
        checkService()
    }

    func scheduleRefresh(after seconds: TimeInterval) {
        refreshTimer?.invalidate()
        let timer = Timer(timeInterval: max(60, seconds), repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshDiscoveryIfDue(force: false)
                self?.continueSetup()
            }
        }
        timer.tolerance = 60
        RunLoop.main.add(timer, forMode: .common)
        refreshTimer = timer
    }

    func applyDiscovery(_ outcome: ReportAPI.DiscoveryOutcome, service: ReportServiceURL) {
        switch outcome {
        case .document(let document):
            let effective = effectiveTransport
            serviceState = .ready(document)
            if identity != nil {
                updateIdentity(service.origin) { identity in
                    identity.discovery = document
                    identity.discoveryFetchedAt = Date()
                }
            }
            if !document.supportsThisClient, identity?.isRegistered == true { problem = .upgrade }
            if effectiveTransport != effective {
                // The document added or dropped the WebSocket capability: a change of transport (METRICS 11.9).
                transportChanged()
            } else {
                run?.scheduler.update(limits: document.limits)
                streamDiscovery(document)
            }
            scheduleRefresh(after: Self.refreshInterval)
            confirmInvitedIfCovered(document, service: service)
        case .notSupported, .invalid:
            serviceState = outcome == .invalid ? .invalid : .notSupported
            streamRefreshFailed()
            if isSettingUp { scheduleRefresh(after: Self.refreshInterval) }
        case .gone:
            serviceState = .gone
            if identity?.isRegistered == true || isSettingUp {
                problem = .gone
                updatePreferences { $0.enabled = false }
                endRun()
                endSetup()
            }
        case .failed(let failure):
            serviceState = .failed(failure)
            streamRefreshFailed()
            scheduleRefresh(after: 3600)
        }
    }

    // MARK: - Consent sheet

    /// Opens the consent sheet once the service's document is known; nothing else is requested.
    func beginConsent(review: Bool) {
        guard settings.networkEnabled, let service = selectedService else { return }
        consentWanted = true
        let open: @MainActor (ReportDiscovery) -> Void = { [weak self] document in
            guard let self, self.selectedService == service, self.consentWanted else { return }
            self.consentWanted = false
            let identity = self.identity
            let username = identity?.username ?? self.nextUsername(rules: document.usernameRules)
            self.consent = ConsentDraft(
                service: service, discovery: document, deviceId: identity?.deviceId ?? ReportCanonical.newUUID(),
                username: username, apiKey: "", sections: self.preferences.sections,
                isReview: review && identity?.isRegistered == true)
        }
        if case .ready(let document) = serviceState, !review {
            open(document)
        } else {
            checkService(then: open)
        }
    }

    func cancelConsent() {
        guard let draft = consent else { return }
        consent = nil
        registering = false
        if draftSigner != nil, preferences.identity(for: draft.service)?.isRegistered != true {
            keyStore.deleteAll(deviceId: draft.deviceId)
        }
        draftSigner = nil
        consentWanted = false
        // A cancelled review keeps reporting paused on `report.service.changed` (REPORTING 3.3); a cancelled first
        // consent — after the invitation too — turns reporting off and leaves nothing behind.
        if identity?.isRegistered != true {
            updatePreferences { $0.enabled = false }
            endSetup()
        }
    }

    /// `report.start`: confirms a review, or creates the key (first time only) and registers (REPORTING 4.1).
    func startReporting(transfer: Bool = false) {
        guard var draft = consent, !draft.isStarting else { return }
        if draft.isReview {
            confirm(draft, reply: nil)
            return
        }
        draft.username = ReportUsername.normalized(draft.username)
        draft.usernameError = ReportUsername.validate(draft.username, rules: draft.discovery.usernameRules)
            .map { Self.usernameKey($0, name: draft.username, rules: draft.discovery.usernameRules) }
        draft.apiKeyError = apiKeyProblem(draft.apiKey, mode: draft.discovery.apiKeyMode)
        draft.failure = nil
        draft.offersTransfer = false
        guard draft.usernameError == nil, draft.apiKeyError == nil, settings.networkEnabled else {
            consent = draft
            return
        }
        let key: any ReportSigner
        do {
            key = try draftSigner ?? keyStore.createKey(deviceId: draft.deviceId)
            draftSigner = key
            if !draft.apiKey.isEmpty { try keyStore.saveAPIKey(draft.apiKey, deviceId: draft.deviceId) }
        } catch {
            draft.failure = ReportFailure.key.key
            consent = draft
            return
        }
        draft.isStarting = true
        consent = draft
        registering = true
        let api = self.api
        let body = ReportDocuments.registration(deviceId: draft.deviceId, username: draft.username,
                                                publicKey: key.publicKey, client: client, transfer: transfer)
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: draft.deviceId,
                                           apiKey: draft.apiKey.isEmpty ? nil : draft.apiKey)
        let url = draft.discovery.api.appending(endpoint: ReportEndpoint.devices)
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "POST", url: url, body: body,
                                             signer: key, timestamp: timestamp)
            self?.finishConsent(attempt, deviceId: draft.deviceId)
        }
    }

    private func finishConsent(_ attempt: Attempt, deviceId: String) {
        registering = false
        guard var draft = consent, draft.deviceId == deviceId else { return }
        draft.isStarting = false
        guard case .reply(let result) = attempt else {
            draft.failure = ReportFailure.key.key
            consent = draft
            return
        }
        switch result {
        case .failure(let error):
            draft.failure = error.failure.key
        case .success(let reply) where reply.result == "created" || reply.result == "exists":
            confirm(draft, reply: reply)
            return
        case .success(let reply):
            switch reply.code {
            case "username_taken":
                draft.usernameError = "report.username.taken"
                draft.offersTransfer = draft.discovery.apiKeyMode != .none && !draft.apiKey.isEmpty
            case "username_invalid":
                draft.usernameError = reply.reason == "reserved"
                    ? "report.username.invalid.reserved"
                    : Self.usernameKey(ReportUsernameProblem(rawValue: reply.reason ?? "") ?? .characters,
                                       name: draft.username, rules: draft.discovery.usernameRules)
            case "api_key_required", "api_key_invalid":
                draft.apiKeyError = "report.status.apiKey"
            case "gone":
                serviceState = .gone
                draft.failure = "report.status.gone"
            default:
                draft.failure = ReportFailure.unavailable.key
            }
        }
        consent = draft
    }

    /// Stores what the user confirmed and turns reporting on.
    private func confirm(_ draft: ConsentDraft, reply: ReportReply?) {
        let now = Date()
        var identity = preferences.identity(for: draft.service) ?? ReportIdentity(
            service: draft.service, deviceId: draft.deviceId, keyCreatedAt: now, lastUsedAt: now)
        if let reply {
            identity.username = reply.username ?? draft.username
            identity.profileURL = reply.profileURL ?? identity.profileURL
            identity.hasAPIKey = !draft.apiKey.isEmpty
            identity.keyCreatedAt = now
        }
        identity.pin = ReportConsentPin(draft.discovery)
        identity.discovery = draft.discovery
        identity.discoveryFetchedAt = now
        identity.lastUsedAt = now
        var dropped: [String] = []
        updatePreferences { preferences in
            dropped = preferences.upsert(identity)
            preferences.sections = draft.sections
            preferences.enabled = true
            // Turning reporting on in Settings answers the dashboard invitation too.
            if preferences.invite == nil { preferences.invite = .accepted }
        }
        for deviceId in dropped { keyStore.deleteAll(deviceId: deviceId) }
        consent = nil
        draftSigner = nil
        problem = nil
        clearSetupMessages()
        strikes = 0
        serviceState = .ready(draft.discovery)
        endRun()
        loadSecrets()
        tick()
    }

    // MARK: - Username

    /// The pane's username field, committed: renames when registered (REPORTING 4.4); otherwise keeps the name for the
    /// next registration (DESIGN 11, Username).
    func commitUsername(_ text: String) {
        let name = ReportUsername.normalized(text)
        guard let identity, identity.isRegistered, let rules = identity.discovery?.usernameRules else {
            storeNextUsername(name)
            return
        }
        guard name.unicodeScalars.elementsEqual((identity.username ?? "").unicodeScalars) == false else { return }
        if let problem = ReportUsername.validate(name, rules: rules) {
            renameState = .failed(Self.usernameKey(problem, name: name, rules: rules))
            return
        }
        clearRejection()
        rename(to: name, automatic: false)
    }

    /// Not registered yet: the name waits locally for the next registration — no request — and a setup that waits
    /// for a new name continues with it.
    func storeNextUsername(_ name: String) {
        let rules = usernameRules
        if let problem = ReportUsername.validate(name, rules: rules) {
            renameState = .failed(Self.usernameKey(problem, name: name, rules: rules))
            return
        }
        renameState = .idle
        let typed: String? = name == defaultUsername(rules: rules) ? nil : name
        updatePreferences { $0.username = typed }
        guard isSettingUp else { return }
        if problem == .usernameTaken || problem == .usernameRejected { problem = nil }
        if case .username = inviteGap { inviteGap = nil }
        continueSetup()
    }

    func rename(to name: String, automatic: Bool) {
        guard let identity, let document = identity.discovery, let key = signer else { return }
        renameState = .working
        let api = self.api
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: identity.deviceId, apiKey: apiKey)
        let url = document.api.appending(endpoint: ReportEndpoint.device(identity.deviceId))
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        let origin = identity.id
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "PATCH", url: url,
                                             body: ReportDocuments.rename(username: name), signer: key, timestamp: timestamp)
            self?.finishRename(attempt, name: name, origin: origin)
        }
    }

    private func finishRename(_ attempt: Attempt, name: String, origin: String) {
        guard case .reply(.success(let reply)) = attempt else {
            switch attempt {
            case .reply(.failure(let error)): renameState = .failed(error.failure.key)
            case .signingFailed: renameState = .failed(ReportFailure.key.key)
            case .reply(.success): break
            }
            return
        }
        if reply.result == "updated" || reply.result == "unchanged" {
            updateIdentity(origin) {
                $0.username = reply.username ?? name
                if let profile = reply.profileURL { $0.profileURL = profile }
            }
            if problem == .usernameTaken { problem = nil }
            renameState = .updated
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
                if self?.renameState == .updated { self?.renameState = .idle }
            }
            return
        }
        switch reply.code {
        case "username_taken": renameState = .failed("report.username.taken")
        case "username_invalid" where reply.reason == "reserved": renameState = .failed("report.username.invalid.reserved")
        case "username_invalid":
            let rules = identity?.discovery?.usernameRules ?? .standard
            renameState = .failed(Self.usernameKey(ReportUsernameProblem(rawValue: reply.reason ?? "") ?? .characters,
                                                   name: name, rules: rules))
        default:
            renameState = .failed(ReportFailure.unavailable.key)
        }
    }

    /// The localized key for a username problem (DESIGN 11, Username).
    static func usernameKey(_ problem: ReportUsernameProblem, name: String, rules: ReportUsernameRules) -> String {
        switch problem {
        case .length: "report.username.invalid.length"
        case .characters: rules.charset == .ascii ? "report.username.invalid.ascii" : "report.username.invalid.characters"
        case .start: "report.username.invalid.start"
        case .spaces: !rules.spaces && name.contains(" ") ? "report.username.invalid.noSpaces" : "report.username.invalid.spaces"
        }
    }

    // MARK: - Registration checks

    /// `unknown_device` (register again) and `signature_invalid` (re-check this key) of REPORTING 8.3.
    func reRegister(recheck: Bool, username: String? = nil) {
        guard let identity, let document = identity.discovery, let key = signer,
              let name = username ?? identity.username else { return }
        registering = true
        let api = self.api
        let body = ReportDocuments.registration(deviceId: identity.deviceId, username: name, publicKey: key.publicKey,
                                                client: client)
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: identity.deviceId, apiKey: apiKey)
        let url = document.api.appending(endpoint: ReportEndpoint.devices)
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        let origin = identity.id
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "POST", url: url, body: body,
                                             signer: key, timestamp: timestamp)
            self?.finishReRegister(attempt, name: name, origin: origin)
        }
    }

    private func finishReRegister(_ attempt: Attempt, name: String, origin: String) {
        registering = false
        // Only a device that is not registered yet waits on this answer; its passing failures are retried hourly.
        let settingUp = preferences.identities.first { $0.id == origin }?.isRegistered == false
        let reply: ReportReply
        switch attempt {
        case .signingFailed:
            if settingUp { registrationFailure = .key }
            return
        case .reply(.failure(let error)):
            if settingUp {
                registrationFailure = error.failure
                scheduleSetupRetry()
            }
            return
        case .reply(.success(let value)):
            reply = value
        }
        switch reply.result ?? reply.code {
        case "created", "exists":
            updateIdentity(origin) {
                $0.username = reply.username ?? name
                if let profile = reply.profileURL { $0.profileURL = profile }
            }
            registrationFailure = nil
            if let problem, [.usernameTaken, .usernameRejected, .keyMismatch, .apiKey].contains(problem) { self.problem = nil }
        case "username_taken":
            problem = .usernameTaken
        case "username_invalid":
            problem = .usernameRejected
        case "device_conflict":
            problem = .keyMismatch
        case "api_key_required", "api_key_invalid":
            problem = .apiKey
        case "forbidden":
            problem = .forbidden
        case "gone":
            problem = .gone
            updatePreferences { $0.enabled = false }
            endRun()
        default:
            if settingUp {
                registrationFailure = .unavailable
                scheduleSetupRetry()
            }
        }
    }

    // MARK: - API key

    /// 8–256 printable ASCII characters without spaces (REPORTING 5.6); nil when acceptable.
    func apiKeyProblem(_ key: String, mode: ReportAPIKeyMode) -> String? {
        if key.isEmpty { return mode == .required ? "report.apiKey.required" : nil }
        let bytes = Array(key.utf8)
        return (8...256).contains(bytes.count) && bytes.allSatisfy({ (0x21...0x7E).contains($0) }) ? nil : "report.status.apiKey"
    }

    func setAPIKey(_ key: String) -> Bool {
        guard let identity, let mode = identity.discovery?.apiKeyMode, !key.isEmpty, apiKeyProblem(key, mode: mode) == nil,
              (try? keyStore.saveAPIKey(key, deviceId: identity.deviceId)) != nil else { return false }
        apiKey = key
        updateIdentity(identity.id) { $0.hasAPIKey = true }
        if problem == .apiKey { problem = nil }
        clearRejection()
        continueSetup()
        return true
    }

    func clearAPIKey() {
        guard let identity else { return }
        keyStore.deleteAPIKey(deviceId: identity.deviceId)
        apiKey = nil
        updateIdentity(identity.id) { $0.hasAPIKey = false }
    }

    // MARK: - Forget, reset, rotate

    /// `report.forget` (REPORTING 4.6) for the selected identity or another one.
    func forget(_ origin: String? = nil) {
        guard let target = preferences.identities.first(where: { $0.id == (origin ?? identity?.id) }),
              let document = target.discovery, settings.networkEnabled,
              let key = target.id == identity?.id ? signer : keyStore.loadKey(deviceId: target.deviceId) else {
            forgetState = .failed(settings.networkEnabled ? .key : .unreachable)
            return
        }
        if target.id == identity?.id { endRun() }
        forgetState = .working
        let api = self.api
        let targetKey = target.hasAPIKey ? (target.id == identity?.id ? apiKey : keyStore.loadAPIKey(deviceId: target.deviceId)) : nil
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: target.deviceId, apiKey: targetKey)
        let url = document.api.appending(endpoint: ReportEndpoint.device(target.deviceId))
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "DELETE", url: url, body: Data(),
                                             signer: key, timestamp: timestamp)
            self?.finishForget(attempt, origin: target.id)
        }
    }

    private func finishForget(_ attempt: Attempt, origin: String) {
        switch attempt {
        case .reply(.success(let reply)) where reply.result == "deleted" || reply.status == 404:
            forgetLocally(origin)
            forgetState = .done
            DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [weak self] in
                if self?.forgetState == .done { self?.forgetState = .idle }
            }
        case .reply(.success):
            forgetState = .failed(.unavailable)
        case .reply(.failure(let error)):
            forgetState = .failed(error.failure)
        case .signingFailed:
            forgetState = .failed(.key)
        }
    }

    /// Deletes the key and the device id here only (`report.forget.localOnly`).
    func forgetLocally(_ origin: String? = nil) {
        profile.reset()
        guard let target = preferences.identities.first(where: { $0.id == (origin ?? identity?.id) }) else { return }
        let selected = target.id == identity?.id
        if selected { endRun() }
        keyStore.deleteAll(deviceId: target.deviceId)
        updatePreferences { preferences in
            preferences.remove(target.id)
            if selected { preferences.enabled = false }
        }
        if selected {
            problem = nil
            lastDeliveryAt = nil
            rank = nil
            loadSecrets()
        }
    }

    func dismissForgetResult() {
        forgetState = .idle
    }

    /// `report.reset` after a key mismatch: a new key and device id, registered again with the same name.
    func resetDevice() {
        profile.reset()
        guard let old = identity, let name = old.username ?? consent?.username else { return }
        endRun()
        keyStore.deleteAll(deviceId: old.deviceId)
        let deviceId = ReportCanonical.newUUID()
        guard let key = try? keyStore.createKey(deviceId: deviceId) else { return }
        var fresh = old
        fresh.deviceId = deviceId
        fresh.username = nil
        fresh.pendingKey = false
        fresh.hasAPIKey = false
        fresh.keyCreatedAt = Date()
        if let apiKey, (try? keyStore.saveAPIKey(apiKey, deviceId: deviceId)) != nil { fresh.hasAPIKey = true }
        updatePreferences { _ = $0.upsert(fresh) }
        signer = key
        pendingSigner = nil
        problem = nil
        reRegister(recheck: false, username: name)
    }

    /// Rotates a key older than 365 days (REPORTING 4.5); the new key stays pending until the service confirms it.
    func rotateKeyIfDue(force: Bool = false) {
        guard !rotating, let identity, identity.isRegistered, let document = identity.discovery, let key = signer,
              force || Date().timeIntervalSince(identity.keyCreatedAt) > Self.rotationAge else { return }
        let pending: any ReportSigner
        if let existing = pendingSigner {
            pending = existing
        } else {
            guard let created = try? keyStore.createKey(deviceId: identity.deviceId, slot: .pending) else { return }
            pending = created
            pendingSigner = created
            updateIdentity(identity.id) { $0.pendingKey = true }
        }
        rotating = true
        let api = self.api
        let builder = ReportRequestBuilder(userAgent: userAgent, deviceId: identity.deviceId, apiKey: apiKey)
        let url = document.api.appending(endpoint: ReportEndpoint.key(identity.deviceId))
        let timestamp = ReportClock.unixMs / 1000 + clockCorrection
        Task { [weak self] in
            let attempt = await Self.perform(api: api, builder: builder, method: "PUT", url: url,
                                             body: ReportDocuments.rotation(publicKey: pending.publicKey), signer: key,
                                             timestamp: timestamp, newKey: pending)
            self?.finishRotation(attempt)
        }
    }

    private func finishRotation(_ attempt: Attempt) {
        rotating = false
        guard case .reply(.success(let reply)) = attempt else { return }
        if reply.result == "rotated" || reply.result == "unchanged" {
            promotePendingKey()
        } else if reply.code == "device_conflict", let identity {
            keyStore.deletePending(deviceId: identity.deviceId)
            pendingSigner = nil
            updateIdentity(identity.id) { $0.pendingKey = false }
        }
    }

    func promotePendingKey() {
        guard let identity, let pending = pendingSigner else { return }
        guard (try? keyStore.promotePending(deviceId: identity.deviceId)) != nil else { return }
        signer = pending
        pendingSigner = nil
        updateIdentity(identity.id) {
            $0.pendingKey = false
            $0.keyCreatedAt = Date()
        }
    }
}
