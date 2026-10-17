namespace Battalion.Sim
{
    public enum OrderType { Hold, Advance, Retreat, Attack, Pin, Entrench }

    /// <summary>攻撃の方向（目標の敵から見た向き）。None は「指定なし」（正面。ただし機敏は側面を選ぶことがある）。</summary>
    public enum AttackAspect { None, Front, Flank, Rear }

    /// <summary>攻撃の目標が崩れたら（P1d）：確保（その場で固める）／追撃可（性格で追うかを決める）。</summary>
    public enum AttackOnBreak { Secure, Pursue }

    /// <summary>
    /// 命令オブジェクト。プレイヤーも敵AIも同じ形でシミュレーションに渡す。
    /// <see cref="Simulation.Issue"/> で渡した命令は次の刻みの最初に即時適用される（伝令の遅延は P2）。
    /// </summary>
    public sealed class Order
    {
        public OrderType Type { get; }
        public int CompanyId { get; }
        /// <summary>前進・後退の目的地。それ以外では null（現在地）。</summary>
        public Vec2? Destination { get; }
        /// <summary>到着後（保持ならその場）で向く角度（ラジアン）。null なら向きを変えない。</summary>
        public double? FacingRad { get; }
        /// <summary>攻撃・拘束の目標の敵中隊の ID。それ以外では −1。</summary>
        public int TargetId { get; }
        public AttackAspect Aspect { get; }
        /// <summary>
        /// 攻撃で側面のときの最初の側（+1 は目標の左、−1 は右、0 は経路コストの小さい側を選ぶ）。
        /// </summary>
        public int FlankSide { get; }
        /// <summary>
        /// 側をプレイヤーが指定したか（駒の端をクリックした）。指定した側は守る。指定していなければ、発起点に着くまで
        /// 経路を引き直すたびに安い側を選び直す（予備隊の台本、機敏、中央のクリック）。
        /// </summary>
        public bool FlankSideLocked { get; }
        /// <summary>攻撃の目標が崩れたら：確保／追撃可。</summary>
        public AttackOnBreak OnBreak { get; }

        Order(OrderType type, int companyId, Vec2? destination, double? facingRad,
            int targetId = -1, AttackAspect aspect = AttackAspect.None, int flankSide = 0,
            bool flankSideLocked = false, AttackOnBreak onBreak = AttackOnBreak.Secure)
        {
            Type = type;
            CompanyId = companyId;
            Destination = destination;
            FacingRad = facingRad;
            TargetId = targetId;
            Aspect = aspect;
            FlankSide = flankSide;
            FlankSideLocked = flankSideLocked;
            OnBreak = onBreak;
        }

        public static Order Hold(int companyId, double? facingRad = null) =>
            new Order(OrderType.Hold, companyId, null, facingRad);

        public static Order Advance(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Advance, companyId, destination, facingRad);

        public static Order Retreat(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Retreat, companyId, destination, facingRad);

        /// <summary>
        /// 攻撃。<paramref name="lockSide"/> を省くと、側を指定したとき（<paramref name="flankSide"/> が 0 でない）は固定する。
        /// 予備隊の台本は、最初の側を決めるが固定しない（false）。
        /// </summary>
        public static Order Attack(int companyId, int targetId, AttackAspect aspect = AttackAspect.None, int flankSide = 0,
            bool? lockSide = null, AttackOnBreak onBreak = AttackOnBreak.Secure) =>
            new Order(OrderType.Attack, companyId, null, null, targetId, aspect, flankSide,
                aspect == AttackAspect.Flank && flankSide != 0 && (lockSide ?? true), onBreak);

        public static Order Pin(int companyId, int targetId) =>
            new Order(OrderType.Pin, companyId, null, null, targetId);

        /// <summary>
        /// 陣地構築。目的地があれば前進と同じく移動し、着いて向きを変え終えたら掘り始める（移動中は進捗が増えない）。
        /// 目的地がなければその場で掘る。
        /// </summary>
        public static Order Entrench(int companyId, Vec2? destination = null, double? facingRad = null) =>
            new Order(OrderType.Entrench, companyId, destination, facingRad);

        /// <summary>移動を伴う命令か（前進・後退・攻撃・拘束（詰める）、目的地のある陣地構築）。</summary>
        public bool Moves => Type == OrderType.Advance || Type == OrderType.Retreat || Type == OrderType.Attack || Type == OrderType.Pin
                             || (Type == OrderType.Entrench && Destination.HasValue);

        public static string Name(OrderType t)
        {
            switch (t)
            {
                case OrderType.Hold: return "hold";
                case OrderType.Advance: return "advance";
                case OrderType.Retreat: return "retreat";
                case OrderType.Attack: return "attack";
                case OrderType.Pin: return "pin";
                default: return "entrench";
            }
        }
    }
}
