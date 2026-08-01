using IslandRpg.Gameplay;
using IslandRpg.Persistence;
using IslandRpg.Rendering.Ui;
using IslandRpg.World;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private const double AiDialogueListeningPoseSeconds = 2.5;
    private readonly TextBoxControlState _aiUrlTextBox =
        new() { MaximumLength = 160 };
    private readonly TextBoxControlState _aiModelTextBox =
        new() { MaximumLength = 80 };
    private readonly TextBoxControlState _aiPasswordTextBox =
        new() { MaximumLength = 160 };
    private readonly NpcAiService _npcAi = new();
    private NpcAiRuntimeState _npcAiState = new(
        NpcAiAvailability.Checking,
        "AI has not been checked.",
        DateTime.MinValue);
    private Task<NpcAiRuntimeState>? _npcAiCheckTask;
    private Task<NpcAiInterpretation?>? _npcAiSpeechTask;
    private int _npcAiSpeechVillagerIndex = -1;
    private string? _npcAiSpeechFallback;
    private const string NpcAiSpeechFallback =
        "Sorry, I didn't understand that. Could you say it another way?";
    private Task<string?>? _npcAiDialogueTask;
    private IReadOnlyList<string>? _npcAiDialogueGroupListenerIds;
    private string? _npcAiDialogueGroupPurpose;
    private string? _npcAiDialogueSpeakerId;
    private string? _npcAiDialogueListenerId;
    private string? _npcAiDialogueFallback;
    private VillagerSocialIntent _npcAiDialogueIntent;
    private bool _npcAiDialogueReplyPending;
    private string? _npcAiDialogueReplyFallback;
    private double _npcAiDialogueReadyAt;
    private Task<IReadOnlyList<VillagerPersona>?>?
        _npcPersonaGenerationTask;
    private PendingNewWorldCreation? _pendingNewWorldCreation;
    private bool _aiFieldsWereFocused;
    private sealed record PendingNewWorldCreation(
        string Name,
        long Seed,
        Vector2 Spawn,
        PlayerProfile Player,
        int Population,
        bool ObserveWorld = false,
        string SharedStory = "",
        string ModelOverride = "");

    private void BeginNpcAiCheck(bool useActiveWorld = false)
    {
        if (_npcAiCheckTask is { IsCompleted: false })
            return;
        var settings = useActiveWorld
            ? ActiveNpcAiSettings()
            : _saves.LoadSettings().EffectiveAi;
        _npcAiState = settings.Enabled
            ? new(
                NpcAiAvailability.Checking,
                "Checking AI server and model...",
                DateTime.UtcNow)
            : new(
                NpcAiAvailability.Disabled,
                "AI is disabled.",
                DateTime.UtcNow);
        _npcAiCheckTask = _npcAi.CheckAsync(settings);
    }

    private void BeginAiWorldCreation(
        string name,
        long seed,
        Vector2 spawn,
        PlayerProfile player,
        int population)
    {
        if (_pendingNewWorldCreation is not null) return;
        _pendingNewWorldCreation = new(
            name, seed, spawn, player, population,
            _newWorldObserveToggle.IsChecked,
            _newWorldSharedStoryTextBox.Text.Trim(),
            _newWorldAiModelOverrideTextBox.Text.Trim());
        _frontendError =
            "Creating survivor histories and personalities...";
        _npcPersonaGenerationTask =
            _npcAi.GeneratePersonasAsync(
                NewWorldNpcAiSettings(),
                name,
                seed,
                Enumerable.Range(0, population)
                    .Select(index =>
                        string.IsNullOrWhiteSpace(_newWorldNpcNameTextBoxes[index].Text)
                            ? VillagerSimulation.NamesForPopulation(population)[index]
                            : _newWorldNpcNameTextBoxes[index].Text.Trim())
                    .ToArray());
    }

    private void UpdateAiWorldCreation()
    {
        if (_pendingNewWorldCreation is not { } pending ||
            _npcPersonaGenerationTask is not { IsCompleted: true } task)
            return;
        IReadOnlyList<VillagerPersona>? personas = null;
        if (task.IsCompletedSuccessfully)
            personas = task.Result;
        else
            _ = task.Exception;
        personas ??= Enumerable.Range(0, pending.Population)
            .Select(VillagerSimulation.DefaultPersona)
            .ToArray();
        _pendingNewWorldCreation = null;
        _npcPersonaGenerationTask = null;
        var setups = BuildNewWorldSetups(personas);
        CompleteNewWorldCreation(
            pending,
            setups.Select(value => value.Persona).ToArray(),
            pending.ObserveWorld,
            pending.SharedStory,
            setups,
            pending.ModelOverride);
    }

    private void InitializeNpcAiSettingsFields(
        NpcAiSettings settings)
    {
        _aiUrlTextBox.SetText(settings.BaseUrl);
        _aiModelTextBox.SetText(settings.Model);
        _aiPasswordTextBox.SetText(settings.Password);
    }

    internal bool UpdateAiSettings(
        Vector2 pointer,
        Vector4 panel)
    {
        _settingsMenu.LayoutContent(panel);
        var settings = _saves.LoadSettings();
        var ai = settings.EffectiveAi;
        if (_settingsMenu.OptionBounds(0).Contains(pointer))
        {
            ai = ai with { Enabled = !ai.Enabled };
            SaveNpcAiSettings(settings, ai);
            BeginNpcAiCheck();
            return true;
        }
        if (AiFieldBounds(1).Contains(pointer))
        {
            FocusTextBox(
                _aiUrlTextBox,
                AiFieldBounds(1),
                pointer);
            return true;
        }
        if (AiFieldBounds(2).Contains(pointer))
        {
            FocusTextBox(
                _aiModelTextBox,
                AiFieldBounds(2),
                pointer);
            return true;
        }
        if (AiFieldBounds(3).Contains(pointer))
        {
            FocusTextBox(
                _aiPasswordTextBox,
                AiFieldBounds(3),
                pointer);
            return true;
        }
        if (!_settingsMenu.OptionBounds(4).Contains(pointer))
            return false;
        ai = ai with
        {
            BaseUrl = _aiUrlTextBox.Text.Trim(),
            Model = _aiModelTextBox.Text.Trim(),
            Password = _aiPasswordTextBox.Text
        };
        SaveNpcAiSettings(settings, ai);
        BeginNpcAiCheck();
        return true;
    }

    private void SaveNpcAiSettings(
        GameSettings settings,
        NpcAiSettings ai) =>
        _saves.SaveSettings(settings with { Ai = ai });

    private Vector4 AiFieldBounds(int option)
    {
        var row = _settingsMenu.OptionBounds(option);
        return new(
            row.X + 120,
            row.Y,
            row.Z - 120,
            row.W);
    }

    private void RenderAiSettings()
    {
        var settings = _saves.LoadSettings().EffectiveAi;
        DrawMenuButton(
            _settingsMenu.OptionBounds(0),
            $"AI enabled: {(settings.Enabled ? "On" : "Off")} — " +
            _npcAiState.Availability);
        RenderAiField(1, "URL", _aiUrlTextBox);
        RenderAiField(2, "Model", _aiModelTextBox);
        RenderAiField(
            3,
            "Password",
            _aiPasswordTextBox,
            mask: true);
        DrawMenuButton(
            _settingsMenu.OptionBounds(4),
            _npcAiState.Availability ==
                NpcAiAvailability.Checking
                ? "Checking AI..."
                : "Save and test — " +
                  _npcAiState.Message);
    }

    private void RenderAiField(
        int option,
        string label,
        TextBoxControlState field,
        bool mask = false)
    {
        var row = _settingsMenu.OptionBounds(option);
        DrawUiText(
            label,
            new(row.X + 10, row.Y + 12),
            new(226, 214, 175, 255));
        field.Bounds = AiFieldBounds(option);
        if (!mask)
        {
            DrawTextField(field);
            return;
        }
        DrawAoEPanelBorder(field.Bounds);
        var value = new string('•', field.Text.Length);
        DrawUiText(
            value,
            VerticallyCenteredTextPosition(
                value, field.Bounds, 14),
            new(226, 214, 175, 255));
    }

    private void UpdateNpcAi()
    {
        var fieldsFocused =
            _aiUrlTextBox.Focused ||
            _aiModelTextBox.Focused ||
            _aiPasswordTextBox.Focused;
        if (_aiFieldsWereFocused && !fieldsFocused)
        {
            var settings = _saves.LoadSettings();
            SaveNpcAiSettings(
                settings,
                settings.EffectiveAi with
                {
                    BaseUrl = _aiUrlTextBox.Text.Trim(),
                    Model = _aiModelTextBox.Text.Trim(),
                    Password = _aiPasswordTextBox.Text
                });
        }
        _aiFieldsWereFocused = fieldsFocused;
        if (_npcAiCheckTask is { IsCompletedSuccessfully: true })
        {
            _npcAiState = _npcAiCheckTask.Result;
            _npcAiCheckTask = null;
        }
        else if (_npcAiCheckTask is { IsFaulted: true })
        {
            _npcAiState = new(
                NpcAiAvailability.ServerUnavailable,
                "AI availability check failed.",
                DateTime.UtcNow);
            _npcAiCheckTask = null;
        }
        CompleteNpcAiDialogue();
        if (_npcAiSpeechTask is { IsFaulted: true })
        {
            var failedIndex = _npcAiSpeechVillagerIndex;
            _npcAiState = new(
                NpcAiAvailability.ModelUnresponsive,
                "AI stopped responding at runtime.",
                DateTime.UtcNow);
            _npcAiSpeechTask = null;
            _npcAiSpeechVillagerIndex = -1;
            var fallback = _npcAiSpeechFallback ??
                           NpcAiSpeechFallback;
            _npcAiSpeechFallback = null;
            if ((uint)failedIndex < (uint)_villagers.Count)
                ShowVillagerSpeech(
                    failedIndex,
                    fallback,
                    _player?.Position ??
                    new(
                        _villagers[failedIndex].PositionX,
                        _villagers[failedIndex].PositionY));
            return;
        }
        if (_npcAiSpeechTask is not
            { IsCompletedSuccessfully: true })
            return;
        var interpretation = _npcAiSpeechTask.Result;
        _npcAiSpeechTask = null;
        var index = _npcAiSpeechVillagerIndex;
        _npcAiSpeechVillagerIndex = -1;
        var speechFallback = _npcAiSpeechFallback ??
                             NpcAiSpeechFallback;
        _npcAiSpeechFallback = null;
        if (interpretation is null)
        {
            _npcAiState = new(
                NpcAiAvailability.Ready,
                "AI response was invalid; used a safe reply and will retry.",
                DateTime.UtcNow);
            if ((uint)index < (uint)_villagers.Count)
                ShowVillagerSpeech(
                    index,
                    speechFallback,
                    _player?.Position ??
                    new(
                        _villagers[index].PositionX,
                        _villagers[index].PositionY));
            return;
        }
        if (
            (uint)index >= (uint)_villagers.Count)
            return;
        ApplyNpcAiInterpretation(
            index, interpretation, speechFallback);
    }

    private void CompleteNpcAiDialogue()
    {
        if (_npcAiDialogueTask is not { IsCompleted: true })
            return;
        if (_clock < _npcAiDialogueReadyAt)
            return;
        var line = _npcAiDialogueTask.IsCompletedSuccessfully
            ? _npcAiDialogueTask.Result
            : null;
        var rawResponse = line;
        var speakerId = _npcAiDialogueSpeakerId;
        var listenerId = _npcAiDialogueListenerId;
        var fallback = _npcAiDialogueFallback;
        var intent = _npcAiDialogueIntent;
        var replyPending = _npcAiDialogueReplyPending;
        var replyFallback = _npcAiDialogueReplyFallback;
        var groupListenerIds = _npcAiDialogueGroupListenerIds;
        var groupPurpose = _npcAiDialogueGroupPurpose;
        _npcAiDialogueTask = null;
        _npcAiDialogueSpeakerId = null;
        _npcAiDialogueListenerId = null;
        _npcAiDialogueFallback = null;
        _npcAiDialogueReplyPending = false;
        _npcAiDialogueReplyFallback = null;
        _npcAiDialogueGroupListenerIds = null;
        _npcAiDialogueGroupPurpose = null;
        _npcAiDialogueReadyAt = 0;
        line = DialogueResponseService.Resolve(line, fallback);
        ObserveLog("ai_dialogue_response", speakerId, new
        {
            ListenerId = listenerId,
            Intent = intent.ToString(),
            RawResponse = rawResponse,
            ResolvedLine = line,
            UsedFallback = !string.Equals(
                rawResponse, line, StringComparison.Ordinal)
        });
        if (string.IsNullOrWhiteSpace(speakerId) ||
            string.IsNullOrWhiteSpace(line))
            return;
        var speakerIndex = _villagers.FindIndex(value =>
            value.Id == speakerId);
        if (speakerIndex < 0) return;
        var listenerPosition =
            _activePlayer is not null &&
            listenerId == _activePlayer.Id &&
            _player is not null
                ? _player.Position
                : _villagers
                    .Where(value => value.Id == listenerId)
                    .Select(value => new Vector2(
                        value.PositionX, value.PositionY))
                    .FirstOrDefault(new Vector2(
                        _villagers[speakerIndex].PositionX,
                        _villagers[speakerIndex].PositionY));
        var listenerIndex = _villagers.FindIndex(value =>
            value.Id == listenerId);
        if (groupListenerIds is { Count: > 0 })
        {
            foreach (var groupListenerId in groupListenerIds)
            {
                var groupIndex = _villagers.FindIndex(value =>
                    value.Id == groupListenerId);
                if (groupIndex < 0) continue;
                _villagers[groupIndex] = VillagerSimulation.RecordDialogueTurn(
                    _villagers[groupIndex], speakerId,
                    _villagers[speakerIndex].Name, line, _worldGameSeconds);
                if (groupPurpose == "introduction" &&
                    groupIndex != speakerIndex)
                    _villagers[groupIndex] =
                        VillagerSimulation.RecordIntroductionResponse(
                            _villagers[groupIndex],
                            speakerId,
                            _villagers[speakerIndex].Name,
                            _worldGameSeconds);
            }
            _villagersDirty = true;
        }
        else
            RecordVillagerDialogueLine(
                speakerIndex, listenerIndex, line);
        if (listenerIndex >= 0)
            HoldVillagerConversation(
                listenerIndex,
                new(
                    _villagers[speakerIndex].PositionX,
                    _villagers[speakerIndex].PositionY),
                ConversationLineSeconds(line));
        ShowVillagerSpeech(
            speakerIndex, line, listenerPosition);
        if (groupPurpose == "proposal" &&
            _settlementCouncilCenterSpeakerId == speakerId)
        {
            _settlementCouncilCandidateShouldReturn = true;
            _settlementCouncilCandidateReturnAt =
                _clock + ConversationLineSeconds(line);
        }
        if (!replyPending)
        {
            _villagers[speakerIndex] =
                VillagerSimulation.ResumeAfterConversation(
                    _villagers[speakerIndex], _worldGameSeconds);
            if (listenerIndex >= 0)
                _villagers[listenerIndex] =
                    VillagerSimulation.ResumeAfterConversation(
                        _villagers[listenerIndex], _worldGameSeconds);
            _villagersDirty = true;
        }
        if (replyPending && listenerIndex >= 0)
        {
            var replyingVillager = _villagers[listenerIndex];
            var originalSpeaker = _villagers[speakerIndex];
            SpeakVillagerDialogue(
                replyingVillager,
                originalSpeaker.Id,
                originalSpeaker.Name,
                intent,
                replyFallback ?? VillagerReplyFallback(
                    replyingVillager, originalSpeaker, intent),
                allowNpcReply: false,
                readyAt:
                    _clock + ConversationLineSeconds(line));
        }
    }

    private void SpeakVillagerDialogue(
        VillagerState speaker,
        string listenerId,
        string listenerName,
        VillagerSocialIntent intent,
        string fallback,
        bool allowNpcReply = true,
        double readyAt = 0,
        string? replyFallback = null)
    {
        if (_npcAiDialogueTask is { IsCompleted: false })
            return;
        var settings = ActiveNpcAiSettings();
        _npcAiDialogueSpeakerId = speaker.Id;
        _npcAiDialogueListenerId = listenerId;
        _npcAiDialogueFallback = fallback;
        _npcAiDialogueIntent = intent;
        _npcAiDialogueReplyPending =
            allowNpcReply &&
            _villagers.Any(value => value.Id == listenerId);
        _npcAiDialogueReplyFallback = replyFallback;
        _npcAiDialogueReadyAt = readyAt;
        var currentSpeakerIndex = _villagers.FindIndex(value =>
            value.Id == speaker.Id);
        var currentListenerPosition =
            _activePlayer?.Id == listenerId &&
            _player is not null
                ? _player.Position
                : _villagers
                    .Where(value => value.Id == listenerId)
                    .Select(value => new Vector2(
                        value.PositionX, value.PositionY))
                    .FirstOrDefault(new Vector2(
                        speaker.PositionX,
                        speaker.PositionY));
        HoldVillagerConversation(
            currentSpeakerIndex,
            currentListenerPosition,
            AiDialogueListeningPoseSeconds);
        var currentListenerIndex = _villagers.FindIndex(value =>
            value.Id == listenerId);
        if (currentListenerIndex >= 0)
            HoldVillagerConversation(
                currentListenerIndex,
                new(speaker.PositionX, speaker.PositionY),
                AiDialogueListeningPoseSeconds);
        TakeConversationFloor(
            speaker.Id,
            AiDialogueListeningPoseSeconds);
        var context = new NpcAiDialogueContext(
                speaker.Name,
                listenerName,
                intent.ToString(),
                fallback,
                speaker.Hunger,
                RelationshipDescription(speaker, listenerId),
                VillagerSimulation.RecallMemories(
                        speaker,
                        listenerId,
                        fallback,
                        _worldGameSeconds)
                    .Select(memory =>
                        memory.Summary ?? memory.Kind)
                    .ToArray(),
                speaker.Persona?.BackgroundStory ?? "",
                speaker.Persona?.Personality ?? "",
                speaker.Persona?.PriorTrade ?? "",
                speaker.Persona?.KnownToolIds ?? [],
                speaker.Persona?.ArrivalMemory ?? "",
                VillagerSimulation.HoursOnIsland(
                    speaker, _worldGameSeconds),
                speaker.ConversationHistory);
        ObserveLog("ai_dialogue_request", speaker.Id, new
        {
            ListenerId = listenerId,
            ListenerName = listenerName,
            Intent = intent.ToString(),
            Fallback = fallback,
            Context = context,
            ModelEnabled = _npcAiState.Ready
        });
        _npcAiDialogueTask = _npcAiState.Ready
            ? _npcAi.ComposeDialogueAsync(
                settings, context)
            : Task.FromResult<string?>(fallback);
    }

    private void RecordVillagerDialogueLine(
        int speakerIndex,
        int listenerIndex,
        string line)
    {
        if ((uint)speakerIndex >= (uint)_villagers.Count)
            return;
        if ((uint)listenerIndex >= (uint)_villagers.Count)
        {
            var speaker = _villagers[speakerIndex];
            _villagers[speakerIndex] =
                VillagerSimulation.RecordDialogueTurn(
                    speaker,
                    speaker.Id,
                    speaker.Name,
                    line,
                    _worldGameSeconds);
            _villagersDirty = true;
            return;
        }
        var exchange =
            VillagerSimulation.RecordSharedDialogueLine(
                _villagers[speakerIndex],
                _villagers[listenerIndex],
                line,
                _worldGameSeconds);
        _villagers[speakerIndex] = exchange.Speaker;
        _villagers[listenerIndex] = exchange.Listener;
        _villagersDirty = true;
    }

    private static string VillagerReplyFallback(
        VillagerState listener,
        VillagerState speaker,
        VillagerSocialIntent intent) =>
        intent switch
        {
            VillagerSocialIntent.Introduce =>
                $"I'm {listener.Name}. It's good to meet you, {speaker.Name}.",
            VillagerSocialIntent.AskOrigin =>
                "I remember waking here, but not how I arrived.",
            VillagerSocialIntent.AskSurvival =>
                "We should share what we learn about food, water, and shelter.",
            VillagerSocialIntent.AskTools =>
                listener.Persona is { } persona
                    ? $"I was a {persona.PriorTrade}; I know a few useful tools."
                    : "I know a few useful tools, but we still need supplies.",
            VillagerSocialIntent.RequestFood =>
                "I'll help if I have enough food to share.",
            VillagerSocialIntent.OfferFood =>
                "Thank you. I'll remember that you shared with me.",
            _ => $"I'm holding up, {speaker.Name}. It's good not to be alone."
        };

    private bool TryBeginNpcAiSpeech(
        int villagerIndex,
        string message)
    {
        if (!_npcAiState.Ready ||
            _npcAiSpeechTask is { IsCompleted: false } ||
            _activePlayer is null ||
            _player is null ||
            (uint)villagerIndex >= (uint)_villagers.Count)
            return false;
        var listener = _villagers[villagerIndex];
        if (VillagerSimulation.TryExtractIntroducedName(
                message, out var claimedName))
            listener = VillagerSimulation.RecordIntroductionResponse(
                listener,
                _activePlayer.Id,
                claimedName,
                _worldGameSeconds);
        var perceivedPlayerName = VillagerSimulation.PerceivedName(
            listener, _activePlayer.Id, "Unknown survivor");
        listener = VillagerSimulation.RecordDialogueTurn(
            listener,
            _activePlayer.Id,
            perceivedPlayerName,
            message,
            _worldGameSeconds);
        _villagers[villagerIndex] = listener;
        _villagersDirty = true;
        HoldVillagerConversation(
            villagerIndex,
            _player.Position,
            double.PositiveInfinity);
        TakeConversationFloor(
            listener.Id,
            double.PositiveInfinity);
        var nearby = new List<NpcAiActor>(
            _villagers.Count + 1)
        {
            new(
                _activePlayer.Id,
                perceivedPlayerName,
                0,
                _activePlayer.Hunger,
                RelationshipDescription(
                    listener, _activePlayer.Id))
        };
        foreach (var actor in _villagers)
        {
            if (actor.Id == listener.Id ||
                actor.WorldLevel != listener.WorldLevel)
                continue;
            nearby.Add(new(
                actor.Id,
                VillagerSimulation.PerceivedName(
                    listener, actor.Id, "Unknown survivor"),
                Vector2.Distance(
                    new(listener.PositionX, listener.PositionY),
                    new(actor.PositionX, actor.PositionY)),
                actor.Hunger,
                RelationshipDescription(listener, actor.Id)));
        }
        var recalledMemories =
            VillagerSimulation.RecallMemories(
                listener,
                _activePlayer.Id,
                message,
                _worldGameSeconds);
        var listenerPosition = new Vector2(
            listener.PositionX, listener.PositionY);
        var nearbyWorld = _worldChunks.Values
            .Where(IsActiveWorldChunk)
            .SelectMany(gpu => gpu.Chunk.GroundObjects)
            .Select(item => new
            {
                Item = item,
                Distance = Vector2.Distance(
                    listenerPosition,
                    new(item.X, item.Y))
            })
            .Where(value => value.Distance <= 16)
            .OrderBy(value => value.Distance)
            .Take(16)
            .Select(value => new NpcAiWorldObservation(
                value.Item.Id.ToString("N"),
                value.Item.ItemId,
                StorageContainerService.IsStorage(
                    value.Item.ItemId)
                    ? "storage"
                    : "ground_item",
                value.Distance,
                value.Item.OwnerId ?? "",
                WorldLevelNavigation.IsWalkable(
                    _worldSeed,
                    (int)MathF.Floor(value.Item.X),
                    (int)MathF.Floor(value.Item.Y),
                    listener.WorldLevel)))
            .ToArray();
        var context = new NpcAiSpeechContext(
            _activePlayer.Id,
            perceivedPlayerName,
            listener.Id,
            listener.Name,
            message,
            nearby,
            listener.Goals?
                .Where(goal =>
                    goal.Status == CommitmentStatus.Active)
                .Select(goal => goal.Kind.ToString())
                .Take(4)
                .ToArray() ?? [],
            recalledMemories
                .Select(memory =>
                    memory.Summary ?? memory.Kind)
                .ToArray(),
            listener.Persona?.BackgroundStory ?? "",
            listener.Persona?.Personality ?? "",
            listener.Persona?.PriorTrade ?? "",
            listener.Persona?.KnownToolIds ?? [],
            listener.Persona?.ArrivalMemory ?? "",
            VillagerSimulation.HoursOnIsland(
                listener, _worldGameSeconds),
            listener.ConversationHistory,
            recalledMemories
                .Where(memory =>
                    !string.IsNullOrWhiteSpace(memory.Summary))
                .Select(memory => new NpcAiKnownFact(
                    memory.Summary!,
                    memory.SubjectId,
                    memory.Confidence,
                    memory.Sentiment,
                    memory.GameSeconds))
                .ToArray(),
            new(
                listener.Health,
                listener.Hunger,
                listener.Inventory
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray(),
                listener.Need.ToString(),
                listener.Activity.ToString(),
                listener.Goals?
                    .Where(goal =>
                        goal.Status == CommitmentStatus.Active)
                    .Select(goal =>
                        $"{goal.Kind}:{goal.ItemId ?? ""}:" +
                        $"{goal.Progress}/{goal.TargetQuantity}")
                    .ToArray() ?? [],
                listener.Promises?
                    .Where(promise =>
                        promise.Status == CommitmentStatus.Active)
                    .Select(promise =>
                        $"{promise.Kind}:{promise.ItemId ?? ""}:" +
                        $"{promise.Progress}/{promise.TargetQuantity}")
                    .ToArray() ?? [],
                listener.LastDeliberation?.PrivateThought ?? ""),
            nearbyWorld);
        _npcAiSpeechVillagerIndex = villagerIndex;
        _npcAiSpeechFallback =
            FallbackNpcReply(listener, message);
        _npcAiSpeechTask = _npcAi.InterpretAsync(
            ActiveNpcAiSettings(),
            context);
        _chatUi.AddMessage(
            $"{listener.Name} considers what you said...",
            ChatMessageStyle.Monologue);
        return true;
    }

    private void ApplyNpcAiInterpretation(
        int villagerIndex,
        NpcAiInterpretation interpretation,
        string speechFallback)
    {
        var villager = _villagers[villagerIndex];
        villager = villager with
        {
            LastDeliberation = new(
                interpretation.PrivateThought,
                interpretation.Decision,
                interpretation.Action,
                interpretation.Willingness,
                interpretation.EstimatedCost,
                interpretation.Risk,
                interpretation.Priority,
                _worldGameSeconds,
                interpretation.ItemId)
        };
        var permitsAction =
            interpretation.Decision is not
                ("refuse" or "clarify");
        if (permitsAction && _activePlayer is not null)
        {
            villager = interpretation.Action switch
            {
                "follow" or "come" => villager with
                {
                    FollowingActorId = _activePlayer.Id,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                "wait" or "stop_following" or "go_away" =>
                    villager with
                    {
                        FollowingActorId = null,
                        Action = EntityAction.Idle,
                        TargetX = null,
                        TargetY = null,
                        NextDecisionGameSeconds =
                            _worldGameSeconds +
                            VillagerSimulation.NearbyDecisionSeconds
                    },
                "explore" => villager with
                {
                    Need = VillagerNeed.Explore,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                "seek_food" => villager with
                {
                    Need = VillagerNeed.Food,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                "seek_shelter" => villager with
                {
                    Need = VillagerNeed.Safe,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                "rest" => villager with
                {
                    Need = VillagerNeed.Idle,
                    Action = EntityAction.Idle,
                    TargetX = null,
                    TargetY = null,
                    NextDecisionGameSeconds =
                        _worldGameSeconds +
                        VillagerSimulation.NearbyDecisionSeconds
                },
                "gather" or "gather_sticks" or "gather_berries" or
                "gather_fibre" or "fish" or "cook" or "withdraw" =>
                    villager with
                    {
                        Need = VillagerNeed.Food,
                        NextDecisionGameSeconds = _worldGameSeconds
                    },
                "cut_tree" or "craft" or "build" or "help_build" or
                "light_fire" or "mine" or "dig" or "enter_cave" or
                "board_boat" or "drop" or "give" => villager with
                {
                    Need = VillagerNeed.Safe,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                "flee" => villager with
                {
                    FollowingActorId = null,
                    Need = VillagerNeed.Safe,
                    NextDecisionGameSeconds = _worldGameSeconds
                },
                _ => villager
            };
        }
        if (_activePlayer is not null &&
            interpretation.Sentiment != 0)
        {
            var relationships =
                villager.Relationships?.ToList() ?? [];
            var relationshipIndex =
                relationships.FindIndex(value =>
                    value.CharacterId == _activePlayer.Id);
            var existing = relationshipIndex >= 0
                ? relationships[relationshipIndex]
                : new VillagerRelationship(
                    _activePlayer.Id, default);
            var amount = MathF.Abs(
                interpretation.Sentiment) / 20f;
            var state = interpretation.Sentiment > 0
                ? (existing.State with
                {
                    Trust = existing.State.Trust + amount,
                    Affection =
                        existing.State.Affection + amount * .5f
                }).Clamp()
                : (existing.State with
                {
                    Trust = existing.State.Trust - amount,
                    Resentment =
                        existing.State.Resentment + amount
                }).Clamp();
            var updated = existing with { State = state };
            if (relationshipIndex >= 0)
                relationships[relationshipIndex] = updated;
            else
                relationships.Add(updated);
            villager = villager with
            {
                Relationships = relationships
            };
        }
        if (!string.IsNullOrWhiteSpace(
                interpretation.Memory))
        {
            var memories = villager.Memories?.ToList() ?? [];
            memories.Add(new(
                Guid.NewGuid(),
                "conversation",
                interpretation.ReferencedActorId,
                null,
                1,
                _worldGameSeconds,
                interpretation.Sentiment,
                interpretation.Memory));
            if (memories.Count >
                VillagerSimulation.MaximumMemories)
                memories.RemoveRange(
                    0,
                    memories.Count -
                    VillagerSimulation.MaximumMemories);
            villager = villager with
            {
                Memories = memories
            };
        }
        if (permitsAction &&
            !interpretation.FreeformThought &&
            !string.IsNullOrWhiteSpace(
                interpretation.Goal))
        {
            var goals = villager.Goals?.ToList() ?? [];
            if (goals.Count >=
                VillagerCommitmentService.MaximumGoals)
                goals.RemoveAt(0);
            goals.Add(new(
                Guid.NewGuid(),
                VillagerGoalKind.HelpPerson,
                string.IsNullOrWhiteSpace(
                    interpretation.ItemId)
                    ? null
                    : interpretation.ItemId,
                Math.Max(1, interpretation.Quantity),
                0,
                _worldGameSeconds,
                PartnerId:
                    interpretation.ReferencedActorId));
            villager = villager with { Goals = goals };
        }
        if (permitsAction &&
            !interpretation.FreeformThought &&
            interpretation.Action == "help_build" &&
            _activePlayer is not null)
        {
            var acceptance =
                VillagerCommitmentService.TryAccept(
                    villager,
                    _activePlayer.Id,
                    VillagerPromiseKind.HelpBuild,
                    null,
                    1,
                    _worldGameSeconds);
            if (acceptance.Accepted &&
                acceptance.Promise is { } promise)
                villager =
                    VillagerCommitmentService.AddPromise(
                        villager, promise);
        }
        if (permitsAction &&
            !interpretation.FreeformThought &&
            interpretation.Action is
                "gather" or "give" &&
            ItemCatalog.TryGet(
                interpretation.ItemId, out _) &&
            _activePlayer is not null)
        {
            var kind = interpretation.Action == "give"
                ? VillagerPromiseKind.GiveItem
                : VillagerPromiseKind.GatherItem;
            var acceptance =
                VillagerCommitmentService.TryAccept(
                    villager,
                    _activePlayer.Id,
                    kind,
                    interpretation.ItemId,
                    Math.Max(1, interpretation.Quantity),
                    _worldGameSeconds);
            if (acceptance.Accepted &&
                acceptance.Promise is { } promise)
                villager =
                    VillagerCommitmentService.AddPromise(
                        villager, promise);
        }
        var reply = string.IsNullOrWhiteSpace(
                interpretation.Reply)
            ? speechFallback
            : interpretation.Reply;
        villager = VillagerSimulation.RecordDialogueTurn(
            villager,
            villager.Id,
            villager.Name,
            reply,
            _worldGameSeconds);
        _villagers[villagerIndex] = villager;
        _villagersDirty = true;
        ShowVillagerSpeech(
            villagerIndex,
            reply,
            _player?.Position ??
            new Vector2(
                villager.PositionX,
                villager.PositionY));
    }

    internal static string FallbackNpcReply(
        VillagerState listener,
        string message)
    {
        var text = message.Trim();
        var lower = text.ToLowerInvariant();
        var words = text.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (lower.Contains("your name") ||
            lower.Contains("who are you"))
            return $"My name is {listener.Name}.";
        if (lower is "hello" or "hi" or "hey" ||
            lower.StartsWith("hello ") ||
            lower.StartsWith("hey "))
            return $"Hello. I'm {listener.Name}.";
        if (lower.Contains("where are you"))
            return "I'm here beside you.";
        if (lower.Contains("what should we do") ||
            lower.Contains("what do we do") ||
            lower.Contains("what now"))
            return listener.Hunger <= 35
                ? "We should find food first, then stay together and look for shelter."
                : "We should stay together, check our supplies, and find a safe place for shelter.";
        if ((lower.Contains("let's") ||
             lower.Contains("lets") ||
             lower.Contains("we should")) &&
            lower.Contains("rock"))
            return "Good idea. Let's collect some rocks together, but leave enough time to find food and shelter.";
        if (lower.Contains("storm") ||
            lower.Contains("shipwreck") ||
            lower.Contains("wreck"))
            return listener.Persona?.ArrivalMemory is { Length: > 0 }
                ? $"That could be it. I remember {LowercaseFirst(
                    listener.Persona.ArrivalMemory)}"
                : "That could explain it. We should compare what each of us remembers.";
        if (lower.Contains("how we ended up") ||
            lower.Contains("how did we get") ||
            lower.Contains("what happened"))
            return listener.Persona?.ArrivalMemory is { Length: > 0 }
                ? $"I'm not certain. I remember {LowercaseFirst(
                    listener.Persona.ArrivalMemory)}"
                : "I'm not certain. We should ask the others what they remember.";
        if (lower.Contains("fuck") ||
            lower.Contains("bitch") ||
            lower.Contains("ugly") ||
            lower.Contains("rude") ||
            lower.Contains("idiot") ||
            lower.Contains("stupid") ||
            lower.Contains("hate you") ||
            lower.Contains("shut up") ||
            lower.Contains("go away"))
            return listener.Relationships?.Any(value =>
                       value.State.Resentment > 15) == true
                ? "Leave me alone."
                : "There's no need to speak to me like that.";
        if (LooksLikePersonalName(words))
            return $"Nice to meet you, {text}.";
        return text.EndsWith('?')
            ? "I'm not sure I understand the question. Can you say it another way?"
            : "I'm not sure what you mean by that.";
    }

    private static string LowercaseFirst(string value) =>
        value.Length == 0
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static bool LooksLikePersonalName(string[] words)
    {
        if (words.Length is < 1 or > 3)
            return false;
        var stopWords = new HashSet<string>(
            [
                "yes", "no", "okay", "ok", "thanks", "please",
                "food", "wood", "help", "come", "follow", "wait"
            ],
            StringComparer.OrdinalIgnoreCase);
        if (words.Any(stopWords.Contains))
            return false;
        if (words.Length > 1 &&
            words.Any(word =>
                word.Length == 0 ||
                !char.IsUpper(word[0])))
            return false;
        return words.All(word =>
            word.All(character =>
                char.IsLetter(character) ||
                character is '-' or '\''));
    }

    private static string RelationshipDescription(
        VillagerState observer,
        string subjectId)
    {
        var relationship =
            observer.Relationships?.FirstOrDefault(value =>
                value.CharacterId == subjectId)?.State ??
            default;
        if (relationship.Trust < -20) return "distrusts";
        if (relationship.Trust > 20) return "trusts";
        return "neutral";
    }
}
