using System.Diagnostics;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;

namespace VectorNNTP.BackFiller.Tests.Hosting;

public sealed class BackFillerProcessStartupTests
{
    [Fact]
    public async Task Published_or_built_process_fails_fast_with_a_useful_validation_error()
    {
        var (fileName, arguments) = ResolveApplicationStart();
        var workingDirectory = Directory.CreateTempSubdirectory("bf-process-cwd-").FullName;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment[BackFillerOptions.NameEnvironmentVariable] =
                "process-smoke";
            process.StartInfo.Environment[BackFillerOptions.ServerIdEnvironmentVariable] = "1";
            process.StartInfo.Environment[BackFillerOptions.GrabberDbEnvironmentVariable] =
                "Server=127.0.0.1;Database=nntp;User ID=nntparticles;Password=db-secret-xyz";
            process.StartInfo.Environment["VECTOR__BINDPORT"] = "1190";
            process.StartInfo.Environment["VECTOR__BINDPORTTLS"] = "0";
            process.StartInfo.Environment["VECTOR__CLOUDFLAREAPIKEY"] = "unit-test-cloudflare-key-not-secret";
            process.StartInfo.Environment["VECTOR__CLOUDFLAREZONEID"] = "0123456789abcdef0123456789abcdef";

            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                Assert.Fail("BackFiller process did not exit within 20s after invalid BindPortTls.");
            }

            var output = string.Concat(await stdout, await stderr);
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("BindPortTls", output, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workingDirectory);
        }
    }

    [Fact]
    public async Task Process_loads_appsettings_from_the_application_base_not_the_working_directory()
    {
        var (fileName, arguments) = ResolveApplicationStart();
        var workingDirectory = Directory.CreateTempSubdirectory("bf-process-appsettings-").FullName;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment[BackFillerOptions.ServerIdEnvironmentVariable] = "";
            process.StartInfo.Environment["VECTOR__SERVERID"] = "";
            process.StartInfo.Environment["VECTOR__CLOUDFLAREAPIKEY"] = "";
            process.StartInfo.Environment["VECTOR__ACMECERTIFICATEPASSWORD"] = "";

            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                Assert.Fail("BackFiller process did not exit within 20s while loading appsettings from a foreign cwd.");
            }

            var output = string.Concat(await stdout, await stderr);
            Assert.Equal(1, process.ExitCode);
            Assert.True(
                output.Contains("ServerId", StringComparison.Ordinal)
                || output.Contains("CloudFlareApiKey", StringComparison.Ordinal),
                output);
            Assert.DoesNotContain("Name is required", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Hosts must contain", output, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workingDirectory);
        }
    }

    private static (string FileName, string[] Arguments) ResolveApplicationStart()
    {
        foreach (var directory in EnumerateApplicationDirectories())
        {
            var exe = Path.Combine(directory, "VectorNNTP.BackFiller.exe");
            var settings = Path.Combine(directory, "appsettings.json");
            if (File.Exists(exe) && File.Exists(settings))
            {
                return (exe, []);
            }

            var dll = Path.Combine(directory, "VectorNNTP.BackFiller.dll");
            if (File.Exists(dll) && File.Exists(settings))
            {
                return ("dotnet", [dll]);
            }
        }

        Assert.Fail("Could not locate VectorNNTP.BackFiller next to appsettings.json.");
        return ("", []);
    }

    private static IEnumerable<string> EnumerateApplicationDirectories()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BackFillerServiceCollectionExtensions).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return assemblyDirectory;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "bin", "Release", "net10.0");
            yield return Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "bin", "Debug", "net10.0");
            directory = directory.Parent;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
