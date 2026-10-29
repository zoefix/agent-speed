#include <algorithm>
#include <set>

#include "failures.hpp"
#include "internal.hpp"
#include "pcre.hpp"

// The recognizers of test runners, compilers and checkers (spec/TASK-PARSERS.md 5.3).
namespace agentspeed::tasks::detail {

namespace {

const rx::Regex kPytestFail{R"re((?:\[gw[0-9]+\] \[ *[0-9]+%\] )?(FAILED|ERROR) (?!\()([^ ]+?)(?: - (.*))?)re", rx::kFull};
const rx::Regex kPytestVerbose{R"re(([^ ]+::.+?) (FAILED|ERROR)(?: +\[ *[0-9]+%\])?)re", rx::kFull};
const rx::Regex kPytestSummary{R"re((?:=+ )?((?:[0-9]+ (?:failed|passed|errors?|skipped|xfailed|xpassed|deselected|warnings?|rerun|reruns|subtests passed|subtests failed)(?:, )?)+) in [0-9]+(?:\.[0-9]+)?s(?: \([0-9:.]+\))?(?: =+)?)re", rx::kFull};
const rx::Regex kPytestNone{R"re((?:=+ )?no tests ran in [0-9]+(?:\.[0-9]+)?s(?: =+)?)re", rx::kFull};
const rx::Regex kUnittestFail{R"re((FAIL|ERROR|UNEXPECTED SUCCESS): ([A-Za-z_][A-Za-z0-9_]*) \(([A-Za-z0-9_.]+)\)(?: .*)?)re", rx::kFull};
const rx::Regex kUnittestRan{R"re(Ran ([0-9]+) tests? in [0-9]+(?:\.[0-9]+)?s)re", rx::kFull};
const rx::Regex kUnittestOk{R"re(OK(?: \((.*)\))?)re", rx::kFull};
const rx::Regex kUnittestFailed{R"re(FAILED \((.*)\))re", rx::kFull};
const rx::Regex kLibtestLine{R"re(test (.+) \.\.\. (FAILED|ok|ignored(?:, .*)?|bench: .*))re", rx::kFull};
const rx::Regex kLibtestStdout{R"re(---- (.+) (?:stdout|stderr) ----)re", rx::kFull};
const rx::Regex kLibtestResult{R"re(test result: (?:ok|FAILED)\. ([0-9]+) passed; ([0-9]+) failed; ([0-9]+) ignored; ([0-9]+) measured; ([0-9]+) filtered out(?:; finished in .*)?)re", rx::kFull};
const rx::Regex kLibtestFailures{R"re(failures:)re", rx::kFull};
const rx::Regex kLibtestListed{R"re(    ([^ ].*))re", rx::kFull};
const rx::Regex kNextestFail{R"re( *(?:FAIL|SIGSEGV|SIGABRT|SIGKILL|SIGBUS|TIMEOUT|LEAK-FAIL|ABORT) \[ *[0-9.]+m?s\] (?:\( *[0-9]+/[0-9]+\) )?([^ ]+) (.+))re", rx::kFull};
const rx::Regex kNextestOther{R"re( *(?:PASS|SKIP|LEAK|SLOW|FLAKY|START|RETRY|TRY [0-9]+ [A-Z-]+) \[.*)re", rx::kFull};
const rx::Regex kNextestSummary{R"re( *Summary \[[^\]]*\] +([0-9]+) tests? run: (.*))re", rx::kFull};
const rx::Regex kDoctestLine{R"re(\(line [0-9]+\))re"};
const rx::Regex kRustHeader{R"re(error(?:\[(E[0-9]{4})\])?: (.+))re", rx::kFull};
const rx::Regex kRustWarn{R"re(warning(?:\[[A-Za-z0-9_:]+\])?: .+)re", rx::kFull};
const rx::Regex kRustLoc{R"re( *--> (.+?):([0-9]+):([0-9]+))re", rx::kFull};
const rx::Regex kRustCouldNot{R"re(error: could not compile `[^`]+`(?: \([^)]*\))?(?:;.*)?(?: due to ([0-9]+) previous errors?| due to previous error)?(?:;.*)?)re", rx::kFull};
const rx::Regex kRustAborting{R"re(error: aborting due to (?:([0-9]+) )?previous errors?(?:;.*)?)re", rx::kFull};
const rx::Regex kRustStatus{R"re(error: (?:test failed|build failed|failed to run|[0-9]+ targets? failed|process didn't exit successfully|Recipe ).*)re", rx::kFull};
const rx::Regex kStIssue{R"re([^ \tA-Za-z0-9]+ Test (.+?) recorded an issue.*)re", rx::kFull};
const rx::Regex kStFailed{R"re([^ \tA-Za-z0-9]+ Test (.+?) failed after [0-9]+(?:\.[0-9]+)? seconds(?: with [0-9]+ issues?)?(?: \(including [0-9]+ known issues?\))?\.)re", rx::kFull};
const rx::Regex kStOther{R"re([^ \tA-Za-z0-9]+ (?:Test .+? (?:passed after .*|started\.|skipped.*)|Suite .+? (?:passed|failed|started).*|Test run started\.|Testing Library Version: .*|Target Platform: .*))re", rx::kFull};
const rx::Regex kStRun{R"re([^ \tA-Za-z0-9]+ Test run with ([0-9]+) tests?(?: in [0-9]+ suites?)? (passed|failed) after [0-9]+(?:\.[0-9]+)? seconds(?: with ([0-9]+) issues?)?(?: \(including [0-9]+ known issues?\))?\.)re", rx::kFull};
const rx::Regex kStCases{R"re( with [0-9]+ test cases?$)re"};
const rx::Regex kXcCase{R"re(Test [Cc]ase '(-\[[^\]]+\]|[^']+)' (failed|passed|started|skipped)(?: on '[^']*')?(?: \([0-9.]+ seconds\))?\.?)re", rx::kFull};
const rx::Regex kXcError{R"re((.+?):([0-9]+): error: (-\[[^\]]+\]|[A-Za-z0-9_.()/]+) : .*)re", rx::kFull};
const rx::Regex kXcSuite{R"re(Test [Ss]uite '.+' (?:started|passed|failed).*)re", rx::kFull};
const rx::Regex kXcExecuted{R"re([ \t]*Executed ([0-9]+) tests?, with (?:([0-9]+) tests? skipped and )?([0-9]+) failures? \(([0-9]+) unexpected\) in [0-9.]+ \([0-9.]+\) seconds)re", rx::kFull};
const rx::Regex kXbFailing{R"re(Failing tests:)re", rx::kFull};
const rx::Regex kXbListed{R"re([ \t]+(.+?)(?: \(.*\))?)re", rx::kFull};
const rx::Regex kXbResult{R"re(\*\* (?:TEST|BUILD|ANALYZE|ARCHIVE|CLEAN|TEST BUILD|INSTALL) (?:FAILED|SUCCEEDED|INTERRUPTED) \*\*)re", rx::kFull};
const rx::Regex kXbCommands{R"re(The following build commands failed:)re", rx::kFull};
const rx::Regex kXbCount{R"re(\(([0-9]+) failures?\))re", rx::kFull};
const rx::Regex kSwiftcError{R"re((.+\.swift):([0-9]+):([0-9]+): error: (.+))re", rx::kFull};
const rx::Regex kSwiftcWarn{R"re((.+\.swift):([0-9]+):([0-9]+): (?:warning|note): .+)re", rx::kFull};
const rx::Regex kSwiftNoise{R"re(error: (?:emit-module command failed|fatalError|compile command failed|link command failed).*)re", rx::kFull};
const rx::Regex kGoFail{R"re([ \t]*--- FAIL: ([^ ]+) \([0-9.]+s\))re", rx::kFull};
const rx::Regex kGoPass{R"re([ \t]*--- (PASS|SKIP): ([^ ]+) \([0-9.]+s\))re", rx::kFull};
const rx::Regex kGoRun{R"re(=== (?:RUN|PAUSE|CONT|NAME) .*)re", rx::kFull};
const rx::Regex kGoPkg{R"re((FAIL|ok)[ \t]+([^ \t]+)[ \t]+(?:[0-9.]+s|\(cached\)|\[([^\]]+)\])(?:[ \t].*)?)re", rx::kFull};
const rx::Regex kGoBare{R"re((?:FAIL|PASS|\?[ \t]+[^ \t]+[ \t]+\[no test files\]))re", rx::kFull};
const rx::Regex kGoError{R"re(([^ \t][^ \t:]*\.go):([0-9]+)(?::([0-9]+))?: (.+))re", rx::kFull};
const rx::Regex kGoHeader{R"re(# [^ ]+(?: \[[^\]]+\])?)re", rx::kFull};
const rx::Regex kJestFile{R"re( ?(FAIL|PASS) +([^ ]+)(?: \([0-9.]+ ?m?s\))?)re", rx::kFull};
const rx::Regex kJestBullet{R"re(  ● (.+))re", rx::kFull};
const rx::Regex kJestTests{R"re(Tests:[ \t]+(.*[0-9]+ total))re", rx::kFull};
const rx::Regex kJestSuites{R"re(Test Suites:[ \t]+(.*[0-9]+ total))re", rx::kFull};
const rx::Regex kJestNoise{R"re((?:Snapshots:|Time:|Ran all test suites).*|[ \t]+[✓✕○✎] .*|Summary of all failing tests)re", rx::kFull};
const rx::Regex kVitestFail{R"re( *FAIL +(.+?) > (.+?)(?: \[[^\]]*\])?)re", rx::kFull};
const rx::Regex kVitestLoad{R"re( *FAIL +([^ ]+) \[ (.+) \])re", rx::kFull};
const rx::Regex kVitestFile{R"re( *[❯>✓] ([^ ]+) \([0-9]+ tests?(?: \|.*)?\)(?: [0-9.]+m?s)?)re", rx::kFull};
const rx::Regex kVitestX{R"re( +[×✗] (.+?)(?: [0-9.]+m?s)?)re", rx::kFull};
const rx::Regex kVitestTests{R"re( *Tests +(.+?) \(([0-9]+)\))re", rx::kFull};
const rx::Regex kVitestFiles{R"re( *Test Files +(.+?) \(([0-9]+)\))re", rx::kFull};
const rx::Regex kMochaPassing{R"re([ \t]*([0-9]+) passing \([0-9a-z.]+\))re", rx::kFull};
const rx::Regex kMochaFailing{R"re([ \t]*([0-9]+) failing)re", rx::kFull};
const rx::Regex kMochaPending{R"re([ \t]*([0-9]+) pending)re", rx::kFull};
const rx::Regex kMochaItem{R"re(([ \t]*)([0-9]+)\) (.*))re", rx::kFull};
const rx::Regex kBunFail{R"re(\(fail\) (.+?)(?: \[[0-9.]+m?s\])?)re", rx::kFull};
const rx::Regex kBunCount{R"re( *([0-9]+) (pass|fail))re", rx::kFull};
const rx::Regex kDenoSummary{R"re((?:ok|FAILED) \| ([0-9]+) passed(?: \([0-9]+ steps?\))? \| ([0-9]+) failed(?: \([0-9]+ steps?\))?.*)re", rx::kFull};
const rx::Regex kDenoFailure{R"re((.+?) => (.+?):[0-9]+:[0-9]+)re", rx::kFull};
const rx::Regex kTscParen{R"re((.+?)\(([0-9]+),([0-9]+)\): error (TS[0-9]+): (.*))re", rx::kFull};
const rx::Regex kTscPretty{R"re((.+?):([0-9]+):([0-9]+) - error (TS[0-9]+): (.*))re", rx::kFull};
const rx::Regex kTscFound{R"re(Found ([0-9]+) errors? in .*)re", rx::kFull};
const rx::Regex kFoundErrors{R"re(Found ([0-9]+) errors?(?: \(([0-9]+) fixed, ([0-9]+) remaining\))?\.)re", rx::kFull};
const rx::Regex kTscTable{R"re(Errors +Files)re", rx::kFull};
const rx::Regex kTscRow{R"re([ \t]+[0-9]+[ \t]+.+:[0-9]+)re", rx::kFull};
const rx::Regex kEslintFile{R"re((/[^:]*|[A-Za-z]:[\\/][^:]*))re", rx::kFull};
const rx::Regex kEslintMsg{R"re([ \t]+([0-9]+):([0-9]+)[ \t]+(error|warning)[ \t]+(.+?)(?:[ \t]{2,}([^ \t]+))?)re", rx::kFull};
const rx::Regex kEslintUnix{R"re((.+?):([0-9]+):([0-9]+): (.+) \[(Error|Warning)/(.+)\])re", rx::kFull};
const rx::Regex kEslintSummary{R"re(✖ ([0-9]+) problems? \(([0-9]+) errors?, ([0-9]+) warnings?\))re", rx::kFull};
const rx::Regex kEslintFixable{R"re([ \t]+[0-9]+ errors? and [0-9]+ warnings? potentially fixable.*)re", rx::kFull};
const rx::Regex kRuffConcise{R"re((.+?\.(?:py|pyi|ipynb)):([0-9]+):([0-9]+): ([A-Z]+[0-9]+|SyntaxError|invalid-syntax):? (?:\[\*\] )?(.*))re", rx::kFull};
const rx::Regex kRuffHeader{R"re(([A-Z]{1,4}[0-9]{1,4}|invalid-syntax|SyntaxError):? (?:\[\*\] )?(.+))re", rx::kFull};
const rx::Regex kRuffLoc{R"re( *--> (.+?):([0-9]+):([0-9]+))re", rx::kFull};
const rx::Regex kRuffPassed{R"re(All checks passed!)re", rx::kFull};
const rx::Regex kRuffWould{R"re(Would reformat: (.+))re", rx::kFull};
const rx::Regex kBlackWould{R"re(would reformat (.+))re", rx::kFull};
const rx::Regex kFormatSummary{R"re((?:Oh no! .*)?([0-9]+) files? would be reformatted(?:, [0-9]+ files? (?:already formatted|would be left unchanged))?\.?)re", rx::kFull};
const rx::Regex kMypyError{R"re((.+?\.pyi?):([0-9]+)(?::([0-9]+))?: error: (.+?)(?:  \[([a-z0-9-]+)\])?)re", rx::kFull};
const rx::Regex kMypyNote{R"re((.+?\.pyi?):([0-9]+)(?::([0-9]+))?: note: .*)re", rx::kFull};
const rx::Regex kMypyFound{R"re(Found ([0-9]+) errors? in [0-9]+ files? \((?:checked [0-9]+ source files?|errors prevented further checking)\))re", rx::kFull};
const rx::Regex kMypySuccess{R"re(Success: no issues found in [0-9]+ source files?)re", rx::kFull};
const rx::Regex kPyrightError{R"re([ \t]*(.+?):([0-9]+):([0-9]+) - error: (.+?)(?: \((report[A-Za-z]+)\))?)re", rx::kFull};
const rx::Regex kPyrightRule{R"re( \((report[A-Za-z]+)\)$)re"};
const rx::Regex kPyrightOther{R"re([ \t]*(.+?):([0-9]+):([0-9]+) - (?:warning|information|info|note): .*)re", rx::kFull};
const rx::Regex kPyrightSummary{R"re(([0-9]+) errors?, ([0-9]+) warnings?, ([0-9]+) (?:informations?|infos?|notes?) ?)re", rx::kFull};
const rx::Regex kMsbError{R"re([ \t]*(.+?)\(([0-9]+)(?:,([0-9]+))?(?:,[0-9]+,[0-9]+)?\): error ([A-Za-z]+[0-9]+): (.*?)(?: \[([^\]]+)\])?)re", rx::kFull};
const rx::Regex kMsbToolError{R"re([ \t]*([A-Za-z][A-Za-z0-9_.-]*) : error ([A-Za-z]+[0-9]+): (.*?)(?: \[([^\]]+)\])?)re", rx::kFull};
const rx::Regex kMsbWarning{R"re([ \t]*.+?(?:\([0-9,]+\))? : warning [A-Za-z]+[0-9]+: .*|[ \t]*.+?\([0-9,]+\): warning [A-Za-z]+[0-9]+: .*)re", rx::kFull};
const rx::Regex kMsbCount{R"re([ \t]*([0-9]+) Error\(s\))re", rx::kFull};
const rx::Regex kMsbNoise{R"re([ \t]*[0-9]+ Warning\(s\)|Build FAILED\.|Build succeeded\.|Time Elapsed .*)re", rx::kFull};
const rx::Regex kMsbTerminal{R"re(Build (?:failed|succeeded)(?: with ([0-9]+) error\(s\)(?: and [0-9]+ warning\(s\))?)?(?: and [0-9]+ warning\(s\))? in [0-9.]+s)re", rx::kFull};
const rx::Regex kVstestFailed{R"re([ \t]*Failed ([^ ]+) \[[^\]]*\])re", rx::kFull};
const rx::Regex kVstestPassed{R"re([ \t]*(?:Passed|Skipped) ([^ ]+) \[[^\]]*\])re", rx::kFull};
const rx::Regex kVstestSummary{R"re((?:Failed|Passed)![ \t]*-[ \t]*Failed:[ \t]*([0-9]+),[ \t]*Passed:[ \t]*([0-9]+),[ \t]*Skipped:[ \t]*([0-9]+),[ \t]*Total:[ \t]*([0-9]+).*)re", rx::kFull};
const rx::Regex kMtpSummary{R"re([ \t]*Test summary: total: ([0-9]+), failed: ([0-9]+), succeeded: ([0-9]+), skipped: ([0-9]+).*)re", rx::kFull};
const rx::Regex kMtpFailed{R"re([ \t]*failed (.+?) \([0-9.]+m?s\))re", rx::kFull};
const rx::Regex kGradleTest{R"re(([^ ]+) > (.+?) (FAILED|PASSED|SKIPPED))re", rx::kFull};
const rx::Regex kGradleSummary{R"re(([0-9]+) tests? completed, ([0-9]+) failed(?:, ([0-9]+) skipped)?)re", rx::kFull};
const rx::Regex kGradleNoise{R"re(> Task [^ ]+(?: [A-Z-]+)?|FAILURE: Build failed with an exception\.|BUILD (?:SUCCESSFUL|FAILED) in .*|\* What went wrong:|\* Try:)re", rx::kFull};
const rx::Regex kSurefireTest{R"re(\[ERROR\] ([A-Za-z0-9_.$]+)\.([A-Za-z0-9_$\[\]]+)(?:\(.*\))? -- Time elapsed: .* <<< (?:FAILURE|ERROR)!)re", rx::kFull};
const rx::Regex kSurefireOld{R"re((?:\[ERROR\] )?([A-Za-z0-9_$]+)\(([A-Za-z0-9_.$]+)\)[ \t]+Time elapsed: .* <<< (?:FAILURE|ERROR)!)re", rx::kFull};
const rx::Regex kSurefireHeader{R"re(\[ERROR\] (?:Failures|Errors): ?)re", rx::kFull};
const rx::Regex kSurefireListed{R"re(\[ERROR\][ \t]+([A-Za-z0-9_$]+)\.([A-Za-z0-9_$\[\]]+)(?::[0-9]+)?(?:[ \t].*)?)re", rx::kFull};
const rx::Regex kSurefireRun{R"re(\[(?:ERROR|WARNING|INFO)\] Tests run: ([0-9]+), Failures: ([0-9]+), Errors: ([0-9]+), Skipped: ([0-9]+)(?:, Flakes: [0-9]+)?(.*))re", rx::kFull};
const rx::Regex kMavenJavac{R"re(\[ERROR\] (.+?\.(?:java|kt|scala)):\[([0-9]+),([0-9]+)\] (.+))re", rx::kFull};
const rx::Regex kMavenErrors{R"re(\[INFO\] ([0-9]+) errors?[ \t]*)re", rx::kFull};
const rx::Regex kMavenNoise{R"re(\[ERROR\] COMPILATION ERROR :?|\[INFO\] BUILD (?:FAILURE|SUCCESS))re", rx::kFull};
const rx::Regex kJavacError{R"re((.+?\.java):([0-9]+): error: (.+))re", rx::kFull};
const rx::Regex kJavacWarning{R"re((.+?\.java):([0-9]+): warning: .*)re", rx::kFull};
const rx::Regex kJavacCount{R"re(([0-9]+) errors?)re", rx::kFull};
const rx::Regex kKotlinError{R"re(e: (?:file://)?(.+?\.kts?):([0-9]+):([0-9]+) (.+))re", rx::kFull};
const rx::Regex kKotlinErrorOld{R"re(e: (.+?\.kts?): \(([0-9]+), ([0-9]+)\): (.+))re", rx::kFull};
const rx::Regex kKotlinWarning{R"re(w: .*)re", rx::kFull};
const rx::Regex kRspecFailed{R"re(rspec (?:\./)?(.+?)(?::[0-9]+|\[[0-9:]+\]) # (.*))re", rx::kFull};
const rx::Regex kRspecSummary{R"re(([0-9]+) examples?, ([0-9]+) failures?(?:, ([0-9]+) pending)?(?:, ([0-9]+) errors? occurred outside of examples)?)re", rx::kFull};
const rx::Regex kRspecLoad{R"re(An error occurred while loading (?:\./)?(.+?)\.?)re", rx::kFull};
const rx::Regex kPhpunitSection{R"re(There (?:was|were) [0-9]+ ([a-z][a-z ]*?):)re", rx::kFull};
const rx::Regex kPhpunitItem{R"re([0-9]+\) ([A-Za-z0-9_\\]+)::([A-Za-z0-9_]+)(?: with data set (#[0-9]+|\"[^\"]*\"))?(?: \(.*\))?)re", rx::kFull};
const rx::Regex kPhpunitTests{R"re(Tests: ([0-9]+), Assertions: [0-9]+((?:, [A-Za-z ]+: [0-9]+)*)\.)re", rx::kFull};
const rx::Regex kPhpunitOk{R"re(OK \(([0-9]+) tests?, [0-9]+ assertions?\))re", rx::kFull};
const rx::Regex kPhpunitNoise{R"re(FAILURES!|ERRORS!|OK, but (?:there were issues|incomplete, skipped, or risky tests)!)re", rx::kFull};
const rx::Regex kCtestLine{R"re( *[0-9]+/[0-9]+ Test +#[0-9]+: (.+?) \.*(?: +|\*\*\*)(Passed|Failed|Exception|Timeout|Not Run|Skipped)(?:[: ].*)?)re", rx::kFull};
const rx::Regex kCtestListHeader{R"re(The following tests (?:FAILED|did not run):)re", rx::kFull};
const rx::Regex kCtestListed{R"re([ \t]+[0-9]+ - (.+?) \((.+)\))re", rx::kFull};
const rx::Regex kCtestSummary{R"re(([0-9]+)% tests passed, ([0-9]+) tests? failed out of ([0-9]+))re", rx::kFull};
const rx::Regex kCtestNoise{R"re( *Start +[0-9]+: .*|Test project .*|Errors while running CTest|Total Test time .*)re", rx::kFull};
const rx::Regex kCcError{R"re((.+?\.(?:c|cc|cpp|cxx|c\+\+|C|h|hh|hpp|hxx|h\+\+|m|mm|cu|cuh|ino|S|s|ipp|tpp|inl)):([0-9]+)(?::([0-9]+))?: (?:fatal )?error: (.+?)(?: \[([^\]]+)\])?)re", rx::kFull};
const rx::Regex kCcWarning{R"re((.+?\.(?:c|cc|cpp|cxx|c\+\+|C|h|hh|hpp|hxx|h\+\+|m|mm|cu|cuh|ino|S|s|ipp|tpp|inl)):([0-9]+)(?::([0-9]+))?: (?:warning|note): .*)re", rx::kFull};
const rx::Regex kCcContext{R"re(In file included from .*|[ \t]+from .*|.+?: In (?:function|member function|constructor|destructor|instantiation of|static member function) .*:)re", rx::kFull};
const rx::Regex kCcGenerated{R"re(([0-9]+) errors? generated\.)re", rx::kFull};
const rx::Regex kLdUndefinedArch{R"re(Undefined symbols for architecture [^ ]+:)re", rx::kFull};
const rx::Regex kLdSymbol{R"re([ \t]+\"(.+)\", referenced from:)re", rx::kFull};
const rx::Regex kLdReference{R"re(undefined reference to [`'\x{2018}](.+?)['\x{2019}])re"};
const rx::Regex kLdNoise{R"re((?:[^ ]*/)?ld(?:\.[a-z]+)?: .*|collect2: error: ld returned [0-9]+ exit status|clang(?:\+\+)?: error: linker command failed.*|[ \t]+(?:_?[A-Za-z0-9_]+(?:\(.*\))? in .+\.o|in function .*))re", rx::kFull};
const rx::Regex kMakeNoise{R"re(g?make(?:\[[0-9]+\])?: \*\*\* .*|g?make(?:\[[0-9]+\])?: (?:Entering|Leaving) directory .*)re", rx::kFull};
const rx::Regex kUnittestCount{R"re([ \t]*([a-z ]+)=([0-9]+)[ \t]*)re", rx::kFull};
const rx::Regex kNextestNote{R"re( \([^)]*\))re"};
const rx::Regex kPhpunitCount{R"re(([A-Za-z ]+): ([0-9]+))re", rx::kFull};

std::size_t indentOf(const std::string& line, std::string_view chars) {
    const auto at = line.find_first_not_of(chars);
    return at == std::string::npos ? line.size() : at;
}

bool indented(const std::string& line) { return startsWith(line, " ") || startsWith(line, "\t"); }

class Pytest final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        std::string nodeid;
        if (const auto r = kPytestFail.fullmatch(line)) {
            nodeid = r.group(2);
        } else if (const auto v = kPytestVerbose.fullmatch(line)) {
            nodeid = v.group(1);
        }
        if (!nodeid.empty()) {
            const auto at = nodeid.find("::");
            if (at == std::string::npos) {
                f.test("pytest", "", nodeid);
            } else {
                f.test("pytest", nodeid.substr(at + 2), nodeid.substr(0, at));
            }
            return 1;
        }
        if (const auto r = kPytestSummary.fullmatch(line)) {
            const auto c = parseCounts(r.group(1));
            f.summary("pytest", "test", countOf(c, "failed") + countOf(c, "error") + countOf(c, "errors"), countOf(c, "passed"));
            return 1;
        }
        if (kPytestNone.fullmatch(line)) {
            f.summary("pytest", "test", 0, 0);
            return 1;
        }
        return 0;
    }
};

class Unittest final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (const auto r = kUnittestFail.fullmatch(line)) {
            const auto name = r.group(2), where = r.group(3);
            f.test("unittest", endsWith(where, "." + name) ? where : where + "." + name);
            return 1;
        }
        if (const auto r = kUnittestRan.fullmatch(line)) {
            ran_ = num(r.group(1));
            return 1;
        }
        if (ran_) {
            auto r = kUnittestOk.fullmatch(line);
            if (!r) r = kUnittestFailed.fullmatch(line);
            if (r) {
                Counts c;
                for (const auto& part : split(r.group(1), ',')) {
                    if (const auto kv = kUnittestCount.fullmatch(part)) c[kv.group(1)] = num(kv.group(2));
                }
                const auto failed = countOf(c, "failures") + countOf(c, "errors") + countOf(c, "unexpected successes");
                const auto passed = std::max<std::int64_t>(0, *ran_ - failed - countOf(c, "skipped") - countOf(c, "expected failures"));
                f.summary("unittest", "test", failed, passed);
                ran_.reset();
                return 1;
            }
        }
        return 0;
    }

private:
    std::optional<std::int64_t> ran_;
};

std::string libtestName(const std::string& name) { return kDoctestLine.sub(name, "(line N)"); }

class Cargo final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (listing_) {
            const auto r = kLibtestListed.fullmatch(line);
            if (r && !contains(splitOn(r.group(1), " - ").front(), " ")) {
                f.test("cargo", libtestName(r.group(1)));
                return 1;
            }
            listing_ = false;
        }
        if (const auto r = kLibtestLine.fullmatch(line)) {
            if (r.group(2) == "FAILED") f.test("cargo", libtestName(r.group(1)));
            return 1;
        }
        if (const auto r = kLibtestStdout.fullmatch(line)) {
            f.test("cargo", libtestName(r.group(1)));
            return 1;
        }
        if (const auto r = kLibtestResult.fullmatch(line)) {
            f.summary("cargo", "test", num(r.group(2)), num(r.group(1)), true);
            return 1;
        }
        if (kLibtestFailures.fullmatch(line) && i + 1 < lines.size() && kLibtestListed.fullmatch(lines[i + 1])) {
            listing_ = true;
            return 1;
        }
        if (const auto r = kNextestFail.fullmatch(line)) {
            f.test("cargo", libtestName(r.group(2)));
            return 1;
        }
        if (kNextestOther.fullmatch(line)) return 1;
        if (const auto r = kNextestSummary.fullmatch(line)) {
            const auto c = parseCounts(kNextestNote.sub(r.group(2), ""));
            const auto failed = countOf(c, "failed") + countOf(c, "timed out") + countOf(c, "exec failed") + countOf(c, "leaky failed");
            f.summary("nextest", "test", failed, countOf(c, "passed"));
            return 1;
        }
        return 0;
    }

private:
    bool listing_ = false;
};

// Index of the `-->` line within the 3 lines after a header, when no other header comes first.
std::optional<std::size_t> rustLocation(const Lines& lines, std::size_t i) {
    for (std::size_t k = i + 1; k < std::min(i + 4, lines.size()); ++k) {
        if (kRustLoc.fullmatch(lines[k])) return k;
        if (kRustHeader.fullmatch(lines[k]) || kRustWarn.fullmatch(lines[k])) return std::nullopt;
    }
    return std::nullopt;
}

class Rustc final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        auto r = kRustCouldNot.fullmatch(line);
        if (!r) r = kRustAborting.fullmatch(line);
        if (r) {
            f.summary("rustc", "error", num(r.has(1) ? r.group(1) : "1"), std::nullopt, true);
            return 1;
        }
        if (kRustStatus.fullmatch(line)) return 1;
        if (const auto h = kRustHeader.fullmatch(line)) {
            const auto k = rustLocation(lines, i);
            if (!k) return 0;
            f.error("rustc", kRustLoc.fullmatch(lines[*k]).group(1), h.group(1), h.group(2));
            return *k - i + 1;
        }
        if (kRustWarn.fullmatch(line)) {
            const auto k = rustLocation(lines, i);
            return k ? *k - i + 1 : 0;
        }
        return 0;
    }
};

std::string xctestId(const std::string& name) {
    auto s = name;
    if (startsWith(s, "-[") && endsWith(s, "]") && s.size() >= 3) s = s.substr(2, s.size() - 3);
    s = replaceAll(replaceAll(replaceAll(s, " ", "."), "/", "."), "()", "");
    Words parts;
    for (auto& p : split(s, '.')) {
        if (!p.empty()) parts.push_back(std::move(p));
    }
    return join(slice(parts, parts.size() >= 2 ? parts.size() - 2 : 0), ".");
}

class Swift final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (listing_) {
            if (const auto r = kXbListed.fullmatch(line)) {
                if (!endsWith(r.group(1), ":")) xctest(r.group(1));
                return 1;
            }
            listing_ = false;
        }
        if (commands_) {
            if (const auto r = kXbCount.fullmatch(line)) {
                f.summary("xcodebuild", "error", num(r.group(1)), std::nullopt);
                commands_ = false;
                return 1;
            }
            if (indented(line)) return 1;
            commands_ = false;
        }
        if (const auto r = kStFailed.fullmatch(line); r && !startsWith(r.group(1), "run with ")) {
            const auto name = kStCases.sub(r.group(1), "");
            stFailed_.insert(name);
            f.test("swift-testing", name);
            return 1;
        }
        if (const auto r = kStRun.fullmatch(line)) {
            const auto total = num(r.group(1));
            std::int64_t failed = 0;
            if (r.group(2) != "passed") {
                failed = stFailed_.empty() ? num(r.has(3) ? r.group(3) : "1") : static_cast<std::int64_t>(stFailed_.size());
            }
            f.summary("swift-testing", "test", failed, std::max<std::int64_t>(0, total - failed));
            return 1;
        }
        if (kStIssue.fullmatch(line) || kStOther.fullmatch(line)) return 1;
        if (const auto r = kXcCase.fullmatch(line)) {
            if (r.group(2) == "failed") xctest(r.group(1));
            return 1;
        }
        if (const auto r = kXcError.fullmatch(line)) {
            xctest(r.group(3));
            return 1;
        }
        if (kXcSuite.fullmatch(line)) return 1;
        if (const auto r = kXcExecuted.fullmatch(line)) {
            const auto total = num(r.group(1)), skipped = num(r.has(2) ? r.group(2) : "0"), failures = num(r.group(3));
            const auto failed = xcFailed_.empty() ? failures : static_cast<std::int64_t>(xcFailed_.size());
            f.summary("xctest", "test", failed, std::max<std::int64_t>(0, total - failed - skipped));
            return 1;
        }
        if (kXbFailing.fullmatch(line)) {
            listing_ = true;
            return 1;
        }
        if (kXbResult.fullmatch(line)) return 1;
        if (kXbCommands.fullmatch(line)) {
            commands_ = true;
            return 1;
        }
        if (const auto r = kSwiftcError.fullmatch(line)) {
            f.error("swiftc", r.group(1), "", r.group(4));
            return 1;
        }
        if (kSwiftcWarn.fullmatch(line) || kSwiftNoise.fullmatch(line)) return 1;
        return 0;
    }

private:
    void xctest(const std::string& name) {
        const auto test = xctestId(name);
        xcFailed_.insert(test);
        f.test("xctest", test);
    }

    std::set<std::string> stFailed_, xcFailed_;
    bool listing_ = false, commands_ = false;
};

class Go final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (const auto r = kGoFail.fullmatch(line)) {
            failed_.insert(r.group(1));
            f.test("go", r.group(1));
            return 1;
        }
        if (const auto r = kGoPass.fullmatch(line)) {
            if (r.group(1) == "PASS") passed_.insert(r.group(2));
            return 1;
        }
        if (kGoRun.fullmatch(line) || kGoBare.fullmatch(line) || kGoHeader.fullmatch(line)) return 1;
        if (const auto r = kGoPkg.fullmatch(line)) {
            ++pkgs_;
            if (r.group(1) == "ok") {
                ++ok_;
            } else if (r.has(3) && in(r.group(3), {"build failed", "setup failed"})) {
                ++broken_;
            }
            return 1;
        }
        if (const auto r = kGoError.fullmatch(line)) {
            f.error("go", r.group(1), "", r.group(4));
            return 1;
        }
        return 0;
    }

    void finish() override {
        if (!pkgs_ && failed_.empty() && passed_.empty()) return;
        std::optional<std::int64_t> passed;
        if (!passed_.empty()) {
            passed = static_cast<std::int64_t>(passed_.size());
        } else if (ok_) {
            passed = ok_;
        }
        f.summary("go", "test", static_cast<std::int64_t>(failed_.size()) + broken_, passed);
    }

private:
    std::set<std::string> failed_, passed_;
    std::int64_t ok_ = 0, pkgs_ = 0, broken_ = 0;
};

class Js final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (mochaDetails_) {
            if (const auto r = kMochaItem.fullmatch(line)) {
                Words parts{r.group(3)};
                std::size_t k = i;
                const auto depth = r.group(1).size();
                while (!endsWith(parts.back(), ":") && k + 1 < lines.size() && !strip(lines[k + 1], kWs).empty() &&
                       indentOf(lines[k + 1], " ") > depth) {
                    ++k;
                    parts.push_back(strip(lines[k], kWs));
                }
                Words stripped;
                for (const auto& p : parts) stripped.push_back(strip(p, kWs));
                const auto title = join(stripped, " ");
                if (endsWith(title, ":")) {
                    f.test("mocha", title.substr(0, title.size() - 1));
                    return k - i + 1;
                }
            }
        }
        if (const auto r = kVitestLoad.fullmatch(line)) {
            f.test("vitest", "", r.group(1));
            return 1;
        }
        if (const auto r = kVitestFail.fullmatch(line)) {
            f.test("vitest", r.group(2), r.group(1));
            return 1;
        }
        if (const auto r = kJestFile.fullmatch(line)) {
            jestFile_ = r.group(1) == "FAIL" ? r.group(2) : "";
            return 1;
        }
        if (const auto r = kJestBullet.fullmatch(line)) {
            if (r.group(1) != "Console") f.test("jest", r.group(1), jestFile_);
            return 1;
        }
        if (const auto r = kJestSuites.fullmatch(line)) {
            flushJest();
            jestSuites_ = parseCounts(r.group(1));
            return 1;
        }
        if (const auto r = kJestTests.fullmatch(line)) {
            jestTests_ = parseCounts(r.group(1));
            flushJest();
            return 1;
        }
        if (kJestNoise.fullmatch(line)) return 1;
        if (const auto r = kVitestFile.fullmatch(line)) {
            vitestFile_ = r.group(1);
            return 1;
        }
        if (const auto r = kVitestX.fullmatch(line); r && !vitestFile_.empty()) {
            f.test("vitest", r.group(1), vitestFile_);
            return 1;
        }
        if (const auto r = kVitestFiles.fullmatch(line)) {
            flushVitest();
            vitestFiles_ = parseCounts(r.group(1), "|");
            return 1;
        }
        if (const auto r = kVitestTests.fullmatch(line)) {
            vitestTests_ = parseCounts(r.group(1), "|");
            flushVitest();
            return 1;
        }
        if (const auto r = kMochaPassing.fullmatch(line)) {
            mochaPassing_ = num(r.group(1));
            return 1;
        }
        if (const auto r = kMochaFailing.fullmatch(line)) {
            mochaFailing_ = num(r.group(1));
            mochaDetails_ = true;
            return 1;
        }
        if (kMochaPending.fullmatch(line)) return 1;
        if (const auto r = kBunFail.fullmatch(line)) {
            f.test("bun", r.group(1));
            return 1;
        }
        if (const auto r = kBunCount.fullmatch(line)) {
            bun_[r.group(2)] = num(r.group(1));
            return 1;
        }
        if (const auto r = kDenoSummary.fullmatch(line)) {
            f.summary("deno", "test", num(r.group(2)), num(r.group(1)));
            return 1;
        }
        if (const auto r = kDenoFailure.fullmatch(line)) {
            f.test("deno", r.group(1), r.group(2));
            return 1;
        }
        return 0;
    }

    void finish() override {
        flushJest();
        flushVitest();
        if (mochaPassing_ || mochaFailing_) f.summary("mocha", "test", mochaFailing_.value_or(0), mochaPassing_.value_or(0));
        if (!bun_.empty()) f.summary("bun", "test", countOf(bun_, "fail"), countOf(bun_, "pass"));
    }

private:
    void flush(const std::string& framework, const std::optional<Counts>& tests, const std::optional<Counts>& files) {
        if (!tests && !files) return;
        auto failed = tests ? countOf(*tests, "failed") : 0;
        if (failed == 0 && files && !files->empty()) failed = countOf(*files, "failed");
        f.summary(framework, "test", failed, tests ? countOf(*tests, "passed") : 0, true);
    }
    void flushJest() {
        flush("jest", jestTests_, jestSuites_);
        jestTests_.reset();
        jestSuites_.reset();
    }
    void flushVitest() {
        flush("vitest", vitestTests_, vitestFiles_);
        vitestTests_.reset();
        vitestFiles_.reset();
    }

    std::string jestFile_, vitestFile_;
    std::optional<Counts> jestTests_, jestSuites_, vitestTests_, vitestFiles_;
    std::optional<std::int64_t> mochaPassing_, mochaFailing_;
    bool mochaDetails_ = false;
    Counts bun_;
};

class Checkers final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (tscTable_) {
            if (kTscRow.fullmatch(line)) return 1;
            tscTable_ = false;
        }
        auto tsc = kTscParen.fullmatch(line);
        if (!tsc) tsc = kTscPretty.fullmatch(line);
        if (tsc) {
            f.error("tsc", tsc.group(1), tsc.group(4), tsc.group(5));
            return 1;
        }
        if (const auto r = kMypyFound.fullmatch(line)) {
            f.summary("mypy", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (const auto r = kTscFound.fullmatch(line)) {
            f.summary("tsc", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (const auto r = kFoundErrors.fullmatch(line)) {
            f.summary("found-errors", "error", num(r.has(3) ? r.group(3) : r.group(1)), std::nullopt);
            return 1;
        }
        if (kTscTable.fullmatch(line)) {
            tscTable_ = true;
            return 1;
        }
        if (const auto r = kEslintUnix.fullmatch(line)) {
            if (r.group(5) == "Error") f.error("eslint", r.group(1), r.group(6), r.group(4));
            return 1;
        }
        if (kEslintFile.fullmatch(line) && i + 1 < lines.size() && kEslintMsg.fullmatch(lines[i + 1])) {
            eslintFile_ = line;
            return 1;
        }
        if (eslintFile_) {
            if (const auto r = kEslintMsg.fullmatch(line)) {
                if (r.group(3) == "error") f.error("eslint", *eslintFile_, r.group(5), r.group(4));
                return 1;
            }
        }
        if (const auto r = kEslintSummary.fullmatch(line)) {
            f.summary("eslint", "error", num(r.group(2)), std::nullopt);
            return 1;
        }
        if (kEslintFixable.fullmatch(line)) return 1;
        if (const auto r = kRuffConcise.fullmatch(line)) {
            f.error("ruff", r.group(1), r.group(4), r.group(5));
            return 1;
        }
        if (const auto r = kRuffHeader.fullmatch(line)) {
            for (std::size_t k = i + 1; k < std::min(i + 4, lines.size()); ++k) {
                if (const auto loc = kRuffLoc.fullmatch(lines[k])) {
                    f.error("ruff", loc.group(1), r.group(1), r.group(2));
                    return k - i + 1;
                }
            }
        }
        if (kRuffPassed.fullmatch(line)) {
            f.summary("ruff", "error", 0, std::nullopt);
            return 1;
        }
        if (const auto r = kRuffWould.fullmatch(line)) {
            f.error("ruff", r.group(1), "format", "");
            return 1;
        }
        if (const auto r = kBlackWould.fullmatch(line)) {
            f.error("black", r.group(1), "format", "");
            return 1;
        }
        if (const auto r = kFormatSummary.fullmatch(line)) {
            f.summary("format", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (const auto r = kMypyError.fullmatch(line)) {
            f.error("mypy", r.group(1), r.group(5), r.group(4));
            return 1;
        }
        if (kMypyNote.fullmatch(line)) return 1;
        if (kMypySuccess.fullmatch(line)) {
            f.summary("mypy", "error", 0, std::nullopt);
            return 1;
        }
        if (const auto r = kPyrightError.fullmatch(line)) {
            const auto indent = indentOf(line, kWs);
            std::size_t k = i;
            auto code = r.group(5);
            while (k + 1 < lines.size() && indentOf(lines[k + 1], kWs) > indent && !kPyrightError.fullmatch(lines[k + 1]) &&
                   !kPyrightOther.fullmatch(lines[k + 1])) {
                ++k;
            }
            if (k > i && code.empty()) code = kPyrightRule.search(lines[k]).group(1);
            f.error("pyright", r.group(1), code, kPyrightRule.sub(r.group(4), ""));
            return k - i + 1;
        }
        if (kPyrightOther.fullmatch(line)) return 1;
        if (const auto r = kPyrightSummary.fullmatch(line)) {
            f.summary("pyright", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        return 0;
    }

private:
    std::optional<std::string> eslintFile_;
    bool tscTable_ = false;
};

class Dotnet final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (const auto r = kMsbError.fullmatch(line)) {
            f.error("msbuild", r.group(1), r.group(4), r.group(5));
            return 1;
        }
        if (const auto r = kMsbToolError.fullmatch(line)) {
            f.error("msbuild", "", r.group(2), r.group(3));
            return 1;
        }
        if (kMsbWarning.fullmatch(line)) return 1;
        if (const auto r = kMsbCount.fullmatch(line)) {
            f.summary("msbuild", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (const auto r = kMsbTerminal.fullmatch(line)) {
            if (!r.group(1).empty()) f.summary("msbuild", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (kMsbNoise.fullmatch(line)) return 1;
        if (const auto r = kVstestFailed.fullmatch(line)) {
            f.test("dotnet", r.group(1));
            return 1;
        }
        if (kVstestPassed.fullmatch(line)) return 1;
        if (const auto r = kVstestSummary.fullmatch(line)) {
            f.summary("dotnet", "test", num(r.group(1)), num(r.group(2)), true);
            return 1;
        }
        if (const auto r = kMtpSummary.fullmatch(line)) {
            f.summary("dotnet-mtp", "test", num(r.group(2)), num(r.group(3)));
            return 1;
        }
        if (const auto r = kMtpFailed.fullmatch(line)) {
            f.test("dotnet", r.group(1));
            return 1;
        }
        return 0;
    }
};

std::string junitId(const std::string& cls, std::string method) {
    if (endsWith(method, "()")) method.resize(method.size() - 2);
    return split(cls, '.').back() + "." + method;
}

class Jvm final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (listing_) {
            if (kSurefireRun.fullmatch(line) || startsWith(line, "[INFO]") || line == "[ERROR] " || line == "[ERROR]") {
                listing_ = false;
            } else {
                const auto r = kSurefireListed.fullmatch(line);
                if (r && !startsWith(line, "[ERROR]   Run ")) {
                    f.test("junit", junitId(r.group(1), r.group(2)));
                    return 1;
                }
                if (startsWith(line, "[ERROR]") || startsWith(line, "[WARNING]")) return 1;
                listing_ = false;
            }
        }
        if (const auto r = kGradleTest.fullmatch(line)) {
            if (r.group(3) == "FAILED") f.test("junit", junitId(r.group(1), r.group(2)));
            return 1;
        }
        if (const auto r = kGradleSummary.fullmatch(line)) {
            const auto total = num(r.group(1)), failed = num(r.group(2)), skipped = num(r.has(3) ? r.group(3) : "0");
            f.summary("gradle", "test", failed, std::max<std::int64_t>(0, total - failed - skipped), true);
            return 1;
        }
        if (kGradleNoise.fullmatch(line)) return 1;
        if (const auto r = kSurefireTest.fullmatch(line)) {
            f.test("junit", junitId(r.group(1), r.group(2)));
            return 1;
        }
        if (const auto r = kSurefireOld.fullmatch(line)) {
            f.test("junit", junitId(r.group(2), r.group(1)));
            return 1;
        }
        if (kSurefireHeader.fullmatch(line)) {
            listing_ = true;
            return 1;
        }
        if (const auto r = kSurefireRun.fullmatch(line)) {
            if (strip(r.group(5), kWs).empty()) {
                const auto run = num(r.group(1)), failures = num(r.group(2)), errors = num(r.group(3)), skipped = num(r.group(4));
                f.summary("maven", "test", failures + errors, std::max<std::int64_t>(0, run - failures - errors - skipped));
            }
            return 1;
        }
        if (const auto r = kMavenJavac.fullmatch(line)) {
            f.error("javac", r.group(1), "", r.group(4));
            return 1;
        }
        if (const auto r = kMavenErrors.fullmatch(line)) {
            f.summary("javac", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        if (kMavenNoise.fullmatch(line)) return 1;
        if (const auto r = kJavacError.fullmatch(line)) {
            f.error("javac", r.group(1), "", r.group(3));
            return 1;
        }
        if (kJavacWarning.fullmatch(line)) return 1;
        if (const auto r = kJavacCount.fullmatch(line)) {
            f.summary("javac", "error", num(r.group(1)), std::nullopt);
            return 1;
        }
        auto k = kKotlinError.fullmatch(line);
        if (!k) k = kKotlinErrorOld.fullmatch(line);
        if (k) {
            f.error("kotlinc", k.group(1), "", k.group(4));
            return 1;
        }
        if (kKotlinWarning.fullmatch(line)) return 1;
        return 0;
    }

private:
    bool listing_ = false;
};

class Misc final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (ctestList_) {
            if (const auto r = kCtestListed.fullmatch(line)) {
                if (*ctestList_ == "FAILED") f.test("ctest", r.group(1));
                return 1;
            }
            ctestList_.reset();
        }
        if (const auto r = kRspecFailed.fullmatch(line)) {
            f.test("rspec", r.group(2), r.group(1));
            return 1;
        }
        if (const auto r = kRspecSummary.fullmatch(line)) {
            const auto examples = num(r.group(1)), failures = num(r.group(2));
            const auto pending = num(r.has(3) ? r.group(3) : "0"), outside = num(r.has(4) ? r.group(4) : "0");
            f.summary("rspec", "test", failures + outside, std::max<std::int64_t>(0, examples - failures - pending));
            return 1;
        }
        if (const auto r = kRspecLoad.fullmatch(line)) {
            f.error("rspec", r.group(1), "load", "");
            return 1;
        }
        if (const auto r = kPhpunitSection.fullmatch(line)) {
            phpSection_ = r.group(1);
            return 1;
        }
        if (const auto r = kPhpunitItem.fullmatch(line)) {
            if (phpSection_ && in(*phpSection_, {"failure", "failures", "error", "errors"})) {
                f.test("phpunit", r.group(1) + "::" + r.group(2) + (r.group(3).empty() ? "" : " " + r.group(3)));
            }
            return 1;
        }
        if (const auto r = kPhpunitTests.fullmatch(line)) {
            Counts c;
            for (const auto& part : splitOn(r.group(2), ", ")) {
                if (const auto kv = kPhpunitCount.fullmatch(part)) c[kv.group(1)] = num(kv.group(2));
            }
            const auto failed = countOf(c, "Errors") + countOf(c, "Failures");
            const auto passed = std::max<std::int64_t>(0, num(r.group(1)) - failed - countOf(c, "Skipped") - countOf(c, "Incomplete"));
            f.summary("phpunit", "test", failed, passed);
            return 1;
        }
        if (const auto r = kPhpunitOk.fullmatch(line)) {
            f.summary("phpunit", "test", 0, num(r.group(1)));
            return 1;
        }
        if (kPhpunitNoise.fullmatch(line)) return 1;
        if (const auto r = kCtestLine.fullmatch(line)) {
            if (in(r.group(2), {"Failed", "Exception", "Timeout"})) f.test("ctest", r.group(1));
            return 1;
        }
        if (kCtestListHeader.fullmatch(line)) {
            ctestList_ = contains(line, "FAILED") ? "FAILED" : "notrun";
            return 1;
        }
        if (const auto r = kCtestSummary.fullmatch(line)) {
            const auto failed = num(r.group(2)), total = num(r.group(3));
            f.summary("ctest", "test", failed, std::max<std::int64_t>(0, total - failed));
            return 1;
        }
        if (kCtestNoise.fullmatch(line)) return 1;
        return 0;
    }

private:
    std::optional<std::string> phpSection_, ctestList_;
};

std::string ccCode(std::string flag) {
    if (startsWith(flag, "-Werror,")) flag.erase(0, 8);
    if (startsWith(flag, "-Werror=")) flag = "-W" + flag.substr(8);
    return flag;
}

class Cc final : public Recognizer {
public:
    using Recognizer::Recognizer;
    std::size_t step(const Lines& lines, std::size_t i) override {
        const auto& line = lines[i];
        if (symbols_) {
            if (const auto r = kLdSymbol.fullmatch(line)) {
                f.error("cc", "", "ld", "undefined symbol " + r.group(1));
                return 1;
            }
            if (indented(line)) return 1;
            symbols_ = false;
        }
        if (const auto r = kCcError.fullmatch(line)) {
            f.error("cc", r.group(1), ccCode(r.group(5)), r.group(4));
            return 1;
        }
        if (kCcWarning.fullmatch(line) || kCcContext.fullmatch(line)) return 1;
        if (const auto r = kCcGenerated.fullmatch(line)) {
            f.summary("cc", "error", num(r.group(1)), std::nullopt, true);
            return 1;
        }
        if (kLdUndefinedArch.fullmatch(line)) {
            symbols_ = true;
            return 1;
        }
        if (const auto r = kLdReference.search(line)) {
            f.error("cc", "", "ld", "undefined reference to " + r.group(1));
            return 1;
        }
        if (kLdNoise.fullmatch(line) || kMakeNoise.fullmatch(line)) return 1;
        return 0;
    }

private:
    bool symbols_ = false;
};

}  // namespace

std::vector<std::unique_ptr<Recognizer>> makeRecognizers(Found& found) {
    std::vector<std::unique_ptr<Recognizer>> all;
    all.push_back(std::make_unique<Pytest>(found));
    all.push_back(std::make_unique<Unittest>(found));
    all.push_back(std::make_unique<Cargo>(found));
    all.push_back(std::make_unique<Rustc>(found));
    all.push_back(std::make_unique<Swift>(found));
    all.push_back(std::make_unique<Go>(found));
    all.push_back(std::make_unique<Js>(found));
    all.push_back(std::make_unique<Checkers>(found));
    all.push_back(std::make_unique<Dotnet>(found));
    all.push_back(std::make_unique<Jvm>(found));
    all.push_back(std::make_unique<Misc>(found));
    all.push_back(std::make_unique<Cc>(found));
    return all;
}

}  // namespace agentspeed::tasks::detail
