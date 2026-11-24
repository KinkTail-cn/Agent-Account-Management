using System;
using System.IO;
using KeyIsle;

internal static class TotpVectors
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Invalid(string input)
    {
        try { Totp.Parse(input); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected an invalid authenticator configuration");
    }

    public static void Main()
    {
        // RFC 6238, Appendix B. Each algorithm uses its specified key length.
        string[] algorithms = { "SHA1", "SHA256", "SHA512" };
        string[] keys = {
            "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
            "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA",
            "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA"
        };
        long[] times = { 59, 1111111109, 1111111111, 1234567890, 2000000000, 20000000000 };
        string[,] expected = {
            { "94287082", "07081804", "14050471", "89005924", "69279037", "65353130" },
            { "46119246", "68084774", "67062674", "91819424", "90698825", "77737706" },
            { "90693936", "25091201", "99943326", "93441116", "38618901", "47863826" }
        };
        for (int algorithm = 0; algorithm < algorithms.Length; algorithm++)
        {
            var config = Totp.Parse("otpauth://totp/RFC%20test?secret=" + keys[algorithm] + "&digits=8&algorithm=" + algorithms[algorithm]);
            for (int i = 0; i < times.Length; i++)
            {
                var result = Totp.Generate(config, times[i]);
                Assert(result.code == expected[algorithm, i], algorithms[algorithm] + " at " + times[i]);
                Assert(result.starts <= times[i] * 1000 && times[i] * 1000 < result.expires, "Incorrect expiry");
            }
        }
        var raw = Totp.Parse("gezd gnbv-gy3t qojq gezd gnbv gy3t qojq");
        Assert(Totp.Generate(raw, 59).code == "287082", "Default raw key parameters");
        Assert(Totp.Generate(raw, 60).starts == 60000, "Exact time-step boundary");
        var longPeriod = Totp.Parse("otpauth://totp/test?secret=" + keys[0] + "&period=60&digits=7");
        Assert(Totp.Generate(longPeriod, 119).code == "4287082", "Non-default period and digit count");
        Assert(Totp.Parse("MY======").secret == "MY", "Padded Base32");
        foreach (string bad in new[] { "", "12345", "AAA", "MZ", "MY=", "otpauth://hotp/x?secret=MY", "otpauth://totp/x?period=30", "otpauth://totp/x?secret=MY&secret=MY", "otpauth://totp/x?secret=MY&period=0", "otpauth://totp/x?secret=MY&algorithm=MD5" }) Invalid(bad);
        Console.WriteLine("18 RFC 6238 vectors, raw and padded keys, rollover, non-default periods and invalid input passed.");
    }
}
