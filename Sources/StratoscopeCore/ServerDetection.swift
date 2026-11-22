import Foundation

/// A server that answered while looking: a router in front of an engine (answers `/running`) or an engine itself (answers `/metrics`).
public struct DetectedServer: Equatable {
    public var router: String?
    public var url: String?

    public init(router: String? = nil, url: String? = nil) { self.router = router; self.url = url }
}

/// Anything that fetches a JSON object: `JSONFetcher` for real, a fake in tests.
public protocol JSONGetting: AnyObject {
    /// Calls `done` on the main queue with the decoded top-level object, or nil on any failure.
    func get(_ url: String, _ done: @escaping ([String: Any]?) -> Void)
}

extension JSONFetcher: JSONGetting {}

/// Finds the servers of this computer when nobody has said where they are (no settings file, no environment variable): the addresses
/// the user's clients (Kilo, OpenCode) already use, then the usual local ports. Nothing is written, and only this computer and the
/// private network are asked. Same rules as the Windows app.
public enum ServerDetection {
    /// Tried after the clients' own addresses: Strata's own port, then the ports of llama-swap and of the setups of this project.
    public static let usualAddresses = [
        "http://127.0.0.1:8095", "http://127.0.0.1:18080", "http://127.0.0.1:8080", "http://127.0.0.1:9000",
    ]

    /// Every server that answers. A router in front of an engine is the server to watch (it knows the model name and the loading
    /// state), so engines are only listed when no router answers. `done` runs once, on the queue the fetcher answers on (the main queue).
    public static func find(fetcher: JSONGetting, clients: ClientConfigReader?, done: @escaping ([DetectedServer]) -> Void) {
        let addresses = candidates(clients: clients)
        guard !addresses.isEmpty else { done([]); return }
        var results = [DetectedServer?](repeating: nil, count: addresses.count)
        var pending = addresses.count
        for (index, address) in addresses.enumerated() {
            probe(fetcher: fetcher, address: address) { server in
                results[index] = server
                pending -= 1
                guard pending == 0 else { return }
                let found = results.compactMap { $0 }
                let routers = found.filter { $0.router != nil }
                done(routers.isEmpty ? found : routers)
            }
        }
    }

    /// The addresses to ask, once each: what the clients use first, then the usual ones. Only local and private-network addresses.
    public static func candidates(clients: ClientConfigReader?) -> [String] {
        let fromClients = clients?.limits().map(\.baseURL) ?? []
        var seen = Set<String>()
        var addresses: [String] = []
        for raw in fromClients + usualAddresses {
            guard let parts = URLComponents(string: raw), let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
                  let host = parts.host, isLocalNetwork(host: host) else { continue }
            // "http://127.0.0.1:18080/v1" is the server at "http://127.0.0.1:18080".
            var origin = scheme + "://" + (host.contains(":") ? "[\(host)]" : host)
            if let port = parts.port { origin += ":\(port)" }
            if seen.insert(origin.lowercased()).inserted { addresses.append(origin) }
        }
        return addresses
    }

    /// This computer (localhost, 127.x, ::1) or a private address: 10.x, 172.16 to 31, 192.168.x, and 100.64 to 127 (Tailscale).
    public static func isLocalNetwork(host: String) -> Bool {
        let name = host.lowercased()
        if name == "localhost" || name.hasSuffix(".localhost") { return true }
        if name.contains(":") { return name == "::1" || name.hasPrefix("fe80:") || name.hasPrefix("fc") || name.hasPrefix("fd") }
        let octets = name.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard octets.count == 4, let a = octets[0], let b = octets[1], octets.allSatisfy({ $0 != nil && (0...255).contains($0!) }) else { return false }
        return a == 127 || a == 10 || (a == 172 && (16...31).contains(b)) || (a == 192 && b == 168) || (a == 100 && (64...127).contains(b))
    }

    private static func probe(fetcher: JSONGetting, address: String, done: @escaping (DetectedServer?) -> Void) {
        fetcher.get(address + "/running") { running in
            if running?["running"] is [Any] { done(DetectedServer(router: address)); return }
            fetcher.get(address + "/metrics") { metrics in
                if let engine = metrics?["engine"] as? [String: Any], !engine.isEmpty { done(DetectedServer(url: address)) }
                else if let metrics = metrics, MtplxReader.recognises(metrics) { done(DetectedServer(url: address)) }
                else { done(nil) }
            }
        }
    }
}
