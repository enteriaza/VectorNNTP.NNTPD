namespace VectorNNTP.Common.Messaging.RabbitMq
{
    /// <summary>
    /// <see cref="IRabbitMqConnectionNameProvider"/> that evaluates a caller-supplied factory.
    /// </summary>
    public sealed class DelegateRabbitMqConnectionNameProvider : IRabbitMqConnectionNameProvider
    {
        private readonly Func<string> _factory;

        /// <summary>Initializes a new instance that invokes <paramref name="factory"/> on each request.</summary>
        /// <param name="factory">Factory that returns the client-provided connection name.</param>
        public DelegateRabbitMqConnectionNameProvider(Func<string> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            _factory = factory;
        }

        /// <inheritdoc />
        public string GetConnectionName()
        {
            var name = _factory();
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return name;
        }
    }
}
