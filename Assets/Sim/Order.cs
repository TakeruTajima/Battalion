namespace Battalion.Sim
{
    public enum OrderType { Hold, Advance, Retreat }

    /// <summary>
    /// 命令オブジェクト。プレイヤーも敵AIも同じ形でシミュレーションに渡す。
    /// P0 では <see cref="Simulation.Issue"/> で渡した命令が次の刻みの最初に即時適用される（伝令の遅延は P2）。
    /// </summary>
    public sealed class Order
    {
        public OrderType Type { get; }
        public int CompanyId { get; }
        /// <summary>前進・後退の目的地。保持では null（現在地）。</summary>
        public Vec2? Destination { get; }
        /// <summary>到着後（保持ならその場）で向く角度（ラジアン）。null なら向きを変えない。</summary>
        public double? FacingRad { get; }

        Order(OrderType type, int companyId, Vec2? destination, double? facingRad)
        {
            Type = type;
            CompanyId = companyId;
            Destination = destination;
            FacingRad = facingRad;
        }

        public static Order Hold(int companyId, double? facingRad = null) =>
            new Order(OrderType.Hold, companyId, null, facingRad);

        public static Order Advance(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Advance, companyId, destination, facingRad);

        public static Order Retreat(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Retreat, companyId, destination, facingRad);

        public static string Name(OrderType t)
        {
            switch (t)
            {
                case OrderType.Hold: return "hold";
                case OrderType.Advance: return "advance";
                default: return "retreat";
            }
        }
    }
}
