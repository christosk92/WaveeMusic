using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>The topic words under the CTA: the core resolves the topics and the search each one opens; the structure draws them
    /// (<see cref="ShowReaderStyle.Topics"/>).</summary>
    sealed class TopicsSlot : Component
    {
        static readonly Action<string> s_openTopic = static topic => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(topic)));

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Shows.Changed.Value;
            var style = StyleOf(UseContext(global::FluentGpu.Theming.ViewRegistry.Context));
            var show = p.Model.Read().Show;
            if (style is null || !show.IsValid || show.TopicsId.IsEmpty) return new BoxEl();
            var topics = Entities.Strings.Resolve(show.TopicsId).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return style.Topics(new ShowTopicsArgs { Topics = topics, Open = s_openTopic, Width = p.Width });
        }
    }

    /// <summary>The show's aside (W-Show: About and the topics; W-Show-Huge adds the details): the core resolves the description, the topics slot and the
    /// facts; the structure draws them (<see cref="ShowReaderStyle.Aside"/>, contract 1.87). It re-renders when the show row or the snapshot moves.</summary>
    sealed class AsideSlot : Component
    {
        static readonly Action<string> s_open = DetailVerbs.NavigateKey;

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Shows.Changed.Value;
            var style = StyleOf(UseContext(global::FluentGpu.Theming.ViewRegistry.Context));
            if (style?.Aside is not { } aside) return new BoxEl();
            var s = p.Model.Read();
            var show = s.Show;
            bool known = show.IsValid;
            string? about = known && show.Knows(ShowFields.About) && (!show.DescriptionId.IsEmpty || !show.HtmlDescriptionId.IsEmpty)
                ? Entities.Strings.Resolve(!show.HtmlDescriptionId.IsEmpty ? show.HtmlDescriptionId : show.DescriptionId) : null;
            Element? topics = known && show.Knows(ShowFields.Topics) && !show.TopicsId.IsEmpty
                ? Embed.Comp(new SlotProps(p.Model, null), static () => new TopicsSlot()) with { Key = "show:aside:topics" }
                : null;
            var details = new List<ShowDetail>(5);
            if (known && show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty)
                details.Add(new ShowDetail(Loc.Get(Strings.Podcast.Details.Publisher), Entities.Strings.Resolve(show.PublisherId)));
            if (s.Total > 0)
            {
                string word = CadenceWord(s.Cadence);
                string count = s.Total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                details.Add(new ShowDetail(Loc.Get(Strings.Podcast.Details.Episodes), word.Length > 0 ? count + " · " + word : count));
            }
            if (s.OldestYear > 0)
                details.Add(new ShowDetail(Loc.Get(Strings.Podcast.Details.Since), s.OldestYear.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (known && show.Knows(ShowFields.Facts))
                details.Add(new ShowDetail(Loc.Get(Strings.Podcast.Details.Format),
                    Loc.Get((show.Flags & ShowFlags.Video) != 0 ? Strings.Podcast.Details.AudioVideo : Strings.Podcast.Details.Audio)));
            if (known && show.Knows(ShowFields.Rating) && show.RatingCount > 0)
                details.Add(new ShowDetail(Loc.Get(Strings.Podcast.Details.Ratings),
                    (show.RatingX100 / 100f).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " ★ · "
                    + show.RatingCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
            return aside(new ShowAsideArgs { About = about, Topics = topics, Details = details.ToArray(), Open = s_open });
        }
    }
}
