import os

/// View with: log stream --level info --predicate 'subsystem == "vn.navtools.navtune"'
enum Log {
    static let engine = Logger(subsystem: "vn.navtools.navtune", category: "engine")
}
