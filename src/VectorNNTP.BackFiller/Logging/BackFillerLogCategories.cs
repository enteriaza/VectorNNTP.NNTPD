namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>
    /// Reserved logger category names for VectorNNTP.BackFiller host diagnostics.
    /// </summary>
    internal static class BackFillerLogCategories
    {
        /// <summary>
        /// Category passed to <see cref="ILoggerFactory.CreateLogger(string)"/> for host integration events,
        /// including the logging-initialized event from <see cref="BackFillerLoggingExtensions.WriteLoggingInitialized"/>.
        /// </summary>
        internal const string Hosting = "VectorNNTP.BackFiller.Hosting";
    }
}
