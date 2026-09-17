using System.Collections.Generic;

namespace HagenDa.Networking
{
    /// <summary>
    /// Pure loadout validation rules (PHASE8), extracted so they can be unit-tested.
    /// <see cref="NetworkEquipment.ValidateLoadout"/> delegates here with its inventory.
    /// </summary>
    public static class LoadoutRules
    {
        /// <summary>
        /// Validate a loadout against the inventory:
        ///   - optional1 / optional2 must be valid Optional items and differ,
        ///   - special must be a Special item,
        ///   - throwable must be a Throwable item.
        /// </summary>
        public static bool Validate(LoadoutDefinition loadout, IList<EquipmentDefinition> inventory, out string reason)
        {
            reason = "";
            if (loadout == null) { reason = "配装为空"; return false; }
            if (inventory == null) { reason = "库存为空"; return false; }

            if (!IsCategory(inventory, loadout.optional1, EquipmentCategory.Optional)) { reason = "可选配备1 无效"; return false; }
            if (!IsCategory(inventory, loadout.optional2, EquipmentCategory.Optional)) { reason = "可选配备2 无效"; return false; }
            if (loadout.optional1 == loadout.optional2) { reason = "可选配备1 与可选配备2 不能相同"; return false; }
            if (!IsCategory(inventory, loadout.special, EquipmentCategory.Special)) { reason = "特有配备 无效"; return false; }
            if (!IsCategory(inventory, loadout.throwable, EquipmentCategory.Throwable)) { reason = "通用投掷物 无效"; return false; }

            return true;
        }

        public static bool IsCategory(IList<EquipmentDefinition> inventory, int idx, EquipmentCategory cat)
        {
            if (idx < 0 || idx >= inventory.Count) return false;
            var def = inventory[idx];
            return def != null && def.category == cat;
        }
    }
}
