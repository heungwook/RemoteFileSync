using System;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class ConflictDetailTests
{
    private static ConflictDetail Sample(string? renamedTo = null) =>
        new ConflictDetail(
            ClientSize: 1024,
            ClientMtimeTicks: new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc).Ticks,
            ServerSize: 2048,
            ServerMtimeTicks: new DateTime(2026, 7, 2, 17, 30, 0, DateTimeKind.Utc).Ticks,
            RenamedTo: renamedTo);

    [Fact]
    public void Encode_IsSingleLineAndVersioned()
    {
        var encoded = Sample("report.conflict-20260720-143052-server.docx").Encode();

        Assert.StartsWith("v1\t", encoded);
        // file_versions.detail is rendered one row per line by the review report; an embedded
        // newline would split one conflict across two report lines.
        Assert.DoesNotContain("\n", encoded);
        Assert.DoesNotContain("\r", encoded);
    }

    [Fact]
    public void Decode_RoundTripsWithoutRename()
    {
        var original = Sample();
        Assert.Equal(original, ConflictDetail.Decode(original.Encode()));
    }

    [Fact]
    public void Decode_RoundTripsWithRename()
    {
        var original = Sample("report.conflict-20260720-143052-server.docx");
        var decoded = ConflictDetail.Decode(original.Encode());

        Assert.Equal(original, decoded);
        Assert.Equal("report.conflict-20260720-143052-server.docx", decoded!.RenamedTo);
    }

    [Fact]
    public void Decode_DistinguishesNullRenameFromEmptyRename()
    {
        // A bare sentinel would make RenamedTo == "" and RenamedTo == null encode identically,
        // and the review report would then claim a rename that never happened.
        Assert.Null(ConflictDetail.Decode(Sample(null).Encode())!.RenamedTo);
        Assert.Equal("", ConflictDetail.Decode(Sample("").Encode())!.RenamedTo);
    }

    [Theory]
    [InlineData("has\ttab.txt")]
    [InlineData("has\nnewline.txt")]
    [InlineData("has\\backslash.txt")]
    [InlineData("has\r\nCRLF.txt")]
    public void Decode_RoundTripsRenamesContainingDelimiterCharacters(string renamedTo)
    {
        var original = Sample(renamedTo);
        var encoded = original.Encode();

        Assert.DoesNotContain("\n", encoded);
        Assert.Equal(original, ConflictDetail.Decode(encoded));
    }

    [Fact]
    public void Decode_RoundTripsNegativeAndZeroSizes()
    {
        var original = new ConflictDetail(0, 0, -1, long.MaxValue, null);
        Assert.Equal(original, ConflictDetail.Decode(original.Encode()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("both sides changed since last sync")]   // legacy free-form English
    [InlineData("v2\t1\t2\t3\t4\t-")]                    // unknown version
    [InlineData("v1\t1\t2\t3\t-")]                       // too few fields
    [InlineData("v1\t1\t2\t3\t4\t-\textra")]             // too many fields
    [InlineData("v1\tnotanumber\t2\t3\t4\t-")]           // unparsable size
    [InlineData("v1\t1\t2\t3\t4\t?name")]                // bad rename flag
    [InlineData("v1\t1\t2\t3\t4\t+trailing\\")]          // dangling escape
    [InlineData("v1\t1\t2\t3\t4\t+bad\\qescape")]        // unknown escape
    public void Decode_ReturnsNullOnAnythingUnparsable(string? detail)
    {
        Assert.Null(ConflictDetail.Decode(detail));
    }
}
