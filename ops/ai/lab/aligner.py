"""The lyric graph and the online CTC Viterbi, shared by the streaming and the whole-song runs.

Graph: the lyric words in order, one CTC token per letter, a word separator between words. Words inside parentheses
(backing vocals and ad-libs, often faint or absent in the separated vocals) are OPTIONAL: the path may skip them.
"""
import re
import unicodedata
import numpy as np

NEG = -1e30
FRAME_S = 0.02
WORD = re.compile(r"[\w'’]+")


def line_words(text: str):
    """[(word, optional)] for one lyric line; a word is optional when it sits inside parentheses."""
    out, depth, pos = [], 0, 0
    for m in WORD.finditer(text):
        seg = text[pos:m.start()]
        depth = max(0, depth + seg.count("(") - seg.count(")"))
        out.append((m.group(0), depth > 0))
        pos = m.end()
    return out


def word_tokens(word: str, vocab: dict) -> list[int]:
    upper = "E" in vocab
    w = word.upper() if upper else word.lower()
    ids = []
    for ch in w.replace("’", "'"):
        if ch not in vocab:
            ch = "".join(c for c in unicodedata.normalize("NFKD", ch) if not unicodedata.combining(c))
        if ch in vocab and ch != "|": ids.append(vocab[ch])
    return ids


def build_graph(lines_words, vocab):
    """tokens, owner per token ((line, word) or None for separators), skip edges (src state, dst state)."""
    sep = vocab["|"]
    tokens, owner, skips = [], [], []
    for li, words in enumerate(lines_words):
        for wi, (w, optional) in enumerate(words):
            ids = word_tokens(w, vocab)
            if not ids: continue
            if tokens: tokens.append(sep); owner.append(None)
            k0 = len(tokens)
            tokens += ids; owner += [(li, wi)] * len(ids)
            k1 = len(tokens) - 1
            if optional: skips.append((2 * k0, 2 * (k1 + 1)))     # blank before the word -> blank after it
    return tokens, owner, skips


class OnlineViterbi:
    """CTC forced alignment that advances frame by frame. The forward pass is exact; a backtrace from the best state at
    the newest frame gives the best partial path, and tokens that end more than `lag` frames back are committed."""

    def __init__(self, tokens, skips=(), blank=0, windows=None, prior_per_s=0.5, skip_cost=2.0):
        """windows: optional (lo_s, hi_s) per token, the time range its line is expected in (from line timestamps).
        A state outside its window pays prior_per_s nats per second of distance, per frame. skip_cost: nats to skip an
        optional word."""
        L = len(tokens)
        self.skip_cost = skip_cost
        self.prior_per_s = prior_per_s
        if windows is not None:
            lo = np.array([w[0] for w in windows]); hi = np.array([w[1] for w in windows])
            slo = np.empty(2 * L + 1); shi = np.empty(2 * L + 1)
            slo[1::2] = lo; shi[1::2] = hi
            slo[0], shi[0] = lo[0], hi[0]; slo[-1], shi[-1] = lo[-1], hi[-1]
            slo[2:-1:2] = np.minimum(lo[:-1], lo[1:]); shi[2:-1:2] = np.maximum(hi[:-1], hi[1:])
            self.win = (slo, shi)
        else:
            self.win = None
        self.S = S = 2 * L + 1
        self.lab = np.full(S, blank); self.lab[1::2] = tokens
        self.skip2 = np.zeros(S, bool)
        if L > 1: self.skip2[3::2] = np.array(tokens[1:]) != np.array(tokens[:-1])
        self.opt_src = np.array([a for a, b in skips], int)
        self.opt_dst = np.array([b for a, b in skips], int)
        self.opt_from = dict(zip(self.opt_dst.tolist(), self.opt_src.tolist()))
        self.dp = None
        self.bp = []
        self.t = 0
        self.committed = 0
        self.spans = [None] * L

    def feed(self, em: np.ndarray):
        lab = self.lab
        for row in em:
            if self.dp is None:
                dp = np.full(self.S, NEG); dp[0] = row[lab[0]]; dp[1] = row[lab[1]]
                for a, b in zip(self.opt_src, self.opt_dst):       # an optional first word may be skipped at once
                    if a == 0: dp[b] = max(dp[b], row[lab[b]])
                self.dp = dp; self.bp.append(np.zeros(self.S, np.int8)); self.t += 1
                continue
            dp = self.dp
            one = np.concatenate(([NEG], dp[:-1]))
            two = np.where(self.skip2, np.concatenate(([NEG, NEG], dp[:-2])), NEG)
            best = np.maximum(dp, np.maximum(one, two))
            bp = np.where(best == dp, 0, np.where(best == one, 1, 2)).astype(np.int8)
            if len(self.opt_src):
                cand = dp[self.opt_src] - self.skip_cost
                better = cand > best[self.opt_dst]
                if better.any():
                    d = self.opt_dst[better]
                    best[d] = cand[better]; bp[d] = 3
            self.bp.append(bp)
            self.dp = best + row[lab]
            if self.win is not None:
                ts = self.t * FRAME_S
                self.dp -= self.prior_per_s * np.maximum(0.0, np.maximum(self.win[0] - ts, ts - self.win[1]))
            self.t += 1

    def _back(self, s, stop_at):
        spans = {}
        for t in range(self.t - 1, -1, -1):
            if s % 2 == 1:
                k = s // 2
                if k < stop_at: break
                a = spans.setdefault(k, [t, t]); a[0] = t
            code = int(self.bp[t][s])
            s = self.opt_from[s] if code == 3 else s - code
        return spans

    def commit(self, lag_frames: int, final=False, deadline_frame: int | None = None):
        """Commit tokens (in order) whose last frame is before t - lag. A skipped optional token commits with no span.
        Returns the newly committed token indices."""
        if final:
            s = self.S - 1 if self.dp[-1] >= self.dp[-2] else self.S - 2
        else:
            s = int(np.argmax(self.dp))
        spans = self._back(s, self.committed)
        horizon = self.t if final else self.t - lag_frames
        if deadline_frame is not None and not final:
            horizon = min(self.t, max(horizon, deadline_frame))      # playback is about to reach these words: commit
        last_k = max(spans) if spans else self.committed - 1
        new = []
        k = self.committed
        while k < len(self.spans):
            if k in spans:
                if spans[k][1] >= horizon: break
                self.spans[k] = spans[k]
            elif k < last_k and any(j > k and spans[j][1] < horizon for j in spans):
                self.spans[k] = None                               # skipped (optional), and the path is past it
            elif final:
                self.spans[k] = None
            else:
                break
            new.append(k); k += 1
        self.committed = k
        if not final and k > 0:
            self.dp[:2 * k] = NEG                                  # the path is past the committed tokens from now on
        return new
