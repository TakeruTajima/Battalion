namespace Battalion.Sim
{
    public enum OrderType { Hold, Advance, Retreat, Attack, Pin, Entrench }

    /// <summary>
    /// 攻撃の方向の範囲（目標の向きを基準に、正面は ±60°、側面は 60〜120°、背面は 120〜180°）。
    /// None は「指定なし」（今いる方角から攻める。機敏はより良い向きを選ぶことがある）。P1e の4。
    /// </summary>
    public enum AttackAspect { None, Front, Flank, Rear }

    /// <summary>拘束の距離（P1e の3）：遠い（350m。足止めの効果はない）／近い（125m）。</summary>
    public enum PinRange { Far, Near }

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
        /// <summary>攻撃の範囲での指定（<see cref="BearingRad"/> があればそちらが優先）。</summary>
        public AttackAspect Aspect { get; }
        /// <summary>
        /// 攻撃の地図の方角での指定：目標の中心から見た発起点の方角（ラジアン、東から反時計回り）。
        /// 指定した方角は、発起点に着くまで守る。null なら範囲（<see cref="Aspect"/>）の規則で選ぶ。
        /// </summary>
        public double? BearingRad { get; }
        /// <summary>攻撃の目標が崩れたら：確保／追撃可。</summary>
        public AttackOnBreak OnBreak { get; }
        /// <summary>拘束の距離。</summary>
        public PinRange PinRange { get; }
        /// <summary>
        /// 報告の駒への攻撃・拘束（P2d）：駒の ID と、命令を出した時の駒の位置。目標の中隊（<see cref="TargetId"/>）はまだ決まっていない（−1）。
        /// 受けた中隊が、駒の位置の近くの気付いている敵を目標にする。駒への命令でなければ −1 と null。
        /// </summary>
        public int PieceId { get; }
        public Vec2? PiecePosition { get; }

        Order(OrderType type, int companyId, Vec2? destination, double? facingRad,
            int targetId = -1, AttackAspect aspect = AttackAspect.None, double? bearingRad = null,
            AttackOnBreak onBreak = AttackOnBreak.Secure, PinRange pinRange = PinRange.Far, int pieceId = -1, Vec2? piecePosition = null)
        {
            PieceId = pieceId;
            PiecePosition = piecePosition;
            Type = type;
            CompanyId = companyId;
            Destination = destination;
            FacingRad = facingRad;
            TargetId = targetId;
            Aspect = aspect;
            BearingRad = bearingRad;
            OnBreak = onBreak;
            PinRange = pinRange;
        }

        public static Order Hold(int companyId, double? facingRad = null) =>
            new Order(OrderType.Hold, companyId, null, facingRad);

        public static Order Advance(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Advance, companyId, destination, facingRad);

        public static Order Retreat(int companyId, Vec2 destination, double? facingRad = null) =>
            new Order(OrderType.Retreat, companyId, destination, facingRad);

        /// <summary>攻撃（範囲での指定。<paramref name="aspect"/> が None なら指定なし）。</summary>
        public static Order Attack(int companyId, int targetId, AttackAspect aspect = AttackAspect.None,
            AttackOnBreak onBreak = AttackOnBreak.Secure) =>
            new Order(OrderType.Attack, companyId, null, null, targetId, aspect, null, onBreak);

        /// <summary>攻撃（地図の方角での指定。目標の中心から <paramref name="bearingRad"/> の方角の 250m の点が発起点）。</summary>
        public static Order AttackFrom(int companyId, int targetId, double bearingRad,
            AttackOnBreak onBreak = AttackOnBreak.Secure) =>
            new Order(OrderType.Attack, companyId, null, null, targetId, AttackAspect.None, bearingRad, onBreak);

        /// <summary>報告の駒への攻撃（範囲での指定。P2d）。</summary>
        public static Order AttackPiece(int companyId, int pieceId, Vec2 piecePosition, AttackAspect aspect = AttackAspect.None,
            AttackOnBreak onBreak = AttackOnBreak.Secure) =>
            new Order(OrderType.Attack, companyId, null, null, -1, aspect, null, onBreak, pieceId: pieceId, piecePosition: piecePosition);

        /// <summary>報告の駒への攻撃（地図の方角での指定。P2d）。</summary>
        public static Order AttackPieceFrom(int companyId, int pieceId, Vec2 piecePosition, double bearingRad,
            AttackOnBreak onBreak = AttackOnBreak.Secure) =>
            new Order(OrderType.Attack, companyId, null, null, -1, AttackAspect.None, bearingRad, onBreak, pieceId: pieceId, piecePosition: piecePosition);

        /// <summary>報告の駒への拘束（P2d）。</summary>
        public static Order PinPiece(int companyId, int pieceId, Vec2 piecePosition, PinRange range = PinRange.Far) =>
            new Order(OrderType.Pin, companyId, null, null, -1, pinRange: range, pieceId: pieceId, piecePosition: piecePosition);

        /// <summary>報告の駒への命令か（目標の中隊がまだ決まっていない）。</summary>
        public bool ToPiece => PiecePosition.HasValue && TargetId < 0;

        /// <summary>報告の駒への命令の目標を決めた命令（種類・範囲・方角・崩れたら・拘束の距離はそのまま）。</summary>
        public Order WithTarget(int targetId) =>
            new Order(Type, CompanyId, Destination, FacingRad, targetId, Aspect, BearingRad, OnBreak, PinRange, PieceId, PiecePosition);

        /// <summary>拘束（初期は遠い）。</summary>
        public static Order Pin(int companyId, int targetId, PinRange range = PinRange.Far) =>
            new Order(OrderType.Pin, companyId, null, null, targetId, pinRange: range);

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
