import Foundation
import Testing
@testable import AgentSpeedCore

/// DESIGN 12, *Number formats* and *Log scale*: the full and compact forms from the rounded integer, and the meters'
/// log scale.
@Suite("Performance number formats")
struct PerfFormatTests {
    @Test func theFullFormGroupsUpTo99999ThenTurnsCompact() {
        let cases: [(Int, String)] = [(0, "0"), (986, "986"), (1286, "1,286"), (9999, "9,999"), (12_840, "12,840"),
                                      (99_999, "99,999"), (100_000, "100k"), (128_400, "128k"), (1_284_000, "1.28M")]
        for (value, text) in cases { #expect(PerfFormat.full(value) == text, "\(value)") }
    }

    @Test func theCompactFormKeepsThreeSignificantDigitsWithTheSuffixOfTheRoundedValue() {
        let cases: [(Int, String)] = [(0, "0"), (986, "986"), (1286, "1,286"), (9999, "9,999"), (10_000, "10.0k"),
                                      (12_840, "12.8k"), (12_850, "12.9k"), (99_949, "99.9k"), (99_960, "100k"),
                                      (128_400, "128k"), (999_499, "999k"), (999_500, "1.00M"), (1_284_000, "1.28M"),
                                      (12_840_000, "12.8M"), (128_400_000, "128M")]
        for (value, text) in cases { #expect(PerfFormat.compact(value) == text, "\(value)") }
    }

    @Test func belowAMillionNoCompactTextIsLongerThanTheMeasureText() {
        for value in stride(from: 0, to: 1_000_000, by: 37) {
            #expect(PerfFormat.compact(value).count <= PerfFormat.measureText.count, "\(value)")
        }
    }

    @Test func theLogScaleIsHalfAtTheReference() {
        #expect(PerfFormat.scale(250) == 0 && PerfFormat.scale(500) == 0.25 && PerfFormat.scale(1000) == 0.5)
        #expect(PerfFormat.scale(2000) == 0.75 && PerfFormat.scale(4000) == 1 && PerfFormat.scale(16_000) == 1)
        #expect(PerfFormat.scale(100) == 0 && PerfFormat.scale(0) == 0 && PerfFormat.scale(-5) == 0)
        #expect(PerfFormat.scale(.infinity) == 0 && PerfFormat.scale(.nan) == 0)
    }
}
