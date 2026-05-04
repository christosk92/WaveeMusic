using System.Threading;
using System.Threading.Tasks;

namespace BetterLyrics.Core.Interfaces.Features
{
    public interface ILyricsTransliterator
    {
        Task<string?> GetTransliterationAsync(string text, string targetLangCode, CancellationToken token);
    }
}
