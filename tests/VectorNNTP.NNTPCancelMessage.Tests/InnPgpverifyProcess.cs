using System.Diagnostics;

namespace VectorNNTP.NNTPCancelMessage.Tests;

/// <summary>
/// Runs the real INN <c>control/pgpverify.in</c> 1.31 script against an article.
/// Does not install INN.
/// </summary>
internal sealed class InnPgpverifyProcess : IDisposable
{
    private readonly string _workDir;
    private readonly string _perl;
    private readonly string _script;

    private InnPgpverifyProcess(string workDir, string perl, string script)
    {
        _workDir = workDir;
        _perl = perl;
        _script = script;
    }

    public static InnPgpverifyProcess Create(string publicArmored)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicArmored);

        var perl = FindExecutable("perl");
        var gpg = FindExecutable("gpg");
        if (perl is null || gpg is null)
        {
            throw new InvalidOperationException(
                "INN pgpverify interop requires perl and gpg on PATH or in Git for Windows usr\\bin.");
        }

        var workDir = Directory.CreateTempSubdirectory("vectornntp-inn-pgpverify-").FullName;
        try
        {
            var publicPath = Path.Combine(workDir, "public.asc");
            File.WriteAllText(publicPath, publicArmored);
            File.WriteAllText(Path.Combine(workDir, "gpg.conf"), "charset utf-8\n");
            ImportPublicKey(gpg, workDir, publicPath);

            var fixture = LocateFixture();
            var configured = Path.Combine(workDir, "pgpverify.in");
            WriteConfiguredScript(fixture, configured, gpg, workDir);
            return new InnPgpverifyProcess(workDir, perl, configured);
        }
        catch
        {
            TryDelete(workDir);
            throw;
        }
    }

    public InnPgpverifyResult Verify(string article)
    {
        ArgumentException.ThrowIfNullOrEmpty(article);
        return Run(article, test: false);
    }

    public InnPgpverifyResult VerifyWithTestOutput(string article)
    {
        ArgumentException.ThrowIfNullOrEmpty(article);
        return Run(article, test: true);
    }

    public void Dispose() => TryDelete(_workDir);

    private InnPgpverifyResult Run(string article, bool test)
    {
        var start = new ProcessStartInfo
        {
            FileName = _perl,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add(_script);
        if (test)
        {
            start.ArgumentList.Add("--test");
        }

        start.Environment["GNUPGHOME"] = ToMsysPath(_workDir);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start perl for INN pgpverify.");
        process.StandardInput.Write(article);
        if (!article.EndsWith('\n'))
        {
            process.StandardInput.Write('\n');
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("INN pgpverify exceeded 60s.");
        }

        return new InnPgpverifyResult(process.ExitCode, stdout, stderr);
    }

    private static void ImportPublicKey(string gpg, string workDir, string publicPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = gpg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        var msysHome = ToMsysPath(workDir);
        start.ArgumentList.Add("--homedir");
        start.ArgumentList.Add(msysHome);
        start.ArgumentList.Add("--batch");
        start.ArgumentList.Add("--yes");
        start.ArgumentList.Add("--no-tty");
        start.ArgumentList.Add("--pinentry-mode");
        start.ArgumentList.Add("loopback");
        start.ArgumentList.Add("--no-default-keyring");
        start.ArgumentList.Add("--keyring");
        start.ArgumentList.Add("pubring.gpg");
        start.ArgumentList.Add("--import");
        start.ArgumentList.Add(ToMsysPath(publicPath));
        start.Environment["GNUPGHOME"] = msysHome;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start gpg --import.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "gpg --import failed (exit " + process.ExitCode + "): " + stdout + stderr);
        }
    }

    private static void WriteConfiguredScript(string fixture, string destination, string gpg, string workDir)
    {
        var gpgPerl = ToPerlSingleQuotedPath(ToMsysPath(gpg));
        var tmpPerl = ToPerlSingleQuotedPath(workDir);
        var keyringPerl = ToPerlSingleQuotedPath(ToMsysPath(workDir));
        var original = File.ReadAllText(fixture);
        const string marker = "# End of configuration section.";
        if (!original.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("INN pgpverify fixture is missing the configuration marker.");
        }

        var insert =
            marker + "\n" +
            "$gpg = " + gpgPerl + ";\n" +
            "$gpg_has_allow_weak_digest_algos_flag = 0;\n" +
            "$tmpdir = " + tmpPerl + ";\n" +
            "$lockdir = $tmpdir;\n" +
            "$keyring = " + keyringPerl + ";\n" +
            "$syslog_method = '';\n" +
            "$log_date = 0;\n";
        File.WriteAllText(destination, original.Replace(marker, insert, StringComparison.Ordinal));
    }

    private static string LocateFixture()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "InnPgpverify", "pgpverify.in"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", "InnPgpverify", "pgpverify.in"),
        };
        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException("INN pgpverify 1.31 fixture pgpverify.in was not found.");
    }

    private static string? FindExecutable(string name)
    {
        var file = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var extras = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "usr", "bin"),
        };
        foreach (var directory in extras)
        {
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToUnixPath(string path) => path.Replace('\\', '/');

    private static string ToMsysPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length >= 2 && full[1] == ':')
        {
            return "/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        }

        return ToUnixPath(full);
    }

    private static string ToPerlSingleQuotedPath(string path)
    {
        return "'" + ToUnixPath(path).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal readonly record struct InnPgpverifyResult(int ExitCode, string Stdout, string Stderr);
