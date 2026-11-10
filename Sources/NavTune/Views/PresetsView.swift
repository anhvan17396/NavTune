import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct PresetsView: View {
    @Environment(AppState.self) private var state
    @State private var savingPreset = false
    @State private var presetName = ""
    @State private var message: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Button(tr("Lưu EQ hiện tại…", "Save current EQ…"), systemImage: "plus") {
                    presetName = ""
                    savingPreset = true
                }
                .buttonStyle(.borderedProminent)
                Spacer()
                if let message {
                    Text(message).font(.caption).foregroundStyle(.secondary)
                }
                Button(tr("Nhập…", "Import…"), systemImage: "square.and.arrow.down", action: importPresets)
                Button(tr("Xuất…", "Export…"), systemImage: "square.and.arrow.up", action: exportPresets)
                    .disabled(state.userPresets.isEmpty)
            }
            .padding(14)

            List {
                Section(tr("Preset của tôi", "My presets")) {
                    if state.userPresets.isEmpty {
                        Text(tr("Chưa có preset nào. Chỉnh EQ theo ý anh rồi bấm “Lưu EQ hiện tại…”.",
                                "No presets yet. Shape the EQ the way you like, then click “Save current EQ…”."))
                            .foregroundStyle(.secondary)
                    }
                    ForEach(state.userPresets) { preset in
                        PresetRow(preset: preset, editable: true)
                    }
                }
                Section(tr("Preset có sẵn", "Built-in presets")) {
                    ForEach(Preset.builtIns) { preset in
                        PresetRow(preset: preset, editable: false)
                    }
                }
            }
        }
        .alert(tr("Lưu preset", "Save preset"), isPresented: $savingPreset) {
            TextField(tr("Tên preset", "Preset name"), text: $presetName)
            Button(tr("Lưu", "Save")) { state.saveCurrentAsPreset(name: presetName) }
            Button(tr("Huỷ", "Cancel"), role: .cancel) {}
        }
    }

    private func exportPresets() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "NavTune-presets.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try state.exportPresets(to: url)
            message = tr("Đã xuất \(state.userPresets.count) preset.", "Exported \(state.userPresets.count) presets.")
        } catch {
            message = tr("Không xuất được: ", "Export failed: ") + error.localizedDescription
        }
    }

    private func importPresets() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let count = try state.importPresets(from: url)
            message = tr("Đã nhập \(count) preset.", "Imported \(count) presets.")
        } catch {
            message = tr("File không hợp lệ.", "Invalid file.")
        }
    }
}

private struct PresetRow: View {
    @Environment(AppState.self) private var state
    let preset: Preset
    let editable: Bool

    var body: some View {
        let selected = state.settings.presetID == preset.id
        HStack(spacing: 12) {
            IconBadge(symbol: preset.icon, size: 24)
            if editable {
                TextField(tr("Tên", "Name"), text: Binding(get: { preset.name },
                                                           set: { state.renamePreset(id: preset.id, to: $0) }))
                    .textFieldStyle(.plain)
                if preset.bands != nil {
                    Text(tr("Tham số", "Parametric")).font(.caption2).foregroundStyle(.secondary)
                }
            } else {
                Text(preset.displayName)
            }
            Spacer()
            PresetCurve(gains: preset.gains).frame(width: 90, height: 24)
            if selected {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            }
            Button(tr("Dùng", "Use")) { state.apply(preset) }
                .disabled(selected)
            if editable {
                Button(role: .destructive) {
                    state.deletePreset(id: preset.id)
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help(tr("Xoá preset", "Delete preset"))
            }
        }
        .padding(.vertical, 3)
    }
}
