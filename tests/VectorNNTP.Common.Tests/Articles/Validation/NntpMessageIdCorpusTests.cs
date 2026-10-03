using VectorNNTP.Common.Articles.Validation;
using Xunit.Abstractions;

namespace VectorNNTP.Common.Tests.Articles.Validation
{
    public sealed class NntpMessageIdCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public NntpMessageIdCorpusTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Production_matches_independent_scalar_oracle()
        {
            var samples = NntpMessageIdCorpus.Samples;
            _output.WriteLine($"corpus={samples.Count}");
            Assert.InRange(samples.Count, 400, 2000);

            foreach (var sample in samples)
            {
                var actual = NntpMessageIdValidation.IsValidMessageId(sample.Bytes);
                var oracle = NntpMessageIdOracle.IsValid(sample.Bytes);
                Assert.True(
                    actual == oracle,
                    $"{sample.Name} production={actual} oracle={oracle} length={sample.Bytes.Length}");
                Assert.Equal(sample.Expected, actual);
            }
        }
    }
}
