namespace ShockWave.Overlay;

internal sealed class SessionLog(string path)
{
    private bool unavailable;

    public string? Append(string line)
    {
        if (unavailable) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                File.Move(path, path + ".previous", true);
            File.AppendAllText(path, line + Environment.NewLine);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            return "Could not save the activity log: " + error.Message;
        }
    }
}
