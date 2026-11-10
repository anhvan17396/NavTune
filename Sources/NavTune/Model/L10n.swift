import Foundation
import Observation

enum AppLanguage: String, CaseIterable, Identifiable {
    case system
    case vietnamese = "vi"
    case english = "en"

    var id: Self { self }

    var title: String {
        switch self {
        case .system: tr("Theo hệ thống", "System")
        case .vietnamese: "Tiếng Việt"
        case .english: "English"
        }
    }
}

/// Current UI language. Observable, so every view that shows text redraws when it changes.
@Observable
final class L10n {
    static let defaultsKey = "language"
    nonisolated(unsafe) static let shared = L10n()

    var isVietnamese: Bool

    private init() {
        isVietnamese = Self.resolve(AppLanguage(rawValue: UserDefaults.standard.string(forKey: Self.defaultsKey) ?? "") ?? .system)
    }

    static func resolve(_ language: AppLanguage) -> Bool {
        switch language {
        case .vietnamese: true
        case .english: false
        case .system: Locale.preferredLanguages.first?.hasPrefix("vi") ?? false
        }
    }
}

/// Picks the Vietnamese or English text for the current app language.
func tr(_ vi: String, _ en: String) -> String {
    L10n.shared.isVietnamese ? vi : en
}
