import AppKit
import SwiftUI

@main
struct NavTuneApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    private let state = AppState.shared

    var body: some Scene {
        Window("NavTune", id: "main") {
            MainView()
                .environment(state)
        }
        .defaultSize(width: 980, height: 700)
        .defaultLaunchBehavior(state.hasOnboarded ? .suppressed : .presented)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        MenuBarExtra {
            MenuBarView()
                .environment(state)
        } label: {
            MenuBarIcon()
                .environment(state)
        }
        .menuBarExtraStyle(.window)
    }
}

private struct MenuBarIcon: View {
    @Environment(AppState.self) private var state
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: state.isOn ? "slider.vertical.3" : "speaker.slash")
            .onAppear {
                state.openMainWindow = { [openWindow] in
                    openWindow(id: "main")
                    NSApp.activate()
                }
            }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        AppState.shared.applyActivationPolicy()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    /// Opening NavTune again from Finder or Launchpad while it runs in the menu bar shows its window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        AppState.shared.openMainWindow?()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        AppState.shared.shutdown()
    }
}
