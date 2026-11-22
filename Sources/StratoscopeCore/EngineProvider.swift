import Foundation

/// An adapter between one inference engine and Stratoscope.
///
/// To support a new engine (Uzu, BaseRT, llama.cpp, MLX, ...): implement this protocol, translate
/// the engine's metrics into an `EngineSnapshot`, and register it in `EngineRegistry`.
/// Report only what the engine really knows; leave everything else `nil`.
public protocol EngineProvider: AnyObject {
    /// Stable identifier used in the config file, e.g. "strata".
    static var id: String { get }
    /// Called every couple of seconds on the main queue. Call `completion` exactly once,
    /// on the main queue. `nil` means "engine not running": the menu bar item hides itself.
    func poll(_ completion: @escaping (EngineSnapshot?) -> Void)
}

public enum EngineRegistry {
    public static let available: [String] = [StrataProvider.id, MtplxReader.id, LlamaCppReader.id, CustomReader.id, DemoProvider.id]

    public static func make(_ config: Config) -> EngineProvider {
        switch config.engine.lowercased() {
        case DemoProvider.id: return DemoProvider()
        default: return StrataProvider(   // also serves `engine: mtplx`, `engine: llamacpp` and `engine: custom`: the provider tells the engines apart by their answers.
config: config, clients: config.readClientConfigs ? ClientConfigReader(configHome: ProcessInfo.processInfo.environment["XDG_CONFIG_HOME"]) : nil)
        }
    }
}
