# Mirthwood: Echoes roadmap

## Inbox

- [ ] Assign each resident a random matching voice in their campaign. Use every voice in each cast before the next round, keep assignments across reloads and save changes, and give replacements their own draw.
  On 2026-11-23 the offline regression covered three rounds for each 16-voice cast, player overrides, roster changes, concurrent draws and recovery after a failed write. Separate processes using Mirthwood’s Mono runtime reopened the same assignments.
  On 2026-11-23 independent review reproduced a second draw for the same resident when another mod changed JSON formatting. The voice pool now controls how it reads and writes resident keys and cast files.

## In progress

## Completed

- [x] Give each speaking resident their current place, calendar, nearby people, weather and landmarks. Include every knowable event today and the next public notice in private Talk, passing remarks, groups and generated quest dialogue. Preserve interrupted activities, the source of local news, privacy, and evidence of what happened after an earlier plan.
  Copied-save checks covered the Fall 12 joust and Fall 17 festival notices, actual event starts and ends, local weather and company, interrupted work, player-led topics, quest choices and retries on divergent saves.
  On 2026-11-23, Drystan credited Averil for relaying Michael’s streamer and duel accounts. Graham recalled only the exchange he heard after joining. Drystan preserved Michael’s denial and declined to speak for both of them. When Michael said he liked lying low, Drystan withheld the source in the wider group and recalled Averil’s line in private. Anastasia had been absent and said she did not hear Averil.

- [x] Keep incidental resident voices quiet throughout private and group Talk, including text-only conversations, and during generated quest offers. Protect spoken passing replies and the overhead quest farewell until the last word. Resume ambient reactions when the conversation ends.
  On 2026-11-23 a copied game reproduced a resident greeting and a toddler call interrupting Echoes speech. All 23 voice checks passed on the standalone audio build. Private and group farewells played through, and speakers and a nearby bystander stayed quiet across 16 audible clips. When Michael declined Averil’s job, the native offer closed and she said “I understand, friend. Goodbye!” overhead. Normal greetings resumed after the farewell.

- [x] Keep 30 in-game days of ordinary dialogue, compact closed days by listener and preserve character knowledge, promises and major moments. On 2026-11-21 the copied slot kept Michael’s words through day 30. On day 31 Averil remembered plum tart after the original exchange expired. The journal and database checks covered who heard a disclosure, corrected preferences, replay and save rewinds.
  On 2026-11-21 the retention rule changed. Exact dialogue is kept for the current game day. The next day’s journal shows a compact, attributed recap. Searchable recaps last 30 elapsed game days including the day of the conversation. Promises and learned details are available on the day they are spoken.

- [x] Keep complete player drafts, submitted lines and resident speech in one overhead bubble. On 2026-11-21 the copied game compared the native bark with 85% lettering and verified a complete 1,200-character draft, its submitted text, a long italicized reply and all four screen edges. Enter responds or dismisses a farewell directly.
  On 2026-11-21, the copied game also checked the player's lines to Averil and Drystan, the group goodbye and all three waiting dots. The parchment now fits the rendered letters with a small margin, and names wrap as whole words.

- [x] Separate each resident’s daily observations and summaries from their private and group conversations. On 2026-11-21, Averil recalled collecting flour and Drystan recalled hearing it from her in a copied save. The journal and dialogue checks covered older records, two visits on one day, different audiences and an earlier save.

- [x] Start Group Talk with the rebindable **T** key and gather nearby residents at separate reachable spots. On 2026-11-21, the copied game verified typing T, a late arrival and early departure, a spoken goodbye, native relationship changes and saved scoring receipts. Mirthwood uses G for the fourth interaction and placement grid.

- [x] Render player-directed native social cues as the resident’s own words, with physical gestures in italics. On 2026-11-20, Averil stayed with a bereavement and a request for quiet, followed the player into a joke, and opened with a joke and a flirt in the copied save. Mirthwood selected the return cards and the conversation journal matched the displayed lines.

- [x] Pause the selected resident while a Talk reply is pending and cycle one to three periods in their native overhead bubble until the reply appears. On 2026-11-20, an isolated Mirthwood session verified the animation, stationary resident, advancing clock, first reply, cancellation, retry and delayed voice fallback.
