using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

public readonly partial struct Show
{
    sealed class TopicsSlot : Component
    {
        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Shows.Changed.Value;
            var show = p.Model.Read().Show;
            if (!show.IsValid || show.TopicsId.IsEmpty) return new BoxEl();
            var topics = Entities.Strings.Resolve(show.TopicsId).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var children = new Element[topics.Length];
            for (int i = 0; i < topics.Length; i++)
            {
                string topic = topics[i];
                children[i] = Button.Subtle(topic, () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Search, default, Entities.Strings.Intern(topic))));
            }
            return new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0,
                MaxWidth = float.IsFinite(p.Width) ? p.Width : float.NaN, Children = children };
        }
    }
}
