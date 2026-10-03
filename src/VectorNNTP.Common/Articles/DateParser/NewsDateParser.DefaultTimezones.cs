namespace VectorNNTP.Common.Articles.DateParser
{
    /// <content>
    /// Built-in trailing timezone-abbreviation table used during date canonicalization.
    /// </content>
    internal static partial class NewsDateParser
    {
        /// <summary>
        /// One trailing date abbreviation and the ASCII offset bytes written in its place.
        /// </summary>
        /// <param name="Abbreviation">Abbreviation bytes matched with ASCII case folding.</param>
        /// <param name="Offset">Replacement bytes, including the sign, such as <c>+00:00</c> or <c>+05:30</c>.</param>
        private readonly record struct TimezoneMapping(byte[] Abbreviation, byte[] Offset);

        /// <summary>
        /// Fixed abbreviation-to-offset table used by <c>SubstituteTimezoneAbbreviation</c>.
        /// First case-insensitive match wins. <c>CST</c> maps to <c>+08:00</c> and <c>CDT</c> maps to <c>-05:00</c>
        /// as stored here. <c>UTC+0</c> through <c>UTC+9</c> and <c>UTC-1</c> through <c>UTC-9</c> are included; other numeric forms are not.
        /// </summary>
        private static readonly TimezoneMapping[] TimezoneMappings = CreateDefaultTimezoneMappings();

        /// <summary>
        /// Builds <see cref="TimezoneMappings"/> from the abbreviation/offset pairs in this method.
        /// </summary>
        /// <returns>The table in match order.</returns>
        private static TimezoneMapping[] CreateDefaultTimezoneMappings() =>
        [
            new("UT"u8.ToArray(), "+00:00"u8.ToArray()),
            new("UTC"u8.ToArray(), "+00:00"u8.ToArray()),
            new("GMT"u8.ToArray(), "+00:00"u8.ToArray()),
            new("BST"u8.ToArray(), "+01:00"u8.ToArray()),
            new("WET"u8.ToArray(), "+00:00"u8.ToArray()),
            new("WEST"u8.ToArray(), "+01:00"u8.ToArray()),
            new("CET"u8.ToArray(), "+01:00"u8.ToArray()),
            new("CEST"u8.ToArray(), "+02:00"u8.ToArray()),
            new("EET"u8.ToArray(), "+02:00"u8.ToArray()),
            new("EEST"u8.ToArray(), "+03:00"u8.ToArray()),
            new("EST"u8.ToArray(), "-05:00"u8.ToArray()),
            new("EDT"u8.ToArray(), "-04:00"u8.ToArray()),
            new("CST"u8.ToArray(), "+08:00"u8.ToArray()),
            new("CDT"u8.ToArray(), "-05:00"u8.ToArray()),
            new("MST"u8.ToArray(), "-07:00"u8.ToArray()),
            new("MDT"u8.ToArray(), "-06:00"u8.ToArray()),
            new("PST"u8.ToArray(), "-08:00"u8.ToArray()),
            new("PDT"u8.ToArray(), "-07:00"u8.ToArray()),
            new("AKST"u8.ToArray(), "-09:00"u8.ToArray()),
            new("AKDT"u8.ToArray(), "-08:00"u8.ToArray()),
            new("HST"u8.ToArray(), "-10:00"u8.ToArray()),
            new("AEST"u8.ToArray(), "+10:00"u8.ToArray()),
            new("AEDT"u8.ToArray(), "+11:00"u8.ToArray()),
            new("ACST"u8.ToArray(), "+09:30"u8.ToArray()),
            new("ACDT"u8.ToArray(), "+10:30"u8.ToArray()),
            new("AWST"u8.ToArray(), "+08:00"u8.ToArray()),
            new("JST"u8.ToArray(), "+09:00"u8.ToArray()),
            new("KST"u8.ToArray(), "+09:00"u8.ToArray()),
            new("HKT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("SGT"u8.ToArray(), "+08:00"u8.ToArray()),
            new("IST"u8.ToArray(), "+05:30"u8.ToArray()),
            new("PKT"u8.ToArray(), "+05:00"u8.ToArray()),
            new("MSK"u8.ToArray(), "+03:00"u8.ToArray()),
            new("TRT"u8.ToArray(), "+03:00"u8.ToArray()),
            new("SAST"u8.ToArray(), "+02:00"u8.ToArray()),
            new("WIB"u8.ToArray(), "+07:00"u8.ToArray()),
            new("WITA"u8.ToArray(), "+08:00"u8.ToArray()),
            new("WIT"u8.ToArray(), "+09:00"u8.ToArray()),
            new("NZST"u8.ToArray(), "+12:00"u8.ToArray()),
            new("NZDT"u8.ToArray(), "+13:00"u8.ToArray()),
            new("NPT"u8.ToArray(), "+05:45"u8.ToArray()),
            new("IRST"u8.ToArray(), "+03:30"u8.ToArray()),
            new("UTC+0"u8.ToArray(), "+00:00"u8.ToArray()),
            new("UTC+1"u8.ToArray(), "+01:00"u8.ToArray()),
            new("UTC+2"u8.ToArray(), "+02:00"u8.ToArray()),
            new("UTC+3"u8.ToArray(), "+03:00"u8.ToArray()),
            new("UTC+4"u8.ToArray(), "+04:00"u8.ToArray()),
            new("UTC+5"u8.ToArray(), "+05:00"u8.ToArray()),
            new("UTC+6"u8.ToArray(), "+06:00"u8.ToArray()),
            new("UTC+7"u8.ToArray(), "+07:00"u8.ToArray()),
            new("UTC+8"u8.ToArray(), "+08:00"u8.ToArray()),
            new("UTC+9"u8.ToArray(), "+09:00"u8.ToArray()),
            new("UTC-1"u8.ToArray(), "-01:00"u8.ToArray()),
            new("UTC-2"u8.ToArray(), "-02:00"u8.ToArray()),
            new("UTC-3"u8.ToArray(), "-03:00"u8.ToArray()),
            new("UTC-4"u8.ToArray(), "-04:00"u8.ToArray()),
            new("UTC-5"u8.ToArray(), "-05:00"u8.ToArray()),
            new("UTC-6"u8.ToArray(), "-06:00"u8.ToArray()),
            new("UTC-7"u8.ToArray(), "-07:00"u8.ToArray()),
            new("UTC-8"u8.ToArray(), "-08:00"u8.ToArray()),
            new("UTC-9"u8.ToArray(), "-09:00"u8.ToArray()),
        ];
    }
}
