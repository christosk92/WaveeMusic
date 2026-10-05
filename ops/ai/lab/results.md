## All tracks pooled

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix (8 tracks) | 446 | 2690 ms | 75910 ms | -45 ms | 30% | 34% | 44% | 233 | 2645 ms | 28% |
| Qwen, whole song, vocals (8 tracks) | 446 | 2356 ms | 58165 ms | -40 ms | 36% | 39% | 43% | 230 | 2367 ms | 34% |
| Qwen, 45 s windows, vocals (1 tracks) | 54 | 1240 ms | 85428 ms | +580 ms | 27% | 40% | 47% | 22 (+9 unplaced) | 1820 ms | 20% |
| Qwen, line-anchored, vocals (1 tracks) | 54 | 445 ms | 8019 ms | +135 ms | 28% | 57% | 67% | 18 | 330 ms | 43% |
| wav2vec2 CTC, vocals (full precision) (8 tracks) | 446 | 170 ms | 450 ms | +90 ms | 80% | 91% | 95% | 14 | 130 ms | 82% |
| wav2vec2 CTC, vocals, all on NPU (6 tracks) | 285 | 220 ms | 612 ms | +180 ms | 64% | 85% | 92% | 15 | 150 ms | 78% |
| NPU streaming (just-in-time) (8 tracks) | 446 | 185 ms | 479 ms | +140 ms | 75% | 91% | 95% | 8 (+4 unplaced) | 130 ms | 84% |
| NPU streaming + silence snap (6 tracks) | 285 | 200 ms | 490 ms | +140 ms | 70% | 90% | 96% | 7 (+4 unplaced) | 160 ms | 77% |
| Final: NPU streaming, just-in-time, silence snap (8 tracks) | 446 | 180 ms | 460 ms | +110 ms | 77% | 91% | 95% | 8 (+4 unplaced) | 140 ms | 81% |

Lead removed: each track's median signed error is subtracted first (a constant shift a player can apply).

## Billie Jean - Michael Jackson

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 54 | 28205 ms | 74742 ms | -28205 ms | 4% | 15% | 19% | 42 | 27345 ms | 0% |
| Qwen, whole song, vocals | 54 | 29585 ms | 79558 ms | -25625 ms | 4% | 7% | 9% | 47 | 25900 ms | 4% |
| Qwen, 45 s windows, vocals | 54 | 1240 ms | 85428 ms | +580 ms | 27% | 40% | 47% | 22 (+9 unplaced) | 1820 ms | 20% |
| Qwen, line-anchored, vocals | 54 | 445 ms | 8019 ms | +135 ms | 28% | 57% | 67% | 18 | 330 ms | 43% |
| wav2vec2 CTC, vocals (full precision) | 54 | 315 ms | 607 ms | +290 ms | 46% | 83% | 94% | 3 | 90 ms | 85% |
| wav2vec2 CTC, vocals, all on NPU | 54 | 390 ms | 656 ms | +390 ms | 22% | 80% | 96% | 2 | 90 ms | 83% |
| NPU streaming (just-in-time) | 54 | 390 ms | 577 ms | +385 ms | 26% | 81% | 94% | 1 | 90 ms | 85% |
| NPU streaming + silence snap | 54 | 355 ms | 570 ms | +335 ms | 31% | 85% | 94% | 1 | 90 ms | 80% |
| Final: NPU streaming, just-in-time, silence snap | 54 | 365 ms | 570 ms | +335 ms | 37% | 83% | 94% | 1 | 115 ms | 78% |

## Blinding Lights - The Weeknd

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 35 | 4920 ms | 29390 ms | -4920 ms | 14% | 23% | 43% | 19 | 5490 ms | 3% |
| Qwen, whole song, vocals | 35 | 380 ms | 16138 ms | +130 ms | 43% | 57% | 60% | 12 | 260 ms | 57% |
| wav2vec2 CTC, vocals (full precision) | 35 | 270 ms | 508 ms | +260 ms | 66% | 89% | 94% | 2 | 80 ms | 86% |
| wav2vec2 CTC, vocals, all on NPU | 35 | 320 ms | 852 ms | +300 ms | 46% | 77% | 89% | 2 | 80 ms | 86% |
| NPU streaming (just-in-time) | 35 | 315 ms | 514 ms | +300 ms | 47% | 88% | 97% | 1 (+3 unplaced) | 75 ms | 94% |
| NPU streaming + silence snap | 35 | 305 ms | 514 ms | +300 ms | 50% | 88% | 97% | 1 (+3 unplaced) | 85 ms | 88% |
| Final: NPU streaming, just-in-time, silence snap | 35 | 305 ms | 514 ms | +300 ms | 50% | 88% | 97% | 1 (+3 unplaced) | 85 ms | 88% |

## Bohemian Rhapsody - Remastered 2011 - Queen

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 47 | 73110 ms | 104794 ms | -73110 ms | 6% | 6% | 6% | 44 | 29800 ms | 6% |
| Qwen, whole song, vocals | 47 | 29570 ms | 97122 ms | -27770 ms | 4% | 4% | 4% | 44 | 25640 ms | 2% |
| wav2vec2 CTC, vocals (full precision) | 47 | 230 ms | 538 ms | -190 ms | 72% | 89% | 91% | 3 | 90 ms | 81% |
| wav2vec2 CTC, vocals, all on NPU | 47 | 120 ms | 578 ms | -90 ms | 79% | 85% | 94% | 3 | 50 ms | 81% |
| NPU streaming (just-in-time) | 47 | 100 ms | 400 ms | -70 ms | 87% | 91% | 98% | 1 | 60 ms | 89% |
| NPU streaming + silence snap | 47 | 150 ms | 448 ms | -120 ms | 83% | 91% | 98% | 1 | 110 ms | 87% |
| Final: NPU streaming, just-in-time, silence snap | 47 | 160 ms | 448 ms | -120 ms | 83% | 91% | 98% | 1 | 110 ms | 87% |

## Despacito - Luis Fonsi, Daddy Yankee

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 73 | 720 ms | 5288 ms | +170 ms | 37% | 41% | 59% | 22 | 740 ms | 37% |
| Qwen, whole song, vocals | 73 | 930 ms | 5568 ms | +140 ms | 38% | 42% | 52% | 27 | 940 ms | 37% |
| wav2vec2 CTC, vocals (full precision) | 73 | 190 ms | 306 ms | +180 ms | 89% | 97% | 99% | 0 | 70 ms | 93% |
| NPU streaming (just-in-time) | 73 | 250 ms | 348 ms | +250 ms | 79% | 93% | 97% | 0 | 50 ms | 89% |
| Final: NPU streaming, just-in-time, silence snap | 73 | 250 ms | 348 ms | +240 ms | 81% | 93% | 97% | 0 | 60 ms | 88% |

## HUMBLE. - Kendrick Lamar

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 51 | 200 ms | 18290 ms | +200 ms | 63% | 63% | 71% | 14 | 180 ms | 59% |
| Qwen, whole song, vocals | 51 | 180 ms | 11468 ms | +180 ms | 69% | 71% | 75% | 11 | 112 ms | 69% |
| wav2vec2 CTC, vocals (full precision) | 51 | 110 ms | 230 ms | +100 ms | 98% | 100% | 100% | 0 | 70 ms | 98% |
| wav2vec2 CTC, vocals, all on NPU | 51 | 150 ms | 310 ms | +150 ms | 88% | 96% | 96% | 1 | 80 ms | 96% |
| NPU streaming (just-in-time) | 51 | 120 ms | 240 ms | +110 ms | 98% | 100% | 100% | 0 | 80 ms | 100% |
| NPU streaming + silence snap | 51 | 140 ms | 240 ms | +90 ms | 96% | 100% | 100% | 0 | 90 ms | 88% |
| Final: NPU streaming, just-in-time, silence snap | 51 | 130 ms | 220 ms | +90 ms | 98% | 100% | 100% | 0 | 90 ms | 94% |

## Shape of You - Ed Sheeran

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 88 | 525 ms | 16164 ms | -30 ms | 42% | 49% | 64% | 29 | 495 ms | 40% |
| Qwen, whole song, vocals | 88 | 185 ms | 10896 ms | +0 ms | 59% | 59% | 66% | 27 | 185 ms | 59% |
| wav2vec2 CTC, vocals (full precision) | 88 | 100 ms | 309 ms | -60 ms | 90% | 91% | 94% | 0 | 95 ms | 89% |
| NPU streaming (just-in-time) | 88 | 65 ms | 384 ms | -10 ms | 89% | 92% | 93% | 1 | 60 ms | 89% |
| Final: NPU streaming, just-in-time, silence snap | 88 | 75 ms | 386 ms | -15 ms | 89% | 92% | 93% | 1 | 75 ms | 89% |

## Smells Like Teen Spirit - Nirvana

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 49 | 67410 ms | 123946 ms | -1300 ms | 0% | 0% | 2% | 47 | 68710 ms | 2% |
| Qwen, whole song, vocals | 49 | 42710 ms | 72804 ms | -2948 ms | 4% | 6% | 6% | 42 | 41408 ms | 2% |
| wav2vec2 CTC, vocals (full precision) | 49 | 170 ms | 644 ms | +120 ms | 82% | 86% | 96% | 2 | 80 ms | 84% |
| wav2vec2 CTC, vocals, all on NPU | 49 | 240 ms | 1428 ms | +230 ms | 55% | 78% | 84% | 5 | 130 ms | 73% |
| NPU streaming (just-in-time) | 49 | 240 ms | 512 ms | +230 ms | 67% | 88% | 96% | 1 | 90 ms | 86% |
| NPU streaming + silence snap | 49 | 240 ms | 512 ms | +230 ms | 69% | 88% | 96% | 1 | 90 ms | 86% |
| Final: NPU streaming, just-in-time, silence snap | 49 | 240 ms | 512 ms | +230 ms | 69% | 88% | 96% | 1 | 90 ms | 86% |

## bad guy - Billie Eilish

| Method | Lines | Median | p90 | Lead | <=300 ms | <=500 ms | <=1 s | >2 s off | Median, lead removed | <=300 ms, lead removed |
|---|---|---|---|---|---|---|---|---|---|---|
| Qwen, whole song, mix | 49 | 140 ms | 42010 ms | -50 ms | 59% | 59% | 63% | 16 | 190 ms | 59% |
| Qwen, whole song, vocals | 49 | 420 ms | 39301 ms | -119 ms | 47% | 53% | 55% | 20 | 301 ms | 49% |
| wav2vec2 CTC, vocals (full precision) | 49 | 80 ms | 922 ms | +60 ms | 86% | 86% | 92% | 4 | 70 ms | 86% |
| wav2vec2 CTC, vocals, all on NPU | 49 | 100 ms | 324 ms | +80 ms | 90% | 90% | 94% | 2 | 50 ms | 90% |
| NPU streaming (just-in-time) | 49 | 95 ms | 657 ms | +85 ms | 90% | 90% | 90% | 3 (+1 unplaced) | 55 ms | 90% |
| NPU streaming + silence snap | 49 | 80 ms | 657 ms | +60 ms | 90% | 90% | 90% | 3 (+1 unplaced) | 70 ms | 90% |
| Final: NPU streaming, just-in-time, silence snap | 49 | 80 ms | 657 ms | +60 ms | 90% | 90% | 90% | 3 (+1 unplaced) | 70 ms | 90% |

