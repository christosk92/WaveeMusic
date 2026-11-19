// ── Podcasts/PodcastRater.cs — the ONE rate verb of the podcast pages (the show page and the audiobook page) ────────────────────────────────────────
//
// A page host owns one `PodcastRater` per subject. It gives the page:
//   · Rate(int)   the write: 1..5 stars through `Spotify.Podcasts.RateAsync`, then the show's rating group refetched and "Your rating was saved."
//                 (`podcast.reader.ratingSaved`); a failed write leaves the stars as they were and says so in the picker. Null under `--fake`: the demo
//                 session has no account to write to.
//   · State       `PodcastRateVm`, computed from what the page's snapshot says (the title, the public ratings line, the listener's own stars, whether
//                 the account may rate) plus the rater's own cells (busy, the failure, the last stars written).
// The stars take a pick only when the account may rate (`ShowFlags.CanRate`, or the book's own flag) and the session can write (not `--fake`): the
// gate the old show-page rating editor had (PodcastReaderUI.RatingEditor, removed with the older reader pages in a9a799dac), now on both pages.

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The rate verb and the rate control's state of one show or book (see the file header).</summary>
internal sealed class PodcastRater
{
    /// <summary>What the page's snapshot says about the rating: the name ("Rate {name}"), the public ratings line ("12,431 ratings", "" = none),
    /// the listener's own stars (0 = none) and whether the account may rate.</summary>
    public readonly record struct Facts(string Name, string Ratings, int Mine, bool CanRate);

    readonly EntityUri _subject;
    readonly Func<Facts> _facts;
    readonly Signal<bool> _busy = new(false);
    readonly Signal<bool> _failed = new(false);
    readonly Signal<int> _written = new(0);

    /// <summary>The write, or null when the session cannot write a rating (`--fake`).</summary>
    public Action<int>? Rate { get; }

    /// <summary>The state's compute: a STABLE delegate for the host's <c>UseComputed</c>. It reads the page's snapshot through the facts thunk.</summary>
    public Func<PodcastRateVm> Compute { get; }

    /// <param name="subject">The show or the book (`spotify:show:…`).</param>
    /// <param name="facts">Reads the page's snapshot (subscribes the compute to it).</param>
    public PodcastRater(EntityUri subject, Func<Facts> facts)
    {
        _subject = subject;
        _facts = facts;
        Rate = Platform.Args.Fake ? null : Submit;
        Compute = StateOf;
    }

    PodcastRateVm StateOf()
    {
        var f = _facts();
        bool busy = _busy.Value, failed = _failed.Value;
        int written = _written.Value;
        int mine = written > 0 ? written : Math.Clamp(f.Mine, 0, Controls.StarCount);
        bool writable = Rate is not null && f.CanRate;
        string line = mine > 0
            ? f.Ratings.Length > 0 ? f.Ratings + " · " + Strings.Podcast.Yours(mine) : Strings.Podcast.Yours(mine)
            : f.Ratings;
        string note = busy ? Loc.Get(Strings.Podcast.Rate.Saving)
            : failed ? Loc.Get(Strings.Podcast.Reader.MutationFailed)
            : Rate is null ? Loc.Get(Strings.Podcast.Rate.Demo)
            : !f.CanRate ? Loc.Get(Strings.Podcast.Rate.Closed)
            : "";
        return new PodcastRateVm(mine, writable && !busy, busy, failed && !busy, Strings.Podcast.Rate.Title(f.Name), line, note);
    }

    async void Submit(int value)
    {
        if (value is < 1 or > Controls.StarCount || _busy.Peek() || !_subject.IsValid) return;
        string uri = _subject.Text;
        uint epoch = Entities.ScopeEpoch.Peek();
        _busy.Value = true;
        _failed.Value = false;
        Spotify.Podcasts.Mutation result;
        try { result = await Spotify.Podcasts.RateAsync(uri, value, CancellationToken.None).ConfigureAwait(false); }
        catch { result = new(false, 0, true); }
        Store.Post(() =>
        {
            _busy.Value = false;
            if (Entities.ScopeEpoch.Peek() != epoch) return;
            if (!result.Success) { _failed.Value = true; return; }
            _written.Value = value;
            var show = Entities.Current.Shows.TryGetSlot(_subject.Id, out int slot) ? new Show(slot) : default;
            if (show.IsValid)
            {
                Entities.Invalidate(Entities.Current.Shows, [show.Slot], (uint)ShowFields.Rating);
                Entities.Ensure(show, ShowFields.Rating);
            }
            Notify.Say(Loc.Get(Strings.Podcast.Reader.RatingSaved), InfoBarSeverity.Success);
        });
    }
}
