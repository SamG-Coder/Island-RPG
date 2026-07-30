namespace IslandRpg.Rendering.Ui;

internal enum ModalScreenKind
{
    None,
    Pause,
    Death,
    Crafting,
    SkillGuide,
    ItemContainer,
    QuestJournal,
    QuestComplete
}

internal sealed class ModalScreenState
{
    public ModalScreenKind Active { get; private set; }
    public bool IsOpen => Active != ModalScreenKind.None;
    public bool PausesSimulation =>
        Active is ModalScreenKind.Pause or ModalScreenKind.Death or
            ModalScreenKind.QuestComplete;
    public bool HidesGameUi => IsOpen;
    public bool CapturesAllInput => IsOpen;
    public bool BlursBackground => IsOpen;

    public void Open(ModalScreenKind kind)
    {
        if (kind == ModalScreenKind.None)
            throw new ArgumentOutOfRangeException(nameof(kind));
        Active = kind;
    }

    public void Close(ModalScreenKind kind)
    {
        if (Active == kind) Active = ModalScreenKind.None;
    }
}
