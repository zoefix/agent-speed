import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Trae long-running log parsing")
struct TraeLogPerformanceTests {
    @Test func retryDetectionPreservesWordBoundariesAndCase() {
        for text in ["retry", "RETRY request", "request retried", "Reconnecting stream", "error: retry!",
                     "request \n retried", "重试 retry 请求", "é retry 😀"] {
            #expect(TraeTiming.isRetryEvent(text), "must reject unsafe timing: \(text)")
        }
        for text in ["enable_retry_after_content_loop", "retrying", "myretry", "retry_count", "preretried",
                     "can_reconnect", "éretry", "重试retry请求", "ordinary response"] {
            #expect(!TraeTiming.isRetryEvent(text), "must retain safe timing: \(text)")
        }
    }

    @Test func largeMessagesKeepRetryEvidenceAtTheEnd() {
        // Native log messages can include large response/tool payloads. The expensive path was a no-match
        // scan over Unicode text, not a short marker; also ensure an optimization never truncates the scan.
        let payload = String(repeating: "普通内容 abcdefghijklmnop ", count: 25_000)
        #expect(!TraeTiming.isRetryEvent(payload))
        #expect(TraeTiming.isRetryEvent(payload + " request RETRIED"))
        #expect(TraeTiming.isRetryEvent(payload + " reconnecting"))
    }

    @Test func unicodeConnectorPunctuationDoesNotHideUnsafeMarkers() {
        for boundary in ["\u{203f}", "\u{2040}", "\u{2054}", "\u{fe33}", "\u{fe4d}", "\u{ff3f}"] {
            #expect(TraeTiming.isRetryEvent(boundary + "retry"))
            #expect(TraeTiming.isRetryEvent("retried" + boundary))
            #expect(TraeTiming.isRetryEvent(boundary + "reconnecting"))
        }
    }
}
