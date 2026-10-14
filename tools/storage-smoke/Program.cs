namespace VectorNNTP.StorageSmoke;

/// <summary>Entry point for the StorageServer VATP smoke and soak client.</summary>
public static class Program
{
    /// <summary>Parses arguments and, for smoke or soak, runs until the requested bound.</summary>
    /// <param name="args">Command line after the process name.</param>
    /// <returns>0 when help is shown or a soak completes its window or a smoke places every article; 1 when a run stops early or a smoke does not; 2 when the arguments or results directory are rejected.</returns>
    public static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parse(args, Console.Error);
        if (parsed.ShowHelp)
        {
            Console.WriteLine(CommandLine.HelpText);
            return 0;
        }

        if (parsed.Command is null)
        {
            return parsed.ExitCode;
        }

        return await PlacementRun.ExecuteAsync(parsed.Command, Console.Out, Console.Error).ConfigureAwait(false);
    }
}
