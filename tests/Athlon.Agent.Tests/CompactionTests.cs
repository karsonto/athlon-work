using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class CompactionTests
{
    private static ContextCompactionSettings CreateDynamicCompactionSettings(Action<ContextCompactionSettings>? configure = null)
    {
        var settings = new ContextCompactionSettings
        {
            DynamicCompaction = new DynamicCompactionSettings { Enabled = true }
        };
        configure?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void ResolveEffectiveEstimate_uses_prompt_pressure_floor_when_higher_than_estimate()
    {
        var settings = new ContextCompactionSettings();
        var messages = new[] { ChatMessage.Create(MessageRole.User, "short") };
        var estimated = ContextTokenEstimator.Estimate(messages);
        var budget = new ContextBudgetSnapshot(
            200_000,
            8192,
            20_000,
            estimated,
            120_000,
            (double)(120_000 + 20_000) / 200_000);

        var effective = ContextTokenEstimator.ResolveEffectiveEstimate(messages, settings, budget);

        Assert.Equal(120_000, effective);
        Assert.True(effective > estimated);
        settings.TriggerMessages = 0;
        settings.TriggerTokens = effective - 1;
        Assert.True(ConversationCutoffPlanner.ShouldCompact(messages, effective, settings, force: false));
    }

    [Fact]
    public void ContextCompactionSettings_UsesAgentScopeDefaults()
    {
        var settings = new ContextCompactionSettings();

        Assert.False(settings.Enabled);
        Assert.False(settings.DynamicCompaction.Enabled);
        Assert.Equal(50, settings.TriggerMessages);
        Assert.Equal(80_000, settings.TriggerTokens);
        Assert.Equal(20, settings.KeepMessages);
        Assert.Equal(2_000, settings.TruncateArgs.MaxArgLength);
        Assert.Equal(80_000, settings.ToolResultEviction.MaxResultChars);
        Assert.Equal(0.7, settings.CompactTriggerRatio);
    }

    [Fact]
    public void ResolveCompactTriggerTokens_UsesMaxOfFixedAndWindowRatio()
    {
        var settings = new ContextCompactionSettings
        {
            TriggerTokens = 80_000,
            ContextWindowTokens = 200_000,
            CompactTriggerRatio = 0.7
        };

        Assert.Equal(140_000, ConversationCutoffPlanner.ResolveCompactTriggerTokens(settings));
    }

    [Fact]
    public void ShouldCompact_TriggersAtWindowRatioThreshold()
    {
        var settings = new ContextCompactionSettings
        {
            TriggerMessages = 0,
            TriggerTokens = 0,
            ContextWindowTokens = 100_000,
            CompactTriggerRatio = 0.7
        };
        var messages = new[] { ChatMessage.Create(MessageRole.User, new string('x', 280_000)) };

        Assert.True(ConversationCutoffPlanner.ShouldCompact(
            messages,
            ContextTokenEstimator.Estimate(messages),
            settings,
            force: false));
    }

    [Fact]
    public void ContextTokenEstimator_UsesCharsPerTokenHeuristic()
    {
        var message = ChatMessage.Create(MessageRole.User, new string('x', 250));
        var textTokens = (int)Math.Ceiling(250 / 2.5);

        Assert.True(ContextTokenEstimator.EstimateMessage(message) >= textTokens);
        Assert.Equal(ContextTokenEstimator.EstimateMessage(message), ContextTokenEstimator.Estimate(new[] { message }));
    }

    [Fact]
    public void ContextTokenEstimator_ExcludesReasoningByDefault()
    {
        var message = ChatMessage.Create(MessageRole.Assistant, "answer", reasoningContent: new string('r', 500));
        var withoutReasoning = ContextTokenEstimator.EstimateMessage(message);
        var withReasoning = ContextTokenEstimator.EstimateMessage(message, includeReasoningInModelContext: true);

        Assert.True(withReasoning > withoutReasoning);
    }

    [Fact]
    public void ConversationCutoffPlanner_LongAgentLoop_CompactsWithoutSecondUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Create(MessageRole.User, "do the task")
        };

        for (var i = 0; i < 4; i++)
        {
            messages.Add(ChatMessage.Create(
                MessageRole.Assistant,
                $"step-{i}",
                toolCalls: new[] { new AgentToolCall($"c{i}", "file_read", new Dictionary<string, string>()) }));
            messages.Add(ChatMessage.Create(MessageRole.Tool, $"output-{i}"));
        }

        var settings = new ContextCompactionSettings { TriggerMessages = 5, KeepMessages = 2 };
        var estimated = ContextTokenEstimator.Estimate(messages);
        Assert.True(ConversationCutoffPlanner.ShouldCompact(messages, estimated, settings, force: false));

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(messages, estimated, settings);
        Assert.True(cutoff > 0);
        Assert.Equal(2, messages.Count - cutoff);
    }

    [Fact]
    public void ConversationCutoffPlanner_KeepTailByMessages()
    {
        var messages = new[]
        {
            ChatMessage.Create(MessageRole.User, "first"),
            ChatMessage.Create(MessageRole.Assistant, "reply-1"),
            ChatMessage.Create(MessageRole.User, "latest question"),
            ChatMessage.Create(MessageRole.Assistant, "thinking", toolCalls: new[] { new AgentToolCall("c1", "file_read", new Dictionary<string, string>()) }),
            ChatMessage.Create(MessageRole.Tool, "tool output"),
        };

        var cutoff = ConversationCutoffPlanner.DetermineCutoffIndex(
            messages,
            ContextTokenEstimator.Estimate(messages),
            new ContextCompactionSettings { KeepMessages = 2 });

        var tail = messages.Skip(cutoff).Select(message => message.Content).ToArray();
        Assert.Contains("tool output", tail);
        Assert.DoesNotContain("first", tail);
        Assert.DoesNotContain("reply-1", tail);
    }

    [Fact]
    public void ConversationCutoffPlanner_FindSafeCutoff_DoesNotSplitToolPair()
    {
        var assistant = ChatMessage.Create(
            MessageRole.Assistant,
            string.Empty,
            toolCalls: new[] { new AgentToolCall("call-1", "file_read", new Dictionary<string, string>()) });
        var tool = ChatMessage.Create(MessageRole.Tool, "ToolCallId: call-1\noutput");
        var messages = new[] { assistant, tool };

        var cutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(messages, 1);
        Assert.Equal(0, cutoff);
    }

    [Fact]
    public void SummaryMessageBuilder_FiltersOldSummaryMessages()
    {
        var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("old summary", null);
        var user = ChatMessage.Create(MessageRole.User, "hello");
        var filtered = SummaryMessageBuilder.FilterSummaryMessages(new[] { summary, user });

        Assert.Equal(MessageRole.Summary, summary.Role);
        Assert.Single(filtered);
        Assert.Equal("hello", filtered[0].Content);
    }

    [Fact]
    public void SummaryMessageBuilder_RecognizesLegacyUserMarkerSummaries()
    {
        var legacy = ChatMessage.Create(
            MessageRole.User,
            ConversationCompactionDefaults.SummaryMessageMarker + "\nlegacy summary");

        Assert.True(SummaryMessageBuilder.IsSummaryMessage(legacy));
        Assert.False(SummaryMessageBuilder.IsSummaryMessage(ChatMessage.Create(MessageRole.User, "hello")));
    }

    [Fact]
    public void PromptPressureStore_Record_OverwritesWithLatestValue()
    {
        var store = new PromptPressureStore();
        store.Record("s1", 100_000);
        store.Record("s1", 40_000);

        Assert.Equal(40_000, store.GetLastPromptTokens("s1"));
    }

    [Fact]
    public void ResolveEffectiveEstimate_ReusesKnownRawHistoryEstimate()
    {
        var settings = new ContextCompactionSettings();
        var messages = new[] { ChatMessage.Create(MessageRole.User, "short") };
        var estimated = ContextTokenEstimator.Estimate(messages);
        var budget = new ContextBudgetSnapshot(
            200_000,
            8192,
            20_000,
            estimated,
            120_000,
            (double)(120_000 + 20_000) / 200_000);

        var effective = ContextTokenEstimator.ResolveEffectiveEstimate(
            messages,
            settings,
            budget,
            knownRawHistoryEstimate: estimated);

        Assert.Equal(120_000, effective);
    }

    [Fact]
    public void SummaryMessageBuilder_WithTranscript_UsesAgentScopeFormat()
    {
        var summary = SummaryMessageBuilder.CreateSummaryPlaceholder("facts", "/tmp/transcript.jsonl");
        Assert.Equal(MessageRole.Summary, summary.Role);
        Assert.Contains("conversation that has been summarized", summary.Content, StringComparison.Ordinal);
        Assert.Contains("/tmp/transcript.jsonl", summary.Content, StringComparison.Ordinal);
        Assert.Contains("<summary>", summary.Content, StringComparison.Ordinal);
        Assert.True(SummaryMessageBuilder.IsSummaryMessage(summary));
    }

    [Fact]
    public async Task PreCompletionPipeline_BelowThreshold_DoesNotCompact()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                TriggerMessages = 100,
                TriggerTokens = 1_000_000
            }
        };

        var session = AgentSession.Create("test")
            .WithMessages(new[] { ChatMessage.Create(MessageRole.User, "hello") });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        var result = await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(0, compactor.CallCount);
        Assert.Single(result.Messages);
    }

    [Fact]
    public async Task PreCompletionPipeline_AboveThreshold_TriggersConversationCompact()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                TriggerMessages = 2,
                KeepMessages = 1
            }
        };

        var session = AgentSession.Create("test")
            .WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three")
            });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(1, compactor.CallCount);
    }

    [Fact]
    public async Task PreCompletionPipeline_Disabled_SkipsProactiveCompaction()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = false,
                TriggerMessages = 2,
                KeepMessages = 1
            }
        };

        var session = AgentSession.Create("disabled")
            .WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three")
            });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(0, compactor.CallCount);
    }

    [Fact]
    public async Task ConversationCompactor_ReplacesPrefixWithSummaryAndTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("compact-session")
                .WithMessages(new[]
                {
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(
                        MessageRole.Assistant,
                        "hi",
                        toolCalls: new[] { new AgentToolCall("t1", "file_read", new Dictionary<string, string>()) }),
                    ChatMessage.Create(MessageRole.Tool, "ToolCallId: t1\ntool output")
                });

            var compactor = new ConversationCompactor(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: false,
                    EmitAudit: true));

            Assert.True(result.Compacted);
            Assert.Equal(4, result.Session.Messages.Count);
            Assert.Equal(MessageRole.Compaction, result.Session.Messages[0].Role);
            Assert.Contains("conversationcompact", result.Session.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
            Assert.True(SummaryMessageBuilder.IsSummaryMessage(result.Session.Messages[1]));
            Assert.Equal(MessageRole.Assistant, result.Session.Messages[2].Role);
            Assert.Equal(MessageRole.Tool, result.Session.Messages[^1].Role);
            Assert.Contains("tool output", result.Session.Messages[^1].Content, StringComparison.Ordinal);

            var transcriptDir = Path.Combine(paths.SessionsPath, session.Id, "transcripts");
            Assert.True(Directory.Exists(transcriptDir));
            Assert.NotEmpty(Directory.GetFiles(transcriptDir, "transcript_*.jsonl"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_ManualStrategy_WritesManualCompactAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("manual-compact-session")
                .WithMessages(new[]
                {
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi")
                });

            var compactor = new ConversationCompactor(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true,
                    Strategy: CompactionStrategy.ManualCompact));

            Assert.True(result.Compacted);
            Assert.Contains("manual_compact", result.Session.Messages[0].Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_Cancellation_DoesNotModifySession()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("cancel-compact-session")
                .WithMessages(new[]
                {
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi")
                });
            var originalCount = session.Messages.Count;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var compactor = new ConversationCompactor(
                settings,
                new CancellingModelClient(),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                compactor.CompactIfNeededAsync(
                    session,
                    new CompactionExecutionRequest(
                        CompactionKind.ConversationCompact,
                        Force: true,
                        EmitAudit: true,
                        Strategy: CompactionStrategy.ManualCompact),
                    cts.Token));

            Assert.Equal(originalCount, session.Messages.Count);
            Assert.DoesNotContain(session.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_ManualForce_CompactsWhenConversationShorterThanKeepMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 100,
                    KeepMessages = 20,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("short-manual").WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three"),
            });

            var compactor = new ConversationCompactor(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true,
                    Strategy: CompactionStrategy.ManualCompact));

            Assert.True(result.Compacted);
            Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_ManualForce_CompactsSingleMessageConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 100,
                    KeepMessages = 20,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("single-manual").WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "only message"),
            });

            var compactor = new ConversationCompactor(
                settings,
                new FakeModelClient("summary text"),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true,
                    Strategy: CompactionStrategy.ManualCompact));

            Assert.True(result.Compacted);
            Assert.Contains(result.Session.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void CompactionMessageContent_IsSummaryPlaceholder_DetectsMarker()
    {
        var content = $"{ConversationCompactionDefaults.SummaryMessageMarker}\nsummary";
        Assert.True(CompactionMessageContent.IsSummaryPlaceholder(content));
    }

    [Fact]
    public void ConversationCompact_PreservesFullSummaryPreview()
    {
        var longSummary = new string('x', 400);
        var content = CompactionMessageContent.CreateConversationCompact(100, 80, 5, "t.jsonl", longSummary);
        Assert.Contains(longSummary, content, StringComparison.Ordinal);
        Assert.DoesNotContain("...", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextBudgetCalculator_IncludesSystemPromptAndToolsInOverhead()
    {
        var settings = new ContextCompactionSettings { ContextWindowTokens = 100_000 };
        var model = new ModelSettings { MaxTokens = 8192 };
        var tools = new[]
        {
            new ToolDefinition("file_read", "Read files", ToolSchema.Object().String("path", "path", required: true).Build())
        };
        var messages = new[] { ChatMessage.Create(MessageRole.User, new string('x', 5000)) };

        var budget = ContextBudgetCalculator.Compute(
            environmentPrompt: new string('s', 10_000),
            tools,
            messages,
            settings,
            model);

        Assert.True(budget.FixedOverhead > 0);
        Assert.True(budget.HistoryBudget < budget.TotalWindow - budget.ReservedOutput);
        Assert.True(budget.HistoryUtilization > 0);
    }

    [Fact]
    public void ContextBudgetCalculator_IncludesRuntimeContextInOverhead()
    {
        var settings = new ContextCompactionSettings { ContextWindowTokens = 100_000 };
        var model = new ModelSettings { MaxTokens = 8192 };

        var withoutRuntimeContext = ContextBudgetCalculator.Compute(
            "system",
            [],
            [],
            settings,
            model);
        var withRuntimeContext = ContextBudgetCalculator.Compute(
            "system",
            [],
            [],
            settings,
            model,
            runtimeContext: new string('r', 1000));

        Assert.True(withRuntimeContext.FixedOverhead > withoutRuntimeContext.FixedOverhead);
        Assert.True(withRuntimeContext.HistoryBudget < withoutRuntimeContext.HistoryBudget);
    }

    [Fact]
    public void ContextBudgetCalculator_DoesNotAllocateHistoryWhenFixedContextExceedsWindow()
    {
        var settings = new ContextCompactionSettings
        {
            ContextWindowTokens = 1000,
            DynamicCompaction = new DynamicCompactionSettings { Enabled = true }
        };
        var budget = ContextBudgetCalculator.Compute(
            "system",
            [],
            [],
            settings,
            new ModelSettings { MaxTokens = 500 },
            runtimeContext: new string('r', 10_000));

        Assert.Equal(0, budget.HistoryBudget);
        Assert.True(budget.FixedOverhead + budget.ReservedOutput > budget.TotalWindow);
        Assert.Equal(
            0,
            ContextPressureEvaluator.ResolveKeepTokenBudget(
                budget,
                ContextPressureLevel.Overflow,
                [],
                settings,
                includesConversationCompact: true));

        var conversation = new[]
        {
            ChatMessage.Create(MessageRole.User, "old question"),
            ChatMessage.Create(MessageRole.Assistant, "old answer")
        };
        Assert.Equal(
            conversation.Length,
            ConversationCutoffPlanner.DetermineCutoffIndex(
                conversation,
                ContextTokenEstimator.Estimate(conversation),
                settings,
                keepTokenBudgetOverride: 0));
    }

    [Fact]
    public void ContextBudgetSnapshot_TotalUtilization_IncludesFixedOverhead()
    {
        var budget = new ContextBudgetSnapshot(200_000, 8192, 40_000, 120_000, 50_000, 0.42);

        Assert.Equal(90_000, budget.EstimatedTotalPrompt);
        Assert.InRange(budget.TotalUtilization, 0.45, 0.48);
    }

    [Fact]
    public void ContextPressureEvaluator_MapsTotalWindowUtilizationToLevels()
    {
        var dynamic = new DynamicCompactionSettings();
        var overflowBudget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 90_000, 0.9);

        Assert.Equal(ContextPressureLevel.Overflow, ContextPressureEvaluator.Evaluate(overflowBudget, dynamic, forceOverflow: true));

        var criticalBudget = new ContextBudgetSnapshot(200_000, 8192, 30_000, 100_000, 150_000, 0.9);
        Assert.Equal(ContextPressureLevel.Critical, ContextPressureEvaluator.Evaluate(criticalBudget, dynamic));

        var elevatedBudget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 90_000, 0.9);
        Assert.Equal(ContextPressureLevel.Elevated, ContextPressureEvaluator.Evaluate(elevatedBudget, dynamic));
    }

    [Fact]
    public void DynamicCompactionPlan_DoesNotTruncateBeforeStaticBaseline()
    {
        var settings = new ContextCompactionSettings();
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 10_000, 0.1);
        var conversation = new[] { ChatMessage.Create(MessageRole.User, "hello") };

        var plan = DynamicCompactionPlan.Create(
            ContextPressureEvaluator.Evaluate(budget, settings.DynamicCompaction),
            budget,
            conversation,
            settings,
            force: false);

        Assert.False(plan.ApplyTruncateArgs);
        Assert.False(plan.ApplyConversationCompact);
    }

    [Fact]
    public void DynamicCompactionPlan_TruncatesWhenStaticThresholdReached()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = Enumerable.Range(0, 30)
            .Select(index => ChatMessage.Create(MessageRole.User, $"message-{index}"))
            .ToList();
        var estimated = ContextTokenEstimator.Estimate(conversation);
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 180_000, estimated, 0.2);

        Assert.True(ContextPressureEvaluator.MeetsStaticTruncateThreshold(conversation, settings));

        var plan = DynamicCompactionPlan.Create(
            ContextPressureEvaluator.Evaluate(budget, settings.DynamicCompaction),
            budget,
            conversation,
            settings,
            force: false);

        Assert.True(plan.ApplyTruncateArgs);
        Assert.False(plan.ApplyConversationCompact);
    }

    [Fact]
    public void DynamicCompactionPlan_DoesNotCompactOnStaticMessageCountWhenUtilizationLow()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = Enumerable.Range(0, 55)
            .Select(index => ChatMessage.Create(MessageRole.User, $"m-{index}"))
            .ToList();
        var estimated = ContextTokenEstimator.Estimate(conversation);
        var budget = new ContextBudgetSnapshot(256_000, 8192, 20_000, 200_000, estimated, 0.25);

        Assert.True(ContextPressureEvaluator.MeetsStaticCompactThreshold(conversation, settings));
        Assert.True(budget.TotalUtilization < settings.DynamicCompaction.TargetUtilization);

        var plan = DynamicCompactionPlan.Create(
            ContextPressureEvaluator.Evaluate(budget, settings.DynamicCompaction),
            budget,
            conversation,
            settings,
            force: false);

        Assert.False(plan.ApplyConversationCompact);
    }

    [Fact]
    public void DynamicCompactionPlan_CompactsWhenTotalWindowNearLimitBeforeStaticHistoryThreshold()
    {
        var settings = CreateDynamicCompactionSettings(s =>
        {
            s.TriggerTokens = 500_000;
            s.TriggerMessages = 0;
        });
        var conversation = new[] { ChatMessage.Create(MessageRole.User, new string('x', 400_000)) };
        var estimated = ContextTokenEstimator.Estimate(conversation);
        Assert.False(ContextPressureEvaluator.MeetsStaticCompactThreshold(conversation, settings));

        var budget = new ContextBudgetSnapshot(256_000, 8192, 60_000, 120_000, estimated, 0.9);
        Assert.True(budget.TotalUtilization >= settings.DynamicCompaction.TargetUtilization);

        var plan = DynamicCompactionPlan.Create(
            ContextPressureEvaluator.Evaluate(budget, settings.DynamicCompaction),
            budget,
            conversation,
            settings,
            force: false);

        Assert.True(plan.ApplyConversationCompact);
    }

    [Fact]
    public void ResolveKeepTokenBudget_NeverBelowStaticKeepTokens()
    {
        var settings = CreateDynamicCompactionSettings(s => s.KeepTokens = 80_000);
        var conversation = new[] { ChatMessage.Create(MessageRole.User, "hello") };
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 5_000, 0.05);

        var keep = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            ContextPressureLevel.Critical,
            conversation,
            settings,
            includesConversationCompact: true);

        Assert.True(keep >= 80_000);
    }

    [Fact]
    public void ResolveKeepTokenBudget_AfterFullPassTargetsThirtyPercent()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = new[] { ChatMessage.Create(MessageRole.User, "hello") };
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 5_000, 0.05);

        var keep = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            ContextPressureLevel.Critical,
            conversation,
            settings,
            includesConversationCompact: true);

        var expected = (int)Math.Floor(
            settings.DynamicCompaction.PostCompactionUtilization * budget.UsablePromptWindow
            - budget.FixedOverhead);

        Assert.Equal(expected, keep);
    }

    [Fact]
    public void ResolveKeepTokenBudget_TruncateOnlyUsesStaticKeepFloor()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = Enumerable.Range(0, 25)
            .Select(index => ChatMessage.Create(MessageRole.User, $"message-{index}"))
            .ToList();
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 5_000, 0.05);

        var keep = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            ContextPressureLevel.High,
            conversation,
            settings,
            includesConversationCompact: false);

        var staticKeep = ContextTokenEstimator.EstimateSuffix(
            conversation,
            Math.Max(0, conversation.Count - settings.KeepMessages));

        Assert.Equal(Math.Max(staticKeep, 512), keep);
    }

    [Fact]
    public void ResolveKeepTokenBudget_OverflowUsesReducedPostCompactionTarget()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = new[] { ChatMessage.Create(MessageRole.User, "hello") };
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, 5_000, 0.05);
        var dynamic = settings.DynamicCompaction;

        var keep = ContextPressureEvaluator.ResolveKeepTokenBudget(
            budget,
            ContextPressureLevel.Overflow,
            conversation,
            settings,
            includesConversationCompact: true);

        var expected = (int)Math.Floor(
            dynamic.OverflowPostCompactionUtilization * budget.UsablePromptWindow
            - budget.FixedOverhead);

        Assert.Equal(Math.Max(512, expected), keep);
    }

    [Fact]
    public void DynamicCompactionPlan_Elevated_AppliesTruncateOnlyWhenStaticTriggered()
    {
        var settings = CreateDynamicCompactionSettings();
        var conversation = Enumerable.Range(0, 30)
            .Select(index => ChatMessage.Create(MessageRole.User, $"message-{index}"))
            .ToList();
        var estimated = ContextTokenEstimator.Estimate(conversation);
        var budget = new ContextBudgetSnapshot(200_000, 8192, 20_000, 100_000, estimated, 0.6);

        var plan = DynamicCompactionPlan.Create(
            ContextPressureLevel.Elevated,
            budget,
            conversation,
            settings,
            force: false);

        Assert.True(plan.ApplyTruncateArgs);
        Assert.False(plan.ApplyPrefixReEvict);
        Assert.False(plan.ApplyConversationCompact);
    }

    [Fact]
    public void SemanticCutoffPlanner_BuildMustPreserveAppendix_IncludesUserMessages()
    {
        var settings = new ContextCompactionSettings();
        var conversation = new[]
        {
            ChatMessage.Create(MessageRole.User, "Implement src/Foo.cs"),
            ChatMessage.Create(MessageRole.Assistant, "ok"),
            ChatMessage.Create(MessageRole.User, "latest"),
            ChatMessage.Create(MessageRole.Assistant, "working")
        };

        var appendix = SemanticCutoffPlanner.BuildMustPreserveAppendix(conversation, settings, keepTokenBudget: 30);

        Assert.NotNull(appendix);
        Assert.Contains("<must_preserve>", appendix, StringComparison.Ordinal);
        Assert.Contains("Foo.cs", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenEstimatorCalibrator_UpdatesMultiplierFromUsage()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                DynamicCompaction = new DynamicCompactionSettings { EnableUsageCalibration = true }
            }
        };
        var calibrator = new TokenEstimatorCalibrator(settings);

        calibrator.Observe("session-1", estimatedPromptTokens: 1000, actualPromptTokens: 2000);

        Assert.True(calibrator.GetMultiplier("session-1") > 1.0);
    }

    [Fact]
    public async Task PreCompletionPipeline_DynamicDisabled_UsesLegacyThresholds()
    {
        var settings = new AppSettings
        {
            ContextCompaction = new ContextCompactionSettings
            {
                Enabled = true,
                TriggerMessages = 2,
                KeepMessages = 1,
                DynamicCompaction = new DynamicCompactionSettings { Enabled = false }
            }
        };

        var session = AgentSession.Create("legacy")
            .WithMessages(new[]
            {
                ChatMessage.Create(MessageRole.User, "one"),
                ChatMessage.Create(MessageRole.Assistant, "two"),
                ChatMessage.Create(MessageRole.User, "three")
            });

        var compactor = new FakeConversationCompactor(settings);
        var pipeline = new PreCompletionPipeline(
            compactor,
            new TruncateArgsService(),
            settings,
            new NoOpLogger());

        await pipeline.RunAsync(session, PreCompletionOptions.AgentLoop);

        Assert.Equal(1, compactor.CallCount);
    }

    [Fact]
    public void SemanticCutoffPlanner_SkipsSummaryPlaceholderAsProtectedUser()
    {
        var settings = new ContextCompactionSettings
        {
            DynamicCompaction = new DynamicCompactionSettings { EnableSemanticCutoff = true }
        };
        // Legacy User+marker must not anchor the protected tail.
        var legacySummary = ChatMessage.Create(
            MessageRole.User,
            ConversationCompactionDefaults.SummaryMessageMarker + "\nprior summary about Foo");
        var conversation = new List<ChatMessage>
        {
            legacySummary,
            ChatMessage.Create(MessageRole.Assistant, "working on it"),
            ChatMessage.Create(MessageRole.Assistant, "still going")
        };

        var cutoff = SemanticCutoffPlanner.DetermineCutoffIndex(conversation, settings, keepTokenBudget: 1);

        // Tiny keep budget + no real user → cutoff at end (not 0, which would keep from the summary).
        Assert.Equal(conversation.Count, cutoff);

        var roleSummary = SummaryMessageBuilder.CreateSummaryPlaceholder("prior summary about Foo", null);
        var withRole = new List<ChatMessage>
        {
            roleSummary,
            ChatMessage.Create(MessageRole.Assistant, "working on it"),
            ChatMessage.Create(MessageRole.User, "continue"),
            ChatMessage.Create(MessageRole.Assistant, "still going")
        };
        var roleCutoff = SemanticCutoffPlanner.DetermineCutoffIndex(withRole, settings, keepTokenBudget: 1);
        Assert.True(roleCutoff > 0);
        Assert.True(roleCutoff < withRole.Count);
        Assert.True(roleCutoff >= 2); // real user index — never treat Summary role as protected user
    }

    [Fact]
    public void TruncateArgsService_HonorsZeroKeepBudgetWithoutStaticGate()
    {
        var settings = new ContextCompactionSettings
        {
            TruncateArgs = new TruncateArgsSettings
            {
                Enabled = true,
                TriggerMessages = 100,
                TriggerTokens = 1_000_000,
                KeepMessages = 20,
                MaxArgLength = 10,
                TruncationText = "...(truncated)"
            }
        };
        var longArg = new string('x', 200);
        var messages = new List<ChatMessage>
        {
            ChatMessage.Create(
                MessageRole.Assistant,
                string.Empty,
                toolCalls: [new AgentToolCall("t1", "file_write", new Dictionary<string, string> { ["content"] = longArg })])
        };

        var updated = new TruncateArgsService().ApplyToMessages(messages, settings, out var changed, keepTokenBudgetOverride: 0);

        Assert.True(changed);
        Assert.DoesNotContain(longArg, updated[0].ToolCallsJson, StringComparison.Ordinal);
        Assert.Contains("...(truncated)", updated[0].ToolCallsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactionAuditDisplay_Parse_IncludesPressureAndUtilization()
    {
        var content = CompactionMessageContent.CreateConversationCompact(
            1000,
            500,
            12,
            "transcript.jsonl",
            "summary body",
            pressureLevel: ContextPressureLevel.Critical,
            utilization: 0.91);

        var display = CompactionAuditDisplay.Parse(content);

        Assert.Contains("Critical", display.StrategySubtitle, StringComparison.Ordinal);
        Assert.Contains("0.91", display.StrategySubtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void FitToMaxChars_KeepsHeadAndTailWithOmitMarker()
    {
        var head = new string('A', 100);
        var middle = new string('M', 500);
        var tail = new string('Z', 100);
        var text = head + middle + tail;

        var fitted = ConversationSummaryFormatter.FitToMaxChars(text, 250);

        Assert.True(fitted.Length <= 250);
        Assert.StartsWith("AAA", fitted, StringComparison.Ordinal);
        Assert.EndsWith("ZZZ", fitted, StringComparison.Ordinal);
        Assert.Contains("[... middle omitted for summary budget ...]", fitted, StringComparison.Ordinal);
        // Head/tail split may still include the end of the middle block in the tail window;
        // ensure the deep middle of the original is gone.
        Assert.DoesNotContain(new string('M', 200), fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void FitToMaxChars_ReturnsOriginalWhenWithinBudget()
    {
        var text = "short";
        Assert.Equal(text, ConversationSummaryFormatter.FitToMaxChars(text, 100));
    }

    [Fact]
    public async Task ConversationCompactor_SummaryFailure_LeavesSessionUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("fail-compact")
                .WithMessages(
                [
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi")
                ]);
            var originalIds = session.Messages.Select(message => message.Id).ToArray();

            var compactor = new ConversationCompactor(
                settings,
                new ThrowingModelClient(),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger());

            var result = await compactor.CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true));

            Assert.False(result.Compacted);
            Assert.Equal(originalIds, result.Session.Messages.Select(message => message.Id).ToArray());
            Assert.DoesNotContain(result.Session.Messages, message => message.Role == MessageRole.Compaction);
            Assert.DoesNotContain(result.Session.Messages, SummaryMessageBuilder.IsSummaryMessage);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_EmptySummary_LeavesSessionUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var session = AgentSession.Create("empty-summary")
                .WithMessages(
                [
                    ChatMessage.Create(MessageRole.User, "old"),
                    ChatMessage.Create(MessageRole.Assistant, "earlier"),
                    ChatMessage.Create(MessageRole.User, "hello"),
                    ChatMessage.Create(MessageRole.Assistant, "hi")
                ]);
            var originalCount = session.Messages.Count;

            var result = await new ConversationCompactor(
                settings,
                new FakeModelClient("   \n\t  "),
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true));

            Assert.False(result.Compacted);
            Assert.Equal(originalCount, result.Session.Messages.Count);
            Assert.DoesNotContain(result.Session.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ConversationCompactor_SecondCompact_IncludesPriorSummaryInPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-tests", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var settings = new AppSettings
            {
                ContextCompaction = new ContextCompactionSettings
                {
                    TriggerMessages = 2,
                    KeepMessages = 1,
                    SummaryMaxTokens = 512,
                    MaxConversationCharsForSummary = 10_000,
                    OffloadBeforeCompact = false
                }
            };

            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var capturing = new CapturingModelClient("first condensed summary about Foo.cs");
            var session = AgentSession.Create("second-compact")
                .WithMessages(
                [
                    ChatMessage.Create(MessageRole.User, "implement Foo.cs"),
                    ChatMessage.Create(MessageRole.Assistant, "done"),
                    ChatMessage.Create(MessageRole.User, "continue"),
                    ChatMessage.Create(MessageRole.Assistant, "ok")
                ]);

            var first = await new ConversationCompactor(
                settings,
                capturing,
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                session,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true,
                    Strategy: CompactionStrategy.ManualCompact));

            Assert.True(first.Compacted);
            Assert.Contains(first.Session.Messages, SummaryMessageBuilder.IsSummaryMessage);

            // Grow the conversation past the first summary so the prior summary lands in the next prefix.
            var grown = first.Session.WithMessages(
                first.Session.Messages
                    .Concat(
                    [
                        ChatMessage.Create(MessageRole.User, "more work"),
                        ChatMessage.Create(MessageRole.Assistant, "working"),
                        ChatMessage.Create(MessageRole.User, "even more"),
                        ChatMessage.Create(MessageRole.Assistant, "still working")
                    ])
                    .ToList());

            capturing.Content = "second summary";
            capturing.ClearCaptured();

            var second = await new ConversationCompactor(
                settings,
                capturing,
                storage,
                new TruncateArgsService(),
                new SessionUsageAccumulator(),
                new NoOpLogger()).CompactIfNeededAsync(
                grown,
                new CompactionExecutionRequest(
                    CompactionKind.ConversationCompact,
                    Force: true,
                    EmitAudit: true,
                    Strategy: CompactionStrategy.ManualCompact));

            Assert.True(second.Compacted);
            Assert.NotNull(capturing.LastPrompt);
            Assert.Contains("first condensed summary about Foo.cs", capturing.LastPrompt, StringComparison.Ordinal);
            Assert.Contains(
                ConversationCompactionDefaults.SummaryMessageMarker,
                capturing.LastPrompt,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task FileStorageService_RoundTripsCompactionMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "athlon-compact-rt", Guid.NewGuid().ToString("N"));
        var paths = new TestAppPathProvider(root);
        paths.EnsureCreated();

        try
        {
            var storage = new FileStorageService(new NoOpLogger(), paths, new JsonFileStore(), new AgentRunContextAccessor());
            var audit = CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(100, 80, 3, "fake.jsonl", "summary"));
            var session = AgentSession.Create("rt").WithMessage(audit);
            await storage.SaveSessionAsync(session);

            var loaded = await storage.LoadSessionAsync(session.Id);
            Assert.NotNull(loaded);
            Assert.Contains(loaded.Messages, message => message.Role == MessageRole.Compaction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class FakeConversationCompactor(AppSettings settings) : IConversationCompactor
    {
        public int CallCount { get; private set; }

        public Task<ConversationCompactResult> CompactIfNeededAsync(
            AgentSession session,
            CompactionExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            var conversation = session.Messages.Where(message => message.Role != MessageRole.Compaction).ToList();
            var cfg = settings.ContextCompaction;

            if (!ConversationCutoffPlanner.ShouldCompact(
                    conversation,
                    ContextTokenEstimator.Estimate(conversation, cfg.IncludeReasoningInModelContext),
                    cfg,
                    request.Force))
            {
                return Task.FromResult(new ConversationCompactResult(session, false));
            }

            CallCount++;
            var audit = CompactionMessageContent.CreateCompactionMessage(
                CompactionMessageContent.CreateConversationCompact(1, 1, conversation.Count, "fake.jsonl", "summary"));
            return Task.FromResult(new ConversationCompactResult(
                session.WithMessages(new[]
                {
                    audit,
                    SummaryMessageBuilder.CreateSummaryPlaceholder("summary", null)
                }),
                true));
        }
    }

    private sealed class FakeModelClient(string content) : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));
    }

    private sealed class ThrowingModelClient : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("summary failed");
    }

    private sealed class CapturingModelClient(string content) : IAgentModelClient
    {
        public string Content { get; set; } = content;

        public string? LastPrompt { get; private set; }

        public void ClearCaptured() => LastPrompt = null;

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = request.Messages.FirstOrDefault()?.Content as string
                ?? request.Messages.FirstOrDefault()?.Content?.ToString();
            return Task.FromResult(new AgentModelResponse(Content, Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class CancellingModelClient : IAgentModelClient
    {
        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentModelResponse("summary", Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }

    internal sealed class TestAppPathProvider(string root) : IAppPathProvider
    {
        public string RootPath { get; } = root;
        public string ConfigPath => Path.Combine(RootPath, "config");
        public string SessionsPath => Path.Combine(RootPath, "sessions");
        public string AuditPath => Path.Combine(RootPath, "audit");
        public string LogsPath => Path.Combine(RootPath, "logs");
        public string CredentialsPath => Path.Combine(RootPath, "credentials");
        public string SkillsPath => Path.Combine(RootPath, AppPathProvider.SkillsFolderName);

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(ConfigPath);
            Directory.CreateDirectory(SessionsPath);
            Directory.CreateDirectory(AuditPath);
            Directory.CreateDirectory(LogsPath);
            Directory.CreateDirectory(CredentialsPath);
            Directory.CreateDirectory(SkillsPath);
        }

        public string ResolveSkillPath(string path) =>
            string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
                ? path
                : Path.Combine(SkillsPath, path);
    }
}
