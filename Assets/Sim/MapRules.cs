namespace Battalion.Sim
{
    /// <summary>
    /// P1 の地図まわりの値（仕様「P1 詳細仕様」）。仕様の方針では数値は JSON に外出しするが、
    /// sim_params.json は docs/reference/sim_params.initial.json とバイト単位で同一に保つ決まりで、
    /// その初期値は仕様側が持つため、ここに置いている（仕様側でキーが決まったら移す）。
    /// </summary>
    public static class MapRules
    {
        /// <summary>露出の地図（と、退路の見積もりに使う敵ごとの見える範囲）を作り直す間隔。</summary>
        public const double ExposureRebuildSec = 10.0;
        /// <summary>経路（攻撃の発起点を含む）を引き直す間隔。</summary>
        public const double ReplanSec = 30.0;
        /// <summary>露出1マスあたりのコスト（m 換算）。脅威の重みに掛ける。</summary>
        public const double ExposureCostM = 50.0;

        /// <summary>攻撃の発起点：敵の中心から、指定の方向へこの距離。</summary>
        public const double AttackStandoffM = 250.0;
        /// <summary>機敏は、側面の経路コストが正面のこの倍以下なら側面を選ぶ。</summary>
        public const double AgileFlankCostRatio = 1.5;
        /// <summary>発起点に着いた後、敵が動いて発起点がこれ以上離れたら、また動く。</summary>
        public const double AttackRepositionM = 50.0;

        /// <summary>目的地が近すぎるときに、ずらす先を探す輪の刻みと最大半径。</summary>
        public const double DestinationSearchStepM = 50.0;
        public const double DestinationSearchMaxM = 1000.0;
        public const int DestinationSearchDirs = 16;

        /// <summary>陣地構築：完成までの時間、完成時の被弾倍率、塹壕にする左右のマス数。</summary>
        public const double EntrenchSec = 600.0;
        public const double EntrenchDamageMult = 0.3;
        public const int EntrenchHalfWidthCells = 2;

        /// <summary>退路の見積もりで、川に突き当たる方位の損害割合（逃げられない）。</summary>
        public const double BlockedEscapeLoss = 1.0;

        public static double ThreatWeight(Personality p)
        {
            switch (p)
            {
                case Personality.Cautious: return 3.0;
                case Personality.Reckless: return 0.3;
                default: return 1.0; // 冷静、機敏
            }
        }
    }
}
