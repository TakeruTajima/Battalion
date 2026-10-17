namespace Battalion.Sim
{
    public enum OrderType { Hold, Advance, Retreat, Attack, Pin, Entrench }

    /// <summary>攻撃の方向（目標の敵から見た向き）。None は「指定なし」（正面。ただし機敏は側面を選ぶことがある）。</summary>
    public enum AttackAspect { None, Front, Flank, Rear }

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
        /// 攻撃で側面を指定したときに、側を固定する（+1 は目標の左、−1 は右、0 は経路コストの小さい側を選ぶ）。
        /// 予備隊の台本が「R3 の側の側面」を指定するのに使う。
        /// </summary>
        public int FlankSide { get; }

        Order(OrderType type, int companyId, Vec2? destination, double? facingRad,
            int targetId = -1, AttackAspect aspect = AttackAspect.None, int flankSide = 0)
        {
            Type = type;
            CompanyId = companyId;
            Destination = destination;
            FacingRad = facingRad;
            TargetId = targetId;
            Aspect = aspect;
            FlankSide = flankSide;
        }

        public static Order Hold(int companyId, double? facingRad = null) =>
            new Order(OrderType.Hold, companyId, null, facingRad);

        public static Order Advance(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Advance, companyId, destination, facingRad);

        public static Order Retreat(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Retreat, companyId, destination, facingRad);

        public static Order Attack(int companyId, int targetId, AttackAspect aspect = AttackAspect.None, int flankSide = 0) =>
            new Order(OrderType.Attack, companyId, null, null, targetId, aspect, flankSide);

        public static Order Pin(int companyId, int targetId) =>
            new Order(OrderType.Pin, companyId, null, null, targetId);

        /// <summary>
        /// 陣地構築。目的地があれば前進と同じく移動し、着いて向きを変え終えたら掘り始める（移動中は進捗が増えない）。
        /// 目的地がなければその場で掘る。
        /// </summary>
        public static Order Entrench(int companyId, Vec2? destination = null, double? facingRad = null) =>
            new Order(OrderType.Entrench, companyId, destination, facingRad);

        /// <summary>移動を伴う命令か（前進・後退・攻撃、目的地のある陣地構築）。</summary>
        public bool Moves => Type == OrderType.Advance || Type == OrderType.Retreat || Type == OrderType.Attack
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
