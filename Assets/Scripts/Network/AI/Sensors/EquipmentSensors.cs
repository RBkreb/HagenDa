using CrashKonijn.Agent.Core;
using CrashKonijn.Goap.Core;
using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Equipment sensors: one MultiSensor providing all Has* world keys
    // (whether a loadout slot carries & has ammo for a given type).
    // ============================================================

    public class EquipmentSensors : MultiSensorBase
    {
        public EquipmentSensors()
        {
            this.AddLocalWorldSensor<HasGrenade>((agent, references) =>
                Has(references, EquipmentType.Grenade));
            this.AddLocalWorldSensor<HasSmokeGrenade>((agent, references) =>
                Has(references, EquipmentType.SmokeGrenade));
            this.AddLocalWorldSensor<HasEmpGrenade>((agent, references) =>
                Has(references, EquipmentType.EmpGrenade));
            this.AddLocalWorldSensor<HasGrenadeLauncher>((agent, references) =>
                Has(references, EquipmentType.GrenadeLauncher));
            this.AddLocalWorldSensor<HasSmokeLauncher>((agent, references) =>
                Has(references, EquipmentType.SmokeLauncher));
            this.AddLocalWorldSensor<HasRpg>((agent, references) =>
                Has(references, EquipmentType.Rpg));
            this.AddLocalWorldSensor<HasSupplyPack>((agent, references) =>
                Has(references, EquipmentType.SmallSupplyPack));
            this.AddLocalWorldSensor<HasSupplyCrate>((agent, references) =>
                Has(references, EquipmentType.LargeSupplyCrate));
            this.AddLocalWorldSensor<HasInterceptor>((agent, references) =>
                Has(references, EquipmentType.Interceptor));
            this.AddLocalWorldSensor<HasSensorEquip>((agent, references) =>
                Has(references, EquipmentType.Sensor));
            this.AddLocalWorldSensor<HasQuickDash>((agent, references) =>
                Has(references, EquipmentType.QuickDash));
            this.AddLocalWorldSensor<HasJammer>((agent, references) =>
                Has(references, EquipmentType.Jammer));
            this.AddLocalWorldSensor<HasArmorPlate>((agent, references) =>
                Has(references, EquipmentType.ArmorPlate));
            this.AddLocalWorldSensor<HasHealingSyringe>((agent, references) =>
                Has(references, EquipmentType.HealingSyringe));
            this.AddLocalWorldSensor<HasDefibrillator>((agent, references) =>
                Has(references, EquipmentType.Defibrillator));
        }

        private static SenseValue Has(IComponentReference references, EquipmentType type)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.HasEquipmentAmmo(type);
        }

        public override void Created() { }
        public override void Update() { }
    }
}
