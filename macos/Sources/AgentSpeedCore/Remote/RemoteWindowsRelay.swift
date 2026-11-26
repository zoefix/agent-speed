import Foundation

/// Development opt-in for a manually staged Windows relay. The release installer and the POSIX probe are independent
/// of this path. OpenSSH runs the fixed command on the remote Windows account in batch mode.
enum RemoteWindowsRelay {
    static let hostVariable = "AGENT_SPEED_WINDOWS_RELAY_HOST"
    static let digestVariable = "AGENT_SPEED_WINDOWS_RELAY_SHA256"

    static func command(destination: String, environment: [String: String]) -> String? {
        #if DEBUG
        guard environment[hostVariable] == destination,
              let digest = environment[digestVariable], RemoteCommand.isDigest(digest) else { return nil }
        let script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $root = [Environment]::GetFolderPath('LocalApplicationData')
            if ([string]::IsNullOrEmpty($root)) { exit 126 }
            $relay = Join-Path $root 'AgentSpeed\\relay\\agent-speed-relay.exe'
            try {
                $file = [IO.File]::Open($relay, 'Open', 'Read', 'Read')
            } catch {
                exit 126
            }
            try {
                if ((Get-FileHash -InputStream $file -Algorithm SHA256).Hash -ne '\(digest.lowercased())') { exit 126 }
                & $relay serve
                exit $LASTEXITCODE
            } finally {
                $file.Dispose()
            }
            """
        // PowerShell's EncodedCommand takes UTF-16LE. All data interpolated above is validated hex.
        guard let encoded = script.data(using: .utf16LittleEndian)?.base64EncodedString() else { return nil }
        return "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand \(encoded)"
        #else
        return nil
        #endif
    }
}
