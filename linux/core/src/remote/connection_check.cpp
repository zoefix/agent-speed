#include "agentspeed/remote_connection_check.hpp"

namespace agentspeed::remote {

namespace {

TestOutcome failed(Status status) {
    TestOutcome out;
    out.failure = std::move(status);
    return out;
}

}  // namespace

std::shared_ptr<ConnectionCheck> ConnectionCheck::start(const Host& host, ConnectionEnvironment environment,
                                                        std::function<void(TestOutcome)> done) {
    auto check = std::shared_ptr<ConnectionCheck>(new ConnectionCheck(std::move(environment), std::move(done)));
    check->launch(host);
    return check;
}

void ConnectionCheck::cancel() {
    done_ = nullptr;
    end();
}

TestOutcome ConnectionCheck::outcome(int exitStatus, std::string_view stdoutText, std::string_view stderrText) {
    if (exitStatus != 0) return failed(classify::ended(exitStatus, stderrText, classify::Stage::Probe));
    const auto result = ProbeResult::parse(stdoutText);
    switch (result.verdict()) {
        case ProbeResult::Verdict::Supported: {
            TestOutcome out;
            out.supported = true;
            out.system = result.system;
            out.arch = result.arch();
            return out;
        }
        case ProbeResult::Verdict::UnsupportedSystem: return failed(Status::unsupportedSystem(result.system));
        case ProbeResult::Verdict::CannotVerify: return failed(Status::of(StatusCode::CannotVerify));
        case ProbeResult::Verdict::Malformed: break;
    }
    return failed(Status::exited(exitStatus));
}

void ConnectionCheck::launch(const Host& host) {
    const auto command = host.connectable() ? command::probe(host.effectiveRelayFolder()) : std::nullopt;
    const auto arguments = command ? ssh::arguments(host.destination, *command, ssh::developmentConfigFile(environment_.processEnvironment))
                                   : std::nullopt;
    if (!arguments) {
        finish(failed(Status::of(StatusCode::InvalidDestination)));
        return;
    }
    if (!environment_.transport->available(std::string(ssh::kExecutable))) {
        finish(failed(Status::of(StatusCode::SshMissing)));
        return;
    }
    // The running process keeps the check alive until it ends.
    auto self = shared_from_this();
    ProcessHandlers handlers;
    handlers.stdoutData = [self](std::string_view data) {
        if (self->stdout_.size() < ProbeResult::kMaxOutputBytes) self->stdout_.append(data.substr(0, ProbeResult::kMaxOutputBytes - self->stdout_.size()));
    };
    handlers.stderrData = [self](std::string_view data) {
        self->stderr_.append(data);
        if (self->stderr_.size() > classify::kMaxStderrBytes) self->stderr_.erase(0, self->stderr_.size() - classify::kMaxStderrBytes);
    };
    handlers.exit = [self](int status) { self->exited(status); };
    process_ = environment_.transport->launch({std::string(ssh::kExecutable), *arguments, ssh::environment(environment_.processEnvironment)},
                                              std::move(handlers));
    if (!process_) {
        finish(failed(Status::exited(127)));
        return;
    }
    process_->closeInput();
    std::weak_ptr<ConnectionCheck> weak = self;
    timer_ = environment_.scheduler->schedule(environment_.scheduler->nowMs() + kTimeLimitMs, [weak] {
        auto check = weak.lock();
        if (!check || !check->process_) return;
        check->timer_.reset();
        check->timedOut_ = true;
        check->end();
    });
}

void ConnectionCheck::exited(int status) {
    process_.reset();
    if (timer_) environment_.scheduler->cancel(*timer_);
    timer_.reset();
    finish(timedOut_ ? failed(Status::of(StatusCode::Unreachable)) : outcome(status, stdout_, stderr_));
}

void ConnectionCheck::end() {
    if (timer_) environment_.scheduler->cancel(*timer_);
    timer_.reset();
    if (!process_) return;
    auto process = process_;
    process->closeInput();
    process->terminate();
    environment_.scheduler->schedule(environment_.scheduler->nowMs() + Connection::kKillDelayMs, [process] { process->kill(); });
}

void ConnectionCheck::finish(const TestOutcome& outcome) {
    auto done = std::move(done_);
    done_ = nullptr;
    if (done) done(outcome);
}

}  // namespace agentspeed::remote
