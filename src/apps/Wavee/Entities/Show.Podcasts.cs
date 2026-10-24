using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
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
}
