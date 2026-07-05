# Mira Roadmap — from Telegram bot to private local Jarvis

_Last updated: 2026-07-05_

## Vision

A private, local-first personal assistant that: remembers facts about me and the people around me; reminds; advises using saved context; tracks health and gym progress; shows everything on a beautiful local dashboard where I can also chat with it; and surfaces the best car deals from SsLvAutoTracker/CopartAnalyzer. Nothing personal leaves my machine unless I explicitly allow it.

## Current state (honest)

Works: Telegram polling (single allowed chat), 13-intent LLM classification, SQLite storage (memory_items / source_captures / reminders / conversation_messages), proactive daily brief + weekly review + knowledge audit, markdown mirror to `%LOCALAPPDATA%/Mira/knowledge`, 47 passing tests.

Broken/missing:
- **Memory search is a placeholder** — `SearchAsync` has no real FTS/vector backing. Recall is the core of the whole product; without it "answer with context" silently degrades to a plain chatbot.
- No dashboard; Telegram text menu only.
- Prompts hardcoded inside `ProcessMessageUseCase.cs` — impossible to iterate without recompiling.
- No token/context management (dumps last 12 messages + up to 8 memories and hopes the model copes).
- Voice rejected; no Claude fallback despite CLAUDE.md mentioning it; automation runner has only `echo_test`.
- **Privacy gap:** all personal input currently transits Telegram's servers (Bot API is not end-to-end encrypted). "Local-first" today applies to storage and LLM, not to the transport.

## Three standing decisions

1. **The dashboard becomes the primary, fully-private channel; Telegram becomes the optional mobile convenience.** That is the only honest way to satisfy "info couldn't be gotten by other people or external resources". Sensitive categories (Medical, Decisions) can be flagged dashboard-only so they never transit Telegram at all.
2. **Telegram "folders" = forum Topics.** A bot cannot create chat-list folders, but it *can* post into separate **Topics of a private forum supergroup** (`message_thread_id` in the Bot API). Create a private supergroup, enable Topics, add the bot: one topic each for Chat, Reminders, Health, Briefs, Deals. This is exactly the per-task separation you wanted.
3. **Obsidian is the viewer, Mira owns the data.** The existing markdown mirror already makes the knowledge folder an Obsidian-openable vault (both local, trusted). Keep sync one-way (Mira → vault) until Phase M5; two-way sync is a known rabbit hole.

---

## Phase M0 — Fix the foundations (~1–2 weeks) — do first

| # | Item | Effort | Detail |
|---|------|--------|--------|
| M0.1 | Real memory search: FTS5 + local embeddings hybrid | M | Add SQLite **FTS5** table over `memory_items` (title, content, subject, tags) maintained by triggers; add an `embeddings` table (memory_id, vector BLOB) filled via LM Studio's `/v1/embeddings`. **Also index `conversation_messages`** — episodic memory ("what did we discuss last month about X") is a separate, equally valuable recall lane alongside semantic facts (the Hermes-agent three-tier pattern: prompt/episodic/semantic). Retrieval = FTS5 BM25 candidates ∪ cosine top-K, merged with recency boost. At personal scale (<100k rows) brute-force cosine in C# is fine; no vector DB needed. **This unblocks everything else.** |
| M0.2 | Externalize prompts | S | Move every system prompt to `prompts/*.md` files loaded at startup (name = intent), with a `{{placeholder}}` convention. Log prompt file hash with each LLM call so you can correlate behavior changes. See Prompt Library below for the revised texts to ship as v2. |
| M0.3 | Context budgeting | S | Token-estimate (chars/4 is fine locally) the assembled prompt; trim oldest dialogue turns first, then lowest-ranked memories, to a configurable budget (default 6k tokens for an 8B model). Prevents silent quality collapse on long days. |
| M0.4 | Privacy guardrail in code, not docs | S | A single `IOutboundPolicy` that every HTTP client goes through: allowlist = local LLM BaseUrl + api.telegram.org (only when Telegram channel enabled). Any other host → exception + log. Add a startup log line stating exactly which endpoints are permitted. Turns the privacy claim into an enforced invariant. |
| M0.5 | Model upgrade + JSON-schema enforcement | S/M | Hermes 3 8B is weak for 13-intent JSON routing. Concrete lineup for this machine is in **Hardware profile** below (RTX 5070 Ti, 16 GB): default **Qwen3-14B Q5_K_M/Q6_K**. Enable LM Studio's structured-output (JSON schema) on the classification call so malformed-JSON fallbacks (`confidence 0.4 → General`) stop polluting memory. |
| M0.6 | Core profile ("prompt memory") | S | Maintain a compact always-injected profile (`core-profile.md`, ~30 lines: who the owner is, key people, active goals, standing preferences) prepended to every `answer.md` call — durable identity facts shouldn't depend on retrieval winning. Refreshed nightly by the night shift (M2.6) from high-confidence pinned memories; owner-editable in the dashboard. |

## Hardware profile — RTX 5070 Ti (16 GB GDDR7)

Everything below must fit in 16 GB *together*: chat model + KV cache + embedding model (+ Whisper in M3). Budget accordingly — don't max out the chat model and starve the rest.

**Recommended lineup (LM Studio, all local):**

| Role | Model | VRAM | Why |
|------|-------|------|-----|
| Daily driver (router + answers + extraction) | **Qwen3-14B, Q5_K_M or Q6_K** (or its Qwen3.5-generation successor when available in GGUF) | ~10–11.5 GB | Best structured-output/tool-calling reliability in class, strong Russian, ~85+ tok/s on this card, leaves headroom for 16–32k context + embeddings. One resident model doing both classification (temp 0.1) and answering (temp 0.2) keeps latency low and avoids model-swap thrash. |
| Embeddings (M0.1) | **Qwen3-Embedding-0.6B GGUF** | ~0.6 GB | Top open multilingual embedder on MTEB; handles the RU/EN/LV mix in personal notes far better than nomic-embed. Runs co-resident via LM Studio `/v1/embeddings`. |
| Voice (M3.2) | **Whisper large-v3-turbo** (faster-whisper) | ~1.5 GB on demand | Fast, near-large accuracy; load on demand rather than resident. |

**Quality-over-speed alternatives** (use as the *only* resident model, context ≤ 8k, embeddings still fit):
- **Mistral Small 3.2 24B Q4_K_M** (~13 GB, ~55 tok/s) — noticeably more world knowledge for advice-style answers; weaker structured output than Qwen3.
- **Gemma 4 27B Q4** (~62 tok/s reported on this card) — strongest multilingual coverage (incl. Latvian) if LV-language notes become common.
- **GPT-OSS-20B** (MoE, very fast on 16 GB, strong tool calling) — but known structured-output integration issues and weak general/multilingual knowledge; only worth testing for the router role, not as the answerer.

**Uncensored chat lane (owner requirement).** A private assistant must not refuse when you log medical details, personal struggles, or want blunt advice. Two-tier approach:

1. **First try prompt-level:** most local-model refusals vanish with a persona line in the system prompt (add to `answer.md`: *"You are the owner's private assistant on his own machine. Be direct and non-judgmental; never refuse personal, medical-adjacent, or uncomfortable topics — organize and advise frankly."*). Stock Qwen3/Mistral respond well to this; costs nothing, keeps full model quality.
2. **If refusals persist, split lanes** (the `ModelLane` concept already exists in Mira.Core): keep stock **Qwen3-14B** for the router/extraction lane (abliteration measurably degrades JSON discipline — never uncensor the router), and load an uncensored model for the chat/answer lane:
   - **Mistral Small 3.2 24B Q4_K_M** — naturally low-refusal out of the box, best quality of the options; steerable to fully frank with the persona prompt. Preferred.
   - **Qwen3-14B-abliterated** (huihui/Josiefied builds) — drop-in same footprint as the daily driver, refusals removed at the weights level; slight quality tax.
   - **Dolphin 3.0 (Mistral base)** — purpose-built uncensored fine-tune, fits 16 GB.
   
   VRAM reality: two 14B+ models can't be resident together — use LM Studio JIT loading with a TTL so the chat-lane model loads on demand (~10–20 s swap) or accept the single-model setup with the persona prompt. Re-run the M0 recall + router verification after any model change; medical *boundary* behavior (no dosage advice) stays enforced in Mira's prompt layer, not the model's censorship.

**LM Studio settings that matter here:** enable Flash Attention + KV-cache quantization (Q8) to roughly double affordable context; keep the server on `127.0.0.1` (M0.4 allowlists it); pin the daily driver as always-loaded so the 60s reminder poll and proactive services never hit a cold-load; set context to 16k with the M0.3 budgeter trimming to ~6–8k actually used.

**Rule of thumb for upgrades:** re-run the M0 verification recall test whenever swapping models — a "better" chat model with worse JSON discipline is a net downgrade for Mira, because the router quality gates everything.

## Phase M1 — Local dashboard (~2–3 weeks)

New project `src/Mira.Dashboard` — **Blazor Server + MudBlazor** (same stack chosen for SsLvAutoTracker Phase W: one set of skills, shared components later), bound to `localhost` only (`Kestrel` listen 127.0.0.1; no auth needed then — the OS session is the auth boundary).

For visual direction use the designer-model role / taste-skill guidance (same convention as SsLvAutoTracker Phase W): this is a dense personal cockpit, not a landing page.

| # | Item | Effort | Detail |
|---|------|--------|--------|
| M1.1 | Channel abstraction | S | Introduce `IChatChannel` (Telegram and Web as peers) over the existing `ProcessMessageUseCase` + `INotificationSink`; proactive services broadcast to all enabled channels. No business-logic changes. |
| M1.2 | Chat page | M | Streaming chat with the local LLM (SignalR is built into Blazor Server). Every message runs through the same classify→act pipeline as Telegram, so "write to the AI and it recognizes if it needs to make a memory" works identically. Show the classifier's decision inline as a small chip ("💾 saved to Person/Anna" / "⏰ reminder Tue 18:00") with one-click undo — trust comes from seeing what it did. |
| M1.3 | Memory browser | M | MudBlazor DataGrid over `memory_items`: filter by category/subject/tag/confidence, full-text search (reuses M0.1), edit/merge/delete, confidence slider, "pin" flag. This is the missing control surface for your second brain. |
| M1.4 | Today view (home) | M | Due + upcoming reminders, today's brief, unprocessed inbox count, last workout + streak, latest deals (after M4). The "glance while having coffee" screen. |
| M1.5 | Health & gym page | M | Charts (weight trend, workout frequency heatmap, per-exercise progression) — needs M2.2 structured logging. Until then: 14-day health summary + raw Health memories list. |
| M1.6 | People page | S/M | Card per person from Person memories (+ M2.3 structured profiles): relationship, facts, preferences, gift ideas, birthday countdown. |
| M1.7 | Reminders + Inbox pages | S | CRUD for reminders (the bot-only flow is clumsy for editing); inbox triage with the same Save/Skip/Retry actions as Telegram buttons. |
| M1.8 | Phone access without the cloud | S | Reach the dashboard from your phone via **Tailscale** (WireGuard mesh, free tier, traffic stays peer-to-peer encrypted) or plain LAN binding when home. This gives the mobile convenience Telegram provides today with zero third-party servers — the prerequisite for ever demoting Telegram to optional. Do NOT use Cloudflare tunnels or port-forwarding; that re-introduces an external party. |

## Phase M2 — Memory quality & structured life data (~2 weeks)

| # | Item | Effort | Detail |
|---|------|--------|--------|
| M2.1 | Update-not-duplicate memory writes | M | Before inserting a new memory, run M0.1 search for near-duplicates (same category+subject, high similarity); classifier decides create / update / append via the memory-write prompt (below). Add `superseded_by` column instead of hard deletes — history preserved, recall uses latest. Fixes the "asked to remember the same fact twice = two rows" problem before it snowballs. |
| M2.2 | Structured workout/health extraction | M | New tables `workouts` (id, date, kind, duration, notes) and `workout_sets` (workout_id, exercise, set_no, reps, weight_kg), plus `body_metrics` (date, weight_kg, sleep_h, ...). A dedicated extraction prompt (below) parses free-text like "bench 4x8 80kg, squats 5x5 100kg" into JSON; falls back to a plain Health memory if parsing confidence is low. Powers the M1.5 charts and real progress answers ("am I progressing on bench?"). |
| M2.3 | Structured people profiles | S/M | Table `people` (name, relation, birthday, notes) + link Person memories by subject. Birthday reminders auto-created. Gift-idea prompt gets both the profile and linked memories. |
| M2.4 | Nightly memory consolidation | M | Extend `KnowledgeMaintenanceService`: nightly job clusters same-subject memories, proposes merges/contradiction resolutions as an **inbox item for approval** (never auto-destructive). Uses the consolidation prompt below. |
| M2.5 | Sensitivity levels | S | `sensitivity` column (Normal / Private / DashboardOnly). DashboardOnly items are excluded from any Telegram-bound response. Default Medical → DashboardOnly. |
| M2.6 | Staged night shift | M | Formalize the overnight pipeline the `NightShiftStage` enum already anticipates, as ordered stages instead of independent timers: **03:00 Refinery** (batch-classify all unprocessed inbox captures, run M2.1 dedup on the day's new memories), **05:30 Editor** (update topic threads M2.7, refresh core profile M0.6, prepare the brief), **08:00 Delivery** (brief goes out), **Sunday 22:00 Audit** (existing knowledge audit + orphan check M2.8). Each stage hands clean state to the next; failures surface in the morning brief instead of silently skipping. |
| M2.7 | Topic threads (living synthesis) | M | Nightly Editor stage maintains one synthesis markdown per active subject in the existing `3-threads/` folder: when new atoms land for a subject, the thread is rewritten to incorporate them (sources cited by memory id). Atoms are raw material; threads are understanding — after months, "what do I know about X" is answered from a maintained thread, not re-derived from scratch each time. Threads are also indexed by M0.1 search. |
| M2.8 | Vault under git + "no source, no note" | S | `git init` the knowledge folder; the night shift commits after every run — one command rolls back any bad night. Enforce the source-link invariant: every `memory_item` must reference a `source_capture` (schema already has the link table); the Sunday audit flags orphans, and orphaned memories are excluded from high-confidence retrieval until resolved. Keeps the brain trustworthy as automation grows. |

## Phase M3 — Telegram topics & channel polish (~1 week)

| # | Item | Effort | Detail |
|---|------|--------|--------|
| M3.1 | Forum-supergroup topics | M | Support `AllowedChatId` = a private forum supergroup; config maps purpose → `message_thread_id` (Chat, Reminders, Health, Briefs, Deals). Proactive services post to their topic; replies in a topic get that context ("in Health topic" biases classification toward health intents). Solves the "separate folders per task" wish with stock Telegram. |
| M3.2 | Voice notes | M | Local Whisper (faster-whisper or whisper.cpp server): Telegram voice → download → transcribe → normal pipeline; reply prefixed with the transcript. Also a mic button on the dashboard chat later. All local. |

## Phase M4 — Car-deal integration (~1 week, after SsLvAutoTracker Phase 0)

SsLvAutoTracker already ships a pull-based feed built exactly for this: `AssistantEventOutbox` + `GET /api/v1/assistant/events` (+ `/ack`) behind `X-Assistant-Api-Key`, and `/api/v1/market/{digest,briefing,radar}`.

| # | Item | Effort | Detail |
|---|------|--------|--------|
| M4.1 | Deals ingestion service | M | New `DealsFeedService` (BackgroundService) in Mira.Infrastructure: poll the events endpoint (localhost), ack, store in a `deal_events` table. Local machine → local machine; no privacy impact. |
| M4.2 | Deal digest delivery | S/M | Local LLM turns raw events into a short verdict (prompt below) → Deals topic in Telegram + Deals panel on the dashboard Today view. Priority threshold configurable so only "act now" quality interrupts you. |
| M4.3 | Ask-about-market | S | Intent `market_question` routed to `/api/v1/market/digest`/`briefing`, response summarized by the local LLM: "Mira, anything worth buying today?" answered from live tracker data. |

## Phase M5 — Later

- **Opt-in cloud escalation:** an explicit `/deep` command (or dashboard toggle per message) sends a *user-approved, shown-before-send* prompt to Claude API for hard reasoning tasks. Never automatic, redact names by default. Until then: no cloud path exists (M0.4 enforces it).
- **Two-way Obsidian sync:** file watcher ingests edits made in the vault back into `memory_items` (content-hash diffing already half-exists via `raw_captures`). Only after M2.1 dedup is solid.
- **Real automations:** replace `echo_test` with actual tasks (backup vault, export health CSV, trigger SsLvAutoTracker crawl) reusing the existing confirmation flow.
- **Wearable/health imports:** Google Fit / Garmin / Huawei export files dropped into a watched folder → parsed into `body_metrics`.

---

## Prompt Library (v2 — ship with M0.2 as `prompts/*.md`)

Notes: written for a 12B–32B local model — short imperative rules, JSON schema enforced by the runtime, no chain-of-thought requests. `{{...}}` are template slots.

### 1. `router.md` — intent classification (temp 0.1, JSON schema on)

```
You are the intent router for Mira, a private personal assistant.
Classify the user's message into one or more actions. Output JSON only.

Actions:
- save_memory: user states a durable fact about themselves, another person, gear, work, hobbies, health, or a decision. Extract: category (Person|Health|Medical|Job|Hobby|Gear|Thought|Decision|DailyNote|General), title (≤60 chars), subject (person/thing name or null), content (the fact, rewritten as a standalone sentence), tags (≤5), confidence (0-1).
- create_reminder: user asks to be reminded. Extract title, due_local (ISO 8601, resolve relative times using NOW below), repeat (none|daily|weekly|monthly).
- answer: user asks a question that saved memories might answer.
- chat: small talk or anything else.
- health_log: message describes a workout, weight, sleep or other measurable health event (route to health extractor).
- market_question: user asks about car deals, listings, prices, auctions.
- list_reminders | complete_reminder | daily_brief | weekly_review | health_summary | inbox_save | inbox_skip | run_automation | clarify: as before.

Rules:
- A message may produce multiple actions (fact + reminder is common). Return {"actions":[...]}.
- Prefer save_memory over chat whenever a durable fact is present, even mid-conversation.
- If the fact updates something likely already known (name matches, same topic), still emit save_memory; the memory writer resolves duplicates.
- Use clarify only when a required field is truly unresolvable (e.g. reminder with no time and no sensible default).
- Never invent facts not present in the message.

NOW: {{now_local}} ({{timezone}})
RECENT DIALOGUE (newest last):
{{recent_dialogue}}
UNPROCESSED INBOX (previews):
{{inbox_previews}}
MESSAGE:
{{message}}
```

### 2. `memory-write.md` — create vs update (used by M2.1)

```
You maintain Mira's memory store. A new fact arrived; similar existing memories are listed.
Decide exactly one: CREATE (genuinely new), UPDATE (same fact, newer/better version — rewrite merged content), APPEND (adds detail to existing — output combined content), CONTRADICT (conflicts with existing — keep both, flag).
Output JSON: {"op":"CREATE|UPDATE|APPEND|CONTRADICT","target_id":null|"<id>","title":"...","content":"...","tags":[...],"confidence":0-1,"note":"one line why"}

Rules:
- Content must be a standalone sentence understandable in 2 years without this conversation.
- Convert relative dates to absolute ({{now_local}}).
- Keep the subject's name in the content ("Anna prefers tulips", not "she prefers tulips").

NEW FACT: {{fact}}
SIMILAR EXISTING MEMORIES:
{{candidates}}
```

### 3. `answer.md` — answer with personal context (temp 0.2)

```
You are Mira, a private local assistant for {{owner_name}}. Answer using ONLY:
1) RETRIEVED MEMORIES (durable facts — trust these over dialogue),
2) RECENT DIALOGUE (continuity),
3) general knowledge (clearly not personal).

Rules:
- If memories answer it, lead with the answer, not the sources.
- If nothing saved is relevant, say so briefly ("nothing saved about that") then answer generally if possible.
- Never invent personal facts. Never claim a memory exists that is not listed.
- Be concise: this is read on a phone. No headers unless listing.
- Medical topics: organize saved info only; add the medical boundary line; no dosage/treatment advice.

RETRIEVED MEMORIES:
{{memories}}
RECENT DIALOGUE:
{{recent_dialogue}}
QUESTION: {{message}}
```

### 4. `health-extract.md` — structured workout/metric parsing (temp 0.1, JSON schema)

```
Extract structured health data from the message. Output JSON:
{"kind":"workout|body_metric|sleep|other",
 "workout":{"date":"YYYY-MM-DD","type":"gym|run|swim|other","duration_min":null|int,
   "sets":[{"exercise":"normalized name","sets":int,"reps":int,"weight_kg":null|number}],"notes":null|"..."},
 "metrics":[{"name":"weight_kg|sleep_h|steps|...","value":number,"date":"YYYY-MM-DD"}],
 "confidence":0-1}

Rules:
- "bench 4x8 80" → exercise "bench press", sets 4, reps 8, weight_kg 80.
- Resolve "today/yesterday" from NOW: {{now_local}}.
- Normalize exercise names to English singular ("приседания"/"squats" → "squat").
- If it is not really health data, return {"kind":"other","confidence":0}.

MESSAGE: {{message}}
```

### 5. `daily-brief.md` (temp 0.3)

```
Write {{owner_name}}'s morning brief from local data only. Max 12 lines, plain text with emoji section markers.
Sections (skip empty ones):
⏰ Today & overdue: reminders due in 48h, overdue first.
🔥 Streaks & health: days since last workout ({{days_since_workout}}), notable trend from recent health entries.
🧠 Open loops: pending decisions, unprocessed inbox count ({{inbox_count}}).
🚗 Deals: {{deals_summary_or_empty}}.
End with one short, specific suggestion for today based ONLY on the data above. No motivational filler. Never invent facts.

DATA:
{{context}}
```

### 6. `deal-digest.md` — Copart/ss.lv alert summarization (M4.2, temp 0.2)

```
You summarize car-deal events from the owner's own market tracker for a reseller in Latvia.
Input: JSON events with listing, asking price, market band (lower/median/upper), gap, priority, confidence, days listed.
Output max 5 lines per deal:
🚗 <year make model, mileage> — <asking> € (market <lower>–<upper> €, gap <gap>%)
Verdict: one of BUY-SIGNAL / NEGOTIATE / WATCH with a one-sentence reason grounded in the numbers (gap, liquidity, days listed, confidence).
Link on its own line.
Order by priority. If confidence is Low, say so explicitly. Never invent numbers not in the event.

EVENTS:
{{events_json}}
```

### 7. `connections.md` — monthly cross-domain connections report (temp 0.4)

```
Review the owner's memories and threads from the last 30 days against older saved knowledge.
Find NON-OBVIOUS connections: ideas from different life areas (work, health, people, car business, hobbies)
that share an underlying pattern, support each other, or quietly conflict.
Output max 5 findings, each 3 lines:
🔗 <the connection, one sentence>
Why it matters: <one sentence>
Sources: <memory/thread ids>
Skip obvious same-topic links. If nothing genuinely non-obvious exists, say so — do not force findings.
Never invent content not present in the sources.

RECENT (last 30 days):
{{recent_items}}
OLDER RELATED (search hits):
{{older_items}}
```

Run monthly via the proactive scheduler; deliver as an inbox item. This is the "system argues with you / connects what you forgot" payoff of the second-brain articles, grounded in real sources only.

### 8. `consolidate.md` — nightly memory maintenance (M2.4, temp 0.2)

```
Review these memories about the same subject. Propose maintenance operations, JSON list:
[{"op":"merge","ids":[...],"merged_content":"..."},
 {"op":"flag_contradiction","ids":[...],"note":"..."},
 {"op":"lower_confidence","id":"...","note":"stale since <date>"},
 {"op":"none"}]
Rules: merging must not lose facts; when in doubt, none. These are proposals for owner approval, not commands.

SUBJECT: {{subject}}
MEMORIES:
{{memories}}
```

---

## Ideas from the research collection (.omp PDFs) — adopted vs rejected

The seven saved articles (@eng_khairallah1 night-shift swarm, @neil_xbt Hermes+Claude knowledge agent, @KeisukeIshikawa multi-model army, @defileo and @monokern Claude+Obsidian, @ArtemXTech NotebookLM-vs-wiki, @AlphaSignalAI open-source stack) were reviewed against this plan.

**Adopted (now in the phases above):** staged night shift with hand-offs (M2.6); topic threads as living synthesis over atoms (M2.7); vault under git + "no source, no note" invariant (M2.8); three-tier memory — always-injected core profile (M0.6) + episodic conversation search (M0.1) + semantic facts; monthly cross-domain connections report (prompt 7); phone access via Tailscale instead of cloud tunnels (M1.8).

**Rejected, with reasons:** *NotebookLM* — cloud, sends personal data to Google, disqualified by the privacy requirement (its lesson — embeddings beat re-reading full sources per query — is already M0.1). *Kimi Work / 300-agent swarm* — a desktop agent with browser+file access is a privacy and reliability liability, and personal-scale nightly processing is a handful of sequential LLM calls, not a swarm; the staging idea was kept, the tool was not. *OpenRouter Fusion / multi-model councils* — cloud routing of personal queries; overkill for assistant tasks (single good local model + optional M5 opt-in escalation covers it). *Hermes Agent / GBrain / GStack as runtime* — Mira **is** the persistent runtime; adopting their patterns (memory tiers, FTS session store) beats adopting their stack. *Obsidian plugins (Smart Connections etc.)* — redundant once M0.1 exists; Obsidian stays a passive viewer of the mirror.

## Verification

- **M0:** save 20 varied facts, then ask 20 questions phrased differently — measure recall hit-rate before/after FTS5+embeddings (should go from ~0 to >80%). `IOutboundPolicy` test: point a rogue HttpClient at example.com, assert it throws.
- **M1:** full loop on dashboard only (Telegram stopped): chat → memory chip appears → memory browser shows it → Today view reflects reminder.
- **M2:** tell it the same fact three ways on three days → exactly one live memory. Log "bench 4x8 80kg" twice a week apart → progression chart shows both points. After a night-shift run: inbox is empty, the affected topic thread grew, a git commit exists, and the morning brief mentions any stage failure.
- **M3:** post in Health topic vs Chat topic → same text classifies differently; brief lands in Briefs topic.
- **M4:** with SsLvAutoTracker running locally, trigger a market alert → event appears in Deals topic + dashboard within one poll interval, and is acked (no redelivery).

## Sequencing

M0 first (search + prompts + guardrail are prerequisites for everything). Then M1 dashboard and M2 memory quality can interleave; M3 topics any time; M4 after SsLvAutoTracker Phase 0 hardening (needs its API stable). M5 stays parked until the core is trusted daily.
