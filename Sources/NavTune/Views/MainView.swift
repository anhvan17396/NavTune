import SwiftUI

enum Page: String, CaseIterable, Identifiable {
    case equalizer, effects, apps, presets, devices, settings

    var id: Self { self }

    var title: String {
        switch self {
        case .equalizer: tr("Bộ chỉnh âm", "Equalizer")
        case .effects: tr("Hiệu ứng", "Effects")
        case .apps: tr("Ứng dụng", "Apps")
        case .presets: tr("Preset", "Presets")
        case .devices: tr("Thiết bị", "Devices")
        case .settings: tr("Cài đặt", "Settings")
        }
    }

    var icon: String {
        switch self {
        case .equalizer: "slider.vertical.3"
        case .effects: "sparkles"
        case .apps: "square.stack.3d.up"
        case .presets: "music.note.list"
        case .devices: "headphones"
        case .settings: "gearshape"
        }
    }
}

struct MainView: View {
    @Environment(AppState.self) private var state
    @State private var page: Page? = .equalizer

    var body: some View {
        @Bindable var state = state
        NavigationSplitView {
            List(Page.allCases, selection: $page) { page in
                Label(page.title, systemImage: page.icon).tag(page)
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 210)
            .safeAreaInset(edge: .top) { sidebarHeader }
            .safeAreaInset(edge: .bottom) { sidebarFooter }
        } detail: {
            Group {
                switch page ?? .equalizer {
                case .equalizer: EqualizerView()
                case .effects: EffectsView()
                case .apps: AppsView()
                case .presets: PresetsView()
                case .devices: DevicesView()
                case .settings: SettingsView()
                }
            }
            .navigationTitle((page ?? .equalizer).title)
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                StatusBadge()
                Toggle(isOn: $state.bypass) {
                    Label(tr("Nghe bản gốc", "Original sound"), systemImage: "ear")
                }
                .toggleStyle(.button)
                .help(tr("Nghe bản gốc để so sánh trước / sau (⌘B)", "Hear the original to compare before / after (⌘B)"))
                .keyboardShortcut("b", modifiers: .command)
                .disabled(!state.isOn)
                Toggle(tr("Bật NavTune", "NavTune on"), isOn: $state.isOn)
                    .toggleStyle(.switch)
                    .help(tr("Bật / tắt NavTune", "Turn NavTune on / off"))
            }
        }
        .tint(Brand.accent)
        .frame(minWidth: 920, minHeight: 640)
        .sheet(isPresented: Binding(get: { !state.hasOnboarded }, set: { _ in })) {
            OnboardingView()
                .environment(state)
                .interactiveDismissDisabled()
        }
    }

    private var sidebarHeader: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .frame(width: 34, height: 34)
            VStack(alignment: .leading, spacing: 0) {
                Text("NavTune").font(.headline)
                Text("navtools.vn").font(.caption2).foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
    }

    private var sidebarFooter: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: state.activeDevice?.icon ?? "speaker.slash")
                    .foregroundStyle(Brand.accent)
                VStack(alignment: .leading, spacing: 1) {
                    Text(state.activeDevice?.name ?? tr("Không có thiết bị", "No device"))
                        .font(.caption.weight(.medium))
                        .lineLimit(1)
                    Text(state.currentPresetName).font(.caption2).foregroundStyle(.secondary)
                }
                Spacer()
            }
            LiveLevelMeter().frame(height: 4)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(.background.secondary))
        .padding(10)
    }
}
