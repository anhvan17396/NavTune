import SwiftUI

struct EqualizerView: View {
    @Environment(AppState.self) private var state
    @State private var savingPreset = false
    @State private var presetName = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                StatusBanner()
                controls

                if state.settings.mode == .graphic {
                    Card {
                        FrequencyGraph(settings: state.settings, sampleRate: state.sampleRate)
                            .frame(height: 200)
                    }
                    Card(title: tr("Bộ chỉnh âm 10 băng", "10-band equalizer"), icon: "slider.vertical.3",
                         trailing: AnyView(ValuePill(text: state.currentPresetName))) {
                        bands
                        footer
                    }
                } else {
                    ParametricEQView()
                    Card { footer }
                }

                Card(title: tr("Preset theo dòng nhạc", "Genre presets"), icon: "music.note.list") {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 8)], spacing: 8) {
                        ForEach(state.allPresets) { preset in
                            PresetChip(preset: preset, selected: state.settings.presetID == preset.id) {
                                state.apply(preset)
                            }
                        }
                    }
                }
            }
            .padding(20)
        }
        .disabled(!state.isOn)
        .alert(tr("Lưu preset", "Save preset"), isPresented: $savingPreset) {
            TextField(tr("Tên preset", "Preset name"), text: $presetName)
            Button(tr("Lưu", "Save")) { state.saveCurrentAsPreset(name: presetName) }
            Button(tr("Huỷ", "Cancel"), role: .cancel) {}
        } message: {
            Text(tr("Lưu đường EQ hiện tại để dùng lại sau.", "Save the current EQ curve for later."))
        }
    }

    private var controls: some View {
        @Bindable var state = state
        return HStack(spacing: 14) {
            Picker("", selection: Binding(get: { state.settings.mode }, set: { state.setMode($0) })) {
                Text(tr("Đồ hoạ 10 băng", "Graphic (10 bands)")).tag(EQMode.graphic)
                Text(tr("Tham số (nâng cao)", "Parametric (advanced)")).tag(EQMode.parametric)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 340)

            Spacer()

            if state.autoPreset {
                HStack(spacing: 6) {
                    Image(systemName: "waveform.badge.magnifyingglass")
                        .symbolEffect(.pulse, isActive: state.detectedContent == nil)
                    Text(state.detectedContent?.title ?? tr("Đang nghe…", "Listening…"))
                }
                .font(.callout)
                .foregroundStyle(.secondary)
                .transition(.opacity)
            }
            Toggle(isOn: $state.autoPreset.animation()) {
                Text(tr("Tự chọn preset theo nội dung", "Auto preset by content"))
            }
            .toggleStyle(.switch)
            .help(tr("NavTune nghe nội dung đang phát (giọng nói, guitar, synth, piano, dàn nhạc…) và tự chọn preset phù hợp. Chạy hoàn toàn trên máy.",
                     "NavTune listens to what's playing (speech, guitar, synth, piano, orchestra…) and picks a matching preset. Runs entirely on your Mac."))
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Text(tr("Âm lượng đầu vào", "Input gain"))
            Slider(value: Binding(get: { Double(state.settings.preamp) },
                                  set: { state.settings.preamp = (Float($0) * 2).rounded() / 2 }),
                   in: -12...6)
                .frame(maxWidth: 220)
            ValuePill(text: Format.dB(state.settings.preamp))
            Spacer()
            Button(tr("Đặt lại", "Reset"), systemImage: "arrow.counterclockwise") { state.resetEQ() }
            Button(tr("Lưu thành preset…", "Save as preset…"), systemImage: "square.and.arrow.down") {
                presetName = ""
                savingPreset = true
            }
        }
    }

    private var bands: some View {
        HStack(alignment: .bottom, spacing: 0) {
            VStack {
                Text("+12")
                Spacer()
                Text("0")
                Spacer()
                Text("-12")
            }
            .font(.system(size: 9))
            .foregroundStyle(.secondary)
            .frame(height: 170)
            .padding(.bottom, 20)

            ForEach(AudioProcessor.frequencies.indices, id: \.self) { band in
                VStack(spacing: 6) {
                    Text(String(format: "%+.1f", state.settings.gains[band]))
                        .font(.system(size: 10).monospacedDigit())
                        .foregroundStyle(state.settings.gains[band] == 0 ? Color.secondary : Brand.accent)
                    VerticalSlider(value: Binding(get: { state.settings.gains[band] },
                                                  set: { state.setGain($0, band: band) }))
                        .frame(height: 170)
                    Text(Format.frequency(AudioProcessor.frequencies[band]))
                        .font(.caption.weight(.medium))
                }
                .frame(maxWidth: .infinity)
            }
        }
    }
}
