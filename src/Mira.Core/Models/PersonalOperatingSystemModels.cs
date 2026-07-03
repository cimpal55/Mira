namespace Mira.Core.Models;

public enum MemoryTier
{
    Prompt,
    Episodic,
    Semantic,
    Procedural
}

public enum NightShiftStage
{
    Scout,
    Refinery,
    Cartographer,
    Critic,
    Editor
}

public enum SourceKind
{
    Text,
    Voice,
    Link,
    Pdf,
    Screenshot,
    Image,
    File,
    Conversation
}

public enum ModelLane
{
    Local,
    Strong,
    Council
}

public sealed record MemoryTierDefinition(MemoryTier Tier, string Name, string Purpose);

public sealed record NightShiftStageDefinition(NightShiftStage Stage, string Name, string Purpose);

public sealed record SourceKindDefinition(SourceKind Kind, string Name, string Purpose);

public sealed record VaultFolderDefinition(string Path, string Purpose, bool PreserveUserEdits);

public sealed record PlanningLayerDefinition(string Name, string Purpose);

public sealed record ModelLaneDefinition(ModelLane Lane, string Name, string Purpose);

public sealed record ProceduralSkillDefinition(string Name, string Purpose);

public static class PersonalOperatingSystemMap
{
    public static IReadOnlyList<SourceKindDefinition> SourceKinds { get; } =
    [
        new(SourceKind.Text, "Text", "Fast thoughts, tasks, decisions, and project notes from Telegram."),
        new(SourceKind.Voice, "Voice", "Low-friction spoken captures for later transcription and extraction."),
        new(SourceKind.Link, "Link", "URLs and references kept as immutable sources before summarization."),
        new(SourceKind.Pdf, "PDF", "Documents stored as sources before derived notes are created."),
        new(SourceKind.Screenshot, "Screenshot", "Images captured for OCR, evidence, and later review."),
        new(SourceKind.Image, "Image", "Photos and visual notes retained before interpretation."),
        new(SourceKind.File, "File", "Arbitrary attachments kept unchanged in the source layer."),
        new(SourceKind.Conversation, "Conversation", "Episodic chat history used for continuity, not invented facts.")
    ];

    public static IReadOnlyList<MemoryTierDefinition> MemoryTiers { get; } =
    [
        new(MemoryTier.Prompt, "Prompt memory", "Stable identity, preferences, routines, tone, goals, and house rules."),
        new(MemoryTier.Episodic, "Episodic memory", "Full conversation and capture history with timestamps and raw references."),
        new(MemoryTier.Semantic, "Semantic memory", "Cleaned facts about people, projects, health, decisions, ideas, and gear."),
        new(MemoryTier.Procedural, "Procedural memory", "Reusable workflows, templates, automations, and personal skills.")
    ];

    public static IReadOnlyList<NightShiftStageDefinition> NightShiftStages { get; } =
    [
        new(NightShiftStage.Scout, "Scout", "Collect pending raw inputs and identify what needs processing."),
        new(NightShiftStage.Refinery, "Refinery", "Split messy captures into atomic memories, tasks, reminders, and decisions."),
        new(NightShiftStage.Cartographer, "Cartographer", "Link new memories to people, projects, goals, and older context."),
        new(NightShiftStage.Critic, "Critic", "Find contradictions, stale facts, unsupported claims, and open questions."),
        new(NightShiftStage.Editor, "Editor", "Generate morning briefs, weekly reviews, summaries, and next actions.")
    ];

    public static IReadOnlyList<VaultFolderDefinition> VaultFolders { get; } =
    [
        new("0-raw/", "Immutable raw captures from Telegram and local ingestion.", true),
        new("0-dashboard/", "Generated local dashboards and Obsidian indexes for live memory visibility.", false),
        new("sources/", "Original external files, links, screenshots, PDFs, and voice notes.", true),
        new("1-desk/", "In-progress triage, drafts, pending reviews, and working notes.", true),
        new("2-atoms/", "Atomic semantic memories grouped by category.", true),
        new("3-threads/", "Longer synthesized narratives, project threads, and reviews.", true),
        new("briefings/", "Daily briefs, weekly reviews, audits, and generated summaries.", true),
        new("_system/profile.md", "Prompt-memory profile for stable identity and preferences.", true),
        new("_system/house-rules.md", "Local operating rules and safety boundaries.", true),
        new("_system/skills/", "Procedural memory: reusable workflows and personal skills.", true)
    ];

    public static IReadOnlyList<PlanningLayerDefinition> PlanningLayers { get; } =
    [
        new("Projects", "Multi-step outcomes with context, status, and related memories."),
        new("Goals", "Longer-lived directions that organize projects and routines."),
        new("Tasks", "Concrete next actions with due dates, energy, time, and dependencies."),
        new("Commitments", "Promises and obligations that should surface at the right moment."),
        new("Routines", "Repeated behaviors and cadence-based life maintenance."),
        new("Next actions", "Smallest useful step Mira can propose for today.")
    ];

    public static IReadOnlyList<ModelLaneDefinition> ModelLanes { get; } =
    [
        new(ModelLane.Local, "Local model", "Private everyday capture, classification, retrieval, and chat."),
        new(ModelLane.Strong, "Strong model", "Hard reasoning, synthesis, important decisions, and planning."),
        new(ModelLane.Council, "Council mode", "Multiple perspectives for high-stakes research and contradictions.")
    ];

    public static IReadOnlyList<ProceduralSkillDefinition> ProceduralSkills { get; } =
    [
        new("daily planning", "Turn reminders, tasks, energy, and recent memories into a plan."),
        new("weekly review", "Review patterns, commitments, wins, risks, and next actions."),
        new("project review", "Summarize project state, blockers, decisions, and next moves."),
        new("decision framework", "Compare options with evidence, tradeoffs, uncertainties, and reversibility."),
        new("research digest", "Condense sources into claims, citations, gaps, and follow-up questions."),
        new("health summary", "Organize health notes without making medical decisions."),
        new("coding/project assistant", "Capture development context and reusable project workflows."),
        new("relationship profile update", "Update person profiles from new interactions and preferences.")
    ];
}
