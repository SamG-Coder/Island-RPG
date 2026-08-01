using IslandRpg.Gameplay;
using IslandRpg.Rendering.Ui;
using FontStashSharp;
using OpenTK.Mathematics;

namespace IslandRpg.Rendering;

internal sealed partial class GameHostWindow
{
    private bool IsObserveWorld =>
        _observeMode is not null || _activeWorld?.ObserveWorld == true;
    private readonly ToggleControlState _newWorldObserveToggle = new(
        "Observe world", "Free camera; no player participation");
    private readonly TextBoxControlState _newWorldSharedStoryTextBox =
        new() { MaximumLength = 320 };
    private readonly TextBoxControlState _newWorldAiModelOverrideTextBox =
        new() { MaximumLength = 100 };
    private readonly TextBoxControlState[] _newWorldNpcNameTextBoxes =
        CreateAdvancedFields(48);
    private readonly TextBoxControlState[] _newWorldNpcPersonalityTextBoxes =
        CreateAdvancedFields(180);
    private readonly TextBoxControlState[] _newWorldNpcTradeTextBoxes =
        CreateAdvancedFields(100);
    private readonly TextBoxControlState[] _newWorldNpcBackstoryTextBoxes =
        CreateAdvancedFields(320);
    private readonly TextBoxControlState[] _newWorldNpcItemsTextBoxes =
        CreateAdvancedFields(240);
    private int _newWorldAdvancedNpcIndex;

    private static TextBoxControlState[] CreateAdvancedFields(int maximumLength) =>
        Enumerable.Range(0, VillagerSimulation.InitialPopulation)
            .Select(_ => new TextBoxControlState { MaximumLength = maximumLength })
            .ToArray();

    private void OpenNewWorldAdvanced()
    {
        var names = VillagerSimulation.NamesForPopulation(
            Math.Max(1, _newWorldAiNpcCount));
        for (var index = 0; index < names.Count; index++)
            if (string.IsNullOrWhiteSpace(_newWorldNpcNameTextBoxes[index].Text))
                _newWorldNpcNameTextBoxes[index].SetText(names[index]);
        _newWorldAdvancedNpcIndex = Math.Clamp(
            _newWorldAdvancedNpcIndex, 0, Math.Max(0, _newWorldAiNpcCount - 1));
        _frontendPage = FrontendPage.NewWorldAdvanced;
        BlurTextBoxes();
    }

    private void UpdateNewWorldAdvancedClick(Vector2 pointer)
    {
        var population = Math.Max(1, _newWorldAiNpcCount);
        for (var index = 0; index < population; index++)
            if (AdvancedSurvivorTabBounds(index, population).Contains(pointer))
            {
                _newWorldAdvancedNpcIndex = index;
                BlurTextBoxes();
                return;
            }

        var fields = AdvancedFields();
        for (var index = 0; index < fields.Length; index++)
            if (AdvancedFieldBounds(index).Contains(pointer))
            {
                FocusTextBox(fields[index], AdvancedFieldBounds(index), pointer);
                return;
            }
        if (_newWorldObserveToggle.Bounds.Contains(pointer))
        {
            _newWorldObserveToggle.ToggleAt(pointer);
            return;
        }
        if (AdvancedDoneButtonBounds().Contains(pointer) ||
            BackButtonBounds().Contains(pointer))
        {
            _frontendPage = FrontendPage.NewWorld;
            BlurTextBoxes();
            return;
        }
        BlurTextBoxes();
    }

    private void RenderNewWorldAdvancedMenu()
    {
        var panel = FrontendPanel(760, 640);
        DrawAoEPanelBorder(panel);
        DrawCenteredUiText("ADVANCED AI SETUP",
            new(panel.X, panel.Y + 18, panel.Z, 36),
            new FSColor(232, 217, 166, 255));
        DrawCenteredUiText(
            "Shape the survivors, their supplies, and the history they share",
            new(panel.X + 35, panel.Y + 52, panel.Z - 70, 22),
            new FSColor(169, 159, 130, 255));

        var population = Math.Max(1, _newWorldAiNpcCount);
        for (var index = 0; index < population; index++)
        {
            var bounds = AdvancedSurvivorTabBounds(index, population);
            DrawMenuButton(bounds, _newWorldNpcNameTextBoxes[index].Text.Trim() is { Length: > 0 } name
                ? name : $"Survivor {index + 1}");
            if (index == _newWorldAdvancedNpcIndex)
                DrawPanelOutline(bounds, 3, new(.72f, .53f, .19f, 1));
        }

        var content = AdvancedContentBounds();
        DrawAoEPanelBorder(content);
        var fields = AdvancedFields();
        string[] labels = ["Name", "Personality override", "Prior trade override",
            "Backstory override", "Starting items (comma separated)",
            "Shared story / what happened", "AI model override"];
        for (var index = 0; index < fields.Length; index++)
        {
            var bounds = AdvancedFieldBounds(index);
            fields[index].Bounds = bounds;
            DrawUiText(labels[index], new(bounds.X, bounds.Y - 18),
                new FSColor(204, 190, 150, 255));
            DrawTextField(fields[index]);
        }
        DrawCenteredUiText(
            "Blank fields inherit generated values and the global AI model. Items use catalog names.",
            new(content.X + 16, content.Y + content.W - 68, content.Z - 32, 18),
            new FSColor(145, 138, 117, 255));
        _newWorldObserveToggle.Layout(
            new(content.X + 16, content.Y + content.W - 48, 300, 42), 0);
        _newWorldObserveToggle.Hovered =
            _newWorldObserveToggle.HitTest(MouseState.Position);
        DrawToggleControl(_newWorldObserveToggle);

        DrawMenuButton(BackButtonBounds(), "Back");
        DrawMenuButton(AdvancedDoneButtonBounds(), "Done");
    }

    private TextBoxControlState[] AdvancedFields()
    {
        var index = Math.Clamp(_newWorldAdvancedNpcIndex, 0,
            VillagerSimulation.InitialPopulation - 1);
        return [_newWorldNpcNameTextBoxes[index],
            _newWorldNpcPersonalityTextBoxes[index],
            _newWorldNpcTradeTextBoxes[index],
            _newWorldNpcBackstoryTextBoxes[index],
            _newWorldNpcItemsTextBoxes[index],
            _newWorldSharedStoryTextBox,
            _newWorldAiModelOverrideTextBox];
    }

    private TextBoxControlState? FocusedAdvancedTextBox() =>
        AdvancedFields().FirstOrDefault(value => value.Focused);

    private void BlurAdvancedTextBoxes()
    {
        _newWorldSharedStoryTextBox.Blur();
        _newWorldAiModelOverrideTextBox.Blur();
        foreach (var value in _newWorldNpcNameTextBoxes
                     .Concat(_newWorldNpcPersonalityTextBoxes)
                     .Concat(_newWorldNpcTradeTextBoxes)
                     .Concat(_newWorldNpcBackstoryTextBoxes)
                     .Concat(_newWorldNpcItemsTextBoxes))
            value.Blur();
    }

    private NewWorldSurvivorSetup[] BuildNewWorldSetups(
        IReadOnlyList<VillagerPersona> personas) =>
        NewWorldSurvivorSetupService.Build(_newWorldAiNpcCount, personas,
            _newWorldNpcNameTextBoxes.Select(x => x.Text).ToArray(),
            _newWorldNpcPersonalityTextBoxes.Select(x => x.Text).ToArray(),
            _newWorldNpcTradeTextBoxes.Select(x => x.Text).ToArray(),
            _newWorldNpcBackstoryTextBoxes.Select(x => x.Text).ToArray(),
            _newWorldNpcItemsTextBoxes.Select(x => x.Text).ToArray(),
            _newWorldSharedStoryTextBox.Text);

    private string? ValidateNewWorldAdvancedItems()
    {
        for (var index = 0; index < _newWorldAiNpcCount; index++)
        {
            var unknown = NewWorldSurvivorSetupService.UnknownItems(
                _newWorldNpcItemsTextBoxes[index].Text);
            if (unknown.Length > 0)
                return $"Unknown starting item for survivor {index + 1}: {unknown[0]}";
            var history = new[]
            {
                _newWorldNpcPersonalityTextBoxes[index].Text,
                _newWorldNpcTradeTextBoxes[index].Text,
                _newWorldNpcBackstoryTextBoxes[index].Text
            };
            if (history.Any(value => !string.IsNullOrWhiteSpace(value) &&
                                     !HistoricalKnowledgePolicy.IsPlausible(value)))
                return $"Survivor {index + 1} has details that do not fit the 1200 AD setting.";
        }
        if (!string.IsNullOrWhiteSpace(_newWorldSharedStoryTextBox.Text) &&
            !HistoricalKnowledgePolicy.IsPlausible(_newWorldSharedStoryTextBox.Text))
            return "The shared story has details that do not fit the 1200 AD setting.";
        return null;
    }

    private Vector4 AdvancedContentBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 40, panel.Y + 130, panel.Z - 80, 402);
    }

    private Vector4 AdvancedSurvivorTabBounds(int index, int count)
    {
        var panel = FrontendPanel(760, 640);
        const float gap = 8;
        var width = (panel.Z - 80 - gap * (count - 1)) / count;
        return new(panel.X + 40 + index * (width + gap), panel.Y + 84, width, 36);
    }

    private Vector4 AdvancedFieldBounds(int index)
    {
        var content = AdvancedContentBounds();
        var row = index < 5 ? index : 0;
        var rightColumn = index >= 5;
        var x = rightColumn ? content.X + content.Z / 2 + 8 : content.X + 16;
        var width = content.Z / 2 - 32;
        var y = rightColumn
            ? content.Y + 34 + (index - 5) * 58
            : content.Y + 34 + row * 58;
        return new(x, y, width, 34);
    }

    private NpcAiSettings NewWorldNpcAiSettings()
    {
        var settings = _saves.LoadSettings().EffectiveAi;
        var model = _newWorldAiModelOverrideTextBox.Text.Trim();
        return model.Length == 0 ? settings : settings with { Model = model };
    }

    private NpcAiSettings ActiveNpcAiSettings()
    {
        var settings = _saves.LoadSettings().EffectiveAi;
        var model = _activeWorld?.AiModelOverride?.Trim() ?? "";
        return model.Length == 0 ? settings : settings with { Model = model };
    }

    private Vector4 AdvancedDoneButtonBounds()
    {
        var panel = FrontendPanel(760, 640);
        return new(panel.X + 340, panel.Y + panel.W - 92, 228, 48);
    }

    private Vector4 NewWorldAdvancedButtonBounds()
    {
        var details = NewWorldDetailsBounds();
        return new(details.X + 318, details.Y + 380, 74, 42);
    }
}
