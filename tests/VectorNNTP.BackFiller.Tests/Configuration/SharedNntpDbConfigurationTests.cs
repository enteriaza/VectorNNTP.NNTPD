using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Hosting;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Configuration
{
    public sealed class SharedNntpDbConfigurationTests
    {
        private const string SharedConnectionString =
            "Server=198.18.0.70;Port=3306;Database=nntpdb;User ID=nntpd;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=32;ConnectionIdleTimeout=300;";

        [Fact]
        public void BackFiller_uses_the_same_nntpdb_configuration_key_as_nntpd()
        {
            Assert.Equal("NntpDB", NntpDbOptions.ConnectionStringName);
            Assert.Equal("ConnectionStrings__NntpDB", NntpDbOptions.ConnectionStringEnvironmentVariable);
            Assert.Null(typeof(BackFillerOptions).GetField(
                "GrabberDbEnvironmentVariable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
            Assert.Null(typeof(BackFillerOptions).Assembly.GetType(
                "VectorNNTP.BackFiller.Configuration.BackFillerConnectionStringsOptions"));
            Assert.Null(typeof(BackFillerOptions).Assembly.GetType(
                "VectorNNTP.BackFiller.Configuration.GrabberDbConnectionString"));
        }

        [Fact]
        public void Same_connection_strings_nntpdb_value_targets_the_same_catalog()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] = SharedConnectionString,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddNntpDbOptions();
            using var provider = services.BuildServiceProvider();
            var bound = provider.GetRequiredService<IOptions<NntpDbOptions>>().Value;

            Assert.Equal(SharedConnectionString, bound.ConnectionString);
            var runtime = BackFillerRuntimeOptionsFactory.Create(
                BackFillerTestOptions.CreateValid(),
                bound);
            Assert.Equal("nntpdb", runtime.NntpDb.Database);
            Assert.Equal("198.18.0.70", runtime.NntpDb.Server);
            Assert.Equal(
                new MySqlConnector.MySqlConnectionStringBuilder(SharedConnectionString).Database,
                runtime.NntpDb.Database);
        }

        [Fact]
        public void Host_resolves_nntpdb_and_does_not_bind_grabberdb()
        {
            var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
            pairs["ConnectionStrings:GrabberDB"] = "Server=203.0.113.10;Database=grabber;User ID=other;Password=other-secret";
            pairs[$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] = SharedConnectionString;

            var builder = Host.CreateApplicationBuilder([]);
            builder.Configuration.AddInMemoryCollection(pairs);
            builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
            builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
                new NoOpCloudflareDnsReconciler());
            builder.Services.AddSingleton<IPhysicalMemoryProvider>(
                new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
            builder.Services.AddSingleton<VectorNNTP.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
                new FakeBackFillerRabbitMqConnectionFactory());
            builder.Services.AddSingleton<VectorNNTP.BackFiller.Accounts.IProviderAccountSource>(
                new FakeProviderAccountSource());
            builder.AddBackFillerHosting();

            using var host = builder.Build();
            var nntpDb = host.Services.GetRequiredService<IOptions<NntpDbOptions>>().Value;
            var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
            Assert.Equal(SharedConnectionString, nntpDb.ConnectionString);
            Assert.Equal("nntpdb", runtime.NntpDb.Database);
            Assert.DoesNotContain("grabber", runtime.NntpDb.ConnectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("203.0.113.10", runtime.NntpDb.ConnectionString, StringComparison.Ordinal);
        }

        [Fact]
        public void Production_appsettings_does_not_contain_connection_strings_or_grabberdb()
        {
            var path = FindProductionAppsettings();
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("ConnectionStrings", out _));
            Assert.DoesNotContain("GrabberDB", json, StringComparison.Ordinal);
            Assert.DoesNotContain("GrabberDb", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", json, StringComparison.Ordinal);
        }

        private static string FindProductionAppsettings()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "VectorNNTP.BackFiller", "VectorNNTP.BackFiller.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not locate src/VectorNNTP.BackFiller/VectorNNTP.BackFiller.json.");
        }
    }
}
