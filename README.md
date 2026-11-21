# Mirthwood: Echoes

Double-click **Mirthwood: Echoes** on the Windows desktop to open its Web UI. **Start Game** is selected when the window opens. Click it or press **Enter** to start the Echoes backend, verify the module and launch Mirthwood through Steam. For journal or settings access before playing, use the **Server** switch.

Keep the companion window open behind the game while playing. **Exit** and the window’s **×** close Echoes and stop both its Web UI and backend. Minimizing suspends both. Restoring the window reopens the Web UI and resumes a previously running backend. The desktop window owns the session, including when the local interface is also open in a browser. [Local Web UI while the companion is open](http://127.0.0.1:17841).

Choose a provider and enter a default model under **LLM Provider / Models**. Conversation and memory overrides inherit the provider’s default when blank. NanoGPT, OpenRouter and Ollama Cloud use their own API keys. OpenAI-compatible accepts a full chat completions URL. **Codex subscription** uses the official app-server. Choose **Windows Codex** to use the ChatGPT sign-in in the Windows Codex app, or **Echoes service account** for a separate login in WSL. The Windows choice opens a temporary dialogue session in the user's installed Codex app through standard input and output. To sign in to the WSL account, run the following in Windows.

```powershell
wsl -d ReignServer -u mirthwood_echoes -- codex login --device-auth
```

**Characters** is a journal for the selected save. Search by resident, town or occupation. Choose a resident to read their profile, including native temperament, conversation interests, traits, gifts, relationships, appearance and inventory when recorded. **Original save details** preserves the underlying controller, components and population register.

**In Mirthwood**, choose **Greet** or **Jeer** for a passing gesture. The resident reacts immediately and Mirthwood sets its usual cooldown. Choose **Talk** for a conversation at any time the resident offers ordinary chat. On a crowded menu, **Next** and **Back** reach the remaining actions with the same four native controls. Type your words or a gesture into the brown bubble above your character. The old “Strike Up A Conversation” topic and response screens stay hidden while Echoes is enabled. Quests and authored scenes keep their **Speak** option. Typing pauses the campaign clock. The bubble grows, wraps at 36 characters with word breaks where possible, and scrolls to the caret on longer drafts. Type `*I tip my hat.* Good morning.` to italicize the gesture. **Enter** sends your words and restores the previous game speed. The resident’s reply appears above their head. A resident tells the joke, asks the question or voices the flirt in their own words. Italics show visible gestures such as a smile or a hand on your sleeve. The player’s last words guide the subject. A native card can color an answer or suggest a related thought, and a personal disclosure can have room to breathe. If you say goodbye, the resident can answer a last question and take their leave in character. Press **Enter** to answer, or to leave after either of you ends Talk. **Esc** leaves at any time. Reopen Talk with the same resident to recover an interrupted exchange.

**Following up on a native interaction.** When a gift actually changes hands or a worker hands over goods, the resident says a short line above their head. Press **Enter** to reply, choose **Talk**, or keep walking. Time runs while the opening line is visible. The resident’s words stay on screen as you type. The player’s bubble moves clear of the reply, and the opening line clears the native action buttons. Gifts use the spoken reaction when available. Collection lines use the confirmed quantity and where the goods ended up. After a passing gesture, a hire or a warning, choose **Talk** to ask the resident about it. For example, “Why that face?” can refer to the gift you just handed over. The brown player bubble holds that context while you type. A resident can also draw on their own earlier work, conversations, household events, letters, funeral attendance and confirmed generated quest hand-ins. The sender knows what they enclosed in a gift letter. Opening it and receiving its attachment belong to the player's journal. Spouses can recall missed visits, changes to household gold, items they received or took from storage, crops that wilted and livestock whose happiness decreased during their own household activities.

Mirthwood still performs the original action, payment, hand-in or warning at its usual time. A native card addressed to you can open a conversation in the resident’s own words. Longer opening remarks turn the page automatically. Press **Enter** to read ahead and again on the last page to reply. **PgUp**, **PgDn** and the mouse wheel let you reread a page. A nearby exchange between two other residents stays in Mirthwood. Each submitted Talk turn uses one conversation call, including when the resident chooses a new card or says goodbye. **Rumors**, **Complain** and **Cheer Up** are available to free text when the resident's native deck offers those cards. Hearing a warning supplies the words the resident spoke. A mourner can refer to a spouse or friendship recorded for the deceased resident. An attendance record on its own supplies no family connection. Theft-specific accusations are disabled pending an attributable native witness.

**First impressions in regular Talk.** A resident who knows you can draw on your public title and their own experience with you. A stranger can place a well-known name when you introduce yourself. Ask what you are wearing and the resident can describe visible headwear, torso clothing and legwear. A vanity cuirass takes precedence over the armor beneath it. Hiding armor can reveal the shirt or trousers you actually have on. In the copied Brightoak save, those changes were visible on the player and in the resident’s replies. The in-game references are the inventory title and the dressed player in the world.

Echoes saves a history marker with the game. Loading an earlier marked save restores the conversations, firsthand observations, promises and memory summaries reachable from that point. Individual residents are matched by their native identity and save owner. Pre-0.7 entries retain their original date-based behavior.

The API key and default model fields accept **Ctrl+V**, **Shift+Insert**, or the **Paste** button. Use **Save changes** to store the profile. Codex profiles have a separate reasoning setting for everyday conversation.

**When a resident offers a quest**, choose **Speak**. The native portrait shows the original offer and the resident asks for your help in character. Type a reply and press **Enter**. **Quest details** brings the original wording back while you decide. Ask about the job, take it on the stated terms, or turn it down. The first request to change the deal gets a polite refusal. A second counteroffer in the same discussion ends it as a native decline. Asking what a job pays does not count as bargaining. **Esc** leaves the offer undecided.

Echoes uses the resident's journal profile, the actual quest text and its generated objectives for one model request per turn. The module checks the same resident, save and live offer again before invoking the original Accept or Decline choice. Mirthwood owns the quest journal, map markers and relationship effects. Confirmed outcomes appear in the resident's conversation history. Other scripted Speak scenes, turn-ins and multi-stage choices follow their original dialogue. The native buttons are available if Echoes cannot prepare the discussion.

The **Server** switch above the four main sections starts or stops the Echoes backend. A small localhost console keeps the switch available when the backend is off. **Install module**, **Update module** or **Check module** appears according to the bundled and installed file hashes. Checking the module while Echoes is off temporarily starts the installer, then returns the backend to its prior state. **Options** controls reply length and daily recall. **Diagnostics** reports the installation, module contact, observation hooks, database counts, first readable text and finished reply time.

## Voice playback

Open **VOICE TTS** in the companion to hear the original cast. Choose your save to preview its automatic player voice, or audition any of the 32 voices. Move **Pitch** while the sample plays to hear the five levels from two semitones lower to two higher. **Save player voice** applies the selection. **Use default** clears a save’s override. Voice playback starts enabled.

The cast includes six American men and six American women, plus two men and two women for each of the British, Spanish, German, Russian and Chinese English-accent casting directions. Residents and replacements draw a repeatable voice and pitch from their saved identities. Each voice has its own recordings for the game’s native chat reactions. Web previews and native reactions play from the installed release.

For live speech, start **Pocket-TTS (GPU / audio.cpp)** in Dwemer Distro. Echoes connects to port **8086** and supplies an original recording for the speaker. The player and resident speak their own words. A separate narrator reads actions enclosed in single asterisks. Words appear in their speaker’s bubble as the game’s audio channel reaches them. For names outside the pronunciation dictionary, Echoes estimates the caption timing from the recording. With Dwemer offline, the complete reply appears as text.

## Group Talk

Press **T** during normal play near two or three available residents. Each walks to an open spot near you. Press **Enter** to speak, **Tab** to listen, or **Esc** to leave. Type a resident’s name at the start of a question to address them directly. If someone else arrives nearby, press **T** between replies to invite them. Time pauses while you write. You can change the shortcut under `Controls / GroupTalk` in `BepInEx/config/mirthwood.echoes.cfg`.

Each line goes into the journals of the residents who heard it. If Averil tells Drystan about gathering flour, he can remember hearing it from her. Averil’s work also appears in her daily history.

## Character journal

| Section | What it contains |
| --- | --- |
| Profile | The saved character and an optional backstory. Existing authored text takes precedence. **Generate backstory** uses the memory model or provider default to write one from available facts such as town, work, origin, family, traits and tastes. Echoes keeps its provenance and never overwrites an existing story. |
| Interactions | Recorded NPC social encounters and hired-work collections, completed conversations, promises and reports of fulfillment, ordered by campaign day. |
| Conversations | Each visit has its own recap and transcript. Open a private exchange or a group conversation to read the speakers’ words, gestures and recorded promises. A resident’s group record contains the lines they were present to hear. Older private exchanges with unknown boundaries remain individually readable. |
| Daily history | Each day with recorded activity has a dated recap. Open it to read what the resident did or witnessed. Recent activity used in dialogue defaults to today and yesterday and can be set to one through seven days in Options. |
| Commitments | Read-only record of each promise, its speaker, original words, model and any earlier proposal needed to understand a short acceptance. Includes the issue day, supported due day and any existing fulfillment report. Open promises survive daily memory compaction. |
| Dynamic personality | The native baseline, developing tendencies and recorded turning points. Repeated observed social choices can move a tendency. A player-marked turning point must reference an actual interaction or conversation and include a reason. |

The five native temperament pairs are anxious/calm, aggressive/genial, impulsive/cautious, reserved/sociable and active/passive. The sixth saved value reflects the current need for company. “Balanced,” “leans” and “strongly” are Echoes readings, using absolute thresholds of 0.15 and 0.65. The original scores are available in the profile. Conversation interests compare the resident’s temperament with the game’s five ordinary chat topics. The in-game conversation also reads the resident’s current mood when preparing a reply. Gift and topic tables are fingerprinted to the installed game resources.

Echoes records a recurring tendency when one social direction leads on three separate days. Growth is limited to one net step of 0.08 per axis per day and 0.32 total in either direction. The journal identifies the source of a change. Echoes uses that development in dialogue and stores it in PostgreSQL. Campaign and resident identities scope every journal. Reloading a marked save selects that timeline’s conversations, observations and promises, including when two saves come from the same day. Older entries that predate save markers follow the campaign day.

## Installing or updating the server

Install Mirthwood through Steam on Windows x64. The desktop companion uses the .NET 10 Windows Desktop Runtime and Microsoft Edge WebView2 Runtime. Echoes runs its backend in a prepared WSL distro with systemd, the .NET 10 ASP.NET Core Runtime and PostgreSQL. The updater defaults to `ReignServer`. Use `-Distro` to select a different prepared distro.

Extract the **Mirthwood-Echoes-Server** release, open PowerShell in the extracted folder and run `.\Update-Server.ps1`. The updater discovers the game in your Steam libraries, installs the matching server and creates the **Mirthwood: Echoes** desktop shortcut. On first launch, open the shortcut, turn on **Server** and select your sign-in and conversation model under **LLM Provider / Models**. Then choose **Start Game**. For subsequent sessions you can start directly from the selected **Start Game** button.

When upgrading from 0.8.0 or earlier, finish your session and close the desktop companion before running the updater. If it was open during the update, reopen it from the shortcut to load the new dashboard. Starting with 0.9.0, the dashboard follows server and release changes while it is open.

The updater retains the in-place migration from the earlier **Mirthwood AI** installation. It migrates the PostgreSQL database and role, Unix account, service directories, encrypted provider keys, play shortcut, game module and plugin settings.

| Installation component | Echoes identifier |
| --- | --- |
| WSL service | `mirthwood-echoes.service` |
| Local Web UI console | Window-owned transient `mirthwood-echoes-console.service` on loopback port 17841. The backend uses 17842. Both stop when the window closes. |
| Database, database role and Linux user | `mirthwood_echoes` |
| Server release and state directories | `/opt/mirthwood-echoes` and `/var/lib/mirthwood-echoes` |
| BepInEx and Harmony ID | `mirthwood.echoes` |
| Windows game module | `BepInEx\plugins\MirthwoodEchoes\Mirthwood.Echoes.dll` |
| Windows launcher | `Mirthwood.Echoes.Play.exe` |

The server carries the compiled BepInEx plugin and the required loader files from the supplied Mirthwood archive. It compares hashes on startup, on launch and every 30 seconds. A Windows process and file-handle check protects a running game. A pending module update applies after exit. Player configuration and other plugins are preserved. If a managed binary differs from both the bundled version and the last recorded install, Diagnostics identifies the file for review.

## Foundation

| Component | Responsibility |
| --- | --- |
| Windows companion | Display the Web UI with the installed Microsoft Edge WebView2 runtime, own the WSL session, and open Steam app 2272900 on Start Game |
| BepInEx module | Observe native social and work events, report the calendar, open the selected resident’s Talk editor and apply the native reaction once |
| WSL backend | Index saves read-only, maintain the module, retrieve character context, receive one streamed model result and journal the spoken reply and promises |
| Local web console | Serve the journal interface and expose the fixed Echoes service and module controls |
| PostgreSQL | Store per-campaign character identity, events, original turns, compacted excerpts, commitments, backstories, development evidence and provider settings |

The server uses Reign’s .NET/PostgreSQL architecture. Its provider IDs, foreground-priority queue and Codex app-server exchange are adapted from ReignServer revision `4d976d6`. Daily observations come from game callbacks. A job title supplies character background. A specific work or social event requires its own observation.

After you send a line in Talk, the resident stays in place while the world clock resumes. One, two, then three periods cycle in their usual brown overhead bubble. Their first words replace the periods. With spoken dialogue enabled, the periods continue until the resident’s caption begins. Escape closes Talk at any time.

An ordinary Talk exchange requests one streamed model completion. The prompt includes the resident’s character, live desire for company, recent observations and conversation, relevant commitments and one matching older memory. The model identifies the main spoken topic and writes short alternatives for a warm, neutral or cool reaction. A hesitant “Hmm” or two equally weighted subjects count as Small Talk. A silent gesture or a direct trade request uses the neutral line and spends no social desire.

Mirthwood scores the player’s card and decides whether the resident follows it with their own card or ends the conversation. A return card spends the resident’s native desire to socialize. Their personality affects its cost and its gradual recovery during game time. The same model call prepares a brief answer or acknowledgment and chooses parting words for each reaction. When the resident decides to leave, they finish their answer and say goodbye aloud. A continuing exchange uses the ordinary answer. The journal keeps the exact spoken words and records the game’s italic action separately. A new promise must match the speaker’s exact words.

A run of positive native reactions lets a resident spend longer on a personal story, a shared interest, or the details of a plan. Stronger and successive positive reactions spend a progressively smaller share of the resident’s usual desire. In **Options**, **Minimum desire loss in a positive conversation** sets that share at the strongest level. The default is **25%**. Choose **100%** to use the stock cost throughout. A neutral reaction spends the usual amount and halves the strength carried into the next turn. A negative reaction spends the usual amount and clears the run. An unscored gesture preserves it. Leaving the conversation resets the run for the next chat. Recovering an interrupted reply restores the original result.

Each accepted exchange has a persistent ID tied to the campaign, resident and words. A pending record on the game PC, a receipt in the save and the completed PostgreSQL turn allow recovery after a connection loss. Loading an older save restores the native state of that save. Replaying a completed exchange retrieves its original reply and recorded return card or ending. If the game stops during the native write and its result cannot be recovered, Echoes closes the exchange with an unknown reaction and skips another roll.

## Building and verifying

Developers can start with the [Mirthwood: Echoes command centre](docs/command-centre/README.md) for branch ownership, changed-path validation, review and server-package delivery.

From the source checkout on Windows, build the separate module, server and launcher projects with the supplied BepInEx archive and the installed game assemblies.

```powershell
.\scripts\Build-Server.ps1
.\scripts\Update-Server.ps1
```

The build produces `.work\Mirthwood-Echoes-Server`. The following regression run creates temporary synthetic saves, a separate PostgreSQL database and a local provider fixture. Run it with Mirthwood closed so it can exercise the first-install and update paths.

```powershell
wsl -d ReignServer -u root -- python3 tests/foundation_smoke.py .work/Mirthwood-Echoes-Server .work
wsl -d ReignServer -u root -- python3 -B tests/relationships.py .work/Mirthwood-Echoes-Server .work/interactions-proof Interactions
wsl -d ReignServer -u root -- python3 -B tests/character_journal.py .work/Mirthwood-Echoes-Server /mnt/c/SteamLibrary/steamapps/common/Mirthwood/Mirthwood_Data
node tests/journal_browser.cjs .work/browser --switch-test
```

In the original foundation tests, BepInEx loaded the module and the server received its heartbeat with both observation hooks registered. Updating the server from 0.1.0 to 0.1.1 installed the corresponding new module in the Steam directory. Slot 0 yielded 59 NPC controllers, one player and three death-registry records. A field-by-field comparison matched 189 controller and sibling components. Three Renoldus instances retained separate identities.

The isolated journal regression covers source attribution, grounded backstories, retained transcripts, commitments after compaction, busy days, personality changes, resident replacement, duplicate names and multi-year saves. The browser check uses the running console and switches the actual backend off and on. Run it between game sessions.

The 0.1.2 upgrade retained the original PostgreSQL database identity, all 63 roster entries, and the exact bytes of the native save and its backup. A separate test configured a disposable provider key in release 0.1.1, upgraded the database, and authenticated a dialogue request with that key. Repeating the migration preserved the new encrypted value. To repeat that test, pass the original 0.1.1 release ZIP and the current package directory to `tests/provider_identity_upgrade.py` in WSL.

## In-game test harness

Run `scripts/Test-Game.ps1 -Mode Voices -IsolatedSaveSlot 0` with Dwemer available to check the native reactions and a spoken player, narrator and resident exchange. The probe reads back game audio channel positions, caption updates and the speaker’s assigned voice. From the checkout, `node tests/journal_browser.cjs .work/voice-browser --voices` checks sample playback and pitch in Edge and restores the previous player voice after testing the Save button.

With Mirthwood closed, the following command opens a visible Echoes window as needed, launches Steam with a hash-verified copy of slot 0, runs the checks, closes that test process, and verifies the original save and backup. The harness closes a companion window it opened for an isolated run. Use the source checkout command below, or run the bundled `Test-Game.ps1` from the extracted release.

```powershell
.\scripts\Test-Game.ps1 -Mode All -IsolatedSaveSlot 0 -Report .work\game-check.json
```

Use `-Mode Provider -IsolatedSaveSlot 0` for a single live provider probe. For the production Talk, retry and reload suite, run `wsl -d ReignServer -u root -- python3 -B tests/relationships.py .work/Mirthwood-Echoes-Server .work/talk-proof` from the checkout. It uses a copied slot, a disposable PostgreSQL database and 15 signed-in Codex turns across three residents. Six turns compare the same resident’s answers about visible clothes and public standing on separate copies of the same moment. The other turns exercise conversation topics, a two-turn personal story, dropped requests, saved and earlier world states, and backend restarts. Set `MIRTHWOOD_ECHOES_CHECK_JOURNAL=1` in WSL to also trace a spoken promise from Mirthwood into the resident’s journal in Edge.

To compare social desire and friendship without a model call, run `.\scripts\Test-Game.ps1 -Mode SocialBalance -IsolatedSaveSlot 0 -TimeoutSeconds 300 -Report .work\social-balance.json`. The harness plays stock topic cards, repeats the sequences at 100% with matched starting traits and random decisions, and compares positive, neutral, and negative native reactions at several cost settings. It follows a positive run to its natural ending at the default cost and observes native recovery. The live retry suite also includes this comparison.

From the source checkout, `tests/native_talk/Measure-Talk.ps1 -ReportDirectory .work/talk-map` exercises the native Talk callback and measures friendship across the five ordinary Chat actions in a copied world.

For a keyboard playthrough, run `wsl -d ReignServer -u root -- python3 -B tests/relationships.py .work/Mirthwood-Echoes-Server .work/walkthrough Walkthrough`. The temporary input probe walks toward a resident in the copied world and presses the native Talk key. You have 30 seconds to start typing into the player bubble. If it stays empty, the probe types a clothing question and submits it. The test captures the visible menu and reply, restarts the private backend and checks that the journal can recover the same words.

- **Runtime** checks the loaded Echoes release, Unity scene and actual Harmony patch owners.
- **World** resolves a loaded resident to the saved profile. In an isolated run it also opens the resident’s overhead dialogue, compares topic readings with Mirthwood’s own calculation and exercises the native social and hired-work callbacks against the copied world. The test records who chose the topic and what item and quantity were collected.
- **Interface** exercises the player’s overhead editor, keyboard submission, word wrapping, action italics, camera anchoring and game-clock pause in a copied world. A local fixture delays its reply while the check observes the rolling periods, the resident’s navigation and the advancing clock. It also exercises cancellation, a rejected turn and caption fallback. The game captures its original NPC bark, the player’s draft, the waiting bubble and the reply under `%LOCALAPPDATA%\MirthwoodEchoes\Diagnostics`.

To play an unscripted test conversation on a copy of slot 0, run `py -3 tests/resident_dialogue.py --play <package> <resident>` on Windows. When the private run reports a ready draft, use `py -3 tests/resident_dialogue.py --say <run> "your words"` in a second terminal. Read the resident's actual reply before choosing what to say next. The test sends Enter to Mirthwood, reads the overhead bubble, checks the journal and records the native reaction and card. For a controlled starting condition, add `tired` or `eager` after the resident's name. This sets their initial social desire once in the copied world. Mirthwood chooses every subsequent card and stopping point.

The isolated run redirects native save and temporary-save paths and uses its own last-slot preference. Test observations and test dialogue never enter a player journal. To inspect an already running game, use **Diagnostics → In-game checks** or run the script with a single `-Mode`. The world and interface checks need a loaded save with a resident on screen. Live cloud reply quality and latency require the selected provider and model.

## Visual references

The waiting indicator uses the resident’s own overhead bark as its reference. In a copied slot 0, Drystan stood beside a canopied stall. The native bark, waiting periods and reply were captured with the same camera. All three use the native brown speech paper, ink and calligraphic lettering. The periods occupy the text area above the same resident’s head.

The **VOICE TTS** page uses the journal’s parchment, brown rules and woodland-green selection cues. Its pitch control follows the Options slider. Spoken dialogue uses the same native brown overhead bubble and italic action lettering as typed dialogue.

The generated quest offer reached through **Speak** supplies the native portrait, paper panel, lettering and choice-row style. Echoes renders the full offer and a personality-informed invitation in that panel. The lower choice row becomes a text field and the other row recalls the original wording. Run the quest suite from the checkout with `wsl -d ReignServer -u root -- python3 -B tests/relationships.py .work/Mirthwood-Echoes-Server .work/quest-proof Quests`. It creates offers through Mirthwood's quest generator on a copy of slot 0 and uses a disposable PostgreSQL journal and the signed-in conversation model.

The Slot 0 save thumbnail shows the cream **Ride** prompt beside a wooded settlement. Its woodland greens, brown ink edges and pale labels informed the Web UI. The masthead uses an original village illustration. The character record uses a parchment reading surface, a compact side index and keyboard-navigable tabs. For dialogue, the reference is a resident speaking in Brightoak. The isolated slot 0 check captured a native overhead bark in Brightoak, the player editing and the nearby resident answering at the same location. Both bubbles use the game’s `thoughtBox` sprite, `Kingthings_Calligraphica_2 SDF` typeface and ink. The editable version tracks the player and grows as the draft is written. The journal holds the transcript.

The social desire slider uses the Options page’s parchment and woodland green, with the current percentage beside the track. It supports the arrow keys and fits the narrow layout used for the character journal. The control appears in the Web UI. Residents still signal the end of a conversation through their in-game dialogue bubble.

A departing resident in Brightoak uses the same brown speech bubble as a resident who stays. Their spoken goodbye appears in regular type, followed by the game’s italic closing cue. The bubble reserves space for **Enter to leave** above it at the edge of the screen.
