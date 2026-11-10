import CoreAudio
import Foundation
import Testing
@testable import NavTune

private func process(_ id: AudioObjectID, owner: String?) -> AudioProcess {
    AudioProcess(id: id, pid: pid_t(id), bundleID: owner.map { $0 + ".helper" } ?? "com.apple.coreaudiod",
                 ownerBundleID: owner, ownerName: owner)
}

private let obs = "com.obsproject.obs-studio"

@Test func mutedAppsLeaveEveryChainAndAreSilenced() {
    let processes = [process(10, owner: "com.google.Chrome"), process(11, owner: "com.google.Chrome"),
                     process(20, owner: obs), process(21, owner: obs),
                     process(30, owner: "us.zoom.xos"),
                     process(40, owner: "com.spotify.client"),
                     process(50, owner: nil)]
    let rules = [AppRule(bundleID: obs, name: "OBS", mode: .mute),
                 AppRule(bundleID: "us.zoom.xos", name: "Zoom", mode: .bypass),
                 AppRule(bundleID: "com.spotify.client", name: "Spotify", mode: .preset, presetID: "edm")]
    let plan = RoutingPlan(rules: rules, processes: processes)
    #expect(plan.muted == [20, 21])
    // Chrome (10, 11) and the system process (50) stay on the main EQ.
    #expect(plan.excluded == [20, 21, 30, 40])
    #expect(plan.chains == ["com.spotify.client": [40]])
}

@Test func rulesForClosedAppsChangeNothing() {
    let plan = RoutingPlan(rules: [AppRule(bundleID: obs, name: "OBS", mode: .mute)],
                           processes: [process(10, owner: "com.google.Chrome")])
    #expect(plan == RoutingPlan(rules: [], processes: []))
}

@Test func unmutingRestoresThePreviousMode() {
    let preset = AppRule(bundleID: "com.spotify.client", name: "Spotify", mode: .preset, presetID: "edm", volume: -3)
    let muted = AppRule.muting(preset, bundleID: preset.bundleID, name: preset.name)
    #expect(muted.mode == .mute)
    #expect(muted.unmuted() == preset)
    // Muting twice must not forget the original mode.
    #expect(AppRule.muting(muted, bundleID: preset.bundleID, name: preset.name).unmuted() == preset)

    let fromMainEQ = AppRule.muting(nil, bundleID: obs, name: "OBS")
    #expect(fromMainEQ.mode == .mute)
    #expect(fromMainEQ.unmuted() == nil)
}

@Test func rulesSavedBeforeMuteExistedStillDecode() throws {
    let json = #"[{"bundleID":"us.zoom.xos","name":"Zoom","mode":"bypass","presetID":"flat","volume":0}]"#
    let rules = try JSONDecoder().decode([AppRule].self, from: Data(json.utf8))
    #expect(rules.first?.mode == .bypass)
    #expect(rules.first?.modeBeforeMute == nil)

    let muted = AppRule.muting(rules[0], bundleID: "us.zoom.xos", name: "Zoom")
    #expect(try JSONDecoder().decode(AppRule.self, from: JSONEncoder().encode(muted)) == muted)
}
