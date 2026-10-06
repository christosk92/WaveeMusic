// ── Wavee.Tests/PlaylistCoverTests.cs — the playlist cover (#155): budget, crop, turn, wire ─────────────────────────
//
// Pure: no engine, no WIC, no network. The image codec (Platform/CoverImage.cs) and the real shell dialog
// (Platform/ModalFilePicker.cs) are exercised end to end under `--fake` (see the PR); what is pinned here is every rule
// they lean on, and the three request bodies the live flow sends. The picker's threading contract is ModalFilePickerTests'.

using System.Text;
using Wavee;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public class PlaylistCoverTests
{
    // ── 1. the encoding budget ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3000, new[] { 640, 512, 400, 300 })]
    [InlineData(640, new[] { 640, 512, 400, 300 })]
    [InlineData(500, new[] { 500, 400, 300 })]
    [InlineData(300, new[] { 300 })]
    [InlineData(64, new[] { 64 })]
    [InlineData(63, new int[0])]
    public void Edges_NeverUpscale_AndStepDownFromTheFirstFit(int side, int[] expected)
        => Assert.Equal(expected, PlaylistCoverRules.Edges(side));

    [Fact]
    public void Fit_TakesTheFirstEncodingUnderTheBudget_LargestEdgeAndBestQualityFirst()
    {
        var tried = new List<(int Edge, int Quality)>();
        var fit = PlaylistCoverRules.Fit(2000, (edge, quality) =>
        {
            tried.Add((edge, quality));
            // Over budget at 640 whatever the quality; fits at 512 from quality 84 down.
            int size = edge == 640 ? PlaylistCoverRules.MaxUploadBytes + 1 : quality > 84 ? PlaylistCoverRules.MaxUploadBytes + 1 : 1000;
            return new byte[size];
        });
        Assert.NotNull(fit);
        Assert.Equal(512, fit.Value.Edge);
        Assert.Equal(84, fit.Value.Quality);
        Assert.Equal(1000, fit.Value.Jpeg.Length);
        // Every quality at 640 first, in ladder order, then 512 at 90 and 84.
        var expected = PlaylistCoverRules.QualityLadder.Select(q => (640, q)).Concat([(512, 90), (512, 84)]).ToList();
        Assert.Equal(expected, tried);
    }

    [Fact]
    public void Fit_AcceptsExactlyTheBudget()
    {
        var fit = PlaylistCoverRules.Fit(640, (_, _) => new byte[PlaylistCoverRules.MaxUploadBytes]);
        Assert.Equal((640, 90), (fit!.Value.Edge, fit.Value.Quality));
    }

    [Fact]
    public void Fit_IsNull_WhenNothingFits_AndStopsAtTheFirstEncoderFailure()
    {
        int calls = 0;
        Assert.Null(PlaylistCoverRules.Fit(700, (_, _) => { calls++; return new byte[PlaylistCoverRules.MaxUploadBytes + 1]; }));
        Assert.Equal(PlaylistCoverRules.Edges(700).Length * PlaylistCoverRules.QualityLadder.Length, calls);

        calls = 0;
        Assert.Null(PlaylistCoverRules.Fit(700, (_, _) => { calls++; return null; }));
        Assert.Equal(1, calls);

        Assert.Null(PlaylistCoverRules.Fit(10, (_, _) => throw new InvalidOperationException("never asked")));
    }

    [Theory]
    [InlineData(1200, 800, 200, 0, 800)]
    [InlineData(800, 1200, 0, 200, 800)]
    [InlineData(640, 640, 0, 0, 640)]
    [InlineData(5, 4, 0, 0, 4)]
    [InlineData(4, 7, 0, 1, 4)]
    public void CenterSquare_IsTheCentredLargestSquare(int w, int h, int x, int y, int side)
        => Assert.Equal((x, y, side), PlaylistCoverRules.CenterSquare(w, h));

    [Theory]
    [InlineData("C:\\a\\b.jpg", 100, CoverProblem.None)]
    [InlineData("C:\\a\\b.JPEG", 100, CoverProblem.None)]
    [InlineData("C:\\a\\b.png", 100, CoverProblem.None)]
    [InlineData("C:\\a\\b.webp", 100, CoverProblem.None)]
    [InlineData("C:\\a\\b.gif", 100, CoverProblem.None)]
    [InlineData("C:\\a\\b.txt", 100, CoverProblem.Unsupported)]
    [InlineData("C:\\a\\b", 100, CoverProblem.Unsupported)]
    [InlineData("", 100, CoverProblem.Unsupported)]
    [InlineData("C:\\a\\b.jpg", -1, CoverProblem.Missing)]
    [InlineData("C:\\a\\b.jpg", 0, CoverProblem.Unreadable)]
    [InlineData("C:\\a\\b.jpg", 40L * 1024 * 1024, CoverProblem.None)]
    [InlineData("C:\\a\\b.jpg", 40L * 1024 * 1024 + 1, CoverProblem.TooLarge)]
    public void CheckFile_RefusesWhatCannotBeACover_BeforeReadingIt(string path, long length, CoverProblem expected)
        => Assert.Equal(expected, PlaylistCoverRules.CheckFile(path, length));

    [Fact]
    public void PickerSpec_OffersExactlyTheAcceptedExtensions()
        => Assert.Equal(PlaylistCoverRules.Extensions.Select(e => "*" + e), PlaylistCoverRules.PickerSpec.Split(';'));

    // ── 2. the EXIF turn and the alpha drop ──────────────────────────────────────────────────────────────────────────

    /// <summary>A 3×3 BGRA square whose pixel at (x, y) carries its stored index in the blue byte.</summary>
    static byte[] Square3()
    {
        var b = new byte[3 * 3 * 4];
        for (int i = 0; i < 9; i++) b[i * 4] = (byte)i;
        return b;
    }

    static int[] Indices(byte[] bgra) => Enumerable.Range(0, bgra.Length / 4).Select(i => (int)bgra[i * 4]).ToArray();

    [Theory]
    // stored:  0 1 2 / 3 4 5 / 6 7 8 — the displayed grid, row by row:
    [InlineData(1, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(2, new[] { 2, 1, 0, 5, 4, 3, 8, 7, 6 })]   // mirror horizontally
    [InlineData(3, new[] { 8, 7, 6, 5, 4, 3, 2, 1, 0 })]   // 180°
    [InlineData(4, new[] { 6, 7, 8, 3, 4, 5, 0, 1, 2 })]   // mirror vertically
    [InlineData(5, new[] { 0, 3, 6, 1, 4, 7, 2, 5, 8 })]   // transpose
    [InlineData(6, new[] { 6, 3, 0, 7, 4, 1, 8, 5, 2 })]   // 90° clockwise: the stored left column becomes the top row
    [InlineData(7, new[] { 8, 5, 2, 7, 4, 1, 6, 3, 0 })]   // transverse
    [InlineData(8, new[] { 2, 5, 8, 1, 4, 7, 0, 3, 6 })]   // 90° counter-clockwise
    [InlineData(0, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]   // unknown values leave it alone
    [InlineData(9, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void Orient_TurnsTheSquareUpright(int orientation, int[] expected)
    {
        var b = Square3();
        PlaylistCoverRules.Orient(b, 3, orientation);
        Assert.Equal(expected, Indices(b));
    }

    [Theory]
    [InlineData(6, 8)]
    [InlineData(8, 6)]
    [InlineData(3, 3)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(7, 7)]
    public void Orient_ThenItsInverse_IsTheIdentity(int turn, int inverse)
    {
        var b = Square3();
        PlaylistCoverRules.Orient(b, 3, turn);
        PlaylistCoverRules.Orient(b, 3, inverse);
        Assert.Equal(Indices(Square3()), Indices(b));
    }

    [Fact]
    public void BgraToBgr_DropsAlpha()
    {
        byte[] bgra = [1, 2, 3, 255, 4, 5, 6, 0];
        var bgr = new byte[6];
        PlaylistCoverRules.BgraToBgr(bgra, bgr);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, bgr);
    }

    // ── 3. the wire: upload → register-image → UPDATE_LIST picture (research 09-CAPTURES-playlist.md, notable 1) ───

    static readonly byte[] CapturedPicture = Convert.FromHexString("ab67706c0000da84ac6c5f0ef8b8d128ed868a2b");

    [Fact]
    public void RegisterBody_IsTheCapturedJson()
        => Assert.Equal("{\"uploadToken\":\"ab67706c5393a0f1.1783518211820.9e4ff2c3\"}",
            Encoding.UTF8.GetString(Spotify.Api.CoverRegisterBody("ab67706c5393a0f1.1783518211820.9e4ff2c3")));

    [Fact]
    public void RegisteredPicture_ReadsTheCapturedAnswer()
        => Assert.Equal(CapturedPicture,
            PlaylistCoverRules.RegisteredPicture(Encoding.UTF8.GetBytes("{\"picture\":\"q2dwbAAA2oSsbF8O+LjRKO2Giis=\"}")));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"picture\":42}")]
    [InlineData("{\"picture\":\"\"}")]
    [InlineData("{\"picture\":\"not base64!\"}")]
    [InlineData("[\"q2dwbAAA2oSsbF8O+LjRKO2Giis=\"]")]
    [InlineData("<html>")]
    public void RegisteredPicture_IsNull_ForAnyOtherShape(string body)
        => Assert.Null(PlaylistCoverRules.RegisteredPicture(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void CdnUrl_IsTheSpellingThePlaylistDecodeGivesAttribute3()
        => Assert.Equal("https://i.scdn.co/image/ab67706c0000da84ac6c5f0ef8b8d128ed868a2b", PlaylistCoverRules.CdnUrlOf(CapturedPicture));

    static Pl.ListAttributesPartialState PatchOnTheWire(PlaylistListPatch patch)
    {
        byte[] revision = new byte[24];
        revision[3] = 0x2d;
        byte[] body = Spotify.Encode.PlaylistChanges(revision, [new PlaylistOp(PlaylistOpKind.UpdateList, Patch: patch)], "owner", 1234, 7);
        var op = Assert.Single(Assert.Single(Pl.ListChanges.Parser.ParseFrom(body).Deltas).Ops);
        Assert.Equal(Pl.Op.Types.Kind.UpdateListAttributes, op.Kind);
        return op.UpdateListAttributes.NewAttributes;
    }

    [Fact]
    public void SetCover_CarriesThePictureBytes_AndNothingElse()
    {
        var partial = PatchOnTheWire(new PlaylistListPatch(Picture: CapturedPicture));
        Assert.Equal(CapturedPicture, partial.Values.Picture.ToByteArray());
        Assert.False(partial.Values.HasName || partial.Values.HasDescription || partial.Values.HasCollaborative);
        Assert.Empty(partial.NoValue);
    }

    [Fact]
    public void RemoveCover_IsTheCapturedBody_EmptyValuesAndNoValuePicture()
    {
        var partial = PatchOnTheWire(new PlaylistListPatch(ClearPicture: true));
        Assert.Equal(new byte[] { 0x0a, 0x00, 0x10, 0x03 }, Google.Protobuf.MessageExtensions.ToByteArray(partial));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://i.scdn.co/image/ab67706c0000da84ac6c5f0ef8b8d128ed868a2b", true)]
    [InlineData("C:\\Users\\x\\cache\\cover-uploads\\a.jpg", true)]
    [InlineData("https://mosaic.scdn.co/640/ab67616d00001e02aa", false)]
    [InlineData("spotify:mosaic:ab67616d00001e02aa:ab67616d00001e02bb", false)]
    public void OwnCover_IsAnyImageButAMosaic(string? image, bool own)
        => Assert.Equal(own, PlaylistCoverRules.IsOwnCover(image));

    [Fact]
    public void ACoverFailure_SaysCover_OnlyWhereTheGenericSentenceWouldHave()
    {
        Assert.Equal(Strings.Detail.Edit.CoverFailed, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Unknown, PlaylistEditVerb.Cover));
        Assert.Equal(Strings.Detail.Edit.Forbidden, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Forbidden, PlaylistEditVerb.Cover));
        Assert.Equal(Strings.Detail.Edit.Failed, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Unknown, PlaylistEditVerb.Rename));
    }
}
