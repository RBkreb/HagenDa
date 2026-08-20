namespace HagenDa.Networking
{
    /// <summary>
    /// Player loadout (PHASE8): the five selectable slots. Slot values are indices
    /// into the shared <see cref="NetworkEquipment.equipmentList"/> inventory, so the
    /// same struct serializes trivially in a [Command] (five ints — no ScriptableObject
    /// reference resolution across the wire).
    ///
    ///  - primary   = -1 (the single M4 primary weapon for now),
    ///  - optional1 / optional2 = Optional-category equipment (must differ, no empty),
    ///  - special   = Special-category equipment,
    ///  - throwable = Throwable-category equipment.
    /// </summary>
    [System.Serializable]
    public class LoadoutDefinition
    {
        public const int PrimaryIndex = -1;   // 主武器槽固定为 -1（当前仅 M4）

        public int optional1 = -1;
        public int optional2 = -1;
        public int special = -1;
        public int throwable = -1;

        public LoadoutDefinition() { }

        public LoadoutDefinition(LoadoutDefinition other)
        {
            if (other == null) return;
            optional1 = other.optional1;
            optional2 = other.optional2;
            special = other.special;
            throwable = other.throwable;
        }
    }
}
