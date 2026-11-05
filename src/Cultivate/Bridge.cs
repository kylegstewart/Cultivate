using System.Text.Json;

namespace Cultivate;

internal sealed class Bridge(Settings settings)
{
    public async Task<JsonElement> Call(string action, object? payload = null)
    {
        var directory = Path.Combine(Files.Home, "Requests");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var output = input + ".result";
        Files.WriteJson(input, new { settings.WebfarmRoot, settings.StateRoot, Payload = payload });
        try
        {
            await Commands.Run(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Bridge.ps1"),
                 "-Action", action, "-InputPath", input, "-OutputPath", output],
                log: action is "Status" or "Heartbeat" ? null :
                    Path.Combine(Files.Home, "Logs", "bridge-" + Path.GetFileNameWithoutExtension(input) + ".log"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            return document.RootElement.Clone();
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    public async Task<Dictionary<string, string>> Mappings(List<string> names) =>
        (await Call("Mappings", new { Names = names })).Deserialize<Dictionary<string, string>>(Files.Json)
        ?? throw new InvalidDataException("Mapping lookup returned no result.");

    public async Task<DeploymentState?> Status()
    {
        var result = await Call("Status");
        return result.ValueKind == JsonValueKind.Null ? null : result.Deserialize<DeploymentState>(Files.Json);
    }
}
