import CoreAudio
import Foundation

struct AudioDevice: Identifiable, Hashable {
    let id: AudioDeviceID
    let uid: String
    let name: String
    let transport: UInt32

    init(id: AudioDeviceID) {
        self.id = id
        uid = (try? id.readString(kAudioDevicePropertyDeviceUID)) ?? ""
        name = (try? id.readString(kAudioObjectPropertyName)) ?? tr("Thiết bị không rõ tên", "Unknown device")
        transport = (try? id.read(kAudioDevicePropertyTransportType, initial: UInt32(0))) ?? 0
    }

    var isBluetooth: Bool {
        transport == kAudioDeviceTransportTypeBluetooth || transport == kAudioDeviceTransportTypeBluetoothLE
    }

    var icon: String {
        switch transport {
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE:
            name.localizedCaseInsensitiveContains("airpods") ? "airpods" : "headphones"
        case kAudioDeviceTransportTypeBuiltIn: "hifispeaker"
        case kAudioDeviceTransportTypeHDMI, kAudioDeviceTransportTypeDisplayPort: "tv"
        case kAudioDeviceTransportTypeUSB: "cable.connector"
        case kAudioDeviceTransportTypeAirPlay: "airplayaudio"
        case kAudioDeviceTransportTypeThunderbolt: "bolt.horizontal"
        default: "speaker.wave.2"
        }
    }

    var kind: String {
        switch transport {
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: "Bluetooth"
        case kAudioDeviceTransportTypeBuiltIn: tr("Loa tích hợp", "Built-in")
        case kAudioDeviceTransportTypeHDMI: "HDMI"
        case kAudioDeviceTransportTypeDisplayPort: "DisplayPort"
        case kAudioDeviceTransportTypeUSB: "USB"
        case kAudioDeviceTransportTypeAirPlay: "AirPlay"
        case kAudioDeviceTransportTypeThunderbolt: "Thunderbolt"
        case kAudioDeviceTransportTypeVirtual: tr("Thiết bị ảo", "Virtual")
        case kAudioDeviceTransportTypeAggregate: tr("Thiết bị gộp", "Aggregate")
        default: tr("Khác", "Other")
        }
    }

    static func outputDevices() -> [AudioDevice] {
        AudioObjectID.system.readArray(kAudioHardwarePropertyDevices)
            .filter(\.hasOutputStreams)
            .map(AudioDevice.init)
            .filter { !$0.uid.isEmpty && !$0.uid.hasPrefix(TapChain.aggregatePrefix) }
            .sorted { $0.name.localizedCompare($1.name) == .orderedAscending }
    }
}
