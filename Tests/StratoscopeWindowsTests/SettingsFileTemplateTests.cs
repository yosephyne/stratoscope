using System.IO;
using Stratoscope.Core;

namespace Stratoscope.Tests;

/// <summary>The file "Open settings file" creates, and what the app says about a setting it does not know.</summary>
public class SettingsFileTemplateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "stratoscope-template-" + Guid.NewGuid().ToString("N"));

    public SettingsFileTemplateTests() { Directory.CreateDirectory(dir); }
    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string Write(string json)
    {
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static readonly IDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    [Fact]
    public void TheTemplateChangesNothingAndKeepsTheServerSearchOn()
    {
        var config = Config.Load(NoEnvironment, Write(Config.Template));
        Assert.Empty(config.Problems);
        Assert.False(config.Configured);
        Assert.Equal(ThemeChoice.System, config.Theme);
    }

    [Theory]
    [InlineData("url", "\"url\": \"http://127.0.0.1:9\"")]
    [InlineData("router", "\"router\": \"http://127.0.0.1:18080\"")]
    [InlineData("theme", "\"theme\": \"dark\"")]
    [InlineData("ui_scale", "\"ui_scale\": 125")]
    [InlineData("readout_position", "\"readout_position\": \"left\"")]
    [InlineData("compact_at", "\"compact_at\": 60000")]
    public void EveryExampleInTheTemplateIsASettingTheAppKnows(string key, string line)
    {
        Assert.Contains("// " + line.Split(':')[0] + ":", Config.Template);      // it is in the template, commented
        var config = Config.Load(NoEnvironment, Write("{ " + line + " }"));
        Assert.Empty(config.Problems);
    }

    [Fact]
    public void ASettingThatDoesNotExistIsNamed()
    {
        var config = Config.Load(NoEnvironment, Write("{ \"display\": \"both\", \"theme\": \"dark\" }"));
        Assert.Contains(config.Problems, p => p.Contains("display"));
        Assert.DoesNotContain(config.Problems, p => p.Contains("theme"));
        Assert.Equal(ThemeChoice.Dark, config.Theme);
    }

    [Fact]
    public void EnvironmentVariablesAreNotReportedAsUnknownSettings()
    {
        var env = new Dictionary<string, string?> { ["STRATOSCOPE_TRACE_MOVES"] = "1", ["STRATOSCOPE_CONFIG"] = "x" };
        Assert.Empty(Config.Load(env, Write("{}")).Problems);
    }
}
