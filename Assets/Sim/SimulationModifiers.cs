using System.Collections.Generic;

namespace Battalion.Sim
{
    /// <summary>今効いている倍率の種類（R2.1。表示の文字は View が決める）。</summary>
    public enum ModifierKind
    {
        // 被弾
        MovingOpen, Trench, TrenchOffArc, Entrench, EntrenchOffArc, Scrape, ScrapeOffArc, Terrain,
        // 撃つ力
        FireShaken, FireNonFrontTarget, FireDisarray, FireSuppressed, FireMoving, FireOccupation,
        // 移動
        MoveTerrain, MovePinned, MovePassThrough, MoveSideways, MoveBackward, MoveTurning,
    }

    /// <summary>今効いている倍率の1つ（1.0 のものは載せない）。</summary>
    public readonly struct ModifierFactor
    {
        public readonly ModifierKind Kind;
        public readonly double Mult;
        /// <summary>地形の倍率のときの地形の文字（それ以外は '\0'）。</summary>
        public readonly char Terrain;

        public ModifierFactor(ModifierKind kind, double mult, char terrain = '\0')
        {
            Kind = kind;
            Mult = mult;
            Terrain = terrain;
        }
    }

    // R2.1：今効いている倍率の読み取りの口（仕様「R2.1：伏せた味方の超越と、効いている倍率の表示（確定）」の2）。
    public sealed partial class Simulation
    {
        /// <summary>
        /// 射手の状態による撃つ力の倍率（損害の式の f_状態）。動揺、正面の弧の外の目標、隊形の乱れ、制圧下（R1）、動きながら撃つ（R1 の規則3）
        /// の順に掛ける。<paramref name="into"/> があれば、1.0 でない要素を積む（表示用）。損害の同時解決（Resolve）もこれを使う。
        /// </summary>
        public double FireStateMult(Company u, bool targetInFrontArc, double t, List<ModifierFactor> into = null)
        {
            double f = 1.0;
            void Mul(ModifierKind k, double m)
            {
                f *= m;
                if (into != null && m != 1.0) into.Add(new ModifierFactor(k, m));
            }
            if (u.State == CompanyState.Shaken) Mul(ModifierKind.FireShaken, _p.Fire.ShakenMult);
            if (!targetInFrontArc) Mul(ModifierKind.FireNonFrontTarget, _p.Fire.NonFrontTargetMult);
            // 隊形の乱れ（壊走中の味方にすり抜けられた。P1d の6）
            if (u.DisarrayUntil >= t) Mul(ModifierKind.FireDisarray, _p.Behavior.DisarrayFireMult);
            // 小銃の制圧下（R1。砲撃による制圧は P3 で fire.suppressed_mult）、動きながら撃つ（R1 の規則3）
            if (u.Suppressed) Mul(ModifierKind.FireSuppressed, _p.Suppression.FireMult);
            if (u.MovedThisTick) Mul(ModifierKind.FireMoving, _p.Fire.MovingFireMult);
            // 占領の乱れ（C12。隊形の乱れとは別に持ち、重なれば掛け合わせる）
            if (_p.Occupation.Enabled && t < u.OccupationDisarrayUntil - 1e-9) Mul(ModifierKind.FireOccupation, _p.Occupation.DisarrayFireMult);
            return f;
        }

        /// <summary>
        /// 今の撃つ力の倍率（表示用）：今狙っている目標（なければ正面の目標を撃つとみなす）に対する <see cref="FireStateMult"/>。
        /// </summary>
        public double CurrentFireMult(Company c, List<ModifierFactor> into = null)
        {
            bool front = true;
            if (c.TargetId >= 0) front = AspectOf(c, _companies[c.TargetId].Position) == Aspect.Front;
            return FireStateMult(c, front, Time, into);
        }

        /// <summary>
        /// 被弾倍率を要素に分ける（表示用）。要素を掛けた値は <see cref="TerrainDamageMult"/> と同じ（浮動小数点の誤差を除く）。
        /// 陣地構築の途中と浅い掩体は、進捗に応じた値を1つの要素にする。
        /// </summary>
        public double DamageFactors(Company c, Vec2? from, List<ModifierFactor> into)
        {
            char ch = _map.TerrainAt(c.Position);
            double off = _p.Terrain.FortificationOffArcMult;
            double f;
            if (ch == TerrainMap.Trench)
            {
                if (c.MovedThisTick) f = 1.0;
                else if (InFortificationArc(_map.FortificationFacingAt(c.Position), c, from))
                {
                    f = _map.DamageMult(TerrainMap.Trench);
                    into.Add(new ModifierFactor(ModifierKind.Trench, f));
                }
                else
                {
                    // 正面の弧の外：守りが効いていないことを示すため、1.0 でも載せる
                    f = off;
                    into.Add(new ModifierFactor(ModifierKind.TrenchOffArc, f));
                }
            }
            else
            {
                f = _map.DamageMult(ch);
                if (f != 1.0) into.Add(new ModifierFactor(ModifierKind.Terrain, f, ch));
            }
            if (!c.MovedThisTick && c.EntrenchProgress > 0 && ch != TerrainMap.Trench)
            {
                // 正面の弧の外で効いていないとき（1.0）も、陣地構築の途中であることを示すため載せる
                bool inArc = InFortificationArc(c.Facing, c, from);
                double target = inArc ? _p.MapRules.EntrenchDamageMult : off;
                double g = f > target ? f + (target - f) * System.Math.Min(1.0, c.EntrenchProgress / _p.MapRules.EntrenchSec) : f;
                into.Add(new ModifierFactor(inArc ? ModifierKind.Entrench : ModifierKind.EntrenchOffArc, g / f));
                f = g;
            }
            if (c.MovedThisTick && IsOpenGround(ch) && _p.Terrain.MovingOpenDamageMult != 1.0)
            {
                f *= _p.Terrain.MovingOpenDamageMult;
                into.Add(new ModifierFactor(ModifierKind.MovingOpen, _p.Terrain.MovingOpenDamageMult));
            }
            // 浅い掩体：掘ってあれば、正面の弧の外で効いていないとき（1.0）も載せる
            bool scrapeArc = InFortificationArc(c.ScrapeFacing, c, from);
            double s = ScrapeMult(c, ch, scrapeArc);
            if (ScrapeMult(c, ch, true) != 1.0)
                into.Add(new ModifierFactor(scrapeArc ? ModifierKind.Scrape : ModifierKind.ScrapeOffArc, s));
            f *= s;
            return f;
        }

        /// <summary>開けた所を移動中で、被弾 ×terrain.moving_open_damage_mult が掛かっているか（R2.1。駒の印）。</summary>
        public bool ExposedWhileMoving(Company c) =>
            c.OnBoard && c.MovedThisTick && IsOpenGround(_map.TerrainAt(c.Position)) && _p.Terrain.MovingOpenDamageMult != 1.0;

        /// <summary>
        /// 直前の刻みの移動の速さの倍率を要素に分ける（表示用。<see cref="FollowPath"/> と同じ要素）。動いていなければ要素なしで 1.0。
        /// </summary>
        public double MoveFactors(Company c, List<ModifierFactor> into)
        {
            if (!c.MovedThisTick || c.State == CompanyState.Routed) return 1.0;
            double f = 1.0;
            void Mul(ModifierKind k, double m, char ch = '\0')
            {
                f *= m;
                if (m != 1.0) into.Add(new ModifierFactor(k, m, ch));
            }
            char t = _map.TerrainAt(c.Position);
            Mul(ModifierKind.MoveTerrain, _map.MoveMult(t), t);
            if (c.Pinned) Mul(ModifierKind.MovePinned, _p.Pinning.MoveMult);
            if (c.PassOverlap) Mul(ModifierKind.MovePassThrough, _p.Movement.PassThroughSpeedMult);
            switch (c.MoveStyle)
            {
                case MoveStyle.Sideways: Mul(ModifierKind.MoveSideways, _p.Movement.SidewaysSpeedMult); break;
                case MoveStyle.Backward: Mul(ModifierKind.MoveBackward, _p.Movement.RetreatSpeedMult); break;
                case MoveStyle.Turning: Mul(ModifierKind.MoveTurning, _p.Movement.TurningSpeedMult); break;
            }
            return f;
        }
    }
}
