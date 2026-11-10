import SwiftUI

struct DevicesView: View {
    @Environment(AppState.self) private var state

    var body: some View {
        @Bindable var state = state
        Form {
            if state.errorMessage != nil || state.noSignal {
                Section { StatusBanner() }
                    .listRowBackground(Color.clear)
            }

            Section(tr("Phát âm thanh qua", "Play sound through")) {
                Picker(tr("Thiết bị phát", "Output device"), selection: $state.preferredDeviceUID) {
                    Label(tr("Theo thiết bị mặc định của macOS", "Follow the macOS default device"), systemImage: "gearshape")
                        .tag(String?.none)
                    ForEach(state.devices) { device in
                        HStack {
                            Label(device.name, systemImage: device.icon)
                            Text(device.kind).foregroundStyle(.secondary)
                            if state.perDeviceProfiles && state.hasSavedSettings(for: device) {
                                Image(systemName: "bookmark.fill")
                                    .foregroundStyle(.secondary)
                                    .help(tr("Đã lưu cài đặt riêng cho thiết bị này", "Has its own saved settings"))
                            }
                        }
                        .tag(Optional(device.uid))
                    }
                }
                .pickerStyle(.radioGroup)
                .labelsHidden()
            }

            Section(tr("Đang phát", "Now playing")) {
                LabeledContent(tr("Thiết bị", "Device")) {
                    Label(state.activeDevice?.name ?? "—", systemImage: state.activeDevice?.icon ?? "speaker.slash")
                }
                LabeledContent(tr("Kết nối", "Connection"), value: state.activeDevice?.kind ?? "—")
                LabeledContent(tr("Tần số lấy mẫu", "Sample rate"),
                               value: state.sampleRate > 0 ? String(format: "%.1f kHz", state.sampleRate / 1000) : "—")
                LabeledContent(tr("Trạng thái", "Status")) { StatusBadge() }
            }

            Section {
                Toggle(tr("Nhớ cài đặt riêng cho từng thiết bị", "Remember settings per device"), isOn: $state.perDeviceProfiles)
            } footer: {
                Text(tr("Ví dụ: AirPods dùng preset “Tăng Bass”, loa ngoài dùng “Gốc”. NavTune tự đổi khi anh kết nối thiết bị khác.",
                        "For example: AirPods on “Bass Boost”, speakers on “Flat”. NavTune switches automatically when you change devices."))
            }

            if state.activeDevice?.isBluetooth == true {
                Section(tr("Lưu ý về Bluetooth", "About Bluetooth")) {
                    Text(tr("Mac gửi âm thanh qua Bluetooth bằng chuẩn nén AAC hoặc SBC, NavTune không thay đổi được chuẩn này. NavTune chỉnh âm trước khi nén, nên nghe rõ, ấm và chắc hơn, nhưng không khôi phục chi tiết đã mất do nén.",
                            "Macs send Bluetooth audio using the AAC or SBC codec, which NavTune can't change. NavTune shapes the sound before compression, so it sounds clearer, warmer and fuller, but it can't restore detail lost to compression."))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
    }
}
