using System.Collections.Generic;
using UnityEngine;

namespace Battalion.View
{
    /// <summary>
    /// 地図の上に置く文字（損害の数字、大きな出来事）。世界座標の TextMesh を使い回す。
    /// IMGUI と違ってカメラの画像に写るので、バッチモードの撮影でも確かめられる。大きさは画面の画素で指定する。
    /// </summary>
    public sealed class TextPool
    {
        const int FontSize = 64;

        readonly Transform _parent;
        readonly Font _font;
        readonly List<TextMesh> _items = new List<TextMesh>();
        int _used;

        public TextPool(Transform parent, Font font)
        {
            _parent = parent;
            _font = font;
        }

        public void Begin() => _used = 0;

        /// <summary>文字を置く。<paramref name="heightPx"/> は画面での文字の高さ、<paramref name="pxToWorld"/> は画素あたりの世界の長さ。</summary>
        public void Draw(string text, Vector2 world, float heightPx, float pxToWorld, Color color, TextAnchor anchor = TextAnchor.LowerCenter)
        {
            TextMesh tm;
            if (_used < _items.Count)
            {
                tm = _items[_used];
            }
            else
            {
                var go = new GameObject("Text");
                go.transform.SetParent(_parent, false);
                tm = go.AddComponent<TextMesh>();
                tm.font = _font;
                tm.fontSize = FontSize;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = _font.material;
                r.sortingOrder = 30;
                _items.Add(tm);
            }
            _used++;
            tm.gameObject.SetActive(true);
            tm.text = text;
            tm.color = color;
            tm.anchor = anchor;
            // TextMesh の1行の高さは、おおよそ characterSize × fontSize ÷ 10（世界の長さ）
            tm.characterSize = heightPx * pxToWorld * 10f / FontSize;
            tm.transform.position = new Vector3(world.x, world.y, -1);
        }

        public void End()
        {
            for (int k = _used; k < _items.Count; k++) _items[k].gameObject.SetActive(false);
        }
    }
}
