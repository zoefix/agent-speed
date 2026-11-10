#include "agentspeed/quota/routes/requests.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "vendor_internal.hpp"

// The request chains: each runs on in the transport's completions and ends by queueing one result.
namespace agentspeed::quota::fetch {

namespace {

using Outcome = Cadence::Outcome;

struct Body {
    std::string text;
    json::Document doc;
};

struct Answer {
    Outcome outcome = Outcome::Failure;
    int status = 0;
    std::shared_ptr<Body> body;  // a 2xx JSON object
    std::optional<std::int64_t> dateMs;
    std::int64_t receivedMs = 0;
    bool noCertificates = false;
};

// 2xx with a JSON object is a success, 401/403 a refusal, anything else (a redirect, the cap, a timeout) a failure.
Answer classify(http::Response response, std::int64_t fallbackMs) {
    Answer answer;
    answer.status = response.status;
    answer.dateMs = response.dateMs;
    answer.receivedMs = response.receivedMs > 0 ? response.receivedMs : fallbackMs;
    if (response.failure != http::Failure::None) {
        answer.noCertificates = response.failure == http::Failure::NoCertificates;
        answer.status = 0;
        return answer;
    }
    if (response.status >= 200 && response.status < 300) {
        auto body = std::make_shared<Body>();
        body->text = std::move(response.body);
        if (body->text.size() <= http::kBodyCap && body->doc.parse(body->text) && body->doc.root().isObject()) {
            answer.outcome = Outcome::Success;
            answer.body = std::move(body);
        }
    } else if (response.status == 401 || response.status == 403) {
        answer.outcome = Outcome::Denied;
    }
    return answer;
}

// Sends one request; a request that breaks NETWORK 9 is never handed to the transport and fails at once.
void ask(const Context& context, http::Request request, std::int64_t fallbackMs, std::function<void(Answer)> next) {
    if (context.inbox.expired()) return;
    if (!http::permitted(request)) {
        next(Answer{});
        return;
    }
    context.transport->send(std::move(request), [inbox = context.inbox, fallbackMs, next = std::move(next)](http::Response response) {
        if (inbox.expired()) return;
        next(classify(std::move(response), fallbackMs));
    });
}

void deliver(const Context& context, VendorService::FetchResult result) {
    auto inbox = context.inbox.lock();
    if (!inbox) return;
    std::function<void()> wake;
    {
        std::lock_guard lock(inbox->mutex);
        inbox->results.push_back(std::move(result));
        wake = inbox->wake;
    }
    if (wake) wake();
}

void finish(VendorService::FetchResult& base, const Answer& answer) {
    base.outcome = answer.outcome;
    base.status = answer.status;
    base.noCertificates = answer.noCertificates;
}

class GeminiChain : public std::enable_shared_from_this<GeminiChain> {
public:
    GeminiChain(Context context, VendorService::FetchResult base, std::string token, std::optional<std::optional<std::string>> project)
        : context_(std::move(context)), base_(std::move(base)), token_(std::move(token)), project_(std::move(project)) {}

    void start() {
        if (project_) {
            quota();
            return;
        }
        auto self = shared_from_this();
        ask(context_, requests::geminiLoad(token_.value()), base_.startedMs, [self](Answer answer) { self->loaded(std::move(answer)); });
    }

private:
    void loaded(Answer answer) {
        if (answer.outcome != Outcome::Success) {
            finish(base_, answer);
            deliver(context_, std::move(base_));
            return;
        }
        project_ = vendors::geminiProject(answer.body->doc.root());
        base_.geminiProject = project_;
        quota();
    }

    void quota() {
        auto self = shared_from_this();
        ask(context_, requests::geminiQuota(token_.value(), *project_), base_.startedMs,
            [self](Answer answer) { self->answered(std::move(answer)); });
    }

    void answered(Answer answer) {
        finish(base_, answer);
        if (answer.outcome == Outcome::Success) {
            base_.windows = vendors::geminiWindows(answer.body->doc.root());
            base_.observedMs = answer.dateMs.value_or(answer.receivedMs);
        }
        deliver(context_, std::move(base_));
    }

    Context context_;
    VendorService::FetchResult base_;
    Secret token_;
    std::optional<std::optional<std::string>> project_;
};

class AntigravityChain : public std::enable_shared_from_this<AntigravityChain> {
public:
    AntigravityChain(Context context, VendorService::FetchResult base, std::vector<AntigravityServer> servers)
        : context_(std::move(context)), base_(std::move(base)), servers_(std::move(servers)) {}
    ~AntigravityChain() {
        for (auto& server : servers_) {
            for (auto& endpoint : server.endpoints) {
                if (endpoint.token) wipe(*endpoint.token);
            }
        }
    }
    AntigravityChain(const AntigravityChain&) = delete;
    AntigravityChain& operator=(const AntigravityChain&) = delete;

    void next() {
        while (server_ < servers_.size()) {
            const auto& endpoints = servers_[server_].endpoints;
            if (endpoint_ >= endpoints.size()) {
                ++server_;
                endpoint_ = 0;
                continue;
            }
            const auto& endpoint = endpoints[endpoint_];
            auto request = requests::antigravity(requests::AntigravityCall::Summary, endpoint.scheme, endpoint.port, endpoint.token);
            if (!request) {
                ++endpoint_;
                continue;
            }
            auto self = shared_from_this();
            ask(context_, std::move(*request), base_.startedMs, [self](Answer answer) { self->summary(std::move(answer)); });
            return;
        }
        base_.outcome = denied_ ? Outcome::Denied : Outcome::Failure;
        deliver(context_, std::move(base_));
    }

private:
    void summary(Answer answer) {
        const auto& endpoint = servers_[server_].endpoints[endpoint_];
        if (answer.status == 404) {
            if (auto request = requests::antigravity(requests::AntigravityCall::Status, endpoint.scheme, endpoint.port, endpoint.token)) {
                auto self = shared_from_this();
                ask(context_, std::move(*request), base_.startedMs,
                    [self](Answer status) { self->answered(std::move(status), requests::AntigravityCall::Status); });
                return;
            }
        }
        answered(std::move(answer), requests::AntigravityCall::Summary);
    }

    void answered(Answer answer, requests::AntigravityCall call) {
        const auto& server = servers_[server_];
        const auto& endpoint = server.endpoints[endpoint_];
        base_.status = answer.status;
        if (answer.outcome == Outcome::Denied) {
            denied_ = true;
            if (endpoint.token) base_.forgetTokens.insert(server.pid);
        }
        if (answer.outcome != Outcome::Success) {
            ++endpoint_;
            next();
            return;
        }
        const auto root = answer.body->doc.root();
        base_.outcome = Outcome::Success;
        base_.windows = call == requests::AntigravityCall::Summary ? vendors::antigravitySummaryWindows(root)
                                                                   : vendors::antigravityStatusWindows(root);
        base_.observedMs = answer.dateMs.value_or(answer.receivedMs);
        deliver(context_, std::move(base_));
    }

    Context context_;
    VendorService::FetchResult base_;
    std::vector<AntigravityServer> servers_;
    std::size_t server_ = 0;
    std::size_t endpoint_ = 0;
    bool denied_ = false;
};

}  // namespace

void single(const Context& context, VendorService::FetchResult base, http::Request request, Parse parse) {
    const auto fallback = base.startedMs;
    ask(context, std::move(request), fallback,
        [context, base = std::move(base), parse = std::move(parse)](Answer answer) mutable {
            finish(base, answer);
            if (answer.outcome == Outcome::Success) {
                if (auto windows = parse(answer.body->doc.root(), answer.receivedMs)) {
                    base.windows = std::move(*windows);
                    base.observedMs = answer.dateMs.value_or(answer.receivedMs);
                } else {
                    base.outcome = Outcome::Failure;
                }
            }
            deliver(context, std::move(base));
        });
}

void gemini(const Context& context, VendorService::FetchResult base, std::string token,
            std::optional<std::optional<std::string>> project) {
    std::make_shared<GeminiChain>(context, std::move(base), std::move(token), std::move(project))->start();
}

void antigravity(const Context& context, VendorService::FetchResult base, std::vector<AntigravityServer> servers) {
    std::make_shared<AntigravityChain>(context, std::move(base), std::move(servers))->next();
}

}  // namespace agentspeed::quota::fetch
