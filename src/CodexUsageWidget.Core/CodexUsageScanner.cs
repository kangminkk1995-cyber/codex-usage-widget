namespace CodexUsageWidget.Core;

public sealed class CodexUsageScanner
{
    private readonly IReadOnlyList<string> _roots;

    public CodexUsageScanner(IEnumerable<string> roots)
    {
        _roots = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static CodexUsageScanner ForCurrentUser()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        return new CodexUsageScanner([
            Path.Combine(codexHome, "sessions"),
            Path.Combine(codexHome, "archived_sessions")
        ]);
    }

    public UsageSnapshot? FindLatest(CancellationToken cancellationToken = default)
    {
        UsageSnapshot? latest = null;
        foreach (var candidateFile in EnumerateCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (latest is not null && candidateFile.LastWriteTimeUtc < latest.CapturedAt.UtcDateTime) break;
            try
            {
                using var stream = new FileStream(candidateFile.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CodexUsageParser.TryParseLine(line, candidateFile.Path, out var candidate) || candidate is null) continue;
                    if (latest is null || candidate.CapturedAt > latest.CapturedAt) latest = candidate;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return latest;
    }

    public IReadOnlyList<string> Roots => _roots;

    private IEnumerable<CandidateFile> EnumerateCandidates()
    {
        var candidates = new List<CandidateFile>();
        foreach (var root in _roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            using var enumerator = files.GetEnumerator();
            while (true)
            {
                string file;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    file = enumerator.Current;
                }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
                try { candidates.Add(new CandidateFile(file, File.GetLastWriteTimeUtc(file))); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return candidates.OrderByDescending(candidate => candidate.LastWriteTimeUtc);
    }

    private sealed record CandidateFile(string Path, DateTime LastWriteTimeUtc);
}
