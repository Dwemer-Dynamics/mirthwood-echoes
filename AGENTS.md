# Mirthwood: Echoes project rules

The mod’s official name is **Mirthwood: Echoes**. Use that spelling and punctuation in the game, Web UI, provider attribution, product metadata and documentation. Use `mirthwood-echoes` for the public machine identifier and `Mirthwood-Echoes` for release filenames. The Windows desktop shortcut displays **Mirthwood: Echoes** and opens the Web UI.

Use the Echoes name for internal identifiers as well. The PostgreSQL database, role and Unix user are `mirthwood_echoes`. The systemd unit is `mirthwood-echoes.service`. BepInEx and Harmony use `mirthwood.echoes`. Reserve former names for upgrade detection and historical records.

## Development command centre

Use the [Echoes command centre](docs/command-centre/README.md) and the `echoes-*` skills in [.agents/skills](.agents/skills). The public repository is `Dwemer-Dynamics/mirthwood-echoes`. The module, server, companion and installer share one source tree and one PR when their contract changes together. The local development checkout, the installed playable server/game, and GitHub's `unstable` are three distinct states. Describe each Git action briefly in plain English when carrying it out.

Before substantive work, check current Echoes tasks, worktrees, open PRs and path ownership. Use the Echoes programme, ledger and checklist for a coordinated batch. Scope memory and handoffs to `mirthwood-echoes`. Put each authorised independent job in a `codex/<task>` worktree based on the fetched `origin/unstable`. Preserve other owners' edits and serialize work on overlapping paths or shared runtime resources.

Plan the exact changed paths and run the [Echoes validator](scripts/validate-change.py) on the final committed source. Inspect the complete staged diff and exact message before committing. Open a draft PR for authorised publication and obtain an independent read-only Claude review of its exact head. Address findings and renew the evidence on changes. Merge only with user authority for that PR or batch, using `--match-head-commit`. Use PRs for promotion from `unstable` to `dev` and then to `mirthwood-echoes`. Never force-push, rewrite shared history or skip hooks. Use a revert PR to undo a merged change. Retain required evidence and packages before removing a clean, released worktree. Use only `git worktree remove` without force and stop if Git refuses.

Keep ordinary validation offline and scoped. Add new tests for a concrete regression or requirement. Keep build/test CI, version bumps, releases, external uploads, installation, game launch and live-provider tests under their respective user instructions. An authorised deployment installs the exact reviewed server package using its updater. The server reconciles the bundled module into the Steam installation. Protect saves, the Echoes database, provider settings, other mods and the visible companion's ownership of the services.

Use [ECHOES_ROADMAP.md](ECHOES_ROADMAP.md) for the Echoes roadmap. Update an existing entry where possible. Put new work in Inbox unless the user names a priority or section. Keep requested values, open requirements and history. Check off an item only after implementation and appropriate verification, and add a dated progress note for material changes.

## Golden rule — rely only on tested capabilities

Treat every untested capability as unproven. Before relying on a game system, mod hook, model, or helper, test the specific behavior with the actual component, configuration, and inputs the feature will use. Include ordinary cases and cases where the evidence is missing, conflicting, or ambiguous. Record what was tested, what passed, and the limits of the result. Recheck when a relevant component or configuration changes.

Keep verified world facts, character claims, and inferences distinct. For example, a small helper model's judgment that an NPC is exaggerating is unverified. Compare the claim with authoritative game state or recorded evidence where available. If the evidence can't resolve it, keep the claim attributed to the speaker and the conclusion unknown. Persist verified facts as facts and preserve uncertainty in any stored claims or inferences.

## 1. Follow Mirthwood's art direction and interface style

Make new content feel at home in Mirthwood. Use the whole game as the visual reference library for world art and interface style. Start with the most relevant in-game screen, location, or event, then draw on other parts of the game for individual visual elements as needed. In the change notes, record where each reference appears, any game state needed to see it, and which visual details inform the addition.

- **World art.** Match the reference's scale, silhouettes, palette, lighting, texture detail, and density of scenery as seen from the gameplay camera. Create original artwork and layouts for new content, using the reference to guide visual choices.
- **Interface.** Reuse native UI components and their interaction behavior where appropriate. Follow the comparable screen's typography, spacing, icon style, visual hierarchy, and focus and selection cues. When a custom interface is needed, use Mirthwood's visual language and create an original layout and artwork that are visually distinct from any specific native screen.
- **Dialogue.** Use a comparable conversation as the reference for speaker portraits or nameplates, text placement, and the layout and styling of choices. Check that long lines and choice labels remain legible.
- **Festivals.** Use a comparable gathering or the host settlement as a reference for the palette, materials, decorations, signage, and lighting. Check that the event's visual changes fit the location and that paths, entrances, and objects players can interact with remain easy to see. Follow native presentation patterns for prompts and event notices where applicable.
- **New worldspaces.** Reference comparable terrain and settlements for the ground textures, vegetation, architecture, prop scale, and lighting. Check terrain transitions, sightlines, and the visual cues for paths, entrances, and boundaries from the gameplay camera.

Once implemented, capture the addition and its reference in game and compare them side by side. Use comparable camera framing, display settings, and lighting or weather where relevant. Step through the relevant UI states to see the layout and visual feedback in context. Fix mismatches that stand out in normal play or affect readability. In the change notes, record intentional departures and any visual checks that still need an in-game pass.

## 2. Use Reign's backend and keep everyday dialogue short

Use Reign's backend with PostgreSQL as Mirthwood's foundation. Keep everyday replies brief and in character. Give the model just enough context to answer the current turn, usually the NPC's disposition, current state, and relevant recent history or commitments. The player can ask follow-up questions for clarification or more detail. Retrieve exact evidence when the question depends on it, and include any facts or terms needed for an accurate answer on the current turn.

In player-directed conversation, give the resident the words of the joke, flirt, question or story in their own voice. Reserve action italics for observable physical behavior. Follow the player’s question or disclosure first. Use the native return card to shape a related beat, give a sensitive moment space, and follow an explicit invitation to change subjects. A card is internal context. The transcript records the actual words and gestures the player saw. Background conversations between residents use Mirthwood’s existing presentation.

Use one streamed model call for ordinary dialogue. Keep background summarization off the reply path. When testing dialogue performance, measure time to the first readable line and time to finish separately.

## 3. Keep the C: workspace lean and limit backups on both drives

Build and test in the C: workspace. Keep the active checkout, files needed for current work, and only the rollback copies needed for immediate recovery. Review generated builds, logs, test outputs, downloads, and temporary copies at the end of each task. Clear disposable files generated by the task when safe, and move retained material that is no longer needed immediately to the Mirthwood project folder under `E:\CodexStorage\Projects`. Follow `E:\CodexStorage\Rules\STORAGE_POLICY.md` for verified moves and keep the last verified copy.

Default to at most two generations of routine rollbacks for each ongoing set of builds or deliverables across C: and E: combined, including any temporary rollback on C:. Reuse existing verified copies, avoid duplicate archives, and check total size before making another large copy. Flag any temporary exception with its size, purpose, and when it can be reviewed for cleanup. Review redundant backups for authorized removal after verifying the copy that will be retained. Preserve unique project data, required history, and active dependencies. Follow the storage policy for archived material.

## 4. Deliver the game module through the server

Keep the BepInEx module and the server as separate builds. Bundle the matching module with each server release. The server installs and verifies it and defers replacements while Mirthwood is open. Preserve player configuration and unrelated mods. Open the Web UI from one desktop shortcut. Make **Start Game** the prominent, initially selected action. It starts the backend, verifies the module and launches Mirthwood through Steam. Provide **Exit**. Keep the backend and Web UI tied to their visible Windows companion and stop them on close, minimize or loss of that window.

Record observed daily activity quietly. Retrieve a small set of facts for the individual NPC and campaign. Keep explicit commitments in their own durable record with the original speaker and wording, independently of the daily recall window.

## 5. Build characters in the journal and hold conversations in game

Keep the Web UI centered on the character record. Use readable native labels with the original save details available on demand. Keep separate views for interactions, complete conversations, compacted memories, commitments and dynamic personality. Preserve an existing backstory. Ground any requested new story in that person’s recorded circumstances and identify generated prose.

Keep acquired personality changes and their supporting interactions in Echoes. Identify the campaign and resident before attaching history. Verify new game hooks and interface behavior in an isolated copy of a save and check the original files afterward. Test traffic belongs in the harness. Record which checks used Mirthwood itself, a local provider fixture or a configured live model.

Use Mirthwood’s live desire to socialize in ordinary Talk. Let the game apply friendship, choose the resident’s next subject or end the exchange, spend social desire on a return card, and recover it as game time passes. A replay must preserve the original decision and apply each effect at most once. Recheck complete native and Echoes conversations together when the game’s social behavior changes.

Let enjoyable conversations last longer, including personal stories, deeper subjects and practical plans. Scale the cost of each native return card with the strength of the resident’s positive native reactions in this conversation. Keep neutral and negative returns at their usual cost, honor the resident’s decision to stop, and preserve the original cost across retries and reloads. The player can tune the strongest positive cost in Options. At 100%, compare Echoes directly with stock Mirthwood.

When the resident decides to end Talk, have them answer the player’s last point or acknowledge it and say goodbye aloud in character. Prepare that ending in the same model call. Use Mirthwood’s actual decision to select it. Keep the farewell in the spoken transcript and the native closing action separate. Show the same words when a reply is recovered.

When the player takes their leave, answer any last question and let Enter close Talk after the spoken parting. Mirthwood still grades the player's card and makes its own return decision. When the player picks up a detail, develop it in the resident's voice. When the player invites a new subject, a suitable native card can supply the whole reply. Test both directions of departure and read complete conversations of different lengths in a copied save.

The intended in-game entry is the resident’s native **Talk** option. Use the resident the player selected. Respect the game’s greeting flow, separate scripted **Speak** interactions and native eligibility checks. For free-form conversation, have the selected cloud model identify the native topic from the player’s words in the same call that produces the reply. Use **Small Talk** when the player speaks without a clear subject or gives several subjects equal weight. A turn consisting solely of narrated actions carries no friendship score. Ground the reaction in Mirthwood’s current resident and let the native system apply the relationship effect once per accepted exchange. Verify topic handling and retries in the isolated harness before enabling relationship changes in player saves.

Use the same native brown overhead speech bubble for both speakers. The talk key focuses the bubble above the player. It grows as they type and pauses game time while editing. Use the native typeface at a slightly smaller size. Grow the paper to the right from its tail as the player types. Align the text on the left and wrap between words at the available width. Size the parchment around the rendered letters or waiting dots with a small inset and clearance for the tail. Keep each entire draft and delivered line in one box. Enter accepts a draft, opens a reply or dismisses a farewell. Escape cancels the draft. Restore the previous time state when the editor closes. Wrap each narrated action in single asterisks and render it in italics in game and in the journal. Keep ordinary speech in regular type and retain the original marked wording in the transcript. Have the selected conversation model identify commitments by either speaker in the same response as its spoken reply. Check each attributed quotation against the actual exchange, retain the proposal behind a short acceptance, and mark the relevant journal entry automatically. Keep detection controls out of the player interface. Record uncertainty when a review cannot be verified. A commitment and its fulfillment require separate evidence.
