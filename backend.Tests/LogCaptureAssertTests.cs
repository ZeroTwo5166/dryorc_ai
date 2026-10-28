using System.Net;
using Microsoft.Extensions.Logging;

namespace backend.Tests;

/// <summary>
/// LogCaptureAssert over synthetic entries (T8; D-13). No host, no database.
/// Every value is synthetic and unique per run; failures are read with
/// Record.Exception and compared by computed booleans with fixed messages.
/// </summary>
public class LogCaptureAssertTests
{
    private const string Label = "SyntheticRequest.Secret";

    private const string SevenDigits = "4718203";

    private static CapturedLog Entry(string message, string? exception = null) =>
        new("Synthetic.Category", LogLevel.Trace, message, exception);

    private static string NewPlusValue() => "syn+" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Failure_Names_LabelIndexAndLength_Only()
    {
        var value = NewPlusValue();
        var first = "synthetic entry without the denied value";
        var second = "synthetic entry holding " + value;
        var exceptionText = "System.InvalidOperationException: synthetic exception text";
        CapturedLog[] entries = [Entry(first), Entry(second, exceptionText)];

        var failure = Record.Exception(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, value)));

        Assert.True(failure is not null, "no failure for an entry holding the denied value");
        var expected = $"{Label} found in captured entry 1 (length {second.Length + exceptionText.Length})";
        Assert.True(failure!.Message == expected, "the failure text is not exactly the label, the entry index and the entry length");
        var holdsEntryText = failure.Message.Contains(first, StringComparison.Ordinal)
            || failure.Message.Contains(second, StringComparison.Ordinal)
            || failure.Message.Contains(exceptionText, StringComparison.Ordinal);
        Assert.False(holdsEntryText, "the failure text holds captured entry text");
        var holdsValue = failure.Message.Contains(value, StringComparison.Ordinal)
            || failure.Message.Contains(WebUtility.UrlEncode(value), StringComparison.Ordinal);
        Assert.False(holdsValue, "the failure text holds the denied value");
    }

    [Fact]
    public void EmptyOrWhitespaceValue_Throws()
    {
        CapturedLog[] entries = [Entry("synthetic entry")];

        Assert.Throws<ArgumentException>(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, null!)));
        Assert.Throws<ArgumentException>(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, "")));
        Assert.Throws<ArgumentException>(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, "   ")));
    }

    [Fact]
    public void NoDeniedValue_Throws()
    {
        CapturedLog[] entries = [Entry("synthetic entry")];

        Assert.Throws<ArgumentException>(() => LogCaptureAssert.NoneContain(entries));
    }

    [Fact]
    public void ShortValue_NeedsOptIn()
    {
        CapturedLog[] entries = [Entry("synthetic entry without the short value")];

        Assert.Throws<ArgumentException>(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, SevenDigits)));
        var failure = Record.Exception(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, SevenDigits, AllowShort: true)));
        Assert.True(failure is null, "a short value with AllowShort over entries that do not hold it must pass");
    }

    [Fact]
    public void EncodedForm_IsFound()
    {
        var value = NewPlusValue();
        var encoded = WebUtility.UrlEncode(value);
        var message = "synthetic entry holding " + encoded;
        Assert.True(encoded != value && !message.Contains(value, StringComparison.Ordinal), "the entry must hold the encoded form only");
        CapturedLog[] entries = [Entry(message)];

        var failure = Record.Exception(() => LogCaptureAssert.NoneContain(entries, new DeniedValue(Label, value)));

        Assert.True(failure is not null, "no failure for an entry holding the encoded form");
        var expected = $"{Label} found in captured entry 0 (length {message.Length})";
        Assert.True(failure!.Message == expected, "the encoded form's failure text is not exactly the label, index 0 and the entry length");
    }

    [Fact]
    public void RawEqualsEncoded_CheckedOnce()
    {
        var value = Guid.NewGuid().ToString("N");
        Assert.True(WebUtility.UrlEncode(value) == value, "the value's encoded form must equal the value");
        var message = "synthetic entry holding " + value;
        CapturedLog[] holding = [Entry(message)];
        CapturedLog[] notHolding = [Entry("synthetic entry without the value")];

        var failure = Record.Exception(() => LogCaptureAssert.NoneContain(holding, new DeniedValue(Label, value)));
        var none = Record.Exception(() => LogCaptureAssert.NoneContain(notHolding, new DeniedValue(Label, value)));

        Assert.True(failure is not null, "no failure for an entry holding the value");
        var expected = $"{Label} found in captured entry 0 (length {message.Length})";
        Assert.True(failure!.Message == expected, "the failure text is not exactly the label, index 0 and the entry length");
        Assert.True(none is null, "a failure for entries that do not hold the value");
    }
}
