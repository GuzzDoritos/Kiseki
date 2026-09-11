namespace Kiseki.Core.Services;

public static class DotEnvFile
{
    public static void Load()
    {
        var current = Directory.GetCurrentDirectory();
        string[] candidates = [Path.Combine(current, ".env"), Path.Combine(current, "..", ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env")];
        var path = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (path is null) return;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            var index = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || index <= 0) continue;
            var key = trimmed[..index].Trim();
            var value = trimmed[(index + 1)..].Trim();
            if (value.Length >= 2 && (value.StartsWith('"') && value.EndsWith('"') || value.StartsWith('\'') && value.EndsWith('\''))) value = value[1..^1];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
