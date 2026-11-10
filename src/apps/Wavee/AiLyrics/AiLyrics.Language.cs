// ── AiLyrics/AiLyrics.Language.cs ────────────────────────────────────────────────────────────────────────────────────
// Rules.DecideLanguage, Rules.LanguageDecision, Rules.SpellableShare: which aligner model may time a lyric, if any
//
// Role: CORE (pure: no I/O, no clock reads, no engine)
//
// The aligner is a per-language CTC model: a model that cannot spell the lyric, or that hears another language, still
// finds a path through the audio and returns confident-looking nonsense. So the language is decided from evidence, in
// this order, and there is NO default: anything unclear is skipped and the track keeps the provider's line sync.
//
//   1. script, per word (Align.Words, the aligner's own tokenisation): Hangul, Latin or another script
//   2. another script (kana, Han, Cyrillic, Greek, Thai, Arabic, ...) >= 10 % of the letters: no model, skip
//      (measured on 972 provider documents: en/nl/ko-tagged lyrics have 0 % CJK at most, Japanese ones 18 % at least)
//   3. Hangul against Latin: the majority script picks the family; the minority's lines stay line-level (the aligner
//      times only words its model spells, and a line with less than half its letters timed is never word-synced)
//   4. a Latin lyric: a provider tag naming a Latin-script language decides (installed or not); untagged, or tagged
//      with a non-Latin language (an English K-pop song tagged "ko"), its words are scored against the model languages
//      and against profiles of other Latin languages, and only a clear winner is taken (see ModelLanguages)
//   5. before a job starts, the chosen model must spell at least half the words (SpellableShare, run by the worker)

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class AiLyrics
{
    public static partial class Rules
    {
        /// <summary>The version of this decision procedure. It is saved in every results file, and a file decided by
        /// another version is not replayed (the track is timed again).</summary>
        public const int DecisionVersion = 2;

        /// <summary>Letters of a script no model speaks (step 2), as a share of all letters, from which a lyric is skipped.</summary>
        public const double OtherScriptSkipShare = 0.10;

        /// <summary>Step 5: words with a letter outside the model's vocabulary must stay under this share.</summary>
        public const double UnspellableSkipShare = 0.50;

        /// <summary>A winner by word score needs at least this many hits on its own list.</summary>
        public const int MinWordHits = 3;

        /// <summary>…and at least this many times the share of the runner-up.</summary>
        public const double WinnerMargin = 2.0;

        /// <summary>The Latin languages an aligner pack offers and the share of a lyric's words (ScoringWords) their
        /// list must reach, besides <see cref="WinnerMargin"/>. Measured over the 1,027 cached provider documents: Dutch-
        /// tagged lyrics score 29 % at the least and a foreign list's winner never reaches 17 % on the Dutch list;
        /// untagged English lyrics run as low as 23-29 % (repetitive hooks: "oh oh oh"), while the highest English share
        /// of a lyric another list wins is 28 % — the 2x margin, not the floor, is what keeps those out.</summary>
        static readonly (string Language, double MinShare)[] ModelLanguages = [("en", 0.20), ("nl", 0.20), ("es", 0.25)];

        /// <summary>Distinctive function words per language. The model languages are the candidates; the rest are
        /// NEGATIVE profiles: a French, German or romanised Japanese lyric wins on its own list, so no model is chosen.
        /// A negative profile keeps only words no candidate list has, and never English "I" (Polish "i", Italian "i"
        /// and the romanised lists' "you", "he", "to" outscored plain English songs).</summary>
        static readonly (string Language, HashSet<string> Words)[] Profiles = Distinctive(
        [
            ("en", W("the and you your i'm it's that this with for are was they she what when don't can't know never because there their would could should i've i'll you're we're have has been were from just like about them then than these those which where who how every nothing something my me we he his her not but all so do if at up out no now of to in on is be")),
            ("nl", W("het een ik je jij jou niet van dat op met voor zijn maar als wat mijn wij nog ook naar bij om heb ben kan nu waar alles nooit altijd wil weet jouw mij zij geen meer dan laat gaan komt zonder samen deze dit hij haar hem ons onze heeft hebben wordt worden was waren toch wel dus want omdat toen al uit aan over veel echt gewoon moet zou zal m'n z'n d'r 't weer even iets niets niemand iemand")),
            ("es", W("el los las que y una del por con para tu yo lo te como pero más muy porque estoy eres soy está todo nada cuando sin quiero tengo contigo corazón ya no sé qué mí él ella nos me se le si hay fue ser tiene vida noche siempre nunca donde sólo solo esto eso ese esta este ahora hoy aquí voy vas va mis tus sus conmigo también")),
            ("fr", W("le les des je tu il elle nous vous ils est et un une du au aux ce cette ces mon ma mes ton ta tes son sa ses que qui dans pour pas plus ne sur avec comme mais ou où tout tous toi moi j'ai c'est suis es sont ont être avoir fait dit quand si bien très sans sous chez ça encore toujours jamais rien peut veux vais")),
            ("de", W("der die das und ich du er sie es wir ihr ist nicht ein eine einen dem den des mit für auf von zu im am auch aber oder wenn dann nur noch schon mein dein sein mich dich mir dir uns was wie wo warum weil dass kann will muss bin bist sind hat habe haben wird war so doch immer nie alles nichts")),
            ("it", W("il lo gli le di che e un una del della dei per con non mi ti si ci io tu lui lei noi voi è sono sei ho hai ha come ma più se anche quando dove perché tutto niente mai sempre solo mio tuo suo questa questo quello nel nella sul alla ai ancora già poi cosa così")),
            ("pt", W("o os as um uma de do da dos das em no na nos nas por para com não que e eu tu você ele ela nós me te se meu minha teu seu sua é são está estou foi ser ter tem tenho mais mas como quando onde porque tudo nada nunca sempre só já também muito isso essa esse aqui agora vou vai pra")),
            ("sv", W("och jag du han hon vi ni de det den ett en är var inte att som på med för av till från om men eller när där här har hade kan ska vill mig dig sig min din sin mitt ditt vår er alla allt inget aldrig alltid bara också nu så då vad hur varför")),
            ("da", W("og jeg du han hun vi de det den et en er var ikke at som på med for af til fra om men eller når hvor her har havde kan skal vil mig dig sig min din sin mit dit vores alle alt intet aldrig altid bare også nu så da hvad hvordan hvorfor")),
            ("id", W("yang di dan ku aku kau kamu ini itu tak tidak dengan untuk ke dari pada akan telah sudah bisa hati cinta selalu hari saat apa bila jika karena hanya saja lagi juga ada mu nya kita kami mereka dia")),
            ("tl", W("ang ng mga sa na ko mo ka ako ikaw at ay si ni kung pa lang din rin ito iyo aking puso mahal nang may wala hindi oo siya kami tayo sila natin namin nila")),
            ("tr", W("ve bir bu ben sen o biz siz için ile de da ne gibi ama çok daha en her şey var yok değil mi mı ki ya beni seni bana sana benim senin")),
            ("pl", W("i w z na nie to się że jest do jak ale co tak ja ty on ona my wy mnie cię mi ci tylko już był była być mam masz")),
            ("ro", W("și în la de pe cu nu că este sunt eu tu el ea noi voi mai dar sau când cum unde ce un o din pentru")),
            ("fi", W("ja on ei se että en et hän me te he oli olen olet kun niin mutta tai jos vain nyt minä sinä mä sä")),
            ("ja-Latn", W("wa no ni wo ga de to mo kara made yo ne kimi boku watashi anata ore omae koto mono nai aru iru shite kono sono ano itsumo zutto dake demo datte nani doko dare")),
            ("ko-Latn", W("neun eun reul eul ga i e eseo do man naega nega neoui naui uri geu jeo ireon geureon hae haeyo hago sipeo saranghae isseo eopseo")),
            ("zh-Latn", W("wo ni ta de shi bu zai you he zhe na ge le ma ba men hao xiang ai xin shuo lai qu dui yao hui")),
        ]);

        static (string Language, HashSet<string> Words)[] Distinctive((string Language, HashSet<string> Words)[] all)
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal) { "i" };
            foreach (var (language, words) in all) if (IsModelLanguage(language)) candidates.UnionWith(words);
            foreach (var (language, words) in all) if (!IsModelLanguage(language)) words.ExceptWith(candidates);
            return all;
        }

        static bool IsModelLanguage(string language)
        {
            foreach (var (l, _) in ModelLanguages) if (l == language) return true;
            return false;
        }

        static HashSet<string> W(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        /// <summary>Primary language subtags written in the Latin script. A tag outside this set (ko, ja, zh, ru, el, …)
        /// says nothing about which LATIN language a Latin lyric is in.</summary>
        static readonly HashSet<string> LatinTags = new(StringComparer.Ordinal)
        {
            "af", "az", "ca", "cs", "cy", "da", "de", "en", "es", "et", "eu", "fi", "fil", "fo", "fr", "fy", "ga", "gd", "gl",
            "ha", "haw", "hr", "ht", "hu", "id", "ig", "is", "it", "jv", "la", "lb", "lt", "lv", "mg", "mi", "ms", "mt", "nb",
            "nl", "nn", "no", "oc", "pl", "pt", "qu", "rm", "ro", "sk", "sl", "sm", "sn", "so", "sq", "su", "sv", "sw", "tl",
            "tn", "to", "tr", "uz", "vi", "wo", "xh", "yo", "zu",
        };

        /// <summary>How the language was decided (or why none was).</summary>
        public enum LanguageStep : byte { NoLetters, OtherScript, Hangul, Tag, Words, Unclear }

        /// <summary>One decision with its evidence, for the log. <see cref="Language"/> is null when no model may time
        /// the lyric. Shares are of letters (scripts) or of Latin words (scores).</summary>
        public readonly record struct LanguageDecision(
            string? Language, LanguageStep Step, string Tag,
            int Letters, int Hangul, int Latin, int Other,
            int Words, string Best, int BestHits, double BestShare, string Second, double SecondShare)
        {
            /// <summary>"step=Words tag=- letters=812 hangul=0.00 latin=1.00 other=0.00 words=301 best=en:0.41(123) second=nl:0.04".</summary>
            public string Describe()
            {
                var ci = CultureInfo.InvariantCulture;
                double l = Math.Max(1, Letters);
                var sb = new StringBuilder();
                sb.Append("step=").Append(Step).Append(" tag=").Append(Tag.Length == 0 ? "-" : Tag)
                  .Append(" letters=").Append(Letters.ToString(ci))
                  .Append(" hangul=").Append((Hangul / l).ToString("0.00", ci))
                  .Append(" latin=").Append((Latin / l).ToString("0.00", ci))
                  .Append(" other=").Append((Other / l).ToString("0.00", ci));
                if (Words > 0)
                    sb.Append(" words=").Append(Words.ToString(ci))
                      .Append(" best=").Append(Best).Append(':').Append(BestShare.ToString("0.00", ci)).Append('(').Append(BestHits.ToString(ci)).Append(')')
                      .Append(" second=").Append(Second).Append(':').Append(SecondShare.ToString("0.00", ci));
                return sb.ToString();
            }
        }

        /// <summary>The aligner language for a lyric, or "" when no model may time it (see <see cref="DecideLanguage"/>;
        /// the file header gives the order). Whether that language is INSTALLED is the caller's question.</summary>
        public static string LanguageOf(Lyrics.Doc doc) => DecideLanguage(doc).Language ?? "";

        /// <summary>The language decision with its evidence: script shares first, then the provider's tag, then word
        /// scores; never a default.</summary>
        public static LanguageDecision DecideLanguage(Lyrics.Doc doc)
        {
            string tag = PrimaryTag(doc.Language);
            int letters = 0, hangul = 0, latin = 0;
            for (int li = 0; li < doc.Lines.Count; li++)
                foreach (var word in Align.Words(doc.Lines[li].Text ?? ""))
                    foreach (char c in word.Text.Normalize(NormalizationForm.FormC))
                    {
                        if (!char.IsLetter(c)) continue;
                        letters++;
                        if (IsHangul(c)) hangul++;
                        else if (IsLatin(c)) latin++;
                    }
            int other = letters - hangul - latin;
            var d = new LanguageDecision(null, LanguageStep.NoLetters, tag, letters, hangul, latin, other, 0, "", 0, 0, "", 0);
            if (letters == 0) return d;
            if (other >= OtherScriptSkipShare * letters) return d with { Step = LanguageStep.OtherScript };
            if (hangul > latin) return d with { Language = "ko", Step = LanguageStep.Hangul };
            if (LatinTags.Contains(tag)) return d with { Language = tag, Step = LanguageStep.Tag };

            // untagged, or tagged with a language of another script: the words decide, and only a clear winner counts
            var hits = new int[Profiles.Length];
            int words = 0;
            for (int li = 0; li < doc.Lines.Count; li++)
                foreach (string w in ScoringWords(doc.Lines[li].Text ?? ""))
                {
                    words++;
                    for (int p = 0; p < Profiles.Length; p++) if (Profiles[p].Words.Contains(w)) hits[p]++;
                }
            if (words == 0) return d with { Step = LanguageStep.Unclear };
            int best = 0, second = -1;
            for (int p = 1; p < Profiles.Length; p++)
            {
                if (hits[p] > hits[best]) { second = best; best = p; }
                else if (second < 0 || hits[p] > hits[second]) second = p;
            }
            d = d with
            {
                Step = LanguageStep.Unclear, Words = words,
                Best = Profiles[best].Language, BestHits = hits[best], BestShare = (double)hits[best] / words,
                Second = Profiles[second].Language, SecondShare = (double)hits[second] / words,
            };
            foreach (var (language, minShare) in ModelLanguages)
            {
                if (language != d.Best) continue;
                if (d.BestHits >= MinWordHits && d.BestShare >= minShare && d.BestShare >= WinnerMargin * d.SecondShare)
                    return d with { Language = language, Step = LanguageStep.Words };
            }
            return d;
        }

        /// <summary>The primary subtag of a provider's language tag, lower case; "" for none, "und" or "zxx".</summary>
        static string PrimaryTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            int cut = tag.IndexOfAny(['-', '_']);
            string primary = (cut > 0 ? tag[..cut] : tag).Trim().ToLowerInvariant();
            return primary is "und" or "zxx" ? "" : primary;
        }

        static bool IsHangul(char c) => c is (>= '가' and <= '힣') or (>= 'ᄀ' and <= 'ᇿ') or (>= '㄰' and <= '㆏');

        static bool IsLatin(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 'À' and <= 'ɏ')
            or (>= 'Ḁ' and <= 'ỿ') or (>= 'Ａ' and <= 'ｚ');

        /// <summary>The words a lyric is scored on: runs of letters, apostrophes and hyphens (so "don't", "m'n" and
        /// "oh-whoa" stay whole), trimmed of leading and trailing apostrophes and hyphens, lower case; only words whose
        /// letters are all Latin (the Hangul of a mixed lyric is not evidence about its Latin language).</summary>
        static IEnumerable<string> ScoringWords(string text)
        {
            string t = text.Normalize(NormalizationForm.FormC).Replace('’', '\'').ToLowerInvariant();
            int start = -1;
            for (int i = 0; i <= t.Length; i++)
            {
                char c = i < t.Length ? t[i] : ' ';
                if (char.IsLetter(c) || c is '\'' or '-') { if (start < 0) start = i; continue; }
                if (start < 0) continue;
                string w = t[start..i].Trim('\'', '-');
                start = -1;
                if (w.Length == 0) continue;
                bool latin = true;
                foreach (char ch in w) if (char.IsLetter(ch) && !IsLatin(ch)) { latin = false; break; }
                if (latin) yield return w;
            }
        }

        /// <summary>Step 5: the share of a lyric's words (those with at least one letter) whose every letter the model
        /// can spell (<see cref="Align.Vocab.Spells"/>); 0 when there are none.</summary>
        public static double SpellableShare(Lyrics.Doc doc, Align.Vocab vocab, out int words)
        {
            words = 0;
            int spellable = 0;
            for (int li = 0; li < doc.Lines.Count; li++)
                foreach (var word in Align.Words(doc.Lines[li].Text ?? ""))
                {
                    if (!vocab.Spells(word.Text, out bool hasLetter) && hasLetter) { words++; continue; }
                    if (!hasLetter) continue;
                    words++; spellable++;
                }
            return words == 0 ? 0 : (double)spellable / words;
        }
    }
}
