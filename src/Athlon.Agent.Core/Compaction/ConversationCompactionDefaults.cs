namespace Athlon.Agent.Core.Compaction;

public static class ConversationCompactionDefaults
{
    public const string SummaryMessageMarker = "__compaction_summary__";
    public const string HiddenSummaryMessageMarker = "__hidden_compaction_summary__";

    public const string PrecedingMessagesPlaceholder =
        "The conversation to summarize is in the preceding messages.";

    /// <summary>
    /// Prefixed onto the compacted summary when it is re-injected for the continuing model
    /// (Codex-style handoff framing).
    /// </summary>
    public const string SummaryResumePrefix =
        """
        Another language model was solving this problem and produced a handoff summary of its progress.
        The retained messages after this summary are recent context; tool results in the tail are still available.
        Build on that work — do not redo completed steps. Prefer the handoff for goals, decisions, constraints, and next steps.
        """;

    /// <summary>
    /// Appended when a handoff note will be reattached. Overrides the sections that duplicate the note.
    /// </summary>
    public const string HandoffPreservedSummaryOverride =
        """
        The session handoff note is stored separately and will be attached verbatim after your summary. Do not restate it.
        Write exactly None for SESSION INTENT.
        Write exactly None for KEY DECISIONS / REJECTED PATHS.
        Write exactly None for NEXT STEPS.
        Keep ## PROGRESS as a narrative of what happened.
        Keep ## ARTIFACTS as file paths only.
        Keep ## CONSTRAINTS / PREFERENCES only if they are not already in the handoff note.
        """;

    /// <summary>
    /// Dedicated CONTEXT CHECKPOINT prompt (Codex-style handoff). Sections stay structured so
    /// must_preserve / session handoff overrides remain mechanical.
    /// </summary>
    public const string DefaultSummaryPrompt =
        """
        You are performing a CONTEXT CHECKPOINT COMPACTION.
        Create a handoff summary for another LLM that will resume this same coding task.
        The conversation history below will be replaced by your summary plus a short retained tail.
        Be concise, structured, and focused on seamless continuation — not a transcript paraphrase.

        Include only what the next model needs:
        - Current progress and key decisions (and why)
        - Important constraints, user preferences, and hard requirements
        - Clear next steps (concrete, ordered)
        - Critical paths, commands, examples, or references needed to continue
        - What was already tried and rejected (so it is not repeated)

        Structure your reply with these sections (populate each or write "None"):

        ## SESSION INTENT
        The user's primary goal or request in one or two sentences.

        ## PROGRESS
        What has been accomplished so far; current work-in-progress state.

        ## KEY DECISIONS / REJECTED PATHS
        Important decisions, reasoning, and approaches that failed or were ruled out.

        ## CONSTRAINTS / PREFERENCES
        User or project constraints that must survive compaction.

        ## ARTIFACTS
        Files or resources created, modified, or heavily referenced (paths + brief what changed).

        ## NEXT STEPS
        Specific remaining tasks to achieve the session intent. Do not invent new goals.

        Respond ONLY with the handoff sections above. Do not wrap in markdown fences.

        {must_preserve}

        <messages>
        {messages}
        </messages>
        """;
}
