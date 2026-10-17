using Battalion.Sim;

namespace Battalion.View
{
    /// <summary>村の目標の状態。</summary>
    public enum VillageState
    {
        /// <summary>空き：村に青がおらず、250m 以内に赤もいない。</summary>
        Empty,
        /// <summary>青が確保：村のマスに指揮下の青がいて、250m 以内に指揮下の赤がいない（今終われば勝ち）。</summary>
        Blue,
        /// <summary>赤が確保：250m 以内に指揮下の赤がいて、村に青がいない。</summary>
        Red,
        /// <summary>争奪中：両方いる。</summary>
        Contested,
    }

    /// <summary>
    /// 勝利条件の見える化のための判定（<see cref="MapScenario.BlueWins"/> と同じ条件を分けて返す）。UnityEngine に依存しない。
    /// </summary>
    public readonly struct ObjectiveStatus
    {
        public readonly bool BlueInVillage;
        public readonly bool RedNearVillage;
        /// <summary>今この瞬間に会戦が終わったら青の勝ちか（赤が全て盤上から消えた場合を含む）。</summary>
        public readonly bool WinIfEndedNow;

        public ObjectiveStatus(bool blueIn, bool redNear, bool winNow)
        {
            BlueInVillage = blueIn;
            RedNearVillage = redNear;
            WinIfEndedNow = winNow;
        }

        public VillageState State =>
            BlueInVillage ? (RedNearVillage ? VillageState.Contested : VillageState.Blue)
                          : (RedNearVillage ? VillageState.Red : VillageState.Empty);

        public static ObjectiveStatus Compute(Simulation sim, MapScenario sc)
        {
            bool blueIn = false, redNear = false;
            foreach (var c in sim.Companies)
            {
                if (!c.UnderCommand) continue;
                if (c.Side == Side.Blue)
                {
                    sim.Map.CellOf(c.Position, out int i, out int j);
                    if (sc.VillageCells.Contains((i, j))) blueIn = true;
                }
                else if (Vec2.Distance(c.Position, MapScenario.VillageCenter) <= MapScenario.VillageHoldRadiusM)
                {
                    redNear = true;
                }
            }
            return new ObjectiveStatus(blueIn, redNear, sc.BlueWins(sim));
        }

        public static string Name(VillageState s)
        {
            switch (s)
            {
                case VillageState.Blue: return "青が確保";
                case VillageState.Red: return "赤が確保";
                case VillageState.Contested: return "争奪中";
                default: return "空き";
            }
        }
    }
}
