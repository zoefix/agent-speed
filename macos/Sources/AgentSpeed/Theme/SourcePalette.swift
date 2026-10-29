import AgentSpeedCore
import SwiftUI

extension SourceKind {
    var tint: RGBColor {
        switch self {
        case .codex: RGBColor(hex: 0x34D399)
        case .claudeCode: RGBColor(hex: 0xFF8A65)
        case .claudeDesktop: RGBColor(hex: 0xB39DFF)
        case .deepseek: RGBColor(hex: 0x4D6BFE)
        case .opencode: RGBColor(hex: 0xF4B13E)
        case .gemini: RGBColor(hex: 0x34B8F0)
        case .omp: RGBColor(hex: 0x84CC16)
        case .antigravity: RGBColor(hex: 0xEC4899)
        case .workbuddy: RGBColor(hex: 0x11A68B)
        case .workbuddyAi: RGBColor(hex: 0x007AB4)
        case .zcode: RGBColor(hex: 0x466BFF)
        case .kimi: RGBColor(hex: 0xE16F8A)
        case .grokBuild: RGBColor(hex: 0x8290A6)
        case .pi: RGBColor(hex: 0xA855F7)
        case .openclaw: RGBColor(hex: 0xDE2E1F)
        case .hermes: RGBColor(hex: 0xB8860B)
        case .droid: RGBColor(hex: 0x9E5F46)
        case .trae: RGBColor(hex: 0x40E0B0)
        }
    }

    /// Source color adjusted for legibility on light surfaces.
    func tint(dark: Bool) -> Color {
        dark ? tint.color : tint.darkened(0.14).color
    }
}
