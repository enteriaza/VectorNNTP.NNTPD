namespace VectorNNTP.Common.Tests.Articles.Validation
{
    /// <summary>
    /// Test-only scalar RFC 5536 §3.1.3 checker.
    /// </summary>
    /// <remarks>
    /// This type does not call <c>NntpMessageIdValidation</c> or the production character-class helpers.
    /// <c>atext</c> is the RFC 5322 §3.2.3 set stored in a lookup table.
    /// <c>mdtext</c> is RFC 5536 <c>%d33-61 / %d63-90 / %d94-126</c>.
    /// </remarks>
    internal static class NntpMessageIdOracle
    {
        private const int MaxLength = 250;

        private static readonly bool[] Atext = BuildAtext();

        private static readonly bool[] Mdtext = BuildMdtext();

        internal static bool IsValid(ReadOnlySpan<byte> messageId)
        {
            if ((uint)(messageId.Length - 3) > (uint)(MaxLength - 3))
            {
                return false;
            }

            if (messageId[0] != (byte)'<' || messageId[^1] != (byte)'>')
            {
                return false;
            }

            var end = messageId.Length - 1;
            var index = 1;
            if (!TryDotAtom(messageId, end, ref index) || index >= end || messageId[index] != (byte)'@')
            {
                return false;
            }

            index++;
            if (index >= end)
            {
                return false;
            }

            if (messageId[index] == (byte)'[')
            {
                return TryLiteral(messageId, end, ref index);
            }

            return TryDotAtom(messageId, end, ref index) && index == end;
        }

        private static bool TryDotAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if (!TryAtom(messageId, end, ref index))
            {
                return false;
            }

            while (index < end && messageId[index] == (byte)'.')
            {
                index++;
                if (!TryAtom(messageId, end, ref index))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAtom(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            if (index >= end || !Atext[messageId[index]])
            {
                return false;
            }

            index++;
            while (index < end && Atext[messageId[index]])
            {
                index++;
            }

            return true;
        }

        private static bool TryLiteral(ReadOnlySpan<byte> messageId, int end, ref int index)
        {
            index++;
            while (index < end && Mdtext[messageId[index]])
            {
                index++;
            }

            if (index >= end || messageId[index] != (byte)']')
            {
                return false;
            }

            index++;
            return index == end;
        }

        private static bool[] BuildAtext()
        {
            var table = new bool[256];
            for (var value = (byte)'A'; value <= (byte)'Z'; value++)
            {
                table[value] = true;
            }

            for (var value = (byte)'a'; value <= (byte)'z'; value++)
            {
                table[value] = true;
            }

            for (var value = (byte)'0'; value <= (byte)'9'; value++)
            {
                table[value] = true;
            }

            foreach (var value in "!#$%&'*+-/=?^_`{|}~"u8)
            {
                table[value] = true;
            }

            return table;
        }

        private static bool[] BuildMdtext()
        {
            var table = new bool[256];
            for (var value = 33; value <= 61; value++)
            {
                table[value] = true;
            }

            for (var value = 63; value <= 90; value++)
            {
                table[value] = true;
            }

            for (var value = 94; value <= 126; value++)
            {
                table[value] = true;
            }

            return table;
        }
    }
}
