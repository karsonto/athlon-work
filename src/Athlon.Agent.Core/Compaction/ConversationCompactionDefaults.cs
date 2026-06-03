namespace Athlon.Agent.Core.Compaction;

public static class ConversationCompactionDefaults
{
    public const string SummaryMessageMarker = "__compaction_summary__";

    public const string DefaultSummaryPrompt =
        """
        <role>
        Context Extraction Assistant
        </role>

        <primary_objective>
        Your sole objective in this task is to extract the highest quality/most relevant context from the conversation history below.
        </primary_objective>

        <objective_information>
        You're nearing the total number of input tokens you can accept, so you must extract the highest quality/most relevant pieces of information from your conversation history.
        This context will then overwrite the conversation history presented below. Because of this, ensure the context you extract is only the most important information to continue working toward your overall goal.
        </objective_information>

        <instructions>
        The conversation history below will be replaced with the context you extract in this step. You want to ensure that you don't repeat any actions you've already completed, so the context you extract from the conversation history should be focused on the most important information to your overall goal.

        Structure your summary using these sections (populate each or write "None"):

        ## SESSION INTENT
        What is the user's primary goal or request?

        ## SUMMARY
        The most important context, decisions, reasoning, and rejected options.

        ## ARTIFACTS
        Files or resources created, modified, or accessed (with specific paths and changes).

        ## NEXT STEPS
        Specific tasks remaining to achieve the session intent.
        </instructions>

        Carefully read through the entire conversation history below and extract the most important context. Respond ONLY with the extracted context.

        {must_preserve}

        <messages>
        {messages}
        </messages>
        """;
}
